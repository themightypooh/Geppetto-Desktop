using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>How a skin is weighted when nobody paints it. Saved with the rig; see <see cref="SkinBinder.Bind"/>.</summary>
public enum BindMethod
{
	/// <summary>Each body to its assigned bone (nearest bone when unassigned), then softened across
	/// the mesh. Rigid parts with smoothed seams: right for machinery and kit-built props.</summary>
	Bodies,

	/// <summary>Bone heat: weights diffuse over the surface from the nearest bone each point can see.
	/// Smooth and shape-aware; best on one closed skin.</summary>
	Heat,

	/// <summary>Geodesic voxel: weights fall off with distance measured THROUGH the solid. Robust on
	/// overlapping parts, open shells and non-manifold meshes.</summary>
	Voxel,

	/// <summary>Straight-line distance to each bone. Cheapest, and leaks between limbs that are close in space.</summary>
	Distance,
}

/// <summary>What an automatic bind actually did — which method ran on which vertices, and why any
/// fell back. Every fallback is named here, so a bind never quietly does something else.</summary>
public sealed class BindReport
{
	public BindMethod Requested;

	/// <summary>The method most vertices were weighted by.</summary>
	public BindMethod Used;

	public int HeatVertices;
	public int VoxelVertices;
	public int OtherVertices;

	/// <summary>Vertices rebound rigidly as rigid islands by Heat (see BindRigidIslands).</summary>
	public int RigidVertices;

	/// <summary>Why the requested method was not used everywhere, or null if it was.</summary>
	public string Fallback;

	public List<string> Notes = new();

	public override string ToString()
	{
		var parts = new List<string> { $"{Requested} bind" };
		if ( Used != Requested ) parts.Add( $"ran as {Used}" );
		if ( HeatVertices > 0 ) parts.Add( $"{HeatVertices} vertices by heat" );
		if ( VoxelVertices > 0 ) parts.Add( $"{VoxelVertices} by voxel distance" );
		if ( Fallback is not null ) parts.Add( Fallback );
		parts.AddRange( Notes );
		return string.Join( "; ", parts );
	}
}

/// <summary>Knobs for <see cref="AutoWeights"/>. The defaults are what the editor uses.</summary>
public sealed class AutoWeightOptions
{
	/// <summary>Influences kept per vertex, renormalised. Four is what engines index.</summary>
	public int MaxInfluences = 4;

	/// <summary>Influences below this are dropped before renormalising.</summary>
	public float MinWeight = 0.01f;

	/// <summary>Voxel binding: cells along the model's longest side. More is finer and slower.</summary>
	public int VoxelResolution = 64;

	/// <summary>Voxel binding: weight ∝ (1 / interior distance)^Falloff. Higher is a tighter joint.</summary>
	public float VoxelFalloff = 3f;

	/// <summary>Voxel binding: neighbour-averaging passes over the mesh after weighting.</summary>
	public int VoxelSmoothPasses = 2;

	/// <summary>
	/// Rigid-island rule for Heat: a disconnected component at most this fraction of the mesh
	/// (and smaller than the largest component) binds rigidly to its nearest bone instead of
	/// smearing across the skin. 0 disables the size rule; name-matched ranges still apply.
	/// </summary>
	public float RigidIslandMaxFraction = 0.10f;

	/// <summary>Body-name fragments that mark a range a rigid island (eyes, teeth, claws, buttons).</summary>
	public string[] RigidIslandNames = { "eye", "teeth", "tooth", "claw", "button", "fang" };

	/// <summary>Weight twist, helper, IK and face bones too. Off: those are widgets, not limbs — the
	/// same rule <see cref="Skeleton.TryBodySpan"/> uses.</summary>
	public bool IncludeHelperBones;
}

/// <summary>
/// Automatic skin weights that respect the shape: bone heat and geodesic voxel binding.
///
/// BONE HEAT (Baran &amp; Popović, "Automatic Rigging and Animation of 3D Characters", SIGGRAPH
/// 2007). Treat the surface as a heat-conducting plate. Each vertex is warmed by the nearest bone it
/// can SEE — a straight segment to the bone that does not leave the body — with strength 1/d², and
/// the weight of bone i is the equilibrium temperature when only bone i is hot:
///   (−Δ + H) wᵢ = H pᵢ,   H_jj = 1/d_j²,   p_ij = 1 if i is vertex j's nearest visible bone.
/// Multiplying through by the vertex areas makes it symmetric: (L + M·H) wᵢ = M·H pᵢ, with L the
/// cotangent Laplacian. H does not depend on i, so ONE factorisation serves every bone — the reason
/// the sparse solver exists. Heat flows over the surface, so a weight cannot jump a gap between
/// two legs the way straight-line distance does.
///
/// GEODESIC VOXEL (Dionne &amp; de Lasa, "Geodesic Voxel Binding for Production Character Meshes",
/// SCA 2013). Voxelise the solid, flood the outside, and measure each vertex's distance to each
/// bone through the inside voxels only; weight falls off with that distance. It needs no manifold,
/// no single shell and no clean normals, which is why it is the fallback: heat fails on exactly the
/// meshes agent-built and kit-built models produce — overlapping parts, open shells, non-manifold
/// joins (the "failed to find solution" every auto-weight user has met).
///
/// THE FALLBACK IS PER PIECE, AND REPORTED. A non-manifold mesh goes to voxel entirely. A piece
/// of the mesh that can see no bone at all (an eyeball inside a closed head) has no heat source and
/// no answer; those vertices alone take voxel weights. <see cref="BindReport"/> says which and why.
/// </summary>
public static class AutoWeights
{
	/// <summary>Bone heat, falling back to voxel where heat has no answer. See the class notes.</summary>
	public static SkinWeights Heat( PolyMesh mesh, Skeleton skeleton, out BindReport report, AutoWeightOptions options = null, IReadOnlyList<BodyRange> ranges = null )
	{
		options ??= new AutoWeightOptions();
		report = new BindReport { Requested = BindMethod.Heat, Used = BindMethod.Heat };
		var bones = Prepare( mesh, skeleton, options, report );

		if ( mesh.VertexCount == 0 )
			return new SkinWeights();

		var validation = MeshValidator.Validate( mesh );

		if ( validation.NonManifoldEdges > 0 )
		{
			report.Used = BindMethod.Voxel;
			report.Fallback = $"the mesh has {validation.NonManifoldEdges} non-manifold edges, where heat has no single answer, "
				+ "so it was weighted by voxel distance instead";
			var voxel = VoxelCore( mesh, skeleton, bones, options, report );
			report.VoxelVertices = mesh.VertexCount;
			NoteRigid( report, BindRigidIslands( mesh, voxel, skeleton, options, ranges ).Count );
			return voxel;
		}

		var lap = MeshLaplacian.Cotangent( mesh );
		var n = mesh.VertexCount;
		var tree = MeshBVH.Build( mesh );
		var diag = Math.Max( mesh.BoundsDiagonal, 1e-6f );
		var eps = 1e-4f * diag;
		var floorDist = 1e-3f * diag;

		var heat = new double[n];
		var nearest = new List<int>[n];
		var order = new int[bones.Length];
		var dist = new float[bones.Length];
		var closest = new Vec3[bones.Length];

		for ( var v = 0; v < n; v++ )
		{
			var p = mesh.Positions[v];

			for ( var b = 0; b < bones.Length; b++ )
			{
				closest[b] = ClosestOnSegment( p, bones[b].Head, bones[b].Tail );
				dist[b] = (closest[b] - p).Length;
				order[b] = b;
			}

			Array.Sort( (float[])dist.Clone(), order );

			var best = -1f;

			foreach ( var b in order )
			{
				// Past the nearest visible bone (and its exact ties), nothing nearer remains.
				if ( best >= 0f && dist[b] > best * 1.0001f + 1e-9f )
					break;

				if ( !Visible( mesh, tree, p, closest[b], dist[b], eps ) )
					continue;

				if ( best < 0f )
					best = dist[b];

				(nearest[v] ??= new List<int>( 1 )).Add( b );
			}

			if ( best >= 0f )
			{
				var d = Math.Max( best, floorDist );
				heat[v] = lap.Mass[v] / ((double)d * d);
			}
		}

		// Pieces with no heat source anywhere are singular; pin them out of the solve and weight
		// them by voxel distance instead. Isolated vertices (no face) likewise.
		var label = lap.Components( out var pieces );
		var warmed = new bool[pieces];

		for ( var v = 0; v < n; v++ )
			if ( heat[v] > 0 ) warmed[label[v]] = true;

		var cold = new List<int>();
		var isolated = new HashSet<int>( lap.Isolated );

		for ( var v = 0; v < n; v++ )
			if ( !warmed[label[v]] || isolated.Contains( v ) )
				cold.Add( v );

		var a = lap.L.AddDiagonal( heat );

		if ( !DirichletSolver.TryCreate( a, cold, out var solver, out var why ) )
		{
			report.Used = BindMethod.Voxel;
			report.Fallback = $"the heat system could not be solved ({why}), so the mesh was weighted by voxel distance instead";
			var voxel = VoxelCore( mesh, skeleton, bones, options, report );
			report.VoxelVertices = n;
			NoteRigid( report, BindRigidIslands( mesh, voxel, skeleton, options, ranges ).Count );
			return voxel;
		}

		// One solve per bone that is anyone's nearest; the others are zero everywhere.
		var columns = new float[bones.Length][];
		var rhs = new double[n];

		for ( var b = 0; b < bones.Length; b++ )
		{
			var any = false;
			Array.Clear( rhs );

			for ( var v = 0; v < n; v++ )
			{
				if ( nearest[v] is not { } list || !list.Contains( b ) )
					continue;

				rhs[v] = heat[v] / list.Count;
				any = true;
			}

			if ( !any )
				continue;

			var w = solver.Solve( rhs, null );
			columns[b] = w.Select( x => (float)x ).ToArray();
		}

		var weights = new SkinWeights( n );
		var acc = new Dictionary<int, float>();

		for ( var v = 0; v < n; v++ )
		{
			acc.Clear();

			for ( var b = 0; b < bones.Length; b++ )
				if ( columns[b] is { } col && col[v] > 0f )
					acc[bones[b].Index] = col[v];

			weights[v] = Tidy( acc, options );
		}

		report.HeatVertices = n - cold.Count;

		if ( cold.Count > 0 )
		{
			var coldPieces = cold.Select( v => label[v] ).Distinct().Count();
			report.Fallback = $"{cold.Count} vertices in {coldPieces} separate piece(s) cannot see any bone (closed inside another "
				+ "part, or floating), so heat has no source there; they were weighted by voxel distance";
			var voxel = VoxelCore( mesh, skeleton, bones, options, report );

			foreach ( var v in cold )
				weights[v] = voxel[v];

			report.VoxelVertices = cold.Count;
		}

		// A vertex the solve left at zero everywhere (rounding on a far tip) gets its nearest bone.
		for ( var v = 0; v < n; v++ )
		{
			if ( weights[v].Length > 0 )
				continue;

			weights[v] = new[] { new BoneWeight( NearestOf( mesh.Positions[v], bones ), 1f ) };
			report.OtherVertices++;
		}

		if ( report.OtherVertices > 0 )
			report.Notes.Add( $"{report.OtherVertices} vertices received no heat and took their nearest bone" );

		// Last, so a rigid island is rigid whatever path weighted it: heat, cold-piece voxel,
		// or the nearest-bone fallback above.
		NoteRigid( report, BindRigidIslands( mesh, weights, skeleton, options, ranges ).Count );

		return weights;
	}

	/// <summary>Geodesic voxel binding over the whole mesh. See the class notes.</summary>
	public static SkinWeights Voxel( PolyMesh mesh, Skeleton skeleton, out BindReport report, AutoWeightOptions options = null )
	{
		options ??= new AutoWeightOptions();
		report = new BindReport { Requested = BindMethod.Voxel, Used = BindMethod.Voxel };
		var bones = Prepare( mesh, skeleton, options, report );

		if ( mesh.VertexCount == 0 )
			return new SkinWeights();

		var weights = VoxelCore( mesh, skeleton, bones, options, report );
		report.VoxelVertices = mesh.VertexCount;
		return weights;
	}

	// --- shared ------------------------------------------------------------------------------

	internal readonly struct BoneSegment
	{
		public readonly int Index;
		public readonly Vec3 Head, Tail;

		public BoneSegment( int index, Vec3 head, Vec3 tail )
		{
			Index = index;
			Head = head;
			Tail = tail;
		}
	}

	static BoneSegment[] Prepare( PolyMesh mesh, Skeleton skeleton, AutoWeightOptions options, BindReport report )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );

		if ( skeleton.Count == 0 )
			throw new InvalidOperationException( "Cannot bind to a skeleton with no bones. Add bones in the Rig workspace first." );

		if ( options.MaxInfluences < 1 )
			throw new ArgumentOutOfRangeException( nameof( options ), "MaxInfluences must be at least 1" );

		var list = new List<BoneSegment>();
		var skipped = new List<string>();

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			if ( !options.IncludeHelperBones && Skeleton.IsAuxiliaryBone( skeleton.Bones[b].Name ) )
			{
				skipped.Add( skeleton.Bones[b].Name );
				continue;
			}

			if ( !skeleton.TryBodySpan( b, out var head, out var tail ) )
			{
				head = skeleton.HeadWorld( b );
				tail = skeleton.TailWorld( b );
			}

			list.Add( new BoneSegment( b, head, tail ) );
		}

		// Every bone is a helper by name: weight them all rather than none.
		if ( list.Count == 0 )
		{
			for ( var b = 0; b < skeleton.Count; b++ )
				list.Add( new BoneSegment( b, skeleton.HeadWorld( b ), skeleton.TailWorld( b ) ) );

			report.Notes.Add( "every bone is named like a helper (twist/IK/face…), so all of them were weighted" );
		}
		else if ( skipped.Count > 0 )
		{
			report.Notes.Add( $"skipped {skipped.Count} helper bone(s) by name: {string.Join( ", ", skipped.Take( 6 ) )}"
				+ (skipped.Count > 6 ? ", …" : "") );
		}

		return list.ToArray();
	}

	static BoneWeight[] Tidy( Dictionary<int, float> acc, AutoWeightOptions options )
	{
		var normalised = SkinWeights.Finish( acc );

		if ( normalised.Length == 0 )
			return normalised;

		var kept = new Dictionary<int, float>();

		for ( var i = 0; i < normalised.Length && i < options.MaxInfluences; i++ )
		{
			if ( i > 0 && normalised[i].Weight < options.MinWeight )
				break;

			kept[normalised[i].Bone] = normalised[i].Weight;
		}

		return SkinWeights.Finish( kept );
	}

	static Vec3 ClosestOnSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var l2 = ab.LengthSquared;

		if ( l2 < 1e-12f )
			return a;

		var t = Math.Clamp( Vec3.Dot( p - a, ab ) / l2, 0f, 1f );
		return a + ab * t;
	}

	static int NearestOf( Vec3 p, BoneSegment[] bones )
	{
		var best = bones[0].Index;
		var bestD = float.MaxValue;

		foreach ( var s in bones )
		{
			var d = (ClosestOnSegment( p, s.Head, s.Tail ) - p).LengthSquared;
			if ( d < bestD ) { bestD = d; best = s.Index; }
		}

		return best;
	}

	/// <summary>Whether the straight segment from a surface point to a point on a bone stays inside —
	/// no face crosses it. Starts a hair along the segment so the point's own faces do not count.</summary>
	static bool Visible( PolyMesh mesh, MeshBVH tree, Vec3 from, Vec3 to, float length, float eps )
	{
		if ( length <= 4f * eps )
			return true;

		var dir = (to - from) / length;
		var hit = tree.Raycast( mesh, from + dir * eps, dir );
		return hit is not { } h || h.Distance >= length - 2f * eps;
	}

	// --- geodesic voxel ----------------------------------------------------------------------

	static SkinWeights VoxelCore( PolyMesh mesh, Skeleton skeleton, BoneSegment[] bones, AutoWeightOptions options, BindReport report )
	{
		var grid = VoxelGrid.Build( mesh, Math.Clamp( options.VoxelResolution, 8, 256 ) );
		var n = mesh.VertexCount;

		// Distance through the solid from every bone to every solid cell.
		var field = new float[bones.Length][];

		for ( var b = 0; b < bones.Length; b++ )
			field[b] = grid.GeodesicFrom( bones[b].Head, bones[b].Tail );

		var weights = new SkinWeights( n );
		var acc = new Dictionary<int, float>();
		var falloff = Math.Max( 0.5f, options.VoxelFalloff );
		var floorDist = grid.Cell * 0.5f;
		var unreached = 0;

		for ( var v = 0; v < n; v++ )
		{
			var p = mesh.Positions[v];
			var cell = grid.SolidCellNear( p );
			acc.Clear();

			if ( cell >= 0 )
			{
				var offset = (grid.Centre( cell ) - p).Length;
				var strongest = 0f;

				for ( var b = 0; b < bones.Length; b++ )
				{
					var d = field[b][grid.SolidIndex( cell )];

					if ( float.IsInfinity( d ) )
						continue;

					var k = 1f / MathF.Pow( Math.Max( d + offset, floorDist ), falloff );
					acc[bones[b].Index] = k;
					strongest = Math.Max( strongest, k );
				}

				// Relative cutoff, as BindSmooth: a bone far across the body owns nothing.
				foreach ( var key in acc.Keys.ToList() )
					if ( acc[key] < strongest * 0.02f )
						acc.Remove( key );
			}

			if ( acc.Count == 0 )
			{
				acc[NearestOf( p, bones )] = 1f;
				unreached++;
			}

			weights[v] = SkinWeights.Finish( acc );
		}

		if ( unreached > 0 )
			report.Notes.Add( $"{unreached} vertices were in solid no bone reaches and took their nearest bone" );

		if ( options.VoxelSmoothPasses > 0 )
			weights = SkinBinder.SmoothWeights( mesh, weights, options.VoxelSmoothPasses, 0.5f );

		var tidy = new Dictionary<int, float>();

		for ( var v = 0; v < n; v++ )
		{
			tidy.Clear();
			foreach ( var bw in weights[v] ) tidy[bw.Bone] = bw.Weight;
			weights[v] = Tidy( tidy, options );
		}

		report.Notes.Add( $"voxel grid {grid.Nx}x{grid.Ny}x{grid.Nz}, {grid.SolidCount} solid cells" );
		return weights;
	}
	/// <summary>
	/// Which disconnected components of <paramref name="mesh"/> are rigid islands, and which
	/// bone each binds to (roadmap J2). Two rules, either one enough:
	/// (1) NAME: a <see cref="BodyRange"/> whose body name or id contains a
	/// <see cref="AutoWeightOptions.RigidIslandNames"/> fragment marks every component it
	/// touches rigid — this is how Eyes bind to eye_L and Teeth to jaw even when the island
	/// is not small. (2) SIZE: a component at most
	/// <see cref="AutoWeightOptions.RigidIslandMaxFraction"/> of the mesh, and smaller than the
	/// largest component, is rigid. A one-component mesh has no islands, and a mesh that IS
	/// one small piece is not an island of itself. Pure planning: nothing is mutated here, so
	/// <see cref="WeightEdit.Run"/> can capture its before-state first.
	/// </summary>
	internal static List<(int Bone, List<int> Vertices)> RigidIslandPlan( PolyMesh mesh, Skeleton skeleton,
		AutoWeightOptions options, IReadOnlyList<BodyRange> ranges )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( skeleton is null || skeleton.Count == 0 )
			throw new InvalidOperationException( "Cannot bind to a skeleton with no bones. Add bones in the Rig workspace first." );

		options ??= new AutoWeightOptions();

		var n = mesh.VertexCount;
		var plan = new List<(int, List<int>)>();

		if ( n == 0 )
			return plan;

		var neighbours = mesh.BuildVertexEdges();
		var label = new int[n];

		Array.Fill( label, -1 );

		var pieces = new List<List<int>>();

		for ( var v = 0; v < n; v++ )
		{
			if ( label[v] >= 0 )
				continue;

			var piece = new List<int>();
			var stack = new Stack<int>();

			stack.Push( v );
			label[v] = pieces.Count;

			while ( stack.Count > 0 )
			{
				var cur = stack.Pop();

				piece.Add( cur );

				foreach ( var e in neighbours[cur] )
				{
					var nb = e.A == cur ? e.B : e.A;

					if ( nb < 0 || nb >= n || label[nb] >= 0 )
						continue;

					label[nb] = pieces.Count;
					stack.Push( nb );
				}
			}

			pieces.Add( piece );
		}

		if ( pieces.Count <= 1 )
			return plan;

		var largest = 0;

		foreach ( var p in pieces )
			largest = Math.Max( largest, p.Count );

		var named = new bool[pieces.Count];

		if ( ranges is not null && options.RigidIslandNames is not null )
		{
			foreach ( var r in ranges )
			{
				var name = (r.BodyName ?? "") + " " + (r.BodyId ?? "");
				var hit = false;

				foreach ( var key in options.RigidIslandNames )
				{
					if ( !string.IsNullOrWhiteSpace( key ) && name.Contains( key, StringComparison.OrdinalIgnoreCase ) )
					{
						hit = true;
						break;
					}
				}

				if ( !hit )
					continue;

				for ( var k = r.Start; k < r.Start + r.Count && k < n; k++ )
					if ( label[k] >= 0 )
						named[label[k]] = true;
			}
		}

		var maxFraction = options.RigidIslandMaxFraction;

		for ( var p = 0; p < pieces.Count; p++ )
		{
			var piece = pieces[p];
			var small = maxFraction > 0f && piece.Count <= maxFraction * n && piece.Count < largest;

			if ( !named[p] && !small )
				continue;

			var centroid = Vec3.Zero;

			foreach ( var v in piece )
				centroid += mesh.Positions[v];

			centroid /= Math.Max( piece.Count, 1 );

			// Nearest by bone segment, helpers included: a rigid island legitimately binds to
			// eye_L or jaw, which the heat bone list may have skipped as auxiliaries.
			plan.Add( (SkinBinder.NearestBone( centroid, skeleton ), piece) );
		}

		return plan;
	}

	/// <summary>Apply <see cref="RigidIslandPlan"/> to <paramref name="weights"/> in place; returns the changed vertices.</summary>
	internal static List<int> BindRigidIslands( PolyMesh mesh, SkinWeights weights, Skeleton skeleton,
		AutoWeightOptions options, IReadOnlyList<BodyRange> ranges )
	{
		if ( weights is null || weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"weights ({weights?.Count ?? 0}) and mesh ({mesh.VertexCount}) disagree" );

		var changed = new List<int>();

		foreach ( var (bone, vertices) in RigidIslandPlan( mesh, skeleton, options, ranges ) )
		{
			var single = new[] { new BoneWeight( bone, 1f ) };

			foreach ( var v in vertices )
			{
				weights[v] = single;
				changed.Add( v );
			}
		}

		return changed;
	}

	static void NoteRigid( BindReport report, int count )
	{
		if ( count <= 0 )
			return;

		report.RigidVertices += count;
		report.Notes.Add( $"{count} vertices in rigid island(s) bound 100% to their nearest bone" );
	}

}
