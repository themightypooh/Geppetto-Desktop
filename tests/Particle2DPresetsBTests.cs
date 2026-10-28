using System.Numerics;
using Marionette.Desktop;

// Lane P15 (particles session, 2026-10-24): 2D preset pack B tests, same harness
// shape as Particle2DTests: registry.Add("particle-2d-presets-b", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   Particle2DPresetsBTests.Register( registry, settings );
// placed after the Particle2DTests.Register line.
// Motion tests use a single deterministic particle per preset (Rate 0, one burst,
// SpeedJitter 0, Spread 0) unless the motion itself needs the preset's spread —
// fireworks keeps Spread 180 because radial spread IS the motion under test.
// Settings/emitter values are asserted on the untouched preset first, before any
// test override, so an override can never mask a wrong preset value.
internal static class Particle2DPresetsBTests
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
		Particle2DSystem System( Particle2DSettings s ) => new( s );
		Particle2DDrawRecord[] Records( Particle2DSystem sys )
		{
			var arr = new Particle2DDrawRecord[sys.AliveCount];
			int n = sys.WriteDrawRecords( arr );
			Check( n == sys.AliveCount, $"WriteDrawRecords returned {n} but AliveCount is {sys.AliveCount}" );
			return arr;
		}
		// One deterministic particle: preset emitter, then rate off, one burst, no
		// jitter, no spread (fireworks keeps its spread — see its motion test).
		Particle2DSystem Single( string name, Vector2 origin, bool keepSpread = false )
		{
			var s = Particle2DPresetsB.SettingsFor( name, new Particle2DSettings { Seed = 7 } );
			var sys = System( s );
			var em = Particle2DPresetsB.Create( name, origin );
			em.Rate = 0;
			em.Bursts = new List<Particle2DBurst> { new( 0, 1 ) };
			em.SpeedJitter = 0;
			if ( !keepSpread ) em.SpreadDegrees = 0;
			sys.AddEmitter( em );
			return sys;
		}

		registry.Add( "particle-2d-presets-b", "names are eight and unique", () =>
		{
			Check( Particle2DPresetsB.Names.Length == 8, $"expected 8 pack-B names, got {Particle2DPresetsB.Names.Length}" );
			var seen = new HashSet<string>();
			foreach ( var n in Particle2DPresetsB.Names )
				Check( seen.Add( n ), $"duplicate pack-B name '{n}'" );
			foreach ( var n in new[] { "storm", "drizzle", "blizzard", "hail", "fog_drift", "pollen", "embers", "fireworks" } )
				Check( Particle2DPresetsB.ResolveName( n ) == n, $"'{n}' should resolve to itself" );
		} );

		registry.Add( "particle-2d-presets-b", "no name duplicates a lane-1 preset", () =>
		{
			foreach ( var b in Particle2DPresetsB.Names )
				foreach ( var lane1 in Particle2DPresets.Names )
					Check( b != lane1, $"pack-B name '{b}' duplicates lane-1 preset '{lane1}'" );
		} );

		registry.Add( "particle-2d-presets-b", "name resolution mirrors lane 1", () =>
		{
			Check( Particle2DPresetsB.ResolveName( "fx:storm" ) == "storm", "fx:storm should resolve to storm" );
			Check( Particle2DPresetsB.ResolveName( "VFX:Blizzard" ) == "blizzard", "VFX:Blizzard should resolve to blizzard" );
			Check( Particle2DPresetsB.ResolveName( "particle_fog_drift" ) == "fog_drift", "particle_fog_drift should resolve to fog_drift" );
			Check( Particle2DPresetsB.ResolveName( "heavy_fog_drift" ) == "fog_drift", "heavy_fog_drift should resolve to fog_drift" );
			Check( Particle2DPresetsB.ResolveName( "fireworks-show" ) == "fireworks", "fireworks-show should resolve to fireworks" );
			Check( Particle2DPresetsB.ResolveName( "my pollen drift" ) == "pollen", "'my pollen drift' should resolve to pollen" );
			Check( Particle2DPresetsB.ResolveName( "fx:embers_burst" ) == "embers", "fx:embers_burst should resolve to embers" );
			Check( Particle2DPresetsB.ResolveName( "fog" ) is null, "'fog' alone must not resolve to fog_drift" );
			Check( Particle2DPresetsB.ResolveName( "sandstorm" ) is null, "'sandstorm' must not resolve to storm (no underscore word boundary)" );
			Check( Particle2DPresetsB.ResolveName( "rain" ) is null, "'rain' is lane 1's name and must not resolve in pack B" );
			Check( Particle2DPresetsB.ResolveName( "camera_cut" ) is null, "camera_cut must not resolve to a preset" );
			Check( Particle2DPresetsB.ResolveName( "" ) is null, "empty name must not resolve" );
			Check( Particle2DPresetsB.ResolveName( "   " ) is null, "whitespace name must not resolve" );
		} );

		registry.Add( "particle-2d-presets-b", "unknown name throws", () =>
		{
			Throws( () => Particle2DPresetsB.Create( "nope", new Vector2( 0, 0 ) ), "Create with unknown name" );
			Throws( () => Particle2DPresetsB.SettingsFor( "nope", new Particle2DSettings { Seed = 7 } ), "SettingsFor with unknown name" );
		} );

		registry.Add( "particle-2d-presets-b", "settingsfor does not mutate the base", () =>
		{
			foreach ( var name in Particle2DPresetsB.Names )
			{
				var baseSettings = new Particle2DSettings { Seed = 7 };
				var s = Particle2DPresetsB.SettingsFor( name, baseSettings );
				Check( baseSettings.Gravity == new Vector2( 0, 980 ), $"SettingsFor({name}) mutated the base gravity" );
				Check( baseSettings.Drag == 0, $"SettingsFor({name}) mutated the base drag" );
				Check( baseSettings.Wind == Vector2.Zero, $"SettingsFor({name}) mutated the base wind" );
				Check( !ReferenceEquals( s, baseSettings ), $"SettingsFor({name}) must return a copy, not the base" );
			}
		} );

		registry.Add( "particle-2d-presets-b", "all presets create, validate, and step", () =>
		{
			foreach ( var name in Particle2DPresetsB.Names )
			{
				var baseSettings = new Particle2DSettings { Seed = 7 };
				var s = Particle2DPresetsB.SettingsFor( name, baseSettings );
				var sys = System( s );
				sys.AddEmitter( Particle2DPresetsB.Create( name, new Vector2( 100, 100 ) ) );
				sys.Step( 60 );
				Check( sys.AliveCount >= 0, $"preset {name} produced a negative alive count" );
			}
		} );

		registry.Add( "particle-2d-presets-b", "storm values: heavy slanted rain", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "storm", new Particle2DSettings { Seed = 7 } );
			Near( s.Gravity.Y, 1800, 0.001f, "storm gravity" ); Near( s.Wind.X, 180, 0.001f, "storm wind" ); Near( s.Drag, 0.1f, 0.001f, "storm drag" );
			var em = Particle2DPresetsB.Create( "storm", new Vector2( 0, 0 ) );
			Check( em.Shape == Particle2DEmitterShape.Box, "storm must be a Box top line" );
			Check( em.BoxHalfExtents == new Vector2( 400, 10 ), "storm box half extents" );
			Near( em.Speed, 1100, 0.001f, "storm speed" ); Near( em.Rate, 500, 0.001f, "storm rate (high)" );
			Near( em.SizeMin, 2, 0.001f, "storm size min" ); Near( em.SizeMax, 4, 0.001f, "storm size max" );
			Check( em.Direction.X > 0 && em.Direction.Y > 0, $"storm direction must slant down-right, got {em.Direction}" );
		} );

		registry.Add( "particle-2d-presets-b", "drizzle values: light fine rain", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "drizzle", new Particle2DSettings { Seed = 7 } );
			Near( s.Gravity.Y, 700, 0.001f, "drizzle gravity" ); Near( s.Wind.X, 30, 0.001f, "drizzle wind" ); Near( s.Drag, 0.2f, 0.001f, "drizzle drag" );
			var em = Particle2DPresetsB.Create( "drizzle", new Vector2( 0, 0 ) );
			Near( em.Speed, 350, 0.001f, "drizzle speed" ); Near( em.Rate, 80, 0.001f, "drizzle rate (low)" );
			Near( em.SizeMin, 1, 0.001f, "drizzle size min (small drops)" ); Near( em.SizeMax, 2, 0.001f, "drizzle size max" );
			Check( em.Rate < Particle2DPresetsB.Create( "storm", new Vector2( 0, 0 ) ).Rate, "drizzle rate must be below storm rate" );
			Check( em.SizeMax <= 2, "drizzle drops must stay fine" );
		} );

		registry.Add( "particle-2d-presets-b", "blizzard values: dense snow, strong wind, heavy turbulence", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "blizzard", new Particle2DSettings { Seed = 7 } );
			Near( s.Gravity.Y, 50, 0.001f, "blizzard gravity (light fall)" ); Near( s.Wind.X, 260, 0.001f, "blizzard wind (strong)" );
			Near( s.Drag, 1.0f, 0.001f, "blizzard drag" ); Near( s.TurbulenceStrength, 120, 0.001f, "blizzard turbulence (heavy)" );
			var em = Particle2DPresetsB.Create( "blizzard", new Vector2( 0, 0 ) );
			Near( em.Rate, 200, 0.001f, "blizzard rate (dense)" ); Near( em.Speed, 60, 0.001f, "blizzard speed" );
			Check( s.TurbulenceStrength > 30, "blizzard turbulence must exceed lane-1 snow's 30" );
		} );

		registry.Add( "particle-2d-presets-b", "hail values: fast, large, high restitution", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "hail", new Particle2DSettings { Seed = 7 } );
			Near( s.Gravity.Y, 1600, 0.001f, "hail gravity" ); Near( s.Drag, 0.05f, 0.001f, "hail drag" );
			Near( s.Restitution, 0.85f, 0.001f, "hail restitution (high bounce)" ); Near( s.Friction, 0.05f, 0.001f, "hail friction" );
			Check( !s.FloorEnabled, "hail must not force FloorEnabled — the floor line is scene-owned (see class header)" );
			var em = Particle2DPresetsB.Create( "hail", new Vector2( 0, 0 ) );
			Near( em.Speed, 700, 0.001f, "hail speed" ); Near( em.SizeMin, 6, 0.001f, "hail size min (larger than rain)" ); Near( em.SizeMax, 10, 0.001f, "hail size max" );
		} );

		registry.Add( "particle-2d-presets-b", "fog_drift values: slow, large, soft, long-lived", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "fog_drift", new Particle2DSettings { Seed = 7 } );
			Near( s.Gravity.Y, 5, 0.001f, "fog_drift gravity (near zero)" ); Near( s.Wind.X, 8, 0.001f, "fog_drift wind" ); Near( s.Drag, 0.8f, 0.001f, "fog_drift drag" );
			var em = Particle2DPresetsB.Create( "fog_drift", new Vector2( 0, 0 ) );
			Near( em.Speed, 6, 0.001f, "fog_drift speed (very slow)" ); Near( em.SizeMin, 30, 0.001f, "fog_drift size min (large)" );
			Near( em.LifetimeMin, 12, 0.001f, "fog_drift lifetime min (long)" ); Near( em.LifetimeMax, 20, 0.001f, "fog_drift lifetime max" );
			Check( em.ColorStart.A <= 0.15f, $"fog_drift must be soft (low alpha), got {em.ColorStart.A}" );
		} );

		registry.Add( "particle-2d-presets-b", "pollen values: slow motes, gentle turbulence, upward bias", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "pollen", new Particle2DSettings { Seed = 7 } );
			Near( s.Gravity.Y, -25, 0.001f, "pollen gravity (slight upward bias)" ); Near( s.Wind.X, 15, 0.001f, "pollen wind" );
			Near( s.TurbulenceStrength, 18, 0.001f, "pollen turbulence (gentle)" );
			var em = Particle2DPresetsB.Create( "pollen", new Vector2( 0, 0 ) );
			Near( em.Speed, 12, 0.001f, "pollen speed" ); Near( em.SizeMax, 3, 0.001f, "pollen size max (motes)" );
			Check( em.ColorStart.R >= 0.9f && em.ColorStart.G >= 0.9f && em.ColorStart.B < 0.8f, $"pollen colour must be pale yellow, got {em.ColorStart}" );
		} );

		registry.Add( "particle-2d-presets-b", "embers values: rising, warm, shrinking, flickering", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "embers", new Particle2DSettings { Seed = 7 } );
			Near( s.Gravity.Y, -120, 0.001f, "embers gravity (rising)" ); Near( s.TurbulenceStrength, 25, 0.001f, "embers turbulence" );
			var em = Particle2DPresetsB.Create( "embers", new Vector2( 0, 0 ) );
			Near( em.Rate, 60, 0.001f, "embers rate (continuous, not a burst)" ); Check( em.Bursts.Count == 0, "embers must have no bursts" );
			Near( em.SizeEndScale, 0.2f, 0.001f, "embers size end scale (shrinks over life)" );
			Near( em.FrameAdvance, 12, 0.001f, "embers frame advance (flicker)" );
			Check( em.Palette is { Count: 4 }, "embers needs a 4-entry warm palette" );
			foreach ( var c in em.Palette! )
				Check( c.R >= 0.9f && c.G <= 0.9f, $"embers palette entry must be warm (red-led), got {c}" );
		} );

		registry.Add( "particle-2d-presets-b", "fireworks values: burst, not a rate", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "fireworks", new Particle2DSettings { Seed = 7 } );
			Near( s.Gravity.Y, 900, 0.001f, "fireworks gravity (falloff)" ); Near( s.Drag, 0.8f, 0.001f, "fireworks drag" );
			var em = Particle2DPresetsB.Create( "fireworks", new Vector2( 0, 0 ) );
			Check( em.Rate == 0, $"fireworks must not be a rate, got Rate {em.Rate}" );
			Check( em.Bursts.Count == 1 && em.Bursts[0].Step == 0 && em.Bursts[0].Count == 150, $"fireworks must be one step-0 burst of 150, got {string.Join( ",", em.Bursts )}" );
			Near( em.SpreadDegrees, 180, 0.001f, "fireworks spread (radial)" ); Near( em.Speed, 500, 0.001f, "fireworks speed" );
			Check( em.LifetimeMax <= 1.4f, "fireworks lifetime must be short" );
			Check( em.Palette is { Count: 5 } && em.EndAlpha == 0, "fireworks needs a fading palette (EndAlpha 0)" );
		} );

		registry.Add( "particle-2d-presets-b", "motion: storm falls fast and slants", () =>
		{
			var sys = Single( "storm", new Vector2( 400, 100 ) );
			sys.Step( 30 );
			var st = sys.States[0];
			Check( st.Alive, "storm particle died unexpectedly" );
			Check( st.Velocity.Y > 800, $"storm must fall fast: velocity Y {st.Velocity.Y}" );
			Check( st.Velocity.X > 100, $"storm must slant right: velocity X {st.Velocity.X}" );
			Check( st.Position.Y > 100 + 200, $"storm must have dropped 200+ px in 0.5 s, Y is {st.Position.Y}" );
		} );

		registry.Add( "particle-2d-presets-b", "motion: drizzle falls gently", () =>
		{
			var sys = Single( "drizzle", new Vector2( 400, 100 ) );
			sys.Step( 30 );
			var st = sys.States[0];
			Check( st.Alive, "drizzle particle died unexpectedly" );
			Check( st.Velocity.Y > 400 && st.Velocity.Y < 900, $"drizzle must fall gently (400–900 px/s), velocity Y {st.Velocity.Y}" );
			Check( st.Velocity.X < 100, $"drizzle slant must stay gentle: velocity X {st.Velocity.X}" );
		} );

		registry.Add( "particle-2d-presets-b", "motion: blizzard drifts sideways", () =>
		{
			var sys = Single( "blizzard", new Vector2( 400, 200 ) );
			sys.Step( 1 );
			float x0 = sys.States[0].Position.X;
			sys.Step( 59 );
			var st = sys.States[0];
			Check( st.Alive, "blizzard particle died unexpectedly" );
			Check( st.Velocity.X > 50, $"blizzard must drift sideways: velocity X {st.Velocity.X}" );
			Check( st.Position.X - x0 > 40, $"blizzard must have drifted 40+ px sideways in 1 s, displacement {st.Position.X - x0}" );
		} );

		registry.Add( "particle-2d-presets-b", "motion: hail bounces on the floor", () =>
		{
			// Preset supplies the restitution; the scene supplies the floor line.
			// Gravity and drag are zeroed AFTER SettingsFor (which sets hail's 1600
			// and 0.05) and the shape is narrowed to Point, so the bounce math is
			// isolated exactly like lane 1's floor test; restitution itself is the
			// preset's value, and gravity/drag are asserted exactly in the values test.
			var s = Particle2DPresetsB.SettingsFor( "hail", new Particle2DSettings { Seed = 7, FloorEnabled = true, FloorY = 100 } );
			s.Gravity = Vector2.Zero; s.Drag = 0;
			var sys = System( s );
			var em = Particle2DPresetsB.Create( "hail", new Vector2( 50, 90 ) );
			em.Shape = Particle2DEmitterShape.Point;
			em.Rate = 0; em.Bursts = new List<Particle2DBurst> { new( 0, 1 ) };
			em.SpeedJitter = 0; em.SpreadDegrees = 0; em.Speed = 100; em.Direction = new Vector2( 0, 1 );
			sys.AddEmitter( em );
			float maxY = float.MinValue;
			for ( int i = 0; i < 7; i++ )
			{
				sys.Step();
				foreach ( var st in sys.States ) if ( st.Alive ) maxY = MathF.Max( maxY, st.Position.Y );
			}
			Check( maxY <= 100.0001f, $"hail passed the floor: max Y {maxY}" );
			var p = sys.States[0];
			Check( p.Alive, "hail particle died unexpectedly" );
			Near( p.Position.Y, 100, 0.01f, "hail should rest on the floor after the bounce step" );
			Near( p.Velocity.Y, -85, 0.01f, "hail velocity after a 0.85-restitution bounce off speed 100" );
		} );

		registry.Add( "particle-2d-presets-b", "motion: fog_drift barely moves over two seconds", () =>
		{
			var sys = Single( "fog_drift", new Vector2( 400, 300 ) );
			sys.Step( 1 );
			var start = sys.States[0].Position;
			sys.Step( 119 );
			var st = sys.States[0];
			Check( st.Alive, "fog_drift particle died unexpectedly (lifetime is 12–20 s)" );
			float moved = (st.Position - start).Length();
			Check( moved < 50, $"fog_drift must barely move in 2 s, moved {moved} px" );
		} );

		registry.Add( "particle-2d-presets-b", "motion: pollen drifts up, slowly", () =>
		{
			// Turbulence is zeroed for this motion check only (its exact preset value
			// is asserted in the pollen values test); the upward bias is what moves.
			var s = Particle2DPresetsB.SettingsFor( "pollen", new Particle2DSettings { Seed = 7 } );
			s.TurbulenceStrength = 0;
			var sys = System( s );
			var em = Particle2DPresetsB.Create( "pollen", new Vector2( 200, 200 ) );
			em.Rate = 0; em.Bursts = new List<Particle2DBurst> { new( 0, 1 ) };
			em.SpeedJitter = 0; em.SpreadDegrees = 0; em.Speed = 0;
			sys.AddEmitter( em );
			sys.Step( 1 );
			var start = sys.States[0].Position; // Box spawn randomizes the start; measure displacement, not absolute Y.
			sys.Step( 59 );
			var st = sys.States[0];
			Check( st.Alive, "pollen particle died unexpectedly" );
			Check( st.Position.Y < start.Y - 5, $"pollen must have risen 5+ px in 1 s, displacement Y is {st.Position.Y - start.Y}" );
			Check( MathF.Abs( st.Position.X - start.X ) < 60, $"pollen drift must stay slow sideways: X displacement {st.Position.X - start.X}" );
		} );

		registry.Add( "particle-2d-presets-b", "motion: embers rise and shrink", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "embers", new Particle2DSettings { Seed = 7 } );
			var sys = System( s );
			var em = Particle2DPresetsB.Create( "embers", new Vector2( 200, 300 ) );
			em.Rate = 0; em.Bursts = new List<Particle2DBurst> { new( 0, 1 ) };
			em.SpeedJitter = 0; em.SpreadDegrees = 0; em.Speed = 0;
			em.LifetimeMin = 1; em.LifetimeMax = 1; em.SizeMin = 4; em.SizeMax = 4;
			sys.AddEmitter( em );
			sys.Step( 1 );
			float early = Records( sys )[0].Size;
			sys.Step( 29 );
			var late = Records( sys )[0];
			Check( late.Position.Y < 300, $"embers must rise: Y is {late.Position.Y}" );
			Check( late.Size < early, $"embers size must shrink over life: early {early}, late {late.Size}" );
			Near( late.Size, 2.4f, 0.2f, "embers mid-life size (4 shrinking to 0.8)" );
			Check( late.Color.R > 0.9f, $"embers colour must stay warm-red, got {late.Color}" );
		} );

		registry.Add( "particle-2d-presets-b", "motion: fireworks burst radially, then fall, then die", () =>
		{
			var s = Particle2DPresetsB.SettingsFor( "fireworks", new Particle2DSettings { Seed = 7 } );
			var sys = System( s );
			var em = Particle2DPresetsB.Create( "fireworks", new Vector2( 200, 200 ) );
			em.SpeedJitter = 0; // spread 180 stays: radial spread IS the motion under test
			sys.AddEmitter( em );
			sys.Step( 1 );
			Check( sys.AliveCount == 150, $"fireworks burst must spawn exactly 150, got {sys.AliveCount}" );
			sys.Step( 9 );
			var early = Records( sys );
			float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, meanX = 0, meanY = 0;
			foreach ( var r in early )
			{
				minX = MathF.Min( minX, r.Position.X ); maxX = MathF.Max( maxX, r.Position.X );
				minY = MathF.Min( minY, r.Position.Y ); maxY = MathF.Max( maxY, r.Position.Y );
				meanX += r.Position.X; meanY += r.Position.Y;
			}
			meanX /= early.Length; meanY /= early.Length;
			Check( minX < 200 && maxX > 200 && minY < 200 && maxY > 200, $"fireworks burst must be radial (all quadrants): X [{minX},{maxX}] Y [{minY},{maxY}]" );
			float meanDist = 0;
			foreach ( var r in early ) meanDist += (r.Position - new Vector2( 200, 200 )).Length();
			meanDist /= early.Length;
			Check( meanDist > 20, $"fireworks burst must have spread: mean distance {meanDist}" );
			sys.Step( 50 );
			var late = Records( sys );
			float lateMeanY = 0;
			foreach ( var r in late ) lateMeanY += r.Position.Y;
			lateMeanY /= late.Length;
			Check( lateMeanY > meanY + 50, $"fireworks must fall back down under gravity: mean Y early {meanY}, late {lateMeanY}" );
			sys.Step( 30 );
			Check( sys.AliveCount == 0, $"fireworks must be dead after 1.5 s (max life 1.4 s), got {sys.AliveCount} alive" );
		} );
	}
}
