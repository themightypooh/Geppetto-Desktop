#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// Wave-6 lane P12 (particles session, 2026-10-24): self-contained 2D force-field
// evaluators for the 2D particle sim (Particle2D.cs, lane 1 — NOT edited here).
//
// Units match Particle2D exactly: flat XY canvas pixels, seconds, Y-DOWN
// (+Y is down the screen, so "up" is -Y). Every evaluator is a pure function of
// a particle's position (and optionally its velocity and the sim time) and
// returns an ACCELERATION vector in px/s^2. No state is mutated, no
// System.Random is used anywhere, and nothing here draws anything — the 2D
// suite owns the canvas/UI that draws particles.
//
// How the sim is meant to apply these (coordinator sweep owns the edit —
// Particle2D.cs is NOT touched by this lane): inside Particle2DSystem.Simulate's
// per-particle loop, after `Vector2 acc = s.Gravity;`, add one line per field
// set, `acc += field.Evaluate( p.Position, p.Velocity, time );`. Fields compose
// with gravity, drag-through-wind, turbulence, vortex and the floor exactly
// like the sim's built-in turbulence term does. NOTE the distinction from
// Particle2DSettings.Wind: that wind acts only through drag (it pulls velocity
// toward itself); a gust field here is a direct acceleration, so it pushes even
// a particle already moving with the wind.
//
// Centre convention (all point fields): a particle EXACTLY on the point has no
// direction toward or away from it, so it gets Vector2.Zero from that field —
// finite, never NaN, never infinite — rather than an arbitrary direction.
// Every other distance uses the field's falloff. The inverse-square clamp is
// what keeps near-centre particles finite: the effective distance is
// max(distance, MinDistance), so the largest magnitude the field can ever
// return is Strength / MinDistance^2.

/// <summary>How a point field's strength decays with distance from its centre.</summary>
public enum Particle2DFieldFalloff
{
	/// <summary>Full strength everywhere inside the radius, zero outside it.</summary>
	Constant,
	/// <summary>Strength at the centre, falling linearly to zero at the radius, zero outside it.</summary>
	Linear,
	/// <summary>Strength / distance^2 (distance clamped below by MinDistance), zero outside the radius.</summary>
	InverseSquare,
}

/// <summary>A 2D force field: a pure acceleration evaluator. Implementations must be deterministic.</summary>
public interface IParticle2DField
{
	/// <summary>Acceleration in px/s^2 for a particle at <paramref name="position"/> moving at <paramref name="velocity"/> at sim time <paramref name="time"/> (seconds). Fields that do not need velocity or time ignore them.</summary>
	Vector2 Evaluate( Vector2 position, Vector2 velocity, float time );
	/// <summary>Throws (ArgumentException family) on any meaningless configuration. The sim hookup should call this once per field before stepping, the way it validates emitters.</summary>
	void Validate();
}

static class Particle2DFieldMath
{
	public static void RequireFinite( Vector2 v, string what )
	{
		if ( !float.IsFinite( v.X ) || !float.IsFinite( v.Y ) )
			throw new ArgumentException( $"{what} must be finite, got ({v.X}, {v.Y})." );
	}

	public static void RequireFiniteNonNegative( float value, string what )
	{
		if ( !float.IsFinite( value ) || value < 0 )
			throw new ArgumentOutOfRangeException( what, $"{what} must be finite and >= 0." );
	}

	/// <summary>Shared point-field magnitude. Caller supplies the signed direction; returns 0 outside the radius and at the exact centre (no direction exists there).</summary>
	public static float PointMagnitude( float distance, float strength, float radius, Particle2DFieldFalloff falloff, float minDistance )
	{
		if ( distance > radius ) return 0;
		switch ( falloff )
		{
			case Particle2DFieldFalloff.Constant: return strength;
			case Particle2DFieldFalloff.Linear: return strength * (1 - distance / radius);
			case Particle2DFieldFalloff.InverseSquare:
			{
				float d = MathF.Max( distance, minDistance );
				return strength / (d * d);
			}
			default: throw new ArgumentException( $"Unknown falloff {falloff}." );
		}
	}

	public static void ValidatePointField( Vector2 center, float strength, float radius, Particle2DFieldFalloff falloff, float minDistance, string what )
	{
		RequireFinite( center, $"{what} Center" );
		RequireFiniteNonNegative( strength, $"{what} Strength" );
		if ( !float.IsFinite( radius ) || radius <= 0 )
			throw new ArgumentOutOfRangeException( $"{what} Radius", $"{what} Radius must be finite and > 0 (it is the cutoff distance; use a large radius for an effectively unbounded field)." );
		if ( !Enum.IsDefined( falloff ) )
			throw new ArgumentException( $"{what} Falloff has unknown value {falloff}." );
		if ( !float.IsFinite( minDistance ) || minDistance <= 0 )
			throw new ArgumentOutOfRangeException( $"{what} MinDistance", $"{what} MinDistance must be finite and > 0 (it is the clamp that keeps inverse-square finite at the centre)." );
		if ( minDistance > radius )
			throw new ArgumentException( $"{what} MinDistance ({minDistance}) must not exceed Radius ({radius}); otherwise the clamp would swallow the whole field." );
	}
}

/// <summary>Pulls particles toward <see cref="Center"/>. Strength is px/s^2 for Constant/Linear, and the inverse-square numerator (px^3/s^2) for InverseSquare — i.e. at distance d the acceleration is Strength / d^2.</summary>
public sealed class Particle2DAttractorField : IParticle2DField
{
	public Vector2 Center;
	public float Strength;
	public float Radius = 100f;
	public Particle2DFieldFalloff Falloff = Particle2DFieldFalloff.Constant;
	/// <summary>Minimum effective distance for InverseSquare (and the documented clamp). Ignored by Constant/Linear.</summary>
	public float MinDistance = 1f;

	public void Validate() => Particle2DFieldMath.ValidatePointField( Center, Strength, Radius, Falloff, MinDistance, "Attractor" );

	public Vector2 Evaluate( Vector2 position, Vector2 velocity, float time )
	{
		_ = velocity; _ = time;
		var r = position - Center;
		float d = r.Length();
		if ( d < 1e-12f ) return Vector2.Zero;
		float mag = Particle2DFieldMath.PointMagnitude( d, Strength, Radius, Falloff, MinDistance );
		if ( mag == 0 ) return Vector2.Zero;
		return r / d * -mag;
	}
}

/// <summary>Pushes particles away from <see cref="Center"/>. Same shape, falloffs and clamp as the attractor, opposite sign.</summary>
public sealed class Particle2DRepellerField : IParticle2DField
{
	public Vector2 Center;
	public float Strength;
	public float Radius = 100f;
	public Particle2DFieldFalloff Falloff = Particle2DFieldFalloff.Constant;
	/// <summary>Minimum effective distance for InverseSquare (and the documented clamp). Ignored by Constant/Linear.</summary>
	public float MinDistance = 1f;

	public void Validate() => Particle2DFieldMath.ValidatePointField( Center, Strength, Radius, Falloff, MinDistance, "Repeller" );

	public Vector2 Evaluate( Vector2 position, Vector2 velocity, float time )
	{
		_ = velocity; _ = time;
		var r = position - Center;
		float d = r.Length();
		if ( d < 1e-12f ) return Vector2.Zero;
		float mag = Particle2DFieldMath.PointMagnitude( d, Strength, Radius, Falloff, MinDistance );
		if ( mag == 0 ) return Vector2.Zero;
		return r / d * mag;
	}
}

/// <summary>
/// A rectangular gust zone: inside the rectangle (bounds inclusive) it adds a
/// time-varying wind acceleration along <see cref="Direction"/>; outside it adds
/// nothing. The instantaneous strength is
///   BaseStrength + GustAmplitude * sin( 2π * GustFrequency * time + phase(Seed) ),
/// where phase(Seed) = 2π * frac(Seed * 0.6180339887498949) — deterministic in
/// time, with seeded variation only through the explicit <see cref="Seed"/> field
/// (Seed 0 gives phase 0). No System.Random anywhere.
/// Note: when GustAmplitude exceeds BaseStrength the wind briefly reverses; that
/// is the documented behaviour, not a bug — keep Amplitude &lt;= BaseStrength for
/// a wind that never reverses.
/// </summary>
public sealed class Particle2DGustField : IParticle2DField
{
	/// <summary>Rectangle corner, inclusive (Y-down: Min is the top-left in canvas terms only if Min.Y &lt;= Max.Y).</summary>
	public Vector2 Min;
	/// <summary>Rectangle corner, inclusive.</summary>
	public Vector2 Max;
	/// <summary>Wind direction; normalized at evaluation. Must be non-zero (see Validate).</summary>
	public Vector2 Direction = new( 1, 0 );
	/// <summary>Steady wind acceleration, px/s^2. Must be &gt;= 0; point Direction the other way for the opposite wind.</summary>
	public float BaseStrength;
	/// <summary>Peak gust deviation from BaseStrength, px/s^2. Must be &gt;= 0.</summary>
	public float GustAmplitude;
	/// <summary>Gust cycles per second. 0 = steady wind at BaseStrength (plus the seed phase offset, which is then constant).</summary>
	public float GustFrequency;
	/// <summary>Seeded phase only — the sole source of per-field variation. Same seed + same time = same wind, always.</summary>
	public uint Seed;

	/// <summary>Deterministic phase offset for a seed, in radians, in [0, 2π).</summary>
	public static float PhaseForSeed( uint seed )
	{
		double frac = seed * 0.6180339887498949;
		frac -= Math.Floor( frac );
		return (float)(frac * 2.0 * Math.PI);
	}

	public void Validate()
	{
		Particle2DFieldMath.RequireFinite( Min, "Gust Min" );
		Particle2DFieldMath.RequireFinite( Max, "Gust Max" );
		if ( Min.X > Max.X || Min.Y > Max.Y )
			throw new ArgumentException( $"Gust rectangle is inverted: Min ({Min.X}, {Min.Y}) must be <= Max ({Max.X}, {Max.Y}) component-wise." );
		Particle2DFieldMath.RequireFinite( Direction, "Gust Direction" );
		if ( Direction.LengthSquared() < 1e-12f )
			throw new ArgumentException( "Gust Direction must be non-zero (point it along the wind; use BaseStrength 0 for a no-op field)." );
		Particle2DFieldMath.RequireFiniteNonNegative( BaseStrength, "Gust BaseStrength" );
		Particle2DFieldMath.RequireFiniteNonNegative( GustAmplitude, "Gust GustAmplitude" );
		Particle2DFieldMath.RequireFiniteNonNegative( GustFrequency, "Gust GustFrequency" );
	}

	public Vector2 Evaluate( Vector2 position, Vector2 velocity, float time )
	{
		_ = velocity;
		if ( position.X < Min.X || position.X > Max.X || position.Y < Min.Y || position.Y > Max.Y )
			return Vector2.Zero;
		float len = Direction.Length();
		if ( len < 1e-12f ) return Vector2.Zero;
		float strength = BaseStrength + GustAmplitude * MathF.Sin( 2f * MathF.PI * GustFrequency * time + PhaseForSeed( Seed ) );
		return Direction / len * strength;
	}
}

/// <summary>Sums any list of fields for one particle, so the sim can apply several fields at once. Nesting is allowed (a combined field inside a combined field just sums). Validate() is the loud gate: it rejects an empty list and any null or invalid entry. Evaluate() on an empty list returns zero, by design — call Validate() to catch that, the way the sim validates emitters before stepping.</summary>
public sealed class Particle2DCombinedField : IParticle2DField
{
	public IReadOnlyList<IParticle2DField> Fields { get; }

	public Particle2DCombinedField( IEnumerable<IParticle2DField> fields )
	{
		ArgumentNullException.ThrowIfNull( fields );
		var list = new List<IParticle2DField>( fields );
		Fields = list;
	}

	/// <summary>Builds a combined field from a list (params form allows Combine(a, b, c)).</summary>
	public static Particle2DCombinedField Combine( params IParticle2DField[] fields ) => new( fields );

	public static Particle2DCombinedField Combine( IEnumerable<IParticle2DField> fields ) => new( fields );

	public void Validate()
	{
		if ( Fields.Count == 0 )
			throw new ArgumentException( "Combined field list must not be empty: a combined field with nothing to sum is a configuration error, not a no-op." );
		for ( int i = 0; i < Fields.Count; i++ )
		{
			var f = Fields[i] ?? throw new ArgumentException( $"Combined field entry {i} is null." );
			f.Validate();
		}
	}

	public Vector2 Evaluate( Vector2 position, Vector2 velocity, float time )
	{
		var sum = Vector2.Zero;
		for ( int i = 0; i < Fields.Count; i++ )
		{
			var f = Fields[i] ?? throw new ArgumentException( $"Combined field entry {i} is null." );
			sum += f.Evaluate( position, velocity, time );
		}
		return sum;
	}
}
