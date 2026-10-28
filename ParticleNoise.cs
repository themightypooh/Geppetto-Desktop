// Lane P29 (particles session, 2026-10-24): self-contained 3D noise-variant
// evaluators. Pure functions of position (and optionally time), deterministic
// for a fixed seed. Units: kernel inches, Z-up, seconds. Never System.Random.
// These evaluators FEED force fields (lane P22's territory); they are not
// forces themselves, and they are not wired into ParticleSystem.Integrate —
// the exact hookup lines are reported for the coordinator sweep, not edited
// in here. Likewise this file does not re-port lane 3's field; it extends it
// with different kernels (see the kernel-difference note on GradientNoiseGrad).
//
// KERNEL DIFFERENCE — READ BEFORE REUSING (variant A vs the existing port):
//   Existing port (ParticleMath.NoiseGrad / ParticleMath.Curl, lane 3):
//     * VALUE noise: each lattice corner holds one scalar, hashed with
//       x*0x8da6b343 ^ y*0xd8163841 ^ z*0xcb1ab31f ^ seed plus a Murmur-style
//       finalizer, mapped to [-1, 1] by (hash & 0xFFFFFF) * (2/16777215) - 1.
//     * SMOOTHSTEP fade u = t^2 (3 - 2t), derivative 6t(1 - t).
//     * Analytic gradient of the trilinear interpolation of those scalars.
//     * Curl from three such potentials with channel seeds
//       seed, seed ^ 0x68bc21eb, seed ^ 0x02e5be93.
//   Variant A (this file, GradientNoiseGrad / CurlA):
//     * GRADIENT noise (Perlin-style): each lattice corner holds a
//       pseudo-random UNIT VECTOR, not a scalar. The vector is derived from
//       a DIFFERENT hash (x*0x27d4eb2d ^ y*0x165667b1 ^ z*0x9e3779b1 ^ seed,
//       finalizer h ^= h >> 15; h *= 0x85ebca6b; h ^= h >> 13;
//       h *= 0xc2b2ae35; h ^= h >> 16) with its three components taken from
//       disjoint 10-bit slices of that hash, then normalized.
//     * QUINTIC fade u = t^3 (t (6t - 15) + 10), derivative
//       30 t^2 (t - 1)^2 — zero first AND second derivative at cell
//       boundaries, where the port's smoothstep has only a zero first
//       derivative. Different fade => different interpolation weights even
//       before the lattice values differ.
//     * Analytic gradient computed as an explicit 8-corner sum:
//       grad += (fade-derivative weight) * dot + (fade weight) * gradient
//       vector. The port's gradient has no second term at all, because a
//       value-noise corner's scalar does not depend on position inside the
//       cell, while a gradient-noise corner's dot product does.
//     * Curl from three such potentials with channel seeds
//       seed ^ 0x51ed2703, seed ^ 0x7f4a7c15, seed ^ 0x3c6ef372 — none of
//       which is one of the port's channel seeds.
//   Consequence: CurlA is a different field from ParticleMath.Curl at the
//   same point, seed and scale — different lattice data, different fade,
//   different gradient formula — not a re-encoding of the port. The curl
//   cross-product itself (dPsi3/dy - dPsi2/dz, ...) is the definition of
//   curl and is shared by any curl-noise construction; the potentials it
//   is applied to are what differ. (Reference for the construction:
//   Bridson, Hourihan & Nordenstam, "Curl-Noise for Procedural Fluid
//   Flow", 2007 — curl of a vector potential is divergence-free by
//   construction, which is why both the port and this variant use it.)
//
// The other variants build on CurlA, so every variant here differs from
// the port's field rather than re-deriving it:
//   * CurlFbm — fBm over the CurlA field (octaves / lacunarity / gain).
//   * WarpedCurl — domain warp: the sample position is displaced by a
//     first CurlA field before a second (fBm) field is evaluated there.
//   * CurlTime — the potentials are 4D gradient noise, with time as the
//     fourth coordinate, so the field is continuous in t by construction
//     (no seed-stepping, no Z-scroll hack).

using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Configuration for the P29 noise variants. All fields are plain
/// data; <see cref="Validate"/> is the loud-failure gate, and every
/// evaluator below calls it before sampling.</summary>
public sealed class ParticleNoiseConfig
{
	/// <summary>Base seed. Channel/warp seeds are derived by XOR, never by
	/// re-seeding a PRNG, so a fixed seed is a fixed field.</summary>
	public uint Seed = 12345u;
	/// <summary>Noise lattice frequency, 1/inch (same meaning as
	/// ParticleSystemSettings.TurbulenceFrequency; default 0.1 matches it).</summary>
	public float Scale = 0.1f;
	/// <summary>fBm octave count. Must be &gt;= 1.</summary>
	public int Octaves = 4;
	/// <summary>Frequency multiplier per octave. Must be &gt; 1.</summary>
	public float Lacunarity = 2f;
	/// <summary>Amplitude multiplier per octave. Must be in (0, 1).</summary>
	public float Gain = 0.5f;
	/// <summary>Domain-warp displacement scale, inches. The warp vector is
	/// a (dimensionless) CurlA field, so the actual displacement at a point
	/// is WarpAmplitude * |CurlA| inches; 0 = no warp.</summary>
	public float WarpAmplitude = 2f;
	/// <summary>Frequency of the warp field, 1/inch.</summary>
	public float WarpFrequency = 0.05f;
	/// <summary>Time scale for <see cref="ParticleNoise.CurlTime"/>: noise
	/// (fourth-dimension) units per second. 0 = frozen field.</summary>
	public float TimeScale = 1f;

	/// <summary>Loud failure: throws on any invalid value, never silently
	/// clamps or substitutes a default.</summary>
	public void Validate()
	{
		if ( Octaves <= 0 ) throw new ArgumentOutOfRangeException( nameof( Octaves ), "Octaves must be positive." );
		if ( !float.IsFinite( Lacunarity ) || Lacunarity <= 1f ) throw new ArgumentOutOfRangeException( nameof( Lacunarity ), "Lacunarity must be > 1." );
		if ( !float.IsFinite( Gain ) || Gain <= 0f || Gain >= 1f ) throw new ArgumentOutOfRangeException( nameof( Gain ), "Gain must be in (0, 1)." );
		if ( !float.IsFinite( Scale ) || Scale < 0f ) throw new ArgumentOutOfRangeException( nameof( Scale ), "Scale must be >= 0." );
		if ( !float.IsFinite( WarpAmplitude ) || WarpAmplitude < 0f ) throw new ArgumentOutOfRangeException( nameof( WarpAmplitude ), "WarpAmplitude must be >= 0." );
		if ( !float.IsFinite( WarpFrequency ) || WarpFrequency < 0f ) throw new ArgumentOutOfRangeException( nameof( WarpFrequency ), "WarpFrequency must be >= 0." );
		if ( !float.IsFinite( TimeScale ) || TimeScale < 0f ) throw new ArgumentOutOfRangeException( nameof( TimeScale ), "TimeScale must be >= 0." );
	}
}

/// <summary>Self-contained 3D noise-variant evaluators (see header note).
/// Every method is a pure function of its inputs; no state, no RNG.</summary>
public static class ParticleNoise
{
	// Channel seeds for CurlA / CurlTime. Deliberately different from the
	// port's (seed, seed ^ 0x68bc21eb, seed ^ 0x02e5be93).
	const uint Channel1 = 0x51ed2703u;
	const uint Channel2 = 0x7f4a7c15u;
	const uint Channel3 = 0x3c6ef372u;
	const uint WarpSeedXor = 0x9e3779b9u;

	static uint Hash3( int x, int y, int z, uint seed )
	{
		unchecked
		{
			uint h = (uint)x * 0x27d4eb2du ^ (uint)y * 0x165667b1u ^ (uint)z * 0x9e3779b1u ^ seed;
			h ^= h >> 15; h *= 0x85ebca6bu; h ^= h >> 13; h *= 0xc2b2ae35u; h ^= h >> 16;
			return h;
		}
	}

	static uint Hash4( int x, int y, int z, int w, uint seed )
	{
		unchecked
		{
			uint h = (uint)x * 0x27d4eb2du ^ (uint)y * 0x165667b1u ^ (uint)z * 0x9e3779b1u ^ (uint)w * 0x85ebca6bu ^ seed;
			h ^= h >> 15; h *= 0xc2b2ae35u; h ^= h >> 13; h *= 0x27d4eb2du; h ^= h >> 16;
			return h;
		}
	}

	/// <summary>Pseudo-random unit gradient vector at a lattice corner:
	/// three components from disjoint 10-bit slices of the corner hash,
	/// mapped to [-1, 1] and normalized. Deterministic; the zero vector
	/// fallback (only when all three slices are exactly centre) is +X.</summary>
	static Vector3 LatticeGradient( int x, int y, int z, uint seed )
	{
		uint h = Hash3( x, y, z, seed );
		float gx = ((h & 0x3FFu) * (1f / 511.5f)) - 1f;
		float gy = (((h >> 10) & 0x3FFu) * (1f / 511.5f)) - 1f;
		float gz = (((h >> 20) & 0x3FFu) * (1f / 511.5f)) - 1f;
		var g = new Vector3( gx, gy, gz );
		float lenSq = g.LengthSquared();
		if ( lenSq < 1e-12f ) return new Vector3( 1, 0, 0 );
		return g / MathF.Sqrt( lenSq );
	}

	static Vector4 LatticeGradient4( int x, int y, int z, int w, uint seed )
	{
		uint h = Hash4( x, y, z, w, seed );
		float gx = ((h & 0x3FFu) * (1f / 511.5f)) - 1f;
		float gy = (((h >> 10) & 0x3FFu) * (1f / 511.5f)) - 1f;
		float gz = (((h >> 20) & 0x3FFu) * (1f / 511.5f)) - 1f;
		float gw = (((h >> 30) & 0x3u) * (1f / 1.5f)) - 1f;
		var g = new Vector4( gx, gy, gz, gw );
		float lenSq = g.X * g.X + g.Y * g.Y + g.Z * g.Z + g.W * g.W;
		if ( lenSq < 1e-12f ) return new Vector4( 1, 0, 0, 0 );
		float inv = 1f / MathF.Sqrt( lenSq );
		return g * inv;
	}

	static float Quintic( float t ) => t * t * t * (t * (t * 6f - 15f) + 10f);
	static float QuinticDeriv( float t ) => 30f * t * t * (t - 1f) * (t - 1f);

	/// <summary>Variant-A kernel: Perlin-style gradient noise in [-~1, 1]
	/// with analytic gradient. See the header note for exactly how this
	/// differs from ParticleMath.NoiseGrad (gradient lattice, quintic
	/// fade, 8-corner gradient sum).</summary>
	public static float GradientNoiseGrad( Vector3 p, uint seed, out Vector3 grad )
	{
		float fx = MathF.Floor( p.X ), fy = MathF.Floor( p.Y ), fz = MathF.Floor( p.Z );
		int ix = (int)fx, iy = (int)fy, iz = (int)fz;
		float tx = p.X - fx, ty = p.Y - fy, tz = p.Z - fz;
		float ux = Quintic( tx ), uy = Quintic( ty ), uz = Quintic( tz );
		float dx = QuinticDeriv( tx ), dy = QuinticDeriv( ty ), dz = QuinticDeriv( tz );
		float value = 0;
		var g = Vector3.Zero;
		for ( int i = 0; i <= 1; i++ )
		for ( int j = 0; j <= 1; j++ )
		for ( int k = 0; k <= 1; k++ )
		{
			var gv = LatticeGradient( ix + i, iy + j, iz + k, seed );
			float ox = tx - i, oy = ty - j, oz = tz - k;
			float dot = gv.X * ox + gv.Y * oy + gv.Z * oz;
			float wx = i == 1 ? ux : 1f - ux;
			float wy = j == 1 ? uy : 1f - uy;
			float wz = k == 1 ? uz : 1f - uz;
			float dwx = (i == 1 ? dx : -dx) * wy * wz;
			float dwy = wx * (j == 1 ? dy : -dy) * wz;
			float dwz = wx * wy * (k == 1 ? dz : -dz);
			float w = wx * wy * wz;
			value += w * dot;
			// Fade-derivative term (position changes the weights) plus
			// lattice term (position changes the dot product itself).
			g += new Vector3( dwx * dot + w * gv.X, dwy * dot + w * gv.Y, dwz * dot + w * gv.Z );
		}
		grad = g;
		return value;
	}

	/// <summary>4D gradient noise, spatial gradient only: the value and
	/// d/dx, d/dy, d/dz of a potential at (p, w). The w-derivative is not
	/// needed for a spatial curl and is not computed. Continuity in w
	/// (hence in t) follows from the same quintic fade in all 4 axes.</summary>
	public static float GradientNoiseGrad4( Vector3 p, float wCoord, uint seed, out Vector3 grad )
	{
		float fx = MathF.Floor( p.X ), fy = MathF.Floor( p.Y ), fz = MathF.Floor( p.Z ), fw = MathF.Floor( wCoord );
		int ix = (int)fx, iy = (int)fy, iz = (int)fz, iw = (int)fw;
		float tx = p.X - fx, ty = p.Y - fy, tz = p.Z - fz, tw = wCoord - fw;
		float ux = Quintic( tx ), uy = Quintic( ty ), uz = Quintic( tz ), uw = Quintic( tw );
		float dx = QuinticDeriv( tx ), dy = QuinticDeriv( ty ), dz = QuinticDeriv( tz );
		float value = 0;
		var g = Vector3.Zero;
		for ( int i = 0; i <= 1; i++ )
		for ( int j = 0; j <= 1; j++ )
		for ( int k = 0; k <= 1; k++ )
		for ( int l = 0; l <= 1; l++ )
		{
			var gv = LatticeGradient4( ix + i, iy + j, iz + k, iw + l, seed );
			float ox = tx - i, oy = ty - j, oz = tz - k, ow = tw - l;
			float dot = gv.X * ox + gv.Y * oy + gv.Z * oz + gv.W * ow;
			float wx = i == 1 ? ux : 1f - ux;
			float wy = j == 1 ? uy : 1f - uy;
			float wz = k == 1 ? uz : 1f - uz;
			float ww = l == 1 ? uw : 1f - uw;
			float dwx = (i == 1 ? dx : -dx) * wy * wz * ww;
			float dwy = wx * (j == 1 ? dy : -dy) * wz * ww;
			float dwz = wx * wy * (k == 1 ? dz : -dz) * ww;
			float w = wx * wy * wz * ww;
			value += w * dot;
			g += new Vector3( dwx * dot + w * gv.X, dwy * dot + w * gv.Y, dwz * dot + w * gv.Z );
		}
		grad = g;
		return value;
	}

	/// <summary>Variant A: curl of the gradient-noise potentials at noise
	/// point p (already scaled; callers pass p * Scale). Divergence-free
	/// by construction: curl = (dPsi3/dy - dPsi2/dz, dPsi1/dz - dPsi3/dx,
	/// dPsi2/dx - dPsi1/dy).</summary>
	public static Vector3 CurlA( Vector3 p, uint seed )
	{
		GradientNoiseGrad( p, seed ^ Channel1, out var g1 );
		GradientNoiseGrad( p, seed ^ Channel2, out var g2 );
		GradientNoiseGrad( p, seed ^ Channel3, out var g3 );
		return new Vector3( g3.Y - g2.Z, g1.Z - g3.X, g2.X - g1.Y );
	}

	/// <summary>Variant A at a world position: scales p by cfg.Scale first.</summary>
	public static Vector3 CurlA( Vector3 p, ParticleNoiseConfig cfg )
	{
		cfg.Validate();
		return CurlA( p * cfg.Scale, cfg.Seed );
	}

	/// <summary>Variant B: multi-octave curl — fBm over the CurlA field.
	/// Octave i samples CurlA at frequency Scale * Lacunarity^i with
	/// amplitude Gain^i, normalized by the amplitude sum so the result
	/// stays on the same scale as a single octave. Octaves = 1 is exactly
	/// CurlA at cfg.Scale.</summary>
	public static Vector3 CurlFbm( Vector3 p, ParticleNoiseConfig cfg )
	{
		cfg.Validate();
		var sum = Vector3.Zero;
		float ampSum = 0, freq = cfg.Scale, amp = 1f;
		for ( int i = 0; i < cfg.Octaves; i++ )
		{
			sum += amp * CurlA( p * freq, cfg.Seed );
			ampSum += amp;
			freq *= cfg.Lacunarity;
			amp *= cfg.Gain;
		}
		return sum / ampSum;
	}

	/// <summary>Domain warp: displace the sample position by a first CurlA
	/// field — warp = CurlA(p * WarpFrequency, seed ^ WarpSeedXor) — scaled
	/// by WarpAmplitude (inches), then evaluate the fBm field at the
	/// warped position. WarpAmplitude = 0 returns exactly CurlFbm(p).
	/// The warp field is divergence-free, so the warp itself does not
	/// inject a sink/source pattern; the irregularity comes from sampling
	/// the second field at displaced positions.</summary>
	public static Vector3 WarpedCurl( Vector3 p, ParticleNoiseConfig cfg )
	{
		cfg.Validate();
		var warp = CurlA( p * cfg.WarpFrequency, cfg.Seed ^ WarpSeedXor );
		return CurlFbm( p + warp * cfg.WarpAmplitude, cfg );
	}

	/// <summary>Time-varying mode: curl of 4D gradient-noise potentials
	/// sampled at (p * Scale, t * TimeScale). TimeScale is noise units
	/// per second, so t advances the fourth coordinate linearly and the
	/// field is continuous in t (quintic fade in the time axis, same as
	/// the spatial axes) — no scrolling offset, no seed stepping.</summary>
	public static Vector3 CurlTime( Vector3 p, float timeSeconds, ParticleNoiseConfig cfg )
	{
		cfg.Validate();
		float w = timeSeconds * cfg.TimeScale;
		var q = p * cfg.Scale;
		GradientNoiseGrad4( q, w, cfg.Seed ^ Channel1, out var g1 );
		GradientNoiseGrad4( q, w, cfg.Seed ^ Channel2, out var g2 );
		GradientNoiseGrad4( q, w, cfg.Seed ^ Channel3, out var g3 );
		return new Vector3( g3.Y - g2.Z, g1.Z - g3.X, g2.X - g1.Y );
	}
}
