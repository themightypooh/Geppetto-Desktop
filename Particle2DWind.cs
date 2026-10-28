#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// Wave-6 lane P74 (particles session, 2026-10-24): time-varying 2D gust
// PROFILES — the shape of wind strength over time — as pure functions of
// time. Every profile returns a dimensionless strength multiplier in [0, 1]
// at time t (seconds). Profiles are DIRECTIONLESS: they say how strong the
// wind is, never which way it blows. Direction, zones, and force
// application belong to other lanes (see below), never here.
//
// WHAT THIS IS NOT (non-duplication, stated plainly):
// - This lane does NOT duplicate P12's Particle2DGustField (Particle2DFields.cs).
//   P12 owns the rectangular gust ZONE: a field that pushes particles along
//   a direction inside a rectangle, with a sinusoidal strength over time
//   (Base + Amplitude x sin(2π·freq·t + phase(seed))). Nothing here has a
//   rectangle, a direction, or a position: a profile is a function of time
//   alone, and can DRIVE any field — including P12's, by replacing its
//   sinusoid with a profile (exact lines for the coordinator sweep are in
//   the lane report; no file is edited by this lane to do it).
// - This lane does NOT duplicate P17's Particle2DNoise (Particle2DNoise.cs).
//   NoiseGust DELEGATES to it (FbmAtTime at a fixed position, mapped from
//   [-1, 1] to [0, 1]); no hash, lattice, or interpolation code is copied
//   here. P17's time field uses the 3D hash domain (time = third axis),
//   which is a different seed domain from P12's sinusoid phase.
// - OUT OF SCOPE: force application into the sim step, gust zones or
//   directions, 3D wind (P65), any wiring into Particle2D.cs, any GPU
//   code, any save/serialization, any canvas/UI. Authoring-only: no game
//   loop, play mode, live physics, input, or networking.
//
// UNITS AND CONVENTIONS (matching Particle2D / Particle2DFields):
// - Times are seconds (float). Ramps are LINEAR unless stated otherwise:
//   a linear ramp is the only shape whose mid-point is exactly 0.5, which
//   is what the tests pin.
// - All profiles are pure: same settings + same time = same value, always.
//   No System.Random anywhere; seeds are explicit uint fields.
// - Evaluation time t is the sim/elapsed time the caller passes in. A
//   finite negative t reads as "before the gust" (front/train return 0;
//   train wraps negatives into its period, so a train is periodic for
//   negative t too). A non-finite t throws — never NaN, never silent.
// - Settings objects ARE the profiles (sealed classes, matching the
//   Particle2DNoiseSettings / Particle2DSettings convention in this tree,
//   not structs): public fields set by object initializer, Validate() as
//   the loud gate, At(t) as the evaluator. Call Validate() once before
//   stepping, the way the sim validates emitters; At() re-checks the
//   time argument but trusts the settings (Validate() is the gate, not
//   a per-sample tax — except NoiseGust, whose delegate validates its
//   own settings on every call by P17's design).
//
// BOUNDARY CONTRACT (GustFront, Attack=A, Sustain=S, Decay=D, Total=T):
//   t < 0            -> 0            (before the gust)
//   t = 0            -> 0            (attack starts at rest, A > 0)
//   0 < t < A        -> t / A        (linear attack; mid-attack = 0.5)
//   t = A            -> 1            (attack reaches full strength)
//   A < t < A+S      -> 1            (sustain)
//   t = A+S          -> 1            (decay starts at full strength)
//   A+S < t < T      -> 1 - (t-A-S)/D (linear decay; mid-decay = 0.5)
//   t = T            -> 0            (gust over)
//   t > T            -> 0            (calm forever after)
//   Zero attack snaps on (At(0) = 1 when A = 0 and S > 0); zero decay
//   snaps off (value drops 1 -> 0 at t = A+S when D = 0). A front with
//   A = S = D = 0 has no shape at all and is REJECTED by Validate().
//   Zero individual phases are legal; only the all-zero total is not.
//
// TRAIN CONTRACT (GustTrain, Period P = A+S+D+Lull):
//   At(t) = Front(t mod P), wrapped so negative t lands in [0, P).
//   The lull interval [A+S+D, P) is exactly 0, including mid-lull.
//   A train with Lull = 0 AND Decay = 0 is REJECTED: with neither a decay
//   ramp nor a calm gap, successive gusts collide at the seam (the value
//   would snap 1 -> 0 at the period boundary — a pop, not a gust). Pick
//   one separation mechanism: a decay ramp (Lull = 0, Decay > 0) or a calm
//   gap (Decay = 0, Lull > 0) or both. (Sub-case worth knowing: if Attack
//   is ALSO 0, that rejected shape would be a constant 1 — the degenerate
//   case the rejection exists to forbid.)
//   Periodicity is exact in shape but float modulo rounding means
//   At(t) vs At(t + P) can differ by ~1e-7 for non-trivial t; tests use a
//   1e-6 tolerance for periodicity and exact values only at clean
//   boundary points.
//
// NOISE CONTRACT (NoiseGust):
//   At(t) = clamp((FbmAtTime(origin, t, noiseSettings) + 1) / 2, 0, 1),
//   where noiseSettings carries this profile's Seed / TimeScale / Octaves
//   / Lacunarity / Gain (Scale is fixed at 1 because the sample position
//   is the fixed origin — position never varies, so Scale cannot affect
//   the curve). P17 guarantees Fbm in [-1, 1]; the clamp is float-safety
//   only, against a last-ulp excursion past an endpoint, not a shape
//   change. TimeScale 0 freezes the field (a legal still mode, matching
//   P17): every t returns the same value. Cost note: one small settings
//   allocation per sample — profiles are sampled per step, not per
//   particle, so this is not a per-particle cost.
//
// COMBINER CONTRACT (CombinedGustProfile):
//   Sum:     clamp(a1 + a2 + ..., 0, 1). Stacking gusts can never exceed
//            full strength; the clamp is the documented behaviour, and
//            the tests pin it (1 + 1 -> 1, not 2).
//   Product: a1 * a2 * ... with NO clamp. Inputs are contractually in
//            [0, 1] (every profile in this file guarantees it), so the
//            product is in [0, 1] by construction; a child that breaks
//            its contract is that child's bug and is not masked here.
//            Product is the gating rule: multiply a gust by a lull
//            envelope and the gust is silenced exactly where the
//            envelope is 0, untouched where it is 1.

/// <summary>A time-varying gust profile: a pure function of time returning a dimensionless strength multiplier in [0, 1]. Directionless by design.</summary>
public interface IGustProfile
{
	/// <summary>Strength multiplier at time <paramref name="time"/> (seconds), in [0, 1]. Throws on non-finite time.</summary>
	float At( float time );
	/// <summary>Throws (ArgumentException family) on any meaningless configuration. Call once before stepping.</summary>
	void Validate();
}

static class GustProfileMath
{
	public static void RequireFiniteNonNegative( float value, string what )
	{
		if ( !float.IsFinite( value ) || value < 0 )
			throw new ArgumentOutOfRangeException( what, $"{what} must be finite and >= 0." );
	}

	public static void RequireFiniteTime( float time )
	{
		if ( !float.IsFinite( time ) )
			throw new ArgumentOutOfRangeException( nameof( time ), "Time must be finite." );
	}

	/// <summary>Shared front shape (see the file header for the boundary table). Callers validate first; zero phases snap, they never divide by zero.</summary>
	public static float FrontValue( float t, float attack, float sustain, float decay )
	{
		if ( t < 0 ) return 0;
		if ( t < attack ) return attack <= 0 ? 1 : t / attack;
		if ( t < attack + sustain ) return 1;
		float total = attack + sustain + decay;
		if ( t < total ) return decay <= 0 ? 0 : 1 - (t - attack - sustain) / decay;
		return 0;
	}

	public static void ValidateFrontTimes( float attack, float sustain, float decay, string what )
	{
		RequireFiniteNonNegative( attack, $"{what} AttackSeconds" );
		RequireFiniteNonNegative( sustain, $"{what} SustainSeconds" );
		RequireFiniteNonNegative( decay, $"{what} DecaySeconds" );
		if ( attack + sustain + decay <= 0 )
			throw new ArgumentException( $"{what} has no shape: AttackSeconds + SustainSeconds + DecaySeconds must be > 0 (an all-zero gust is a configuration error, not a no-op)." );
	}
}

/// <summary>A single passing gust: linear attack ramp 0 -> 1, sustain at 1, linear decay 1 -> 0, then 0 forever. See the file header for the exact boundary table.</summary>
public sealed class GustFrontProfile : IGustProfile
{
	/// <summary>Seconds to ramp from 0 to 1. 0 = snaps on at t = 0. Must be finite and >= 0.</summary>
	public float AttackSeconds = 0.5f;
	/// <summary>Seconds held at 1. Must be finite and >= 0.</summary>
	public float SustainSeconds = 1f;
	/// <summary>Seconds to decay from 1 to 0. 0 = snaps off at the end of sustain. Must be finite and >= 0.</summary>
	public float DecaySeconds = 0.5f;

	/// <summary>Total gust length in seconds (attack + sustain + decay).</summary>
	public float Duration => AttackSeconds + SustainSeconds + DecaySeconds;

	public void Validate() => GustProfileMath.ValidateFrontTimes( AttackSeconds, SustainSeconds, DecaySeconds, "GustFront" );

	public float At( float time )
	{
		GustProfileMath.RequireFiniteTime( time );
		return GustProfileMath.FrontValue( time, AttackSeconds, SustainSeconds, DecaySeconds );
	}
}

/// <summary>A GustFront repeated forever: each period is attack + sustain + decay + lull, where the lull is a calm gap of exact 0 between gusts. See the file header for the rejection rule (no lull AND no decay).</summary>
public sealed class GustTrainProfile : IGustProfile
{
	/// <summary>Seconds to ramp from 0 to 1. 0 = snaps on. Must be finite and >= 0.</summary>
	public float AttackSeconds = 0.5f;
	/// <summary>Seconds held at 1. Must be finite and >= 0.</summary>
	public float SustainSeconds = 1f;
	/// <summary>Seconds to decay from 1 to 0. 0 = snaps off. Must be finite and >= 0.</summary>
	public float DecaySeconds = 0.5f;
	/// <summary>Calm gap in seconds between gusts, exactly 0 throughout. Must be finite and >= 0.</summary>
	public float LullSeconds = 1f;

	/// <summary>Full cycle length in seconds (attack + sustain + decay + lull).</summary>
	public float Period => AttackSeconds + SustainSeconds + DecaySeconds + LullSeconds;

	public void Validate()
	{
		GustProfileMath.ValidateFrontTimes( AttackSeconds, SustainSeconds, DecaySeconds, "GustTrain" );
		GustProfileMath.RequireFiniteNonNegative( LullSeconds, "GustTrain LullSeconds" );
		if ( LullSeconds == 0 && DecaySeconds == 0 )
			throw new ArgumentException( "GustTrain with LullSeconds = 0 and DecaySeconds = 0 is rejected: with neither a decay ramp nor a calm gap, successive gusts collide at the seam (and with AttackSeconds = 0 too it would be a constant 1). Give the train a decay ramp, a lull gap, or both." );
	}

	public float At( float time )
	{
		GustProfileMath.RequireFiniteTime( time );
		float period = Period;
		if ( !(period > 0) ) return 0;
		float local = time % period;
		if ( local < 0 ) local += period;
		return GustProfileMath.FrontValue( local, AttackSeconds, SustainSeconds, DecaySeconds );
	}
}

/// <summary>A seeded, smoothly-varying multiplier in [0, 1], delegating to P17's Particle2DNoise (FbmAtTime at a fixed origin, mapped from [-1, 1] to [0, 1] and clamped for float safety). No noise code is duplicated here. Continuous in time (smoothstep fade, zero derivative at cell boundaries — no popping), deterministic for a fixed seed, different across seeds.</summary>
public sealed class NoiseGustProfile : IGustProfile
{
	/// <summary>Lattice seed. Any uint; different seeds give unrelated curves.</summary>
	public uint Seed = 1;
	/// <summary>Noise time-axis speed, noise units per second. 0 freezes the field (a legal still mode). Must be finite and >= 0. Default 0.5 matches P17 (one time cell per 2 s).</summary>
	public float TimeScale = 0.5f;
	/// <summary>fBm octave count. Must be >= 1. Default 4 matches P17.</summary>
	public int Octaves = 4;
	/// <summary>Frequency multiplier per octave. Must be finite and > 1. Default 2 matches P17.</summary>
	public float Lacunarity = 2f;
	/// <summary>Amplitude multiplier per octave. Must be finite and in (0, 1). Default 0.5 matches P17.</summary>
	public float Gain = 0.5f;

	public void Validate()
	{
		GustProfileMath.RequireFiniteNonNegative( TimeScale, "NoiseGust TimeScale" );
		if ( Octaves <= 0 ) throw new ArgumentOutOfRangeException( nameof( Octaves ), "NoiseGust Octaves must be positive." );
		if ( !float.IsFinite( Lacunarity ) || Lacunarity <= 1 ) throw new ArgumentOutOfRangeException( nameof( Lacunarity ), "NoiseGust Lacunarity must be finite and > 1." );
		if ( !float.IsFinite( Gain ) || Gain <= 0 || Gain >= 1 ) throw new ArgumentOutOfRangeException( nameof( Gain ), "NoiseGust Gain must be finite and in (0, 1)." );
	}

	Particle2DNoiseSettings NoiseSettings() => new()
	{
		Seed = Seed,
		Scale = 1f,
		TimeScale = TimeScale,
		Octaves = Octaves,
		Lacunarity = Lacunarity,
		Gain = Gain,
	};

	public float At( float time )
	{
		GustProfileMath.RequireFiniteTime( time );
		float v = Particle2DNoise.FbmAtTime( Vector2.Zero, time, NoiseSettings() );
		return Math.Clamp( (v + 1f) / 2f, 0f, 1f );
	}
}

/// <summary>How a <see cref="CombinedGustProfile"/> combines its children.</summary>
public enum GustCombineMode
{
	/// <summary>Sum, clamped to [0, 1]. Stacking gusts never exceeds full strength.</summary>
	Sum,
	/// <summary>Product, no clamp (inputs are contractually in [0, 1], so the product is too). The gating rule: a gust times a lull envelope.</summary>
	Product,
}

/// <summary>Combines any list of profiles under one rule (see <see cref="GustCombineMode"/>). Mirrors P12's combined-field conventions: Validate() rejects an empty list and any null or invalid entry; At() on an empty list returns 0 by design (call Validate() to catch that) and throws on a null entry.</summary>
public sealed class CombinedGustProfile : IGustProfile
{
	public IReadOnlyList<IGustProfile> Profiles { get; }
	/// <summary>Combination rule. Default Sum (clamped).</summary>
	public GustCombineMode Mode = GustCombineMode.Sum;

	public CombinedGustProfile( IEnumerable<IGustProfile> profiles )
	{
		ArgumentNullException.ThrowIfNull( profiles );
		Profiles = new List<IGustProfile>( profiles );
	}

	/// <summary>Builds a combined profile from a list (params form allows Combine(mode, a, b, c)).</summary>
	public static CombinedGustProfile Combine( GustCombineMode mode, params IGustProfile[] profiles )
	{
		var combined = new CombinedGustProfile( profiles );
		combined.Mode = mode;
		return combined;
	}

	public void Validate()
	{
		if ( Profiles.Count == 0 )
			throw new ArgumentException( "Combined gust profile list must not be empty: a combined profile with nothing to combine is a configuration error, not a no-op." );
		for ( int i = 0; i < Profiles.Count; i++ )
		{
			var p = Profiles[i] ?? throw new ArgumentException( $"Combined gust profile entry {i} is null." );
			p.Validate();
		}
	}

	public float At( float time )
	{
		GustProfileMath.RequireFiniteTime( time );
		if ( Profiles.Count == 0 ) return 0;
		switch ( Mode )
		{
			case GustCombineMode.Sum:
			{
				float sum = 0;
				for ( int i = 0; i < Profiles.Count; i++ )
					sum += (Profiles[i] ?? throw new ArgumentException( $"Combined gust profile entry {i} is null." )).At( time );
				return Math.Clamp( sum, 0f, 1f );
			}
			case GustCombineMode.Product:
			{
				float product = 1;
				for ( int i = 0; i < Profiles.Count; i++ )
					product *= (Profiles[i] ?? throw new ArgumentException( $"Combined gust profile entry {i} is null." )).At( time );
				return product;
			}
			default: throw new ArgumentException( $"Unknown combine mode {Mode}." );
		}
	}
}
