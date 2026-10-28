using System;

namespace Effigy;

/// <summary>
/// The pattern a tri-planar paint stroke samples. None means "no pattern" — the stroke paints
/// its plain falloff, exactly as before.
/// </summary>
public enum TriPlanarPattern
{
	None,
	Stripes,
	Checker,
	Spots,
	Fbm,
	Worley,
	Ridged,
}

/// <summary>
/// Substance-style tri-planar pattern sampling for paint strokes.
///
/// WHY WORLD SPACE, NOT UV SPACE. A pattern sampled on UV coordinates breaks wherever the atlas
/// breaks: at every island boundary the UV jumps, so a stripe that runs across a seam restarts
/// with a different phase, and a stretched island stretches the pattern with it. A pattern sampled
/// on the surface's world position has no UV to jump across — two texels that sit next to each
/// other on the surface sample next to each other in pattern space, whatever island each one
/// happens to live on. That is the whole seam-free claim, and it is a claim about the sampling
/// domain, not about the geometry: on a smooth surface the sample varies smoothly with position,
/// and at a hard normal edge the blend weights jump with the normal, so a hard edge can still
/// show a pattern transition. Tri-planar removes the UV seam; it does not remove the edge.
///
/// HOW IT BLENDS. Each of the three world planes contributes the pattern sampled on its two
/// in-plane coordinates (X plane: y,z; Y plane: x,z; Z plane: x,y), weighted by the surface
/// normal raised to <see cref="Weights"/> sharpness and normalised. A face pointing straight
/// up samples its pattern from the XZ plane alone; a face at 45° blends two planes equally.
/// <c>scale</c> is world units per pattern repeat, so a stripe that should repeat every 2 units
/// of surface takes scale 2 whatever the mesh's UV density is.
///
/// THE PATTERNS ARE DELIBERATELY SMALL. Stripes is a sine (smooth, so a stripe edge is a
/// gradient, not a cliff); Checker is a hard parity grid (the case that most exposes a seam,
/// because one wrong cell reads instantly); Spots is a deterministic value-noise field (same
/// input, same output, every build — a pattern that moved between rebuilds would repaint the
/// model under the artist). A texture-image pattern is a later step, not this one: these three
/// prove the sampling domain, which is what was missing.
/// </summary>
public static class TriPlanar
{
	/// <summary>
	/// Blend weights per world axis for a surface normal: pow(|n|, sharpness), normalised.
	/// An axis-aligned normal puts all the weight on its own plane; a zero, NaN, or
	/// otherwise degenerate normal falls back to equal thirds rather than NaN or an
	/// arbitrary axis (no +Z bias: a faceless direction must not silently paint as up).
	/// </summary>
	public static (float X, float Y, float Z) Weights( Vec3 normal, float sharpness )
	{
		sharpness = Math.Clamp( sharpness, 0.5f, 16f );

		// .Normal already returns Zero for a degenerate vector (see Vec.cs); a zero direction
		// has no plane to favour, so it blends all three equally instead of pretending to face up.
		var n = normal.Normal;
		if ( n.LengthSquared <= 0f )
			return ( 1f / 3f, 1f / 3f, 1f / 3f );
		var wx = MathF.Pow( MathF.Abs( n.x ), sharpness );
		var wy = MathF.Pow( MathF.Abs( n.y ), sharpness );
		var wz = MathF.Pow( MathF.Abs( n.z ), sharpness );
		var sum = wx + wy + wz;

		if ( sum <= 1e-6f )
			return ( 1f / 3f, 1f / 3f, 1f / 3f );

		return ( wx / sum, wy / sum, wz / sum );
	}

	/// <summary>
	/// The pattern value at a surface point, 0..1. None returns 1 (no modulation) so the
	/// tri-planar replay path and the plain path agree exactly when no pattern is set.
	/// </summary>
	public static float Sample( TriPlanarPattern pattern, Vec3 pos, Vec3 normal, float scale, float sharpness )
		=> Sample( pattern, pos, normal, scale, sharpness, 0 );

	/// <summary>Seeded overload: Fbm/Ridged/Worley take the seed so a
	/// pattern does not re-roll between rebuilds. The classic patterns
	/// ignore the seed, exactly as before.</summary>
	public static float Sample( TriPlanarPattern pattern, Vec3 pos, Vec3 normal, float scale, float sharpness, int seed )
	{
		if ( pattern == TriPlanarPattern.None )
			return 1f;

		if ( scale <= 0f )
			scale = 1f;

		var (wx, wy, wz) = Weights( normal, sharpness );
		var sx = PatternAt( pattern, pos.y / scale, pos.z / scale, seed );
		var sy = PatternAt( pattern, pos.x / scale, pos.z / scale, seed );
		var sz = PatternAt( pattern, pos.x / scale, pos.y / scale, seed );

		return Math.Clamp( wx * sx + wy * sy + wz * sz, 0f, 1f );
	}

	static float PatternAt( TriPlanarPattern pattern, float u, float v, int seed ) => pattern switch
	{
		TriPlanarPattern.Stripes => 0.5f + 0.5f * MathF.Sin( 2f * MathF.PI * u ),
		// Bitwise parity, not %: C#'s % keeps the sign, so a negative cell index would flip
		// the checker on the negative side of the origin. & 1 on two's complement is parity
		// on both sides.
		TriPlanarPattern.Checker => (( (int)MathF.Floor( u ) + (int)MathF.Floor( v ) ) & 1) == 0 ? 1f : 0f,
		TriPlanarPattern.Spots => ValueNoise( u, v ),
		// 3D noise promoted to triplanar: the third coordinate is the seed
		// offset, so two seeds paint two patterns and one seed repaints
		// the same pattern every rebuild. Worley reads as pits: the value
		// is 0 at a cell centre and rises to the cell edge.
		TriPlanarPattern.Fbm => Math.Clamp( 0.5f + Noise( seed ).Fbm( new Vec3( u, v, seed * 0.371f ), 3 ), 0f, 1f ),
		TriPlanarPattern.Ridged => Math.Clamp( 0.5f + 0.5f * Noise( seed ).Ridged( new Vec3( u, v, seed * 0.371f ), 3 ), 0f, 1f ),
		TriPlanarPattern.Worley => Math.Clamp( SurfaceDetail.Worley( new Vec3( u, v, seed * 0.13f ), seed ).F1, 0f, 1f ),
		_ => 1f,
	};

	// Noise3 construction shuffles a 512-entry permutation; cache one
	// per seed instead of rebuilding it for every texel sampled.
	static readonly System.Collections.Generic.Dictionary<int, Noise3> _noiseCache = new();
	static Noise3 Noise( int seed )
	{
		if ( !_noiseCache.TryGetValue( seed, out var n ) )
			_noiseCache[seed] = n = new Noise3( seed );
		return n;
	}

	/// <summary>Deterministic value noise: same lattice, same output, every build.</summary>
	static float ValueNoise( float u, float v )
	{
		var xi = (int)MathF.Floor( u );
		var yi = (int)MathF.Floor( v );
		var xf = u - xi;
		var yf = v - yi;
		var sx = xf * xf * (3f - 2f * xf);
		var sy = yf * yf * (3f - 2f * yf);
		var a = Hash( xi, yi );
		var b = Hash( xi + 1, yi );
		var c = Hash( xi, yi + 1 );
		var d = Hash( xi + 1, yi + 1 );

		return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
	}

	static float Hash( int x, int y )
	{
		var h = MathF.Sin( x * 127.1f + y * 311.7f ) * 43758.5453f;
		return h - MathF.Floor( h );
	}
}
