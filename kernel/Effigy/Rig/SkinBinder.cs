using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Which body a run of vertices in a merged mesh came from. See PartStudio.ToMeshWithBodies.</summary>
public readonly struct BodyRange
{
	public readonly string BodyId;
	public readonly string BodyName;
	public readonly int Start;
	public readonly int Count;

	/// <summary>The body arrived with skin weights of its own — an imported rigged mesh, or a garment
	/// that copied the weights of the body under it. Binding keeps them unless the body is pinned
	/// to a bone.</summary>
	public readonly bool HasOwnWeights;

	public BodyRange( string bodyId, string bodyName, int start, int count, bool hasOwnWeights = false )
	{
		BodyId = bodyId;
		BodyName = bodyName;
		Start = start;
		Count = count;
		HasOwnWeights = hasOwnWeights;
	}

	public bool Contains( int vertex ) => vertex >= Start && vertex < Start + Count;

	public override string ToString() => $"{BodyName} [{Start}..{Start + Count})";
}

/// <summary>
/// <summary>How a per-vertex weight set is rescaled. See <see cref="SkinBinder.Normalize"/>.</summary>
public enum SkinNormalizeMode
{
	/// <summary>Rescale so the weights sum to 1 — the partition of unity every other step assumes.
	/// This is what <see cref="SkinWeights.Blend"/> and the binders already produce, so it is the
	/// default and re-applying it changes nothing.</summary>
	Sum,

	/// <summary>Rescale so the strongest influence is exactly 1, keeping the ratios between bones.
	/// The set then sums to more than 1: it is NOT a partition of unity and <c>Validate</c> will
	/// flag it. It is for overlay/additive layers, where the strongest bone must drive at full
	/// strength rather than being diluted by how many bones share the vertex.</summary>
	Strongest,

	/// <summary>No rescaling at all — tidy copy only (duplicates merged, non-positive and
	/// relatively negligible influences dropped, strongest first). For preview and diagnostics:
	/// what the raw influences actually are before any normalisation hides them. Also not a
	/// partition of unity in general.</summary>
	None,
}

/// <summary>What happens to a vertex that ends up with no weights at all. A vertex with no
/// weights does not move in <see cref="SkinBinder.Deform"/> — it stays on its bind position — so
/// an unweighted vertex is a hole in the rig, not a crash, and these modes are the authoring-time
/// answers for closing it.</summary>
public enum UnweightedMode
{
	/// <summary>Leave empty vertices empty. The historical behaviour everywhere: <c>Deform</c>
	/// keeps them on bind, <c>TransferFrom</c> keeps whatever the target already had.</summary>
	Keep,

	/// <summary>Assign the nearest bone segment at full weight. Needs a skeleton; without one this
	/// throws rather than guessing. Produces a complete rig in one pass, at the cost of rigid
	/// seams where the fill meets painted weights — follow with <c>SmoothWeights</c> if that
	/// shows.</summary>
	NearestBone,

	/// <summary>Fill from mesh neighbours: an empty vertex takes the blend of its non-empty
	/// neighbours, repeated until no more fill. Respects the surface the way smoothing does —
	/// weight flows around a limb rather than through space — but an island of empty vertices
	/// with no weighted neighbour stays empty. Needs no skeleton.</summary>
	NeighbourAverage,
}

/// <summary>Options for <see cref="SkinBinder.TransferFrom(PolyMesh, PolyMesh, TransferOptions, Skeleton)"/>.
/// Every field defaults to the historical behaviour, so the options overload with a default
/// <see cref="TransferOptions"/> is the old call exactly.</summary>
public sealed class TransferOptions
{
	/// <summary>Vertices further than this from the source keep what they had (or nothing).
	/// Default is unbounded, as before.</summary>
	public float MaxDistance = float.MaxValue;

	/// <summary>Exponent on the inverse-distance corner blend: a corner at distance d contributes
	/// 1/d^<c>Falloff</c>. 1 is the historical blend; higher values sharpen toward the nearest
	/// corner, lower values soften across the face. Must be positive.</summary>
	public float Falloff = 1f;

	/// <summary>How much of the transferred weights replaces what the target already had: 1 is a
	/// full replace (historical), 0 keeps the existing weights entirely, in-between blends the
	/// two by this factor. Clamped to [0, 1].</summary>
	public float Mix = 1f;

	/// <summary>What happens to vertices the surface pass did not reach (beyond
	/// <see cref="MaxDistance"/>). <see cref="UnweightedMode.Keep"/> is historical.
	/// <see cref="UnweightedMode.NearestBone"/> needs the skeleton argument to
	/// <c>TransferFrom</c>; without it, it throws rather than guessing.</summary>
	public UnweightedMode Unweighted = UnweightedMode.Keep;
}

/// Generating skin weights, so nobody has to paint them.
///
/// TWO FAMILIES. This file holds the cheap ones: distance to the bone SEGMENT (BindRigid,
/// BindSmooth) and smoothing over mesh adjacency (SmoothWeights), which need no solver and are
/// right for hard-surface props, but let two limbs that are close in space bleed into each other.
/// The shape-aware ones — bone heat over the cotangent Laplacian and geodesic voxel binding — are
/// in <see cref="AutoWeights"/>, reached through <see cref="Bind"/> with a <see cref="BindMethod"/>.
///
/// THE INTERESTING ONE IS BindBodies. Because the model is parametric, a bone can own a FEATURE
/// rather than a set of vertices — "this cylinder is the forearm". Vertex indices die on every
/// rebuild; a body id does not. So the binding survives a parameter change, and the weights are
/// regenerated rather than invalidated. Blender cannot do this, because it has no history to bind
/// against.
/// </summary>
public static partial class SkinBinder
{
	/// <summary>Each vertex fully weighted to whichever bone segment is nearest. Blocky across
	/// joints, and exactly right for a rigid mechanical part like a hinge or a piston.</summary>
	public static SkinWeights BindRigid( PolyMesh mesh, Skeleton skeleton )
	{
		RequireBones( skeleton );

		var weights = new SkinWeights();
		var segments = Segments( skeleton );

		foreach ( var p in mesh.Positions )
		{
			var best = 0;
			var bestDist = float.MaxValue;

			for ( var b = 0; b < segments.Length; b++ )
			{
				var d = DistanceToSegment( p, segments[b].Head, segments[b].Tail );

				if ( d < bestDist )
				{
					bestDist = d;
					best = b;
				}
			}

			weights.Vertices.Add( new[] { new BoneWeight( best, 1f ) } );
		}

		return weights;
	}

	/// <summary>
	/// Inverse-distance weighting against every bone segment, so joints bend smoothly.
	///
	/// `falloff` is the exponent: 1 is very soft and reaches across the whole model, 4 is nearly
	/// rigid. 2 is a reasonable default and matches what most auto-skin tools land on.
	/// Contributions below `cutoff` of the strongest are dropped before normalising, which is what
	/// stops a bone on the far side of the model owning half a percent of every vertex.
	/// </summary>
	public static SkinWeights BindSmooth( PolyMesh mesh, Skeleton skeleton, float falloff = 2f, float cutoff = 0.02f )
	{
		RequireBones( skeleton );

		if ( falloff <= 0f )
			throw new ArgumentOutOfRangeException( nameof( falloff ), "Falloff must be positive" );

		var weights = new SkinWeights();
		var segments = Segments( skeleton );
		var distances = new float[segments.Length];

		foreach ( var p in mesh.Positions )
		{
			var nearest = float.MaxValue;

			for ( var b = 0; b < segments.Length; b++ )
			{
				distances[b] = DistanceToSegment( p, segments[b].Head, segments[b].Tail );
				nearest = MathF.Min( nearest, distances[b] );
			}

			// A vertex sitting exactly on a bone would divide by zero. Snapping it wholly to that
			// bone is both the limit of the formula and the answer anyone would expect.
			if ( nearest < 1e-6f )
			{
				var onBone = Array.IndexOf( distances, nearest );
				weights.Vertices.Add( new[] { new BoneWeight( onBone < 0 ? 0 : onBone, 1f ) } );
				continue;
			}

			var acc = new Dictionary<int, float>();
			var strongest = 0f;

			for ( var b = 0; b < segments.Length; b++ )
			{
				var influence = 1f / MathF.Pow( distances[b], falloff );
				acc[b] = influence;
				strongest = MathF.Max( strongest, influence );
			}

			var floor = strongest * cutoff;

			foreach ( var b in new List<int>( acc.Keys ) )
			{
				if ( acc[b] < floor )
					acc.Remove( b );
			}

			weights.Vertices.Add( SkinWeights.Finish( acc ) );
		}

		return weights;
	}

	/// <summary>
	/// <summary>
	/// <see cref="BindSmooth(PolyMesh, Skeleton, float, float)"/> with a normalisation mode.
	/// <see cref="SkinNormalizeMode.Sum"/> delegates to the old call and is identical to it.
	/// <see cref="SkinNormalizeMode.Strongest"/> rescales each vertex so its strongest bone is 1;
	/// <see cref="SkinNormalizeMode.None"/> returns the raw influences tidied (duplicates merged,
	/// non-positive and relatively negligible influences dropped, strongest first) without
	/// rescaling. Neither of the latter is a partition of unity — they are for overlay layers and
	/// diagnostics, not for export.
	/// </summary>
	public static SkinWeights BindSmooth( PolyMesh mesh, Skeleton skeleton, SkinNormalizeMode normalize, float falloff = 2f, float cutoff = 0.02f )
	{
		if ( normalize == SkinNormalizeMode.Sum )
			return BindSmooth( mesh, skeleton, falloff, cutoff );

		RequireBones( skeleton );

		if ( falloff <= 0f )
			throw new ArgumentOutOfRangeException( nameof( falloff ), "Falloff must be positive" );

		var weights = new SkinWeights();
		var segments = Segments( skeleton );
		var distances = new float[segments.Length];

		foreach ( var p in mesh.Positions )
		{
			var nearest = float.MaxValue;

			for ( var b = 0; b < segments.Length; b++ )
			{
				distances[b] = DistanceToSegment( p, segments[b].Head, segments[b].Tail );
				nearest = MathF.Min( nearest, distances[b] );
			}

			// Same on-bone snap as the old call: the limit of the formula, and the expected answer.
			if ( nearest < 1e-6f )
			{
				var onBone = Array.IndexOf( distances, nearest );
				weights.Vertices.Add( new[] { new BoneWeight( onBone < 0 ? 0 : onBone, 1f ) } );
				continue;
			}

			var acc = new Dictionary<int, float>();
			var strongest = 0f;

			for ( var b = 0; b < segments.Length; b++ )
			{
				var influence = 1f / MathF.Pow( distances[b], falloff );
				acc[b] = influence;
				strongest = MathF.Max( strongest, influence );
			}

			var floor = strongest * cutoff;

			foreach ( var b in new List<int>( acc.Keys ) )
			{
				if ( acc[b] < floor )
					acc.Remove( b );
			}

			weights.Vertices.Add( FinishMode( acc, normalize ) );
		}

		return weights;
	}

	/// Bind whole bodies to named bones — the parametric path.
	///
	/// Every vertex of a bound body gets that bone at full weight. Bodies with no mapping fall back
	/// to the nearest bone, so a partial mapping still produces a complete rig rather than a mesh
	/// with holes in its weighting.
	///
	/// Follow it with SmoothWeights to soften the seams between bodies; on its own this produces
	/// rigid parts, which is the correct result for machinery and the wrong one for a limb.
	/// </summary>
	public static SkinWeights BindBodies(
		PolyMesh mesh,
		IReadOnlyList<BodyRange> ranges,
		IReadOnlyDictionary<string, string> bodyIdToBoneName,
		Skeleton skeleton )
	{
		RequireBones( skeleton );

		if ( ranges is null ) throw new ArgumentNullException( nameof( ranges ) );
		if ( bodyIdToBoneName is null ) throw new ArgumentNullException( nameof( bodyIdToBoneName ) );

		var fallback = BindRigid( mesh, skeleton );
		var weights = new SkinWeights( mesh.VertexCount );

		for ( var i = 0; i < mesh.VertexCount; i++ )
			weights[i] = fallback[i];

		foreach ( var range in ranges )
		{
			if ( !bodyIdToBoneName.TryGetValue( range.BodyId, out var boneName ) )
			{
				// Not pinned, but carrying weights of its own: those win over nearest-bone. A
				// garment that copied the body's weights must not be flattened back to rigid here.
				if ( range.HasOwnWeights && mesh.IsRigged )
				{
					for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
						if ( mesh.Skin[i] is { Length: > 0 } own )
							weights[i] = own;
				}

				continue;
			}

			var bone = skeleton.IndexOf( boneName );

			if ( bone < 0 )
				throw new InvalidOperationException(
					$"Body '{range.BodyName}' is bound to bone '{boneName}', which is not in the skeleton" );

			var bound = new[] { new BoneWeight( bone, 1f ) };

			for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
				weights[i] = bound;
		}

		return weights;
	}

	/// <summary>
	/// Average each vertex's weights with its mesh neighbours, a few times.
	///
	/// This is what turns a rigid body-bound rig into one that bends: it diffuses weight ACROSS THE
	/// SURFACE rather than through space, so it respects the mesh the way plain distance weighting
	/// does not. It is a poor man's heat diffusion — same idea, no solver, run to a fixed iteration
	/// count instead of to convergence.
	///
	/// `strength` is how far each pass moves toward the neighbour average; 1 is a full replace.
	/// Each pass is an affine combination, so the partition of unity survives any number of them.
	/// </summary>
	public static SkinWeights SmoothWeights( PolyMesh mesh, SkinWeights weights, int iterations = 2, float strength = 0.5f )
	{
		if ( iterations <= 0 )
			return weights;

		strength = Math.Clamp( strength, 0f, 1f );

		var neighbours = mesh.BuildVertexEdges();
		var current = weights;

		for ( var pass = 0; pass < iterations; pass++ )
		{
			var next = new SkinWeights( mesh.VertexCount );

			for ( var v = 0; v < mesh.VertexCount; v++ )
			{
				var edges = neighbours[v];

				if ( edges.Count == 0 )
				{
					next[v] = current[v];
					continue;
				}

				var terms = new List<(BoneWeight[], float)>( edges.Count + 1 )
				{
					(current[v], 1f - strength)
				};

				var share = strength / edges.Count;

				foreach ( var e in edges )
					terms.Add( (current[e.A == v ? e.B : e.A], share) );

				next[v] = SkinWeights.Blend( terms );
			}

			current = next;
		}

		return current;
	}

	/// <summary>
	/// Linear blend skinning: deform bind positions by a posed skeleton.
	///
	/// THE FIFTEEN LINES THE POSE PREVIEW RUNS ON. For each vertex, blend its bind position through
	/// every influencing bone's skin matrix — <c>pose * bind⁻¹</c>, the transform that carries the
	/// bind pose to the current pose — and sum by weight. At a zero pose (pose == bind) every skin
	/// matrix is identity and the mesh is unchanged, which is the property a "reset pose" leans on.
	///
	/// Positions come back as a fresh array parallel to <paramref name="positions"/>; the caller
	/// owns putting them on a display copy. The cage is never touched — the CAD history owns the
	/// bind mesh, and a pose must not be able to write its way back into it.
	/// </summary>
	public static Vec3[] Deform( IReadOnlyList<Vec3> positions, SkinWeights weights,
		IReadOnlyList<Xform> bindWorld, IReadOnlyList<Xform> poseWorld )
	{
		if ( positions is null ) throw new ArgumentNullException( nameof( positions ) );
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( bindWorld is null ) throw new ArgumentNullException( nameof( bindWorld ) );
		if ( poseWorld is null ) throw new ArgumentNullException( nameof( poseWorld ) );

		// One skin matrix per bone, computed once rather than per vertex. A bone that has not moved
		// still pays for an identity here, which is cheap and keeps the loop branch-free.
		var skin = new Xform[bindWorld.Count];

		for ( var b = 0; b < skin.Length; b++ )
			skin[b] = poseWorld[b] * bindWorld[b].Inverse;

		var result = new Vec3[positions.Count];

		for ( var v = 0; v < positions.Count; v++ )
		{
			var p = positions[v];
			var acc = Vec3.Zero;

			foreach ( var bw in weights[v] )
				acc += skin[bw.Bone].TransformPoint( p ) * bw.Weight;

			// An unweighted vertex has no bone to move it; leaving it on its bind position is the
			// honest answer, and a world better than collapsing it onto the origin.
			result[v] = weights[v].Length == 0 ? p : acc;
		}

		return result;
	}

	/// <summary>
	/// The bone whose segment passes closest to a point.
	///
	/// This is <see cref="BindRigid"/>'s inner loop, lifted out because collision needs the same
	/// answer for a whole body that weighting needs for a vertex: an unassigned body has to end up
	/// on SOME bone, and the nearest one is the one its vertices were already going to be weighted
	/// to. Leaving it on the root instead would be the original bug in miniature — a mesh that bends
	/// away from collision that does not.
	/// </summary>
	public static int NearestBone( Vec3 point, Skeleton skeleton )
	{
		RequireBones( skeleton );

		var segments = Segments( skeleton );
		var best = 0;
		var bestDist = float.MaxValue;

		for ( var b = 0; b < segments.Length; b++ )
		{
			var d = DistanceToSegment( point, segments[b].Head, segments[b].Tail );

			if ( d >= bestDist )
				continue;

			bestDist = d;
			best = b;
		}

		return best;
	}

	/// <summary>
	/// Rescale every vertex's weights by <paramref name="mode"/>, returning a NEW set — the input
	/// is never mutated. Duplicates are merged by summing, non-positive and relatively negligible
	/// influences are dropped, and each set comes back strongest first, matching the order
	/// <c>Prune</c> and the binders guarantee (SmdWriter reads weights[0] as the parent bone).
	/// Empty sets stay empty in every mode: there is nothing to rescale, and inventing a bone
	/// would be worse than the hole.
	/// </summary>
	public static SkinWeights Normalize( SkinWeights weights, SkinNormalizeMode mode = SkinNormalizeMode.Sum )
	{
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );

		var result = new SkinWeights();

		foreach ( var w in weights.Vertices )
		{
			var acc = new Dictionary<int, float>();

			if ( w is not null )
			{
				foreach ( var bw in w )
				{
					if ( bw.Weight <= 0f ) continue;
					acc.TryGetValue( bw.Bone, out var existing );
					acc[bw.Bone] = existing + bw.Weight;
				}
			}

			result.Vertices.Add( FinishMode( acc, mode ) );
		}

		return result;
	}

	/// <summary>
	/// Close the holes in a weight set: every vertex with no influences gets one, by
	/// <paramref name="mode"/>. Returns the filled set (a NEW set — the input is never mutated)
	/// and how many vertices were filled. <see cref="UnweightedMode.Keep"/> returns a clone and 0.
	/// <see cref="UnweightedMode.NearestBone"/> needs <paramref name="skeleton"/>; the other modes
	/// accept a null skeleton. Vertices <c>NeighbourAverage</c> cannot reach — an island of empty
	/// vertices with no weighted neighbour — stay empty; chain it with <c>NearestBone</c> if a
	/// complete rig matters more than surface flow.
	/// </summary>
	public static (SkinWeights Weights, int Filled) FillUnweighted( PolyMesh mesh, SkinWeights weights, Skeleton skeleton, UnweightedMode mode = UnweightedMode.NearestBone )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"{weights.Count} weight sets for {mesh.VertexCount} vertices", nameof( weights ) );

		if ( mode == UnweightedMode.Keep )
			return (weights.Clone(), 0);

		if ( mode == UnweightedMode.NearestBone )
		{
			RequireBones( skeleton );

			var filled = new SkinWeights();
			var count = 0;

			for ( var v = 0; v < mesh.VertexCount; v++ )
			{
				var w = weights[v];

				if ( w is { Length: > 0 } )
				{
					filled.Vertices.Add( (BoneWeight[])w.Clone() );
					continue;
				}

				filled.Vertices.Add( new[] { new BoneWeight( NearestBone( mesh.Positions[v], skeleton ), 1f ) } );
				count++;
			}

			return (filled, count);
		}

		if ( mode != UnweightedMode.NeighbourAverage )
			throw new ArgumentOutOfRangeException( nameof( mode ), $"Unknown unweighted mode {mode}" );

		var neighbours = mesh.BuildVertexEdges();
		var current = weights.Clone();
		var total = 0;

		// Snapshot semantics per pass: fills are computed from the state at the pass start, so a
		// line of empty vertices fills from both ends inward one step per pass rather than in
		// whatever order the loop happens to visit them. Passes stop when one fills nothing.
		while ( true )
		{
			var next = current.Clone();
			var filledThisPass = 0;

			for ( var v = 0; v < mesh.VertexCount; v++ )
			{
				if ( current[v] is { Length: > 0 } )
					continue;

				var terms = new List<(BoneWeight[] Weights, float Coefficient)>();

				foreach ( var e in neighbours[v] )
				{
					var n = e.A == v ? e.B : e.A;

					if ( current[n] is { Length: > 0 } own )
						terms.Add( (own, 1f) );
				}

				if ( terms.Count == 0 )
					continue;

				next[v] = SkinWeights.Blend( terms );
				filledThisPass++;
			}

			current = next;
			total += filledThisPass;

			if ( filledThisPass == 0 )
				break;
		}

		return (current, total);
	}

	/// <summary>Same tidy rule as <see cref="SkinWeights.Finish"/> — drop non-positive and
	/// relatively negligible influences, strongest first — then rescale by mode. <c>Sum</c> is
	/// exactly <c>Finish</c>'s rescale; the other modes only change the divisor. Kept separate so
	/// <c>BindSmooth</c>'s non-default modes and <c>Normalize</c> share one definition.</summary>
	static BoneWeight[] FinishMode( Dictionary<int, float> acc, SkinNormalizeMode mode )
	{
		var total = 0f;

		foreach ( var v in acc.Values )
		{
			if ( v > 0f )
				total += v;
		}

		if ( total <= 0f )
			return Array.Empty<BoneWeight>();

		var floor = total * 1e-6f;
		var kept = new List<(int Bone, float Weight)>();
		var strongest = 0f;
		var sum = 0f;

		foreach ( var (bone, v) in acc )
		{
			if ( v > floor )
			{
				kept.Add( (bone, v) );
				strongest = MathF.Max( strongest, v );
				sum += v;
			}
		}

		if ( kept.Count == 0 )
			return Array.Empty<BoneWeight>();

		var divisor = mode switch
		{
			SkinNormalizeMode.Sum => sum,
			SkinNormalizeMode.Strongest => strongest,
			_ => 1f,
		};

		kept.Sort( ( a, b ) => b.Weight.CompareTo( a.Weight ) );

		var result = new BoneWeight[kept.Count];

		for ( var i = 0; i < kept.Count; i++ )
			result[i] = new BoneWeight( kept[i].Bone, kept[i].Weight / divisor );

		return result;
	}

	// --- helpers -----------------------------------------------------------------------------

	static void RequireBones( Skeleton skeleton )
	{
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		if ( skeleton.Count == 0 )
			throw new InvalidOperationException( "Cannot bind to a skeleton with no bones" );
	}

	static (Vec3 Head, Vec3 Tail)[] Segments( Skeleton skeleton )
	{
		var segments = new (Vec3, Vec3)[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
			segments[i] = (skeleton.HeadWorld( i ), skeleton.TailWorld( i ));

		return segments;
	}

	/// <summary>Distance from a point to a line segment — not to the infinite line, which would
	/// let a short bone influence vertices far off its ends.</summary>
	public static float DistanceToSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var lengthSquared = ab.LengthSquared;

		if ( lengthSquared < 1e-12f )
			return (p - a).Length;

		var t = Math.Clamp( Vec3.Dot( p - a, ab ) / lengthSquared, 0f, 1f );
		return (p - (a + ab * t)).Length;
	}

	/// <summary>
	/// Give <paramref name="target"/> the weights of the nearest point on <paramref name="source"/>'s
	/// surface — how a garment learns to move with the body it was fitted to.
	///
	/// NEAREST SURFACE, NOT NEAREST VERTEX. A shirt vertex over the middle of a large chest face is
	/// nearest to whichever corner happens to be closest, and snapping to it gives stripes of
	/// rigid weights across the fabric. The point on the face is blended from all its corners by
	/// inverse distance, which is smooth across the face and exact at a corner.
	///
	/// Vertices further than <paramref name="maxDistance"/> from the source keep what they had (or
	/// nothing). Returns how many were reached.
	/// </summary>
	public static int TransferFrom( PolyMesh target, PolyMesh source, float maxDistance = float.MaxValue ) =>
		TransferFrom( target, source, new TransferOptions { MaxDistance = maxDistance } );

	/// <summary>
	/// <see cref="TransferFrom(PolyMesh, PolyMesh, float)"/> with options. Defaults reproduce the
	/// old call exactly: <c>Falloff</c> 1 uses the historical 1/d blend bit-for-bit, <c>Mix</c> 1
	/// takes the transferred weights verbatim, and <c>Unweighted</c> <c>Keep</c> leaves unreached
	/// vertices alone. <c>Falloff</c> sharpens or softens the corner blend; <c>Mix</c> below 1
	/// blends the transfer into what the target already had instead of replacing it — for a
	/// garment that already carries paint worth keeping. <c>Unweighted</c> closes the holes the
	/// surface pass leaves: <c>NeighbourAverage</c> fills from reached neighbours along the
	/// surface, <c>NearestBone</c> pins the rest to the nearest bone but needs
	/// <paramref name="skeleton"/> — without it, it throws rather than guessing. The return is
	/// still how many vertices the surface pass reached; filled vertices are not counted there.
	/// </summary>
	public static int TransferFrom( PolyMesh target, PolyMesh source, TransferOptions options, Skeleton skeleton = null )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		if ( source is null || !source.IsRigged )
			throw new InvalidOperationException( "The body to copy weights from has no skin weights. Rig it first." );
		if ( options is null )
			throw new ArgumentNullException( nameof( options ) );
		if ( float.IsNaN( options.Falloff ) || float.IsInfinity( options.Falloff ) || options.Falloff <= 0f )
			throw new ArgumentOutOfRangeException( nameof( options ), "Transfer falloff must be positive" );
		if ( float.IsNaN( options.Mix ) || float.IsInfinity( options.Mix ) )
			throw new ArgumentOutOfRangeException( nameof( options ), "Transfer mix must be finite" );

		var mix = Math.Clamp( options.Mix, 0f, 1f );

		if ( options.Unweighted == UnweightedMode.NearestBone && skeleton is null )
			throw new InvalidOperationException( "Transfer with NearestBone unweighted handling needs a skeleton" );

		var tree = MeshBVH.Build( source );
		var weights = target.IsRigged ? target.Skin.Clone() : new SkinWeights( target.VertexCount );
		var reached = 0;

		for ( var v = 0; v < target.VertexCount; v++ )
		{
			if ( tree.NearestSurface( source, target.Positions[v], options.MaxDistance ) is not { } hit )
				continue;

			if ( mix <= 0f )
			{
				// Mix 0 keeps the existing weights bit-for-bit — not even tidied by a blend.
				reached++;
				continue;
			}

			var face = source.Faces[hit.FaceIndex];
			var terms = new List<(BoneWeight[] Weights, float Coefficient)>();
			var total = 0f;

			foreach ( var corner in face.Indices )
			{
				var d = (source.Positions[corner] - hit.Point).Length;

				// On a corner: that corner's weights, exactly.
				if ( d < 1e-6f )
				{
					terms.Clear();
					terms.Add( (source.Skin[corner], 1f) );
					total = 1f;
					break;
				}

				// Falloff 1 uses the historical 1/d bit-for-bit; other exponents go through Pow.
				var k = options.Falloff == 1f ? 1f / d : 1f / MathF.Pow( d, options.Falloff );
				terms.Add( (source.Skin[corner], k) );
				total += k;
			}

			for ( var i = 0; i < terms.Count; i++ )
				terms[i] = (terms[i].Weights, terms[i].Coefficient / total);

			var transferred = SkinWeights.Blend( terms );

			weights[v] = mix >= 1f
				? transferred
				: SkinWeights.Blend( (weights[v], 1f - mix), (transferred, mix) );
			reached++;
		}

		if ( options.Unweighted != UnweightedMode.Keep )
			weights = FillUnweighted( target, weights, skeleton, options.Unweighted ).Weights;

		target.Skin = weights;
		return reached;
	}

}
