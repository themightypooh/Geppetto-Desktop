using System;
using System.Collections.Generic;

namespace Effigy;

public static partial class SkinBinder
{
	/// <summary>Bone heat weights, falling back to voxel distance where heat has no answer. See <see cref="AutoWeights"/>.</summary>
	/// <summary>Bone heat weights, falling back to voxel distance where heat has no answer. See <see cref="AutoWeights"/>.
	/// <paramref name="ranges"/> reaches Heat's rigid-island rule: a name-matched body range binds rigidly
	/// even when it is larger than <see cref="AutoWeightOptions.RigidIslandMaxFraction"/>.</summary>
	public static SkinWeights BindHeat( PolyMesh mesh, Skeleton skeleton, out BindReport report, AutoWeightOptions options = null, IReadOnlyList<BodyRange> ranges = null ) =>
		AutoWeights.Heat( mesh, skeleton, out report, options, ranges );

	/// <summary>Geodesic voxel weights: distance measured through the solid. See <see cref="AutoWeights"/>.</summary>
	public static SkinWeights BindVoxel( PolyMesh mesh, Skeleton skeleton, out BindReport report, AutoWeightOptions options = null ) =>
		AutoWeights.Voxel( mesh, skeleton, out report, options );

	/// <summary>
	/// Bounded biharmonic weights (Jacobson et al., "Bounded Biharmonic Weights for Real-Time
	/// Deformation", SIGGRAPH 2011), reimplemented here from the paper's formulation — not copied
	/// from any implementation. Each bone's weight field minimises the biharmonic energy
	/// ∫|Δw|² over the surface, i.e. it solves the bilaplacian system (L·M⁻¹·L) w = 0 with
	/// Dirichlet data, where L is the cotangent Laplacian and M the lumped mass matrix from
	/// <see cref="MeshLaplacian"/>. The bilaplacian penalises curvature of the field, so the
	/// transition across a joint is smoother than a harmonic (heat) field, which only penalises
	/// gradient and can kink where two bones' influence meets.
	///
	/// HANDLES. A bone's Dirichlet data is its core: the vertices nearest to that bone whose
	/// projection onto the bone segment falls in the middle 70% (t in [0.15, 0.85]). The joint
	/// ends are deliberately left free, so the solved field — not the pin choice — decides the
	/// blend there. A bone with no core vertex (a bone no vertex projects onto the middle of)
	/// pins its single nearest vertex instead, so every solved bone has data. Only bones that
	/// are the nearest bone of at least one vertex are solved; the rest are zero everywhere,
	/// exactly as Heat does.
	///
	/// BOUNDED. After the solve each column is clamped to [0, 1], then pruned by
	/// <see cref="AutoWeightOptions.MinWeight"/> / <see cref="AutoWeightOptions.MaxInfluences"/>
	/// and renormalised, so every vertex keeps a partition of unity. This is the clamp-and-
	/// renormalise approximation to the paper's full active-set quadratic program: the QP's
	/// inequality constraints are enforced after the fact rather than during the solve, so a
	/// field that overshoots near a sharp joint is pulled back instead of ringing.
	///
	/// FALLBACKS, ALL NAMED IN THE REPORT. A vertex with no usable mass (isolated, or a
	/// degenerate triangle's zero mass), a vertex in a mesh component that holds no pin at
	/// all, or a vertex the solve leaves at zero everywhere takes its nearest bone rigidly and
	/// is counted in <see cref="BindReport.OtherVertices"/>. If the conjugate-gradient solve
	/// fails to converge, the whole bind falls back to <see cref="BindHeat"/> and says so —
	/// it never ships a half-converged field as biharmonic.
	///
	/// REPORT HONESTY. <see cref="BindMethod"/> has no Biharmonic value (it lives in
	/// AutoWeights.cs, outside this file's boundary), so Requested/Used say Heat and the first
	/// note states plainly that the weights are biharmonic, not heat. The follow-up that makes
	/// this a first-class option — add BindMethod.Biharmonic to AutoWeights.cs, a Bind() case,
	/// and a TryParseMethod case — is a coordinator sweep item, not something smuggled in here.
	/// Carried own-weights are applied last: a range that arrived with weights of its own
	/// (an imported rig, a garment that copied its wearer) keeps them, the same rule
	/// <see cref="BindBodies"/> uses. Rigid pinning of a body to a bone is <see cref="Bind"/>'s
	/// overlay and arrives with the Bind() case follow-up, not here.
	/// </summary>
	public static SkinWeights BindBiharmonic(
		PolyMesh mesh,
		Skeleton skeleton,
		out BindReport report,
		AutoWeightOptions options = null,
		IReadOnlyList<BodyRange> ranges = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		RequireBones( skeleton );

		options ??= new AutoWeightOptions();

		if ( options.MaxInfluences < 1 )
			throw new ArgumentOutOfRangeException( nameof( options ), "MaxInfluences must be at least 1" );

		report = new BindReport { Requested = BindMethod.Heat, Used = BindMethod.Heat };
		report.Notes.Add( "Bounded-biharmonic (bilaplacian) weights, NOT heat: Requested/Used say Heat only because "
			+ "BindMethod has no Biharmonic value yet — see the follow-up to add one to AutoWeights.cs." );

		var n = mesh.VertexCount;

		if ( n == 0 )
			return new SkinWeights();

		var bones = BiharmonicBones( skeleton, options, report );
		var lap = MeshLaplacian.Cotangent( mesh );
		var mass = lap.Mass;
		var label = lap.Components( out _ );
		var isolated = new HashSet<int>( lap.Isolated );

		var solvable = new bool[n];

		for ( var v = 0; v < n; v++ )
			solvable[v] = !isolated.Contains( v ) && mass[v] > 1e-12;

		// Nearest prepared bone per vertex, by segment distance.
		var nearest = new int[n];
		var nearestDist = new float[n];

		for ( var v = 0; v < n; v++ )
		{
			var best = 0;
			var bestDist = float.MaxValue;

			for ( var b = 0; b < bones.Count; b++ )
			{
				var d = DistanceToSegment( mesh.Positions[v], bones[b].Head, bones[b].Tail );

				if ( d < bestDist )
				{
					bestDist = d;
					best = b;
				}
			}

			nearest[v] = best;
			nearestDist[v] = bestDist;
		}

		// Solved bones: nearest to at least one solvable vertex.
		var solved = new bool[bones.Count];

		for ( var v = 0; v < n; v++ )
			if ( solvable[v] )
				solved[nearest[v]] = true;

		// Core pins per solved bone: middle 70% of the segment, else the nearest vertex.
		var cores = new List<int>[bones.Count];

		for ( var b = 0; b < bones.Count; b++ )
		{
			if ( !solved[b] )
				continue;

			cores[b] = new List<int>();
			var ab = bones[b].Tail - bones[b].Head;
			var lenSq = ab.LengthSquared;

			for ( var v = 0; v < n; v++ )
			{
				if ( !solvable[v] || nearest[v] != b || lenSq < 1e-12f )
					continue;

				var t = Vec3.Dot( mesh.Positions[v] - bones[b].Head, ab ) / lenSq;

				if ( t >= 0.15f && t <= 0.85f )
					cores[b].Add( v );
			}

			if ( cores[b].Count == 0 )
			{
				var closest = -1;
				var closestDist = float.MaxValue;

				for ( var v = 0; v < n; v++ )
				{
					if ( !solvable[v] || nearest[v] != b || nearestDist[v] >= closestDist )
						continue;

					closestDist = nearestDist[v];
					closest = v;
				}

				if ( closest >= 0 )
					cores[b].Add( closest );
			}
		}

		var columns = new double[bones.Count][];

		for ( var b = 0; b < bones.Count; b++ )
		{
			if ( !solved[b] || cores[b].Count == 0 )
				continue;

			var pinned = new bool[n];
			var pinnedValues = new double[n];
			var pinComponents = new HashSet<int>();

			for ( var o = 0; o < bones.Count; o++ )
			{
				if ( !solved[o] || cores[o].Count == 0 )
					continue;

				foreach ( var v in cores[o] )
				{
					pinned[v] = true;
					pinnedValues[v] = o == b ? 1.0 : 0.0;
					pinComponents.Add( label[v] );
				}
			}

			var free = new List<int>();

			for ( var v = 0; v < n; v++ )
				if ( solvable[v] && !pinned[v] && pinComponents.Contains( label[v] ) )
					free.Add( v );

			if ( !TrySolveBilaplacian( lap.L, mass, free, pinned, pinnedValues, out var solvedColumn ) )
			{
				var heat = AutoWeights.Heat( mesh, skeleton, out var heatReport, options, ranges );

				if ( heatReport.Fallback is null )
					heatReport.Fallback = "the biharmonic solve did not converge, so the mesh was weighted by heat instead";

				heatReport.Notes.Add( "Biharmonic (bilaplacian) solve failed to converge; these weights are heat, not biharmonic." );
				report = heatReport;
				return heat;
			}

			columns[b] = solvedColumn;
		}

		var weights = new SkinWeights( n );
		var rigid = 0;

		for ( var v = 0; v < n; v++ )
		{
			var entries = new List<(int Bone, float Weight)>();

			for ( var b = 0; b < bones.Count; b++ )
			{
				if ( columns[b] is null )
					continue;

				var w = (float)Math.Clamp( columns[b][v], 0.0, 1.0 );

				if ( w > 0f )
					entries.Add( (bones[b].Index, w) );
			}

			if ( !solvable[v] || entries.Count == 0 )
			{
				weights[v] = new[] { new BoneWeight( bones[nearest[v]].Index, 1f ) };
				rigid++;
				continue;
			}

			var kept = new List<(int Bone, float Weight)>();

			foreach ( var e in entries )
				if ( e.Weight >= options.MinWeight )
					kept.Add( e );

			if ( kept.Count == 0 )
			{
				var best = entries[0];

				foreach ( var e in entries )
					if ( e.Weight > best.Weight )
						best = e;

				kept.Add( best );
			}

			kept.Sort( ( x, y ) => y.Weight.CompareTo( x.Weight ) );

			if ( kept.Count > options.MaxInfluences )
				kept.RemoveRange( options.MaxInfluences, kept.Count - options.MaxInfluences );

			var total = 0f;

			foreach ( var e in kept )
				total += e.Weight;

			var arr = new BoneWeight[kept.Count];

			for ( var i = 0; i < kept.Count; i++ )
				arr[i] = new BoneWeight( kept[i].Bone, kept[i].Weight / total );

			weights[v] = arr;
		}

		report.OtherVertices = rigid;

		if ( rigid > 0 )
			report.Notes.Add( $"{rigid} vertices had no biharmonic answer (isolated, zero mass, or a pin-less component) and took their nearest bone" );

		// Last: a range that arrived with weights of its own keeps them, the same rule
		// BindBodies uses for unpinned bodies. No pinning is invented here — BindBiharmonic
		// takes no bodyIdToBoneName, and a BodyId is a body, not a bone name.
		ranges ??= Array.Empty<BodyRange>();

		foreach ( var range in ranges )
		{
			if ( !range.HasOwnWeights || !mesh.IsRigged )
				continue;

			for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
				if ( mesh.Skin[i] is { Length: > 0 } own )
					weights[i] = own;
		}

		return weights;
	}

	/// <summary>
	/// THE ONE BIND every caller should use — editor preview, export, playermodels, scripts — so
	/// that what is previewed is what ships.
	///
	/// <see cref="BindMethod.Bodies"/> is exactly the long-standing behaviour: BindBodies then
	/// SmoothWeights. The automatic methods weight the whole merged mesh, then lay the parametric
	/// rules over it the way BindBodies does: a body pinned to a bone in
	/// <paramref name="bodyIdToBoneName"/> is rigid to it, and an unpinned body that arrived with
	/// weights of its own (an imported rig, a garment that copied its wearer) keeps them.
	/// </summary>
	public static SkinWeights Bind(
		PolyMesh mesh,
		IReadOnlyList<BodyRange> ranges,
		IReadOnlyDictionary<string, string> bodyIdToBoneName,
		Skeleton skeleton,
		BindMethod method,
		out BindReport report,
		AutoWeightOptions options = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		RequireBones( skeleton );

		ranges ??= Array.Empty<BodyRange>();
		bodyIdToBoneName ??= new Dictionary<string, string>();

		if ( method == BindMethod.Bodies )
		{
			report = new BindReport { Requested = method, Used = method, OtherVertices = mesh.VertexCount };
			return SmoothWeights( mesh, BindBodies( mesh, ranges, bodyIdToBoneName, skeleton ) );
		}

		SkinWeights weights;

		switch ( method )
		{
			case BindMethod.Heat:
				weights = AutoWeights.Heat( mesh, skeleton, out report, options, ranges );
				break;

			case BindMethod.Voxel:
				weights = AutoWeights.Voxel( mesh, skeleton, out report, options );
				break;

			case BindMethod.Distance:
				weights = BindSmooth( mesh, skeleton );
				report = new BindReport { Requested = method, Used = method, OtherVertices = mesh.VertexCount };
				var max = (options ?? new AutoWeightOptions()).MaxInfluences;

				for ( var v = 0; v < weights.Count; v++ )
					weights[v] = SkinWeights.Prune( weights[v], max );

				break;

			default:
				throw new ArgumentOutOfRangeException( nameof( method ), $"Unknown bind method {method}" );
		}

		var pinned = 0;

		foreach ( var range in ranges )
		{
			if ( bodyIdToBoneName.TryGetValue( range.BodyId, out var boneName ) )
			{
				var bone = skeleton.IndexOf( boneName );

				if ( bone < 0 )
					throw new InvalidOperationException(
						$"Body '{range.BodyName}' is bound to bone '{boneName}', which is not in the skeleton. "
						+ "Re-assign the body in the Rig workspace, or clear its assignment." );

				var rigid = new[] { new BoneWeight( bone, 1f ) };

				for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
					weights[i] = rigid;

				pinned++;
				continue;
			}

			if ( range.HasOwnWeights && mesh.IsRigged )
			{
				for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
					if ( mesh.Skin[i] is { Length: > 0 } own )
						weights[i] = own;
			}
		}

		if ( pinned > 0 )
			report.Notes.Add( $"{pinned} assigned bod{(pinned == 1 ? "y" : "ies")} kept rigid to their bone" );

		return weights;
	}

	/// <summary>The bind method's name as scripts and the document spell it: bodies, heat, voxel, distance.</summary>
	public static string MethodName( BindMethod method ) => method.ToString().ToLowerInvariant();

	/// <summary>Parse <see cref="MethodName"/>'s spelling (case-insensitive). False for anything else.</summary>
	public static bool TryParseMethod( string text, out BindMethod method )
	{
		method = BindMethod.Bodies;

		switch ( (text ?? "").Trim().ToLowerInvariant() )
		{
			case "bodies": case "body": case "parts": method = BindMethod.Bodies; return true;
			case "heat": case "auto": method = BindMethod.Heat; return true;
			case "voxel": case "geodesic": method = BindMethod.Voxel; return true;
			case "distance": case "envelope": method = BindMethod.Distance; return true;
			default: return false;
		}
	}

	// --- biharmonic helpers ----------------------------------------------------------------

	/// <summary>AutoWeights.Prepare, reimplemented inside this file's boundary: helper bones are
	/// skipped unless asked for, a body's span wins over head/tail, and an all-helper skeleton
	/// weights every bone rather than none. AutoWeights.Prepare itself is private to that class.</summary>
	static List<(int Index, Vec3 Head, Vec3 Tail)> BiharmonicBones( Skeleton skeleton, AutoWeightOptions options, BindReport report )
	{
		var list = new List<(int, Vec3, Vec3)>();
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

			list.Add( (b, head, tail) );
		}

		if ( list.Count == 0 )
		{
			for ( var b = 0; b < skeleton.Count; b++ )
				list.Add( (b, skeleton.HeadWorld( b ), skeleton.TailWorld( b ) ) );

			report.Notes.Add( "every bone is named like a helper (twist/IK/face…), so all of them were weighted" );
		}
		else if ( skipped.Count > 0 )
		{
			report.Notes.Add( $"{skipped.Count} helper bone{(skipped.Count == 1 ? "" : "s")} skipped" );
		}

		return list;
	}

	/// <summary>
	/// Solve (L·M⁻¹·L) x = 0 on the free vertices with the pinned vertices held at
	/// <paramref name="pinnedValues"/>, by preconditioned conjugate gradients on the free-free
	/// block. The bilaplacian is never assembled: each product is L·(M⁻¹·(L·x)) through the
	/// existing sparse matvec, and the Jacobi preconditioner is diag(L·M⁻¹·L), gathered from
	/// L's entries in one pass. False when the iteration does not converge (a singular block,
	/// which the component filter in the caller should already have ruled out, or a genuinely
	/// ill-conditioned mesh) — the caller falls back to heat rather than shipping the result.
	/// </summary>
	static bool TrySolveBilaplacian(
		SparseMatrix lap,
		double[] mass,
		List<int> free,
		bool[] pinned,
		double[] pinnedValues,
		out double[] full )
	{
		var n = lap.Rows;
		full = new double[n];

		for ( var i = 0; i < n; i++ )
			if ( pinned[i] )
				full[i] = pinnedValues[i];

		if ( free.Count == 0 )
			return true;

		double[] KApply( double[] x )
		{
			var t = lap.Multiply( x );

			for ( var i = 0; i < n; i++ )
				t[i] = mass[i] > 0 ? t[i] / mass[i] : 0.0;

			return lap.Multiply( t );
		}

		double[] Embed( double[] freeVec )
		{
			var embedded = new double[n];

			for ( var k = 0; k < free.Count; k++ )
				embedded[free[k]] = freeVec[k];

			return embedded;
		}

		double[] Restrict( double[] fullVec )
		{
			var restricted = new double[free.Count];

			for ( var k = 0; k < free.Count; k++ )
				restricted[k] = fullVec[free[k]];

			return restricted;
		}

		static double Dot( double[] a, double[] b )
		{
			var s = 0.0;

			for ( var i = 0; i < a.Length; i++ )
				s += a[i] * b[i];

			return s;
		}

		// Jacobi preconditioner: diag(K)_c = sum over rows r of L[r,c]^2 / mass[r].
		var invDiag = new double[n];

		for ( var i = 0; i < n; i++ )
			invDiag[i] = 1.0;

		var diag = new double[n];

		for ( var row = 0; row < n; row++ )
		{
			if ( mass[row] <= 0 )
				continue;

			for ( var pi = lap.RowStart[row]; pi < lap.RowStart[row + 1]; pi++ )
			{
				var c = lap.Col[pi];
				var v = lap.Val[pi];
				diag[c] += v * v / mass[row];
			}
		}

		for ( var i = 0; i < n; i++ )
			if ( diag[i] > 0 )
				invDiag[i] = 1.0 / diag[i];

		var kPinned = KApply( full );
		var rhs = new double[free.Count];

		for ( var k = 0; k < free.Count; k++ )
			rhs[k] = -kPinned[free[k]];

		var rhsNorm = Math.Sqrt( Dot( rhs, rhs ) );
		var tolerance = 1e-8 * Math.Max( 1.0, rhsNorm );
		var x = new double[free.Count];
		var r = (double[])rhs.Clone();
		var z = new double[free.Count];

		for ( var k = 0; k < free.Count; k++ )
			z[k] = r[k] * invDiag[free[k]];

		var p = (double[])z.Clone();
		var rsold = Dot( r, z );

		for ( var iter = 0; iter < 2000; iter++ )
		{
			var ap = Restrict( KApply( Embed( p ) ) );
			var pAp = Dot( p, ap );

			if ( !(pAp > 0.0) || double.IsNaN( pAp ) || double.IsInfinity( pAp ) )
				return false;

			var alpha = rsold / pAp;

			for ( var k = 0; k < free.Count; k++ )
			{
				x[k] += alpha * p[k];
				r[k] -= alpha * ap[k];
			}

			if ( Math.Sqrt( Dot( r, r ) ) <= tolerance )
			{
				for ( var k = 0; k < free.Count; k++ )
					full[free[k]] = x[k];

				return true;
			}

			for ( var k = 0; k < free.Count; k++ )
				z[k] = r[k] * invDiag[free[k]];

			var rsnew = Dot( r, z );

			if ( !(rsnew > 0.0) || double.IsNaN( rsnew ) )
				return false;

			var beta = rsnew / rsold;

			for ( var k = 0; k < free.Count; k++ )
				p[k] = z[k] + beta * p[k];

			rsold = rsnew;
		}

		return false;
	}
}
