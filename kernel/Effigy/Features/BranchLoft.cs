using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Skin a trunk that SPLITS into branches — one section becoming several. The crane's
/// struts, a tree's limbs, a river delta: one column that forks into N tubes.
/// Onshape has no direct equivalent; this is the loft's branching cousin.
///
/// HOW THE JUNCTION STAYS ONE SOLID, NOT OVERLAPPING SOLIDS. A trunk ring cannot be
/// stitched straight to N branch rings and stay manifold — one ring's edges would end
/// up shared by the trunk and every branch at once, which is non-manifold by definition,
/// and a junction surface triangulated between them by hand would be a research project
/// dressed as a feature. So this builds honest closed solids and unions them: the trunk
/// skinned base-to-junction, and each branch skinned from a start ring pulled slightly
/// BACK into the trunk (so the overlap is volumetric, never just two flush faces
/// meeting at the junction plane), up a short neck on the trunk's own axis and out
/// through its cap, and only then slanting to its tip section. The MeshBoolean provider unions them
/// into one closed mesh with the internal faces cut away. The delivered mesh is one
/// solid — check it with MeshValidator, not with a body count — and the overlap is a
/// construction detail the boolean removes, not a seam the user is handed.
///
/// V1 LIMITS, STATED NOT HIDDEN. Branches are single-section: each branch runs from the
/// junction section's shape to one tip section, in one ruled span — no midpoint
/// ring, because an exact-midpoint ring adds no shape and sends the boolean's BSP
/// into infinite recursion (see the union pipeline below). A branch with several
/// sections of its own is the follow-up, not a silent stretch of this one. No holes: a holed trunk splitting into holed branches needs the
/// multi-hole skinning carried through the union, which is not done here. Branch start
/// sections are all the junction section's shape — branches differ by their tip section
/// and direction, not by a per-branch start shape.
/// </summary>
public sealed class BranchLoftFeature : SketchConsumingFeature
{
	public override string TypeName => "Branch loft";

	/// <summary>
	/// The trunk's section sketches, in order. The last one is the junction — the
	/// section the branches split from. Unlike Loft, there is no "every sketch"
	/// fallback: with branches in play, guessing which sketches are trunk and which
	/// are tips would guess wrong in exactly the cases that matter.
	/// </summary>
	public List<string> Sections = new();

	/// <summary>
	/// One tip section sketch per branch, in order. Each branch runs from the junction
	/// section to its tip section. At least one is required; the task's crane case is
	/// four, and nothing here caps the count.
	/// </summary>
	public List<string> Branches = new();

	/// <summary>How many points each section is resampled to. More is smoother and heavier; the
	/// default is enough for a circle to read as round without subdivision.</summary>
	public readonly IntParam Segments = new( "Segments", 24, 3, 512 );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Segments, Result, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Sections.Count < 2 )
			Fail(
				"A branch loft needs at least two trunk sections.",
				$"Only {Sections.Count} trunk section(s) were given, so there is no trunk to split.",
				"Give the trunk at least a base section and a junction section",
				"Use a plain Loft for a trunk with no branches" );

		if ( Branches.Count < 1 )
			Fail(
				"A branch loft needs at least one branch.",
				"No branch tip section was given, so there is nothing to split into.",
				"Give at least one branch tip section",
				"Use a plain Loft for a trunk with no branches" );

		var trunkSketches = ResolveSections( ctx );
		var count = Segments.Clamped;

		// Trunk rings: resample + align, exactly the plain-loft path (no holes here),
		// so a trunk on its own rebuilds the way a loft would.
		var rings = new List<List<Vec3>>( trunkSketches.Count );

		foreach ( var sketch in trunkSketches )
		{
			var found = ProfileFinder.Find( sketch );

			if ( found.Profiles.Count == 0 )
				Fail(
					"One of the trunk's sections has no closed region in it.",
					"Every trunk section has to be a closed loop so it can be skinned to the next.",
					"Close that section's curves into a loop",
					"Remove that sketch from the trunk" );

			var ring = Skinner.Resample( found.Profiles[0].Outer, count )
				.Select( sketch.Plane.ToWorld ).ToList();

			if ( rings.Count > 0 )
				ring = Skinner.Align( ring, rings[^1] );

			rings.Add( ring );
		}

		var junction = rings[^1];
		var junctionCentre = SweepFeature.CentreOf3( junction );
		var prevCentre = SweepFeature.CentreOf3( rings[^2] );
		var trunkDir = (junctionCentre - prevCentre).Normal;

		// How far back into the trunk each branch starts: a quarter of the junction
		// section's mean radius. Enough that the union overlap is a volume, small
		// enough that the branch does not swallow the trunk's lower half.
		var meanRadius = 0f;

		foreach ( var p in junction )
			meanRadius += (p - junctionCentre).Length;

		meanRadius /= junction.Count;

		var overlap = meanRadius * 0.25f;

		var result = Skinner.SkinChecked(
			rings.Select( r => new List<Vec3>( r ) ).ToList(), capEnds: true, Material.Clamped );

		// THE NECK IS WHAT MAKES THE UNION CLEAN. A branch that slants straight from
		// the junction to its tip pokes through the trunk's SIDE wall just below the
		// top rim as well as through its cap — two intersections meeting at the rim,
		// which is exactly the case the exact boolean mangles (open edges, edges
		// shared by 3-4 faces). So each branch first runs straight up the trunk's own
		// axis, inside the trunk and out through its cap only, and only then slants to
		// its tip, above the trunk entirely. One clean through-cap exit per branch.
		var neck = junction.Select( p => junctionCentre + (p - junctionCentre) * 0.9f + trunkDir * (meanRadius * 0.75f) ).ToList();

		foreach ( var branchId in Branches )
		{
			if ( !ctx.Sketches.TryGetValue( branchId, out var tipSketch ) )
				Fail(
					$"Sketch '{branchId}' is not available at this point in the tree",
					"A branch this loft was pointed at has been deleted, suppressed, or sits below this feature.",
					"Pick a sketch that sits above this feature",
					"Move this feature below that sketch" );

			var found = ProfileFinder.Find( tipSketch );

			if ( found.Profiles.Count == 0 )
				Fail(
					"One of the branch tips has no closed region in it.",
					"Every branch tip has to be a closed loop so it can be skinned from the junction.",
					"Close that tip's curves into a loop",
					"Remove that sketch from the branches" );

			var tip = Skinner.Resample( found.Profiles[0].Outer, count )
				.Select( tipSketch.Plane.ToWorld ).ToList();
			tip = Skinner.Align( tip, junction );

			// Start slightly SMALLER than the junction section as well as pulled back:
			// a start ring on the trunk's own wall cylinder shares the wall's surface,
				// which is a second degeneracy the boolean does not need.
			var start = junction.Select( p => junctionCentre + (p - junctionCentre) * 0.9f - trunkDir * overlap ).ToList();
			// Neck is shared across branches, and SkinChecked reverses its rings in
			// place when it flips a winding — so each branch works on its own COPY.
			// Sharing the list would let branch 1's flip twist branch 2's rings.
			var neckCopy = new List<Vec3>( neck );

			// NO MIDPOINT RING, deliberately. A ring at the exact midpoint of neck-to-tip
			// adds no shape — it lies exactly on the ruled interpolation, so the volume
			// is identical with or without it — and its exact-midpoint collinearity is
			// what sends the boolean's BSP into infinite recursion (proven in the
			// harness: same tube overflows with mid, completes without it).
			var branch = Skinner.SkinChecked(
				new List<List<Vec3>> { start, neckCopy, tip }, capEnds: true, Material.Clamped );

			// UNION PIPELINE, in order — each step proven necessary in the harness:
			// 1. TRIANGULATE both inputs. Skin stitches rings with quads, and a quad
			// between two rings that differ in centre, radius, or rotation is generally
			// WARPED (non-planar): its fourth vertex sits off the plane of its first
			// three. The exact boolean builds BSP planes from polygon normals, so a
			// warped quad straddles its OWN plane and the BSP mangles the seam. Triangles
			// are planar by definition. The vertical start-to-neck cylinder worked
			// un-triangulated precisely because its quads are rectangles, i.e. planar.
			// 2. WELD at 1e-3, but CO-FACE-AWARE — never merging two vertices that
			// share a face. A plain weld turns a sliver face (v1,v2,w) with v1 almost
			// equal to v2 into (v,v,w): a zero-length self-loop edge, which
			// MeshValidator counts as a boundary edge — and the NEXT branch's union
			// then refuses the mesh as "not closed", failing the whole feature on
			// branch 2. Skipping co-face pairs keeps every sliver face a real
			// triangle (its neighbours merge onto its vertices consistently, so its
			// edges stay shared by two faces) while still closing the boolean's
			// stitch misses, which are always between faces that share nothing.
			// Larger tolerances only CREATE non-manifold edges, so 1e-3 is the number.
			// 3. COPLANAR MERGE. It drops the boolean's split fragments back together
			// and takes the last non-manifold edge with it.
			// FIXED (2026-10-24, wave 5 boolean lane): the 11 boundary edges above are
			// gone. Root cause was in ExactMeshBoolean's weld, not in this feature:
			// its single-cell grid lookup left vertices 1e-6 apart unwelded whenever
			// they straddled a cell boundary, and the resulting twin vertices ended up
			// inside the same faces, which is exactly what this weld's co-face rule
			// refuses to merge — so no later pass could close the cracks. The boolean's
			// weld now searches neighbouring cells (cell side sqrt(3) * WeldEps), the
			// twins merge, and the first branch union comes back with 0 boundary
			// edges. A plain global weld is still NOT the answer: it merges across
			// legitimately distinct sheets and drops faces, losing volume (proven in
			// the wave 5 probe: volume 56.7 -> 54.0, open edges 11 -> 56).
			if ( !MeshBoolean.Available )
				Fail(
					"Branch union unavailable",
					"No mesh boolean provider is installed, so the trunk and branches cannot be unioned.",
					"Run this in the s&box editor, where the engine boolean is installed",
					"Or install a boolean provider before rebuilding" );

			if ( !MeshBoolean.Provider.TryApply( BooleanOp.Union, Triangulate( result ), Triangulate( branch ), out var union, out var error ) )
			{
				// The boolean's generic remedies name Method=Voxel, which this
				// feature has no parameter for — strip it so the user is not
				// sent after an option that does not exist here.
				var msg = (error ?? "The boolean refused the trunk/branch union.")
					.Replace( "Simplify the sections, lower the segment count, move the branch so its faces are not coincident with the trunk, or use Method=Voxel.",
						"Simplify the sections, lower the segment count, or move the branch so its faces are not coincident with the trunk." )
					.Replace( " Use Method=Voxel for a mesh this size.", "" )
					.Replace( " or use Method=Voxel", "" )
					.Replace( ", or use Method=Voxel", "" );
				Fail(
					"Branch union failed",
					msg,
					"Simplify the branch sections", "Lower the segment count" );
			}

			var welded = WeldKeepingFaces( union, 1e-3f );
			CoplanarMerge.Merge( welded );
			result = welded;
		}

		// Validation counts DISTINCT FACES per edge, not BuildEdgeFaces entries.
		// A degenerate face (v,v,w) contributes its (v,w) edge TWICE to
		// BuildEdgeFaces, so a count-based check reads a real opening as closed
		// (proven: plain-weld result reports 1 boundary edge by count, 2 by
		// distinct faces). Distinct-face counting cannot be fooled that way.
		// A zero-length self-loop edge (both endpoints the same vertex) is a
		// degenerate artifact of the boolean's splitting, not a hole: nothing can
		// pass through it, it has no area, and deleting the faces that carry it
		// OPENS real holes (proven: deleting the degenerate faces after the weld
		// took the test case from 1 open edge to 34, with volume lost). So real
		// open/non-manifold edges fail, and degenerate ones are reported in the
		// warning and kept.
		var distinctFaces = new Dictionary<EdgeKey, HashSet<int>>();

		for ( var fi = 0; fi < result.FaceCount; fi++ )
		{
			var indices = result.Faces[fi].Indices;

			for ( var i = 0; i < indices.Length; i++ )
			{
				var key = new EdgeKey( indices[i], indices[i + 1 == indices.Length ? 0 : i + 1] );

				if ( !distinctFaces.TryGetValue( key, out var set ) )
					distinctFaces[key] = set = new HashSet<int>();

				set.Add( fi );
			}
		}

		var openReal = 0;
		var nonManifoldReal = 0;
		var degenerate = 0;

		foreach ( var (key, faces) in distinctFaces )
		{
			if ( faces.Count == 2 )
				continue;

			var length = (result.Positions[key.A] - result.Positions[key.B]).Length;

			if ( length <= 1e-6f )
				degenerate++;
			else if ( faces.Count == 1 )
				openReal++;
			else
				nonManifoldReal++;
		}

		if ( openReal > 0 || nonManifoldReal > 0 )
			Fail(
				"Branch loft came back open",
				$"The trunk/branch union left {openReal} open and {nonManifoldReal} non-manifold real edges, so the result is not a solid.",
				"Simplify the branch sections", "Lower the segment count" );

		if ( degenerate > 0 )
			Warning = $"Branch loft kept {degenerate} zero-length degenerate edge(s) from the boolean's splitting — artifacts with no area, not openings. Volume is unaffected.";

		Emit( ctx, result );
	}

	/// <summary>Fan-split every face into triangles. Quads stay quads only when
	/// they are already triangles; n-gon caps fan from vertex 0, which is exact for
	/// the planar rings skins produce. UVs follow their vertices.</summary>
	static PolyMesh Triangulate( PolyMesh mesh )
	{
		var result = new PolyMesh();

		foreach ( var p in mesh.Positions )
			result.AddVertex( p );

		foreach ( var face in mesh.Faces )
		{
			foreach ( var (indices, uvs) in TriangulateFace( mesh, face ) )
				result.AddFace( indices, uvs, face.Material );
		}

		return result;
	}

	// Ear clipping, not fan-from-vertex-0. A fan emits every spoke from loop[0],
	// and for a CONCAVE face some of those spokes leave the face: the triangle on
	// the far side of a spoke is then claimed by the neighbouring face too, and
	// the next union's input comes back with that edge shared by 4 faces — which
	// is exactly how branch 2's union was refused after branch 1 closed clean
	// (proven in the wave 5 probe: the branch-1 result validates closed as
	// n-gons, but fan triangulation gives edge (208,277) 4 faces, and the same
	// for (465,553), (465,578) and (461,465) once the merge is deferred).
	// An ear is only emitted when its triangle lies inside the face, so a
	// diagonal interior to one face is never also emitted by its neighbour: the
	// diagonal plus the shared boundary chain bounds a triangle on exactly one
	// side of the chain. Faces here are planar (boolean fragments and skin
	// quads), so the inside test runs in the Newell-plane projection; a face
	// whose projection folds, or where no ear can be found, falls back to the
	// old fan rather than failing the feature.
	static List<(int[] Indices, Vec2[] UVs)> TriangulateFace( PolyMesh mesh, Face face )
	{
		var loop = new List<int>( face.Indices );
		var uvs = new List<Vec2>( face.UVs );
		var tris = new List<(int[], Vec2[])>();

		// Defensive: consecutive duplicates (and a closing duplicate) carry no
		// geometry; dropping them keeps the ear test honest.
		for ( var i = loop.Count - 1; i >= 0 && loop.Count > 1; i-- )
		{
			var prev = loop[(i + loop.Count - 1) % loop.Count];
			if ( loop[i] == prev )
			{
				loop.RemoveAt( i );
				uvs.RemoveAt( i );
			}
		}

		if ( loop.Count < 3 )
			return tris;

		if ( loop.Count == 3 )
		{
			tris.Add( (loop.ToArray(), uvs.ToArray()) );
			return tris;
		}

		// Newell normal, for the projection plane and the convex-side sign.
		var n = Vec3.Zero;
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = mesh.Positions[loop[i]];
			var b = mesh.Positions[loop[(i + 1) % loop.Count]];
			n += Vec3.Cross( a, b );
		}

		if ( n.LengthSquared < 1e-12f )
		{
			// No area at all: fall back to the fan; the boolean drops the
			// degenerate triangles downstream.
			Fan( loop, uvs, tris );
			return tris;
		}

		// Drop the dominant normal axis: the remaining two coordinates are the
		// projection plane.
		var ax = MathF.Abs( n.x ) >= MathF.Abs( n.y ) && MathF.Abs( n.x ) >= MathF.Abs( n.z ) ? 0
			: MathF.Abs( n.y ) >= MathF.Abs( n.z ) ? 1 : 2;
		var (u, v) = ax == 0 ? (1, 2) : ax == 1 ? (0, 2) : (0, 1);
		Vec2 P( int vi )
		{
			var p = mesh.Positions[vi];
			var c = new[] { p.x, p.y, p.z };
			return new Vec2( c[u], c[v] );
		}
		// Sign of the projected Newell area: ears must turn the same way.
		var area2 = 0f;
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = P( loop[i] );
			var b = P( loop[(i + 1) % loop.Count] );
			area2 += a.x * b.y - b.x * a.y;
		}
		var sign = area2 >= 0 ? 1f : -1f;

		// HYBRID: a face that is convex in projection keeps the old fan exactly —
		// same triangles, same order, so skin inputs triangulate byte-identically
		// to before (the BSP is order-sensitive and overflows on some inputs, so
		// branch 1 must not change). Only concave faces take the ear path, and
		// only a concave face can emit a fan spoke outside itself.
		var convex = true;
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = P( loop[(i + loop.Count - 1) % loop.Count] );
			var b = P( loop[i] );
			var c = P( loop[(i + 1) % loop.Count] );
			var cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
			if ( cross * sign <= 0 ) { convex = false; break; }
		}
		if ( convex )
		{
			Fan( loop, uvs, tris );
			return tris;
		}

		var guard = 0;
		while ( loop.Count > 3 && guard++ < loop.Count * loop.Count + 16 )
		{
			var ear = -1;
			for ( var i = 0; i < loop.Count; i++ )
			{
				var prev = loop[(i + loop.Count - 1) % loop.Count];
				var cur = loop[i];
				var next = loop[(i + 1) % loop.Count];
				var a = P( prev );
					var b = P( cur );
					var c = P( next );
					var cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
					// Zero-area ear: the corner is collinear with its neighbours,
					// so the triangle has no area — but it must still be EMITTED.
					// Dropping the corner instead leaves its two loop edges owned
					// by the neighbour alone, which is exactly the 73 boundary
					// edges the first ear-clipping version produced: the corner's
					// edges are real surface edges even when the corner is flat.
					if ( MathF.Abs( cross ) < 1e-12f )
					{
						var pi0 = (i + loop.Count - 1) % loop.Count;
						var ni0 = (i + 1) % loop.Count;
						tris.Add( (new[] { loop[pi0], loop[i], loop[ni0] }, new[] { uvs[pi0], uvs[i], uvs[ni0] }) );
						loop.RemoveAt( i );
						uvs.RemoveAt( i );
						ear = -2;
						break;
					}
					if ( cross * sign <= 0 )
						continue;
					var inside = false;
					for ( var j = 0; j < loop.Count; j++ )
					{
						if ( j == (i + loop.Count - 1) % loop.Count || j == i || j == (i + 1) % loop.Count )
							continue;
						if ( PointInTriangleStrict( P( loop[j] ), a, b, c ) )
						{ inside = true; break; }
					}
					if ( inside )
						continue;
					ear = i;
					break;
			}
			if ( ear == -2 )
				continue;
			if ( ear < 0 )
				break;
			var pi = (ear + loop.Count - 1) % loop.Count;
			var ni = (ear + 1) % loop.Count;
			tris.Add( (new[] { loop[pi], loop[ear], loop[ni] }, new[] { uvs[pi], uvs[ear], uvs[ni] }) );
			loop.RemoveAt( ear );
			uvs.RemoveAt( ear );
		}

		if ( loop.Count == 3 )
			tris.Add( (loop.ToArray(), uvs.ToArray()) );
		else if ( loop.Count > 3 )
			// No ear found (folded projection or degenerate loop): fan the
			// remainder rather than dropping the face.
			Fan( loop, uvs, tris );

		return tris;
	}

	static void Fan( List<int> loop, List<Vec2> uvs, List<(int[], Vec2[])> tris )
	{
		for ( var i = 1; i + 1 < loop.Count; i++ )
			tris.Add( (new[] { loop[0], loop[i], loop[i + 1] }, new[] { uvs[0], uvs[i], uvs[i + 1] }) );
	}

	// Strictly inside (not on an edge): a vertex exactly on an ear edge is a
	// seam vertex CoplanarMerge left in the loop, not an obstruction.
	static bool PointInTriangleStrict( Vec2 p, Vec2 a, Vec2 b, Vec2 c )
	{
		var d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
		var d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
		var d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
		const float tol = 1e-9f;
		return (d1 > tol && d2 > tol && d3 > tol) || (d1 < -tol && d2 < -tol && d3 < -tol);
	}

	List<Sketch> ResolveSections( FeatureContext ctx )
	{
		var result = new List<Sketch>( Sections.Count );

		foreach ( var id in Sections )
		{
			if ( !ctx.Sketches.TryGetValue( id, out var sketch ) )
			{
				Fail(
					$"Sketch '{id}' is not available at this point in the tree",
					"A section this branch loft was pointed at has been deleted, suppressed, or sits below this feature.",
					"Pick a sketch that sits above this feature",
					"Move this feature below that sketch" );
			}

			result.Add( sketch );
		}

		return result;
	}
	/// <summary>
	/// Weld vertices within tolerance, except pairs that share a face. Merging a
	/// co-face pair would collapse its face into a repeated-vertex triangle whose
	/// self-loop edge MeshValidator counts as a boundary — the exact failure that
	/// blocks the next union in the loop above. The conflict check (no face may
	/// hold members of both components being merged) keeps the invariant that no
	/// face ever ends up with two vertices in one component, so no remapped face
	/// repeats an index. If one somehow did, the unwelded mesh is returned and the
	/// feature's validation fails loudly downstream rather than corrupting geometry.
	/// Per-vertex data is not carried: branch meshes are Skinner outputs, unrigged
	/// and uncoloured, and per-corner UVs already travel with the faces.
	/// </summary>
	static PolyMesh WeldKeepingFaces( PolyMesh mesh, float tolerance )
	{
		var count = mesh.VertexCount;
		var parent = new int[count];

		for ( var v = 0; v < count; v++ )
			parent[v] = v;

		int Find( int v )
		{
			while ( parent[v] != v )
			{
				parent[v] = parent[parent[v]];
				v = parent[v];
			}

			return v;
		}

		var coFace = new HashSet<(int, int)>();

		foreach ( var face in mesh.Faces )
			for ( var i = 0; i < face.Count; i++ )
				for ( var j = i + 1; j < face.Count; j++ )
					coFace.Add( (Math.Min( face.Indices[i], face.Indices[j] ), Math.Max( face.Indices[i], face.Indices[j] )) );

		var vertexFaces = mesh.BuildVertexFaces();
		var members = new Dictionary<int, List<int>>();

		for ( var v = 0; v < count; v++ )
			members[v] = new List<int> { v };

		var cells = new Dictionary<(long, long, long), List<int>>();

		(long, long, long) Cell( Vec3 p ) =>
			((long)Math.Floor( p.x / tolerance ), (long)Math.Floor( p.y / tolerance ), (long)Math.Floor( p.z / tolerance ));

		for ( var v = 0; v < count; v++ )
		{
			var cell = Cell( mesh.Positions[v] );

			if ( !cells.TryGetValue( cell, out var list ) )
				cells[cell] = list = new List<int>();

			list.Add( v );
		}

		bool Conflict( int rootA, int rootB )
		{
			foreach ( var member in members[rootA] )
				foreach ( var faceIndex in vertexFaces[member] )
					foreach ( var corner in mesh.Faces[faceIndex].Indices )
						if ( Find( corner ) == rootB )
							return true;

			return false;
		}

		foreach ( var (cell, verts) in cells )
			for ( var dx = -1; dx <= 1; dx++ )
				for ( var dy = -1; dy <= 1; dy++ )
					for ( var dz = -1; dz <= 1; dz++ )
						if ( cells.TryGetValue( (cell.Item1 + dx, cell.Item2 + dy, cell.Item3 + dz), out var neighbours ) )
							foreach ( var a in verts )
								foreach ( var b in neighbours )
								{
									if ( b <= a )
										continue;

									if ( (mesh.Positions[a] - mesh.Positions[b]).Length > tolerance )
										continue;

									if ( coFace.Contains( (Math.Min( a, b ), Math.Max( a, b )) ) )
										continue;

									var rootA = Find( a );
									var rootB = Find( b );

									if ( rootA == rootB || Conflict( rootA, rootB ) )
										continue;

									parent[rootB] = rootA;
									members[rootA].AddRange( members[rootB] );
									members.Remove( rootB );
								}

		var reps = members.Keys.OrderBy( root => members[root].Min() ).ToList();
		var remap = new Dictionary<int, int>();

		for ( var i = 0; i < reps.Count; i++ )
			remap[reps[i]] = i;

		var result = new PolyMesh();

		foreach ( var rep in reps )
			result.AddVertex( mesh.Positions[members[rep].Min()] );

		foreach ( var face in mesh.Faces )
		{
			var indices = face.Indices.Select( vertex => remap[Find( vertex )] ).ToArray();

			if ( indices.Distinct().Count() != indices.Length )
				return mesh.Clone();

			result.AddFace( indices, face.UVs.ToArray(), face.Material );
		}

		return result;
	}

}
