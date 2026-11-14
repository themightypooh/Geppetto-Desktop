// Cluster/frustum streaming: stream, don't load (godlike Platform P1).
//
// Nanite streams only visible clusters; Geppetto today loads the whole mesh
// and draws all of it. This file is the CPU-side plan for the same shape:
//   - a mesh is partitioned into spatial clusters (MeshClusters.Build),
//   - each frame a frustum test decides which clusters are visible,
//   - a schedule decides which visible clusters to load and which resident
//     ones to evict under a triangle budget (StreamSchedule).
//
// What this file is NOT: it does not move geometry to the GPU — the upload
// stays in ViewportMesh.Upload, and the per-cluster GPU buffers are the
// host's job (the schedule's Load/Evict lists are the contract it reads).
// It also never evicts a VISIBLE cluster to meet a budget: when the visible
// set alone is over budget, the schedule reports the overage and the proxy
// system (ViewportProxyLod) steps those objects down a level instead. The
// two systems split the job that way on purpose — the schedule decides WHAT
// is resident, the proxy decides at WHAT level.
//
// Kernel-side, engine-free, no GL: a frustum is six planes, a cluster is a
// bounding sphere plus a face range. The host builds the frustum from the
// camera (see ViewFrustum.FromMatrix — the 16 floats of View * Projection,
// row-major as System.Numerics stores them) and applies the schedule.
#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>A view frustum as six planes. A point is inside when
/// Dot(Normal, p) + Distance &gt;= 0 for every plane. Normals point INWARD.
/// Built from a view-projection matrix (<see cref="FromMatrix"/>) or directly
/// from six planes (<see cref="FromPlanes"/>).</summary>
public sealed class ViewFrustum
{
	/// <summary>Six (Normal, Distance) pairs: Left, Right, Bottom, Top, Near, Far.</summary>
	public (Vec3 Normal, float Distance)[] Planes = new (Vec3, float)[6];

	/// <summary>Build from six planes in Left/Right/Bottom/Top/Near/Far order.
/// Each plane is (inward normal, distance); normals are normalised here.</summary>
	public static ViewFrustum FromPlanes( IReadOnlyList<(Vec3 Normal, float Distance)> planes )
	{
		ArgumentNullException.ThrowIfNull( planes );
		if ( planes.Count != 6 ) throw new ArgumentException( "A frustum needs exactly six planes.", nameof( planes ) );
		var frustum = new ViewFrustum();
		for ( var i = 0; i < 6; i++ )
		{
			var (n, d) = planes[i];
			var len = n.Length;
			if ( len <= 1e-9f || !float.IsFinite( len ) ) throw new ArgumentException( $"Plane {i} has a zero or non-finite normal.", nameof( planes ) );
			frustum.Planes[i] = (n / len, d / len);
		}
		return frustum;
	}

	/// <summary>Build from the 16 floats of a combined view-projection matrix,
	/// row-major (M11..M44 as System.Numerics stores them), for a row-vector
	/// transform v_clip = v * M and an OpenGL -1..1 clip volume (which is what
	/// OrbitCamera's ProjectionMatrix produces). Planes are combinations of the
	/// matrix COLUMNS: left = col4+col1, right = col4-col1, bottom = col4+col2,
	/// top = col4-col2, near = col4+col3, far = col4-col3. The unit test is the
	/// authority on the convention: a real OrbitCamera's target must be inside
	/// and a point behind the eye must be outside.</summary>
	public static ViewFrustum FromMatrix( IReadOnlyList<float> m )
	{
		ArgumentNullException.ThrowIfNull( m );
		if ( m.Count != 16 ) throw new ArgumentException( "A view-projection matrix has 16 floats.", nameof( m ) );
		// Columns of the row-major matrix: col1 = (M11,M21,M31,M41), etc.
		var c2 = new Vec3( m[1], m[5], m[9] ); var c2w = m[13];
		var c3 = new Vec3( m[2], m[6], m[10] ); var c3w = m[14];
		var c4 = new Vec3( m[3], m[7], m[11] ); var c4w = m[15];
		var c1v = new Vec3( m[0], m[4], m[8] ); var c1w = m[12];
		// (written out plainly: each plane is a column sum/difference)
		var planes = new (Vec3, float)[6];
		planes[0] = (c4 + c1v, c4w + c1w); // left:   col4 + col1
		planes[1] = (c4 - c1v, c4w - c1w); // right:  col4 - col1
		planes[2] = (c4 + c2, c4w + c2w);  // bottom: col4 + col2
		planes[3] = (c4 - c2, c4w - c2w);  // top:    col4 - col2
		planes[4] = (c4 + c3, c4w + c3w);  // near:   col4 + col3
		planes[5] = (c4 - c3, c4w - c3w);  // far:    col4 - col3
		var frustum = new ViewFrustum();
		for ( var i = 0; i < 6; i++ )
		{
			var (n, d) = planes[i];
			var len = n.Length;
			if ( len <= 1e-9f || !float.IsFinite( len ) ) throw new ArgumentException( $"Plane {i} of the view-projection matrix is degenerate.", nameof( m ) );
			frustum.Planes[i] = (n / len, d / len);
		}
		return frustum;
	}

	/// <summary>True when the sphere is at least partly inside (touching counts).</summary>
	public bool IntersectsSphere( Vec3 centre, float radius )
	{
		if ( radius < 0f ) throw new ArgumentOutOfRangeException( nameof( radius ), "A sphere radius cannot be negative." );
		for ( var i = 0; i < 6; i++ )
		{
			var (n, d) = Planes[i];
			if ( Vec3.Dot( n, centre ) + d < -radius ) return false;
		}
		return true;
	}

	/// <summary>True when the axis-aligned box is at least partly inside. Uses the
	/// positive vertex per plane (the box corner farthest along the plane normal).</summary>
	public bool IntersectsBox( Vec3 min, Vec3 max )
	{
		for ( var i = 0; i < 6; i++ )
		{
			var (n, d) = Planes[i];
			var px = n.x >= 0f ? max.x : min.x;
			var py = n.y >= 0f ? max.y : min.y;
			var pz = n.z >= 0f ? max.z : min.z;
			if ( n.x * px + n.y * py + n.z * pz + d < 0f ) return false;
		}
		return true;
	}
}

/// <summary>One spatial cluster of a mesh: a contiguous run of faces in
/// Morton order, with a bounding sphere and a triangle count. A cluster is
/// the unit the streamer loads, draws and evicts — never a single face, never
/// the whole mesh.</summary>
public sealed class MeshCluster
{
	/// <summary>Index in the cluster list, 0-based.</summary>
	public int Index;
	/// <summary>Bounding-sphere centre (average of the cluster's face centroids).</summary>
	public Vec3 Centre;
	/// <summary>Bounding-sphere radius (farthest face vertex from the centre).</summary>
	public float Radius;
	/// <summary>Triangles in the cluster (a face with k corners counts as k-2).</summary>
	public int TriangleCount;
	/// <summary>Face indices into the source mesh, in Morton order.</summary>
	public int[] FaceIndices = Array.Empty<int>();
	/// <summary>Which LOD level this cluster belongs to (0 = full mesh). The
	/// proxy system steps a cluster's level down instead of evicting it.</summary>
	public int Level;
}

/// <summary>Partition a mesh into spatial clusters for streaming. Faces are
/// ordered by a 3D Morton code of their centroid (so nearby faces land in the
/// same cluster), then chunked into runs of about
/// <paramref name="targetTrianglesPerCluster"/> triangles.</summary>
/// <remarks>Chunks are consecutive runs in Morton order. On a mesh whose parts
/// are far apart, one chunk can straddle the gap and hold faces from both
/// parts; its sphere then covers both and the cluster loads them together.
/// That is conservative and correct (never a false cull), just coarser
/// streaming. In the real system clusters are built per object (one set per
/// ViewportMesh/batch), so a straddle only happens inside a single
/// disconnected mesh.</remarks>
public static class MeshClusters
{
	/// <summary>Default cluster size: 512 triangles — small enough that culling
	/// one cluster saves real work, large enough that the per-cluster overhead
	/// (one draw call's worth of bookkeeping) stays negligible.</summary>
	public const int DefaultTargetTriangles = 512;

	public static List<MeshCluster> Build( PolyMesh mesh, int targetTrianglesPerCluster = DefaultTargetTriangles )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		if ( targetTrianglesPerCluster < 1 ) throw new ArgumentOutOfRangeException( nameof( targetTrianglesPerCluster ), "A cluster needs at least one triangle." );
		if ( mesh.FaceCount == 0 ) throw new ArgumentException( "Cannot cluster an empty mesh.", nameof( mesh ) );

		// Bounds for the Morton normalisation (plain floats: Vec3 fields are readonly).
		var minX = float.MaxValue; var minY = float.MaxValue; var minZ = float.MaxValue;
		var maxX = float.MinValue; var maxY = float.MinValue; var maxZ = float.MinValue;
		var centroids = new Vec3[mesh.FaceCount];
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var c = mesh.FaceCentroid( mesh.Faces[fi] );
			centroids[fi] = c;
			if ( c.x < minX ) minX = c.x; if ( c.y < minY ) minY = c.y; if ( c.z < minZ ) minZ = c.z;
			if ( c.x > maxX ) maxX = c.x; if ( c.y > maxY ) maxY = c.y; if ( c.z > maxZ ) maxZ = c.z;
		}
		var min = new Vec3( minX, minY, minZ );
		// Uniform scale (the largest axis extent), NOT per-axis extents: a
		// Morton bit must mean the same physical length on every axis. With
		// per-axis normalisation a thin axis (a 0.6-deep relief on a 6-wide
		// mesh) gets 21 bits of precision over 0.6 units, so tiny height
		// differences outrank real x/y distance and one chunk ends up holding
		// faces from opposite corners (radius 5+ on a 6-wide grid, measured
		// 2026-11-13). Uniform scale leaves the thin axis's top bits at zero,
		// which is correct: it does not subdivide space at coarse levels.
		var uniform = Math.Max( Math.Max( maxX - minX, maxY - minY ), maxZ - minZ );
		var size = new Vec3( Math.Max( uniform, 1e-6f ), Math.Max( uniform, 1e-6f ), Math.Max( uniform, 1e-6f ) );

		var order = new int[mesh.FaceCount];
		for ( var i = 0; i < order.Length; i++ ) order[i] = i;
		Array.Sort( order, ( a, b ) => Morton( centroids[a], min, size ).CompareTo( Morton( centroids[b], min, size ) ) );

		var clusters = new List<MeshCluster>();
		var run = new List<int>();
		var runTriangles = 0;
		foreach ( var fi in order )
		{
			var tris = Math.Max( 1, mesh.Faces[fi].Count - 2 );
			if ( run.Count > 0 && runTriangles + tris > targetTrianglesPerCluster )
			{
				clusters.Add( FinishCluster( clusters.Count, mesh, run ) );
				run = new List<int>();
				runTriangles = 0;
			}
			run.Add( fi );
			runTriangles += tris;
		}
		if ( run.Count > 0 ) clusters.Add( FinishCluster( clusters.Count, mesh, run ) );
		return clusters;
	}

	static MeshCluster FinishCluster( int index, PolyMesh mesh, List<int> faces )
	{
		var centre = Vec3.Zero;
		var tris = 0;
		foreach ( var fi in faces )
		{
			centre += mesh.FaceCentroid( mesh.Faces[fi] );
			tris += Math.Max( 1, mesh.Faces[fi].Count - 2 );
		}
		centre /= faces.Count;
		var radius = 0f;
		foreach ( var fi in faces )
			foreach ( var vi in mesh.Faces[fi].Indices )
			{
				var d = (mesh.Positions[vi] - centre).Length;
				if ( d > radius ) radius = d;
			}
		return new MeshCluster
		{
			Index = index,
			Centre = centre,
			Radius = radius,
			TriangleCount = tris,
			FaceIndices = faces.ToArray(),
			Level = 0,
		};
	}

	/// <summary>3D Morton code (21 bits per axis, interleaved) of a point
	/// normalised into the unit cube. Nearby points share long prefixes, so
	/// sorting by code groups nearby faces together.</summary>
	static ulong Morton( Vec3 p, Vec3 min, Vec3 size )
	{
		uint x = (uint)Math.Clamp( (p.x - min.x) / size.x * 2097151f, 0f, 2097151f );
		uint y = (uint)Math.Clamp( (p.y - min.y) / size.y * 2097151f, 0f, 2097151f );
		uint z = (uint)Math.Clamp( (p.z - min.z) / size.z * 2097151f, 0f, 2097151f );
		return Interleave( x ) | (Interleave( y ) << 1) | (Interleave( z ) << 2);
	}

	static ulong Interleave( uint v )
	{
		ulong x = v & 0x1FFFFFUL;
		x = (x | (x << 32)) & 0x1F00000000FFFFUL;
		x = (x | (x << 16)) & 0x1F0000FF0000FFUL;
		x = (x | (x << 8)) & 0x100F00F00F00F00FUL;
		x = (x | (x << 4)) & 0x10C30C30C30C30C3UL;
		x = (x | (x << 2)) & 0x1249249249249249UL;
		return x;
	}
}

/// <summary>One frame's streaming decision: which clusters are visible, which
/// of those still need loading, and which resident clusters can go — plus
/// whether the visible set alone is over budget (in which case the proxy
/// system steps levels down; this schedule never evicts a visible cluster).</summary>
public sealed class StreamSchedule
{
	/// <summary>Cluster indices inside the frustum this frame.</summary>
	public List<int> Visible = new();
	/// <summary>Visible clusters that are not resident yet: load these.</summary>
	public List<int> Load = new();
	/// <summary>Resident clusters that are not visible: evict these, least-recently-used first.</summary>
	public List<int> Evict = new();
	/// <summary>Triangles resident after this schedule (visible kept + newly loaded - evicted).</summary>
	public int ResidentTriangles;
	/// <summary>Triangles of the visible set. When this is over the budget, the
	/// schedule cannot fix it by evicting — the proxy system must step levels down.</summary>
	public int VisibleTriangles;
	/// <summary>True when the visible set alone exceeds the budget. The caller
	/// hands the overage to the proxy system; nothing is dropped here.</summary>
	public bool OverBudget;
}

/// <summary>The per-frame streaming decision. Pure function of (frustum,
/// clusters, resident set, last-used frames, budget): no IO, no GL, testable
/// on the CPU. Loading itself (uploading a cluster's buffers) stays with the
/// host, which reads <see cref="StreamSchedule.Load"/> /
/// <see cref="StreamSchedule.Evict"/>.</summary>
public static class ViewportStreaming
{
	/// <param name="clusters">All clusters of the mesh, from <see cref="MeshClusters.Build"/>.</param>
	/// <param name="resident">Cluster indices currently resident (uploaded).</param>
	/// <param name="lastUsedFrame">Frame each resident cluster was last visible, for LRU eviction. Missing entries count as never used.</param>
	/// <param name="frame">Current frame number.</param>
	/// <param name="triangleBudget">Triangles the resident set may hold. Visible clusters are never evicted to meet it.</param>
	public static StreamSchedule Schedule( ViewFrustum frustum, IReadOnlyList<MeshCluster> clusters,
		IReadOnlySet<int> resident, IReadOnlyDictionary<int, int> lastUsedFrame, int frame, int triangleBudget )
	{
		ArgumentNullException.ThrowIfNull( frustum );
		ArgumentNullException.ThrowIfNull( clusters );
		ArgumentNullException.ThrowIfNull( resident );
		if ( triangleBudget < 0 ) throw new ArgumentOutOfRangeException( nameof( triangleBudget ), "A triangle budget cannot be negative." );

		var schedule = new StreamSchedule();
		var visibleSet = new HashSet<int>();
		for ( var i = 0; i < clusters.Count; i++ )
		{
			var c = clusters[i];
			if ( frustum.IntersectsSphere( c.Centre, c.Radius ) )
			{
				visibleSet.Add( i );
				schedule.Visible.Add( i );
				schedule.VisibleTriangles += c.TriangleCount;
				if ( !resident.Contains( i ) ) schedule.Load.Add( i );
			}
		}

		// Evict resident-but-invisible clusters, least-recently-used first, until
		// the resident total (after loading) fits the budget. Visible clusters
		// are never in this list.
		var residentTriangles = 0;
		foreach ( var i in resident )
			if ( i >= 0 && i < clusters.Count ) residentTriangles += clusters[i].TriangleCount;
		var afterLoad = residentTriangles;
		foreach ( var i in schedule.Load ) afterLoad += clusters[i].TriangleCount;

		var evictCandidates = new List<int>();
		foreach ( var i in resident )
			if ( !visibleSet.Contains( i ) && i >= 0 && i < clusters.Count )
				evictCandidates.Add( i );
		evictCandidates.Sort( ( a, b ) =>
		{
			var la = lastUsedFrame is not null && lastUsedFrame.TryGetValue( a, out var fa ) ? fa : int.MinValue;
			var lb = lastUsedFrame is not null && lastUsedFrame.TryGetValue( b, out var fb ) ? fb : int.MinValue;
			return la.CompareTo( lb );
		} );

		schedule.ResidentTriangles = afterLoad;
		foreach ( var i in evictCandidates )
		{
			if ( afterLoad <= triangleBudget ) break;
			schedule.Evict.Add( i );
			afterLoad -= clusters[i].TriangleCount;
			schedule.ResidentTriangles = afterLoad;
		}

		schedule.OverBudget = schedule.VisibleTriangles > triangleBudget;
		return schedule;
	}
}
