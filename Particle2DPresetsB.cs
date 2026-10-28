#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// Lane P15 (particles session, 2026-10-24): 2D preset pack B — eight more emitter
// presets in Particle2D.cs's SettingsFor/Create style. Flat XY, Y-down canvas pixels,
// seconds, exactly like lane 1's Particle2DPresets. This file is NOT wired into
// Particle2D.cs: it carries its own Names / ResolveName / Create / SettingsFor, so
// the coordinator sweep can either keep it as a standalone pack or fold its cases
// into Particle2DPresets. Until that sweep lands, call this class directly —
// Particle2DPresets.Create / SettingsFor do NOT know these eight names.
//
// A preset is two halves, mirroring lane 1:
//   - Create(): the emitter bundle — shape, rate or burst, launch direction, speed,
//     lifetime, size, colour. A preset that is a burst (fireworks) has Rate = 0 and
//     its particles in Bursts; a continuous preset has Rate > 0 and no bursts.
//   - SettingsFor(): the medium — a copy of the caller's base settings with only the
//     preset's fields changed (gravity, wind, drag, turbulence, restitution,
//     friction). The base is never mutated, and fields the preset does not mention
//     keep the base value, exactly as lane 1's SettingsFor behaves.
//
// Name rules (mirrored character-for-character from Particle2DPresets.ResolveName):
// trim, lowercase, strip ONE leading prefix from fx:, fx_, vfx:, vfx_, particle:,
// particle_ (first match wins), replace '-', ':', ' ', '/' with '_', then match when
// the result equals a preset name or carries it as a whole underscore-separated word
// (prefix word, suffix word, or middle word). So "fx:storm", "VFX:Blizzard",
// "heavy_fog_drift" and "fireworks-show" all resolve, while "fog" alone, "sandstorm"
// and "rain" do not resolve here ("rain" is lane 1's name, not pack B's).
// None of the eight names duplicates a lane-1 preset name (sparks, rain, snow,
// bubbles, confetti, magic, dust, splash).
//
// Floor note for hail: the floor line is scene-owned, not preset-owned — a fixed
// FloorY would be wrong for every canvas but one. SettingsFor therefore sets the
// bounce itself (Restitution 0.85, Friction 0.05) and leaves FloorEnabled / FloorY
// at the base value; enable the floor and set FloorY to the scene's ground line to
// get the bounce. Every other preset's medium is complete on its own.

public static class Particle2DPresetsB
{
	public static readonly string[] Names =
		{ "storm", "drizzle", "blizzard", "hail", "fog_drift", "pollen", "embers", "fireworks" };

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

	/// <summary>Fresh emitter for a preset at <paramref name="origin"/>. A non-zero <paramref name="direction"/> re-aims the launch; otherwise the preset's own direction stands.</summary>
	public static Particle2DEmitter Create( string name, Vector2 origin, Vector2? direction = null )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D particle preset pack B '{name}'. Presets: {string.Join( ", ", Names )}." );
		var em = new Particle2DEmitter { Position = origin };
		switch ( key )
		{
			case "storm":
				// Heavy slanted rain: a wide top line, fast fall, high rate. The slant is
				// in the launch direction (0.35, 1); the medium adds a 180 px/s wind on top.
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 400, 10 );
				em.Direction = new Vector2( 0.35f, 1 );
				em.Speed = 1100f; em.SpeedJitter = 0.1f; em.SpreadDegrees = 2f;
				em.LifetimeMin = 1.2f; em.LifetimeMax = 1.2f; em.SizeMin = 2f; em.SizeMax = 4f;
				em.ColorStart = new Particle2DColor( 0.55f, 0.75f, 1, 0.8f ); em.ColorEnd = new Particle2DColor( 0.55f, 0.75f, 1, 0 );
				em.Rate = 500f;
				break;
			case "drizzle":
				// Light fine rain: same top line as storm, but small drops, a low rate and
				// a gentle fall — the quiet counterpart, not a dimmer storm.
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 400, 10 );
				em.Direction = new Vector2( 0.05f, 1 );
				em.Speed = 350f; em.SpeedJitter = 0.15f; em.SpreadDegrees = 2f;
				em.LifetimeMin = 2.5f; em.LifetimeMax = 2.5f; em.SizeMin = 1f; em.SizeMax = 2f;
				em.ColorStart = new Particle2DColor( 0.65f, 0.85f, 1, 0.5f ); em.ColorEnd = new Particle2DColor( 0.65f, 0.85f, 1, 0 );
				em.Rate = 80f;
				break;
			case "blizzard":
				// Dense snow: a wide top line at a high rate, slow fall, wide spread. The
				// sideways drive is in the medium (260 px/s wind + heavy turbulence).
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 400, 10 );
				em.Direction = new Vector2( 0, 1 );
				em.Speed = 60f; em.SpeedJitter = 0.8f; em.SpreadDegrees = 30f;
				em.LifetimeMin = 6f; em.LifetimeMax = 10f; em.SizeMin = 2f; em.SizeMax = 5f;
				em.ColorStart = new Particle2DColor( 1, 1, 1, 0.95f ); em.ColorEnd = new Particle2DColor( 1, 1, 1, 0.9f );
				em.Rate = 200f;
				break;
			case "hail":
				// Fast hard fall, larger stones than any rain preset. Bounce is the
				// medium's Restitution 0.85 — see the floor note in the class header for
				// why FloorY itself is left to the scene.
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 400, 10 );
				em.Direction = new Vector2( 0, 1 );
				em.Speed = 700f; em.SpeedJitter = 0.1f; em.SpreadDegrees = 3f;
				em.LifetimeMin = 2f; em.LifetimeMax = 2f; em.SizeMin = 6f; em.SizeMax = 10f;
				em.ColorStart = new Particle2DColor( 0.85f, 0.9f, 1, 0.9f ); em.ColorEnd = new Particle2DColor( 0.85f, 0.9f, 1, 0 );
				em.Rate = 150f;
				break;
			case "fog_drift":
				// Very slow, large, soft puffs: a broad box, near-zero gravity, a long
				// life, and a low alpha so overlapping puffs read as fog, not blobs.
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 200, 100 );
				em.Direction = new Vector2( 1, 0 );
				em.Speed = 6f; em.SpeedJitter = 0.8f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 12f; em.LifetimeMax = 20f; em.SizeMin = 30f; em.SizeMax = 60f; em.SizeEndScale = 1.2f;
				em.ColorStart = new Particle2DColor( 0.9f, 0.9f, 0.95f, 0.12f ); em.ColorEnd = new Particle2DColor( 0.9f, 0.9f, 0.95f, 0 );
				em.Rate = 8f;
				break;
			case "pollen":
				// Slow drifting motes: a broad box, a slight upward bias in the medium
				// (gravity -25, versus bubbles' -90), gentle turbulence, pale yellow.
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 200, 100 );
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 12f; em.SpeedJitter = 0.8f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 5f; em.LifetimeMax = 9f; em.SizeMin = 1f; em.SizeMax = 3f;
				em.ColorStart = new Particle2DColor( 1, 0.95f, 0.6f, 0.5f ); em.ColorEnd = new Particle2DColor( 1, 0.95f, 0.6f, 0 );
				em.Rate = 20f;
				break;
			case "embers":
				// Rising sparks: continuous (Rate, not a burst), warm palette, size that
				// shrinks to 20% over life and fast frame cycling for the flicker. The
				// medium rises them (gravity -120) with a light turbulent wobble.
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 120f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 40f;
				em.LifetimeMin = 1f; em.LifetimeMax = 2f; em.SizeMin = 2f; em.SizeMax = 4f; em.SizeEndScale = 0.2f;
				em.Palette = new List<Particle2DColor>
				{
					new( 1, 0.9f, 0.3f, 1 ), new( 1, 0.6f, 0.1f, 1 ),
					new( 1, 0.3f, 0, 1 ), new( 1, 0.85f, 0.5f, 1 ),
				};
				em.EndAlpha = 0;
				em.FrameMin = 0; em.FrameMax = 3; em.FrameAdvance = 12f;
				em.AngularSpeedMax = 6f;
				em.Rate = 60f;
				break;
			case "fireworks":
				// One explosion, not a rate: Rate is 0 and the whole effect is a single
				// 150-particle burst at step 0. Spread 180 makes the launch radial in
				// every direction (the stored direction is only a formality); the medium
				// then drags the burst out and drops it (gravity 900) while the palette
				// fades to alpha 0 over a short life.
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 500f; em.SpeedJitter = 0.3f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 0.8f; em.LifetimeMax = 1.4f; em.SizeMin = 3f; em.SizeMax = 6f;
				em.Palette = new List<Particle2DColor>
				{
					new( 1, 0.2f, 0.2f, 1 ), new( 1, 0.85f, 0.2f, 1 ), new( 0.2f, 0.85f, 1, 1 ),
					new( 1, 0.3f, 0.85f, 1 ), new( 0.3f, 0.9f, 0.4f, 1 ),
				};
				em.EndAlpha = 0;
				em.Bursts.Add( new Particle2DBurst( 0, 150 ) );
				break;
		}
		if ( direction is { } d && d.LengthSquared() > 1e-12f ) em.Direction = d;
		return em;
	}

	/// <summary>A copy of <paramref name="baseSettings"/> with this preset's medium applied. The base is never mutated; fields the preset does not mention keep the base value.</summary>
	public static Particle2DSettings SettingsFor( string name, Particle2DSettings baseSettings )
	{
		ArgumentNullException.ThrowIfNull( baseSettings );
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D particle preset pack B '{name}'. Presets: {string.Join( ", ", Names )}." );
		var s = Clone( baseSettings );
		switch ( key )
		{
			case "storm": s.Gravity = new Vector2( 0, 1800f ); s.Wind = new Vector2( 180, 0 ); s.Drag = 0.1f; break;
			case "drizzle": s.Gravity = new Vector2( 0, 700f ); s.Wind = new Vector2( 30, 0 ); s.Drag = 0.2f; break;
			case "blizzard":
				s.Gravity = new Vector2( 0, 50f ); s.Wind = new Vector2( 260, 0 ); s.Drag = 1.0f;
				s.TurbulenceStrength = 120f; s.TurbulenceFrequency = 0.01f; s.TurbulenceSpeed = 0.8f;
				break;
			case "hail": s.Gravity = new Vector2( 0, 1600f ); s.Drag = 0.05f; s.Restitution = 0.85f; s.Friction = 0.05f; break;
			case "fog_drift":
				s.Gravity = new Vector2( 0, 5f ); s.Wind = new Vector2( 8, 0 ); s.Drag = 0.8f;
				s.TurbulenceStrength = 10f; s.TurbulenceFrequency = 0.01f; s.TurbulenceSpeed = 0.3f;
				break;
			case "pollen":
				s.Gravity = new Vector2( 0, -25f ); s.Wind = new Vector2( 15, 0 ); s.Drag = 0.6f;
				s.TurbulenceStrength = 18f; s.TurbulenceFrequency = 0.02f; s.TurbulenceSpeed = 0.4f;
				break;
			case "embers":
				s.Gravity = new Vector2( 0, -120f ); s.Wind = new Vector2( 10, 0 ); s.Drag = 0.4f;
				s.TurbulenceStrength = 25f; s.TurbulenceFrequency = 0.02f; s.TurbulenceSpeed = 0.6f;
				break;
			case "fireworks": s.Gravity = new Vector2( 0, 900f ); s.Drag = 0.8f; break;
		}
		return s;
	}

	static Particle2DSettings Clone( Particle2DSettings s ) => new()
	{
		Capacity = s.Capacity, Seed = s.Seed, FixedDeltaTime = s.FixedDeltaTime, Substeps = s.Substeps,
		Gravity = s.Gravity, GravityAngleDegrees = s.GravityAngleDegrees, Wind = s.Wind, Drag = s.Drag,
		TurbulenceStrength = s.TurbulenceStrength, TurbulenceFrequency = s.TurbulenceFrequency, TurbulenceSpeed = s.TurbulenceSpeed,
		VortexStrength = s.VortexStrength, VortexCenter = s.VortexCenter, VortexRadius = s.VortexRadius,
		FloorEnabled = s.FloorEnabled, FloorY = s.FloorY, Restitution = s.Restitution, Friction = s.Friction,
		WrapEnabled = s.WrapEnabled, WrapMin = s.WrapMin, WrapMax = s.WrapMax,
	};
}
