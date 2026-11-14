using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Mirror plane for manual retopo. Left-right is Y = 0 in this kernel (inches, Z-up),
/// the same convention TopologyReport's symmetry measure uses.</summary>
public enum RetopoMirrorAxis
{
	None,
	X,
	Y,
	Z
}

/// <summary>Result of <see cref="ManualRetopo.Relax"/>: how many interior vertices moved, how
/// many of them re-snapped onto the source surface, and how many stayed off-surface because no
/// source triangle was within the snap radius.</summary>
public sealed class RelaxResult
{
	public int Moved, Snapped, Unsnapped;
}

/// <summary>Hero-cage validation: the Mesh Doctor report for the cage plus the quad census.
/// AllQuad means every face is a quad; a pole triangle is legitimate retopo but it is not an
/// all-quad cage, so it is reported, not hidden.</summary>
public sealed class CageReport
{
	public MeshDoctorReport Report;
	public int QuadCount, TriangleCount, NgonCount, BoundaryLoops, OpenLoops;
	public bool AllQuad => QuadCount > 0 && TriangleCount == 0 && NgonCount == 0;
	public bool IsManifold => Report != null && Report.IsManifold;
	public bool IsClosed => Report != null && Report.IsClosed;

	public string Describe() =>
		$"cage: {QuadCount} quads, {TriangleCount} tris, {NgonCount} n-gons, " +
		$"all-quad {AllQuad}, manifold {IsManifold}, closed {IsClosed}, " +
		$"boundary loops {BoundaryLoops} ({OpenLoops} open). {Report?.Describe()}";
}

/// <summary>
/// Manual retopo mode: Quad Draw-style quad drawing snapped onto a live source surface.
///
/// The source mesh is read-only; the cage being drawn is <see cref="Target"/>. Every point
/// the user places is projected onto the source with the BVH closest-point query
/// (<see cref="MeshBVH.NearestSurface"/>), so a drawn vertex is on the surface by
/// construction, never approximately near it. Symmetry adds the mirrored twin vertex and
/// <see cref="AddQuadMirrored"/> adds the mirrored quad with reversed winding (a mirror
/// flips orientation, so the twin quad must be listed backwards to keep its normal outward).
/// <see cref="WeldClose"/> auto-welds coincident cage vertices through
/// <see cref="MeshRepair.WeldStaged"/>; <see cref="Relax"/> Laplacian-smooths interior cage
/// vertices and re-snaps them. <see cref="ValidateCage"/> is the hero-cage gate: all-quad,
/// manifold, zero holes.
///
/// This is the MANUAL mode only. Automatic retopology is the OpenVDB / Instant Meshes
/// ticket's job (<c>integrate-openvdb-remesh-and-instant-meshes-retopo-backends</c>) and is
/// deliberately not re-implemented here.
/// </summary>
public sealed class ManualRetopo
{
	public PolyMesh Source { get; }
	public PolyMesh Target { get; } = new PolyMesh();
	public MeshBVH Bvh { get; }
	public float SnapRadius { get; }

	public RetopoMirrorAxis Symmetry { get; set; } = RetopoMirrorAxis.None;
	public float SymmetryOffset { get; set; } = 0f;

	readonly Dictionary<int, int> _twins = new Dictionary<int, int>();

	/// <param name="snapRadius">Maximum snap distance. Defaults to the source's bounds
	/// diagonal, so a point placed a reasonable distance off the surface still snaps, while
	/// a point placed nowhere near the model is refused instead of silently landing far away.</param>
	public ManualRetopo( PolyMesh source, float? snapRadius = null )
	{
		Source = source ?? throw new ArgumentNullException( nameof( source ) );
		if ( source.FaceCount == 0 ) throw new ArgumentException( "Retopo needs a source mesh with faces.", nameof( source ) );
		Bvh = MeshBVH.Build( source );
		SnapRadius = snapRadius ?? Math.Max( source.BoundsDiagonal, 1e-3f );
		if ( !(SnapRadius > 0f) || !float.IsFinite( SnapRadius ) )
			throw new ArgumentOutOfRangeException( nameof( snapRadius ), "Snap radius must be a positive finite distance." );
	}

	float AxisCoord( Vec3 p ) => Symmetry switch
	{
		RetopoMirrorAxis.X => p.x,
		RetopoMirrorAxis.Y => p.y,
		RetopoMirrorAxis.Z => p.z,
		_ => 0f
	};

	Vec3 Mirror( Vec3 p )
	{
		var d = 2f * SymmetryOffset - AxisCoord( p );
		return Symmetry switch
		{
			RetopoMirrorAxis.X => new Vec3( d, p.y, p.z ),
			RetopoMirrorAxis.Y => new Vec3( p.x, d, p.z ),
			RetopoMirrorAxis.Z => new Vec3( p.x, p.y, d ),
			_ => p
		};
	}

	/// <summary>Snap an approximate point onto the source surface and add it to the cage.
	/// With symmetry on, the mirrored twin is added too (unless the point is on the plane,
	/// within 1e-4, in which case it is its own mirror). Throws when no source surface is
	/// within the snap radius — a missed snap is refused, never silently placed.</summary>
	public int AddPoint( Vec3 approx )
	{
		var hit = Bvh.NearestSurface( Source, approx, SnapRadius );
		if ( hit is null )
			throw new InvalidOperationException( $"No source surface within {SnapRadius:0.###} of {approx}; move closer or raise the snap radius." );
		var v = Target.AddVertex( hit.Value.Point );
		if ( Symmetry != RetopoMirrorAxis.None && MathF.Abs( AxisCoord( hit.Value.Point ) - SymmetryOffset ) > 1e-4f )
		{
			var twin = Target.AddVertex( Mirror( hit.Value.Point ) );
			_twins[v] = twin;
			_twins[twin] = v;
		}
		return v;
	}

	/// <summary>The mirrored twin of a cage vertex, or -1 when it has none (symmetry off, or
	/// the vertex sits on the mirror plane).</summary>
	public int TwinOf( int vertex ) => _twins.TryGetValue( vertex, out var twin ) ? twin : -1;

	void ValidateIndices( params int[] indices )
	{
		foreach ( var i in indices )
			if ( i < 0 || i >= Target.VertexCount )
				throw new ArgumentOutOfRangeException( nameof( indices ), $"Cage vertex {i} does not exist (cage has {Target.VertexCount} vertices)." );
	}

	public Face AddQuad( int a, int b, int c, int d )
	{
		ValidateIndices( a, b, c, d );
		return Target.AddFace( new[] { a, b, c, d } );
	}

	public Face AddTriangle( int a, int b, int c )
	{
		ValidateIndices( a, b, c );
		return Target.AddFace( new[] { a, b, c } );
	}

	/// <summary>Add a quad and its mirrored twin. The twin lists its vertices backwards:
	/// mirroring flips winding, so the reversed order keeps the twin's normal outward.
	/// A vertex on the mirror plane mirrors to itself.</summary>
	public Face AddQuadMirrored( int a, int b, int c, int d )
	{
		AddQuad( a, b, c, d );
		int MirrorIndex( int v ) => TwinOf( v ) >= 0 ? TwinOf( v ) : v;
		return Target.AddFace( new[] { MirrorIndex( d ), MirrorIndex( c ), MirrorIndex( b ), MirrorIndex( a ) } );
	}

	/// <summary>Auto-weld: merge coincident cage vertices (staged, scale-relative, through
	/// MeshRepair.WeldStaged). Twin bookkeeping is cleared — a weld can merge a twin pair
	/// into one vertex, so twin indices from before the weld are no longer meaningful.
	/// Draw first, weld last.</summary>
	public List<string> WeldClose( float epsilon )
	{
		var lines = MeshRepair.WeldStaged( Target, epsilon );
		_twins.Clear();
		return lines;
	}

	/// <summary>Relax interior cage vertices toward the average of their neighbours (one
	/// Jacobi pass per iteration, half-step), then re-snap each moved vertex onto the source
	/// surface. Boundary vertices never move: relaxing a rim would shrink the patch.</summary>
	public RelaxResult Relax( int iterations = 1 )
	{
		if ( iterations < 1 ) throw new ArgumentOutOfRangeException( nameof( iterations ), "Relax needs at least one iteration." );
		var result = new RelaxResult();
		for ( var it = 0; it < iterations; it++ )
		{
			var neighbours = new Dictionary<int, HashSet<int>>();
			foreach ( var f in Target.Faces )
			{
				for ( var i = 0; i < f.Count; i++ )
				{
					var a = f.Indices[i];
					var b = f.Indices[(i + 1) % f.Count];
					if ( !neighbours.TryGetValue( a, out var na ) ) neighbours[a] = na = new HashSet<int>();
					if ( !neighbours.TryGetValue( b, out var nb ) ) neighbours[b] = nb = new HashSet<int>();
					na.Add( b );
					nb.Add( a );
				}
			}

			var boundary = new HashSet<int>();
			foreach ( var kv in Target.BuildEdgeFaces() )
			{
				if ( kv.Value.Count != 1 ) continue;
				boundary.Add( kv.Key.A );
				boundary.Add( kv.Key.B );
			}

			var next = new Vec3[Target.VertexCount];
			for ( var v = 0; v < Target.VertexCount; v++ )
			{
				if ( boundary.Contains( v ) || !neighbours.TryGetValue( v, out var ns ) || ns.Count == 0 )
				{
					next[v] = Target.Positions[v];
					continue;
				}
				var avg = Vec3.Zero;
				foreach ( var n in ns ) avg += Target.Positions[n];
				avg /= ns.Count;
				next[v] = Vec3.Lerp( Target.Positions[v], avg, 0.5f );
			}

			for ( var v = 0; v < Target.VertexCount; v++ )
			{
				if ( boundary.Contains( v ) ) continue;
				if ( (next[v] - Target.Positions[v]).LengthSquared <= 1e-12f ) continue;
				result.Moved++;
				Target.Positions[v] = next[v];
				var hit = Bvh.NearestSurface( Source, next[v], SnapRadius );
				if ( hit is null ) result.Unsnapped++;
				else
				{
					Target.Positions[v] = hit.Value.Point;
					result.Snapped++;
				}
			}
			Target.InvalidateVertexCache();
		}
		return result;
	}

	/// <summary>Hero-cage gate: the Mesh Doctor report for the cage plus the quad census and
	/// the boundary-loop count. A cage with an eye or mouth opening has boundary loops by
	/// design; what fails the gate is an OPEN loop (a chain that never closes) or any
	/// non-manifold / degenerate / flipped defect.</summary>
	public CageReport ValidateCage()
	{
		var report = MeshDoctor.Check( Target, "retopo cage" );
		var loops = MeshDoctor.FindBoundaryLoops( Target );
		var cage = new CageReport { Report = report, BoundaryLoops = loops.Count };
		foreach ( var f in Target.Faces )
		{
			if ( f.Count == 4 ) cage.QuadCount++;
			else if ( f.Count == 3 ) cage.TriangleCount++;
			else if ( f.Count > 4 ) cage.NgonCount++;
		}
		foreach ( var loop in loops )
			if ( !loop.Closed ) cage.OpenLoops++;
		return cage;
	}

	/// <summary>Largest distance from any cage vertex to the source surface. PositiveInfinity
	/// when a vertex has no source surface within the search radius. Zero when the cage is empty.</summary>
	public float MaxSnapDeviation()
	{
		if ( Target.VertexCount == 0 ) return 0f;
		var radius = Math.Max( SnapRadius, Source.BoundsDiagonal );
		var worst = 0f;
		foreach ( var p in Target.Positions )
		{
			var hit = Bvh.NearestSurface( Source, p, radius );
			if ( hit is null ) return float.PositiveInfinity;
			if ( hit.Value.Distance > worst ) worst = hit.Value.Distance;
		}
		return worst;
	}
}
