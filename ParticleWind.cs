#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// Wave-6 lane P65 (particles session, 2026-10-24): time-varying 3D wind
// GUST PROFILES — pure functions of time returning a wind VECTOR (in/s).
//
// WHAT THIS IS (and the one mechanism it feeds):
// A profile answers "what is the wind, right now?" — nothing else. The
// returned vector is meant to be fed, per step, into the sim's existing
// Wind input (ParticleSystemSettings.Wind), where the sim's drag rule
// already does the physics — semi-implicit linear drag toward the wind
// velocity (ParticleSystem.Integrate):
//     vel = (vel + (acc + Wind * Drag) * h) / (1 + Drag * h)
// Gusts are WIND VALUES, never a second acceleration path. This file
// contains no force application, no per-particle state, and no stepping:
// the caller evaluates a profile at the current sim time and assigns the
// result to Settings.Wind before Step(). Exact integration lines for the
// coordinator sweep are in the lane report; no existing file is edited
// by this lane to do it.
//
// WHAT THIS IS NOT (non-duplication, stated plainly):
// - This lane does NOT duplicate P22's ParticleWindZoneField
//   (ParticleForceFields.cs). P22 owns the SPATIAL wind zone: an
//   acceleration applied inside a box, as a function of position. Nothing
//   here has a box, a position, or an acceleration: a profile is a
//   function of time alone, and can DRIVE any wind input — including a
//   zone's strength, though the sweep's job is the sim's Wind input.
// - This lane does NOT duplicate P74's Particle2DWind.cs. P74 profiles
//   are DIRECTIONLESS strength multipliers in [0, 1] for 2D (Y-down
//   pixels). These profiles return full 3D wind vectors in kernel
//   inches/second, Z-up, with their own direction per profile. Profile
//   SHAPES are mirrored where the brief asks for them (sine gust, burst
//   envelope, multi-sine flutter) but every formula here is written in
//   3D vector units and no P74 code is copied: P74's front/train/noise
//   shapes, its [0, 1] clamping, and its negative-time wrapping are all
//   deliberately NOT carried over (see the negative-time rule below).
// - OUT OF SCOPE: force-field acceleration paths, spatial wind zones
//   (P22), 2D gust profiles (P74), GPU code, save/serialization,
//   live-cabinet checks. Authoring-only: no game loop, play mode, live
//   physics, input, or networking.
//
// UNITS AND CONVENTIONS:
// - Times are seconds (float). Wind vectors are inches/second, Z-up.
// - All profiles are pure: same settings + same time = same vector,
//   always, bit-identical. No System.Random anywhere; phases are
//   explicit inputs (see the sine profile), never seeded randomness.
// - Settings objects ARE the profiles (sealed classes, matching the
//   ParticleSystemSettings / Particle2DWind convention in this tree):
//   public fields set by object initializer, Validate() as the loud
//   gate, At(t) as the evaluator. At() re-validates on every call:
//   profiles are sampled once per step, never per particle, so the
//   check is not a per-particle tax, and it guarantees loud failure
//   even if a caller skipped Validate().
// - NEGATIVE TIME IS REJECTED, loudly (ArgumentOutOfRangeException),
//   by every profile. This is a deliberate departure from P74, which
//   reads negative t as "before the gust" / wraps it into a period.
//   Here t is the sim clock, and the sim clock never runs backward:
//   silently clamping or wrapping a negative time would hide a clock
//   bug. A non-finite time throws ArgumentException, never NaN.
// - A gust needs a direction: a zero (or near-zero, LengthSquared <
//   1e-12, the tree's vortex-axis threshold) Direction is rejected.
//   There is no "directionless" mode in this file; P74 owns that shape.
//
// PROFILE FORMULAS (exact — the tests pin these values):
//
// 1. WindSineProfile — steady wind + sinusoidal gust along one axis:
//        W(t) = BaseWind + d * Amplitude * sin(2*PI*FrequencyHz*t + PhaseRadians)
//     where d = Direction / |Direction| (unit vector). BaseWind is the
//     steady wind vector (it carries its own direction); the gust
//     oscillates along Direction only. PhaseRadians is an EXPLICIT
//     input, default 0 — no RNG is involved at all, seeded or otherwise.
//     Hand pins (Base (10,0,0), d (0,1,0), Amplitude 5, Frequency 1 Hz,
//     Phase 0): t=0 -> (10,0,0); t=0.25 (quarter period) -> (10,5,0);
//     t=0.5 (half period) -> (10,0,0); t=0.75 -> (10,-5,0).
//
// 2. WindBurstProfile — repeating smooth bursts from base to peak:
//        Period = AttackSeconds + SustainSeconds + DecaySeconds + LullSeconds
//        local  = t mod Period  (t >= 0, so local is in [0, Period))
//        envelope e(local):
//          attack  [0, A)          : e = 0.5 * (1 - cos(PI * local / A))
//          sustain [A, A+S)        : e = 1
//          decay   [A+S, A+S+D)    : e = 0.5 * (1 + cos(PI * (local-A-S) / D))
//          lull    [A+S+D, Period) : e = 0
//        W(t) = BaseWind + (PeakWind - BaseWind) * e(local)
//     Raised-cosine attack/decay (not linear, not smoothstep): the
//     envelope is C1-smooth at every joint — attack starts at 0 with
//     zero slope, reaches exactly 1 at local = A; decay starts at
//     exactly 1 and reaches exactly 0 at local = A+S+D. Continuity is
//     a VALIDATED contract, not an accident: AttackSeconds > 0 and
//     DecaySeconds > 0 are REQUIRED (a zero attack would snap from
//     base to peak at the burst start; a zero decay would snap from
//     peak to base at the sustain end). SustainSeconds >= 0 and
//     LullSeconds >= 0 (back-to-back bursts, Lull = 0, stay continuous
//     because decay already returned the envelope to base).
//     Hand pins (Base (2,0,0), Peak (12,0,0), A=1, S=1, D=1, Lull=1,
//     Period=4): t=0 -> (2,0,0); t=0.5 (mid-attack) -> (7,0,0);
//     t=1 (attack end) -> (12,0,0); t=1.5 (mid-sustain) -> (12,0,0);
//     t=2.5 (mid-decay) -> (7,0,0); t=3 (decay end) -> (2,0,0);
//     t=3.5 (mid-lull) -> (2,0,0); t=4.5 -> (7,0,0) (periodicity).
//
// 3. WindFlutterProfile — turbulent flutter as a sum of sines:
//        W(t) = BaseWind + d * SUM_i ( Amplitudes[i]
//                                   * sin(2*PI*FrequenciesHz[i]*t + PhasesRadians[i]) )
//     where d = Direction / |Direction|. Components are parallel
//     arrays (same length, at least one); defaults are three
//     components — amplitudes (3, 2, 1) in/s, frequencies
//     (1.3, 2.9, 4.7) Hz, phases (0, 1.1, 2.2) rad — chosen to be
//     mutually incommensurate so the sum looks irregular while staying
//     exactly deterministic. Bound (triangle inequality, |sin| <= 1,
//     |d| = 1): |W(t) - BaseWind| <= SUM_i Amplitudes[i], hence
//     |W(t)| <= |BaseWind| + SUM_i Amplitudes[i] for every t. The
//     tests prove the bound over a sampled range (t in [0, 10] s,
//     step 0.001 s) and prove determinism (same t -> bit-identical
//     vector on repeat calls).
//
// COMPOSITION RULE (CombinedWindProfile): profiles combine by SUM,
// exactly, with NO clamp. At(t) = W1(t) + W2(t) + ... in list order.
// A wind vector has no [0, 1] ceiling to clamp to (that was P74's
// multiplier world, not this one), and clamping would break the exact
// sum the tests pin: combined.At(t) == a.At(t) + b.At(t), bit-identical,
// because accumulation starts from the exact zero vector. Validate()
// rejects an empty list and any null or invalid entry; At() on an
// empty list returns Vector3.Zero by design (call Validate() to catch
// that) and throws on a null entry.

/// <summary>A time-varying 3D wind profile: a pure function of time returning a wind vector in inches/second, Z-up. Throws on negative or non-finite time.</summary>
public interface IParticleWindProfile
{
	/// <summary>Wind vector at time <paramref name="time"/> (seconds), in inches/second. Throws on negative or non-finite time, and on any invalid configuration.</summary>
	Vector3 At( float time );
	/// <summary>Throws (ArgumentException family) on any meaningless configuration. Call once before stepping; At() re-validates anyway.</summary>
	void Validate();
}

static class WindProfileMath
{
	public static void RequireFinite( Vector3 value, string what )
	{
		if ( !float.IsFinite( value.X ) || !float.IsFinite( value.Y ) || !float.IsFinite( value.Z ) )
			throw new ArgumentException( $"{what} must be finite." );
	}

	public static void RequireFinite( float value, string what )
	{
		if ( !float.IsFinite( value ) )
			throw new ArgumentException( $"{what} must be finite." );
	}

	public static void RequireTime( float time )
	{
		if ( !float.IsFinite( time ) )
			throw new ArgumentException( "Time must be finite." );
		if ( time < 0 )
			throw new ArgumentOutOfRangeException( nameof( time ), "Time must be >= 0: the sim clock never runs backward; negative time is a caller error, not 'before the gust'." );
	}

	public static Vector3 RequireDirection( Vector3 direction, string what )
	{
		RequireFinite( direction, what );
		if ( direction.LengthSquared() < 1e-12f )
			throw new ArgumentException( $"{what} must be non-zero: a gust needs a direction." );
		return Vector3.Normalize( direction );
	}
}

/// <summary>Steady wind plus a sinusoidal gust along a fixed direction: W(t) = BaseWind + d * Amplitude * sin(2*PI*FrequencyHz*t + PhaseRadians). Phase is an explicit input; no RNG is used.</summary>
public sealed class WindSineProfile : IParticleWindProfile
{
	/// <summary>Steady wind vector, inches/second. Finite; may be zero (pure gust).</summary>
	public Vector3 BaseWind;
	/// <summary>Gust direction; normalized at evaluation. Finite and non-zero (a gust needs a direction).</summary>
	public Vector3 Direction = new( 1, 0, 0 );
	/// <summary>Gust amplitude, inches/second. Finite and >= 0. Zero = steady BaseWind only.</summary>
	public float Amplitude;
	/// <summary>Gust frequency, Hz. Finite and > 0 (validated even at zero amplitude).</summary>
	public float FrequencyHz = 1f;
	/// <summary>Gust phase, radians. Finite. Explicit input — no seeded RNG, no System.Random.</summary>
	public float PhaseRadians;

	public void Validate()
	{
		WindProfileMath.RequireFinite( BaseWind, "WindSineProfile BaseWind" );
		WindProfileMath.RequireDirection( Direction, "WindSineProfile Direction" );
		WindProfileMath.RequireFinite( Amplitude, "WindSineProfile Amplitude" );
		if ( Amplitude < 0 )
			throw new ArgumentOutOfRangeException( nameof( Amplitude ), "WindSineProfile Amplitude must be >= 0." );
		WindProfileMath.RequireFinite( FrequencyHz, "WindSineProfile FrequencyHz" );
		if ( FrequencyHz <= 0 )
			throw new ArgumentOutOfRangeException( nameof( FrequencyHz ), "WindSineProfile FrequencyHz must be > 0." );
		WindProfileMath.RequireFinite( PhaseRadians, "WindSineProfile PhaseRadians" );
	}

	public Vector3 At( float time )
	{
		WindProfileMath.RequireTime( time );
		Validate();
		Vector3 d = Vector3.Normalize( Direction );
		return BaseWind + d * (Amplitude * MathF.Sin( 2f * MathF.PI * FrequencyHz * time + PhaseRadians ));
	}
}

/// <summary>Repeating smooth gust bursts: raised-cosine attack, flat sustain, raised-cosine decay, calm lull, then the next burst. W(t) = BaseWind + (PeakWind - BaseWind) * e(t mod Period). Attack and decay must be > 0 so the envelope is continuous — no jumps.</summary>
public sealed class WindBurstProfile : IParticleWindProfile
{
	/// <summary>Wind vector outside bursts (and at burst start/end), inches/second. Finite.</summary>
	public Vector3 BaseWind;
	/// <summary>Wind vector at full burst (attack end through decay start), inches/second. Finite. May point elsewhere than BaseWind; the burst blends vectors, not just magnitudes.</summary>
	public Vector3 PeakWind = new( 10, 0, 0 );
	/// <summary>Seconds to swell from base to peak (raised cosine). Finite and > 0 — required for continuity.</summary>
	public float AttackSeconds = 0.5f;
	/// <summary>Seconds held at peak. Finite and >= 0.</summary>
	public float SustainSeconds = 1f;
	/// <summary>Seconds to fall from peak back to base (raised cosine). Finite and > 0 — required for continuity.</summary>
	public float DecaySeconds = 0.5f;
	/// <summary>Calm gap in seconds between bursts, exactly base throughout. Finite and >= 0. Zero = back-to-back bursts (still continuous, decay already returned to base).</summary>
	public float LullSeconds = 1f;

	/// <summary>Full cycle length in seconds (attack + sustain + decay + lull).</summary>
	public float Period => AttackSeconds + SustainSeconds + DecaySeconds + LullSeconds;

	/// <summary>Full burst length in seconds (attack + sustain + decay), excluding the lull.</summary>
	public float Duration => AttackSeconds + SustainSeconds + DecaySeconds;

	public void Validate()
	{
		WindProfileMath.RequireFinite( BaseWind, "WindBurstProfile BaseWind" );
		WindProfileMath.RequireFinite( PeakWind, "WindBurstProfile PeakWind" );
		WindProfileMath.RequireFinite( AttackSeconds, "WindBurstProfile AttackSeconds" );
		if ( AttackSeconds <= 0 )
			throw new ArgumentOutOfRangeException( nameof( AttackSeconds ), "WindBurstProfile AttackSeconds must be > 0: a zero attack would snap from base to peak at the burst start." );
		WindProfileMath.RequireFinite( SustainSeconds, "WindBurstProfile SustainSeconds" );
		if ( SustainSeconds < 0 )
			throw new ArgumentOutOfRangeException( nameof( SustainSeconds ), "WindBurstProfile SustainSeconds must be >= 0." );
		WindProfileMath.RequireFinite( DecaySeconds, "WindBurstProfile DecaySeconds" );
		if ( DecaySeconds <= 0 )
			throw new ArgumentOutOfRangeException( nameof( DecaySeconds ), "WindBurstProfile DecaySeconds must be > 0: a zero decay would snap from peak to base at the sustain end." );
		WindProfileMath.RequireFinite( LullSeconds, "WindBurstProfile LullSeconds" );
		if ( LullSeconds < 0 )
			throw new ArgumentOutOfRangeException( nameof( LullSeconds ), "WindBurstProfile LullSeconds must be >= 0." );
	}

	public Vector3 At( float time )
	{
		WindProfileMath.RequireTime( time );
		Validate();
		float period = Period;
		float local = time % period;
		float e;
		if ( local < AttackSeconds )
			e = 0.5f * (1f - MathF.Cos( MathF.PI * local / AttackSeconds ));
		else if ( local < AttackSeconds + SustainSeconds )
			e = 1f;
		else if ( local < AttackSeconds + SustainSeconds + DecaySeconds )
			e = 0.5f * (1f + MathF.Cos( MathF.PI * (local - AttackSeconds - SustainSeconds) / DecaySeconds ));
		else
			e = 0f;
		return BaseWind + (PeakWind - BaseWind) * e;
	}
}

/// <summary>Turbulent flutter: a sum of sine components along one direction. Deterministic for fixed parameters (same t -> bit-identical wind), bounded by |BaseWind| + sum(amplitudes) for every t.</summary>
public sealed class WindFlutterProfile : IParticleWindProfile
{
	/// <summary>Mean wind vector the flutter oscillates around, inches/second. Finite.</summary>
	public Vector3 BaseWind;
	/// <summary>Flutter direction; normalized at evaluation. Finite and non-zero.</summary>
	public Vector3 Direction = new( 1, 0, 0 );
	/// <summary>Per-component amplitudes, inches/second. Each finite and >= 0. Parallel to FrequenciesHz and PhasesRadians; at least one component.</summary>
	public float[] Amplitudes = new[] { 3f, 2f, 1f };
	/// <summary>Per-component frequencies, Hz. Each finite and > 0. Defaults (1.3, 2.9, 4.7) are mutually incommensurate, so the sum looks irregular.</summary>
	public float[] FrequenciesHz = new[] { 1.3f, 2.9f, 4.7f };
	/// <summary>Per-component phases, radians. Each finite. Explicit inputs — no RNG.</summary>
	public float[] PhasesRadians = new[] { 0f, 1.1f, 2.2f };

	/// <summary>Sum of component amplitudes: the proven bound on |At(t) - BaseWind| for every t.</summary>
	public float AmplitudeBound
	{
		get
		{
			float sum = 0;
			if ( Amplitudes is not null ) foreach ( float a in Amplitudes ) sum += a;
			return sum;
		}
	}

	public void Validate()
	{
		WindProfileMath.RequireFinite( BaseWind, "WindFlutterProfile BaseWind" );
		WindProfileMath.RequireDirection( Direction, "WindFlutterProfile Direction" );
		if ( Amplitudes is null || FrequenciesHz is null || PhasesRadians is null )
			throw new ArgumentException( "WindFlutterProfile component arrays must not be null." );
		if ( Amplitudes.Length == 0 )
			throw new ArgumentException( "WindFlutterProfile needs at least one component." );
		if ( FrequenciesHz.Length != Amplitudes.Length || PhasesRadians.Length != Amplitudes.Length )
			throw new ArgumentException( "WindFlutterProfile Amplitudes, FrequenciesHz, and PhasesRadians must have the same length." );
		for ( int i = 0; i < Amplitudes.Length; i++ )
		{
			WindProfileMath.RequireFinite( Amplitudes[i], $"WindFlutterProfile Amplitudes[{i}]" );
			if ( Amplitudes[i] < 0 )
				throw new ArgumentOutOfRangeException( nameof( Amplitudes ), $"WindFlutterProfile Amplitudes[{i}] must be >= 0." );
			WindProfileMath.RequireFinite( FrequenciesHz[i], $"WindFlutterProfile FrequenciesHz[{i}]" );
			if ( FrequenciesHz[i] <= 0 )
				throw new ArgumentOutOfRangeException( nameof( FrequenciesHz ), $"WindFlutterProfile FrequenciesHz[{i}] must be > 0." );
			WindProfileMath.RequireFinite( PhasesRadians[i], $"WindFlutterProfile PhasesRadians[{i}]" );
		}
	}

	public Vector3 At( float time )
	{
		WindProfileMath.RequireTime( time );
		Validate();
		Vector3 d = Vector3.Normalize( Direction );
		float s = 0;
		for ( int i = 0; i < Amplitudes.Length; i++ )
			s += Amplitudes[i] * MathF.Sin( 2f * MathF.PI * FrequenciesHz[i] * time + PhasesRadians[i] );
		return BaseWind + d * s;
	}
}

/// <summary>Combines wind profiles by SUM, exactly, with no clamp: At(t) = W1(t) + W2(t) + ... in list order. A wind vector has no [0, 1] ceiling; clamping would break the exact sum.</summary>
public sealed class CombinedWindProfile : IParticleWindProfile
{
	public IReadOnlyList<IParticleWindProfile> Profiles { get; }

	public CombinedWindProfile( IEnumerable<IParticleWindProfile> profiles )
	{
		ArgumentNullException.ThrowIfNull( profiles );
		Profiles = new List<IParticleWindProfile>( profiles );
	}

	/// <summary>Builds a summed profile from a list (params form allows CombinedWindProfile.Sum(a, b, c)).</summary>
	public static CombinedWindProfile Sum( params IParticleWindProfile[] profiles ) => new( profiles );

	public void Validate()
	{
		if ( Profiles.Count == 0 )
			throw new ArgumentException( "Combined wind profile list must not be empty: a combined profile with nothing to combine is a configuration error, not a no-op." );
		for ( int i = 0; i < Profiles.Count; i++ )
		{
			var p = Profiles[i] ?? throw new ArgumentException( $"Combined wind profile entry {i} is null." );
			p.Validate();
		}
	}

	public Vector3 At( float time )
	{
		WindProfileMath.RequireTime( time );
		Vector3 sum = Vector3.Zero;
		for ( int i = 0; i < Profiles.Count; i++ )
			sum += (Profiles[i] ?? throw new ArgumentException( $"Combined wind profile entry {i} is null." )).At( time );
		return sum;
	}
}
