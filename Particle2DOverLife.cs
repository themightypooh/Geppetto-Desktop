#nullable enable
using System;
using System.Collections.Generic;

namespace Marionette.Desktop;

// 2D over-life curves for Geppetto's 2D particles (wave-6 lane P11).
//
// Self-contained, pure evaluators that a 2D sim's draw records can consume.
// They do NOT touch Particle2D.cs, draw anything, load textures, or keep
// state: every evaluation is a pure function of normalized age t in [0,1]
// plus the keyframe lists it is given, and there is no System.Random
// anywhere in this file (a curve is authored, not rolled).
//
// Units and conventions, matching Particle2D.cs:
//   - Space/time: canvas pixels, seconds, Y-DOWN (Particle2D.cs header).
//     Curves themselves are unit-free: size is a multiplier, alpha is
//     0..1, rotation is degrees, colour channels are 0..1.
//   - Size: EvaluateSize returns a MULTIPLIER. A draw record's size is
//     base size x multiplier (base = the particle's spawn size,
//     Particle2DState.SizeStart). 1 = unchanged, 0 = invisible point,
//     > 1 = grown. It does NOT replace Particle2D's SizeStart->SizeEnd
//     linear interpolation; when a sim adopts curves, that interpolation
//     is superseded for size (see the sweep lines at the end of this
//     file's header comment in the lane report, not an edit here).
//   - Rotation: degrees, and the sign convention matches Particle2D.cs:
//     Particle2DState.Rotation is in RADIANS and grows with positive
//     AngularVelocity (rad/s). In Y-down canvas space a growing rotation
//     turns CLOCKWISE on screen (the same direction QML Canvas rotate()
//     takes as positive). So a positive rotation-curve value is also
//     CLOCKWISE, and the integrator adds RotationRadians (degrees x
//     PI/180) to the record's Rotation. A negative value turns
//     counter-clockwise. Rotation keys are checked only for finiteness:
//     any magnitude is legal (a particle may spin past 360 degrees).
//   - Colour: per-channel linear interpolation between RGBA keyframes,
//     reusing Particle2DColor.Lerp from Particle2D.cs.
//   - Alpha: a separate scalar curve, 0..1. It MULTIPLIES the colour
//     curve's alpha (see Particle2DOverLifeResult.FinalColor), so a
//     fade-out can live in either curve and combining both still ends
//     at 0, never double-counts past it. When there is no colour curve,
//     the result colour is opaque white and FinalColor is white with
//     alpha = the alpha curve; when there is no alpha curve, alpha is 1.
//   - Outside the key range: CLAMP to the end keys, never extrapolate.
//     A single-key curve is constant at that key's value for all t.
//     Evaluate(age, lifetime) clamps age/lifetime to [0,1] the same way
//     Particle2DSystem.NormalizedAge does, so a particle past its
//     lifetime (which the sim expires anyway) reads its final key.
//   - Absent vs empty: a null key list means "no curve" and yields the
//     default (size 1, alpha 1, rotation 0, white). A non-null but EMPTY
//     list is a loud error in Validate() and in every evaluator.
//   - Validate() is cheap (small keyframe lists) and Evaluate() calls
//     it, so a bad curve fails loudly at evaluation time even if the
//     caller forgot to validate at authoring time. Validate once at
//     authoring time anyway when curves are built, not per frame, if
//     the caller wants to avoid the per-call scan.

/// <summary>One scalar keyframe: value at normalized age <see cref="Time"/> (0..1).</summary>
public readonly record struct OverLifeKey( float Time, float Value );

/// <summary>One colour keyframe: RGBA colour at normalized age <see cref="Time"/> (0..1).</summary>
public readonly record struct OverLifeColorKey( float Time, Particle2DColor Color );

/// <summary>One particle's over-life evaluation: size multiplier, alpha, rotation offset, colour.</summary>
public struct Particle2DOverLifeResult
{
	public float SizeMultiplier;
	public float Alpha;
	/// <summary>Rotation offset in degrees. Positive = clockwise in Y-down canvas space (see file header).</summary>
	public float RotationDegrees;
	/// <summary>Colour-curve value at t, or opaque white when there is no colour curve.</summary>
	public Particle2DColor Color;
	/// <summary>Rotation offset in radians, for adding to Particle2DDrawRecord.Rotation (which is radians).</summary>
	public float RotationRadians => RotationDegrees * MathF.PI / 180f;
	/// <summary>Colour with the alpha curve applied: alpha = Color.A x Alpha. This is the alpha a draw record should paint with.</summary>
	public Particle2DColor FinalColor => Color.WithAlpha( Color.A * Alpha );
}

/// <summary>Over-life curves for one 2D particle effect. Null list = curve absent (default); empty list = error.</summary>
public sealed class Particle2DOverLifeCurves
{
	/// <summary>Size multiplier keys. Values must be finite and &gt;= 0.</summary>
	public List<OverLifeKey>? SizeKeys;
	/// <summary>Opacity keys. Values must be finite and in 0..1.</summary>
	public List<OverLifeKey>? AlphaKeys;
	/// <summary>Rotation-offset keys, degrees. Values must be finite (any magnitude).</summary>
	public List<OverLifeKey>? RotationKeys;
	/// <summary>RGBA colour keys. Channels must be in 0..1.</summary>
	public List<OverLifeColorKey>? ColorKeys;

	/// <summary>Constant curves: size 1, alpha 1, rotation 0, opaque white.</summary>
	public static Particle2DOverLifeCurves Constant() => new()
	{
		SizeKeys = new List<OverLifeKey> { new( 0, 1 ) },
		AlphaKeys = new List<OverLifeKey> { new( 0, 1 ) },
		RotationKeys = new List<OverLifeKey> { new( 0, 0 ) },
		ColorKeys = new List<OverLifeColorKey> { new( 0, new Particle2DColor( 1, 1, 1, 1 ) ) },
	};

	/// <summary>Alpha-only fade in then out: 0 at birth, 1 by <paramref name="fadeInEnd"/>, 1 until <paramref name="fadeOutStart"/>, 0 at death.</summary>
	public static Particle2DOverLifeCurves FadeInOut( float fadeInEnd = 0.25f, float fadeOutStart = 0.75f )
	{
		if ( !float.IsFinite( fadeInEnd ) || !float.IsFinite( fadeOutStart ) || fadeInEnd < 0 || fadeOutStart > 1 || fadeOutStart <= fadeInEnd )
			throw new ArgumentException( $"FadeInOut needs 0 <= fadeInEnd < fadeOutStart <= 1, got fadeInEnd={fadeInEnd}, fadeOutStart={fadeOutStart}." );
		return new Particle2DOverLifeCurves
		{
			AlphaKeys = new List<OverLifeKey> { new( 0, 0 ), new( fadeInEnd, 1 ), new( fadeOutStart, 1 ), new( 1, 0 ) },
		};
	}

	/// <summary>Loudly rejects unsorted keys, duplicate times, out-of-range times or values, and empty key lists. Null lists are absent, not errors.</summary>
	public void Validate()
	{
		ValidateScalar( SizeKeys, v => float.IsFinite( v ) && v >= 0, "SizeKeys values must be finite and >= 0" );
		ValidateScalar( AlphaKeys, v => float.IsFinite( v ) && v >= 0 && v <= 1, "AlphaKeys values must be finite and in 0..1" );
		ValidateScalar( RotationKeys, v => float.IsFinite( v ), "RotationKeys values must be finite" );
		if ( ColorKeys is not null )
		{
			if ( ColorKeys.Count == 0 ) throw new ArgumentException( "ColorKeys must not be empty (leave it null for no colour curve)." );
			for ( int i = 0; i < ColorKeys.Count; i++ )
			{
				var k = ColorKeys[i];
				if ( !float.IsFinite( k.Time ) || k.Time < 0 || k.Time > 1 )
					throw new ArgumentException( $"ColorKeys[{i}]: Time must be finite and in 0..1, got {k.Time}." );
				if ( i > 0 && k.Time <= ColorKeys[i - 1].Time )
					throw new ArgumentException( $"ColorKeys: times must be strictly increasing (duplicate or unsorted at index {i}: {ColorKeys[i - 1].Time} then {k.Time})." );
				if ( !k.Color.ChannelsValid() )
					throw new ArgumentException( $"ColorKeys[{i}]: colour channels must be in 0..1, got {k.Color}." );
			}
		}
	}

	static void ValidateScalar( List<OverLifeKey>? keys, Func<float, bool> valueOk, string valueMessage )
	{
		if ( keys is null ) return;
		if ( keys.Count == 0 ) throw new ArgumentException( "Key list must not be empty (leave it null for no curve)." );
		for ( int i = 0; i < keys.Count; i++ )
		{
			var k = keys[i];
			if ( !float.IsFinite( k.Time ) || k.Time < 0 || k.Time > 1 )
				throw new ArgumentException( $"Key[{i}]: Time must be finite and in 0..1, got {k.Time}." );
			if ( i > 0 && k.Time <= keys[i - 1].Time )
				throw new ArgumentException( $"Keys: times must be strictly increasing (duplicate or unsorted at index {i}: {keys[i - 1].Time} then {k.Time})." );
			if ( !valueOk( k.Value ) )
				throw new ArgumentException( $"Key[{i}]: {valueMessage}, got {k.Value}." );
		}
	}

	/// <summary>Evaluates all curves at normalized age <paramref name="t"/>. t must be finite; it is clamped to [0,1] (end keys, never extrapolation).</summary>
	public Particle2DOverLifeResult EvaluateNormalized( float t )
	{
		if ( !float.IsFinite( t ) ) throw new ArgumentException( $"Normalized age must be finite, got {t}." );
		Validate();
		float c = Math.Clamp( t, 0, 1 );
		var color = ColorKeys is null ? new Particle2DColor( 1, 1, 1, 1 ) : EvalColor( ColorKeys, c );
		return new Particle2DOverLifeResult
		{
			SizeMultiplier = SizeKeys is null ? 1 : EvalScalar( SizeKeys, c ),
			Alpha = AlphaKeys is null ? 1 : EvalScalar( AlphaKeys, c ),
			RotationDegrees = RotationKeys is null ? 0 : EvalScalar( RotationKeys, c ),
			Color = color,
		};
	}

	/// <summary>Single entry point for one particle: normalized age is age/lifetime, clamped to [0,1]. Lifetime must be finite and &gt; 0; age must be finite.</summary>
	public Particle2DOverLifeResult Evaluate( float age, float lifetime )
	{
		if ( !float.IsFinite( age ) ) throw new ArgumentException( $"Age must be finite, got {age}." );
		if ( !float.IsFinite( lifetime ) || lifetime <= 0 ) throw new ArgumentException( $"Lifetime must be finite and > 0, got {lifetime}." );
		return EvaluateNormalized( Math.Clamp( age / lifetime, 0, 1 ) );
	}

	/// <summary>Size multiplier at normalized age t (clamped to [0,1]); 1 when there is no size curve.</summary>
	public float EvaluateSize( float t ) => EvaluateNormalized( t ).SizeMultiplier;
	/// <summary>Alpha at normalized age t (clamped to [0,1]); 1 when there is no alpha curve.</summary>
	public float EvaluateAlpha( float t ) => EvaluateNormalized( t ).Alpha;
	/// <summary>Rotation offset in degrees at normalized age t (clamped to [0,1]); 0 when there is no rotation curve.</summary>
	public float EvaluateRotationDegrees( float t ) => EvaluateNormalized( t ).RotationDegrees;
	/// <summary>Colour at normalized age t (clamped to [0,1]); opaque white when there is no colour curve.</summary>
	public Particle2DColor EvaluateColor( float t ) => EvaluateNormalized( t ).Color;

	static float EvalScalar( IReadOnlyList<OverLifeKey> keys, float t )
	{
		if ( t <= keys[0].Time ) return keys[0].Value;
		if ( t >= keys[keys.Count - 1].Time ) return keys[keys.Count - 1].Value;
		for ( int i = 1; i < keys.Count; i++ )
		{
			if ( t == keys[i].Time ) return keys[i].Value;
			if ( t < keys[i].Time )
			{
				float f = (t - keys[i - 1].Time) / (keys[i].Time - keys[i - 1].Time);
				return keys[i - 1].Value + (keys[i].Value - keys[i - 1].Value) * f;
			}
		}
		return keys[keys.Count - 1].Value;
	}

	static Particle2DColor EvalColor( IReadOnlyList<OverLifeColorKey> keys, float t )
	{
		if ( t <= keys[0].Time ) return keys[0].Color;
		if ( t >= keys[keys.Count - 1].Time ) return keys[keys.Count - 1].Color;
		for ( int i = 1; i < keys.Count; i++ )
		{
			if ( t == keys[i].Time ) return keys[i].Color;
			if ( t < keys[i].Time )
			{
				float f = (t - keys[i - 1].Time) / (keys[i].Time - keys[i - 1].Time);
				return Particle2DColor.Lerp( keys[i - 1].Color, keys[i].Color, f );
			}
		}
		return keys[keys.Count - 1].Color;
	}
}
