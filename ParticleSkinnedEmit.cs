#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

// ParticleSkinnedEmit — wave-6 lane P78 (skinned-mesh emit).
//
// THE HONEST BOUNDARY — read this before wiring anything up:
// This lane does NOT solve skinning. It does NOT own bones, rigs, or animation
// data, and it does NOT read any rig file format. The rig session owns bones
// and where bone matrices come from. What this lane owns is the pure step AFTER
// posing: given (a) a bind-pose mesh and (b) bone matrices for a single pose,
// compute posed vertex positions, then sample emit points from the posed
// triangles.
//
// Inputs, exactly:
// - Bind geometry is ParticleSystem's ParticleMesh (positions + triangle
//   indices, kernel inches, Z-up) — the same type the MeshSurface emitter
//   already samples. Build it with ParticleMesh.FromPolyMesh for a kernel mesh.
// - Per-vertex bone weights are the codebase's own format, Effigy.SkinWeights:
//   a BoneWeight[] per vertex, parallel to the bind positions, in the kernel
//   Rig/SkinWeights.cs sense. The kernel format is deliberately UNBOUNDED in
//   influence count (smoothing/subdivision produce more than four); this lane
//   sums every listed influence and applies NO 4-cap and NO pruning — an
//   export cap would change posed positions and belongs to an exporter, not
//   to posing. The sweep mapping point is therefore trivial: the rig session
//   hands over PolyMesh.Skin (a SkinWeights) alongside the ParticleMesh built
//   from the same PolyMesh, with bone indices in Skeleton.Bones order.
// - Bone matrices are ONE Matrix4x4 per bone, supplied by the caller, and each
//   one must ALREADY be the complete bind->posed transform for that bone
//   (posed-world * inverse-bind-world, as the rig session computes it).
//   This lane never computes bind inverses, never touches a Skeleton, and
//   never reads a rig file. An identity matrix means "this bone did not
//   move", so an all-identity pose returns the bind pose bit-identically.
//
// Posing math (linear blend skinning, nothing fancier):
//   posedPosition = sum over influencing bones of (weight_i * (boneMatrix_i * bindPosition))
// with System.Numerics row-vector convention (Vector3.Transform).
//
// Weight rule, picked once and tested: weights are NEVER silently
// renormalised. Validate() throws when a vertex's weights do not sum to 1
// within WeightSumTolerance (1e-3, the same tolerance SkinWeights.Validate
// uses), when a weight is negative or non-finite, when a vertex is
// unweighted, or when a bone index is out of range of the supplied matrices.
// Unused extra bone matrices are ignored; only referenced matrices must be
// finite.
//
// Sampling: posed triangles are sampled area-weighted, with the exact draw
// pattern of ParticleSystem's MeshSurface emitter (one RNG float to pick a
// triangle off the posed area CDF, two more for a uniform point in it).
// Density-weighted sampling is NOT in this lane: the P77 sampler
// (ParticleMeshEmit.cs) does not exist in this tree as surveyed, so there
// is nothing clean to delegate to. If P77 lands later, a density-weighted
// overload can delegate to it without touching this file's area path.
// Seeded ParticleRng (SplitMix64/xorshift, never System.Random); same seed
// + same pose = bit-identical samples.
//
// OUT OF SCOPE, deliberately: bone/rig/animation data, rig file reading,
// animation playback or timeline (cue timing is P52/P53's), any change to
// the MeshSurface emitter or to ParticleSystem.Spawn, texture/density
// loading, GPU code, save/serialization, canvas/UI. This is authoring-only
// sim data: posed positions and emit samples, nothing that runs a game loop.

/// <summary>One emit sample from a posed triangle: its position, the posed
/// triangle's unit normal (Vector3.Zero for a degenerate triangle), and the
/// triangle's index in the posed mesh.</summary>
public readonly record struct SkinnedEmitSample( Vector3 Position, Vector3 Normal, int TriangleIndex );

/// <summary>Pure functions over posed geometry: linear-blend-skin a bind
/// mesh with caller-supplied bone matrices, then area-sample emit points
/// from the posed triangles. No state, no rig access, no file access.</summary>
public static class ParticleSkinnedEmit
{
	/// <summary>Allowed |sum - 1| for a vertex's weights; outside this,
	/// Validate() throws instead of renormalising. Matches SkinWeights.Validate.</summary>
	public const float WeightSumTolerance = 1e-3f;

	/// <summary>Throws ArgumentException/ArgumentOutOfRangeException on any
	/// bad input (see the file header for the full rule list). A valid
	/// input passes silently.</summary>
	public static void Validate( ParticleMesh bindMesh, SkinWeights weights, IReadOnlyList<Matrix4x4> boneMatrices )
	{
		ArgumentNullException.ThrowIfNull( bindMesh );
		ArgumentNullException.ThrowIfNull( weights );
		ArgumentNullException.ThrowIfNull( boneMatrices );
		if ( weights.Vertices.Count != bindMesh.Vertices.Length )
			throw new ArgumentException( $"Skinned emit: {weights.Vertices.Count} weight sets for {bindMesh.Vertices.Length} bind vertices." );
		var referenced = new HashSet<int>();
		for ( int v = 0; v < weights.Vertices.Count; v++ )
		{
			var w = weights.Vertices[v];
			if ( w is null || w.Length == 0 )
				throw new ArgumentException( $"Skinned emit: vertex {v} is unweighted." );
			float sum = 0f;
			foreach ( var bw in w )
			{
				if ( bw.Bone < 0 || bw.Bone >= boneMatrices.Count )
					throw new ArgumentOutOfRangeException( nameof( boneMatrices ), $"Skinned emit: vertex {v} references bone {bw.Bone}, outside 0..{boneMatrices.Count - 1}." );
				if ( !float.IsFinite( bw.Weight ) || bw.Weight < 0 )
					throw new ArgumentException( $"Skinned emit: vertex {v} has an invalid weight {bw.Weight} on bone {bw.Bone}." );
				sum += bw.Weight;
				referenced.Add( bw.Bone );
			}
			if ( MathF.Abs( sum - 1f ) > WeightSumTolerance )
				throw new ArgumentException( $"Skinned emit: vertex {v} weights sum to {sum}, not 1 (tolerance {WeightSumTolerance}); weights are never silently renormalised." );
		}
		foreach ( var b in referenced )
			if ( !Finite( boneMatrices[b] ) )
				throw new ArgumentException( $"Skinned emit: bone matrix {b} is not finite." );
	}

	static Vector3 PoseOne( Vector3 bind, BoneWeight[] w, IReadOnlyList<Matrix4x4> bones )
	{
		var posed = Vector3.Zero;
		foreach ( var bw in w )
			posed += Vector3.Transform( bind, bones[bw.Bone] ) * bw.Weight;
		return posed;
	}

	/// <summary>Posed vertex positions, one per bind vertex, in bind order.
	/// Identity matrices return the bind positions bit-identically.</summary>
	public static Vector3[] PoseVertices( ParticleMesh bindMesh, SkinWeights weights, IReadOnlyList<Matrix4x4> boneMatrices )
	{
		Validate( bindMesh, weights, boneMatrices );
		var posed = new Vector3[bindMesh.Vertices.Length];
		for ( int v = 0; v < posed.Length; v++ )
			posed[v] = PoseOne( bindMesh.Vertices[v], weights.Vertices[v], boneMatrices );
		return posed;
	}

	/// <summary>The bind mesh posed: same triangle indices, posed vertices,
	/// and therefore a posed area CDF for sampling. Throws (from the
	/// ParticleMesh constructor) if posing collapses the mesh to zero
	/// total area — a loud failure, not a silent empty emitter.</summary>
	public static ParticleMesh PoseMesh( ParticleMesh bindMesh, SkinWeights weights, IReadOnlyList<Matrix4x4> boneMatrices )
		=> new( PoseVertices( bindMesh, weights, boneMatrices ), bindMesh.Indices );

	/// <summary>One area-weighted emit sample from an already-posed mesh:
	/// the same triangle-pick + barycentric draws as the MeshSurface
	/// emitter, so a posed mesh and its bind mesh sampled with the same
	/// seed pick the same triangles with the same barycentric points.</summary>
	public static SkinnedEmitSample SamplePoint( ParticleMesh posedMesh, ref ParticleRng rng )
	{
		ArgumentNullException.ThrowIfNull( posedMesh );
		float r = rng.NextFloat() * posedMesh.TotalArea;
		int tri = Array.BinarySearch( posedMesh.AreaCdf, r );
		if ( tri < 0 ) tri = ~tri;
		tri = Math.Min( tri, posedMesh.TriangleCount - 1 );
		// Skip zero-area triangles that share a CDF value with the previous one.
		while ( tri > 0 && posedMesh.AreaCdf[tri] == posedMesh.AreaCdf[tri - 1] && r < posedMesh.AreaCdf[tri - 1] ) tri--;
		float s1 = rng.NextFloat(), s2 = rng.NextFloat();
		if ( s1 + s2 > 1 ) { s1 = 1 - s1; s2 = 1 - s2; }
		posedMesh.GetTriangle( tri, out var a, out var b, out var c );
		var pos = a + (b - a) * s1 + (c - a) * s2;
		var n = Vector3.Cross( b - a, c - a );
		var normal = n.LengthSquared() > 0 ? Vector3.Normalize( n ) : Vector3.Zero;
		return new SkinnedEmitSample( pos, normal, tri );
	}

	/// <summary><paramref name="count"/> area-weighted emit samples from an
	/// already-posed mesh, in RNG order. Count 0 returns an empty array
	/// and draws nothing.</summary>
	public static SkinnedEmitSample[] SamplePoints( ParticleMesh posedMesh, int count, ref ParticleRng rng )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ), "Sample count must be >= 0." );
		var samples = new SkinnedEmitSample[count];
		for ( int i = 0; i < count; i++ ) samples[i] = SamplePoint( posedMesh, ref rng );
		return samples;
	}

	/// <summary>Pose, then sample: the one-call path for a spawn site that
	/// holds bind geometry + weights + one pose's bone matrices.</summary>
	public static SkinnedEmitSample[] SamplePoints( ParticleMesh bindMesh, SkinWeights weights, IReadOnlyList<Matrix4x4> boneMatrices, int count, ref ParticleRng rng )
		=> SamplePoints( PoseMesh( bindMesh, weights, boneMatrices ), count, ref rng );

	static bool Finite( Matrix4x4 m )
		=> float.IsFinite( m.M11 ) && float.IsFinite( m.M12 ) && float.IsFinite( m.M13 ) && float.IsFinite( m.M14 )
		&& float.IsFinite( m.M21 ) && float.IsFinite( m.M22 ) && float.IsFinite( m.M23 ) && float.IsFinite( m.M24 )
		&& float.IsFinite( m.M31 ) && float.IsFinite( m.M32 ) && float.IsFinite( m.M33 ) && float.IsFinite( m.M34 )
		&& float.IsFinite( m.M41 ) && float.IsFinite( m.M42 ) && float.IsFinite( m.M43 ) && float.IsFinite( m.M44 );
}
