using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The dedicated shadow mesh for Source 2 shadow casting (S&amp;box Lane 8, shadow half).
///
/// A shadow mesh is NOT a render LOD and NOT a LodChain level — it is a separate,
/// deliberately dumb mesh whose only job is to block light. It is built by one
/// <see cref="Decimate"/> pass at 10% of the source triangle count, then stripped
/// of everything a shadow pass cannot use and checked until it is provably closed:
///
/// - positions and triangles only. No skin weights (<see cref="PolyMesh.Skin"/> is
///   null), no morphs, no UV2, no per-corner UVs worth the name (every corner UV
///   is zero), one material slot. A shadow caster has no texture and no bones, so
///   carrying them would only cost memory and invite a caller to render it.
/// - closed, or it does not come back at all. Every undirected edge must be shared
///   by exactly two triangles. A hole in a shadow mesh is a leak of light through
///   the model, so <see cref="Build"/> repairs boundary loops with a fan fill and
///   throws <see cref="InvalidOperationException"/> if the result still is not
///   closed, instead of quietly returning a leaky mesh. An open SOURCE mesh throws
///   <see cref="ArgumentException"/> up front for the same reason: a shadow mesh
///   built from a shell with a real opening is a guess, not a silhouette.
/// - the source mesh is never mutated. The build works on a welded, triangulated
///   copy, so the caller's render mesh comes back exactly as it went in.
///
/// WHAT "10%" MEANS. <see cref="Decimate"/> is triangle-based, like
/// <see cref="LodExport"/>'s ratios, so the 10% is a triangle target: a 1200-triangle
/// source aims at 120 triangles. The vertex count is reported alongside it and
/// lands near 10% on dense fixtures because a decimated surface loses vertices
/// roughly with its triangles; it is a measured result, not a second target, and
/// <see cref="Result.VertexRatio"/> says what it actually was. If
/// <see cref="Decimate"/> runs out of legal collapses first,
/// <see cref="Result.ReachedTarget"/> is false and the mesh is still closed —
/// bigger than asked, never broken.
///
/// WHAT THIS DOES NOT DO. It does not preserve UV seams or skin weights (there
/// is nothing to preserve them FOR), it does not smooth or re-project the
/// surface (the fan fill is flat, and a shadow does not care), and it does not
/// emit any .vmdl node — wiring the mesh into a model as its shadow caster is
/// the export lane's job, not this file's. It also does not own
/// <c>LodChain</c>: that file does not exist in this tree and this file does
/// not create it.
/// </summary>
public static class ShadowMesh
{
	/// <summary>The spec's shadow ratio: one tenth of the source triangles.</summary>
	public const float DefaultRatio = 0.10f;

	/// <summary>Below this many source triangles a shadow mesh saves next to nothing
	/// (same floor as <see cref="LodExport.MinimumTriangles"/>); such a mesh is
	/// already cheaper than its own shadow copy would be worth.</summary>
	public const int MinimumTriangles = 64;

	/// <summary>What to aim for. Only <see cref="Ratio"/> is a target; everything
	/// else about the output (closed, unskinned, untextured) is unconditional.</summary>
	public sealed class Options
	{
		/// <summary>Fraction of the source triangle count to keep. 0.10 by default.
		/// Must be in (0, 1]; anything else throws rather than silently clamping.</summary>
		public float Ratio = DefaultRatio;
	}

	/// <summary>What a build did, so a caller can report it rather than diff two meshes.</summary>
	public readonly struct Result
	{
		public readonly PolyMesh Mesh;
		public readonly int SourceVertices;
		public readonly int SourceTriangles;
		public readonly int Vertices;
		public readonly int Triangles;
		public readonly float VertexRatio;
		public readonly float TriangleRatio;
		public readonly bool ReachedTarget;
		public readonly bool IsClosed;
		public readonly int BoundaryEdges;
		public readonly int HolesFilled;

		/// <summary>
		/// Worst (smallest) projected-area ratio over the X, Y and Z axes: the shadow
		/// mesh's silhouette area divided by the source's, per axis. 1.0 means the
		/// silhouette is unchanged from that direction; a decimated cube keeps 1.0
		/// on every axis because its faces stay flat.
		/// </summary>
		public readonly float SilhouetteRatio;

		public Result( PolyMesh mesh, int sourceVertices, int sourceTriangles, int vertices,
			int triangles, bool reachedTarget, bool isClosed, int boundaryEdges, int holesFilled,
			float silhouetteRatio )
		{
			Mesh = mesh;
			SourceVertices = sourceVertices;
			SourceTriangles = sourceTriangles;
			Vertices = vertices;
			Triangles = triangles;
			VertexRatio = sourceVertices > 0 ? (float)vertices / sourceVertices : 0f;
			TriangleRatio = sourceTriangles > 0 ? (float)triangles / sourceTriangles : 0f;
			ReachedTarget = reachedTarget;
			IsClosed = isClosed;
			BoundaryEdges = boundaryEdges;
			HolesFilled = holesFilled;
			SilhouetteRatio = silhouetteRatio;
		}
	}

	/// <summary>Build the shadow mesh for <paramref name="source"/> at the default 10%.</summary>
	public static Result Build( PolyMesh source ) => Build( source, null );

	/// <summary>
	/// Build the shadow mesh for <paramref name="source"/>.
	/// Throws <see cref="ArgumentNullException"/> for a null source,
	/// <see cref="ArgumentException"/> for an empty, tiny (&lt; 64 triangles), open,
	/// or badly-optioned input, and <see cref="InvalidOperationException"/> if the
	/// decimated mesh cannot be closed. Never returns a leaky mesh.
	/// </summary>
	public static Result Build( PolyMesh source, Options options )
	{
		if ( source is null )
			throw new ArgumentNullException( nameof( source ) );
		options ??= new Options();
		if ( options.Ratio <= 0f || options.Ratio > 1f )
			throw new ArgumentException( $"Ratio must be in (0, 1], got {options.Ratio}", nameof( options ) );

		// Work on a welded, triangulated copy: the source is never mutated, and
		// edge counting below is over real shared vertices, not per-triangle copies.
		var welded = WeldAndTriangulate( source );
		var sourceTriangles = welded.Faces.Count;
		var sourceVertices = welded.Positions.Count;
		if ( sourceTriangles == 0 )
			throw new ArgumentException( "Source mesh has no triangles", nameof( source ) );
		if ( sourceTriangles < MinimumTriangles )
			throw new ArgumentException(
				$"Source mesh has {sourceTriangles} triangles, below the {MinimumTriangles} " +
				"minimum: a shadow copy saves nothing at that size", nameof( source ) );

		var (isSourceClosed, sourceBoundary, _) = Validate( welded );
		if ( !isSourceClosed )
			throw new ArgumentException(
				$"Source mesh is open: {sourceBoundary} boundary edge(s). A shadow mesh " +
				"must start from a closed mesh, or its silhouette is a guess", nameof( source ) );

		// One decimation pass. UV seams are deliberately NOT preserved: a shadow
		// caster has no texture, and holding seams would keep vertices the shadow
		// does not need. Boundaries are preserved (there are none on a closed mesh,
		// so this costs nothing and guards the repair step's assumptions).
		var decimated = Decimate.Run( welded, new Decimate.Options
		{
			Ratio = options.Ratio,
			PreserveUVSeams = false,
			PreserveBoundary = true,
		} );

		// Strip everything a shadow pass cannot use, whatever Decimate carried.
		var stripped = Strip( decimated.Mesh );
		var closed = CloseHoles( stripped, out var holesFilled );
		var (isClosed, boundary, nonManifold) = Validate( closed );
		if ( !isClosed )
			throw new InvalidOperationException(
				$"Shadow mesh is not closed after repair: {boundary} boundary edge(s), " +
				$"{nonManifold} non-manifold edge(s). No leaky shadow mesh is returned." );
		if ( nonManifold > 0 )
			throw new InvalidOperationException(
				$"Shadow mesh has {nonManifold} non-manifold edge(s) after repair. " +
				"No broken shadow mesh is returned." );

		var silhouette = SilhouetteRatio( welded, closed );
		return new Result( closed, sourceVertices, sourceTriangles,
			closed.Positions.Count, closed.Faces.Count, decimated.ReachedTarget,
			isClosed, boundary, holesFilled, silhouette );
	}

	/// <summary>
	/// Close every boundary loop of a triangle mesh with a flat fan fill and return
	/// the result. A mesh that is already closed comes back compacted and unchanged
	/// in shape. Throws <see cref="InvalidOperationException"/> when a boundary
	/// loop branches or fails to close, instead of returning a half-patched mesh.
	/// Fan fill is flat by design: a shadow does not care about surface smoothness,
	/// it cares that no light leaks through. This is the repair <see cref="Build"/>
	/// runs on its decimated output; it is public so the repair itself is testable
	/// without depending on decimation opening a hole first.
	/// </summary>
	public static PolyMesh CloseHoles( PolyMesh mesh )
	{
		var closed = CloseHoles( mesh, out _ );
		return closed;
	}

	static PolyMesh CloseHoles( PolyMesh mesh, out int holesFilled )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		holesFilled = 0;
		var work = WeldAndTriangulate( mesh );
		for ( ;; )
		{
			var counts = EdgeCounts( work );
			var boundary = BoundaryDirectedEdges( work, counts );
			if ( boundary.Count == 0 )
				break;
			var loops = ExtractLoops( boundary );
			foreach ( var loop in loops )
			{
				// Fan from the first loop vertex. The loop is in reversed-edge order
				// (see ExtractLoops), so the fan winds the way the missing surface
				// did: a single missing triangle on a cube comes back as exactly
				// that triangle, and the cube's signed volume comes back with it.
				for ( var i = 1; i + 1 < loop.Count; i++ )
					work.Faces.Add( new Face( new[] { loop[0], loop[i], loop[i + 1] } ) );
				holesFilled++;
			}
			// A second pass only runs if the fill itself left a boundary (it should
			// not); loop instead of recursing so a pathological mesh terminates in
			// the Validate check in Build, not in a stack overflow. Guard against an
			// infinite fill loop: if a pass filled nothing, stop and let the caller
			// see the open mesh in the validation result.
			if ( loops.Count == 0 )
				break;
		}
		return Compact( work );
	}

	/// <summary>
	/// Edge census of a mesh: closed when every undirected edge is shared by exactly
	/// two triangles. Boundary edges have one triangle, non-manifold edges more.
	/// Counts are over exact positions after welding, so an unwelded export does not
	/// read as open just because every triangle carries its own vertices.
	/// </summary>
	public static (bool IsClosed, int BoundaryEdges, int NonManifoldEdges) Validate( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		var welded = WeldAndTriangulate( mesh );
		var counts = EdgeCounts( welded );
		var boundary = 0;
		var nonManifold = 0;
		foreach ( var count in counts.Values )
		{
			if ( count == 1 )
				boundary++;
			else if ( count > 2 )
				nonManifold++;
		}
		return (boundary == 0 && nonManifold == 0, boundary, nonManifold);
	}

	/// <summary>
	/// Worst per-axis projected-area ratio of <paramref name="shadow"/> against
	/// <paramref name="source"/>: min over X, Y, Z of (shadow projected area /
	/// source projected area). For a closed mesh the projected area per axis is
	/// half the sum of |triangle area projected on that axis| over all triangles;
	/// the half cancels in the ratio. 0 when the source projects to nothing on an
	/// axis (a flat mesh) is reported as 1 if the shadow matches, else 0.
	/// </summary>
	public static float SilhouetteRatio( PolyMesh source, PolyMesh shadow )
	{
		if ( source is null )
			throw new ArgumentNullException( nameof( source ) );
		if ( shadow is null )
			throw new ArgumentNullException( nameof( shadow ) );
		var s = WeldAndTriangulate( source );
		var h = WeldAndTriangulate( shadow );
		var worst = float.MaxValue;
		for ( var axis = 0; axis < 3; axis++ )
		{
			var sArea = ProjectedArea( s, axis );
			var hArea = ProjectedArea( h, axis );
			var ratio = sArea <= 0f ? (hArea <= 0f ? 1f : 0f) : hArea / sArea;
			if ( ratio < worst )
				worst = ratio;
		}
		return worst == float.MaxValue ? 1f : worst;
	}

	// --- helpers -----------------------------------------------------------------

	/// <summary>Positions + triangles only: drops skin, morphs, UV2 and per-corner UVs.</summary>
	static PolyMesh Strip( PolyMesh mesh )
	{
		var stripped = new PolyMesh();
		stripped.Positions.AddRange( mesh.Positions );
		foreach ( var face in mesh.Faces )
		{
			if ( face.Count != 3 )
				continue;
			stripped.Faces.Add( new Face( new[] { face.Indices[0], face.Indices[1], face.Indices[2] } ) );
		}
		return Compact( stripped );
	}

	/// <summary>
	/// Weld by exact position and fan-triangulate every n-gon. Exact positions, not
	/// a tolerance: two vertices that differ by any amount are different vertices,
	/// and Decimate's own weld (tolerance-based) runs later on this copy anyway.
	/// Degenerate triangles (a repeated index) are dropped, not kept as zero-area
	/// faces that would corrupt every edge count downstream.
	/// </summary>
	static PolyMesh WeldAndTriangulate( PolyMesh mesh )
	{
		var welded = new PolyMesh();
		var index = new Dictionary<Vec3, int>();
		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;
			var mapped = new int[face.Count];
			for ( var i = 0; i < face.Count; i++ )
			{
				var p = mesh.Positions[face.Indices[i]];
				if ( !index.TryGetValue( p, out var w ) )
				{
					w = welded.Positions.Count;
					index[p] = w;
					welded.Positions.Add( p );
				}
				mapped[i] = w;
			}
			for ( var i = 1; i + 1 < mapped.Length; i++ )
			{
				var a = mapped[0];
				var b = mapped[i];
				var c = mapped[i + 1];
				if ( a == b || b == c || a == c )
					continue;
				welded.Faces.Add( new Face( new[] { a, b, c } ) );
			}
		}
		return Compact( welded );
	}

	/// <summary>Drop vertices no triangle references, remapping indices in place.</summary>
	static PolyMesh Compact( PolyMesh mesh )
	{
		var used = new bool[mesh.Positions.Count];
		foreach ( var face in mesh.Faces )
			foreach ( var i in face.Indices )
				if ( i >= 0 && i < used.Length )
					used[i] = true;
		var remap = new int[mesh.Positions.Count];
		var compact = new PolyMesh();
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			if ( !used[i] )
			{
				remap[i] = -1;
				continue;
			}
			remap[i] = compact.Positions.Count;
			compact.Positions.Add( mesh.Positions[i] );
		}
		foreach ( var face in mesh.Faces )
		{
			var indices = new int[face.Count];
			var ok = true;
			for ( var i = 0; i < face.Count; i++ )
			{
				indices[i] = remap[face.Indices[i]];
				if ( indices[i] < 0 )
					ok = false;
			}
			if ( ok )
				compact.Faces.Add( new Face( indices ) );
		}
		return compact;
	}

	static Dictionary<EdgeKey, int> EdgeCounts( PolyMesh mesh )
	{
		var counts = new Dictionary<EdgeKey, int>();
		foreach ( var face in mesh.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				var key = new EdgeKey( face.Indices[i], face.Indices[(i + 1) % face.Count] );
				counts.TryGetValue( key, out var n );
				counts[key] = n + 1;
			}
		}
		return counts;
	}

	/// <summary>
	/// Boundary edges as directed edges REVERSED against their single triangle's
	/// traversal. Chaining reversed edges walks a hole's loop in the order the
	/// missing surface wound in, so a fan over that loop restores the original
	/// winding instead of flipping it inside-out.
	/// </summary>
	static List<(int From, int To)> BoundaryDirectedEdges( PolyMesh mesh, Dictionary<EdgeKey, int> counts )
	{
		var boundary = new List<(int, int)>();
		foreach ( var face in mesh.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[(i + 1) % face.Count];
				if ( counts[new EdgeKey( a, b )] == 1 )
					boundary.Add( (b, a) );
			}
		}
		return boundary;
	}

	/// <summary>Chain directed boundary edges into closed loops. Throws when the
	/// boundary branches (a vertex with more than one outgoing boundary edge) or a
	/// chain fails to return to its start, instead of inventing a fill.</summary>
	static List<List<int>> ExtractLoops( List<(int From, int To)> boundary )
	{
		var next = new Dictionary<int, int>();
		foreach ( var (from, to) in boundary )
		{
			if ( next.ContainsKey( from ) )
				throw new InvalidOperationException(
					$"Boundary branches at vertex {from}: a hole fill cannot be chosen " +
					"unambiguously, so no fill is attempted." );
			next[from] = to;
		}
		var visited = new HashSet<int>();
		var loops = new List<List<int>>();
		foreach ( var (from, _) in boundary )
		{
			if ( visited.Contains( from ) )
				continue;
			var loop = new List<int>();
			var v = from;
			while ( true )
			{
				if ( !visited.Add( v ) )
					throw new InvalidOperationException(
						"Boundary chain revisits a vertex without closing: the hole " +
						"is not a simple closed loop, so no fill is attempted." );
				loop.Add( v );
				if ( !next.TryGetValue( v, out var to ) )
					throw new InvalidOperationException(
						"Boundary chain ends without returning to its start: the hole " +
						"is not a closed loop, so no fill is attempted." );
				v = to;
				if ( v == from )
					break;
				if ( loop.Count > boundary.Count )
					throw new InvalidOperationException(
						"Boundary chain loops without closing: the hole is not a " +
						"closed loop, so no fill is attempted." );
			}
			if ( loop.Count < 3 )
				throw new InvalidOperationException(
					$"Boundary loop has {loop.Count} vertices: too small to fan-fill, " +
					"so no fill is attempted." );
			loops.Add( loop );
		}
		// Every boundary edge must belong to exactly one loop. A shared vertex
		// (two loops through one point) leaves edges unclaimed here; filling only
		// some of them would leave a half-patched mesh, so fail loudly instead.
		var claimed = 0;
		foreach ( var loop in loops )
			claimed += loop.Count;
		if ( claimed != boundary.Count )
			throw new InvalidOperationException(
				$"Boundary has {boundary.Count} edges but only {claimed} form simple " +
				"loops: the hole pattern branches, so no fill is attempted." );
		return loops;
	}

	static float ProjectedArea( PolyMesh mesh, int axis )
	{
		var sum = 0f;
		foreach ( var face in mesh.Faces )
		{
			if ( face.Count != 3 )
				continue;
			var a = mesh.Positions[face.Indices[0]];
			var b = mesh.Positions[face.Indices[1]];
			var c = mesh.Positions[face.Indices[2]];
			var cross = Vec3.Cross( b - a, c - a );
			var component = axis == 0 ? cross.x : axis == 1 ? cross.y : cross.z;
			sum += MathF.Abs( component );
		}
		return 0.5f * sum;
	}
}
