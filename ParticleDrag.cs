using System;
using System.Numerics;

// Lane P67 (particles session, 2026-10-24): drag models, 3D, pure evaluators only.
// Units throughout: kernel inches, Z-up, seconds. Velocities in in/s, accelerations
// in in/s^2. No state, no RNG (never System.Random), no game loop, no wiring:
// these functions are NOT called by ParticleSystem.Integrate yet. The sweep owns
// that hookup (exact lines are reported with this lane, never edited in here).
//
// How this relates to the sim's existing wind (read ParticleSystem.cs first):
// ParticleSystemSettings documents Wind as "only acts through Drag (drag pulls
// velocity toward wind)" — wind is NOT an independent acceleration, and nothing
// here contradicts that. Every evaluator below is a pure function of the
// RELATIVE velocity (velocity - wind) and returns an acceleration that opposes
// that relative velocity, i.e. it always pulls the particle's velocity toward
// the wind velocity, never away from it. When velocity == wind the relative
// velocity is zero and every model returns exactly zero.
//   - Linear below is the explicit-acceleration form of the sim's existing
//     linear drag: ParticleSystem.Integrate integrates it semi-implicitly as
//     vel = (vel + (acc + Wind * Drag) * h) / (1 + Drag * h), which is the
//     implicit form of a = -Drag * (v - wind) plus the other accelerations.
//     Same model, different integration form; the two must agree in the limit
//     of small h, they are not two different physics.
//   - Quadratic and Stokes are NEW models: neither is in Integrate today.
//     Quadratic dominates at high speed (it grows with the square of the
//     relative speed); Stokes is the low-Reynolds-number linear regime and
//     differs from Linear in USE, not in shape: Linear takes one coefficient
//     for the whole system, Stokes takes a per-particle coefficient and mass,
//     so two particles in the same fluid with different masses (or sizes,
//     folded into the coefficient) feel different drag from the same wind.
//   - Combined is Linear + Quadratic only, matching the medium bundle shape
//     {gravity, dragLinear, dragQuadratic, buoyancy, density} (P38's bundle):
//     buoyancy belongs to P66, not here, and is deliberately not summed in.
//     Coefficients are plain parameters so this evaluator works standalone
//     whether or not P38's bundle file exists yet.
//
// Stokes form, stated once and used everywhere: a = -stokesCoefficient
// * (v - wind) / mass, where stokesCoefficient is the full Stokes factor
// 6 * pi * eta * r (fluid viscosity eta, particle radius r, folded into one
// number). No viscosity value is assumed or hidden in here: a caller that
// wants a different fluid or a different particle radius supplies a different
// coefficient. Consequence, stated plainly: the sim defines no mass unit, so
// stokesCoefficient and mass must use CONSISTENT mass units (whatever unit
// the caller chose for mass, the coefficient's mass part must match it), or
// the result is meaningless. If the coefficient and the mass disagree on
// units, that is a caller error this file cannot detect.
// Out of scope, named so they do not creep in: buoyancy (P66), turbulence /
// vortex (P29/P64), wind gusts (P65), any GPU code, any save/serialization,
// any canvas/UI, any change to ParticleSystem.Integrate or its settings.
namespace Marionette.Desktop;

public static class ParticleDrag
{
	// Linear drag: a = -dragLinear * (v - wind).
	// dragLinear units: 1/s. At dragLinear = 2/s and a relative velocity of
	// 10 in/s along an axis, the acceleration is -20 in/s^2 along that axis.
	// Zero dragLinear is legal (drag off) and returns exactly zero.
	public static Vector3 Linear( Vector3 velocity, Vector3 wind, float dragLinear )
	{
		ValidateCoefficient( dragLinear, nameof( dragLinear ) );
		ValidateVelocities( velocity, wind );
		return -dragLinear * ( velocity - wind );
	}

	// Quadratic drag: a = -dragQuadratic * |v - wind| * (v - wind).
	// dragQuadratic units: 1/in, so (1/in) * (in/s) * (in/s) = in/s^2.
	// It dominates at high speed: doubling the relative speed quadruples the
	// magnitude (linear drag only doubles it). At low speed it fades faster
	// than linear drag, which is why real medium presets carry both.
	// Zero dragQuadratic is legal (drag off) and returns exactly zero.
	public static Vector3 Quadratic( Vector3 velocity, Vector3 wind, float dragQuadratic )
	{
		ValidateCoefficient( dragQuadratic, nameof( dragQuadratic ) );
		ValidateVelocities( velocity, wind );
		Vector3 rel = velocity - wind;
		return -dragQuadratic * rel.Length() * rel;
	}

	// Stokes drag (low-Reynolds-number linear regime):
	// a = -stokesCoefficient * (v - wind) / mass.
	// stokesCoefficient is 6 * pi * eta * r (see the header note: no viscosity
	// is assumed here; the caller folds their fluid and radius into this one
	// number). Its units are (mass / s), so that dividing by mass leaves 1/s
	// times a velocity, i.e. in/s^2. Mass must be positive and finite; a zero
	// or negative mass is rejected, never clamped. Zero stokesCoefficient is
	// legal (drag off) and returns exactly zero.
	// How it differs from Linear in use, although both are linear in relative
	// velocity: Linear's coefficient is system-wide, Stokes' acceleration
	// depends on the individual particle (its mass, and its size via the
	// coefficient), so a heavy particle and a light particle in the same
	// fluid drag differently. Use Linear for a whole-system feel, Stokes when
	// per-particle mass/size matters.
	public static Vector3 Stokes( Vector3 velocity, Vector3 wind, float stokesCoefficient, float mass )
	{
		ValidateCoefficient( stokesCoefficient, nameof( stokesCoefficient ) );
		if ( !float.IsFinite( mass ) || mass <= 0 )
			throw new ArgumentOutOfRangeException( nameof( mass ), "Stokes drag needs a positive, finite mass." );
		ValidateVelocities( velocity, wind );
		return -stokesCoefficient / mass * ( velocity - wind );
	}

	// Combined drag: Linear + Quadratic, the two coefficients the sim's
	// medium bundle carries. Buoyancy is NOT included (P66 owns it).
	// Zero coefficients are legal and return exactly zero.
	public static Vector3 Combined( Vector3 velocity, Vector3 wind, float dragLinear, float dragQuadratic )
	{
		return Linear( velocity, wind, dragLinear ) + Quadratic( velocity, wind, dragQuadratic );
	}

	// Validate the linear + quadratic coefficient pair (the Combined inputs).
	// Loud failure only: negative or non-finite coefficients throw; zero is
	// legal. Nothing is clamped, nothing is silently corrected.
	public static void Validate( float dragLinear, float dragQuadratic )
	{
		ValidateCoefficient( dragLinear, nameof( dragLinear ) );
		ValidateCoefficient( dragQuadratic, nameof( dragQuadratic ) );
	}

	// Validate the Stokes inputs. Loud failure only: negative or non-finite
	// coefficient, or non-positive / non-finite mass, throw; zero coefficient
	// (with a valid mass) is legal.
	public static void ValidateStokes( float stokesCoefficient, float mass )
	{
		ValidateCoefficient( stokesCoefficient, nameof( stokesCoefficient ) );
		if ( !float.IsFinite( mass ) || mass <= 0 )
			throw new ArgumentOutOfRangeException( nameof( mass ), "Stokes drag needs a positive, finite mass." );
	}

	static void ValidateCoefficient( float value, string name )
	{
		if ( !float.IsFinite( value ) || value < 0 )
			throw new ArgumentOutOfRangeException( name, "Drag coefficients must be finite and >= 0 (zero = drag off)." );
	}

	static void ValidateVelocities( Vector3 velocity, Vector3 wind )
	{
		if ( !ParticleMath.Finite( velocity ) )
			throw new ArgumentException( "Velocity must be finite.", nameof( velocity ) );
		if ( !ParticleMath.Finite( wind ) )
			throw new ArgumentException( "Wind velocity must be finite.", nameof( wind ) );
	}
}
