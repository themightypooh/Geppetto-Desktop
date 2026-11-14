using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace Effigy;

/// <summary>
/// Production weight-paint operations on a whole <see cref="SkinWeights"/>: the batch
/// tools the brush strokes sit on top of — normalize, Limit Total, prune, blur/sharpen,
/// gradient and mirror — with locked-bone and per-bone-isolate support throughout.
///
/// Everything here is engine-free kernel code, so a test can run it without a mesh
/// editor, a UI, or a history stack. The per-stroke equivalents live in
/// <see cref="WeightBrush"/> (Add/Subtract/Set/Smooth/Gradient kinds) and in the core
/// <c>WeightPaintService</c> (Fill/Gradient/Normalize with its own lock-aware
/// <c>SetWeight</c>/<c>NormalizeWeights</c>); this file is the batch, whole-mesh side —
/// the "fix the rig" pass that runs before export, not the stroke that paints it.
///
/// THE INVARIANT, everywhere: every vertex's influences are non-negative and sum to 1,
/// and a locked bone keeps its exact weight. <see cref="SetBone"/> is the one function
/// that rule lives in for a single bone; <see cref="Tidy"/> is the one it lives in for
/// a whole set. A vertex that cannot be fixed without breaking the rule — only locked
/// influences, or locked weights already summing past 1 — is left alone and counted as
/// skipped, never silently rewritten. That is the same posture as
/// <see cref="WeightBrush.Retarget"/> and the service's <c>SetWeight</c>.
///
/// ISOLATE: <c>isolateBone</c> ≥ 0 restricts an operation to vertices that carry weight
/// on that bone. It is a vertex filter, not a channel filter — the operation still
/// renormalises the whole vertex, because a vertex whose other bones were left
/// untouched would come out unnormalized.
/// </summary>
public static class WeightPaintTools
{
	const float Epsilon = 1e-6f;

	/// <summary>The weight of one bone on a vertex, or 0 when it is not there.</summary>
	public static float WeightOf( BoneWeight[] weights, int bone )
	{
		if ( weights is null ) return 0f;
		var total = 0f;
		foreach ( var w in weights )
			if ( w.Bone == bone ) total += w.Weight;
		return total;
	}

	/// <summary>Merge duplicate bone entries by summing, dropping non-finite and
	/// non-positive weights. A tidy set has one entry per bone.</summary>
	static Dictionary<int, float> Merge( BoneWeight[] weights )
	{
		var acc = new Dictionary<int, float>();
		if ( weights is null ) return acc;
		foreach ( var w in weights )
		{
			if ( !float.IsFinite( w.Weight ) || w.Weight <= 0f ) continue;
			acc[w.Bone] = acc.TryGetValue( w.Bone, out var e ) ? e + w.Weight : w.Weight;
		}
		return acc;
	}

	/// <summary>Finish a merged set: locked bones keep their exact weight, the unlocked
	/// ones are rescaled to fill 1 − locked, strongest first. Null when it cannot be
	/// done — no unlocked weight to rescale, or locked weights already past 1.</summary>
	static BoneWeight[] Tidy( Dictionary<int, float> acc, IReadOnlySet<int>? locked )
	{
		float lockedSum = 0f, others = 0f;
		foreach ( var (b, w) in acc )
		{
			if ( locked is not null && locked.Contains( b ) ) lockedSum += w;
			else others += w;
		}
		if ( lockedSum > 1f + 1e-6f ) return null;
		if ( others <= Epsilon ) return null;

		var scale = (1f - lockedSum) / others;
		var result = new List<BoneWeight>( acc.Count );
		foreach ( var (b, w) in acc )
		{
			var isLocked = locked is not null && locked.Contains( b );
			var value = isLocked ? w : w * scale;
			if ( isLocked || value > Epsilon ) result.Add( new BoneWeight( b, value ) );
		}
		if ( result.Count == 0 ) return null;
		result.Sort( ( x, y ) => y.Weight != x.Weight ? y.Weight.CompareTo( x.Weight ) : x.Bone.CompareTo( y.Bone ) );
		return result.ToArray();
	}

	/// <summary>Whether two weight sets name the same bones at the same weights, within
	/// 1e-6 — order-insensitive, so a set that only got re-sorted does not count as
	/// changed.</summary>
	static bool Same( BoneWeight[] a, BoneWeight[] b )
	{
		if ( a is null || b is null ) return a is null && b is null;
		var x = Merge( a );
		var y = Merge( b );
		if ( x.Count != y.Count ) return false;
		foreach ( var (bone, weight) in x )
			if ( !y.TryGetValue( bone, out var other ) || MathF.Abs( other - weight ) > 1e-6f ) return false;
		return true;
	}

	static IEnumerable<int> TargetVertices( SkinWeights weights, IEnumerable<int>? vertices )
	{
		if ( vertices is not null ) return vertices;
		var all = new int[weights.Count];
		for ( var i = 0; i < all.Length; i++ ) all[i] = i;
		return all;
	}

	static void CheckVertex( SkinWeights weights, int v )
	{
		if ( v < 0 || v >= weights.Count )
			throw new ArgumentOutOfRangeException( nameof( v ), $"Vertex {v} is outside 0..{weights.Count - 1}." );
	}

	/// <summary>One bone moved to a target on one vertex, the kernel twin of the core
	/// service's <c>SetWeight</c> rule: locked bones keep their exact weight, the bone is
	/// clamped to [0, 1 − locked], the other unlocked bones are rescaled to fill the rest.
	/// The bone is REMOVED rather than left at 0 — a zero influence is an export slot
	/// spent on nothing. Null when it cannot be done: lowering a bone that has no unlocked
	/// partner to take the weight. An unweighted vertex painted above 0 becomes fully
	/// that bone, the service's rule — there is nothing else to rescale against.
	/// Throws when the bone itself is locked.</summary>
	public static BoneWeight[]? SetBone( BoneWeight[]? weights, int bone, float target, IReadOnlySet<int>? locked = null )
	{
		if ( bone < 0 ) throw new ArgumentOutOfRangeException( nameof( bone ) );
		if ( !float.IsFinite( target ) ) throw new ArgumentOutOfRangeException( nameof( target ) );
		if ( locked is not null && locked.Contains( bone ) ) throw new InvalidOperationException( $"Bone {bone} is locked." );

		var acc = Merge( weights ?? Array.Empty<BoneWeight>() );
		float lockedSum = 0f, others = 0f;
		foreach ( var (b, w) in acc )
		{
			if ( b == bone ) continue;
			if ( locked is not null && locked.Contains( b ) ) lockedSum += w;
			else others += w;
		}

		var available = Math.Max( 0f, 1f - lockedSum );
		target = Math.Clamp( target, 0f, available );

		if ( acc.Count == 0 )
			return target > Epsilon ? new[] { new BoneWeight( bone, 1f ) } : null;

		if ( others <= Epsilon )
		{
			if ( target < available - Epsilon ) return null;
			target = available;
		}

		var result = new List<BoneWeight>( acc.Count + 1 );
		var scale = others > Epsilon ? (available - target) / others : 0f;
		if ( target > Epsilon ) result.Add( new BoneWeight( bone, target ) );
		foreach ( var (b, w) in acc )
		{
			if ( b == bone ) continue;
			var isLocked = locked is not null && locked.Contains( b );
			var value = isLocked ? w : w * scale;
			if ( isLocked || value > Epsilon ) result.Add( new BoneWeight( b, value ) );
		}
		if ( result.Count == 0 ) return null;
		result.Sort( ( x, y ) => y.Weight != x.Weight ? y.Weight.CompareTo( x.Weight ) : x.Bone.CompareTo( y.Bone ) );
		return result.ToArray();
	}

	/// <summary>Rescale every vertex to sum to 1, locked bones untouched. Unweighted
	/// vertices stay unweighted — normalising nothing is not a fix — and a vertex whose
	/// only weight is locked is skipped, not rewritten. Returns how many vertices
	/// changed.</summary>
	public static int Normalize( SkinWeights weights, IReadOnlySet<int>? locked = null, int isolateBone = -1, IEnumerable<int>? vertices = null )
	{
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( isolateBone < -1 ) throw new ArgumentOutOfRangeException( nameof( isolateBone ) );

		var changed = 0;
		foreach ( var v in TargetVertices( weights, vertices ) )
		{
			CheckVertex( weights, v );
			var before = weights[v];
			if ( before is null || before.Length == 0 ) continue;
			if ( isolateBone >= 0 && WeightOf( before, isolateBone ) <= 0f ) continue;
			var after = Tidy( Merge( before ), locked );
			if ( after is null || Same( before, after ) ) continue;
			weights[v] = after;
			changed++;
		}
		return changed;
	}

	/// <summary>Drop to at most <paramref name="max"/> influences per vertex (4 for game
	/// export, 8 PC-only), keeping the strongest — locked bones are never dropped, even
	/// past the cap — and renormalise the survivors. A vertex whose locked bones alone
	/// are past the cap is left alone; <see cref="RigCheck"/> still reports it, because
	/// no automatic pass can fix a vertex the artist pinned past the limit. Returns how
	/// many vertices changed.</summary>
	public static int LimitTotal( SkinWeights weights, int max = 4, IReadOnlySet<int>? locked = null, int isolateBone = -1, IEnumerable<int>? vertices = null )
	{
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( max <= 0 ) throw new ArgumentOutOfRangeException( nameof( max ) );
		if ( isolateBone < -1 ) throw new ArgumentOutOfRangeException( nameof( isolateBone ) );

		var changed = 0;
		foreach ( var v in TargetVertices( weights, vertices ) )
		{
			CheckVertex( weights, v );
			var before = weights[v];
			if ( before is null || before.Length == 0 ) continue;
			if ( isolateBone >= 0 && WeightOf( before, isolateBone ) <= 0f ) continue;

			var acc = Merge( before );
			var lockedCount = acc.Count( kv => locked is not null && locked.Contains( kv.Key ) );
			var keepUnlocked = Math.Max( 0, max - lockedCount );
			var kept = acc
				.Where( kv => locked is not null && locked.Contains( kv.Key ) )
				.Concat( acc.Where( kv => locked is null || !locked.Contains( kv.Key ) ).OrderByDescending( kv => kv.Value ).Take( keepUnlocked ) )
				.ToDictionary( kv => kv.Key, kv => kv.Value );

			var after = Tidy( kept, locked );
			if ( after is null || Same( before, after ) ) continue;
			weights[v] = after;
			changed++;
		}
		return changed;
	}

	/// <summary>Drop influences below <paramref name="threshold"/> (0.01 for export) and
	/// renormalise the survivors — the dust that costs an export slot and nothing else.
	/// Locked bones are never dropped, whatever they weigh. A vertex that would come out
	/// empty keeps its strongest influence at 1.0 rather than becoming unweighted.
	/// Returns how many vertices changed.</summary>
	public static int PruneThreshold( SkinWeights weights, float threshold = 0.01f, IReadOnlySet<int>? locked = null, int isolateBone = -1, IEnumerable<int>? vertices = null )
	{
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( !(threshold > 0f) ) throw new ArgumentOutOfRangeException( nameof( threshold ), "Prune threshold must be positive." );
		if ( isolateBone < -1 ) throw new ArgumentOutOfRangeException( nameof( isolateBone ) );

		var changed = 0;
		foreach ( var v in TargetVertices( weights, vertices ) )
		{
			CheckVertex( weights, v );
			var before = weights[v];
			if ( before is null || before.Length == 0 ) continue;
			if ( isolateBone >= 0 && WeightOf( before, isolateBone ) <= 0f ) continue;

			var acc = Merge( before );
			var kept = acc
				.Where( kv => (locked is not null && locked.Contains( kv.Key )) || kv.Value >= threshold )
				.ToDictionary( kv => kv.Key, kv => kv.Value );

			if ( kept.Count == 0 )
			{
				var best = acc.OrderByDescending( kv => kv.Value ).First();
				var solo = new[] { new BoneWeight( best.Key, 1f ) };
				if ( Same( before, solo ) ) continue;
				weights[v] = solo;
				changed++;
				continue;
			}

			var after = Tidy( kept, locked );
			if ( after is null || Same( before, after ) ) continue;
			weights[v] = after;
			changed++;
		}
		return changed;
	}

	/// <summary>The neighbour average of a vertex's influences: the vertex itself plus
	/// every vertex sharing an edge with it, each counted once, merged by bone. A vertex
	/// with no neighbours averages to itself, so an isolated vertex never changes.</summary>
	static Dictionary<int, float> NeighbourAverage( BoneWeight[][] snapshot, List<EdgeKey>[] neighbours, int v )
	{
		var acc = new Dictionary<int, float>();
		var count = 1;

		void Add( BoneWeight[] set )
		{
			foreach ( var w in set )
			{
				if ( !float.IsFinite( w.Weight ) || w.Weight <= 0f ) continue;
				acc[w.Bone] = acc.TryGetValue( w.Bone, out var e ) ? e + w.Weight : w.Weight;
			}
		}

		Add( snapshot[v] );
		foreach ( var edge in neighbours[v] )
		{
			var other = edge.A == v ? edge.B : edge.A;
			if ( other < 0 || other >= snapshot.Length ) continue;
			Add( snapshot[other] );
			count++;
		}

		var keys = acc.Keys.ToArray();
		foreach ( var k in keys ) acc[k] /= count;
		return acc;
	}

	/// <summary>Blend one vertex toward (blur) or away from (sharpen) its neighbour
	/// average, then tidy. Locked bones are restored to their exact weight before the
	/// tidy, so a blur never moves a pinned bone.</summary>
	static BoneWeight[]? BlendTowardAverage( BoneWeight[] current, Dictionary<int, float> average, float strength, bool sharpen, IReadOnlySet<int>? locked )
	{
		var acc = new Dictionary<int, float>();
		var bones = new HashSet<int>();
		foreach ( var w in current ) bones.Add( w.Bone );
		foreach ( var b in average.Keys ) bones.Add( b );

		foreach ( var b in bones )
		{
			var cur = WeightOf( current, b );
			var avg = average.TryGetValue( b, out var a ) ? a : 0f;
			var value = sharpen ? cur + strength * (cur - avg) : cur + strength * (avg - cur);
			if ( value > 0f ) acc[b] = value;
		}

		if ( locked is not null )
			foreach ( var b in locked )
				if ( WeightOf( current, b ) is var kept && kept > 0f ) acc[b] = kept;

		return Tidy( acc, locked );
	}

	/// <summary>Blur: move every influence toward the neighbour average — the seam
	/// remover. <paramref name="strength"/> 1 replaces the set with the average;
	/// <paramref name="iterations"/> repeats the pass, each reading the previous pass's
	/// snapshot, so two gentle passes reach further than one hard one. Returns how many
	/// vertices changed.</summary>
	public static int Blur( SkinWeights weights, PolyMesh mesh, float strength, int iterations = 1, IReadOnlySet<int>? locked = null, int isolateBone = -1 )
	{
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( !(strength >= 0f) || strength > 1f ) throw new ArgumentOutOfRangeException( nameof( strength ), "Blur strength is 0..1." );
		if ( iterations < 1 ) throw new ArgumentOutOfRangeException( nameof( iterations ) );
		if ( weights.Count != mesh.VertexCount ) throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );
		if ( isolateBone < -1 ) throw new ArgumentOutOfRangeException( nameof( isolateBone ) );

		var neighbours = mesh.BuildVertexEdges();
		var changed = 0;
		for ( var it = 0; it < iterations; it++ )
		{
			var snapshot = weights.Vertices.Select( w => (BoneWeight[])w.Clone() ).ToArray();
			for ( var v = 0; v < weights.Count; v++ )
			{
				if ( isolateBone >= 0 && WeightOf( snapshot[v], isolateBone ) <= 0f ) continue;
				var average = NeighbourAverage( snapshot, neighbours, v );
				var after = BlendTowardAverage( snapshot[v], average, strength, false, locked );
				if ( after is null || Same( snapshot[v], after ) ) continue;
				weights[v] = after;
				changed++;
			}
		}
		return changed;
	}

	/// <summary>Sharpen: move every influence AWAY from the neighbour average — the
	/// opposite of <see cref="Blur"/>, for putting a crisp joint line back after a blur
	/// went too far. Same strength and iteration rules. Values that go negative are
	/// dropped, and the survivors are tidied, so a sharpen can never produce a negative
	/// influence. Returns how many vertices changed.</summary>
	public static int Sharpen( SkinWeights weights, PolyMesh mesh, float strength, int iterations = 1, IReadOnlySet<int>? locked = null, int isolateBone = -1 )
	{
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( !(strength >= 0f) || strength > 1f ) throw new ArgumentOutOfRangeException( nameof( strength ), "Sharpen strength is 0..1." );
		if ( iterations < 1 ) throw new ArgumentOutOfRangeException( nameof( iterations ) );
		if ( weights.Count != mesh.VertexCount ) throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );
		if ( isolateBone < -1 ) throw new ArgumentOutOfRangeException( nameof( isolateBone ) );

		var neighbours = mesh.BuildVertexEdges();
		var changed = 0;
		for ( var it = 0; it < iterations; it++ )
		{
			var snapshot = weights.Vertices.Select( w => (BoneWeight[])w.Clone() ).ToArray();
			for ( var v = 0; v < weights.Count; v++ )
			{
				if ( isolateBone >= 0 && WeightOf( snapshot[v], isolateBone ) <= 0f ) continue;
				var average = NeighbourAverage( snapshot, neighbours, v );
				var after = BlendTowardAverage( snapshot[v], average, strength, true, locked );
				if ( after is null || Same( snapshot[v], after ) ) continue;
				weights[v] = after;
				changed++;
			}
		}
		return changed;
	}

	/// <summary>Gradient: paint a smooth ramp of <paramref name="bone"/> along the
	/// <paramref name="a"/>-to-<paramref name="b"/> axis — <paramref name="valueA"/> at
	/// the start, <paramref name="valueB"/> at the end, linear in between — one stroke
	/// doing the forearm-to-upper-arm roll-off that takes fiddly Add+Smooth work
	/// otherwise. A zero-length axis is all <paramref name="valueA"/> — a dot, not a
	/// ramp. Unweighted vertices are skipped: a gradient cleans up a binding, it does
	/// not create one. Returns how many vertices changed. Throws when the bone itself
	/// is locked.</summary>
	public static int Gradient( SkinWeights weights, PolyMesh mesh, int bone, Vec3 a, Vec3 b, float valueA = 0f, float valueB = 1f, IReadOnlySet<int>? locked = null, int isolateBone = -1 )
	{
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( bone < 0 ) throw new ArgumentOutOfRangeException( nameof( bone ) );
		if ( locked is not null && locked.Contains( bone ) ) throw new InvalidOperationException( $"Bone {bone} is locked." );
		if ( weights.Count != mesh.VertexCount ) throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );
		if ( isolateBone < -1 ) throw new ArgumentOutOfRangeException( nameof( isolateBone ) );

		var ab = b - a;
		var len2 = ab.LengthSquared;
		var changed = 0;
		for ( var v = 0; v < weights.Count; v++ )
		{
			var before = weights[v];
			if ( before is null || before.Length == 0 ) continue;
			if ( isolateBone >= 0 && WeightOf( before, isolateBone ) <= 0f ) continue;
			var t = len2 < 1e-12f ? 0f : Math.Clamp( Vec3.Dot( mesh.Positions[v] - a, ab ) / len2, 0f, 1f );
			var after = SetBone( before, bone, valueA + (valueB - valueA) * t, locked );
			if ( after is null || Same( before, after ) ) continue;
			weights[v] = after;
			changed++;
		}
		return changed;
	}

	/// <summary>Remap one weight set through the mirror-partner map: every influence
	/// moves to its partner bone (arm_L → arm_R, spine → itself). Sums are unchanged,
	/// so a normalized set stays normalized.</summary>
	static BoneWeight[] RemapBones( BoneWeight[] weights, int[] partners )
	{
		var acc = new Dictionary<int, float>();
		foreach ( var w in weights )
		{
			if ( !float.IsFinite( w.Weight ) || w.Weight <= 0f ) continue;
			var target = w.Bone >= 0 && w.Bone < partners.Length ? partners[w.Bone] : w.Bone;
			acc[target] = acc.TryGetValue( target, out var e ) ? e + w.Weight : w.Weight;
		}
		return acc
			.OrderByDescending( kv => kv.Value )
			.ThenBy( kv => kv.Key )
			.Select( kv => new BoneWeight( kv.Key, kv.Value ) )
			.ToArray();
	}

	/// <summary>Mirror the WEIGHTS across the rig: every vertex's influences swap to
	/// their mirror-partner bones (<see cref="Skeleton.MirrorPartners"/> — _L/_R, .L/.R,
	/// Left/Right words, l_/r_ prefixes). The left arm's paint becomes the right arm's.
	/// Unpaired bones (spine) map to themselves. Returns how many vertices changed.</summary>
	public static int MirrorBones( SkinWeights weights, Skeleton skeleton )
	{
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );

		var partners = skeleton.MirrorPartners( out _ );
		var changed = 0;
		for ( var v = 0; v < weights.Count; v++ )
		{
			var before = weights[v];
			if ( before is null || before.Length == 0 ) continue;
			var after = RemapBones( before, partners );
			if ( Same( before, after ) ) continue;
			weights[v] = after;
			changed++;
		}
		return changed;
	}

	/// <summary>Mirror the weights across the MESH: each vertex on the −normal side has
	/// its weight set copied onto its mirror vertex on the +normal side, with the bones
	/// swapped to their partners — painting the left arm lands on the right arm, on the
	/// right vertices. The source side is never written. Vertices on the mirror plane
	/// map to themselves and are skipped, and a vertex with no mirror partner inside
	/// <paramref name="tolerance"/> is left alone. Returns how many vertices were
	/// written.</summary>
	public static int MirrorVertices( PolyMesh mesh, SkinWeights weights, Skeleton skeleton, Vec3 mirrorNormal, float tolerance = 1e-3f )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( weights.Count != mesh.VertexCount ) throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );
		if ( !(tolerance > 0f) ) throw new ArgumentOutOfRangeException( nameof( tolerance ) );

		var n = mirrorNormal.Normal;
		if ( n.LengthSquared < 0.5f ) throw new ArgumentException( "A mirror plane needs a normal.", nameof( mirrorNormal ) );

		var partners = skeleton.MirrorPartners( out _ );
		var bvh = MeshBVH.Build( mesh );
		var found = new List<int>();
		var done = new bool[mesh.VertexCount];
		var written = 0;

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			if ( done[v] ) continue;
			done[v] = true;

			var p = mesh.Positions[v];
			var dist = Vec3.Dot( p, n );
			if ( MathF.Abs( dist ) <= tolerance ) continue;

			var reflected = p - n * (2f * dist);
			found.Clear();
			bvh.VerticesInRadius( mesh, reflected, tolerance, found );

			var best = -1;
			var bestDistance = float.MaxValue;
			foreach ( var u in found )
			{
				var d = (mesh.Positions[u] - reflected).Length;
				if ( d < bestDistance ) { bestDistance = d; best = u; }
			}
			if ( best < 0 || best == v ) continue;
			done[best] = true;

			// The −normal side is the source, the +normal side the target, so the copy
			// direction does not depend on vertex order.
			var source = dist < 0f ? v : best;
			var target = dist < 0f ? best : v;
			weights[target] = RemapBones( weights[source], partners );
			written++;
		}
		return written;
	}
}
