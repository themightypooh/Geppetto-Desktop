// Wave-6 lane P77 — mesh-surface emit v2: density-weighted surface sampling.
//
// This is a PURE SAMPLER. It does not touch ParticleSystem, its MeshSurface
// emitter, or any Spawn path. The v1 area-weighted emitter in
// ParticleSystem.cs stays untouched and stays the default; this file only
// becomes live when the coordinator sweep wires it into the MeshSurface
// spawn path (integration lines reported with the lane, never edited in).
//
// What ParticleMesh does and does not carry (read from ParticleSystem.cs,
// not assumed): a ParticleMesh holds positions (Vertices, Vector3[]) and
// triangle indices (Indices, int[], 3 per triangle) plus its own
// area-weighted AreaCdf. It carries NO vertex colours and NO UVs. So the
// caller supplies the per-vertex density data alongside the mesh, in one
// of the three forms below, index-aligned with mesh.Vertices:
//
//   * Vertex density (scalar): one float per vertex, >= 0, finite.
//   * Vertex colour: one Vector3 RGB per vertex; density = mean(R, G, B).
//     Black (0, 0, 0) has density 0 and contributes nothing. Channels must
//     be finite and >= 0; values above 1 are allowed (HDR-style density)
//     and are NOT clamped — only their mean matters.
//   * UV density: one Vector2 UV per vertex plus a caller-supplied
//     Func<Vector2, float>. No texture is loaded or decoded in this lane;
//     the caller supplies the function (e.g. a painted density map sampled
//     by the caller). The function is evaluated ONCE per triangle, at the
//     triangle's UV centroid (the mean of its three vertex UVs) — NOT at
//     the sampled point's UV. Density therefore varies triangle to
//     triangle, not within a triangle; a triangle is either live or dead
//     as a whole, weighted by its centroid density.
//
// Weight and CDF (documented, same area convention as v1):
//   area(t)   = 0.5 * |cross(b - a, c - a)|   (exactly ParticleMesh's area)
//   weight(t) = area(t) * meanDensity(t)
//     - vertex forms: meanDensity(t) = mean of the three vertex densities
//       (for colours, the per-vertex density is mean(R, G, B) first).
//     - UV form: meanDensity(t) = density(uvCentroid(t)).
//   Cdf[t] = sum of weights up to and including t, accumulated in double
//   (ParticleMesh accumulates its own area CDF the same way). Cdf[last]
//   == TotalWeight. A triangle with weight 0 (zero area, or zero density)
//   leaves the CDF flat across its entry and is never picked.
//
// Sampling (documented):
//   1. Draw u = rng.NextFloat() in [0, 1) from the sim's seeded
//      ParticleRng (SplitMix64-seeded xorshift; System.Random is never
//      used). r = u * TotalWeight.
//   2. Pick the FIRST triangle t with Cdf[t] > r by binary search
//      (lower bound). Because zero-weight triangles never raise the CDF,
//      the first index above r is always a live triangle, so no
//      zero-weight triangle is ever returned and no post-hoc skip loop
//      is needed (v1 uses Array.BinarySearch plus a skip loop for the
//      same reason; the lower bound subsumes both).
//   3. Draw s1, s2 uniform in [0, 1) and fold: if s1 + s2 > 1, replace
//      (s1, s2) with (1 - s1, 1 - s2). This is the SAME fold v1's Spawn
//      uses. It is unbiased: the unit square's outer triangle maps onto
//      the inner triangle by reflection, which preserves area, so the
//      folded pair is uniform over the triangle. Point =
//      a + (b - a) * s1 + (c - a) * s2.
//   Draw order is fixed (1 triangle draw, then 2 barycentric draws), so
//   a fixed seed + a fixed mesh gives bit-identical sample sequences.
//
// Units: kernel inches, Z-up, like ParticleMesh. Geometry is used as-is;
// no transform is applied here (the caller transforms the mesh first,
// e.g. via ParticleMesh.FromPolyMesh with an Xform).
//
// Loud failures (all at construction, never silently at sample time):
// null mesh / density arrays / density function, density/UV array length
// != vertex count, non-finite or negative densities / colours / UVs /
// density-function results, and zero total weight (all densities zero,
// or every live-area triangle dead). An "empty mesh" cannot even reach
// this class: ParticleMesh's own constructor already rejects empty or
// non-multiple-of-3 index lists, and that rejection is pinned in tests.

#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>One density-weighted surface sample: the point, its triangle, and that triangle's unit normal.</summary>
public readonly record struct MeshSurfaceSample( Vector3 Point, int Triangle, Vector3 Normal );

/// <summary>
/// Density-weighted triangle-mesh surface sampler (mesh-surface emit v2).
/// Pure: no system, no spawn, no GPU, no I/O. Build once per mesh+density,
/// then call <see cref="Sample"/> with the sim's seeded <see cref="ParticleRng"/>.
/// </summary>
public sealed class ParticleMeshDensitySampler
{
	public ParticleMesh Mesh { get; }
	public int TriangleCount => Mesh.TriangleCount;
	/// <summary>Per-triangle weight area * mean density, length TriangleCount.</summary>
	public readonly double[] Weights;
	/// <summary>Cumulative weights, length TriangleCount; last entry == TotalWeight.</summary>
	public readonly double[] Cdf;
	/// <summary>Sum of all triangle weights; always > 0 (zero total weight throws at construction).</summary>
	public readonly double TotalWeight;

	ParticleMeshDensitySampler( ParticleMesh mesh, double[] weights )
	{
		Mesh = mesh;
		Weights = weights;
		Cdf = new double[weights.Length];
		double total = 0;
		for ( int t = 0; t < weights.Length; t++ )
		{
			if ( !double.IsFinite( weights[t] ) || weights[t] < 0 )
				throw new ArgumentException( $"Triangle {t} weight must be finite and >= 0, got {weights[t]}." );
			total += weights[t];
			Cdf[t] = total;
		}
		if ( !( total > 0 ) || !double.IsFinite( total ) )
			throw new ArgumentException( "Density-weighted mesh has zero total weight: every triangle has zero area or zero density, so no triangle can be sampled." );
		TotalWeight = total;
	}

	static double TriangleArea( ParticleMesh mesh, int t )
	{
		mesh.GetTriangle( t, out var a, out var b, out var c );
		return 0.5 * Vector3.Cross( b - a, c - a ).Length();
	}

	static void CheckMesh( ParticleMesh? mesh )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		if ( mesh.TriangleCount == 0 )
			throw new ArgumentException( "Mesh has no triangles.", nameof( mesh ) );
	}

	/// <summary>Build from one density scalar per vertex (index-aligned with mesh.Vertices).</summary>
	public static ParticleMeshDensitySampler FromVertexDensity( ParticleMesh mesh, IReadOnlyList<float> vertexDensity )
	{
		CheckMesh( mesh );
		ArgumentNullException.ThrowIfNull( vertexDensity );
		if ( vertexDensity.Count != mesh.Vertices.Length )
			throw new ArgumentException( $"Vertex density count {vertexDensity.Count} != vertex count {mesh.Vertices.Length}." );
		var densities = new double[vertexDensity.Count];
		for ( int i = 0; i < densities.Length; i++ )
		{
			float d = vertexDensity[i];
			if ( !float.IsFinite( d ) || d < 0 )
				throw new ArgumentException( $"Vertex {i} density must be finite and >= 0, got {d}." );
			densities[i] = d;
		}
		var weights = new double[mesh.TriangleCount];
		for ( int t = 0; t < weights.Length; t++ )
		{
			double mean = ( densities[mesh.Indices[t * 3]] + densities[mesh.Indices[t * 3 + 1]] + densities[mesh.Indices[t * 3 + 2]] ) / 3.0;
			weights[t] = TriangleArea( mesh, t ) * mean;
		}
		return new ParticleMeshDensitySampler( mesh, weights );
	}

	/// <summary>
	/// Build from one RGB colour per vertex (index-aligned with mesh.Vertices).
	/// Per-vertex density = mean(R, G, B); black contributes exactly 0.
	/// </summary>
	public static ParticleMeshDensitySampler FromVertexColours( ParticleMesh mesh, IReadOnlyList<Vector3> vertexColours )
	{
		CheckMesh( mesh );
		ArgumentNullException.ThrowIfNull( vertexColours );
		if ( vertexColours.Count != mesh.Vertices.Length )
			throw new ArgumentException( $"Vertex colour count {vertexColours.Count} != vertex count {mesh.Vertices.Length}." );
		var densities = new double[vertexColours.Count];
		for ( int i = 0; i < densities.Length; i++ )
		{
			var c = vertexColours[i];
			if ( !float.IsFinite( c.X ) || !float.IsFinite( c.Y ) || !float.IsFinite( c.Z ) || c.X < 0 || c.Y < 0 || c.Z < 0 )
				throw new ArgumentException( $"Vertex {i} colour must be finite with channels >= 0, got {c}." );
			densities[i] = ( (double)c.X + c.Y + c.Z ) / 3.0;
		}
		var weights = new double[mesh.TriangleCount];
		for ( int t = 0; t < weights.Length; t++ )
		{
			double mean = ( densities[mesh.Indices[t * 3]] + densities[mesh.Indices[t * 3 + 1]] + densities[mesh.Indices[t * 3 + 2]] ) / 3.0;
			weights[t] = TriangleArea( mesh, t ) * mean;
		}
		return new ParticleMeshDensitySampler( mesh, weights );
	}

	/// <summary>
	/// Build from one UV per vertex plus a caller-supplied density function over
	/// UV space. The function is evaluated at each triangle's UV centroid
	/// (mean of its three vertex UVs); no texture is loaded here.
	/// </summary>
	public static ParticleMeshDensitySampler FromUvDensity( ParticleMesh mesh, IReadOnlyList<Vector2> vertexUvs, Func<Vector2, float> density )
	{
		CheckMesh( mesh );
		ArgumentNullException.ThrowIfNull( vertexUvs );
		ArgumentNullException.ThrowIfNull( density );
		if ( vertexUvs.Count != mesh.Vertices.Length )
			throw new ArgumentException( $"Vertex UV count {vertexUvs.Count} != vertex count {mesh.Vertices.Length}." );
		for ( int i = 0; i < vertexUvs.Count; i++ )
			if ( !float.IsFinite( vertexUvs[i].X ) || !float.IsFinite( vertexUvs[i].Y ) )
				throw new ArgumentException( $"Vertex {i} UV must be finite, got {vertexUvs[i]}." );
		var weights = new double[mesh.TriangleCount];
		for ( int t = 0; t < weights.Length; t++ )
		{
			var uv = ( vertexUvs[mesh.Indices[t * 3]] + vertexUvs[mesh.Indices[t * 3 + 1]] + vertexUvs[mesh.Indices[t * 3 + 2]] ) / 3f;
			float d = density( uv );
			if ( !float.IsFinite( d ) || d < 0 )
				throw new ArgumentException( $"UV density at triangle {t} centroid {uv} must be finite and >= 0, got {d}." );
			weights[t] = TriangleArea( mesh, t ) * d;
		}
		return new ParticleMeshDensitySampler( mesh, weights );
	}

	/// <summary>Unit normal of triangle t (normalized cross product); Zero for a degenerate triangle.</summary>
	public Vector3 TriangleNormal( int t )
	{
		if ( (uint)t >= (uint)TriangleCount ) throw new ArgumentOutOfRangeException( nameof( t ) );
		Mesh.GetTriangle( t, out var a, out var b, out var c );
		var n = Vector3.Cross( b - a, c - a );
		return n.LengthSquared() > 0 ? Vector3.Normalize( n ) : Vector3.Zero;
	}

	/// <summary>
	/// One density-weighted surface sample. Draw order (fixed, for determinism):
	/// 1 triangle draw, then 2 barycentric draws, all from <paramref name="rng"/>.
	/// </summary>
	public MeshSurfaceSample Sample( ref ParticleRng rng )
	{
		double r = rng.NextFloat() * TotalWeight;
		// Lower bound: first t with Cdf[t] > r. r < TotalWeight == Cdf[last]
		// always holds (NextFloat() < 1), so a live triangle is always found,
		// and flat (zero-weight) CDF entries are skipped by construction.
		int lo = 0, hi = TriangleCount - 1;
		while ( lo < hi )
		{
			int mid = ( lo + hi ) / 2;
			if ( Cdf[mid] > r ) hi = mid;
			else lo = mid + 1;
		}
		int tri = lo;
		float s1 = rng.NextFloat(), s2 = rng.NextFloat();
		if ( s1 + s2 > 1 ) { s1 = 1 - s1; s2 = 1 - s2; }
		Mesh.GetTriangle( tri, out var a, out var b, out var c );
		var point = a + (b - a) * s1 + (c - a) * s2;
		return new MeshSurfaceSample( point, tri, TriangleNormal( tri ) );
	}
}
