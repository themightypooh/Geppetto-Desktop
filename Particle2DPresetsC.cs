#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Wave-6 lane P73: 2D preset pack C — sparkle, portal, halo, shockwave,
/// smoke, and fireflies. Six named emitter + medium bundles in the lane-1
/// <see cref="Particle2DPresets"/> style: <see cref="Create"/> is the emitter
/// shape plus launch numbers, <see cref="SettingsFor"/> is the medium
/// (gravity / drag / turbulence / vortex / floor, as applicable). Units are
/// canvas pixels, px/s, seconds, Y-down, like the rest of Particle2D: +Y is
/// down the screen, so a negative-Y gravity rises and a positive-Y gravity
/// falls.
///
/// Medium rule (same as lane 1 and the 3D pack-C pattern): a preset's medium
/// OVERRIDES the scene gravity, it never adds to it — <see cref="SettingsFor"/>
/// writes an absolute gravity value, so a system built from a pack-C preset
/// behaves the same whatever base it started from.
///
/// This file is NOT wired into <see cref="Particle2DPresets"/>: the
/// coordinator sweep adds the fallback-delegation hookup (see the sweep lines
/// in the lane report). Until then these presets resolve only through this
/// class, never through <see cref="Particle2DPresets.ResolveName"/>.
///
/// Name-collision check (2026-10-24, hard requirement of the brief, done
/// against the exact names, not loose matches): NO exact-name collision.
/// Pack-C names (sparkle_trails, portal, aura, shockwave_ring, smoke_puff,
/// fireflies) match none of lane 1's 8 2D presets (sparks, rain, snow,
/// bubbles, confetti, magic, dust, splash), none of P15 pack B's reserved
/// roster (storm, drizzle, blizzard, hail, fog_drift, pollen, embers,
/// fireworks), and none of the 3D built-in names (water_splash, bubbles,
/// blood_mist, sparks, dust_motes, breath_frost, fire, smoke, rain, snow,
/// leaves, confetti, steam, magic_sparkle) nor the 3D pack-B/C names
/// (explosion, fountain, waterfall, tornado, galaxy, plasma, slime, ash,
/// ink, plankton, sand, silt, caustic_motes, oil, lava, frost_breath).
/// Near-misses, checked and cleared: sparkle_trails vs sparks — "sparkle"
/// is not the whole word "sparks", so neither name resolves as the other
/// (proven by test); fireflies vs fire — "fireflies" is a single word with
/// no underscore, so it carries no whole word "fire"; smoke_puff vs smoke —
/// distinct exact names, but smoke_puff does carry "smoke" as a leading
/// underscore word, so a future unified loose resolver that checked 3D
/// names first would claim it: any such resolver must exact-match first
/// (or check 2D pack C before the 3D built-ins). Within this class there is
/// no shadowing: ResolveName returns null for every name it does not own,
/// including sparks, magic, and smoke.
///
/// Honest limits, carried in the Descriptions too (the 2D sim does not yet
/// carry these features, and this pack does not fake them):
/// - sparkle_trails: the sim has no trail/strand records in 2D, so the
///   "trail" is fast fading shrinking points, not a drawn trail.
/// - portal: the sim's vortex is tangential-only (perp(r) acceleration),
///   there is no centre-pull force, so particles swirl around the centre
///   rather than spiralling inward. SettingsFor sets the vortex strength
///   and radius but NOT the centre: the caller must set
///   settings.VortexCenter to the portal's origin after SettingsFor.
/// - fireflies: the sim's colour over life is one start→end lerp plus
///   FadeIn — there is no oscillating blink curve, so fireflies fade in
///   and out over life but do not blink on/off repeatedly. True blink
///   needs an over-life alpha curve (P11's lane, not this one).</summary>
public static class Particle2DPresetsC
{
	public static readonly string[] Names =
		{ "sparkle_trails", "portal", "aura", "shockwave_ring", "smoke_puff", "fireflies" };

	/// <summary>Same name rules as <see cref="Particle2DPresets.ResolveName"/>:
	/// an exact preset name, an fx:/vfx:/particle:-prefixed one, or a name
	/// that carries the preset as a whole underscore-separated word
	/// (e.g. soft_sparkle_trails). Returns null for anything else,
	/// including every pre-existing preset — pack C never claims a name
	/// it does not own, and never shadows one.</summary>
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
	/// the lane-1 <c>Create</c> does; otherwise the preset's own direction
	/// stands. Burst presets carry one burst at step 0 for a fresh system;
	/// rate presets loop until the caller disables the emitter.</summary>
	public static Particle2DEmitter Create( string name, Vector2 origin, Vector2? direction = null )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var em = new Particle2DEmitter { Position = origin };
		switch ( key )
		{
			// Small fast points that fade and shrink over life (size-over-
			// life twinkle). One burst; the sim has no 2D trail records,
			// so the trail read comes from speed + fade, not a drawn trail.
			case "sparkle_trails":
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 320f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 120f;
				em.LifetimeMin = 0.5f; em.LifetimeMax = 1.1f; em.SizeMin = 1f; em.SizeMax = 3f;
				em.SizeEndScale = 0.15f;
				em.ColorStart = new Particle2DColor( 1, 0.95f, 0.7f, 1 ); em.ColorEnd = new Particle2DColor( 0.7f, 0.9f, 1, 0 );
				em.ColorJitter = 0.15f; em.AngularSpeedMax = 8f; em.FadeIn = 0.1f;
				em.Bursts.Add( new Particle2DBurst( 0, 60 ) );
				break;
			// Swirling disc around a centre point: circle spawn, any
			// direction, long life, continuous rate. The vortex itself
			// lives in SettingsFor (caller sets VortexCenter = origin).
			case "portal":
				em.Shape = Particle2DEmitterShape.Circle; em.CircleRadius = 70f;
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 30f; em.SpeedJitter = 0.6f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 4f; em.LifetimeMax = 6f; em.SizeMin = 3f; em.SizeMax = 6f;
				em.SizeEndScale = 0.6f;
				em.ColorStart = new Particle2DColor( 0.65f, 0.4f, 1, 0.9f ); em.ColorEnd = new Particle2DColor( 0.3f, 0.9f, 1, 0 );
				em.AngularSpeedMax = 4f; em.Rate = 40f;
				break;
			// Soft slow halo around an origin: point source, slow in any
			// direction, large soft sizes that grow slightly, low alpha,
			// gentle fade-in. Near-zero gravity lives in SettingsFor.
			case "aura":
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 12f; em.SpeedJitter = 0.6f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 3f; em.LifetimeMax = 5f; em.SizeMin = 14f; em.SizeMax = 26f;
				em.SizeEndScale = 1.2f;
				em.ColorStart = new Particle2DColor( 1, 0.9f, 0.7f, 0.25f ); em.ColorEnd = new Particle2DColor( 1, 0.8f, 0.5f, 0 );
				em.FadeIn = 0.3f; em.Rate = 20f;
				break;
			// Fast expanding ring burst: point source, every direction at
			// once, high speed, short life, one burst. Zero gravity in
			// SettingsFor keeps the ring clean (no sag while it expands).
			case "shockwave_ring":
				em.Direction = new Vector2( 0, 1 );
				em.Speed = 620f; em.SpeedJitter = 0.15f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 0.4f; em.LifetimeMax = 0.7f; em.SizeMin = 3f; em.SizeMax = 6f;
				em.SizeEndScale = 0.4f;
				em.ColorStart = new Particle2DColor( 1, 0.85f, 0.5f, 0.9f ); em.ColorEnd = new Particle2DColor( 1, 0.4f, 0.1f, 0 );
				em.Bursts.Add( new Particle2DBurst( 0, 140 ) );
				break;
			// Slow rising soft cloud: box source, gentle upward launch,
			// long life, sizes that grow as the puff expands, low alpha.
			// The rise itself is the negative-Y gravity in SettingsFor.
			case "smoke_puff":
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 36, 12 );
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 24f; em.SpeedJitter = 0.6f; em.SpreadDegrees = 40f;
				em.LifetimeMin = 3f; em.LifetimeMax = 5f; em.SizeMin = 10f; em.SizeMax = 22f;
				em.SizeEndScale = 1.8f;
				em.ColorStart = new Particle2DColor( 0.55f, 0.55f, 0.58f, 0.35f ); em.ColorEnd = new Particle2DColor( 0.6f, 0.6f, 0.62f, 0 );
				em.FadeIn = 0.25f; em.Rate = 14f;
				break;
			// Slow wandering dots around an origin: box source, slow in
			// any direction, very long life, gentle turbulence in
			// SettingsFor. Palette of warm firefly hues fading to
			// transparent; no true blink (see the class note).
			case "fireflies":
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 60, 40 );
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 16f; em.SpeedJitter = 0.7f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 6f; em.LifetimeMax = 10f; em.SizeMin = 2f; em.SizeMax = 4f;
				em.SizeEndScale = 1f;
				em.Palette = new List<Particle2DColor>
				{
					new( 1, 0.9f, 0.4f, 1 ), new( 0.7f, 1, 0.4f, 1 ), new( 1, 1, 0.7f, 1 ),
				};
				em.EndAlpha = 0; em.FadeIn = 0.15f; em.Rate = 8f;
				break;
		}
		if ( direction is { } d && d.LengthSquared() > 1e-12f ) em.Direction = d;
		return em;
	}

	/// <summary>A copy of <paramref name="baseSettings"/> with this preset's
	/// medium applied. The base is never mutated. Gravity values here are
	/// absolute overrides, never added to the base gravity — see the class
	/// note. Portal callers must set the returned settings' VortexCenter
	/// to the portal origin: SettingsFor has no origin parameter, and the
	/// base centre (0,0) is almost never the portal's centre.</summary>
	public static Particle2DSettings SettingsFor( string name, Particle2DSettings baseSettings )
	{
		ArgumentNullException.ThrowIfNull( baseSettings );
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var s = Clone( baseSettings );
		switch ( key )
		{
			// Low gravity (well under the 980 default) so sparks arc
			// gently instead of plunging; light drag keeps them fast.
			case "sparkle_trails":
				s.Gravity = new Vector2( 0, 150f ); s.Drag = 0.3f;
				break;
			// No gravity (a portal hangs in place). The vortex radius
			// is wide (60) so the tangential acceleration stays fairly
			// flat across the disc instead of flinging centre particles
			// outward; drag 0.5 bleeds the launch velocity down.
			// VortexCenter is NOT set here — the caller sets it.
			case "portal":
				s.Gravity = new Vector2( 0, 0 ); s.Drag = 0.5f;
				s.VortexStrength = 60000f; s.VortexRadius = 60f;
				break;
			// Near-zero gravity (exactly zero: a halo neither falls nor
			// rises), drag toward still air, gentle turbulence drift.
			case "aura":
				s.Gravity = new Vector2( 0, 0 ); s.Drag = 1.2f;
				s.TurbulenceStrength = 12f; s.TurbulenceFrequency = 0.02f; s.TurbulenceSpeed = 0.3f;
				break;
			// Zero gravity and near-zero drag: the ring expands on its
			// launch velocity alone, with no sag and no stall.
			case "shockwave_ring":
				s.Gravity = new Vector2( 0, 0 ); s.Drag = 0.15f;
				break;
			// Negative-Y gravity in Y-down space = upward acceleration:
			// the puff rises. High drag caps the rise speed, turbulence
			// billows it, slight wind drifts it sideways.
			case "smoke_puff":
				s.Gravity = new Vector2( 0, -55f ); s.Drag = 2.0f;
				s.TurbulenceStrength = 18f; s.TurbulenceFrequency = 0.015f; s.TurbulenceSpeed = 0.4f;
				s.Wind = new Vector2( 6, 0 );
				break;
			// Zero gravity, drag toward still air, gentle turbulence for
			// the wander. No blink field exists in the sim — see the
			// class note — so nothing here pretends to be one.
			case "fireflies":
				s.Gravity = new Vector2( 0, 0 ); s.Drag = 0.9f;
				s.TurbulenceStrength = 26f; s.TurbulenceFrequency = 0.02f; s.TurbulenceSpeed = 0.6f;
				break;
		}
		return s;
	}

	/// <summary>Documented look colour for a preset: its start colour (for
	/// fireflies, the first palette entry). The 2D sim carries per-particle
	/// colour, so this matches what Create actually spawns — it is not a
	/// separate render-layer guess like the 3D packs' colour.</summary>
	public static Particle2DColor ColourFor( string name )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		return key switch
		{
			"sparkle_trails" => new Particle2DColor( 1, 0.95f, 0.7f, 1 ),
			"portal" => new Particle2DColor( 0.65f, 0.4f, 1, 0.9f ),
			"aura" => new Particle2DColor( 1, 0.9f, 0.7f, 0.25f ),
			"shockwave_ring" => new Particle2DColor( 1, 0.85f, 0.5f, 0.9f ),
			"smoke_puff" => new Particle2DColor( 0.55f, 0.55f, 0.58f, 0.35f ),
			"fireflies" => new Particle2DColor( 1, 0.9f, 0.4f, 1 ),
			_ => throw new ArgumentException( $"Unknown 2D particle preset '{name}'." ),
		};
	}

	/// <summary>One-line documented look for a preset, for authoring UIs and
	/// for the sweep reader checking a name against its intent. Limits of
	/// the sim are stated here, not hidden.</summary>
	public static string DescriptionFor( string name )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		return key switch
		{
			"sparkle_trails" => "Small fast points that fade and shrink over life; low gravity, size-over-life twinkle. No drawn trail: the 2D sim has no trail records.",
			"portal" => "Swirling disc around a centre point: vortex-leaning, long life. The sim's vortex is tangential-only, so particles orbit rather than spiralling inward; set VortexCenter to the portal origin.",
			"aura" => "Soft slow halo drifting around an origin: near-zero gravity, gentle turbulence, large low-alpha sizes.",
			"shockwave_ring" => "Fast expanding ring burst: outward velocities in every direction, zero gravity, short life.",
			"smoke_puff" => "Slow rising soft cloud: upward drift from negative-Y gravity in Y-down space, high drag, billowing turbulence.",
			"fireflies" => "Slow wandering dots with gentle turbulence and long life, fading in and out over life. No true blink: the sim's colour over life is one lerp, not an oscillating curve.",
			_ => throw new ArgumentException( $"Unknown 2D particle preset '{name}'." ),
		};
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
