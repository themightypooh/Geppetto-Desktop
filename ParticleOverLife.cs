#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>One key of a scalar over-life curve: normalized age <see cref="Time"/> in [0,1] and its <see cref="Value"/>.</summary>
public readonly record struct OverLifeFloatKey( float Time, float Value );

/// <summary>One key of a colour over-life curve: normalized age <see cref="Time"/> in [0,1] and an RGBA <see cref="Colour"/> with every channel in [0,1].</summary>
public readonly record struct OverLifeColourKey( float Time, Vector4 Colour );

/// <summary>
/// Result of evaluating every over-life curve for one particle at one normalized age.
/// All fields are pure functions of normalized age: no hidden state, no RNG, so the same
/// (age, lifetime, curves) always returns the same sample.
/// </summary>
public readonly record struct OverLifeSample(
	float NormalizedAge,
	float SizeMultiplier,
	Vector4 Colour,
	float RotationOffsetDegrees,
	float RotationSpeedMultiplier,
	float SpeedMultiplier );

/// <summary>
/// The over-life curves for one 3D particle effect. A curve left <c>null</c> is unset and
/// evaluates to its identity default (size 1, opaque white, rotation offset 0, rotation-speed
/// multiplier 1, speed multiplier 1). A curve that is set must be non-empty; <see cref="Validate"/>
/// rejects an explicitly empty list loudly instead of silently treating it as unset.
/// </summary>
public sealed class ParticleOverLifeCurves
{
	public IReadOnlyList<OverLifeFloatKey>? SizeKeys { get; set; }
	public IReadOnlyList<OverLifeColourKey>? ColourKeys { get; set; }
	public IReadOnlyList<OverLifeFloatKey>? RotationSpeedKeys { get; set; }
	public IReadOnlyList<OverLifeFloatKey>? RotationOffsetKeys { get; set; }
	public IReadOnlyList<OverLifeFloatKey>? SpeedKeys { get; set; }

	/// <summary>Curves with every curve unset: every sample is the identity (no change over life).</summary>
	public static ParticleOverLifeCurves Default => new();

	/// <summary>
	/// Loudly rejects bad curves: an explicitly empty key list, keys out of [0,1] time order,
	/// duplicate times, non-finite values, negative size/speed/rotation-speed multipliers, or
	/// colour channels outside [0,1]. Rotation-offset values may be any finite number of degrees
	/// (negative = spin the other way); they are the one value range that is not clamped to [0,1].
	/// </summary>
	public void Validate()
	{
		ValidateFloats( nameof( SizeKeys ), SizeKeys, nonNegative: true );
		ValidateFloats( nameof( RotationSpeedKeys ), RotationSpeedKeys, nonNegative: true );
		ValidateFloats( nameof( RotationOffsetKeys ), RotationOffsetKeys, nonNegative: false );
		ValidateFloats( nameof( SpeedKeys ), SpeedKeys, nonNegative: true );
		if ( ColourKeys is not null )
		{
			if ( ColourKeys.Count == 0 ) throw new ArgumentException( "ColourKeys: key list must not be empty." );
			float prev = float.NegativeInfinity;
			for ( int i = 0; i < ColourKeys.Count; i++ )
			{
				var key = ColourKeys[i];
				if ( !float.IsFinite( key.Time ) || key.Time < 0f || key.Time > 1f )
					throw new ArgumentException( $"ColourKeys[{i}]: time {key.Time} is outside [0,1]." );
				if ( key.Time <= prev )
					throw new ArgumentException( $"ColourKeys[{i}]: time {key.Time} is not strictly after the previous key time {prev} (keys must be sorted, no duplicates)." );
				prev = key.Time;
				if ( !float.IsFinite( key.Colour.X ) || !float.IsFinite( key.Colour.Y ) || !float.IsFinite( key.Colour.Z ) || !float.IsFinite( key.Colour.W )
					|| key.Colour.X < 0f || key.Colour.X > 1f || key.Colour.Y < 0f || key.Colour.Y > 1f
					|| key.Colour.Z < 0f || key.Colour.Z > 1f || key.Colour.W < 0f || key.Colour.W > 1f )
					throw new ArgumentException( $"ColourKeys[{i}]: colour channels must all be finite and in [0,1]." );
			}
		}
	}

	static void ValidateFloats( string name, IReadOnlyList<OverLifeFloatKey>? keys, bool nonNegative )
	{
		if ( keys is null ) return;
		if ( keys.Count == 0 ) throw new ArgumentException( $"{name}: key list must not be empty." );
		float prev = float.NegativeInfinity;
		for ( int i = 0; i < keys.Count; i++ )
		{
			var key = keys[i];
			if ( !float.IsFinite( key.Time ) || key.Time < 0f || key.Time > 1f )
				throw new ArgumentException( $"{name}[{i}]: time {key.Time} is outside [0,1]." );
			if ( key.Time <= prev )
				throw new ArgumentException( $"{name}[{i}]: time {key.Time} is not strictly after the previous key time {prev} (keys must be sorted, no duplicates)." );
			prev = key.Time;
			if ( !float.IsFinite( key.Value ) )
				throw new ArgumentException( $"{name}[{i}]: value must be finite." );
			if ( nonNegative && key.Value < 0f )
				throw new ArgumentException( $"{name}[{i}]: value {key.Value} must not be negative." );
		}
	}

	/// <summary>Evaluate this curve set for a particle of the given age and lifetime. See <see cref="ParticleOverLife.Evaluate"/>.</summary>
	public OverLifeSample Evaluate( float ageSeconds, float lifetimeSeconds ) => ParticleOverLife.Evaluate( ageSeconds, lifetimeSeconds, this );
}

/// <summary>
/// Self-contained over-life curve evaluators for 3D particles. Units are kernel inches, Z-up,
/// seconds — the same conventions as <see cref="ParticleSystem"/> — but nothing here touches
/// the sim: these are pure functions of normalized age t in [0,1], where
/// t = clamp(ageSeconds / lifetimeSeconds, 0, 1), exactly the NormalizedAge convention the
/// sim already uses for its billboard fade. Interpolation is linear between neighbouring keys
/// and clamps to the end keys outside the key range: it never extrapolates, so a particle
/// older than its lifetime (or younger than the first key) simply holds the end value.
/// <para/>
/// Rotation convention, matching <see cref="ParticleState"/>: the sim stores rotation as a
/// <see cref="Quaternion"/> and angular velocity as a world-space vector in radians per second.
/// <see cref="OverLifeSample.RotationSpeedMultiplier"/> therefore scales that rad/s angular
/// velocity (no degree/radian mixing on that path), while
/// <see cref="OverLifeSample.RotationOffsetDegrees"/> is an EXTRA spin angle in degrees about
/// the particle's angular-velocity axis (Z axis when the angular velocity is ~zero), composed
/// as axis-angle * Rotation at render/integration time — it never replaces Rotation itself.
/// Use <see cref="ParticleOverLife.OffsetQuaternion"/> for that composition.
/// <para/>
/// Wiring is NOT done here: the sim step and the render layers do not call these evaluators
/// yet. The coordinator sweep owns the integration lines (see the lane report).
/// </summary>
public static class ParticleOverLife
{
	/// <summary>
	/// Evaluate every curve for one particle. <paramref name="curves"/> is validated on every
	/// call, so authoring mistakes fail loudly at evaluation time too, not just at Validate time.
	/// Callers evaluating many particles per frame should Validate once and cache the sample
	/// shape, or accept the small per-call validation cost over a handful of keys.
	/// </summary>
	public static OverLifeSample Evaluate( float ageSeconds, float lifetimeSeconds, ParticleOverLifeCurves curves )
	{
		ArgumentNullException.ThrowIfNull( curves );
		if ( !float.IsFinite( ageSeconds ) ) throw new ArgumentException( $"ageSeconds {ageSeconds} must be finite." );
		if ( !float.IsFinite( lifetimeSeconds ) || lifetimeSeconds <= 0f ) throw new ArgumentException( $"lifetimeSeconds {lifetimeSeconds} must be finite and > 0." );
		curves.Validate();
		float t = Math.Clamp( ageSeconds / lifetimeSeconds, 0f, 1f );
		return new OverLifeSample(
			t,
			EvalFloat( curves.SizeKeys, t, 1f ),
			EvalColour( curves.ColourKeys, t ),
			EvalFloat( curves.RotationOffsetKeys, t, 0f ),
			EvalFloat( curves.RotationSpeedKeys, t, 1f ),
			EvalFloat( curves.SpeedKeys, t, 1f ) );
	}

	/// <summary>Evaluate for a live sim particle: t comes from <paramref name="state"/>.Age over <paramref name="lifetimeSeconds"/> (the sim's per-slot life value).</summary>
	public static OverLifeSample Evaluate( ParticleState state, float lifetimeSeconds, ParticleOverLifeCurves curves )
		=> Evaluate( state.Age, lifetimeSeconds, curves );

	/// <summary>
	/// Linear interpolation of a scalar curve at normalized age <paramref name="t"/>.
	/// Null keys return <paramref name="defaultValue"/>; a single key is constant; outside the
	/// key range the end key holds (never extrapolate).
	/// </summary>
	public static float EvalFloat( IReadOnlyList<OverLifeFloatKey>? keys, float t, float defaultValue )
	{
		if ( keys is null ) return defaultValue;
		if ( keys.Count == 0 ) throw new ArgumentException( "Key list must not be empty." );
		if ( t <= keys[0].Time ) return keys[0].Value;
		if ( t >= keys[keys.Count - 1].Time ) return keys[keys.Count - 1].Value;
		for ( int i = 1; i < keys.Count; i++ )
		{
			if ( t <= keys[i].Time )
			{
				float f = ( t - keys[i - 1].Time ) / ( keys[i].Time - keys[i - 1].Time );
				return keys[i - 1].Value + ( keys[i].Value - keys[i - 1].Value ) * f;
			}
		}
		return keys[keys.Count - 1].Value;
	}

	/// <summary>Per-channel linear interpolation of a colour curve; null keys return opaque white.</summary>
	public static Vector4 EvalColour( IReadOnlyList<OverLifeColourKey>? keys, float t )
	{
		if ( keys is null ) return Vector4.One;
		if ( keys.Count == 0 ) throw new ArgumentException( "Key list must not be empty." );
		if ( t <= keys[0].Time ) return keys[0].Colour;
		if ( t >= keys[keys.Count - 1].Time ) return keys[keys.Count - 1].Colour;
		for ( int i = 1; i < keys.Count; i++ )
		{
			if ( t <= keys[i].Time )
			{
				float f = ( t - keys[i - 1].Time ) / ( keys[i].Time - keys[i - 1].Time );
				return Vector4.Lerp( keys[i - 1].Colour, keys[i].Colour, f );
			}
		}
		return keys[keys.Count - 1].Colour;
	}

	/// <summary>
	/// Size multiplier at normalized age <paramref name="t"/> (1 when unset), colour at
	/// <paramref name="t"/> (opaque white when unset), rotation-speed multiplier (1 when unset),
	/// rotation offset in degrees (0 when unset), and speed multiplier (1 when unset).
	/// Thin wrappers over <see cref="EvalFloat"/> / <see cref="EvalColour"/> for callers that
	/// need one curve, not a full sample.
	/// </summary>
	public static float SizeAt( ParticleOverLifeCurves curves, float t ) => EvalFloat( curves.SizeKeys, t, 1f );
	public static Vector4 ColourAt( ParticleOverLifeCurves curves, float t ) => EvalColour( curves.ColourKeys, t );
	public static float RotationSpeedAt( ParticleOverLifeCurves curves, float t ) => EvalFloat( curves.RotationSpeedKeys, t, 1f );
	public static float RotationOffsetAt( ParticleOverLifeCurves curves, float t ) => EvalFloat( curves.RotationOffsetKeys, t, 0f );
	public static float SpeedAt( ParticleOverLifeCurves curves, float t ) => EvalFloat( curves.SpeedKeys, t, 1f );

	/// <summary>
	/// Compose a rotation-offset quaternion: <paramref name="degrees"/> about
	/// <paramref name="axis"/> (normalized; the Z axis is used when the axis is ~zero, i.e. a
	/// particle with no angular velocity). The sweep integration composes this as
	/// offset * state.Rotation. Degrees are converted to radians only here.
	/// </summary>
	public static Quaternion OffsetQuaternion( float degrees, Vector3 axis )
	{
		if ( axis.LengthSquared() < 1e-12f ) axis = Vector3.UnitZ;
		return Quaternion.CreateFromAxisAngle( Vector3.Normalize( axis ), degrees * MathF.PI / 180f );
	}
}
