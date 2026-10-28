#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// P38 (particles session, 2026-10-24): the medium standard.
//
// CORRECTION FIRST: no cross-engine "medium" standard exists. There is no
// Unity/Unreal/Godot medium type to copy. This file IS the standard for
// Geppetto: one explicit, inspectable bundle, one source of truth. A preset
// names a bundle; it never re-derives medium physics from scattered constants.
//
// THE RULE (enforced by ApplyTo, proven by test): a preset's medium
// OVERRIDES scene gravity, it never ADDS to it. ApplyTo discards the
// incoming settings' gravity entirely and replaces it with the bundle's net
// gravity. A scene gravity of -1000 plus the water bundle gives +63.9114,
// not -936.0886 and not -1000 + anything. If you ever see medium code doing
// `settings.Gravity += bundle.Gravity`, that is the bug this file exists to prevent.
//
// UNITS (same as ParticleSystem.cs): kernel inches, seconds, Z-up.
//   Gravity       — acceleration vector, inches/s^2, Z-up. Earth surface is
//                   (0, 0, -386.0886): 9.80665 m/s^2 expressed in inches/s^2.
//   DragLinear    — linear drag coefficient, 1/s. ParticleSystemSettings.Drag
//                   is exactly this: drag pulls velocity toward Wind at this
//                   rate, integrated semi-implicitly (stable for any value >= 0).
//   DragQuadratic — quadratic drag coefficient, 1/inch. Acceleration loss is
//                   DragQuadratic * |v| * v. There is NO field for this in
//                   ParticleSystemSettings (it has a single linear Drag), so
//                   ApplyTo does NOT fold it into Drag — that would corrupt
//                   the linear semantics. Consumers that model quadratic drag
//                   (the 2D sim, a future quadratic-drag 3D path) read the
//                   bundle directly.
//   Buoyancy      — upward acceleration, inches/s^2, +Z, for the bundle's
//                   REFERENCE particle (a bubble in water; zero in air, space
//                   and zero-G, where there is no buoyant lift worth modelling).
//                   It is folded into ApplyTo's gravity because
//                   ParticleSystemSettings has a single Gravity field: the
//                   settings gravity IS the medium's net acceleration.
//                   Sane range enforced by Validate: 0..2000 in/s^2 (0 = no
//                   lift, 2000 ~= 5.2 g of lift; water's 450 sits inside it).
//                   Per-particle density correction is NOT in this bundle:
//                   ParticleState carries no density, so a density-corrected
//                   lift needs a future lane that adds per-particle density.
//                   Until then, a heavy grain in water is modelled by the
//                   bundle's raw gravity + drags (see the dust test), and the
//                   bundle's Buoyancy stays the bubble-reference lift.
//   Density       — medium density, kg/m^3 (air 1.2, water 1000, near-vacuum
//                   ~0). Validated > 0, never used silently: no consumer in
//                   this tree divides by it yet, so it is carried for the
//                   future density-aware lane, not faked into a force today.
//
// FIELD SEMANTICS ARE DIMENSION-AGNOSTIC. The same bundle describes a 3D
// medium and a 2D medium; only the axis mapping changes:
//   2D (pixels, Y-down, the 2D sim's canvas): a 3D acceleration (gx, gy, gz)
//   in/s^2 becomes (gx * S, -gz * S) px/s^2, where S is pixels per inch.
//   The 3D Y axis has no flat-canvas counterpart and is dropped; the 3D Z
//   (up) becomes canvas -Y (up on screen), so Buoyancy's +Z lift becomes
//   a -Y lift. DragLinear is unchanged (1/s is scale-free); DragQuadratic
//   divides by S (1/inch -> 1/pixel); Density is unchanged (a medium
//   property, not a length). The conversion itself is another lane's job —
//   this note exists so that lane cannot silently flip a sign.
//   Out of scope here: honey/vacuum/helium bundles (a separate lane),
//   save/serialization (lane 5), canvas/UI, 2D conversion code.
//
// LIMITS, stated honestly:
//   - ApplyTo can only carry Gravity and DragLinear into
//     ParticleSystemSettings; DragQuadratic, Buoyancy-as-separate-term and
//     Density have no settings field and are NOT smuggled into other fields.
//   - The water bundle's ApplyTo result is the BUBBLE case (net +63.9114,
//     close to the existing bubbles preset's +60). A dust grain in water
//     sinks; it does not rise — its scenario uses the bundle's raw gravity
//     + drags, because per-particle density does not exist yet.
//   - Headless only. No UI, no script verb, no save format. The sweep owns
//     the TestRegistry line; SettingsFor hookup lines (if the coordinator
//     wants presets to resolve through bundles) are reported, not made.

/// <summary>One inspectable medium: gravity, drags, buoyancy, density.</summary>
public readonly struct MediumBundle
{
	/// <summary>Gravitational acceleration, in/s^2, Z-up (before buoyancy).</summary>
	public readonly Vector3 Gravity;
	/// <summary>Linear drag coefficient, 1/s. Maps to ParticleSystemSettings.Drag.</summary>
	public readonly float DragLinear;
	/// <summary>Quadratic drag coefficient, 1/inch. No ParticleSystemSettings field.</summary>
	public readonly float DragQuadratic;
	/// <summary>Upward (+Z) acceleration for the bundle's reference particle, in/s^2, 0..2000.</summary>
	public readonly float Buoyancy;
	/// <summary>Medium density, kg/m^3, must be &gt; 0.</summary>
	public readonly float Density;

	public MediumBundle( Vector3 gravity, float dragLinear, float dragQuadratic, float buoyancy, float density )
	{
		Gravity = gravity;
		DragLinear = dragLinear;
		DragQuadratic = dragQuadratic;
		Buoyancy = buoyancy;
		Density = density;
		Validate();
	}

	/// <summary>Loud failure on invalid bundles, never silent clamping.</summary>
	public void Validate()
	{
		if ( !float.IsFinite( Gravity.X ) || !float.IsFinite( Gravity.Y ) || !float.IsFinite( Gravity.Z ) )
			throw new ArgumentException( "MediumBundle gravity must be finite." );
		if ( !float.IsFinite( DragLinear ) || DragLinear < 0 )
			throw new ArgumentOutOfRangeException( nameof( DragLinear ), "DragLinear must be finite and >= 0." );
		if ( !float.IsFinite( DragQuadratic ) || DragQuadratic < 0 )
			throw new ArgumentOutOfRangeException( nameof( DragQuadratic ), "DragQuadratic must be finite and >= 0." );
		if ( !float.IsFinite( Buoyancy ) || Buoyancy < 0 || Buoyancy > 2000 )
			throw new ArgumentOutOfRangeException( nameof( Buoyancy ), "Buoyancy must be finite and in 0..2000 in/s^2." );
		if ( !float.IsFinite( Density ) || Density <= 0 )
			throw new ArgumentOutOfRangeException( nameof( Density ), "Density must be finite and > 0 kg/m^3." );
	}

	/// <summary>Net acceleration of the bundle's reference particle: gravity + buoyant lift.</summary>
	public Vector3 NetGravity() => Gravity + new Vector3( 0, 0, Buoyancy );

	/// <summary>Canonical bundle: still air at Earth gravity, light linear drag.</summary>
	public static MediumBundle Air => new( new Vector3( 0, 0, -386.0886f ), 0.05f, 0.0005f, 0f, 1.2f );

	/// <summary>Canonical bundle: Earth gravity, heavy drag, bubble-reference lift 450 in/s^2 (net +63.9114).</summary>
	public static MediumBundle Water => new( new Vector3( 0, 0, -386.0886f ), 2.0f, 0.05f, 450f, 1000f );

	/// <summary>Canonical bundle: near-vacuum. Near-zero gravity and drag, never exactly zero (it is a medium, not nothing).</summary>
	public static MediumBundle Space => new( new Vector3( 0, 0, -0.01f ), 0.001f, 0f, 0f, 1e-6f );

	/// <summary>Canonical bundle: exactly zero gravity, air-like drag and density (a cabin in free fall, not a vacuum).</summary>
	public static MediumBundle ZeroG => new( new Vector3( 0, 0, 0f ), 0.05f, 0.0005f, 0f, 1.2f );

	/// <summary>Pure function: a NEW settings object, never the input mutated.
	/// Gravity is REPLACED by the bundle's net gravity (bundle gravity + buoyancy);
	/// the incoming scene gravity plays no part — override, never add.
	/// Drag is replaced by DragLinear. Every other field is copied untouched.
	/// DragQuadratic and Density have no ParticleSystemSettings field and are
	/// deliberately not folded into any other field; read the bundle for them.</summary>
	public static ParticleSystemSettings ApplyTo( ParticleSystemSettings settings, MediumBundle bundle )
	{
		ArgumentNullException.ThrowIfNull( settings );
		bundle.Validate();
		return new ParticleSystemSettings
		{
			Capacity = settings.Capacity,
			Seed = settings.Seed,
			FixedDeltaTime = settings.FixedDeltaTime,
			Substeps = settings.Substeps,
			Gravity = bundle.NetGravity(),
			Wind = settings.Wind,
			Drag = bundle.DragLinear,
			TurbulenceStrength = settings.TurbulenceStrength,
			TurbulenceFrequency = settings.TurbulenceFrequency,
			TurbulenceSpeed = settings.TurbulenceSpeed,
			VortexStrength = settings.VortexStrength,
			VortexCenter = settings.VortexCenter,
			VortexAxis = settings.VortexAxis,
			VortexRadius = settings.VortexRadius,
			GroundEnabled = settings.GroundEnabled,
			GroundNormal = settings.GroundNormal,
			GroundOffset = settings.GroundOffset,
			CollisionMesh = settings.CollisionMesh,
			CollisionRadius = settings.CollisionRadius,
			Restitution = settings.Restitution,
			Friction = settings.Friction,
			RenderType = settings.RenderType,
			StrandPoints = settings.StrandPoints,
			KillVolumes = new System.Collections.Generic.List<ParticleKillVolume>( settings.KillVolumes ),
		};
	}
}
