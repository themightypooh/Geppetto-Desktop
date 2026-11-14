using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Effigy;

/// <summary>
/// Lane 8a: the multi-LOD chain — LOD0 at full detail plus three decimated levels, built from
/// the ONE source mesh, each level decimated from the original rather than from the level
/// before it, so a coarse level never inherits the previous level's error on top of its own.
///
/// VERTEX TARGETS, NOT TRIANGLE TARGETS. The spec (docs/SBOX-NATIVE-AI-ASSET-MAKER-LANES.md
/// Lane 8) states the ladder in vertices — LOD0 100%, LOD1 50%, LOD2 25%, LOD3 12.5% — while
/// <see cref="Decimate"/> aims at a triangle count. A vertex target is turned into a triangle
/// target by search, not by a ratio guess: the first probe uses the closed-mesh estimate
/// (one collapse removes one vertex and two triangles), then a bounded binary search over the
/// triangle target closes in on the exact vertex count. Every probe is a fresh
/// <see cref="Decimate.Run"/> from the original mesh, so the search never compounds. A level
/// whose exact vertex count is unreachable — the mesh ran out of legal collapses first, or
/// welding already dropped it below target — reports <see cref="Level.ReachedTarget"/> false
/// and keeps the closest probe, rather than silently shipping a level of the wrong size.
///
/// WHAT RIDES ALONG. Each probe runs with <see cref="Decimate.Options.PreserveUVSeams"/>
/// and <see cref="Decimate.Options.PreserveBoundary"/> on (both are Decimate's defaults, set
/// here explicitly so the guarantee does not depend on a default surviving a refactor):
/// per-corner UVs are carried through the collapse, seam edges are walled and seam-branch
/// vertices pinned, and skin weights are blended along each collapse, so every level is the
/// same rigged model with fewer vertices. Nothing here edits <see cref="LodExport"/> or
/// <see cref="Decimate"/>; <see cref="LodExport.DefaultDistance"/> is called read-only for
/// the switch distances, and <see cref="ToLodGroupList"/> emits the LODGroupList node shape
/// <c>VmdlDocument</c> already writes, with the caller's mesh names in it.
///
/// NOT IN THIS FILE. The dedicated 10% shadow-caster mesh from Lane 8's spec is a separate
/// sub-lane (it is a silhouette mesh for shadow passes, not a fourth LOD), and no wiring into
/// <see cref="LodExport"/> or the test registry is applied here — the owed lines are listed
/// in the lane report, not applied.
/// </summary>
public static class LodChain
{
	/// <summary>Vertex fractions, LOD0 first: full, half, quarter, eighth.</summary>
	public static readonly float[] VertexRatios = { 1f, 0.5f, 0.25f, 0.125f };

	/// <summary>One level of the chain.</summary>
	public sealed class Level
	{
		/// <summary>0 for the full mesh, 1–3 for the decimated copies.</summary>
		public int Index;

		/// <summary>Vertex fraction this level aims at, from <see cref="VertexRatios"/>.</summary>
		public float VertexRatio;

		/// <summary>Vertex count this level aims at (see <see cref="TargetVertices"/>).</summary>
		public int VertexTarget;

		/// <summary>Vertex count this level actually has.</summary>
		public int VertexCount;

		/// <summary>Triangle count of the level's mesh.</summary>
		public int Triangles;

		/// <summary>True only when <see cref="VertexCount"/> equals <see cref="VertexTarget"/>.</summary>
		public bool ReachedTarget;

		/// <summary>Distance, in units, past which this level takes over; 0 for LOD0.</summary>
		public float SwitchDistance;

		/// <summary>The level's mesh. LOD0 is the source mesh itself, not a copy.</summary>
		public PolyMesh Mesh;
	}

	/// <summary>
	/// Vertex count a level aims at: the source count times the level's ratio, halves rounded
	/// away from zero (289 × 0.5 aims at 145, not 144), never below 4 — fewer vertices cannot
	/// enclose anything, and Decimate's own floor is 4 triangles.
	/// </summary>
	public static int TargetVertices( int sourceVertices, int level )
	{
		if ( level < 0 || level >= VertexRatios.Length )
			throw new ArgumentOutOfRangeException( nameof( level ), "LOD level must be 0, 1, 2 or 3" );

		return Math.Max( 4, (int)MathF.Round( sourceVertices * VertexRatios[level], MidpointRounding.AwayFromZero ) );
	}

	/// <summary>
	/// The full LOD0–LOD3 chain for <paramref name="mesh"/>. Levels are decimated from the
	/// source independently, in order, LOD0 first. The source mesh is never modified:
	/// Decimate works on its own unpacked copy and LOD0 hands the source back untouched.
	/// </summary>
	public static List<Level> BuildLodChain( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( mesh.VertexCount < 8 )
			throw new ArgumentException( $"a LOD chain needs at least 8 vertices, got {mesh.VertexCount}", nameof( mesh ) );

		var fromTriangles = Decimate.TriangleCount( mesh );
		if ( fromTriangles < 4 )
			throw new ArgumentException( $"a LOD chain needs at least 4 triangles, got {fromTriangles}", nameof( mesh ) );

		var diagonal = mesh.BoundsDiagonal;
		var chain = new List<Level>
		{
			new()
			{
				Index = 0, VertexRatio = VertexRatios[0], VertexTarget = mesh.VertexCount,
				VertexCount = mesh.VertexCount, Triangles = fromTriangles,
				ReachedTarget = true, SwitchDistance = 0f, Mesh = mesh,
			},
		};

		for ( var i = 1; i < VertexRatios.Length; i++ )
		{
			var target = TargetVertices( mesh.VertexCount, i );
			var (result, exact) = ReduceToVertices( mesh, target, fromTriangles );
			chain.Add( new Level
			{
				Index = i, VertexRatio = VertexRatios[i], VertexTarget = target,
				VertexCount = result.Mesh.VertexCount, Triangles = result.ToTriangles,
				ReachedTarget = exact, SwitchDistance = LodExport.DefaultDistance( diagonal, i ),
				Mesh = result.Mesh,
			} );
		}

		return chain;
	}

	/// <summary>
	/// The Decimate run from <paramref name="mesh"/> whose vertex count is closest to
	/// <paramref name="targetVertices"/>, with whether it hit the target exactly. The search
	/// probes a triangle target, never a vertex count Decimate does not take: first the
	/// closed-mesh estimate (one collapse = one vertex, two triangles), then, only if that
	/// misses, a binary search over the triangle target, which is monotone in vertex count
	/// (a smaller triangle budget can only ask for more collapses). At most 24 probes, and
	/// the best probe seen is kept, so a miss still returns the closest reachable level.
	/// </summary>
	static (Decimate.Result Result, bool Exact) ReduceToVertices( PolyMesh mesh, int targetVertices, int fromTriangles )
	{
		Decimate.Result best = default;
		var bestDiff = int.MaxValue;
		var probes = 0;

		Decimate.Result Try( int triangles )
		{
			var result = Decimate.Run( mesh, new Decimate.Options
			{
				TargetTriangles = triangles,
				PreserveUVSeams = true,
				PreserveBoundary = true,
			} );
			probes++;
			var diff = Math.Abs( result.Mesh.VertexCount - targetVertices );
			if ( diff < bestDiff || (diff == bestDiff && result.Mesh.VertexCount > best.Mesh.VertexCount) )
			{
				best = result;
				bestDiff = diff;
			}
			return result;
		}

		// Closed-mesh estimate first: it is exact on a closed triangulated mesh, so the
		// common case costs one probe, not a search.
		var guess = Math.Clamp( fromTriangles - 2 * (mesh.VertexCount - targetVertices), 4, fromTriangles );
		if ( Try( guess ).Mesh.VertexCount == targetVertices )
			return (best, true);

		// No decimation at all: if welding already dropped the mesh below target, nothing
		// above it exists to find — the unwelded count was never reachable.
		var hi = Try( fromTriangles );
		if ( hi.Mesh.VertexCount == targetVertices )
			return (best, true);
		if ( hi.Mesh.VertexCount < targetVertices )
			return (best, false);

		// Maximum decimation: if even that stays above target, the mesh ran out of legal
		// collapses first — the closest probe is the floor run.
		var lo = Try( 4 );
		if ( lo.Mesh.VertexCount == targetVertices )
			return (best, true);
		if ( lo.Mesh.VertexCount > targetVertices )
			return (best, false);

		// Bracketed: vertices(lo) <= target <= vertices(hi), so binary search applies.
		var loT = 4;
		var hiT = fromTriangles;
		while ( probes < 24 && hiT - loT > 1 )
		{
			var mid = (loT + hiT) / 2;
			var midResult = Try( mid );
			if ( midResult.Mesh.VertexCount == targetVertices )
				return (best, true);
			if ( midResult.Mesh.VertexCount > targetVertices )
				hiT = mid;
			else
				loT = mid;
		}

		return (best, false);
	}

	/// <summary>
	/// The .vmdl LODGroupList node for a chain, in the shape <c>VmdlDocument</c> writes: one
	/// LODGroup per level, in order, the first at threshold 0 and each next one at its switch
	/// distance, naming <paramref name="meshNames"/>[i] for level i. One mesh name per level,
	/// none empty, or the node would name a mesh that does not exist.
	/// </summary>
	public static string ToLodGroupList( IReadOnlyList<Level> chain, IReadOnlyList<string> meshNames )
	{
		if ( chain is null )
			throw new ArgumentNullException( nameof( chain ) );
		if ( meshNames is null )
			throw new ArgumentNullException( nameof( meshNames ) );
		if ( meshNames.Count != chain.Count )
			throw new ArgumentException( $"{meshNames.Count} mesh name(s) for {chain.Count} level(s): give one per level", nameof( meshNames ) );

		var sb = new StringBuilder();
		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"LODGroupList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );

		for ( var i = 0; i < chain.Count; i++ )
		{
			if ( string.IsNullOrEmpty( meshNames[i] ) )
				throw new ArgumentException( $"mesh name for level {i} is empty", nameof( meshNames ) );

			var threshold = i == 0 ? 0f : chain[i].SwitchDistance;
			sb.Append( "\t\t\t\t\t{\n" );
			sb.Append( "\t\t\t\t\t\t_class = \"LODGroup\"\n" );
			sb.Append( $"\t\t\t\t\t\tswitch_threshold = {threshold.ToString( "0.0##", CultureInfo.InvariantCulture )}\n" );
			sb.Append( "\t\t\t\t\t\tmeshes = \n" );
			sb.Append( "\t\t\t\t\t\t[\n" );
			sb.Append( $"\t\t\t\t\t\t\t\"{meshNames[i]}\",\n" );
			sb.Append( "\t\t\t\t\t\t]\n" );
			sb.Append( "\t\t\t\t\t},\n" );
		}

		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );
		return sb.ToString();
	}
}
