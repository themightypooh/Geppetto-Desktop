#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Documented look colour for a pack-C preset (linear-ish 0..1 RGBA).
/// The CPU sim carries no per-particle colour (render layers own the look, as
/// with the built-in presets), so this is the authoring look a layer should
/// paint the preset with — it is data, not a claim that the sim draws it.</summary>
public readonly record struct ParticlePresetColour( float R, float G, float B, float A );

/// <summary>Wave-6 lane P26: 3D preset pack C — underwater / materials.
/// Eight named settings bundles in the <see cref="ParticleEmitterPresets"/>
/// style: <see cref="Create"/> is the emitter shape plus launch numbers,
/// <see cref="SettingsFor"/> is the medium (gravity / wind / drag /
/// turbulence, plus ground for sand). Units are kernel inches and seconds,
/// Z-up, like <see cref="ParticleSystem"/>.
///
/// Medium rule (same as the built-in presets, and the lane-18 bundle rule):
/// a preset's medium OVERRIDES the scene gravity, it never adds to it —
/// <see cref="SettingsFor"/> writes an absolute gravity value, so a system
/// built from a pack-C preset behaves the same whatever base it started from.
/// The one exception is <c>frost_breath</c>, which like <c>breath_frost</c>
/// deliberately leaves gravity alone: breath happens in air, so it inherits
/// the scene's gravity and arcs down under it.
///
/// This file is NOT wired into <see cref="ParticleEmitterPresets"/>: the
/// coordinator sweep adds the hookup (see the sweep lines in the lane
/// report). Until then these presets resolve only through this class, never
/// through <see cref="ParticleEmitterPresets.ResolveName"/>.
///
/// Name note: <c>frost_breath</c> is NOT a collision with the built-in
/// <c>breath_frost</c>. The whole-underscore-word resolution rule matches
/// <c>breath_frost</c> only as that exact word sequence, and
/// <c>frost_breath</c> is the reverse sequence, so neither name resolves to
/// the other — proven by a test in ParticlePresetPackCTests. The brief's
/// rename-if-needed clause therefore does not trigger; the name stands as
/// briefed, documented here so nobody "fixes" it later.</summary>
public static class ParticlePresetPackC
{
	public static readonly string[] Names =
		{ "ink", "plankton", "sand", "silt", "caustic_motes", "oil", "lava", "frost_breath" };

	/// <summary>Same name rules as <see cref="ParticleEmitterPresets.ResolveName"/>:
	/// an exact preset name, an <c>fx:</c>/<c>vfx:</c>/<c>particle:</c>-prefixed
	/// one, or a name that carries the preset as a whole underscore-separated
	/// word (<c>deep_ink_cloud</c>). Returns null for anything else, including
	/// the built-in presets — pack C never claims a name it does not own.</summary>
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
	/// non-zero <paramref name="direction"/> re-aims the launch, exactly as
	/// the built-in <c>Create</c> does; otherwise the preset's own direction
	/// stands. Burst presets carry one burst at step 0 for a fresh system;
	/// rate presets loop until the caller disables the emitter.</summary>
	public static ParticleEmitter Create( string name, Vector3 origin, Vector3? direction = null )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var em = new ParticleEmitter { Position = origin };
		switch ( key )
		{
			// Underwater ink cloud: a slow puff that hangs and spreads in
			// water. Point source, wide cone, long life, big soft sizes.
			case "ink":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 25f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 60f;
				em.LifetimeMin = 2f; em.LifetimeMax = 4f; em.SizeMin = 2f; em.SizeMax = 6f;
				em.AngularSpeedMax = 2f; em.Bursts.Add( new ParticleBurst( 0, 80 ) );
				break;
			// Tiny drifting motes in water: a whole volume of near-weightless
			// specks carried by gentle currents. Box volume, any direction,
			// very long life, tiny sizes, continuous rate.
			case "plankton":
				em.Shape = ParticleEmitterShape.Box; em.BoxHalfExtents = new Vector3( 40f, 40f, 15f ); em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 3f; em.SpeedJitter = 0.8f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 8f; em.LifetimeMax = 15f; em.SizeMin = 0.2f; em.SizeMax = 0.5f;
				em.Rate = 20f;
				break;
			// Heavy grains falling fast onto a floor: launched downward,
			// short-ish life, small hard sizes, one burst. The ground bounce
			// (low restitution) lives in SettingsFor, not here.
			case "sand":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, -1 );
				em.Speed = 80f; em.SpeedJitter = 0.3f; em.SpreadDegrees = 15f;
				em.LifetimeMin = 1.5f; em.LifetimeMax = 2.5f; em.SizeMin = 0.5f; em.SizeMax = 1.2f;
				em.Bursts.Add( new ParticleBurst( 0, 120 ) );
				break;
			// Slow-settling murk in water: a volume of fine sediment sinking
			// almost reluctantly. Box volume, gentle downward launch, long
			// life, small sizes, continuous rate.
			case "silt":
				em.Shape = ParticleEmitterShape.Box; em.BoxHalfExtents = new Vector3( 30f, 30f, 5f ); em.Direction = new Vector3( 0, 0, -1 );
				em.Speed = 5f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 30f;
				em.LifetimeMin = 4f; em.LifetimeMax = 8f; em.SizeMin = 0.3f; em.SizeMax = 0.8f;
				em.Rate = 30f;
				break;
			// Bright tiny sparkles drifting in water (light playing on the
			// surface, caught mid-water): a sphere volume, any direction,
			// slow, small, bright, continuous rate.
			case "caustic_motes":
				em.Shape = ParticleEmitterShape.Sphere; em.SphereRadius = 10f; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 8f; em.SpeedJitter = 0.7f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 3f; em.LifetimeMax = 6f; em.SizeMin = 0.2f; em.SizeMax = 0.6f;
				em.Rate = 25f;
				break;
			// Slow heavy blobs rising very slowly in water (buoyancy barely
			// positive): point source, gentle upward launch, large sizes,
			// long life, slow continuous rate.
			case "oil":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 5f; em.SpeedJitter = 0.4f; em.SpreadDegrees = 20f;
				em.LifetimeMin = 4f; em.LifetimeMax = 7f; em.SizeMin = 3f; em.SizeMax = 6f;
				em.Rate = 10f;
				break;
			// Rising hot blobs: strong upward bias, high turbulence so the
			// column churns, bright sizes, burst-ish continuous rate (90/s —
			// a fountain, not a single pop).
			case "lava":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 120f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 25f;
				em.LifetimeMin = 1f; em.LifetimeMax = 2f; em.SizeMin = 2f; em.SizeMax = 5f;
				em.AngularSpeedMax = 6f; em.Rate = 90f;
				break;
			// Cold breath vapour: a forward cone burst, like breath_frost
			// but under this pack's own name (see the class note on why the
			// two names do not collide). Short life = fast fade.
			case "frost_breath":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 1, 0, 0 );
				em.Speed = 60f; em.SpeedJitter = 0.3f; em.SpreadDegrees = 15f;
				em.LifetimeMin = 0.6f; em.LifetimeMax = 1.2f; em.SizeMin = 1f; em.SizeMax = 2.5f;
				em.Bursts.Add( new ParticleBurst( 0, 40 ) );
				break;
		}
		if ( direction is { } d && d.LengthSquared() > 1e-12f ) em.Direction = d;
		return em;
	}

	/// <summary>A copy of <paramref name="baseSettings"/> with this preset's
	/// medium applied. The base is never mutated. Gravity values here are
	/// absolute overrides, never added to the base gravity — see the class
	/// note. Field-for-field this mirrors
	/// <see cref="ParticleEmitterPresets.SettingsFor"/>'s clone (including
	/// its KillVolumes behaviour: a fresh empty list, not the base's).</summary>
	public static ParticleSystemSettings SettingsFor( string name, ParticleSystemSettings baseSettings )
	{
		ArgumentNullException.ThrowIfNull( baseSettings );
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var s = Clone( baseSettings );
		switch ( key )
		{
			// Water, near-neutral buoyancy: a slight sink (-15 vs the
			// -386 air default), heavy drag so the puff stalls and hangs,
			// moderate curl so it billows rather than drifts.
			case "ink":
				s.Gravity = new Vector3( 0, 0, -15f ); s.Drag = 3f;
				s.TurbulenceStrength = 25f; s.TurbulenceFrequency = 0.2f;
				break;
			// Water, near-zero gravity (-2) plus a gentle current as wind:
			// drag pulls motes toward the current, so the cloud drifts
			// sideways instead of falling. Low turbulence keeps it calm.
			case "plankton":
				s.Gravity = new Vector3( 0, 0, -2f ); s.Drag = 1f;
				s.TurbulenceStrength = 5f; s.TurbulenceFrequency = 0.15f; s.Wind = new Vector3( 3f, 1f, 0f );
				break;
			// Air, heavier than default: gravity -500 (above the -386
			// default) and almost no drag, so grains fall fast; the ground
			// plane is on with low restitution (0.15) and noticeable
			// friction, so grains land with a dead thud and stay put.
			case "sand":
				s.Gravity = new Vector3( 0, 0, -500f ); s.Drag = 0.05f;
				s.GroundEnabled = true; s.GroundNormal = new Vector3( 0, 0, 1 ); s.GroundOffset = 0f;
				s.Restitution = 0.15f; s.Friction = 0.5f;
				break;
			// Water, gentle fall: gravity -20 against high drag (2.5) gives
			// a terminal sink of only ~8 in/s, so murk settles slowly and
			// the long fade (4–8 s life) is the look.
			case "silt":
				s.Gravity = new Vector3( 0, 0, -20f ); s.Drag = 2.5f;
				s.TurbulenceStrength = 6f; s.TurbulenceFrequency = 0.1f;
				break;
			// Water, near-weightless sparkle: gravity -5, drag 1.2, gentle
			// curl (12) — motes wander slowly instead of falling or rising.
			case "caustic_motes":
				s.Gravity = new Vector3( 0, 0, -5f ); s.Drag = 1.2f;
				s.TurbulenceStrength = 12f; s.TurbulenceFrequency = 0.3f;
				break;
			// Water, buoyancy barely positive: gravity +12 (compare
			// bubbles' +60) against high drag (3) gives a terminal rise of
			// only ~4 in/s — blobs creep upward, heavy and slow.
			case "oil":
				s.Gravity = new Vector3( 0, 0, 12f ); s.Drag = 3f;
				s.TurbulenceStrength = 4f; s.TurbulenceFrequency = 0.15f;
				break;
			// Hot rise: gravity +150 (strong upward bias, above fire's
			// +80) with low drag, and the pack's highest turbulence (50)
			// so the column churns hard while it climbs.
			case "lava":
				s.Gravity = new Vector3( 0, 0, 150f ); s.Drag = 0.8f;
				s.TurbulenceStrength = 50f; s.TurbulenceFrequency = 0.3f;
				break;
			// Air breath, like breath_frost: no gravity override (inherits
			// the scene's gravity and arcs down under it), drag 1.8 so the
			// cone stalls quickly, light curl for vapour texture.
			case "frost_breath":
				s.Drag = 1.8f; s.TurbulenceStrength = 12f; s.TurbulenceFrequency = 0.4f;
				break;
		}
		return s;
	}

	/// <summary>Documented look colour for a preset (see
	/// <see cref="ParticlePresetColour"/>). Render layers own the actual
	/// drawing; this is what they should paint.</summary>
	public static ParticlePresetColour ColourFor( string name )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		return key switch
		{
			"ink" => new ParticlePresetColour( 0.05f, 0.05f, 0.12f, 0.90f ), // dark blue-black cloud
			"plankton" => new ParticlePresetColour( 0.80f, 0.90f, 0.85f, 0.60f ), // pale drifting motes
			"sand" => new ParticlePresetColour( 0.76f, 0.66f, 0.45f, 1.00f ), // tan grains
			"silt" => new ParticlePresetColour( 0.45f, 0.42f, 0.35f, 0.50f ), // murky brown-grey
			"caustic_motes" => new ParticlePresetColour( 0.90f, 1.00f, 0.95f, 0.80f ), // bright near-white sparkle
			"oil" => new ParticlePresetColour( 0.25f, 0.15f, 0.05f, 0.85f ), // dark amber blobs
			"lava" => new ParticlePresetColour( 1.00f, 0.45f, 0.10f, 1.00f ), // bright orange-red
			"frost_breath" => new ParticlePresetColour( 0.85f, 0.95f, 1.00f, 0.70f ), // pale icy blue-white
			_ => throw new ArgumentException( $"Unknown particle preset '{name}'." ),
		};
	}

	/// <summary>One-line documented look for a preset, for authoring UIs and
	/// for the sweep reader checking a name against its intent.</summary>
	public static string DescriptionFor( string name )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		return key switch
		{
			"ink" => "Underwater ink cloud: slow expansion, high drag, near-neutral buoyancy, dark colour, long fade.",
			"plankton" => "Tiny drifting motes in water: near-zero gravity, gentle currents, very long lifetime.",
			"sand" => "Heavy grains falling fast: high gravity, ground bounce with low restitution, short life after landing.",
			"silt" => "Slow-settling murk in water: gentle fall, high drag, long fade.",
			"caustic_motes" => "Bright tiny sparkles drifting in water: slow, gentle turbulence.",
			"oil" => "Slow heavy blobs rising very slowly in water: buoyancy barely positive, high drag, large size.",
			"lava" => "Rising hot blobs: strong upward bias, high turbulence, bright colour, burst-ish rate.",
			"frost_breath" => "Cold breath vapour: forward cone burst, slow expansion, fast fade, cool colour.",
			_ => throw new ArgumentException( $"Unknown particle preset '{name}'." ),
		};
	}

	static ParticleSystemSettings Clone( ParticleSystemSettings s ) => new()
	{
		Capacity = s.Capacity, Seed = s.Seed, FixedDeltaTime = s.FixedDeltaTime, Substeps = s.Substeps,
		Gravity = s.Gravity, Wind = s.Wind, Drag = s.Drag,
		TurbulenceStrength = s.TurbulenceStrength, TurbulenceFrequency = s.TurbulenceFrequency, TurbulenceSpeed = s.TurbulenceSpeed,
		VortexStrength = s.VortexStrength, VortexCenter = s.VortexCenter, VortexAxis = s.VortexAxis, VortexRadius = s.VortexRadius,
		GroundEnabled = s.GroundEnabled, GroundNormal = s.GroundNormal, GroundOffset = s.GroundOffset,
		CollisionMesh = s.CollisionMesh, CollisionRadius = s.CollisionRadius, Restitution = s.Restitution, Friction = s.Friction,
		RenderType = s.RenderType, StrandPoints = s.StrandPoints,
	};
}
