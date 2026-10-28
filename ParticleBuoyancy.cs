#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// P66 (particles session, 2026-10-24): 3D buoyancy, pure evaluators only.
// Data + math, no sim edits: nothing here touches ParticleSystem, and no
// ParticleState field is invented. ParticleState carries NO per-particle
// density (Position, Age, Velocity, Alive, Rotation, AngularVelocity, Size
// only), so every evaluator below takes particle density as an explicit
// caller-supplied input. The sweep decision this forces is stated at the
// bottom of this header: per-particle density field vs caller-supplied
// density. Until the sweep decides, the sim-side hookup is: the sim's
// gravity input becomes this lane's net acceleration, per particle or per
// medium (see "SIM HOOKUP" below).
//
// UNITS: kernel inches, Z-up, seconds. Densities are kg/m^3 on P38's scale
// (air 1.2, water 1000). Gravity is an acceleration vector, in/s^2; Earth
// surface gravity is (0, 0, -386.0886). Buoyant lift is exactly opposite
// the gravity vector, so in this Z-up world it is +Z.
//
// PHYSICS — pure Archimedes, no added mass, no drag (drag is P67's lane):
//   Let g = gravity vector, rho_m = medium density, rho_p = particle density.
//   Buoyant acceleration  b = -g * (rho_m / rho_p)
//   Net acceleration      a = g + b = g * (1 - rho_m / rho_p)
// The volume cancels: buoyant force = rho_m * V * |g|, mass = rho_p * V,
// so acceleration depends only on the density ratio, never on size. The
// formulas are vector formulas: they hold for any gravity direction, and
// the Z component alone is NetAccelerationZ / BuoyantAccelerationZ.
//
// CONSEQUENCES, all exact in the formula (proved by test, not approximate):
//   - Neutral buoyancy, rho_p == rho_m: rho_m / rho_p is exactly 1.0 in
//     IEEE float division of equal finite values, so a = g * 0 = exactly 0.
//   - Lighter particle, rho_p < rho_m: ratio > 1, (1 - ratio) < 0, and
//     since g points down (-Z), a points UP (+Z). The particle rises.
//   - Heavier particle, rho_p > rho_m: ratio < 1, a keeps g's sign: sinks.
//   - Vacuum / zero-density medium, rho_m == 0: b = 0 exactly, a = g
//     exactly, for any particle density. A zero medium density is therefore
//     ALLOWED here (unlike P38's bundle, which validates Density > 0,
//     because a bundle is a medium, not nothing — see below).
//
// ZERO PARTICLE DENSITY — REJECTED, by decision (not maximally buoyant):
// rho_p == 0 would divide by zero and give infinite lift, and a massless
// particle has no defined acceleration under Archimedes. Negative,
// non-finite, or zero particle density throws ArgumentOutOfRangeException.
// Negative or non-finite medium density throws the same; non-finite
// gravity throws ArgumentException. A non-finite RESULT (overflow from an
// extreme-but-finite ratio, e.g. rho_m / rho_p overflowing float) throws
// InvalidOperationException rather than silently returning Infinity.
//
// TERMINAL VELOCITY (linear drag only; quadratic drag is P67's lane):
//   At terminal velocity the net acceleration is balanced by linear drag:
//     dv/dt = a_net - k * v = 0  =>  v_t = a_net / k
//   where a_net is the (signed, Z) net acceleration and k is the linear
//   drag coefficient in 1/s (ParticleSystemSettings.Drag, or a bundle's
//   DragLinear). Sign follows a_net: a rising particle has a positive
//   terminal velocity, a sinking one negative, a neutrally buoyant one
//   exactly 0. k == 0 means no terminal velocity exists (drag never
//   balances anything), so TerminalVelocity throws InvalidOperationException
//   instead of returning Infinity; k < 0 or non-finite throws
//   ArgumentOutOfRangeException. This is a steady-state balance, not a
//   time integration: it says nothing about HOW FAST the particle gets
//   there (that time constant is 1/k).
//
// P38 RECONCILIATION — READ THIS BEFORE WIRING ANYTHING:
//   P38's MediumBundle.Buoyancy is NOT this lane's buoyant acceleration.
//   It is an acceleration in in/s^2 for the bundle's REFERENCE particle
//   (a bubble in water: 450 in/s^2, net gravity +63.9114), tuned so the
//   bubble case looks right — it is NOT Archimedes at any physical bubble
//   density. Proof: Archimedes at the water bundle's own density
//   (rho_m = 1000) with a real air bubble (rho_p = 1.2) gives lift
//   386.0886 * 1000 / 1.2 = 321740.5 in/s^2, not 450. P38's 450 equals
//   Archimedes only at one reference density, ReferenceDensity(bundle):
//     rho* = rho_m * |g| / Buoyancy = 1000 * 386.0886 / 450 = 857.9747 kg/m^3.
//   So P38's bubble behaves, under pure Archimedes, like a particle of
//   density ~858 kg/m^3 — slightly lighter than water, NOT like air.
//   Corollaries the sweep MUST respect:
//     (a) At neutral density this lane says net = 0 while P38's water
//         bundle says net = +63.9114. Both are right under their own
//         definitions; they disagree because P38's reference particle
//         is not neutrally buoyant. Do not "fix" either number.
//     (b) NEVER ADD the two lifts. Net = g + bundle.Buoyancy + P66 lift
//         double-counts buoyancy. The sweep must pick ONE per particle:
//         either the bundle's reference lift (today's behaviour, keeps
//         bubbles rising at +63.9114) or this lane's density-corrected
//         net (per-particle, needs the density decision below).
//     (c) NetAcceleration(bundle, particleDensity) deliberately IGNORES
//         bundle.Buoyancy and uses only bundle.Gravity + bundle.Density.
//         Two bundles differing only in Buoyancy give identical results
//         here — that is the point, and it is tested.
//     (d) ReferenceDensity throws InvalidOperationException when the
//         bundle has Buoyancy == 0 (air, space, zero-G: zero lift would
//         need infinite particle density) or zero gravity (no lift to
//         match against). The reconciliation the sweep must make is:
//         keep P38's tuned reference lifts as the per-medium default AND
//         use P66 where a particle density is known (replacing, never
//         adding), OR recalibrate bundle Buoyancy values to Archimedes
//         at a chosen reference density. This lane does not choose.
//   P38's bundles are NOT redefined here: no bundle constant is changed,
//   and this file never writes into a MediumBundle.
//
// WHY THIS DIFFERS FROM P75's 2D FORMULA — deliberately, not drift:
//   Particle2DMedium.NetAcceleration(p) = G * (p - B*Density) / (p + 0.5*B*Density),
//   where B is a DIMENSIONLESS factor in [0, 1] and Density is a RELATIVE
//   density (air = 1), in Y-down canvas pixels. Three differences:
//     (1) The 2D formula carries a 0.5 added-mass (virtual mass) term in
//         the denominator: a sphere accelerating through fluid drags
//         fluid with it. This 3D lane uses pure Archimedes (no added
//         mass) because ParticleSystem integrates plain acceleration and
//         has no added-mass term anywhere; smuggling 0.5 in here would
//         change transient response with no consumer expecting it.
//     (2) The 2D B is a stylised 0..1 tuning factor; the 3D inputs are
//         absolute kg/m^3 densities and a real gravity vector. The two
//         "buoyancy" words name different quantities — do not equate them.
//     (3) Both formulas agree at neutral buoyancy (net exactly 0) and on
//         rise/sink signs; they differ in MAGNITUDE away from neutral
//         (the 2D denominator bounds the lift; pure Archimedes does not —
//         see LIMITS). If the sweep ever wants 2D/3D parity, that is a
//         follow-up lane that adds added mass to BOTH sides explicitly,
//         not a silent edit to this file.
//
// SIM HOOKUP (reported, not made — this lane edits no existing file):
//   The sim's gravity input becomes this lane's net acceleration:
//     settings.Gravity = ParticleBuoyancy.NetAcceleration( bundle, density )
//   for a single-density system, or per particle (needs the density-field
//   decision) as settings.Gravity = NetAcceleration( gravity, rho_m, rho_p )
//   with the particle's own density each step. Until then, a caller that
//   knows only the medium uses the bundle path; a caller that knows a
//   particle density uses the raw path. Drag for the terminal-velocity
//   check comes from the bundle's DragLinear / settings.Drag (P67 owns
//   the drag models; this lane only balances against a caller-supplied k).
//
// LIMITS, stated honestly:
//   - Pure Archimedes gives enormous lift for near-zero-density particles
//     (an air bubble in water: +321354 in/s^2 net). Real bubbles do not
//     accelerate like that because quadratic drag (P67) caps them almost
//     immediately; the linear-drag terminal velocity for such a particle
//     is likewise enormous and should not be quoted as a physical speed
//     without quadratic drag. The evaluators are exact; the physical
//     regime they are exact IN is "before drag dominates".
//   - No added mass, no surface tension, no viscosity beyond the
//     caller-supplied linear k, no buoyancy varying with depth or
//     temperature, no partially-submerged particles (a particle is
//     wholly in one medium here; interface crossing is a future lane).
//   - Headless only. No UI, no script verb, no save format. The sweep
//     owns the TestRegistry line and the density-field decision.
//
// OPEN DECISION FOR THE SWEEP (do not resolve silently):
//   Per-particle density field on ParticleState (a new float, breaking
//   the 64-byte GPU layout and needing a layout migration) vs
//   caller-supplied density (this lane's API as-is, per-system or
//   per-call). This lane RECOMMENDS caller-supplied density for now:
//   the 64-byte layout is load-bearing for GPU upload, most systems are
//   single-medium and single-material anyway (one density per system
//   is enough for bubbles, dust, droplets), and a state field can be
//   added later without changing any formula here. If the sweep adds
//   the field, these evaluators are consumed unchanged, one call per
//   particle per step.

/// <summary>Pure 3D buoyancy evaluators: Archimedes lift + net acceleration + linear-drag terminal velocity.</summary>
public static class ParticleBuoyancy
{
	/// <summary>Earth surface gravity magnitude in in/s^2, for reference in tests and callers.</summary>
	public const float EarthGravity = 386.0886f;

	static void ValidateGravity( Vector3 gravity )
	{
		if ( !float.IsFinite( gravity.X ) || !float.IsFinite( gravity.Y ) || !float.IsFinite( gravity.Z ) )
			throw new ArgumentException( "ParticleBuoyancy gravity must be finite." );
	}

	static void ValidateDensities( float mediumDensity, float particleDensity )
	{
		if ( !float.IsFinite( mediumDensity ) || mediumDensity < 0 )
			throw new ArgumentOutOfRangeException( nameof( mediumDensity ), "Medium density must be finite and >= 0 kg/m^3 (0 = vacuum)." );
		if ( !float.IsFinite( particleDensity ) || particleDensity <= 0 )
			throw new ArgumentOutOfRangeException( nameof( particleDensity ), "Particle density must be finite and > 0 kg/m^3 (zero density is rejected, not maximally buoyant)." );
	}

	static float CheckFinite( float value, string what )
	{
		if ( !float.IsFinite( value ) )
			throw new InvalidOperationException( $"ParticleBuoyancy {what} overflowed to a non-finite value for these densities." );
		return value;
	}

	static Vector3 CheckFinite( Vector3 value, string what )
	{
		if ( !float.IsFinite( value.X ) || !float.IsFinite( value.Y ) || !float.IsFinite( value.Z ) )
			throw new InvalidOperationException( $"ParticleBuoyancy {what} overflowed to a non-finite value for these densities." );
		return value;
	}

	/// <summary>Buoyant acceleration vector: -gravity * (mediumDensity / particleDensity). Exactly opposite gravity; +Z lift under -Z gravity. Vacuum (medium 0) gives exactly zero.</summary>
	public static Vector3 BuoyantAcceleration( Vector3 gravity, float mediumDensity, float particleDensity )
	{
		ValidateGravity( gravity );
		ValidateDensities( mediumDensity, particleDensity );
		return CheckFinite( -gravity * ( mediumDensity / particleDensity ), "buoyant acceleration" );
	}

	/// <summary>Buoyant acceleration, Z component only: -gravityZ * (mediumDensity / particleDensity).</summary>
	public static float BuoyantAccelerationZ( float gravityZ, float mediumDensity, float particleDensity )
	{
		if ( !float.IsFinite( gravityZ ) )
			throw new ArgumentException( "ParticleBuoyancy gravityZ must be finite." );
		ValidateDensities( mediumDensity, particleDensity );
		return CheckFinite( -gravityZ * ( mediumDensity / particleDensity ), "buoyant acceleration" );
	}

	/// <summary>Net acceleration vector: gravity * (1 - mediumDensity / particleDensity). Exactly zero at neutral buoyancy, +Z (up) for lighter particles, gravity-signed for heavier ones. Vacuum gives gravity exactly.</summary>
	public static Vector3 NetAcceleration( Vector3 gravity, float mediumDensity, float particleDensity )
	{
		ValidateGravity( gravity );
		ValidateDensities( mediumDensity, particleDensity );
		return CheckFinite( gravity * ( 1f - mediumDensity / particleDensity ), "net acceleration" );
	}

	/// <summary>Net acceleration, Z component only: gravityZ * (1 - mediumDensity / particleDensity).</summary>
	public static float NetAccelerationZ( float gravityZ, float mediumDensity, float particleDensity )
	{
		if ( !float.IsFinite( gravityZ ) )
			throw new ArgumentException( "ParticleBuoyancy gravityZ must be finite." );
		ValidateDensities( mediumDensity, particleDensity );
		return CheckFinite( gravityZ * ( 1f - mediumDensity / particleDensity ), "net acceleration" );
	}

	/// <summary>Net acceleration for a particle of the given density in a P38 medium bundle. Uses ONLY bundle.Gravity + bundle.Density — bundle.Buoyancy is deliberately ignored (see header reconciliation); never add the two lifts.</summary>
	public static Vector3 NetAcceleration( MediumBundle bundle, float particleDensity )
	{
		bundle.Validate();
		return NetAcceleration( bundle.Gravity, bundle.Density, particleDensity );
	}

	/// <summary>Net acceleration, Z component only, for a particle in a P38 bundle (bundle.Buoyancy ignored, as above).</summary>
	public static float NetAccelerationZ( MediumBundle bundle, float particleDensity )
	{
		bundle.Validate();
		return NetAccelerationZ( bundle.Gravity.Z, bundle.Density, particleDensity );
	}

	/// <summary>Buoyant acceleration for a particle in a P38 bundle (bundle.Buoyancy ignored, as above).</summary>
	public static Vector3 BuoyantAcceleration( MediumBundle bundle, float particleDensity )
	{
		bundle.Validate();
		return BuoyantAcceleration( bundle.Gravity, bundle.Density, particleDensity );
	}

	/// <summary>Terminal velocity (signed, in/s, Z component) under linear drag: v_t = netAccelerationZ / dragLinear. Zero net gives exactly 0; dragLinear == 0 throws (no terminal velocity exists); negative or non-finite inputs throw.</summary>
	public static float TerminalVelocity( float netAccelerationZ, float dragLinear )
	{
		if ( !float.IsFinite( netAccelerationZ ) )
			throw new ArgumentOutOfRangeException( nameof( netAccelerationZ ), "Net acceleration must be finite." );
		if ( !float.IsFinite( dragLinear ) || dragLinear < 0 )
			throw new ArgumentOutOfRangeException( nameof( dragLinear ), "Linear drag must be finite and >= 0 /s." );
		if ( dragLinear == 0 )
			throw new InvalidOperationException( "No terminal velocity exists with zero linear drag: drag never balances net acceleration." );
		return CheckFinite( netAccelerationZ / dragLinear, "terminal velocity" );
	}

	/// <summary>Terminal velocity for a particle of the given density in a P38 bundle, balancing this lane's net acceleration against the bundle's DragLinear.</summary>
	public static float TerminalVelocity( MediumBundle bundle, float particleDensity )
	{
		bundle.Validate();
		return TerminalVelocity( NetAccelerationZ( bundle, particleDensity ), bundle.DragLinear );
	}

	/// <summary>The particle density at which pure Archimedes lift equals the bundle's tuned Buoyancy: rho* = rho_m * |g| / Buoyancy. For water this is 857.9747 kg/m^3 — the density P38's bubble implicitly behaves as. Throws when Buoyancy == 0 (no finite density gives zero lift except infinity) or gravity is zero.</summary>
	public static float ReferenceDensity( MediumBundle bundle )
	{
		bundle.Validate();
		if ( bundle.Buoyancy == 0 )
			throw new InvalidOperationException( "A bundle with zero Buoyancy has no finite Archimedean reference density (zero lift would need infinite particle density)." );
		float g = bundle.Gravity.Length();
		if ( g == 0 )
			throw new InvalidOperationException( "A bundle with zero gravity has no Archimedean reference density (there is no lift to match)." );
		return CheckFinite( bundle.Density * g / bundle.Buoyancy, "reference density" );
	}
}
