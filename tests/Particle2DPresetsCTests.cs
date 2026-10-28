using System.Numerics;
using Marionette.Desktop;

// Wave-6 lane P73 tests. Group: particle-2d-presets-c.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   Particle2DPresetsCTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line (and after the
// Particle2DTests.Register line where ordering matters).

internal static class Particle2DPresetsCTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		Particle2DSystem SystemFor( string preset, Vector2 origin, uint seed = 7, int capacity = 4000 )
		{
			var settings = Particle2DPresetsC.SettingsFor( preset, new Particle2DSettings { Seed = seed, Capacity = capacity } );
			if ( preset == "portal" ) settings.VortexCenter = origin;
			var sys = new Particle2DSystem( settings );
			sys.AddEmitter( Particle2DPresetsC.Create( preset, origin ) );
			return sys;
		}

		(float meanX, float meanY, float meanR, int n) Mean( Particle2DSystem sys, Vector2 origin )
		{
			float sx = 0, sy = 0, sr = 0; int n = 0;
			for ( int i = 0; i < sys.States.Length; i++ )
			{
				if ( !sys.IsAlive( i ) ) continue;
				var p = sys.States[i].Position;
				sx += p.X; sy += p.Y; sr += (p - origin).Length(); n++;
			}
			Check( n > 0, "expected live particles for the mean" );
			return (sx / n, sy / n, sr / n, n);
		}

		Particle2DDrawRecord[] Records( Particle2DSystem sys )
		{
			var arr = new Particle2DDrawRecord[sys.AliveCount];
			int n = sys.WriteDrawRecords( arr );
			Check( n == sys.AliveCount, $"WriteDrawRecords returned {n} but AliveCount is {sys.AliveCount}" );
			return arr;
		}

		registry.Add( "particle-2d-presets-c", "all six names resolve exactly", () =>
		{
			foreach ( var name in Particle2DPresetsC.Names )
				Check( Particle2DPresetsC.ResolveName( name ) == name, $"exact '{name}' did not resolve to itself" );
			Check( Particle2DPresetsC.Names.Length == 6, $"expected 6 pack-C names, got {Particle2DPresetsC.Names.Length}" );
		} );

		registry.Add( "particle-2d-presets-c", "prefixed, case-insensitive, whole-word names resolve", () =>
		{
			Check( Particle2DPresetsC.ResolveName( "FX:Portal" ) == "portal", "fx: + case failed for portal" );
			Check( Particle2DPresetsC.ResolveName( "vfx_sparkle_trails" ) == "sparkle_trails", "vfx_ prefix failed for sparkle_trails" );
			Check( Particle2DPresetsC.ResolveName( "particle:SHOCKWAVE_RING" ) == "shockwave_ring", "particle: + case failed for shockwave_ring" );
			Check( Particle2DPresetsC.ResolveName( "soft_aura_halo" ) == "aura", "whole-word middle failed for aura" );
			Check( Particle2DPresetsC.ResolveName( "my_smoke_puff" ) == "smoke_puff", "whole-word suffix failed for smoke_puff" );
			Check( Particle2DPresetsC.ResolveName( "fireflies_at_dusk" ) == "fireflies", "whole-word prefix failed for fireflies" );
			Check( Particle2DPresetsC.ResolveName( "deep_portal" ) == "portal", "whole-word suffix failed for portal" );
			Check( Particle2DPresetsC.ResolveName( "Sparkle-Trails" ) == "sparkle_trails", "dash + case failed for sparkle_trails" );
		} );

		registry.Add( "particle-2d-presets-c", "unknown and empty names resolve null and throw", () =>
		{
			Check( Particle2DPresetsC.ResolveName( "nope" ) is null, "unknown name resolved" );
			Check( Particle2DPresetsC.ResolveName( "" ) is null, "empty name resolved" );
			Check( Particle2DPresetsC.ResolveName( "   " ) is null, "whitespace name resolved" );
			Check( Particle2DPresetsC.ResolveName( "spark" ) is null, "partial word 'spark' resolved" );
			var origin = new Vector2( 100, 100 );
			var baseSettings = new Particle2DSettings();
			Throws( () => Particle2DPresetsC.Create( "nope", origin ), "Create(unknown)" );
			Throws( () => Particle2DPresetsC.Create( "", origin ), "Create(empty)" );
			Throws( () => Particle2DPresetsC.SettingsFor( "nope", baseSettings ), "SettingsFor(unknown)" );
			Throws( () => Particle2DPresetsC.ColourFor( "nope" ), "ColourFor(unknown)" );
			Throws( () => Particle2DPresetsC.DescriptionFor( "nope" ), "DescriptionFor(unknown)" );
		} );

		registry.Add( "particle-2d-presets-c", "no-shadow rule: pack C never claims a pre-existing name", () =>
		{
			// Pack C's own resolver: every pre-existing name resolves to null here.
			foreach ( var old in new[] { "sparks", "rain", "snow", "bubbles", "confetti", "magic", "dust", "splash",
				"storm", "drizzle", "blizzard", "hail", "fog_drift", "pollen", "embers", "fireworks",
				"water_splash", "blood_mist", "dust_motes", "breath_frost", "fire", "smoke", "leaves", "steam", "magic_sparkle" } )
				Check( Particle2DPresetsC.ResolveName( old ) is null, $"pack C shadows pre-existing '{old}'" );
			// Lane 1's resolver, the other direction: no pack-C name resolves as a lane-1 preset.
			foreach ( var name in Particle2DPresetsC.Names )
				Check( Particle2DPresets.ResolveName( name ) is null, $"lane 1 claims pack-C name '{name}'" );
			// The closest pair, both directions: sparkle_trails is NOT sparks, and vice versa.
			Check( Particle2DPresetsC.ResolveName( "sparkle_trails" ) == "sparkle_trails", "sparkle_trails did not resolve to itself" );
			Check( Particle2DPresets.ResolveName( "sparks" ) == "sparks", "sparks did not resolve to itself" );
			Check( Particle2DPresets.ResolveName( "sparkle_trails" ) is null, "lane 1 resolves sparkle_trails as sparks" );
			Check( Particle2DPresetsC.ResolveName( "sparks" ) is null, "pack C resolves sparks" );
			Check( Particle2DPresetsC.ResolveName( "magic" ) is null, "pack C resolves magic" );
		} );

		registry.Add( "particle-2d-presets-c", "sparkle_trails emitter numbers", () =>
		{
			var em = Particle2DPresetsC.Create( "sparkle_trails", new Vector2( 100, 100 ) );
			Check( em.Shape == Particle2DEmitterShape.Point, "sparkle_trails is not a point emitter" );
			Near( em.Speed, 320f, 0.01f, "sparkle_trails speed" );
			Check( em.LifetimeMin == 0.5f && em.LifetimeMax == 1.1f, "sparkle_trails life range wrong" );
			Check( em.SizeMin == 1f && em.SizeMax == 3f, "sparkle_trails size range wrong" );
			Near( em.SizeEndScale, 0.15f, 0.001f, "sparkle_trails size-over-life shrink" );
			Check( em.Bursts.Count == 1 && em.Bursts[0].Count == 60, "sparkle_trails burst wrong" );
		} );

		registry.Add( "particle-2d-presets-c", "portal emitter numbers", () =>
		{
			var em = Particle2DPresetsC.Create( "portal", new Vector2( 400, 300 ) );
			Check( em.Shape == Particle2DEmitterShape.Circle, "portal is not a circle emitter" );
			Near( em.CircleRadius, 70f, 0.01f, "portal circle radius" );
			Near( em.Speed, 30f, 0.01f, "portal speed" );
			Check( em.LifetimeMin == 4f && em.LifetimeMax == 6f, "portal life range wrong" );
			Check( em.Rate == 40f, "portal rate wrong" );
		} );

		registry.Add( "particle-2d-presets-c", "aura emitter numbers", () =>
		{
			var em = Particle2DPresetsC.Create( "aura", new Vector2( 400, 300 ) );
			Check( em.Shape == Particle2DEmitterShape.Point, "aura is not a point emitter" );
			Near( em.Speed, 12f, 0.01f, "aura speed" );
			Check( em.LifetimeMin == 3f && em.LifetimeMax == 5f, "aura life range wrong" );
			Check( em.SizeMin == 14f && em.SizeMax == 26f, "aura size range wrong" );
			Check( em.Rate == 20f, "aura rate wrong" );
		} );

		registry.Add( "particle-2d-presets-c", "shockwave_ring emitter numbers", () =>
		{
			var em = Particle2DPresetsC.Create( "shockwave_ring", new Vector2( 400, 300 ) );
			Check( em.Shape == Particle2DEmitterShape.Point, "shockwave_ring is not a point emitter" );
			Near( em.Speed, 620f, 0.01f, "shockwave_ring speed" );
			Near( em.SpreadDegrees, 180f, 0.01f, "shockwave_ring spread" );
			Check( em.LifetimeMin == 0.4f && em.LifetimeMax == 0.7f, "shockwave_ring life range wrong" );
			Check( em.Bursts.Count == 1 && em.Bursts[0].Count == 140, "shockwave_ring burst wrong" );
		} );

		registry.Add( "particle-2d-presets-c", "smoke_puff emitter numbers", () =>
		{
			var em = Particle2DPresetsC.Create( "smoke_puff", new Vector2( 400, 300 ) );
			Check( em.Shape == Particle2DEmitterShape.Box, "smoke_puff is not a box emitter" );
			Check( em.Direction == new Vector2( 0, -1 ), "smoke_puff launch is not upward" );
			Check( em.LifetimeMin == 3f && em.LifetimeMax == 5f, "smoke_puff life range wrong" );
			Check( em.SizeMin == 10f && em.SizeMax == 22f, "smoke_puff size range wrong" );
			Near( em.SizeEndScale, 1.8f, 0.001f, "smoke_puff growth" );
		} );

		registry.Add( "particle-2d-presets-c", "fireflies emitter numbers", () =>
		{
			var em = Particle2DPresetsC.Create( "fireflies", new Vector2( 400, 300 ) );
			Check( em.Shape == Particle2DEmitterShape.Box, "fireflies is not a box emitter" );
			Near( em.Speed, 16f, 0.01f, "fireflies speed" );
			Check( em.LifetimeMin == 6f && em.LifetimeMax == 10f, "fireflies life range wrong" );
			Check( em.Palette is { Count: 3 }, "fireflies palette wrong" );
			Check( em.Rate == 8f, "fireflies rate wrong" );
		} );

		registry.Add( "particle-2d-presets-c", "settings bundles: gravity signs, drag, turbulence, vortex", () =>
		{
			var baseSettings = new Particle2DSettings { Gravity = new Vector2( 0, 980f ), Drag = 0.5f };
			var sparkle = Particle2DPresetsC.SettingsFor( "sparkle_trails", baseSettings );
			Check( sparkle.Gravity == new Vector2( 0, 150f ), $"sparkle gravity {sparkle.Gravity} (low, positive-Y fall)" );
			Near( sparkle.Drag, 0.3f, 0.001f, "sparkle drag" );
			var portal = Particle2DPresetsC.SettingsFor( "portal", baseSettings );
			Check( portal.Gravity == Vector2.Zero, "portal gravity is not zero" );
			Check( portal.VortexStrength == 60000f && portal.VortexRadius == 60f, "portal vortex wrong" );
			Near( portal.Drag, 0.5f, 0.001f, "portal drag" );
			var aura = Particle2DPresetsC.SettingsFor( "aura", baseSettings );
			Check( aura.Gravity == Vector2.Zero, "aura gravity is not near-zero" );
			Near( aura.Drag, 1.2f, 0.001f, "aura drag" );
			Check( aura.TurbulenceStrength == 12f, "aura turbulence wrong" );
			var ring = Particle2DPresetsC.SettingsFor( "shockwave_ring", baseSettings );
			Check( ring.Gravity == Vector2.Zero, "shockwave gravity is not zero" );
			Near( ring.Drag, 0.15f, 0.001f, "shockwave drag" );
			var smoke = Particle2DPresetsC.SettingsFor( "smoke_puff", baseSettings );
			Check( smoke.Gravity == new Vector2( 0, -55f ), $"smoke gravity {smoke.Gravity} (must be negative Y: rises in Y-down space)" );
			Near( smoke.Drag, 2.0f, 0.001f, "smoke drag (high)" );
			var flies = Particle2DPresetsC.SettingsFor( "fireflies", baseSettings );
			Check( flies.Gravity == Vector2.Zero, "fireflies gravity is not zero" );
			Check( flies.TurbulenceStrength == 26f, "fireflies turbulence wrong" );
			// Base never mutated, and the medium overrides rather than adds.
			Check( baseSettings.Gravity == new Vector2( 0, 980f ) && baseSettings.Drag == 0.5f, "SettingsFor mutated the base settings" );
		} );

		registry.Add( "particle-2d-presets-c", "colour and description for every preset", () =>
		{
			foreach ( var name in Particle2DPresetsC.Names )
			{
				var c = Particle2DPresetsC.ColourFor( name );
				Check( c.ChannelsValid(), $"{name} colour channels out of range" );
				var d = Particle2DPresetsC.DescriptionFor( name );
				Check( d.Length > 20, $"{name} description too short" );
			}
			Check( Particle2DPresetsC.DescriptionFor( "fireflies" ).Contains( "blink", StringComparison.OrdinalIgnoreCase ),
				"fireflies description must name the missing blink feature" );
			Check( Particle2DPresetsC.DescriptionFor( "portal" ).Contains( "VortexCenter", StringComparison.OrdinalIgnoreCase ),
				"portal description must name the VortexCenter caller step" );
			Check( Particle2DPresetsC.DescriptionFor( "sparkle_trails" ).Contains( "trail", StringComparison.OrdinalIgnoreCase ),
				"sparkle_trails description must name the trail limit" );
		} );

		registry.Add( "particle-2d-presets-c", "stepped sim: smoke rises (mean Y decreases in Y-down space)", () =>
		{
			var origin = new Vector2( 400, 300 );
			var sys = SystemFor( "smoke_puff", origin );
			// Rate is 14/s, so the first step spawns nothing (14/60 < 1):
			// compare a 1 s-old population against the 5 s steady state.
			sys.Step( 60 );
			var early = Mean( sys, origin );
			sys.Step( 240 );
			var late = Mean( sys, origin );
			Check( late.meanY < early.meanY - 10f, $"smoke did not rise: mean Y {early.meanY} -> {late.meanY} (Y-down: rise = smaller Y)" );
		} );

		registry.Add( "particle-2d-presets-c", "stepped sim: shockwave ring mean radius grows", () =>
		{
			var origin = new Vector2( 400, 300 );
			var sys = SystemFor( "shockwave_ring", origin );
			sys.Step( 5 );
			var early = Mean( sys, origin );
			sys.Step( 25 );
			var late = Mean( sys, origin );
			Check( late.meanR > early.meanR + 50f, $"ring did not expand: mean radius {early.meanR} -> {late.meanR}" );
			Check( late.meanR > 150f, $"ring radius at 0.5 s too small: {late.meanR}" );
		} );

		registry.Add( "particle-2d-presets-c", "stepped sim: fireflies stay near origin over their life", () =>
		{
			var origin = new Vector2( 400, 300 );
			var sys = SystemFor( "fireflies", origin );
			sys.Step( 240 );
			var m = Mean( sys, origin );
			Check( m.meanR < 250f, $"fireflies wandered too far: mean distance from origin {m.meanR} after 4 s" );
			Check( sys.AliveCount > 0, "fireflies all dead after 4 s (life is 6-10 s)" );
		} );

		registry.Add( "particle-2d-presets-c", "stepped sim: portal swirls around its centre", () =>
		{
			var origin = new Vector2( 400, 300 );
			var sys = SystemFor( "portal", origin );
			sys.Step( 30 );
			int slot = -1;
			for ( int i = 0; i < sys.States.Length; i++ ) if ( sys.IsAlive( i ) ) { slot = i; break; }
			Check( slot >= 0, "no portal particle alive at step 30" );
			float AngleAt( int s ) => MathF.Atan2( sys.States[s].Position.Y - origin.Y, sys.States[s].Position.X - origin.X );
			float a0 = AngleAt( slot );
			sys.Step( 90 );
			Check( sys.IsAlive( slot ), "tracked portal particle died before step 120 (life is 4-6 s)" );
			float a1 = AngleAt( slot );
			float d = MathF.Abs( a1 - a0 );
			if ( d > MathF.PI ) d = 2 * MathF.PI - d;
			Check( d > 0.3f, $"portal particle did not swirl: angle moved only {d} rad over 1.5 s" );
			var m = Mean( sys, origin );
			Check( m.meanR < 300f, $"portal cloud escaped: mean radius {m.meanR}" );
		} );

		registry.Add( "particle-2d-presets-c", "stepped sim: aura stays near origin", () =>
		{
			var origin = new Vector2( 400, 300 );
			var sys = SystemFor( "aura", origin );
			sys.Step( 120 );
			var m = Mean( sys, origin );
			Check( m.meanR < 150f, $"aura drifted too far: mean distance from origin {m.meanR} after 2 s" );
		} );

		registry.Add( "particle-2d-presets-c", "same seed gives identical fireflies draw records", () =>
		{
			Particle2DDrawRecord[] Build()
			{
				var sys = SystemFor( "fireflies", new Vector2( 400, 300 ), seed: 7 );
				sys.Step( 120 );
				return Records( sys );
			}
			var a = Build();
			var b = Build();
			Check( a.Length == b.Length && a.Length > 0, $"expected non-empty identical record sets, got {a.Length} vs {b.Length}" );
			for ( int i = 0; i < a.Length; i++ )
			{
				Check( a[i].Position == b[i].Position, $"record {i} position differs" );
				Check( a[i].Size == b[i].Size, $"record {i} size differs" );
				Check( a[i].Color.R == b[i].Color.R && a[i].Color.G == b[i].Color.G && a[i].Color.B == b[i].Color.B && a[i].Color.A == b[i].Color.A, $"record {i} color differs" );
			}
		} );
	}
}
