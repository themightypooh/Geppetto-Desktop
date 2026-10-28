#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// P75 (particles session, 2026-10-24): the 2D medium standard.
//
// THE SAME BUNDLE SHAPE AS P38's 3D MediumBundle, ADAPTED TO 2D UNITS.
// P38 (ParticleMedia.cs) is the 3D standard; this file is its 2D twin, not a
// second invention. Field-for-field mapping:
//   P38 Gravity (Vector3, in/s^2, Z-up)  -> here Gravity (Vector2, px/s^2, Y-down)
//   P38 DragLinear (1/s)                -> here DragLinear (1/s, unchanged: scale-free)
//   P38 DragQuadratic (1/inch)          -> here DragQuadratic (1/px)
//   P38 Buoyancy (in/s^2, +Z lift)      -> here Buoyancy (dimensionless factor, see below)
//   P38 Density (kg/m^3)                -> here Density (relative, air = 1, see below)
// The two buoyancy/density changes are deliberate, not drift: the 2D task brief
// specifies a dimensionless buoyancy factor and a relative density, and the 2D
// sim has no per-particle density yet, so an absolute in/s^2 lift (P38's form)
// would be the wrong shape here. P38's conversion note (3D Z-up -> canvas -Y)
// still holds: a 3D upward lift is a NEGATIVE canvas-Y acceleration, which is
// why a rising bubble has a negative net Y below.
//
// UNITS (same as Particle2D.cs): canvas pixels, seconds, Y-DOWN. Downward is
// +Y, so gravity is +Y and anything rising has a NEGATIVE Y acceleration.
//   Gravity       — acceleration vector, px/s^2, Y-down. Every bundle uses
//                   (0, 980): the Particle2D default gravity (lane 1). Media
//                   differ in their NET gravity (via buoyancy) and drag, not
//                   in raw gravity — one Earth gravity, many media.
//   DragLinear    — linear drag coefficient, 1/s. Maps to
//                   Particle2DSettings.Drag exactly (drag pulls velocity
//                   toward Wind at this rate). Consumed, not defined, here:
//                   the drag math itself is P67's lane, not this file's.
//   DragQuadratic — quadratic drag coefficient, 1/px. Acceleration loss is
//                   DragQuadratic * |v| * v (P67's model to apply). There is
//                   NO field for this in Particle2DSettings, so Apply does
//                   NOT fold it into Drag — that would corrupt the linear
//                   semantics, exactly as P38 refused to do in 3D.
//   Buoyancy      — dimensionless factor in [0, 1]. EXACT MEANING, used
//                   consistently everywhere in this file:
//                     0 = no buoyant lift at all: NetAcceleration equals
//                         Gravity for every particle, whatever its density.
//                     1 = full Archimedes-style counter-gravity for a
//                         particle whose density equals the medium's:
//                         NetAcceleration is exactly zero for p = Density
//                         (it hovers; it neither rises nor sinks).
//                   General form (2D consumption formula — P66 owns the
//                   buoyancy derivation and may replace this consumption
//                   with a derived model; this lane does not re-derive it):
//                     NetAcceleration(bundle, p)
//                       = Gravity * (p - B*Density) / (p + 0.5*B*Density)
//                   where B is Buoyancy and p is the particle's density on
//                   the SAME relative scale as Density (air = 1). The
//                   numerator is weight minus B-scaled buoyant force; the
//                   denominator is effective inertia (particle plus a
//                   sphere added-mass term, coefficient 0.5, scaled by B so
//                   that B = 0 recovers pure gravity exactly). Consequences
//                   the tests pin: a light particle (p << Density) rises
//                   with net Y negative in Y-down space, bounded at -2x
//                   Gravity as p -> 0 (it never diverges); a heavy particle
//                   (p >> Density) sinks at just under full Gravity; a
//                   neutral particle (p = Density, B = 1) hovers at zero.
//   Density       — medium density, RELATIVE and dimensionless, with
//                   AIR = 1 as the reference. Air is 1 by definition. Water
//                   is 800: the real ratio is ~1000/1.2 = 833, rounded to 800
//                   for 2D readability (the rounding is stated here so no
//                   number looks more precise than it is). Thick smoke is
//                   1.2: smoke-laden air, slightly denser than clean air.
//                   Density is carried for the density-aware lane; it also
//                   enters NetAcceleration above, but it is NOT a force on
//                   its own and is never silently divided into gravity.
//
// THE ONE-SOURCE-OF-TRUTH RULE (same correction as P38, non-negotiable):
// a medium bundle's gravity is an absolute OVERRIDE, never an addition to
// scene gravity. Apply REPLACES the sim's gravity/drag values with the
// bundle's, never adds to them. A base gravity of (0, 9999) plus the water
// bundle gives exactly the water net gravity (0, 49), not (0, 10048) and not
// (0, 9999) plus anything. If you ever see medium code doing
// `settings.Gravity += bundle.Gravity`, that is the bug this file exists to
// prevent. Proven by test with a hostile base.
//
// APPLY SEMANTICS — Apply(bundle, settings) returns a NEW Particle2DSettings
// (the input is never mutated) with exactly these changes:
//   SETS:    Gravity = bundle.NetGravity() = Gravity * (1 - Buoyancy)
//            GravityAngleDegrees = null (a stale angle would silently re-aim
//              the bundle's absolute gravity; the bundle's direction stands)
//            Drag = bundle.DragLinear
//   LEAVES ALONE (copied untouched): Wind, TurbulenceStrength,
//            TurbulenceFrequency, TurbulenceSpeed, VortexStrength,
//            VortexCenter, VortexRadius, FloorEnabled, FloorY, WrapEnabled,
//            WrapMin, WrapMax, Restitution, Friction, Capacity, Seed,
//            FixedDeltaTime, Substeps. Emitter shapes are not settings fields
//            at all and are untouched by construction.
//   NOT CARRIED (no Particle2DSettings field exists): DragQuadratic,
//            Buoyancy, Density. They are deliberately NOT folded into Drag
//            or Gravity. The sweep lines that would add them are reported
//            with this lane, not made by it (this lane edits no existing file).
//
// BUNDLES. Air and Water are the required minimum; ThickSmoke is the one
// extra bundle (the brief allows at most two). FlowingWater was deliberately
// NOT added: this bundle shape has no current/wind field, and a flowing-water
// bundle without a current would be a palette swap of Water, not a distinct
// medium. A current belongs in a future lane that adds a Current field.
//   Air: light drag (0.05, the lane-1 sparks value and P38's linear drag),
//     full downward gravity (buoyancy 0, so net = raw gravity), density 1.
//   Water: strong drag (linear 2.5, quadratic 0.02), buoyancy 0.95, so the
//     bundle net is (0, 49): things sink slowly or hover rather than
//     plummet. A bubble (p = 1, air) in water RISES: net Y is negative
//     (about -1952 px/s^2). A stone (p = 2500) in water SINKS slowly: net
//     Y is positive but well under air gravity (about 592 px/s^2), and the
//     strong drag caps its terminal speed far below an air fall.
//   ThickSmoke: NOT a P54 import. P54's pack-D media (vacuum, helium, honey,
//     smoke-filled room) were not renamed into 2D. These numbers are derived
//     from 2D behaviour: the 2D dust/magic presets hang and drift with drag
//     0.5-1.5 and near-zero net gravity, so thick smoke sits between air and
//     water — heavy drag (1.8) but not water-heavy, net (0, 147) so the
//     reference mote settles slowly while light motes (p = 0.8) still rise.
//     That hang-and-drift, neither air-fall nor water-hover, is what makes
//     it genuinely distinct.
//
// NAME RESOLUTION mirrors lane 1's Particle2DPresets.ResolveName exactly:
// fx:/vfx:/particle: prefixes (plus the underscore forms fx_/vfx_/particle_),
// case-insensitive, whole underscore-separated words only ("underwater" does
// NOT resolve to "water"; "morning_water_splash" does). Unknown names return
// null from ResolveName and throw from BundleFor, in lane 1's style — no new
// convention is invented here.
//
// OUT OF SCOPE (same boundary as the brief): 3D media (P38/P54), drag models
// (P67 — coefficients are consumed, drag math is not defined here), buoyancy
// derivation (P66), force application into the sim step, any wiring into
// Particle2D.cs, any GPU code, any save/serialization, any canvas/UI. This
// file is data + pure accessors only: no game loop, play mode, live physics,
// input, or networking. Headless only; the coordinator sweep owns wiring.

/// <summary>One inspectable 2D medium: gravity, drags, buoyancy factor, relative density.</summary>
public readonly struct Particle2DMedium
{
	/// <summary>Gravitational acceleration, px/s^2, Y-down (before buoyancy).</summary>
	public readonly Vector2 Gravity;
	/// <summary>Linear drag coefficient, 1/s. Maps to Particle2DSettings.Drag.</summary>
	public readonly float DragLinear;
	/// <summary>Quadratic drag coefficient, 1/px. No Particle2DSettings field.</summary>
	public readonly float DragQuadratic;
	/// <summary>Buoyancy factor, 0..1. 0 = no lift; 1 = full counter-gravity at p = Density.</summary>
	public readonly float Buoyancy;
	/// <summary>Medium density, relative, dimensionless, air = 1. Must be &gt; 0.</summary>
	public readonly float Density;

	public Particle2DMedium( Vector2 gravity, float dragLinear, float dragQuadratic, float buoyancy, float density )
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
		if ( !Particle2DMath.Finite( Gravity ) )
			throw new ArgumentException( "Particle2DMedium gravity must be finite." );
		if ( !float.IsFinite( DragLinear ) || DragLinear < 0 )
			throw new ArgumentOutOfRangeException( nameof( DragLinear ), "DragLinear must be finite and >= 0." );
		if ( !float.IsFinite( DragQuadratic ) || DragQuadratic < 0 )
			throw new ArgumentOutOfRangeException( nameof( DragQuadratic ), "DragQuadratic must be finite and >= 0." );
		if ( !float.IsFinite( Buoyancy ) || Buoyancy < 0 || Buoyancy > 1 )
			throw new ArgumentOutOfRangeException( nameof( Buoyancy ), "Buoyancy must be finite and in 0..1." );
		if ( !float.IsFinite( Density ) || Density <= 0 )
			throw new ArgumentOutOfRangeException( nameof( Density ), "Density must be finite and > 0." );
	}

	/// <summary>Bundle-level net gravity: raw gravity with the buoyancy fraction removed. This is what Apply writes into settings.</summary>
	public Vector2 NetGravity() => Gravity * ( 1f - Buoyancy );

	/// <summary>Net acceleration of a particle of relative density <paramref name="particleDensity"/> (same scale as <see cref="Density"/>, air = 1) in this medium. Negative Y rises in Y-down space. See the file header for the exact formula and its limits.</summary>
	public Vector2 NetAcceleration( float particleDensity )
	{
		if ( !float.IsFinite( particleDensity ) || particleDensity <= 0 )
			throw new ArgumentOutOfRangeException( nameof( particleDensity ), "Particle density must be finite and > 0." );
		float b = Buoyancy * Density;
		return Gravity * ( ( particleDensity - b ) / ( particleDensity + 0.5f * b ) );
	}

	/// <summary>Canonical bundle: still air at 2D gravity, light linear drag, no lift.</summary>
	public static Particle2DMedium Air => new( new Vector2( 0, 980f ), 0.05f, 0.0005f, 0f, 1f );

	/// <summary>Canonical bundle: 2D gravity, strong drag, buoyancy 0.95 — net (0, 49): sink slowly or hover.</summary>
	public static Particle2DMedium Water => new( new Vector2( 0, 980f ), 2.5f, 0.02f, 0.95f, 800f );

	/// <summary>2D-derived bundle (not a P54 import): smoke-laden air that hangs and drifts — heavy drag, slow settle, light motes rise.</summary>
	public static Particle2DMedium ThickSmoke => new( new Vector2( 0, 980f ), 1.8f, 0.01f, 0.85f, 1.2f );

	public static readonly string[] Names = { "air", "water", "thick_smoke" };

	/// <summary>Lane-1 preset resolution, mirrored: prefixes, case-insensitive, whole underscore words only. Unknown names return null.</summary>
	public static string? ResolveName( string? name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		var n = name.Trim().ToLowerInvariant();
		foreach ( var p in new[] { "fx:", "fx_", "vfx:", "vfx_", "particle:", "particle_" } )
			if ( n.StartsWith( p ) ) { n = n[p.Length..]; break; }
		var flat = n.Replace( '-', '_' ).Replace( ':', '_' ).Replace( ' ', '_' ).Replace( '/', '_' );
		foreach ( var medium in Names )
			if ( flat == medium || flat.StartsWith( medium + "_" ) || flat.EndsWith( "_" + medium ) || flat.Contains( "_" + medium + "_" ) )
				return medium;
		return null;
	}

	/// <summary>Bundle for a name, resolved like lane 1. Unknown names throw, in lane 1's style.</summary>
	public static Particle2DMedium BundleFor( string name )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D medium '{name}'. Media: {string.Join( ", ", Names )}." );
		return key switch
		{
			"air" => Air,
			"water" => Water,
			"thick_smoke" => ThickSmoke,
			_ => throw new ArgumentException( $"Unknown 2D medium '{name}'. Media: {string.Join( ", ", Names )}." ),
		};
	}

	/// <summary>Pure function: a NEW settings object, never the input mutated.
	/// Gravity is REPLACED by the bundle's net gravity; the incoming scene
	/// gravity plays no part — override, never add. Drag is replaced by
	/// DragLinear. GravityAngleDegrees is cleared so a stale angle cannot
	/// re-aim the bundle's absolute gravity. Every other field is copied
	/// untouched. DragQuadratic, Buoyancy and Density have no
	/// Particle2DSettings field and are deliberately not folded into any
	/// other field; read the bundle for them.</summary>
	public static Particle2DSettings Apply( Particle2DMedium bundle, Particle2DSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		bundle.Validate();
		return new Particle2DSettings
		{
			Capacity = settings.Capacity,
			Seed = settings.Seed,
			FixedDeltaTime = settings.FixedDeltaTime,
			Substeps = settings.Substeps,
			Gravity = bundle.NetGravity(),
			GravityAngleDegrees = null,
			Wind = settings.Wind,
			Drag = bundle.DragLinear,
			TurbulenceStrength = settings.TurbulenceStrength,
			TurbulenceFrequency = settings.TurbulenceFrequency,
			TurbulenceSpeed = settings.TurbulenceSpeed,
			VortexStrength = settings.VortexStrength,
			VortexCenter = settings.VortexCenter,
			VortexRadius = settings.VortexRadius,
			FloorEnabled = settings.FloorEnabled,
			FloorY = settings.FloorY,
			WrapEnabled = settings.WrapEnabled,
			WrapMin = settings.WrapMin,
			WrapMax = settings.WrapMax,
			Restitution = settings.Restitution,
			Friction = settings.Friction,
		};
	}
}
