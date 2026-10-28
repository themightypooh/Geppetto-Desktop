#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// Wave-6 lane P16 (particles session, 2026-10-24): self-contained 2D spawn
// patterns for the 2D particle sim (Particle2D.cs, lane 1).
//
// Units are flat XY, Y-DOWN canvas pixels, seconds — the same convention as
// Particle2D.cs (matches QML Canvas: +Y is down the screen, so "up" is -Y).
// Angles are degrees in Y-down space, the same convention as Particle2D.cs:
// 0 = +X (right), 90 = +Y (down), so a positive rotation turns clockwise on
// screen. Every generator is a pure function of a seeded ParticleRng (the
// SplitMix64/xorshift RNG from ParticleSystem.cs, never System.Random): the
// same seed and the same call order give the same spawn list, so effects
// replay deterministically. Each call returns a fresh list; no state is kept.
//
// RNG draw order, per particle, so a caller can reproduce a stream exactly:
// BurstRing draws one angle jitter per particle (only when jitter > 0);
// Spiral draws one speed jitter per particle (only when speedJitter > 0);
// Fountain draws one cone angle per particle, then one speed (only when
// speedMin < speedMax); Spray draws one segment position per particle, then
// one spread angle (only when spread > 0), then one speed jitter (only when
// speedJitter > 0).
//
// This file is NOT wired into Particle2DSystem.Spawn — the hookup is
// reported as integration lines for the coordinator sweep, not made here.
// What this file does NOT do: canvas/QML/UI drawing (the 2D suite owns the
// canvas), 3D spawn patterns (separate lanes; see ParticleEmitterShapes.cs),
// emission masks (lane P06), save/serialization (lane 5), or stepping the
// sim (the generators only describe spawn positions and velocities).

/// <summary>One particle's spawn descriptor: where it starts and how fast it
/// starts moving, in Y-down canvas pixels and pixels/second.</summary>
public readonly struct Particle2DSpawn
{
	public Particle2DSpawn( Vector2 position, Vector2 velocity )
	{
		Position = position;
		Velocity = velocity;
	}

	/// <summary>Spawn position, canvas pixels.</summary>
	public readonly Vector2 Position;
	/// <summary>Initial velocity, canvas pixels/second.</summary>
	public readonly Vector2 Velocity;
}

/// <summary>Self-contained 2D spawn-pattern generators. All outputs are
/// lists of <see cref="Particle2DSpawn"/> in spawn order (index 0 first).</summary>
public static class Particle2DPatterns
{
	const float TwoPi = (float)(Math.PI * 2.0);

	/// <summary>Rotate a vector by an angle in degrees, Y-down: positive
	/// angles turn clockwise on screen (0 = +X, 90 = +Y).</summary>
	static Vector2 Rotate( Vector2 v, float angleDegrees )
	{
		float rad = angleDegrees * MathF.PI / 180f;
		float c = MathF.Cos( rad ), s = MathF.Sin( rad );
		return new Vector2( v.X * c - v.Y * s, v.X * s + v.Y * c );
	}

	static Vector2 UnitFromAngle( float angleDegrees )
	{
		float rad = angleDegrees * MathF.PI / 180f;
		return new Vector2( MathF.Cos( rad ), MathF.Sin( rad ) );
	}

	/// <summary>Shared loud gate. Only the values a caller supplies are
	/// checked: defaults are valid neutral values (radius 0, speed 0, cone
	/// 90), so a generator passes exactly the parameters it owns and every
	/// other parameter is left unchecked. Loud = ArgumentException, never a
	/// clamped or silently corrected value.</summary>
	/// <exception cref="ArgumentException">Count is not positive, a supplied
	/// radius or speed is negative or non-finite, or a supplied cone angle
	/// is outside the open interval (0, 180) degrees.</exception>
	public static void Validate( int count, float radius = 0f, float speed = 0f, float coneAngleDegrees = 90f )
	{
		if ( count <= 0 ) throw new ArgumentException( $"Particle2DPatterns: count must be > 0, got {count}." );
		if ( !float.IsFinite( radius ) || radius < 0 ) throw new ArgumentException( $"Particle2DPatterns: radius must be finite and >= 0, got {radius}." );
		if ( !float.IsFinite( speed ) || speed < 0 ) throw new ArgumentException( $"Particle2DPatterns: speed must be finite and >= 0, got {speed}." );
		if ( !float.IsFinite( coneAngleDegrees ) || coneAngleDegrees <= 0 || coneAngleDegrees >= 180 ) throw new ArgumentException( $"Particle2DPatterns: cone angle must be finite and in (0, 180) degrees, got {coneAngleDegrees}." );
	}

	static void CheckJitter( float jitter, string what )
	{
		if ( !float.IsFinite( jitter ) || jitter < 0 || jitter > 1 ) throw new ArgumentException( $"Particle2DPatterns: {what} must be finite and in [0, 1], got {jitter}." );
	}

	static void CheckSpread( float spreadDegrees, string what )
	{
		if ( !float.IsFinite( spreadDegrees ) || spreadDegrees < 0 || spreadDegrees > 180 ) throw new ArgumentException( $"Particle2DPatterns: {what} must be finite and in [0, 180] degrees, got {spreadDegrees}." );
	}

	/// <summary>Burst ring: <paramref name="count"/> particles evenly around
	/// a circle of <paramref name="radius"/> about <paramref name="center"/>,
	/// each with an outward velocity of <paramref name="speed"/> (radial
	/// direction, so velocity is parallel to position minus centre).
	/// Particle i sits at base angle 360*i/count degrees before jitter.
	/// <paramref name="angularJitterDegrees"/> shifts each angle by a uniform
	/// draw in [-jitter, +jitter] degrees (documented range [0, 180]);
	/// 0 means exactly even, with no RNG draw. A jittered particle is still
	/// exactly on the circle — only its angle moves.</summary>
	public static List<Particle2DSpawn> BurstRing( Vector2 center, float radius, float speed, int count, ref ParticleRng rng, float angularJitterDegrees = 0f )
	{
		Validate( count, radius, speed );
		CheckSpread( angularJitterDegrees, "angular jitter" );
		if ( !Particle2DMath.Finite( center ) ) throw new ArgumentException( $"Particle2DPatterns: center must be finite, got {center}." );
		var outList = new List<Particle2DSpawn>( count );
		for ( int i = 0; i < count; i++ )
		{
			float angle = 360f * i / count;
			if ( angularJitterDegrees > 0 ) angle += rng.Range( -angularJitterDegrees, angularJitterDegrees );
			var dir = UnitFromAngle( angle );
			outList.Add( new Particle2DSpawn( center + radius * dir, speed * dir ) );
		}
		return outList;
	}

	/// <summary>Archimedean spiral: particle i sits at radius
	/// startRadius + radiusStep*i and angle startAngleDegrees +
	/// angleStepDegrees*i, so the radius grows by exactly
	/// <paramref name="radiusStep"/> pixels per step (outward only;
	/// negative steps are rejected). Velocities are tangential, following
	/// the spiral's index order: the tangent of increasing angle,
	/// (-sin θ, cos θ), times +1 for a positive angle step and -1 for a
	/// negative one (angle step 0 keeps the increasing-angle tangent, so a
	/// zero-step spiral is a radial line with tangential velocities).
	/// Winding in Y-down space: a positive angle step winds clockwise on
	/// screen, a negative step counter-clockwise. Speed is
	/// <paramref name="speed"/> times (1 ± speedJitter), uniformly drawn.</summary>
	public static List<Particle2DSpawn> Spiral( Vector2 center, float startRadius, float radiusStep, float startAngleDegrees, float angleStepDegrees, float speed, int count, ref ParticleRng rng, float speedJitter = 0f )
	{
		Validate( count, startRadius, speed );
		if ( !float.IsFinite( radiusStep ) || radiusStep < 0 ) throw new ArgumentException( $"Particle2DPatterns: radius step must be finite and >= 0, got {radiusStep}." );
		if ( !float.IsFinite( startAngleDegrees ) || !float.IsFinite( angleStepDegrees ) ) throw new ArgumentException( $"Particle2DPatterns: spiral angles must be finite, got start {startAngleDegrees}, step {angleStepDegrees}." );
		if ( !Particle2DMath.Finite( center ) ) throw new ArgumentException( $"Particle2DPatterns: center must be finite, got {center}." );
		CheckJitter( speedJitter, "speed jitter" );
		float sign = angleStepDegrees < 0 ? -1f : 1f;
		var outList = new List<Particle2DSpawn>( count );
		for ( int i = 0; i < count; i++ )
		{
			float r = startRadius + radiusStep * i;
			var radial = UnitFromAngle( startAngleDegrees + angleStepDegrees * i );
			var tangent = sign * new Vector2( -radial.Y, radial.X );
			float s = speedJitter > 0 ? speed * (1 + (rng.NextFloat() * 2 - 1) * speedJitter) : speed;
			outList.Add( new Particle2DSpawn( center + r * radial, s * tangent ) );
		}
		return outList;
	}

	/// <summary>Fountain: <paramref name="count"/> particles all starting at
	/// <paramref name="origin"/>, with velocities inside an upward cone.
	/// "Up" is -Y in Y-down space. <paramref name="coneAngleDegrees"/> is
	/// the FULL apex angle of the cone, in the open interval (0, 180), so
	/// the half-angle is under 90° and every velocity has a strictly
	/// negative Y component (genuinely upward-biased, never horizontal or
	/// downward). Each particle's direction angle is uniform in
	/// [-cone/2, +cone/2] about up, and its speed is uniform in
	/// [speedMin, speedMax].</summary>
	public static List<Particle2DSpawn> Fountain( Vector2 origin, float speedMin, float speedMax, float coneAngleDegrees, int count, ref ParticleRng rng )
	{
		Validate( count, speed: speedMax, coneAngleDegrees: coneAngleDegrees );
		if ( !float.IsFinite( speedMin ) || speedMin < 0 ) throw new ArgumentException( $"Particle2DPatterns: fountain speedMin must be finite and >= 0, got {speedMin}." );
		if ( speedMin > speedMax ) throw new ArgumentException( $"Particle2DPatterns: fountain speedMin ({speedMin}) must be <= speedMax ({speedMax})." );
		if ( !Particle2DMath.Finite( origin ) ) throw new ArgumentException( $"Particle2DPatterns: origin must be finite, got {origin}." );
		var up = new Vector2( 0, -1 );
		float half = coneAngleDegrees / 2;
		var outList = new List<Particle2DSpawn>( count );
		for ( int i = 0; i < count; i++ )
		{
			var dir = Rotate( up, rng.Range( -half, half ) );
			float s = speedMin < speedMax ? rng.Range( speedMin, speedMax ) : speedMin;
			outList.Add( new Particle2DSpawn( origin, s * dir ) );
		}
		return outList;
	}

	/// <summary>Spray: <paramref name="count"/> particles starting uniformly
	/// along the segment from <paramref name="lineStart"/> to
	/// <paramref name="lineEnd"/>, with velocities perpendicular to the
	/// segment plus spread. The perpendicular is the clockwise normal in
	/// Y-down space, (-segment.Y, segment.X) normalized — for an eastward
	/// segment that points down (+Y); swapping the segment endpoints flips
	/// the side. <paramref name="spreadDegrees"/> is the FULL angular
	/// spread about that perpendicular, in [0, 180] degrees, so velocities
	/// never cross to the segment's other side; 0 means exactly
	/// perpendicular. Speed is <paramref name="speed"/> times
	/// (1 ± speedJitter), uniformly drawn.</summary>
	public static List<Particle2DSpawn> Spray( Vector2 lineStart, Vector2 lineEnd, float speed, int count, ref ParticleRng rng, float spreadDegrees = 0f, float speedJitter = 0f )
	{
		Validate( count, speed: speed );
		if ( !Particle2DMath.Finite( lineStart ) || !Particle2DMath.Finite( lineEnd ) ) throw new ArgumentException( $"Particle2DPatterns: spray endpoints must be finite, got {lineStart} to {lineEnd}." );
		var segment = lineEnd - lineStart;
		if ( segment.LengthSquared() < 1e-12f ) throw new ArgumentException( "Particle2DPatterns: spray segment must have non-zero length (lineStart and lineEnd are equal, so no perpendicular exists)." );
		CheckSpread( spreadDegrees, "spray spread" );
		CheckJitter( speedJitter, "speed jitter" );
		var perp = new Vector2( -segment.Y, segment.X ) / segment.Length();
		float halfSpread = spreadDegrees / 2;
		var outList = new List<Particle2DSpawn>( count );
		for ( int i = 0; i < count; i++ )
		{
			var pos = lineStart + segment * rng.NextFloat();
			var dir = spreadDegrees > 0 ? Rotate( perp, rng.Range( -halfSpread, halfSpread ) ) : perp;
			float s = speedJitter > 0 ? speed * (1 + (rng.NextFloat() * 2 - 1) * speedJitter) : speed;
			outList.Add( new Particle2DSpawn( pos, s * dir ) );
		}
		return outList;
	}
}
