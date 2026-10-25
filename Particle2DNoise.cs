#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// Self-contained 2D noise evaluators for Geppetto's 2D particles (lane P17,
// particles session, 2026-10-24). Pure functions of position (and optionally
// time), deterministic for a fixed seed. Flat XY, Y-down canvas pixels,
// seconds — the same units as Particle2D. No System.Random anywhere: the
// lattice is a hash, so there is no RNG state to seed or stir.
//
// COMPATIBILITY WITH Particle2D (read, not edited):
// - The 2D lattice below deliberately mirrors Particle2DMath.Hash/Lattice
//   bit-for-bit (same primes, same murmur finalizer, same 24-bit mapping),
//   so Value(q, seed) equals the scalar potential inside
//   Particle2DMath.NoiseGrad(q, seed). The cross-check test in
//   Particle2DNoiseTests pins that equality.
// - The time axis uses the codebase's 3D hash scheme (ParticleMath.Hash:
//   same primes plus z * 0xcb1ab31f, same finalizer) with z = time cell,
//   so the time-varying field is the codebase's 3D value noise with the
//   third axis standing for time. Note: a fixed-time slice of the time
//   field is NOT the 2D Value field (different hash domain) — the two
//   modes are separate fields by design.
// - Curl here rotates the gradient 90° as (-dPsi/dy, dPsi/dx), exactly the
//   rotation Particle2DMath.Curl applies to its analytic gradient, so a
//   turbulence step fed by CurlValue agrees in direction with the sim's
//   built-in curl (finite-difference vs analytic gradient is the only
//   difference). Curl is computed by central differences (step 0.05 noise
//   units) because the fBm potential has no closed-form gradient.
// - Curl returns a gradient in NOISE units, like Particle2DMath.Curl does
//   for its input coordinates: multiply by a strength (px/s^2) to use it
//   as acceleration, the way Particle2D multiplies its curl by
//   TurbulenceStrength. Do NOT multiply by Scale again — the caller
//   already scaled position into noise space via settings.Scale.
//
// RANGES (documented, tested):
// - Value: [-1, 1]. Fbm: [-1, 1] (amplitudes normalized by their sum).
// - Curl / CurlValue: no hard bound — a gradient of a [-1,1] field.
//   Measured maxima on the test grids (seed 7, 48x48 positions): 2.77 for
//   CurlValue, 2.72 for default-settings Curl. Callers scale by strength,
//   never assume a bound.
// - Time variants carry the same ranges as their static counterparts.
//
// COST (honest): Value is 4 hash lookups. Fbm is Octaves x that. A curl
// is 4 potential evaluations, so CurlAtTime at default 4 octaves is
// 4 x 4 x 8 = 128 hash lookups per call. For a per-particle turbulence
// step prefer CurlValue (16 lookups) or Octaves 1-2; keep the fBm curl
// for sparse emitters and offline baking.
//
// OUT OF SCOPE: this file applies no forces, moves no particles, draws
// nothing, and saves nothing. It only evaluates fields; the 2D sim (or a
// future sweep hookup) decides how a curl becomes acceleration.

/// <summary>Settings for <see cref="Particle2DNoise"/>. All fields validated by <see cref="Validate"/>.</summary>
public sealed class Particle2DNoiseSettings
{
	/// <summary>Lattice seed. Any uint; different seeds give unrelated fields.</summary>
	public uint Seed = 1;
	/// <summary>Noise lattice frequency, 1/px: noise coordinate = position * Scale. Default 0.02 (one lattice cell per 50 px), matching Particle2DSettings.TurbulenceFrequency's units and default.</summary>
	public float Scale = 0.02f;
	/// <summary>fBm octave count. Default 4.</summary>
	public int Octaves = 4;
	/// <summary>Frequency multiplier per octave. Must be &gt; 1. Default 2.</summary>
	public float Lacunarity = 2f;
	/// <summary>Amplitude multiplier per octave. Must be in (0, 1). Default 0.5.</summary>
	public float Gain = 0.5f;
	/// <summary>Time-axis speed, noise units per second: time coordinate = time * TimeScale. 0 freezes the field (a legal still mode); negative is rejected. Default 0.5 (one time cell per 2 s).</summary>
	public float TimeScale = 0.5f;

	public void Validate()
	{
		if ( Octaves <= 0 ) throw new ArgumentOutOfRangeException( nameof( Octaves ), "Octaves must be positive." );
		if ( !float.IsFinite( Lacunarity ) || Lacunarity <= 1 ) throw new ArgumentOutOfRangeException( nameof( Lacunarity ), "Lacunarity must be finite and > 1." );
		if ( !float.IsFinite( Gain ) || Gain <= 0 || Gain >= 1 ) throw new ArgumentOutOfRangeException( nameof( Gain ), "Gain must be finite and in (0, 1)." );
		if ( !float.IsFinite( Scale ) || Scale <= 0 ) throw new ArgumentOutOfRangeException( nameof( Scale ), "Scale must be finite and > 0 (a zero scale is a constant field, not noise)." );
		if ( !float.IsFinite( TimeScale ) || TimeScale < 0 ) throw new ArgumentOutOfRangeException( nameof( TimeScale ), "TimeScale must be finite and >= 0." );
	}
}

/// <summary>Pure 2D noise evaluators: value noise, fBm, curl fields, and time-varying variants.</summary>
public static class Particle2DNoise
{
	/// <summary>Central-difference step in noise units for every curl gradient.</summary>
	public const float CurlStep = 0.05f;

	static void CheckFbmArgs( int octaves, float lacunarity, float gain )
	{
		if ( octaves <= 0 ) throw new ArgumentOutOfRangeException( nameof( octaves ), "Octaves must be positive." );
		if ( !float.IsFinite( lacunarity ) || lacunarity <= 1 ) throw new ArgumentOutOfRangeException( nameof( lacunarity ), "Lacunarity must be finite and > 1." );
		if ( !float.IsFinite( gain ) || gain <= 0 || gain >= 1 ) throw new ArgumentOutOfRangeException( nameof( gain ), "Gain must be finite and in (0, 1)." );
	}

	// --- Lattice (mirrors Particle2DMath / ParticleMath bit-for-bit; see header) ---

	static uint Hash2( int x, int y, uint seed )
	{
		unchecked
		{
			uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ seed;
			h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
			return h;
		}
	}

	static uint Hash3( int x, int y, int z, uint seed )
	{
		unchecked
		{
			uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ (uint)z * 0xcb1ab31fu ^ seed;
			h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
			return h;
		}
	}

	static float Lattice2( int x, int y, uint seed ) => (Hash2( x, y, seed ) & 0xFFFFFFu) * (2f / 16777215f) - 1f;
	static float Lattice3( int x, int y, int z, uint seed ) => (Hash3( x, y, z, seed ) & 0xFFFFFFu) * (2f / 16777215f) - 1f;

	static float Fade( float t ) => t * t * (3f - 2f * t);

	/// <summary>Value noise in [-1, 1] at noise coordinate <paramref name="q"/> (already scaled). Smoothstep fade, bilinear lattice interpolation.</summary>
	public static float Value( Vector2 q, uint seed )
	{
		float fx = MathF.Floor( q.X ), fy = MathF.Floor( q.Y );
		int ix = (int)fx, iy = (int)fy;
		float tx = q.X - fx, ty = q.Y - fy;
		float ux = Fade( tx ), uy = Fade( ty );
		float c00 = Lattice2( ix, iy, seed ), c10 = Lattice2( ix + 1, iy, seed );
		float c01 = Lattice2( ix, iy + 1, seed ), c11 = Lattice2( ix + 1, iy + 1, seed );
		float x0 = c00 + (c10 - c00) * ux, x1 = c01 + (c11 - c01) * ux;
		return x0 + (x1 - x0) * uy;
	}

	/// <summary>Value noise in [-1, 1] at canvas position <paramref name="position"/> (px), scaled by <paramref name="settings"/>.Scale.</summary>
	public static float ValueAt( Vector2 position, Particle2DNoiseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		return Value( position * settings.Scale, settings.Seed );
	}

	/// <summary>3D value noise in [-1, 1] at noise coordinate <paramref name="q"/> (x, y, time). Smoothstep fade on all three axes, trilinear lattice interpolation. Fade derivatives are 0 at cell boundaries, so the field is continuous across time cells — no popping as time advances.</summary>
	public static float Value3( Vector3 q, uint seed )
	{
		float fx = MathF.Floor( q.X ), fy = MathF.Floor( q.Y ), fz = MathF.Floor( q.Z );
		int ix = (int)fx, iy = (int)fy, iz = (int)fz;
		float tx = q.X - fx, ty = q.Y - fy, tz = q.Z - fz;
		float ux = Fade( tx ), uy = Fade( ty ), uz = Fade( tz );
		float c000 = Lattice3( ix, iy, iz, seed ), c100 = Lattice3( ix + 1, iy, iz, seed );
		float c010 = Lattice3( ix, iy + 1, iz, seed ), c110 = Lattice3( ix + 1, iy + 1, iz, seed );
		float c001 = Lattice3( ix, iy, iz + 1, seed ), c101 = Lattice3( ix + 1, iy, iz + 1, seed );
		float c011 = Lattice3( ix, iy + 1, iz + 1, seed ), c111 = Lattice3( ix + 1, iy + 1, iz + 1, seed );
		float x00 = c000 + (c100 - c000) * ux, x10 = c010 + (c110 - c010) * ux;
		float x01 = c001 + (c101 - c001) * ux, x11 = c011 + (c111 - c011) * ux;
		float y0 = x00 + (x10 - x00) * uy, y1 = x01 + (x11 - x01) * uy;
		return y0 + (y1 - y0) * uz;
	}

	/// <summary>Time-varying value noise in [-1, 1] at canvas position <paramref name="position"/> (px) and time <paramref name="time"/> (s). Time coordinate is time * <paramref name="settings"/>.TimeScale in the 3D lattice's third axis; spatial coordinates are position * Scale as in <see cref="ValueAt"/>.</summary>
	public static float ValueAtTime( Vector2 position, float time, Particle2DNoiseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		if ( !float.IsFinite( time ) ) throw new ArgumentOutOfRangeException( nameof( time ), "Time must be finite." );
		Vector2 q = position * settings.Scale;
		return Value3( new Vector3( q.X, q.Y, time * settings.TimeScale ), settings.Seed );
	}

	/// <summary>fBm in [-1, 1] at noise coordinate <paramref name="q"/>: sum of <paramref name="octaves"/> value-noise octaves, frequency x <paramref name="lacunarity"/> and amplitude x <paramref name="gain"/> per octave, normalized by the amplitude sum so the range stays [-1, 1]. Octaves = 1 returns Value exactly.</summary>
	public static float Fbm( Vector2 q, uint seed, int octaves, float lacunarity, float gain )
	{
		CheckFbmArgs( octaves, lacunarity, gain );
		float sum = 0, ampSum = 0, amp = 1, freq = 1;
		for ( int i = 0; i < octaves; i++ )
		{
			sum += amp * Value( q * freq, seed );
			ampSum += amp;
			amp *= gain;
			freq *= lacunarity;
		}
		return sum / ampSum;
	}

	/// <summary>fBm in [-1, 1] at canvas position <paramref name="position"/> (px) under <paramref name="settings"/> (Scale, Octaves, Lacunarity, Gain, Seed).</summary>
	public static float FbmAt( Vector2 position, Particle2DNoiseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		return Fbm( position * settings.Scale, settings.Seed, settings.Octaves, settings.Lacunarity, settings.Gain );
	}

	/// <summary>Time-varying fBm in [-1, 1] at canvas position <paramref name="position"/> (px) and time <paramref name="time"/> (s). Each octave scales BOTH the spatial frequency and the time coordinate by Lacunarity, so finer octaves evolve faster — the field morphs coherently instead of sliding.</summary>
	public static float FbmAtTime( Vector2 position, float time, Particle2DNoiseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		if ( !float.IsFinite( time ) ) throw new ArgumentOutOfRangeException( nameof( time ), "Time must be finite." );
		Vector2 q = position * settings.Scale;
		float t = time * settings.TimeScale;
		float sum = 0, ampSum = 0, amp = 1, freq = 1;
		for ( int i = 0; i < settings.Octaves; i++ )
		{
			sum += amp * Value3( new Vector3( q.X * freq, q.Y * freq, t * freq ), settings.Seed );
			ampSum += amp;
			amp *= settings.Gain;
			freq *= settings.Lacunarity;
		}
		return sum / ampSum;
	}

	// --- Curl: central-difference gradient of a scalar potential, rotated 90° ---

	static Vector2 CurlOf( Func<Vector2, float> potential, Vector2 q )
	{
		float h = CurlStep;
		float gx = (potential( q + new Vector2( h, 0 ) ) - potential( q - new Vector2( h, 0 ) )) / (2f * h);
		float gy = (potential( q + new Vector2( 0, h ) ) - potential( q - new Vector2( 0, h ) )) / (2f * h);
		return new Vector2( -gy, gx );
	}

	/// <summary>Divergence-free-ish velocity direction at noise coordinate <paramref name="q"/>: the curl of the single-octave value-noise potential. Closest to the sim's built-in curl (same potential family, finite-difference vs analytic gradient) and the cheapest curl here (4 Value evaluations). No hard magnitude bound; see the header.</summary>
	public static Vector2 CurlValue( Vector2 q, uint seed ) => CurlOf( p => Value( p, seed ), q );

	/// <summary>Curl of the single-octave value potential at canvas position <paramref name="position"/> (px) under <paramref name="settings"/>.</summary>
	public static Vector2 CurlValueAt( Vector2 position, Particle2DNoiseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		return CurlValue( position * settings.Scale, settings.Seed );
	}

	/// <summary>Curl of the single-octave value potential at canvas position <paramref name="position"/> (px) and time <paramref name="time"/> (s) under <paramref name="settings"/>.</summary>
	public static Vector2 CurlValueAtTime( Vector2 position, float time, Particle2DNoiseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		if ( !float.IsFinite( time ) ) throw new ArgumentOutOfRangeException( nameof( time ), "Time must be finite." );
		Vector2 q = position * settings.Scale;
		float t = time * settings.TimeScale;
		return CurlOf( p => Value3( new Vector3( p.X, p.Y, t ), settings.Seed ), q );
	}

	/// <summary>Divergence-free-ish velocity direction at noise coordinate <paramref name="q"/>: the curl of the fBm potential (richer, multi-scale swirl than <see cref="CurlValue"/>). No hard magnitude bound; see the header for cost.</summary>
	public static Vector2 Curl( Vector2 q, uint seed, int octaves, float lacunarity, float gain )
	{
		CheckFbmArgs( octaves, lacunarity, gain );
		return CurlOf( p => Fbm( p, seed, octaves, lacunarity, gain ), q );
	}

	/// <summary>Curl of the fBm potential at canvas position <paramref name="position"/> (px) under <paramref name="settings"/>.</summary>
	public static Vector2 CurlAt( Vector2 position, Particle2DNoiseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		return Curl( position * settings.Scale, settings.Seed, settings.Octaves, settings.Lacunarity, settings.Gain );
	}

	/// <summary>Curl of the time-varying fBm potential at canvas position <paramref name="position"/> (px) and time <paramref name="time"/> (s) under <paramref name="settings"/>.</summary>
	public static Vector2 CurlAtTime( Vector2 position, float time, Particle2DNoiseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		if ( !float.IsFinite( time ) ) throw new ArgumentOutOfRangeException( nameof( time ), "Time must be finite." );
		Vector2 q = position * settings.Scale;
		float t = time * settings.TimeScale;
		return CurlOf( p => FbmAtTimeRaw( p, t, settings ), q );
	}

	static float FbmAtTimeRaw( Vector2 q, float t, Particle2DNoiseSettings settings )
	{
		float sum = 0, ampSum = 0, amp = 1, freq = 1;
		for ( int i = 0; i < settings.Octaves; i++ )
		{
			sum += amp * Value3( new Vector3( q.X * freq, q.Y * freq, t * freq ), settings.Seed );
			ampSum += amp;
			amp *= settings.Gain;
			freq *= settings.Lacunarity;
		}
		return sum / ampSum;
	}
}
