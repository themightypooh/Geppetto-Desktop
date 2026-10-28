// ParticleSharedDefinition.cs — wave-6 lane P79 (shared definition fields).
//
// A mapping table + pure conversions between the two particle sims' settings.
// It does NOT change either sim, wire into either sim, save anything, or draw
// anything. It stands alone until the coordinator sweep wires its tests in;
// no hookup into either sim is needed for this file to be useful.
//
// WHAT THE TWO SIMS ACTUALLY CARRY (read from code, not assumed):
//   3D: ParticleSystemSettings + ParticleEmitter in ParticleSystem.cs.
//       Kernel inches, Z-up, seconds. Gravity default (0, 0, -386.0886) in/s^2.
//   2D: Particle2DSettings + Particle2DEmitter in Particle2D.cs.
//       Canvas pixels, Y-down, seconds. Gravity default (0, 980) px/s^2.
// Lifetime, size, rate and bursts live on the EMITTER in both sims, not on
// the settings struct. The brief's settings-only signatures therefore map the
// system fields; the emitter overloads + emitter companions map the rest.
// Together they give the full round-trip; neither half alone claims to.
//
// CANONICAL UNIT OF THE SHARED RECORD: inches + seconds (the 3D sim's units).
// That is why ToSharedFrom3D takes no factor and FromSharedTo3D takes none.
//
// UNIT RULE, STATED HONESTLY: 2D canvas pixels have NO fixed inch scale, so
// there is NO universal px-to-inch constant here and none is claimed. Every
// 2D conversion takes an explicit caller-supplied pixelsPerInch (> 0,
// validated). A conversion with factor 1 is identity on magnitudes. A
// conversion with factor F scales as follows (2D -> shared divides, shared
// -> 2D multiplies, by the same rule):
//   Lengths            (size, emitter position, vortex radius, ground offset,
//                       floor position): scaled by F.
//   Length-rates       (wind, base velocity, launch speed): scaled by F.
//   Length-accelerations (gravity, turbulence strength): scaled by F.
//   Length^2 / time^2  (vortex strength): scaled by F^2.
//   Inverse lengths    (turbulence frequency, 1/length): scaled by 1/F
//                       (frequency MULTIPLIES by F going 2D -> shared).
//   NEVER scaled: times (fixed step, lifetimes), counts (capacity, burst
//     steps/counts), seeds, substeps, and dimensionless factors (drag 1/s,
//     restitution, friction, speed jitter, spread degrees, angular speed
//     rad/s, turbulence speed in noise units/s, emission rate 1/s).
// Never invent a default factor and never silently pick one.
//
// AXIS RULE (the lane's failure mode, so it is explicit): the shared plane
// is the X-vertical plane. Shared vectors are Vector2 (X, Down), where Down
// is positive DOWNWARD.
//   3D -> shared: (x, y, z) -> (x, -z). Down is -Z in 3D (Z-up).
//   2D -> shared: (x, y) -> (x, y). Down is +Y in 2D (Y-down canvas).
//   Shared -> 3D: (x, down) -> (x, 0, -down). The reconstructed depth (Y) is
//     0 by construction, never a recovered value.
//   Shared -> 2D: (x, down) -> (x, down) (times F for length quantities).
// Hence 3D gravity (0, 0, -386) maps to 2D gravity (0, +386) at factor 1:
// down is -Z in 3D and +Y in 2D. A 3D vector's Y (depth) component has NO
// 2D counterpart in this side-view projection; when it is non-zero it is
// reported in DroppedFields, never silently discarded.
//
// CORRECTIONS TO THE BRIEF'S EXPECTED OVERLAP LIST (code disagreed here):
//   * "Drag coefficients" (plural): both sims carry exactly ONE linear Drag
//     (1/s). There is no quadratic drag coefficient in either sim, so the
//     shared record carries one Drag, not two.
//   * Gravity/wind/direction/velocity/position are 3D vectors; only their X
//     and vertical components map. The Y (depth) component is dropped+flagged
//     when non-zero (see axis rule above).
//   * Turbulence frequency is an inverse length, so it scales inversely to
//     lengths (see unit rule). Vortex strength is length^2/time^2, so it
//     scales by F^2, not F.
//   * Capacity caps differ: 3D allows 1..4,000,000, 2D allows 1..1,000,000.
//     The shared record allows 1..4,000,000 and does NOT clamp: a shared
//     capacity above 1M converts to 2D settings that fail 2D Validate().
//     That failure is the caller's signal, not something to hide.
//   * 2D GravityAngleDegrees: when set, the 2D sim ignores the Gravity
//     vector's direction and uses its magnitude along the angle. Conversion
//     therefore converts the EFFECTIVE gravity (magnitude along the angle)
//     and reports the angle itself in DroppedFields.
//   * Vortex is carried as scalar knobs (strength, radius) plus a mapped
//     centre position. The 3D vortex axis has no 2D counterpart and is
//     reported in DroppedFields when it differs from the (0, 0, 1) default
//     (the default is also the reconstruction default). The turbulence and
//     vortex FIELDS themselves are not reproduced exactly across sims —
//     the two noise implementations differ — only their parameters map.
//   * Ground/floor: only a vertical 3D ground plane maps exactly. A 3D
//     ground normal other than (0, 0, 1) is reported in DroppedFields
//     (orientation lost; enabled flag + offset still carried). 2D FloorY
//     maps to 3D ground offset with a sign flip: FloorY = -offset * F,
//     because canvas +Y is down while 3D +Z is up.
//   * TurbulenceSpeed is a scalar in noise units/s in both sims and is
//     carried as-is; its noise-space scroll axis differs (3D +Z, 2D +X)
//     but that is a noise-space axis, not a world axis, so no mapping.
//   * Emitter shape geometry (area edges, box extents, radii, line ends,
//     masks) is sim-specific and is NOT in the shared record. Reconstructed
//     emitters from FromSharedTo*Emitter are Point emitters; the caller
//     re-applies shape. Shape loss is reported in DroppedFields for
//     non-Point source emitters.
//
// DROPPEDFIELDS RULE: SharedParticleFields.DroppedFields lists what the
// inbound conversion could NOT represent. Ungated sim-only fields (colour,
// frames, size/fade over life, sort mode, collision radius) are always
// listed, because the shared record has no slot for them at any value.
// Gated fields (wrap, mask, sub-emitters, mesh, kill volumes, non-default
// vortex axis, non-vertical ground normal) are listed only when active.
// A vector depth component is listed only when non-zero (zero loses
// nothing). Mapped fields are never listed. The outbound leg (shared ->
// either sim) loses nothing by construction: every shared field maps to
// both sims, so reconstructed settings/emitters carry no dropped list.
//
// VALIDATION: conversions call the source settings/emitter Validate() (loud
// failure on a bad source) and FromShared* calls SharedParticleFields.
// Validate(). FromShared* does NOT call the target sim's Validate() and
// does NOT clamp: a shared value outside a target sim's range (e.g.
// capacity above the 2D cap) survives conversion and fails that sim's own
// Validate(), which is the honest signal. Null settings/emitters/shared,
// non-finite inputs, and non-positive pixelsPerInch are rejected loudly.
// Round-trip tolerance used by the tests: 1e-4 absolute on float fields;
// counts, steps, seeds, and booleans are exact.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>One burst: <see cref="Count"/> particles at fixed step <see cref="Step"/>.</summary>
public readonly record struct SharedBurst( int Step, int Count );

/// <summary>
/// The overlapping subset both particle sims genuinely share, in canonical
/// units (inches, seconds; vectors as (X, Down), Down positive downward).
/// See the file header for the unit rule, the axis rule, and the list of
/// corrections to the brief's expected overlap. Emitter fields come from a
/// paired emitter; the settings-only conversions fill them with neutral
/// defaults (rate 0, no bursts, lifetime 1..1, size 1..1, speed 0).
/// </summary>
public sealed record SharedParticleFields
{
	// ---- System fields (from the settings structs) ----
	/// <summary>Deterministic seed. Identical in both sims, never scaled.</summary>
	public uint Seed { get; init; } = 1;
	/// <summary>Particle capacity. Caps differ by sim (3D 4M, 2D 1M); not clamped.</summary>
	public int Capacity { get; init; } = 10_000;
	/// <summary>Fixed step, seconds. Never scaled.</summary>
	public float FixedDeltaTime { get; init; } = 1f / 60f;
	/// <summary>Substeps per fixed step. Never scaled.</summary>
	public int Substeps { get; init; } = 1;
	/// <summary>Gravity, (X, Down) in/s^2. 3D (x, y, z) -> (x, -z); 2D (x, y) -> (x, y).</summary>
	public Vector2 Gravity { get; init; }
	/// <summary>Wind (air velocity), (X, Down) in/s. Acts through drag in both sims.</summary>
	public Vector2 Wind { get; init; }
	/// <summary>Single linear drag coefficient, 1/s. Never scaled. (Not plural: neither sim has a second coefficient.)</summary>
	public float Drag { get; init; }
	/// <summary>Turbulence acceleration magnitude, in/s^2. Scaled by F from 2D.</summary>
	public float TurbulenceStrength { get; init; }
	/// <summary>Noise lattice frequency, 1/in. Scaled by 1/F from 2D (multiplies by F going 2D -> shared).</summary>
	public float TurbulenceFrequency { get; init; } = 0.1f;
	/// <summary>Noise scroll speed, noise units/s. Never scaled.</summary>
	public float TurbulenceSpeed { get; init; }
	/// <summary>Vortex strength knob, in^2/s^2. Scaled by F^2 from 2D.</summary>
	public float VortexStrength { get; init; }
	/// <summary>Vortex centre, (X, Down) in. 3D depth component dropped+flagged when non-zero.</summary>
	public Vector2 VortexCenter { get; init; }
	/// <summary>Vortex radius, in. Scaled by F from 2D.</summary>
	public float VortexRadius { get; init; } = 1f;
	/// <summary>Ground (3D) / floor line (2D) enabled.</summary>
	public bool GroundEnabled { get; init; }
	/// <summary>Ground plane height, in. 2D FloorY = -GroundOffset * F.</summary>
	public float GroundOffset { get; init; }
	/// <summary>Normal velocity retained on contact, 0..1. Never scaled.</summary>
	public float Restitution { get; init; } = 0.3f;
	/// <summary>Tangential velocity retained on contact is (1 - Friction), 0..1. Never scaled.</summary>
	public float Friction { get; init; } = 0.2f;

	// ---- Emitter fields (from a paired emitter) ----
	/// <summary>Continuous emission rate, particles/s. Never scaled.</summary>
	public float Rate { get; init; }
	/// <summary>Step bursts. Steps and counts never scaled.</summary>
	public IReadOnlyList<SharedBurst> Bursts { get; init; } = Array.Empty<SharedBurst>();
	/// <summary>Particle lifetime range, seconds. Never scaled.</summary>
	public float LifetimeMin { get; init; } = 1f;
	/// <summary>Particle lifetime range, seconds. Never scaled.</summary>
	public float LifetimeMax { get; init; } = 1f;
	/// <summary>Particle size range, in. Scaled by F from 2D.</summary>
	public float SizeMin { get; init; } = 1f;
	/// <summary>Particle size range, in. Scaled by F from 2D.</summary>
	public float SizeMax { get; init; } = 1f;
	/// <summary>Launch speed, in/s. Scaled by F from 2D.</summary>
	public float Speed { get; init; }
	/// <summary>Speed jitter, 0..1 (speed uniform in Speed*(1 +/- jitter)). Never scaled.</summary>
	public float SpeedJitter { get; init; }
	/// <summary>Cone half-angle around the launch direction, degrees 0..180. Never scaled.</summary>
	public float SpreadDegrees { get; init; }
	/// <summary>Launch direction, (X, Down) components as authored (not normalized here; each sim normalizes at spawn).</summary>
	public Vector2 Direction { get; init; }
	/// <summary>Base velocity added to every launch velocity, (X, Down) in/s. Scaled by F from 2D.</summary>
	public Vector2 BaseVelocity { get; init; }
	/// <summary>Max angular speed, rad/s. Never scaled.</summary>
	public float AngularSpeedMax { get; init; }
	/// <summary>Emitter enabled flag.</summary>
	public bool Enabled { get; init; } = true;
	/// <summary>Emitter anchor, (X, Down) in. Point position / box centre / circle centre / line start, per shape.</summary>
	public Vector2 EmitterPosition { get; init; }

	/// <summary>
	/// What the inbound conversion could not represent, as "Field path (reason)"
	/// strings. Empty for a hand-built record. See the file header for the rule.
	/// </summary>
	public IReadOnlyList<string> DroppedFields { get; init; } = Array.Empty<string>();

	public void Validate()
	{
		if ( Bursts is null ) throw new ArgumentNullException( nameof( Bursts ) );
		if ( DroppedFields is null ) throw new ArgumentNullException( nameof( DroppedFields ) );
		if ( Capacity <= 0 || Capacity > 4_000_000 ) throw new ArgumentOutOfRangeException( nameof( Capacity ), "Capacity must be 1..4,000,000." );
		if ( !float.IsFinite( FixedDeltaTime ) || FixedDeltaTime <= 0 || FixedDeltaTime > 1 ) throw new ArgumentOutOfRangeException( nameof( FixedDeltaTime ), "Fixed step must be in (0, 1] s." );
		if ( Substeps < 1 || Substeps > 64 ) throw new ArgumentOutOfRangeException( nameof( Substeps ), "Substeps must be 1..64." );
		if ( !Finite( Gravity ) || !Finite( Wind ) || !Finite( VortexCenter ) || !Finite( Direction ) || !Finite( BaseVelocity ) || !Finite( EmitterPosition ) )
			throw new ArgumentException( "Shared vectors must be finite." );
		if ( !float.IsFinite( Drag ) || Drag < 0 ) throw new ArgumentOutOfRangeException( nameof( Drag ) );
		if ( !float.IsFinite( TurbulenceStrength ) || TurbulenceStrength < 0 || !float.IsFinite( TurbulenceFrequency ) || TurbulenceFrequency <= 0 || !float.IsFinite( TurbulenceSpeed ) )
			throw new ArgumentException( "Turbulence needs strength >= 0, frequency > 0, finite speed." );
		if ( !float.IsFinite( VortexStrength ) || !float.IsFinite( VortexRadius ) || VortexRadius <= 0 ) throw new ArgumentException( "Vortex needs finite strength and radius > 0." );
		if ( !float.IsFinite( GroundOffset ) ) throw new ArgumentException( "Ground offset must be finite." );
		if ( !float.IsFinite( Restitution ) || Restitution < 0 || Restitution > 1 ) throw new ArgumentOutOfRangeException( nameof( Restitution ), "Restitution must be 0..1." );
		if ( !float.IsFinite( Friction ) || Friction < 0 || Friction > 1 ) throw new ArgumentOutOfRangeException( nameof( Friction ), "Friction must be 0..1." );
		if ( !float.IsFinite( Rate ) || Rate < 0 ) throw new ArgumentOutOfRangeException( nameof( Rate ), "Rate must be finite and >= 0." );
		if ( !float.IsFinite( LifetimeMin ) || !float.IsFinite( LifetimeMax ) || LifetimeMin <= 0 || LifetimeMax < LifetimeMin )
			throw new ArgumentException( "Lifetime must satisfy 0 < LifetimeMin <= LifetimeMax." );
		if ( !float.IsFinite( SizeMin ) || !float.IsFinite( SizeMax ) || SizeMin <= 0 || SizeMax < SizeMin )
			throw new ArgumentException( "Size must satisfy 0 < SizeMin <= SizeMax." );
		if ( !float.IsFinite( Speed ) || Speed < 0 || !float.IsFinite( SpeedJitter ) || SpeedJitter < 0 || SpeedJitter > 1 )
			throw new ArgumentException( "Speed >= 0 and SpeedJitter in 0..1 required." );
		if ( !float.IsFinite( SpreadDegrees ) || SpreadDegrees < 0 || SpreadDegrees > 180 ) throw new ArgumentOutOfRangeException( nameof( SpreadDegrees ), "SpreadDegrees must be 0..180." );
		if ( !float.IsFinite( AngularSpeedMax ) || AngularSpeedMax < 0 ) throw new ArgumentOutOfRangeException( nameof( AngularSpeedMax ) );
		foreach ( var b in Bursts )
			if ( b.Step < 0 || b.Count < 0 ) throw new ArgumentException( "Bursts need Step >= 0 and Count >= 0." );
	}

	static bool Finite( Vector2 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y );
}

/// <summary>Pure conversions between the shared record and each sim. No state, no I/O, no wiring.</summary>
public static class ParticleSharedDefinition
{
	// ---- 2D -> shared ----

	/// <summary>System fields only; emitter fields take neutral defaults. Use the emitter overload for a full mapping.</summary>
	public static SharedParticleFields ToSharedFrom2D( Particle2DSettings settings, float pixelsPerInch )
	{
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		RequirePixelsPerInch( pixelsPerInch );
		settings.Validate();
		var dropped = new List<string>();
		var shared = From2DSettings( settings, pixelsPerInch, dropped );
		return shared with { DroppedFields = dropped };
	}

	/// <summary>Full mapping: system fields + emitter fields.</summary>
	public static SharedParticleFields ToSharedFrom2D( Particle2DSettings settings, Particle2DEmitter emitter, float pixelsPerInch )
	{
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		if ( emitter is null ) throw new ArgumentNullException( nameof( emitter ) );
		RequirePixelsPerInch( pixelsPerInch );
		settings.Validate();
		emitter.Validate( 0 );
		var dropped = new List<string>();
		var shared = From2DSettings( settings, pixelsPerInch, dropped );
		shared = From2DEmitter( shared, emitter, pixelsPerInch, dropped );
		return shared with { DroppedFields = dropped };
	}

	static SharedParticleFields From2DSettings( Particle2DSettings s, float f, List<string> dropped )
	{
		// Effective gravity: when GravityAngleDegrees is set, the 2D sim uses
		// the Gravity vector's magnitude along that angle (degrees, Y-down:
		// 0 = +X, 90 = +Y down). The angle itself is dropped+flagged.
		Vector2 gravity2D = s.Gravity;
		if ( s.GravityAngleDegrees is { } angle )
		{
			float mag = s.Gravity.Length();
			float rad = angle * ( MathF.PI / 180f );
			gravity2D = new Vector2( mag * MathF.Cos( rad ), mag * MathF.Sin( rad ) );
			dropped.Add( "Particle2DSettings.GravityAngleDegrees (2D-only: effective gravity converted, angle itself not stored)" );
		}
		if ( s.WrapEnabled )
			dropped.Add( "Particle2DSettings.WrapEnabled/WrapMin/WrapMax (2D-only: no 3D wrap equivalent)" );
		return new SharedParticleFields
		{
			Seed = s.Seed,
			Capacity = s.Capacity,
			FixedDeltaTime = s.FixedDeltaTime,
			Substeps = s.Substeps,
			Gravity = Div( gravity2D, f ),
			Wind = Div( s.Wind, f ),
			Drag = s.Drag,
			TurbulenceStrength = s.TurbulenceStrength / f,
			TurbulenceFrequency = s.TurbulenceFrequency * f,
			TurbulenceSpeed = s.TurbulenceSpeed,
			VortexStrength = s.VortexStrength / ( f * f ),
			VortexCenter = Div( s.VortexCenter, f ),
			VortexRadius = s.VortexRadius / f,
			GroundEnabled = s.FloorEnabled,
			GroundOffset = -s.FloorY / f,
			Restitution = s.Restitution,
			Friction = s.Friction,
		};
	}

	static SharedParticleFields From2DEmitter( SharedParticleFields shared, Particle2DEmitter e, float f, List<string> dropped )
	{
		var bursts = new SharedBurst[e.Bursts.Count];
		for ( int i = 0; i < bursts.Length; i++ ) bursts[i] = new SharedBurst( e.Bursts[i].Step, e.Bursts[i].Count );
		// Ungated 2D-only look/behaviour fields: always listed (no shared slot at any value).
		dropped.Add( "Particle2DEmitter.ColorStart (2D-only: no per-particle colour in shared record)" );
		dropped.Add( "Particle2DEmitter.ColorEnd (2D-only: no per-particle colour in shared record)" );
		dropped.Add( "Particle2DEmitter.ColorJitter (2D-only: no per-particle colour in shared record)" );
		if ( e.Palette is not null )
		{
			dropped.Add( "Particle2DEmitter.Palette (2D-only: no palette in shared record)" );
			dropped.Add( "Particle2DEmitter.EndAlpha (2D-only: palette-mode alpha in shared record)" );
		}
		dropped.Add( "Particle2DEmitter.FrameMin (2D-only: no sprite frames in shared record)" );
		dropped.Add( "Particle2DEmitter.FrameMax (2D-only: no sprite frames in shared record)" );
		dropped.Add( "Particle2DEmitter.FrameAdvance (2D-only: no sprite frames in shared record)" );
		dropped.Add( "Particle2DEmitter.SizeEndScale (2D-only: no size-over-life in shared record)" );
		dropped.Add( "Particle2DEmitter.SizeIncrX (2D-only: no per-axis size growth in shared record)" );
		dropped.Add( "Particle2DEmitter.SizeIncrY (2D-only: no per-axis size growth in shared record)" );
		dropped.Add( "Particle2DEmitter.SpeedIncr (2D-only: no speed-over-time in shared record)" );
		dropped.Add( "Particle2DEmitter.FadeIn (2D-only: no fade-in in shared record)" );
		dropped.Add( "Particle2DEmitter.FadePower (2D-only: no fade curve in shared record)" );
		dropped.Add( "Particle2DEmitter.RotAuto (2D-only: no auto-rotation in shared record)" );
		dropped.Add( "Particle2DEmitter.EmitDirectionAsAngle (2D-only: no launch-angle rotation in shared record)" );
		dropped.Add( "Particle2DEmitter.SortMode (2D-only: no draw-order mode in shared record)" );
		// Shape geometry: only Point loses nothing (a point needs no extents).
		switch ( e.Shape )
		{
			case Particle2DEmitterShape.Line:
				dropped.Add( "Particle2DEmitter.Shape=Line (2D-only shape: spawn-region geometry not in shared record)" );
				dropped.Add( "Particle2DEmitter.LineEnd (2D-only: line geometry not in shared record)" );
				break;
			case Particle2DEmitterShape.Box:
				dropped.Add( "Particle2DEmitter.Shape=Box (spawn-region geometry not in shared record)" );
				dropped.Add( "Particle2DEmitter.BoxHalfExtents (2D-only: box geometry not in shared record)" );
				break;
			case Particle2DEmitterShape.Circle:
				dropped.Add( "Particle2DEmitter.Shape=Circle (2D-only shape: spawn-region geometry not in shared record)" );
				dropped.Add( "Particle2DEmitter.CircleRadius (2D-only: circle geometry not in shared record)" );
				break;
			case Particle2DEmitterShape.Direction:
				dropped.Add( "Particle2DEmitter.Shape=Direction (2D-only shape: spawn-region geometry not in shared record)" );
				dropped.Add( "Particle2DEmitter.EmitDist (2D-only: direction-mode spread not in shared record)" );
				dropped.Add( "Particle2DEmitter.EmitAngleDegrees (2D-only: direction-mode angle not in shared record)" );
				break;
		}
		if ( e.Mask is not null )
		{
			dropped.Add( "Particle2DEmitter.Mask (2D-only: emission mask not in shared record)" );
			dropped.Add( "Particle2DEmitter.MaskMode (2D-only: emission mask not in shared record)" );
			dropped.Add( "Particle2DEmitter.MaskCellSize (2D-only: emission mask not in shared record)" );
			dropped.Add( "Particle2DEmitter.CaptureColorFromMask (2D-only: emission mask not in shared record)" );
		}
		return shared with
		{
			Rate = e.Rate,
			Bursts = bursts,
			LifetimeMin = e.LifetimeMin,
			LifetimeMax = e.LifetimeMax,
			SizeMin = e.SizeMin / f,
			SizeMax = e.SizeMax / f,
			Speed = e.Speed / f,
			SpeedJitter = e.SpeedJitter,
			SpreadDegrees = e.SpreadDegrees,
			Direction = e.Direction,
			BaseVelocity = Div( e.BaseVelocity, f ),
			AngularSpeedMax = e.AngularSpeedMax,
			Enabled = e.Enabled,
			EmitterPosition = Div( e.Position, f ),
		};
	}

	// ---- 3D -> shared ----

	/// <summary>System fields only; emitter fields take neutral defaults. Use the emitter overload for a full mapping.</summary>
	public static SharedParticleFields ToSharedFrom3D( ParticleSystemSettings settings )
	{
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		settings.Validate();
		var dropped = new List<string>();
		var shared = From3DSettings( settings, dropped );
		return shared with { DroppedFields = dropped };
	}

	/// <summary>Full mapping: system fields + emitter fields.</summary>
	public static SharedParticleFields ToSharedFrom3D( ParticleSystemSettings settings, ParticleEmitter emitter )
	{
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		if ( emitter is null ) throw new ArgumentNullException( nameof( emitter ) );
		settings.Validate();
		emitter.Validate( 0 );
		var dropped = new List<string>();
		var shared = From3DSettings( settings, dropped );
		shared = From3DEmitter( shared, emitter, dropped );
		return shared with { DroppedFields = dropped };
	}

	static SharedParticleFields From3DSettings( ParticleSystemSettings s, List<string> dropped )
	{
		// CollisionRadius is 3D-only (2D has no particle radius): always listed.
		dropped.Add( "ParticleSystemSettings.CollisionRadius (3D-only: no particle radius in shared record)" );
		if ( s.CollisionMesh is not null )
			dropped.Add( "ParticleSystemSettings.CollisionMesh (3D-only: no mesh collision in shared record)" );
		if ( s.KillVolumes.Count > 0 )
			dropped.Add( "ParticleSystemSettings.KillVolumes (3D-only: no kill volumes in shared record)" );
		if ( s.RenderType != ParticleRenderType.Billboard )
			dropped.Add( "ParticleSystemSettings.RenderType (3D-only: no render type in shared record)" );
		if ( s.StrandPoints > 0 )
			dropped.Add( "ParticleSystemSettings.StrandPoints (3D-only: no strand history in shared record)" );
		if ( s.GroundEnabled && ( s.GroundNormal - new Vector3( 0, 0, 1 ) ).LengthSquared() > 1e-12f )
			dropped.Add( "ParticleSystemSettings.GroundNormal (3D-only orientation: only a vertical (0,0,1) ground plane maps to the 2D floor line)" );
		if ( ( s.VortexAxis - new Vector3( 0, 0, 1 ) ).LengthSquared() > 1e-12f )
			dropped.Add( "ParticleSystemSettings.VortexAxis (3D-only: no vortex axis in shared record)" );
		return new SharedParticleFields
		{
			Seed = s.Seed,
			Capacity = s.Capacity,
			FixedDeltaTime = s.FixedDeltaTime,
			Substeps = s.Substeps,
			Gravity = From3DVector( s.Gravity, "ParticleSystemSettings.Gravity.Y (depth)", dropped ),
			Wind = From3DVector( s.Wind, "ParticleSystemSettings.Wind.Y (depth)", dropped ),
			Drag = s.Drag,
			TurbulenceStrength = s.TurbulenceStrength,
			TurbulenceFrequency = s.TurbulenceFrequency,
			TurbulenceSpeed = s.TurbulenceSpeed,
			VortexStrength = s.VortexStrength,
			VortexCenter = From3DVector( s.VortexCenter, "ParticleSystemSettings.VortexCenter.Y (depth)", dropped ),
			VortexRadius = s.VortexRadius,
			GroundEnabled = s.GroundEnabled,
			GroundOffset = s.GroundOffset,
			Restitution = s.Restitution,
			Friction = s.Friction,
		};
	}

	static SharedParticleFields From3DEmitter( SharedParticleFields shared, ParticleEmitter e, List<string> dropped )
	{
		var bursts = new SharedBurst[e.Bursts.Count];
		for ( int i = 0; i < bursts.Length; i++ ) bursts[i] = new SharedBurst( e.Bursts[i].Step, e.Bursts[i].Count );
		switch ( e.Shape )
		{
			case ParticleEmitterShape.Area:
				dropped.Add( "ParticleEmitter.Shape=Area (spawn-region geometry not in shared record)" );
				dropped.Add( "ParticleEmitter.AreaU (3D-only: area geometry not in shared record)" );
				dropped.Add( "ParticleEmitter.AreaV (3D-only: area geometry not in shared record)" );
				break;
			case ParticleEmitterShape.Box:
				dropped.Add( "ParticleEmitter.Shape=Box (spawn-region geometry not in shared record)" );
				dropped.Add( "ParticleEmitter.BoxHalfExtents (3D-only: box geometry not in shared record)" );
				break;
			case ParticleEmitterShape.Sphere:
				dropped.Add( "ParticleEmitter.Shape=Sphere (spawn-region geometry not in shared record)" );
				dropped.Add( "ParticleEmitter.SphereRadius (3D-only: sphere geometry not in shared record)" );
				break;
			case ParticleEmitterShape.MeshSurface:
				dropped.Add( "ParticleEmitter.Shape=MeshSurface (spawn-region geometry not in shared record)" );
				dropped.Add( "ParticleEmitter.Mesh (3D-only: mesh surface not in shared record)" );
				break;
		}
		if ( e.Shape is ParticleEmitterShape.Area or ParticleEmitterShape.MeshSurface )
			dropped.Add( "ParticleEmitter.UseSurfaceNormal (3D-only: no surface normal in shared record)" );
		if ( e.DeathSubEmitter is not null )
			dropped.Add( "ParticleEmitter.DeathSubEmitter (3D-only: no sub-emitters in shared record)" );
		if ( e.CollisionSubEmitter is not null )
			dropped.Add( "ParticleEmitter.CollisionSubEmitter (3D-only: no sub-emitters in shared record)" );
		if ( e.DieOnCollision )
			dropped.Add( "ParticleEmitter.DieOnCollision (3D-only: no hide-on-contact in shared record)" );
		return shared with
		{
			Rate = e.Rate,
			Bursts = bursts,
			LifetimeMin = e.LifetimeMin,
			LifetimeMax = e.LifetimeMax,
			SizeMin = e.SizeMin,
			SizeMax = e.SizeMax,
			Speed = e.Speed,
			SpeedJitter = e.SpeedJitter,
			SpreadDegrees = e.SpreadDegrees,
			Direction = From3DVector( e.Direction, "ParticleEmitter.Direction.Y (depth)", dropped ),
			BaseVelocity = From3DVector( e.BaseVelocity, "ParticleEmitter.BaseVelocity.Y (depth)", dropped ),
			AngularSpeedMax = e.AngularSpeedMax,
			Enabled = e.Enabled,
			EmitterPosition = From3DVector( e.Position, "ParticleEmitter.Position.Y (depth)", dropped ),
		};
	}

	// ---- Shared -> 2D ----

	/// <summary>System fields only. Does NOT call the target sim's Validate() and does NOT clamp (see header).</summary>
	public static Particle2DSettings FromSharedTo2D( SharedParticleFields shared, float pixelsPerInch )
	{
		if ( shared is null ) throw new ArgumentNullException( nameof( shared ) );
		RequirePixelsPerInch( pixelsPerInch );
		shared.Validate();
		return new Particle2DSettings
		{
			Seed = shared.Seed,
			Capacity = shared.Capacity,
			FixedDeltaTime = shared.FixedDeltaTime,
			Substeps = shared.Substeps,
			Gravity = Mul( shared.Gravity, pixelsPerInch ),
			Wind = Mul( shared.Wind, pixelsPerInch ),
			Drag = shared.Drag,
			TurbulenceStrength = shared.TurbulenceStrength * pixelsPerInch,
			TurbulenceFrequency = shared.TurbulenceFrequency / pixelsPerInch,
			TurbulenceSpeed = shared.TurbulenceSpeed,
			VortexStrength = shared.VortexStrength * pixelsPerInch * pixelsPerInch,
			VortexCenter = Mul( shared.VortexCenter, pixelsPerInch ),
			VortexRadius = shared.VortexRadius * pixelsPerInch,
			FloorEnabled = shared.GroundEnabled,
			FloorY = -shared.GroundOffset * pixelsPerInch,
			Restitution = shared.Restitution,
			Friction = shared.Friction,
		};
	}

	/// <summary>
	/// Emitter fields. Reconstructed emitters are Point emitters: the shared
	/// record is shape-agnostic, so the caller re-applies shape. GravityAngle
	/// stays null (the Gravity vector is used as-is).
	/// </summary>
	public static Particle2DEmitter FromSharedTo2DEmitter( SharedParticleFields shared, float pixelsPerInch )
	{
		if ( shared is null ) throw new ArgumentNullException( nameof( shared ) );
		RequirePixelsPerInch( pixelsPerInch );
		shared.Validate();
		var bursts = new List<Particle2DBurst>();
		foreach ( var b in shared.Bursts ) bursts.Add( new Particle2DBurst( b.Step, b.Count ) );
		return new Particle2DEmitter
		{
			Rate = shared.Rate,
			Bursts = bursts,
			LifetimeMin = shared.LifetimeMin,
			LifetimeMax = shared.LifetimeMax,
			SizeMin = shared.SizeMin * pixelsPerInch,
			SizeMax = shared.SizeMax * pixelsPerInch,
			Speed = shared.Speed * pixelsPerInch,
			SpeedJitter = shared.SpeedJitter,
			SpreadDegrees = shared.SpreadDegrees,
			Direction = shared.Direction,
			BaseVelocity = Mul( shared.BaseVelocity, pixelsPerInch ),
			AngularSpeedMax = shared.AngularSpeedMax,
			Enabled = shared.Enabled,
			Position = Mul( shared.EmitterPosition, pixelsPerInch ),
		};
	}

	// ---- Shared -> 3D ----

	/// <summary>System fields only. Does NOT call the target sim's Validate() and does NOT clamp (see header).</summary>
	public static ParticleSystemSettings FromSharedTo3D( SharedParticleFields shared )
	{
		if ( shared is null ) throw new ArgumentNullException( nameof( shared ) );
		shared.Validate();
		return new ParticleSystemSettings
		{
			Seed = shared.Seed,
			Capacity = shared.Capacity,
			FixedDeltaTime = shared.FixedDeltaTime,
			Substeps = shared.Substeps,
			Gravity = To3DVector( shared.Gravity ),
			Wind = To3DVector( shared.Wind ),
			Drag = shared.Drag,
			TurbulenceStrength = shared.TurbulenceStrength,
			TurbulenceFrequency = shared.TurbulenceFrequency,
			TurbulenceSpeed = shared.TurbulenceSpeed,
			VortexStrength = shared.VortexStrength,
			VortexCenter = To3DVector( shared.VortexCenter ),
			VortexAxis = new Vector3( 0, 0, 1 ),
			VortexRadius = shared.VortexRadius,
			GroundEnabled = shared.GroundEnabled,
			GroundNormal = new Vector3( 0, 0, 1 ),
			GroundOffset = shared.GroundOffset,
			Restitution = shared.Restitution,
			Friction = shared.Friction,
		};
	}

	/// <summary>Emitter fields. Reconstructed emitters are Point emitters (see FromSharedTo2DEmitter).</summary>
	public static ParticleEmitter FromSharedTo3DEmitter( SharedParticleFields shared )
	{
		if ( shared is null ) throw new ArgumentNullException( nameof( shared ) );
		shared.Validate();
		var bursts = new List<ParticleBurst>();
		foreach ( var b in shared.Bursts ) bursts.Add( new ParticleBurst( b.Step, b.Count ) );
		return new ParticleEmitter
		{
			Rate = shared.Rate,
			Bursts = bursts,
			LifetimeMin = shared.LifetimeMin,
			LifetimeMax = shared.LifetimeMax,
			SizeMin = shared.SizeMin,
			SizeMax = shared.SizeMax,
			Speed = shared.Speed,
			SpeedJitter = shared.SpeedJitter,
			SpreadDegrees = shared.SpreadDegrees,
			Direction = To3DVector( shared.Direction ),
			BaseVelocity = To3DVector( shared.BaseVelocity ),
			AngularSpeedMax = shared.AngularSpeedMax,
			Enabled = shared.Enabled,
			Position = To3DVector( shared.EmitterPosition ),
		};
	}

	// ---- Helpers ----

	static void RequirePixelsPerInch( float pixelsPerInch )
	{
		if ( !float.IsFinite( pixelsPerInch ) || pixelsPerInch <= 0 )
			throw new ArgumentOutOfRangeException( nameof( pixelsPerInch ), "pixelsPerInch must be finite and > 0. There is no default factor; the caller supplies it." );
	}

	static Vector2 Div( Vector2 v, float f ) => new( v.X / f, v.Y / f );
	static Vector2 Mul( Vector2 v, float f ) => new( v.X * f, v.Y * f );

	/// <summary>3D (x, y, z) -> shared (x, -z); flags the depth component when non-zero.</summary>
	static Vector2 From3DVector( Vector3 v, string depthField, List<string> dropped )
	{
		if ( v.Y != 0 ) dropped.Add( depthField + " (3D-only depth component: side-view projection keeps X and vertical only)" );
		return new Vector2( v.X, -v.Z );
	}

	/// <summary>Shared (x, down) -> 3D (x, 0, -down). Depth is reconstructed as 0, never recovered.</summary>
	static Vector3 To3DVector( Vector2 v ) => new( v.X, 0f, -v.Y );
}
