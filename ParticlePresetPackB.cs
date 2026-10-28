#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Wave-6 lane P25: 3D preset pack B. Eight named settings bundles in
/// <see cref="ParticleEmitterPresets"/> style (kernel inches, Z-up, seconds):
/// each preset is an emitter shape plus launch numbers (<see cref="Create"/>)
/// and a medium (<see cref="SettingsFor"/>). This file is standalone: it does
/// NOT edit <see cref="ParticleEmitterPresets"/> — the coordinator sweep wires
/// these names into that registry (see the sweep lines at the bottom of the
/// lane report). Until then these presets resolve only through this class.
///
/// Medium rule (lane P38's design, applied here): a preset's medium OVERRIDES
/// the scene gravity — <see cref="SettingsFor"/> assigns
/// <c>Gravity</c> outright, it never adds to the base gravity. The same holds
/// for every other medium field a preset sets (drag, wind, turbulence,
/// vortex, ground/restitution/friction): the value in the table below is the
/// value the system runs with. Fields a preset does not list are inherited
/// from the base settings unchanged, exactly like the existing presets.
/// A medium is per effect system: never pour two presets with different
/// media into one system (the bubbles-fall bug, phase-1 finding 2).
///
/// Name resolution mirrors <see cref="ParticleEmitterPresets.ResolveName"/>:
/// trim, lowercase, strip one <c>fx:</c>/<c>fx_</c>/<c>vfx:</c>/<c>vfx_</c>/
/// <c>particle:</c>/<c>particle_</c> prefix, fold <c>-</c>/<c>:</c>/space/
/// <c>/</c> to <c>_</c>, then match a preset name exactly or as a whole
/// underscore-separated word (<c>big_explosion_burst</c> resolves, bare
/// <c>explosions</c> does not). None of the eight names duplicates any of
/// the existing 14 preset names.
///
/// Vortex presets (tornado, galaxy): <see cref="SettingsFor"/> centres the
/// vortex at the world origin, because settings carry no emitter position.
/// Put the emitter at the origin, or overwrite
/// <c>VortexCenter</c> with the emitter origin after calling
/// <see cref="SettingsFor"/> — the same limitation the existing presets
/// have (they never set a vortex centre at all).
///
/// Size over life is NOT a sim field: a particle's <c>Size</c> is fixed at
/// spawn and only the billboard layer's fade-out shrink changes it
/// (explosion's "fading size" is that layer fade, same as every preset).
/// No colour, texture, or render-layer behaviour lives here; render layers
/// own drawing.</summary>
public static class ParticlePresetPackB
{
	public static readonly string[] Names =
		{ "explosion", "fountain", "waterfall", "tornado", "galaxy", "plasma", "slime", "ash" };

	public static string? ResolveName( string cueName )
	{
		if ( string.IsNullOrWhiteSpace( cueName ) ) return null;
		var n = cueName.Trim().ToLowerInvariant();
		foreach ( var p in new[] { "fx:", "fx_", "vfx:", "vfx_", "particle:", "particle_" } )
			if ( n.StartsWith( p ) ) { n = n[p.Length..]; break; }
		var flat = n.Replace( '-', '_' ).Replace( ':', '_' ).Replace( ' ', '_' ).Replace( '/', '_' );
		foreach ( var name in Names )
			if ( flat == name || flat.StartsWith( name + "_" ) || flat.EndsWith( "_" + name ) || flat.Contains( "_" + name + "_" ) )
				return name;
		return null;
	}

	/// <summary>Fresh emitter for a preset at <paramref name="origin"/>. A
	/// non-zero <paramref name="direction"/> re-aims the launch, otherwise
	/// the preset's own direction stands (same rule as the existing presets).
	/// Burst presets carry one burst at step 0 for a fresh system;
	/// <see cref="ParticleSystem.TriggerCue"/> retimes bursts on cue systems.
	/// Rate presets loop until the caller disables the emitter.</summary>
	public static ParticleEmitter Create( string name, Vector3 origin, Vector3? direction = null )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var em = new ParticleEmitter { Position = origin };
		switch ( key )
		{
			// Radial burst: every direction at once, fast, dead in under a
			// second. Gravity is plain air gravity; the look is the burst
			// itself, and the layer fade-out shrinks it as it dies.
			case "explosion":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 500f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 0.3f; em.LifetimeMax = 0.8f; em.SizeMin = 0.5f; em.SizeMax = 1.5f;
				em.AngularSpeedMax = 15f; em.Bursts.Add( new ParticleBurst( 0, 200 ) );
				break;
			// Continuous upward jet: narrow cone, fast, looping. The arc
			// and the ground bounce come from the medium (air gravity plus
			// a bouncy ground plane), not from the launch numbers.
			case "fountain":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 320f; em.SpeedJitter = 0.25f; em.SpreadDegrees = 18f;
				em.LifetimeMin = 1.5f; em.LifetimeMax = 2.5f; em.SizeMin = 0.8f; em.SizeMax = 2f;
				em.Rate = 120f;
				break;
			// Downward sheet: a flat area emitter (80 in wide, 16 in deep)
			// with the surface normal deliberately NOT used — the sheet
			// falls straight down instead of spraying along the area
			// normal. Misty drag (1.2) keeps the fall soft.
			case "waterfall":
				em.Shape = ParticleEmitterShape.Area; em.AreaU = new Vector3( 40f, 0, 0 ); em.AreaV = new Vector3( 0, 8f, 0 );
				em.UseSurfaceNormal = false; em.Direction = new Vector3( 0, 0, -1 );
				em.Speed = 20f; em.SpeedJitter = 0.3f; em.SpreadDegrees = 10f;
				em.LifetimeMin = 1.2f; em.LifetimeMax = 2f; em.SizeMin = 1f; em.SizeMax = 3f;
				em.Rate = 300f;
				break;
			// Vortex-driven column: a tall box (24 x 24 x 120 in) of
			// particles launched upward into a strong vortex. The vortex
			// does the swirling (purely tangential acceleration); the
			// upward launch plus weak gravity does the rising.
			case "tornado":
				em.Shape = ParticleEmitterShape.Box; em.BoxHalfExtents = new Vector3( 12f, 12f, 60f ); em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 60f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 45f;
				em.LifetimeMin = 3f; em.LifetimeMax = 5f; em.SizeMin = 2f; em.SizeMax = 4f;
				em.AngularSpeedMax = 6f; em.Rate = 200f;
				break;
			// Slow spiral disc: a thin box (100 x 100 x 8 in) of long-lived
			// motes under a gentle vortex and near-zero gravity. Nothing
			// here is fast on purpose — the disc should turn, not fly apart.
			case "galaxy":
				em.Shape = ParticleEmitterShape.Box; em.BoxHalfExtents = new Vector3( 50f, 50f, 4f ); em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 5f; em.SpeedJitter = 0.8f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 8f; em.LifetimeMax = 12f; em.SizeMin = 0.5f; em.SizeMax = 1.2f;
				em.AngularSpeedMax = 2f; em.Rate = 60f;
				break;
			// Fast jittery burst: high launch speed in a wide cone plus
			// the strongest turbulence in the pack, dead in half a second.
			// Small size reads bright-and-sharp once a layer draws it.
			case "plasma":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 380f; em.SpeedJitter = 0.7f; em.SpreadDegrees = 120f;
				em.LifetimeMin = 0.2f; em.LifetimeMax = 0.5f; em.SizeMin = 0.2f; em.SizeMax = 0.6f;
				em.AngularSpeedMax = 20f; em.Bursts.Add( new ParticleBurst( 0, 150 ) );
				break;
			// Slow heavy drips: launched downward, stronger-than-air
			// gravity, high drag, and a ground plane that sticks
			// (restitution 0.05, friction 0.9) — drips land and stay.
			case "slime":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, -1 );
				em.Speed = 15f; em.SpeedJitter = 0.2f; em.SpreadDegrees = 15f;
				em.LifetimeMin = 2f; em.LifetimeMax = 4f; em.SizeMin = 2.5f; em.SizeMax = 5f;
				em.Rate = 30f;
				break;
			// Slow falling grey motes: a wide box of long-lived particles
			// under gentle gravity and high drag, so the fall is a drift
			// (terminal speed ~17 in/s) with a light wind push and soft
			// turbulence wobble.
			case "ash":
				em.Shape = ParticleEmitterShape.Box; em.BoxHalfExtents = new Vector3( 60f, 60f, 10f ); em.Direction = new Vector3( 0, 0, -1 );
				em.Speed = 8f; em.SpeedJitter = 0.6f; em.SpreadDegrees = 25f;
				em.LifetimeMin = 6f; em.LifetimeMax = 10f; em.SizeMin = 0.3f; em.SizeMax = 0.8f;
				em.AngularSpeedMax = 4f; em.Rate = 40f;
				break;
		}
		if ( direction is { } d && d.LengthSquared() > 1e-12f ) em.Direction = d;
		return em;
	}

	/// <summary>A copy of <paramref name="baseSettings"/> with this preset's
	/// medium applied. The base is never mutated. Every gravity below is
	/// an outright OVERRIDE of the base gravity (never an addition):
	/// explosion/fountain/waterfall/plasma run in plain air
	/// (-386.0886 in/s², the default 9.80665 m/s² in inches), tornado in
	/// weak gravity (-40), galaxy in near-zero gravity (-5), slime in
	/// heavy gravity (-600), ash in gentle gravity (-25).</summary>
	public static ParticleSystemSettings SettingsFor( string name, ParticleSystemSettings baseSettings )
	{
		ArgumentNullException.ThrowIfNull( baseSettings );
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var s = Clone( baseSettings );
		switch ( key )
		{
			case "explosion":
				s.Gravity = new Vector3( 0, 0, -386.0886f ); s.Drag = 0.1f;
				break;
			case "fountain":
				s.Gravity = new Vector3( 0, 0, -386.0886f ); s.Drag = 0.15f;
				s.GroundEnabled = true; s.GroundNormal = new Vector3( 0, 0, 1 ); s.GroundOffset = 0f;
				s.Restitution = 0.45f; s.Friction = 0.25f;
				break;
			case "waterfall":
				s.Gravity = new Vector3( 0, 0, -386.0886f ); s.Drag = 1.2f;
				s.TurbulenceStrength = 12f; s.TurbulenceFrequency = 0.2f;
				s.GroundEnabled = true; s.GroundNormal = new Vector3( 0, 0, 1 ); s.GroundOffset = 0f;
				s.Restitution = 0.2f; s.Friction = 0.4f;
				break;
			case "tornado":
				s.Gravity = new Vector3( 0, 0, -40f ); s.Drag = 0.3f;
				s.TurbulenceStrength = 10f; s.TurbulenceFrequency = 0.2f;
				s.VortexStrength = 60000f; s.VortexCenter = Vector3.Zero;
				s.VortexAxis = new Vector3( 0, 0, 1 ); s.VortexRadius = 15f;
				break;
			case "galaxy":
				s.Gravity = new Vector3( 0, 0, -5f ); s.Drag = 0.05f;
				s.TurbulenceStrength = 2f; s.TurbulenceFrequency = 0.1f;
				// Wide vortex radius on purpose. Vortex accel magnitude
				// is S*r/(r^2+R^2) (the cross product carries an |r|), so
				// it peaks at S/(2R) when r=R: a narrow radius peaks
				// violently near the centre (S=8000, R=20 peaks at 200
				// in/s^2 and flings the disc apart — the first cut's bug).
				// S=1200, R=40 peaks at just 15 in/s^2 at r=40 and stays
				// gentle everywhere in the disc — a slow turn, not a fling.
				s.VortexStrength = 1200f; s.VortexCenter = Vector3.Zero;
				s.VortexAxis = new Vector3( 0, 0, 1 ); s.VortexRadius = 40f;
				break;
			case "plasma":
				s.Gravity = new Vector3( 0, 0, -386.0886f ); s.Drag = 0.05f;
				s.TurbulenceStrength = 120f; s.TurbulenceFrequency = 0.5f; s.TurbulenceSpeed = 1f;
				break;
			case "slime":
				s.Gravity = new Vector3( 0, 0, -600f ); s.Drag = 2.5f;
				s.GroundEnabled = true; s.GroundNormal = new Vector3( 0, 0, 1 ); s.GroundOffset = 0f;
				s.Restitution = 0.05f; s.Friction = 0.9f;
				break;
			case "ash":
				s.Gravity = new Vector3( 0, 0, -25f ); s.Drag = 1.5f;
				s.TurbulenceStrength = 6f; s.TurbulenceFrequency = 0.15f;
				s.Wind = new Vector3( 3f, 1f, 0f );
				break;
		}
		return s;
	}

	// Same field list as ParticleEmitterPresets.Clone, plus KillVolumes:
	// the lane-4 clone silently drops the base kill volumes, this one
	// preserves them (the list itself is shared, never mutated here).
	static ParticleSystemSettings Clone( ParticleSystemSettings s ) => new()
	{
		Capacity = s.Capacity, Seed = s.Seed, FixedDeltaTime = s.FixedDeltaTime, Substeps = s.Substeps,
		Gravity = s.Gravity, Wind = s.Wind, Drag = s.Drag,
		TurbulenceStrength = s.TurbulenceStrength, TurbulenceFrequency = s.TurbulenceFrequency, TurbulenceSpeed = s.TurbulenceSpeed,
		VortexStrength = s.VortexStrength, VortexCenter = s.VortexCenter, VortexAxis = s.VortexAxis, VortexRadius = s.VortexRadius,
		GroundEnabled = s.GroundEnabled, GroundNormal = s.GroundNormal, GroundOffset = s.GroundOffset,
		CollisionMesh = s.CollisionMesh, CollisionRadius = s.CollisionRadius, Restitution = s.Restitution, Friction = s.Friction,
		RenderType = s.RenderType, StrandPoints = s.StrandPoints, KillVolumes = s.KillVolumes,
	};
}
