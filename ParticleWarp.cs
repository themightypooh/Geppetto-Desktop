// Lane P68 (particles session, 2026-10-24): generic 3D domain warp.
// Pure functions of position. Units: kernel inches, Z-up, seconds.
// Never System.Random anywhere in this file.
//
// WHAT THIS IS — AND WHAT IT IS NOT:
//   This is a generic, reusable domain-warp TRANSFORM that works on ANY
//   vector field (or scalar field): evaluate a base field at a warped
//   position p' = p + amplitude * warpField(p * warpFrequency), where
//   warpField itself is any Vector3 -> Vector3 function. Amplitude is
//   in inches (a displacement scale); WarpFrequency is in 1/inch and
//   scales ONLY the warp field's input, never the base field's.
//   Chained warp (warp-of-warp, one amplitude per stage) is a
//   first-class operation here.
//   This is NOT lane P29's WarpedCurl, and it does not copy, re-skin,
//   or reimplement it. P29's WarpedCurl (ParticleNoise.cs) is a single
//   noise kernel: a first CurlA field (fixed seed-XOR split) displaces
//   the sample position of a second, fBm field, in one stage only.
//   This file contains NO noise kernel, NO curl construction, and NO
//   lattice/hash code at all: the warp field is supplied by the caller
//   as a delegate, so the same transform warps a linear field, a
//   sinusoidal field, a P29 curl field, or anything else, and chains
//   to any depth. The two convenience helpers at the bottom delegate
//   to ParticleNoise.CurlA / CurlFbm; they add no noise code of their
//   own and exist only because ParticleNoise.cs is present.
// OUT OF SCOPE (stated, not smuggled in): new noise kernels (P29),
// curl itself (lane 3 / P29), force application, any wiring into
// ParticleSystem.Integrate, any GPU code, any save/serialization,
// any canvas/UI. Authoring-only helpers: no game loop, play mode,
// live physics, input, or networking.

using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Parameters for <see cref="ParticleWarp"/>. Plain data;
/// <see cref="Validate"/> is the loud-failure gate, and every entry
/// point below calls it (or its equivalent checks) before sampling.
/// Amplitude is in inches; WarpFrequency is in 1/inch and is applied
/// to the warp field's input only.</summary>
public struct ParticleWarpSettings
{
	/// <summary>Single-stage displacement scale, inches. 0 = identity
	/// (the warped field equals the base field at every point).</summary>
	public float Amplitude;
	/// <summary>Frequency scale applied to the warp field's input:
	/// the warp field is sampled at p * WarpFrequency. 1 = as supplied.</summary>
	public float WarpFrequency;
	/// <summary>Per-stage amplitudes for <see cref="ParticleWarp.WarpChain"/>
	/// (one entry per stage, applied in order). Null means "no chain
	/// configured"; the chain methods reject a null or empty array
	/// loudly rather than silently returning the base field.</summary>
	public float[] StageAmplitudes;

	/// <summary>Loud failure: throws on any invalid value, never
	/// silently clamps or substitutes a default.</summary>
	public void Validate()
	{
		if ( !float.IsFinite( Amplitude ) || Amplitude < 0f )
			throw new ArgumentOutOfRangeException( nameof( Amplitude ), "Amplitude must be finite and >= 0." );
		if ( !float.IsFinite( WarpFrequency ) || WarpFrequency < 0f )
			throw new ArgumentOutOfRangeException( nameof( WarpFrequency ), "WarpFrequency must be finite and >= 0." );
		if ( StageAmplitudes is not null )
		{
			for ( int i = 0; i < StageAmplitudes.Length; i++ )
				if ( !float.IsFinite( StageAmplitudes[i] ) || StageAmplitudes[i] < 0f )
					throw new ArgumentOutOfRangeException( nameof( StageAmplitudes ), $"Stage amplitude {i} must be finite and >= 0." );
		}
	}
}

/// <summary>Generic domain warp for 3D vector and scalar fields.
/// Every method is a pure function of its inputs; no state, no RNG.
/// The warp is a position transform, not a field: the base field is
/// never modified, only sampled at a displaced position.</summary>
public static class ParticleWarp
{
	static void CheckAmplitude( float amplitude )
	{
		if ( !float.IsFinite( amplitude ) || amplitude < 0f )
			throw new ArgumentOutOfRangeException( nameof( amplitude ), "Amplitude must be finite and >= 0." );
	}

	static void CheckFrequency( float warpFrequency )
	{
		if ( !float.IsFinite( warpFrequency ) || warpFrequency < 0f )
			throw new ArgumentOutOfRangeException( nameof( warpFrequency ), "WarpFrequency must be finite and >= 0." );
	}

	static float[] CheckStages( float[] stageAmplitudes )
	{
		ArgumentNullException.ThrowIfNull( stageAmplitudes );
		if ( stageAmplitudes.Length == 0 )
			throw new ArgumentException( "A chain needs at least one stage amplitude.", nameof( stageAmplitudes ) );
		for ( int i = 0; i < stageAmplitudes.Length; i++ )
			CheckAmplitude( stageAmplitudes[i] );
		return stageAmplitudes;
	}

	/// <summary>The warped position itself: p + amplitude * warpField(
	/// p * warpFrequency). Amplitude is in inches; warpFrequency scales
	/// only the warp field's input. Amplitude 0 returns p exactly.</summary>
	public static Vector3 WarpPosition( Vector3 p, Func<Vector3, Vector3> warpField, float amplitude, float warpFrequency )
	{
		ArgumentNullException.ThrowIfNull( warpField );
		CheckAmplitude( amplitude );
		CheckFrequency( warpFrequency );
		return p + amplitude * warpField( p * warpFrequency );
	}

	/// <summary><see cref="WarpPosition"/> from a settings struct.</summary>
	public static Vector3 WarpPosition( Vector3 p, Func<Vector3, Vector3> warpField, ParticleWarpSettings settings )
	{
		settings.Validate();
		return WarpPosition( p, warpField, settings.Amplitude, settings.WarpFrequency );
	}

	/// <summary>Evaluate any vector field at the warped position:
	/// baseField( p + amplitude * warpField( p * warpFrequency ) ).</summary>
	public static Vector3 WarpField( Vector3 p, Func<Vector3, Vector3> baseField, Func<Vector3, Vector3> warpField, float amplitude, float warpFrequency )
	{
		ArgumentNullException.ThrowIfNull( baseField );
		return baseField( WarpPosition( p, warpField, amplitude, warpFrequency ) );
	}

	/// <summary><see cref="WarpField"/> from a settings struct.</summary>
	public static Vector3 WarpField( Vector3 p, Func<Vector3, Vector3> baseField, Func<Vector3, Vector3> warpField, ParticleWarpSettings settings )
	{
		settings.Validate();
		return WarpField( p, baseField, warpField, settings.Amplitude, settings.WarpFrequency );
	}

	/// <summary>Scalar-field variant: the same warped position, applied
	/// to a Vector3 -> float field (e.g. a density or potential field).</summary>
	public static float WarpScalar( Vector3 p, Func<Vector3, float> scalarField, Func<Vector3, Vector3> warpField, float amplitude, float warpFrequency )
	{
		ArgumentNullException.ThrowIfNull( scalarField );
		return scalarField( WarpPosition( p, warpField, amplitude, warpFrequency ) );
	}

	/// <summary><see cref="WarpScalar"/> from a settings struct.</summary>
	public static float WarpScalar( Vector3 p, Func<Vector3, float> scalarField, Func<Vector3, Vector3> warpField, ParticleWarpSettings settings )
	{
		settings.Validate();
		return WarpScalar( p, scalarField, warpField, settings.Amplitude, settings.WarpFrequency );
	}

	/// <summary>Chained warp position (warp-of-warp): start at p and
	/// apply the single-stage warp once per stage amplitude, in order,
	/// each stage's warp field sampled at the ALREADY-warped position
	/// (stage i+1 displaces p_i, it does not re-displace the original
	/// p). This is the piece P29 does not have: P29 is single-stage.
	/// Two stages of amplitude a equal one stage of amplitude 2a ONLY
	/// when the warp field is constant; for varying fields the chain
	/// and the summed stage differ, by construction — see the tests.</summary>
	public static Vector3 WarpChainPosition( Vector3 p, Func<Vector3, Vector3> warpField, float[] stageAmplitudes, float warpFrequency )
	{
		CheckStages( stageAmplitudes );
		CheckFrequency( warpFrequency );
		ArgumentNullException.ThrowIfNull( warpField );
		var pos = p;
		for ( int i = 0; i < stageAmplitudes.Length; i++ )
			pos = WarpPosition( pos, warpField, stageAmplitudes[i], warpFrequency );
		return pos;
	}

	/// <summary><see cref="WarpChainPosition"/> from a settings struct,
	/// using its <see cref="ParticleWarpSettings.StageAmplitudes"/> and
	/// <see cref="ParticleWarpSettings.WarpFrequency"/>.</summary>
	public static Vector3 WarpChainPosition( Vector3 p, Func<Vector3, Vector3> warpField, ParticleWarpSettings settings )
	{
		settings.Validate();
		return WarpChainPosition( p, warpField, CheckStages( settings.StageAmplitudes ), settings.WarpFrequency );
	}

	/// <summary>Evaluate any vector field at the chained-warp position.</summary>
	public static Vector3 WarpChain( Vector3 p, Func<Vector3, Vector3> baseField, Func<Vector3, Vector3> warpField, float[] stageAmplitudes, float warpFrequency )
	{
		ArgumentNullException.ThrowIfNull( baseField );
		return baseField( WarpChainPosition( p, warpField, stageAmplitudes, warpFrequency ) );
	}

	/// <summary><see cref="WarpChain"/> from a settings struct.</summary>
	public static Vector3 WarpChain( Vector3 p, Func<Vector3, Vector3> baseField, Func<Vector3, Vector3> warpField, ParticleWarpSettings settings )
	{
		settings.Validate();
		return WarpChain( p, baseField, warpField, settings.StageAmplitudes, settings.WarpFrequency );
	}

	/// <summary>Evaluate any scalar field at the chained-warp position.</summary>
	public static float WarpScalarChain( Vector3 p, Func<Vector3, float> scalarField, Func<Vector3, Vector3> warpField, float[] stageAmplitudes, float warpFrequency )
	{
		ArgumentNullException.ThrowIfNull( scalarField );
		return scalarField( WarpChainPosition( p, warpField, stageAmplitudes, warpFrequency ) );
	}

	/// <summary><see cref="WarpScalarChain"/> from a settings struct.</summary>
	public static float WarpScalarChain( Vector3 p, Func<Vector3, float> scalarField, Func<Vector3, Vector3> warpField, ParticleWarpSettings settings )
	{
		settings.Validate();
		return WarpScalarChain( p, scalarField, warpField, settings.StageAmplitudes, settings.WarpFrequency );
	}

	// Convenience helpers. These exist ONLY because ParticleNoise.cs is
	// present in the canonical tree at this lane's check time, and they
	// DELEGATE to it — no noise, lattice, hash, or curl code lives here.
	// Difference from P29's WarpedCurl, stated plainly: WarpedCurl warps
	// with CurlA at a seed-XOR split and a single stage into CurlFbm;
	// these helpers warp with CurlA at the SAME config (same seed, no
	// XOR split), through the generic single-stage transform above, and
	// the caller chains them via WarpChain if a second stage is wanted.
	// Note the frequency bookkeeping: the warp delegate below is
	// q => CurlA(q, cfg), and CurlA itself scales its input by
	// cfg.Scale, so the effective warp lattice frequency is
	// settings.WarpFrequency * cfg.Scale. Pass WarpFrequency = 1 to
	// warp at exactly cfg.Scale.

	/// <summary>Warp the P29 CurlA field by itself (same config for base
	/// and warp), via the generic transform. Amplitude 0 returns
	/// exactly CurlA(p, cfg).</summary>
	public static Vector3 WarpCurlA( Vector3 p, ParticleNoiseConfig cfg, ParticleWarpSettings settings )
	{
		ArgumentNullException.ThrowIfNull( cfg );
		cfg.Validate();
		return WarpField( p, q => ParticleNoise.CurlA( q, cfg ), q => ParticleNoise.CurlA( q, cfg ), settings );
	}

	/// <summary>Warp the P29 CurlFbm field by CurlA (same config),
	/// via the generic transform. Amplitude 0 returns exactly
	/// CurlFbm(p, cfg).</summary>
	public static Vector3 WarpCurlFbm( Vector3 p, ParticleNoiseConfig cfg, ParticleWarpSettings settings )
	{
		ArgumentNullException.ThrowIfNull( cfg );
		cfg.Validate();
		return WarpField( p, q => ParticleNoise.CurlFbm( q, cfg ), q => ParticleNoise.CurlA( q, cfg ), settings );
	}
}
