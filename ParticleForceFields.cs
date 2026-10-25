#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// Lane P22 (particles session, 2026-10-24): 3D force fields for the CPU particle sim.
// SELF-CONTAINED EVALUATORS ONLY — this file does not touch ParticleSystem.cs and is
// NOT wired into its force step. The coordinator sweep owns the hookup; the exact
// integration lines are listed at the bottom of this file.
//
// Conventions (same as ParticleSystem.cs): kernel inches, Z-up, seconds.
// Every evaluator is a pure function of particle position (velocity and time are
// accepted for signature uniformity and future velocity/time-dependent fields, but
// the fields in this file ignore them) returning an ACCELERATION in inches/s^2.
// A field never moves a particle by itself: the sim adds the returned acceleration
// to its per-substep acceleration sum and integrates it at the 60 Hz fixed step,
// exactly like gravity and the vortex term in ParticleSystem.Integrate.
//
// Falloff modes (point fields) and the Radius contract:
//   - Radius is the field's RANGE in every mode: beyond Radius the field is zero.
//   - Constant: full Strength everywhere inside Radius.
//   - Linear: Strength at the centre, falling linearly to zero at Radius.
//   - InverseSquare: Strength / d^2 inside Radius, where Strength is the
//     acceleration ONE INCH from the point (so Strength has units in/s^2 and the
//     1/d^2 shape carries the inches). A particle at distance d gets
//     Strength * (1 inch / d)^2 toward/away from the point.
// Minimum-distance clamp (InverseSquare + gravity well): distances below
// MinDistance are treated as MinDistance, so the 1/d^2 term can never explode.
// A particle EXACTLY on the point has no defined direction, so these fields
// return Vector3.Zero there (finite, never NaN/Infinity) — one inch away in any
// direction gives the full clamped strength. That zero-at-centre rule is tested.
//
// Out of scope, by lane boundary: 2D fields (lane P12), turbulence noise itself,
// vortex variants, wind gusts, save/serialization (lane 5), and the sim-step
// wiring (sweep only, below).

/// <summary>Distance falloff shape for point force fields.</summary>
public enum ParticleForceFalloff
{
	/// <summary>Full strength everywhere inside <see cref="ParticleAttractorField.Radius"/>.</summary>
	Constant,
	/// <summary>Strength at the centre, linear to zero at <see cref="ParticleAttractorField.Radius"/>.</summary>
	Linear,
	/// <summary>Strength / d^2 inside Radius; Strength is the acceleration at 1 inch.</summary>
	InverseSquare,
}

/// <summary>
/// A pure acceleration field: position (+ velocity, time) in, acceleration out.
/// Implementations must be pure (no RNG, no mutation) and finite for every finite
/// input, including a particle exactly on a point field's centre.
/// </summary>
public interface IParticleForceField
{
	Vector3 Acceleration( Vector3 position, Vector3 velocity, float time );
	void Validate();
}

/// <summary>Shared validation helpers for this file (kept local so the file stays self-contained).</summary>
internal static class ParticleForceFieldChecks
{
	public static bool Finite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );

	public static void NeedFinite( Vector3 v, string name )
	{
		if ( !Finite( v ) ) throw new ArgumentException( $"{name} must be finite." );
	}

	public static void NeedNonNegative( float value, string name )
	{
		if ( !float.IsFinite( value ) || value < 0 ) throw new ArgumentOutOfRangeException( name, $"{name} must be finite and >= 0." );
	}

	public static void NeedPositive( float value, string name )
	{
		if ( !float.IsFinite( value ) || value <= 0 ) throw new ArgumentOutOfRangeException( name, $"{name} must be finite and > 0." );
	}
}

/// <summary>
/// Point attractor: pulls particles toward <see cref="Center"/>.
/// Strength &gt;= 0 (a negative strength would silently act as a repeller, so
/// <see cref="Validate"/> rejects it loudly instead of flipping the sign).
/// </summary>
public sealed class ParticleAttractorField : IParticleForceField
{
	public Vector3 Center;
	public float Strength;
	public float Radius = 10f;
	public ParticleForceFalloff Falloff = ParticleForceFalloff.Linear;
	/// <summary>Clamp floor for the 1/d^2 term (InverseSquare only); must be &gt; 0. Default 1 inch.</summary>
	public float MinDistance = 1f;

	public void Validate()
	{
		ParticleForceFieldChecks.NeedFinite( Center, nameof( Center ) );
		ParticleForceFieldChecks.NeedNonNegative( Strength, nameof( Strength ) );
		ParticleForceFieldChecks.NeedPositive( Radius, nameof( Radius ) );
		ParticleForceFieldChecks.NeedPositive( MinDistance, nameof( MinDistance ) );
	}

	public Vector3 Acceleration( Vector3 position, Vector3 velocity, float time )
	{
		Vector3 r = Center - position;
		float d = r.Length();
		if ( d > Radius || d <= 0 ) return Vector3.Zero;
		float magnitude = Falloff switch
		{
			ParticleForceFalloff.Constant => Strength,
			ParticleForceFalloff.Linear => Strength * (1f - d / Radius),
			ParticleForceFalloff.InverseSquare => Strength / (MathF.Max( d, MinDistance ) * MathF.Max( d, MinDistance )),
			_ => throw new InvalidOperationException( $"Unknown falloff {Falloff}." ),
		};
		return r / d * magnitude;
	}
}

/// <summary>
/// Point repeller: the attractor's shape with the opposite sign — pushes particles
/// away from <see cref="Center"/> with the same falloff options and the same
/// minimum-distance clamp. Strength &gt;= 0; the direction, not a negative
/// strength, is what makes it repel.
/// </summary>
public sealed class ParticleRepellerField : IParticleForceField
{
	public Vector3 Center;
	public float Strength;
	public float Radius = 10f;
	public ParticleForceFalloff Falloff = ParticleForceFalloff.Linear;
	/// <summary>Clamp floor for the 1/d^2 term (InverseSquare only); must be &gt; 0. Default 1 inch.</summary>
	public float MinDistance = 1f;

	public void Validate()
	{
		ParticleForceFieldChecks.NeedFinite( Center, nameof( Center ) );
		ParticleForceFieldChecks.NeedNonNegative( Strength, nameof( Strength ) );
		ParticleForceFieldChecks.NeedPositive( Radius, nameof( Radius ) );
		ParticleForceFieldChecks.NeedPositive( MinDistance, nameof( MinDistance ) );
	}

	public Vector3 Acceleration( Vector3 position, Vector3 velocity, float time )
	{
		Vector3 r = position - Center;
		float d = r.Length();
		if ( d > Radius || d <= 0 ) return Vector3.Zero;
		float magnitude = Falloff switch
		{
			ParticleForceFalloff.Constant => Strength,
			ParticleForceFalloff.Linear => Strength * (1f - d / Radius),
			ParticleForceFalloff.InverseSquare => Strength / (MathF.Max( d, MinDistance ) * MathF.Max( d, MinDistance )),
			_ => throw new InvalidOperationException( $"Unknown falloff {Falloff}." ),
		};
		return r / d * magnitude;
	}
}

/// <summary>
/// Box-shaped wind zone: adds a constant <see cref="WindAcceleration"/> while the
/// particle is inside the box (Min..Max, inclusive).
/// BOUNDARY BEHAVIOUR, picked and tested: feathered edge when
/// <see cref="Feather"/> &gt; 0, hard edge when it is 0.
///   - Feathered: weight ramps from 0 exactly ON a face to full Feather inches
///     inside it, per axis, multiplied across axes (so corners round off);
///     outside the box is always 0 — there is no halo beyond the box. A Feather
///     larger than a half-extent is clamped to that half-extent on that axis,
///     so the core never inverts.
///   - Hard (Feather = 0): full acceleration everywhere inside including the
///     faces, zero outside — a step, not a ramp.
/// </summary>
public sealed class ParticleWindZoneField : IParticleForceField
{
	public Vector3 Min;
	public Vector3 Max;
	/// <summary>Constant acceleration added inside the zone, in/s^2 (NOT a wind velocity — unlike ParticleSystemSettings.Wind, which only acts through drag).</summary>
	public Vector3 WindAcceleration;
	/// <summary>Edge ramp width in inches; 0 = hard edge. Must be &gt;= 0.</summary>
	public float Feather;

	public void Validate()
	{
		ParticleForceFieldChecks.NeedFinite( Min, nameof( Min ) );
		ParticleForceFieldChecks.NeedFinite( Max, nameof( Max ) );
		ParticleForceFieldChecks.NeedFinite( WindAcceleration, nameof( WindAcceleration ) );
		ParticleForceFieldChecks.NeedNonNegative( Feather, nameof( Feather ) );
		if ( Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z )
			throw new ArgumentException( "Wind zone box is inverted: Min must be <= Max on every axis." );
	}

	static float AxisWeight( float p, float min, float max, float feather )
	{
		if ( p < min || p > max ) return 0f;
		if ( feather <= 0 ) return 1f;
		float extent = max - min;
		if ( extent <= 0 ) return 1f;
		float f = MathF.Min( feather, extent * 0.5f );
		float fromEdge = MathF.Min( p - min, max - p );
		return MathF.Min( 1f, fromEdge / f );
	}

	public Vector3 AccelerationAt( Vector3 position ) => WindAcceleration * (AxisWeight( position.X, Min.X, Max.X, Feather ) * AxisWeight( position.Y, Min.Y, Max.Y, Feather ) * AxisWeight( position.Z, Min.Z, Max.Z, Feather ));

	public Vector3 Acceleration( Vector3 position, Vector3 velocity, float time ) => AccelerationAt( position );
}

/// <summary>
/// Gravity well: a point field with pure inverse-square falloff, tuned for
/// orbital-ish behaviour. Mu is the gravitational parameter in inches^3/s^2
/// (acceleration at distance d is Mu / d^2 toward the centre, clamped below by
/// <see cref="MinDistance"/>). This is an ACCELERATION FIELD, not an orbital
/// integrator: closed orbits only emerge once a sim integrates it, and a fixed
/// 60 Hz step near the centre will not conserve energy exactly — the clamp is
/// what keeps close passes finite, not accurate.
/// </summary>
public sealed class ParticleGravityWellField : IParticleForceField
{
	public Vector3 Center;
	/// <summary>Gravitational parameter, in^3/s^2. Must be &gt;= 0.</summary>
	public float Mu;
	/// <summary>Clamp floor for the 1/d^2 term; must be &gt; 0. Default 1 inch.</summary>
	public float MinDistance = 1f;

	public void Validate()
	{
		ParticleForceFieldChecks.NeedFinite( Center, nameof( Center ) );
		ParticleForceFieldChecks.NeedNonNegative( Mu, nameof( Mu ) );
		ParticleForceFieldChecks.NeedPositive( MinDistance, nameof( MinDistance ) );
	}

	public Vector3 Acceleration( Vector3 position, Vector3 velocity, float time )
	{
		Vector3 r = Center - position;
		float d = r.Length();
		if ( d <= 0 ) return Vector3.Zero;
		float dEff = MathF.Max( d, MinDistance );
		return r / d * (Mu / (dEff * dEff));
	}
}

/// <summary>
/// Sums any list of fields for one particle: the combined acceleration is the
/// vector sum of every field's acceleration at that position. An empty list
/// sums to zero. A null entry fails loudly from <see cref="Validate"/> (and
/// from evaluation, via NullReference-free explicit check) instead of being
/// silently skipped.
/// </summary>
public sealed class ParticleCombinedField : IParticleForceField
{
	public IReadOnlyList<IParticleForceField> Fields;

	public ParticleCombinedField( IEnumerable<IParticleForceField> fields )
	{
		ArgumentNullException.ThrowIfNull( fields );
		var list = new List<IParticleForceField>( fields );
		Fields = list;
	}

	public void Validate()
	{
		for ( int i = 0; i < Fields.Count; i++ )
		{
			if ( Fields[i] is null ) throw new ArgumentException( $"Combined field entry {i} is null." );
			Fields[i].Validate();
		}
	}

	public Vector3 Acceleration( Vector3 position, Vector3 velocity, float time )
	{
		Vector3 sum = Vector3.Zero;
		for ( int i = 0; i < Fields.Count; i++ )
		{
			if ( Fields[i] is null ) throw new ArgumentException( $"Combined field entry {i} is null." );
			sum += Fields[i].Acceleration( position, velocity, time );
		}
		return sum;
	}
}

/// <summary>Factory for combined fields.</summary>
public static class ParticleForceFields
{
	/// <summary>Returns a field whose acceleration is the vector sum of <paramref name="fields"/>.</summary>
	public static ParticleCombinedField Combine( IEnumerable<IParticleForceField> fields ) => new( fields );
}

// ---------------------------------------------------------------------------
// SWEEP INTEGRATION LINES (coordinator only — this lane made NO edit to
// ParticleSystem.cs). To wire fields into the 3D sim's force step:
//
// 1. ParticleSystemSettings (near the Vortex block): add
//      /// <summary>Extra acceleration fields, evaluated per substep in Integrate. Empty = off.</summary>
//      public List<IParticleForceField> ForceFields = new();
//    and in Validate(), after the vortex checks, add
//      for ( int i = 0; i < ForceFields.Count; i++ ) ForceFields[i].Validate();
//    (a null entry must throw there, not at sim time).
//
// 2. SimParams (readonly struct, next to VortexRadius2): add
//      public readonly IParticleForceField? ForceField;
//    and in its constructor, after the VortexRadius2 assignment, add
//      ForceField = s.ForceFields.Count == 0 ? null : new ParticleCombinedField( s.ForceFields );
//    (combining once per system build, not once per particle per substep).
//
// 3. ParticleSystem.Integrate, immediately after the vortex block
//    (acc += Vector3.Cross(...) ...) and before the semi-implicit drag line
//    (vel = (vel + (acc + p.Wind * p.Drag) * h) / ...), add
//      if ( p.ForceField is not null ) acc += p.ForceField.Acceleration( pos, vel, time );
//    Field acceleration joins gravity/turbulence/vortex BEFORE drag, so drag
//    still pulls velocity toward Wind afterwards, exactly like the existing
//    forces. NOTE: this changes Integrate, so the GpuParticleSystem GLSL mirror
//    note at the top of ParticleSystem.cs applies — the GPU shader does NOT
//    mirror these fields (out of this lane's scope; GPU parity lane owns it).
// ---------------------------------------------------------------------------
