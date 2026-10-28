using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticlePresetPackCTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			throw new Exception( $"{what}: expected ArgumentException, none thrown" );
		}

		ParticleSystemSettings Base( uint seed = 7 ) => new() { Seed = seed, Capacity = 4000 };
		ParticleSystem Build( string preset, Vector3 origin, uint seed = 7 )
		{
			var sys = new ParticleSystem( ParticlePresetPackC.SettingsFor( preset, Base( seed ) ) );
			sys.AddEmitter( ParticlePresetPackC.Create( preset, origin ) );
			return sys;
		}
		// Mean position / mean |offset| over live particles; empty => NaN, callers check AliveCount first.
		(Vector3 mean, float meanDist) Mean( ParticleSystem sys, Vector3 origin )
		{
			Vector3 sum = default; float dsum = 0; int n = 0;
			foreach ( var st in sys.States )
				if ( st.Alive != 0 ) { sum += st.Position; dsum += (st.Position - origin).Length(); n++; }
			Check( n > 0, "mean over zero live particles" );
			return (sum / n, dsum / n);
		}

		registry.Add( "particle-preset-pack-c", "eight names, distinct within the pack", () =>
		{
			Check( ParticlePresetPackC.Names.Length == 8, $"expected 8 pack-C names, got {ParticlePresetPackC.Names.Length}" );
			Check( new HashSet<string>( ParticlePresetPackC.Names ).Count == 8, "pack-C names contain a duplicate" );
			foreach ( var n in ParticlePresetPackC.Names )
				Check( ParticlePresetPackC.ResolveName( n ) == n, $"exact name '{n}' did not resolve to itself" );
		} );

		registry.Add( "particle-preset-pack-c", "no duplicate against the 14 built-in presets", () =>
		{
			Check( ParticleEmitterPresets.Names.Length == 14, $"expected 14 built-in presets, got {ParticleEmitterPresets.Names.Length} — tree moved, re-check" );
			var builtIn = new HashSet<string>( ParticleEmitterPresets.Names );
			foreach ( var n in ParticlePresetPackC.Names )
				Check( !builtIn.Contains( n ), $"pack-C name '{n}' duplicates a built-in preset" );
			// Pack B (lane P25) had not landed in the canonical tree at check time,
			// so this test can only prove non-duplication against in-tree names;
			// the coordinator sweep must re-check against pack B when it lands.
		} );

		registry.Add( "particle-preset-pack-c", "name resolution matches built-in rules", () =>
		{
			Check( ParticlePresetPackC.ResolveName( "fx:ink" ) == "ink", "fx: prefix failed" );
			Check( ParticlePresetPackC.ResolveName( "vfx_caustic_motes" ) == "caustic_motes", "vfx_ prefix failed" );
			Check( ParticlePresetPackC.ResolveName( "particle:oil" ) == "oil", "particle: prefix failed" );
			Check( ParticlePresetPackC.ResolveName( "LAVA" ) == "lava", "case-insensitivity failed" );
			Check( ParticlePresetPackC.ResolveName( "deep_ink_cloud" ) == "ink", "whole-word ink failed" );
			Check( ParticlePresetPackC.ResolveName( "underwater-frost-breath" ) == "frost_breath", "dash + whole-word frost_breath failed" );
			Check( ParticlePresetPackC.ResolveName( "nope" ) is null, "unknown name resolved" );
			Check( ParticlePresetPackC.ResolveName( "" ) is null, "empty name resolved" );
			Check( ParticlePresetPackC.ResolveName( "   " ) is null, "whitespace name resolved" );
			Check( ParticlePresetPackC.ResolveName( "fire" ) is null, "built-in name 'fire' must not resolve in pack C" );
		} );

		registry.Add( "particle-preset-pack-c", "frost_breath does not collide with breath_frost", () =>
		{
			Check( ParticlePresetPackC.ResolveName( "frost_breath" ) == "frost_breath", "frost_breath must resolve in pack C" );
			Check( ParticlePresetPackC.ResolveName( "breath_frost" ) is null, "built-in breath_frost must NOT resolve in pack C" );
			Check( ParticleEmitterPresets.ResolveName( "frost_breath" ) is null, "pack-C frost_breath must NOT resolve as a built-in preset" );
			Check( ParticleEmitterPresets.ResolveName( "breath_frost" ) == "breath_frost", "built-in breath_frost must still resolve as itself" );
			// So no rename was needed: the two names are distinct word sequences.
		} );

		registry.Add( "particle-preset-pack-c", "unknown names throw in Create, SettingsFor, ColourFor, DescriptionFor", () =>
		{
			Throws( () => ParticlePresetPackC.Create( "nope", default ), "Create unknown" );
			Throws( () => ParticlePresetPackC.SettingsFor( "nope", Base() ), "SettingsFor unknown" );
			Throws( () => ParticlePresetPackC.ColourFor( "nope" ), "ColourFor unknown" );
			Throws( () => ParticlePresetPackC.DescriptionFor( "nope" ), "DescriptionFor unknown" );
			Throws( () => ParticlePresetPackC.SettingsFor( "nope", null! ), "SettingsFor null base must throw" );
		} );

		registry.Add( "particle-preset-pack-c", "create preserves origin and direction override re-aims", () =>
		{
			var origin = new Vector3( 10, 20, 30 );
			var em = ParticlePresetPackC.Create( "lava", origin );
			Check( em.Position == origin, $"origin lost: {em.Position}" );
			var reaimed = ParticlePresetPackC.Create( "frost_breath", origin, new Vector3( 0, 0, 1 ) );
			Check( reaimed.Direction == new Vector3( 0, 0, 1 ), $"direction override lost: {reaimed.Direction}" );
			var kept = ParticlePresetPackC.Create( "frost_breath", origin );
			Check( kept.Direction == new Vector3( 1, 0, 0 ), $"frost_breath own direction must be +X, got {kept.Direction}" );
		} );

		registry.Add( "particle-preset-pack-c", "emitter shapes and launch numbers per preset", () =>
		{
			var o = new Vector3( 0, 0, 0 );
			var ink = ParticlePresetPackC.Create( "ink", o );
			Check( ink.Shape == ParticleEmitterShape.Point && ink.Bursts.Count == 1 && ink.Bursts[0].Count == 80, "ink must be a point burst of 80" );
			Near( ink.Speed, 25f, 1e-6f, "ink speed" ); Near( ink.LifetimeMin, 2f, 1e-6f, "ink life min" ); Near( ink.SizeMax, 6f, 1e-6f, "ink size max" );
			var plankton = ParticlePresetPackC.Create( "plankton", o );
			Check( plankton.Shape == ParticleEmitterShape.Box, "plankton must be a box" );
			Near( plankton.Rate, 20f, 1e-6f, "plankton rate" ); Near( plankton.LifetimeMax, 15f, 1e-6f, "plankton life max" );
			var sand = ParticlePresetPackC.Create( "sand", o );
			Check( sand.Direction == new Vector3( 0, 0, -1 ), "sand must launch downward" );
			Near( sand.Speed, 80f, 1e-6f, "sand speed" );
			var silt = ParticlePresetPackC.Create( "silt", o );
			Check( silt.Shape == ParticleEmitterShape.Box && silt.Rate == 30f, "silt must be a box rate emitter" );
			var motes = ParticlePresetPackC.Create( "caustic_motes", o );
			Check( motes.Shape == ParticleEmitterShape.Sphere, "caustic_motes must be a sphere" );
			Near( motes.SizeMax, 0.6f, 1e-6f, "caustic_motes size max" );
			var oil = ParticlePresetPackC.Create( "oil", o );
			Near( oil.SizeMin, 3f, 1e-6f, "oil size min (large blobs)" ); Near( oil.Rate, 10f, 1e-6f, "oil rate" );
			var lava = ParticlePresetPackC.Create( "lava", o );
			Near( lava.Rate, 90f, 1e-6f, "lava burst-ish rate" ); Near( lava.Speed, 120f, 1e-6f, "lava speed" );
			var frost = ParticlePresetPackC.Create( "frost_breath", o );
			Check( frost.Bursts.Count == 1 && frost.Bursts[0].Count == 40, "frost_breath must be a cone burst of 40" );
			Near( frost.LifetimeMax, 1.2f, 1e-6f, "frost_breath life max (fast fade)" );
		} );

		registry.Add( "particle-preset-pack-c", "medium values per preset (exact settings table)", () =>
		{
			void Grav( string name, float z ) { Near( ParticlePresetPackC.SettingsFor( name, Base() ).Gravity.Z, z, 1e-6f, name + " gravity Z" ); }
			Grav( "ink", -15f ); Grav( "plankton", -2f ); Grav( "sand", -500f ); Grav( "silt", -20f );
			Grav( "caustic_motes", -5f ); Grav( "oil", 12f ); Grav( "lava", 150f );
			void Drag( string name, float d ) { Near( ParticlePresetPackC.SettingsFor( name, Base() ).Drag, d, 1e-6f, name + " drag" ); }
			Drag( "ink", 3f ); Drag( "plankton", 1f ); Drag( "sand", 0.05f ); Drag( "silt", 2.5f );
			Drag( "caustic_motes", 1.2f ); Drag( "oil", 3f ); Drag( "lava", 0.8f ); Drag( "frost_breath", 1.8f );
			void Turb( string name, float s ) { Near( ParticlePresetPackC.SettingsFor( name, Base() ).TurbulenceStrength, s, 1e-6f, name + " turbulence" ); }
			Turb( "ink", 25f ); Turb( "plankton", 5f ); Turb( "silt", 6f ); Turb( "caustic_motes", 12f );
			Turb( "oil", 4f ); Turb( "lava", 50f ); Turb( "frost_breath", 12f );
			var plankton = ParticlePresetPackC.SettingsFor( "plankton", Base() );
			Check( plankton.Wind == new Vector3( 3f, 1f, 0f ), $"plankton current must be (3,1,0), got {plankton.Wind}" );
			var sand = ParticlePresetPackC.SettingsFor( "sand", Base() );
			Check( sand.GroundEnabled, "sand must enable the ground plane" );
			Near( sand.Restitution, 0.15f, 1e-6f, "sand restitution (low bounce)" );
			Near( sand.Friction, 0.5f, 1e-6f, "sand friction" );
			// frost_breath deliberately leaves gravity alone (air breath, like breath_frost).
			var fb = ParticlePresetPackC.SettingsFor( "frost_breath", Base() );
			Check( fb.Gravity == Base().Gravity, $"frost_breath must inherit base gravity, got {fb.Gravity}" );
		} );

		registry.Add( "particle-preset-pack-c", "medium overrides, never adds to, scene gravity", () =>
		{
			var hostile = new ParticleSystemSettings { Seed = 7, Capacity = 1000, Gravity = new Vector3( 0, 0, -999f ) };
			Near( ParticlePresetPackC.SettingsFor( "ink", hostile ).Gravity.Z, -15f, 1e-6f, "ink over hostile base" );
			Near( ParticlePresetPackC.SettingsFor( "lava", hostile ).Gravity.Z, 150f, 1e-6f, "lava over hostile base" );
			Near( ParticlePresetPackC.SettingsFor( "oil", hostile ).Gravity.Z, 12f, 1e-6f, "oil over hostile base" );
			Near( hostile.Gravity.Z, -999f, 1e-6f, "base must not be mutated" );
			var derived = ParticlePresetPackC.SettingsFor( "ink", hostile );
			Check( !ReferenceEquals( derived, hostile ), "SettingsFor must return a copy, not the base" );
		} );

		registry.Add( "particle-preset-pack-c", "documented colours per preset", () =>
		{
			void Colour( string name, float r, float g, float b, float a )
			{
				var c = ParticlePresetPackC.ColourFor( name );
				Near( c.R, r, 1e-6f, name + " R" ); Near( c.G, g, 1e-6f, name + " G" );
				Near( c.B, b, 1e-6f, name + " B" ); Near( c.A, a, 1e-6f, name + " A" );
			}
			Colour( "ink", 0.05f, 0.05f, 0.12f, 0.90f );
			Colour( "caustic_motes", 0.90f, 1.00f, 0.95f, 0.80f );
			Colour( "oil", 0.25f, 0.15f, 0.05f, 0.85f );
			Colour( "lava", 1.00f, 0.45f, 0.10f, 1.00f );
			Colour( "frost_breath", 0.85f, 0.95f, 1.00f, 0.70f );
			Check( ParticlePresetPackC.ColourFor( "caustic_motes" ).R > ParticlePresetPackC.ColourFor( "ink" ).R,
				"caustic motes must be brighter than ink" );
			foreach ( var n in ParticlePresetPackC.Names )
				Check( ParticlePresetPackC.DescriptionFor( n ).Length > 20, $"description missing for {n}" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: ink expands slowly and outlives 1s", () =>
		{
			var origin = new Vector3( 0, 0, 50 );
			var sys = Build( "ink", origin );
			sys.Step( 60 ); // 1 s
			Check( sys.AliveCount == 80, $"ink burst must still be fully alive at 1 s (life min 2 s), got {sys.AliveCount}" );
			var m = Mean( sys, origin );
			Check( m.meanDist > 0.5f && m.meanDist < 60f, $"ink mean expansion at 1 s must be slow (0.5..60 in), got {m.meanDist}" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: plankton drifts with the current, barely falls", () =>
		{
			var origin = new Vector3( 0, 0, 50 );
			var sys = Build( "plankton", origin );
			sys.Step( 300 ); // 5 s
			Check( sys.AliveCount > 50, $"plankton must still be alive at 5 s (life min 8 s), got {sys.AliveCount}" );
			var m = Mean( sys, origin );
			Check( m.mean.X > 1f, $"plankton mean X must drift with the +X current, got {m.mean.X}" );
			Check( MathF.Abs( m.mean.Z - 50f ) < 40f, $"plankton mean Z must barely move under near-zero gravity, got {m.mean.Z}" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: sand falls fast, lands dead, dies by 3s", () =>
		{
			var origin = new Vector3( 0, 0, 100 );
			var sys = Build( "sand", origin );
			sys.Step( 60 ); // 1 s: fall time to the floor is ~0.5 s
			Check( sys.AliveCount > 0, "sand must still be alive at 1 s" );
			var m = Mean( sys, origin );
			Check( m.mean.Z < 30f, $"sand mean Z at 1 s must be near the floor (fell from 100), got {m.mean.Z}" );
			sys.Step( 120 ); // 3 s total, past the 2.5 s life max
			Check( sys.AliveCount == 0, $"sand must be dead by 3 s, got {sys.AliveCount} alive" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: silt settles gently, not like sand", () =>
		{
			var origin = new Vector3( 0, 0, 50 );
			var sys = Build( "silt", origin );
			sys.Step( 120 ); // 2 s
			Check( sys.AliveCount > 0, "silt must still be alive at 2 s" );
			var m = Mean( sys, origin );
			float drop = 50f - m.mean.Z;
			Check( drop > 2f && drop < 60f, $"silt mean drop at 2 s must be gentle (2..60 in), got {drop}" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: caustic motes drift slowly", () =>
		{
			var origin = new Vector3( 0, 0, 50 );
			var sys = Build( "caustic_motes", origin );
			sys.Step( 120 ); // 2 s
			Check( sys.AliveCount > 0, "caustic motes must still be alive at 2 s" );
			var m = Mean( sys, origin );
			Check( m.meanDist < 60f, $"caustic motes mean displacement at 2 s must be slow (< 60 in), got {m.meanDist}" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: oil rises barely", () =>
		{
			var origin = new Vector3( 0, 0, 50 );
			var sys = new ParticleSystem( ParticlePresetPackC.SettingsFor( "oil", Base() ) );
			var em = ParticlePresetPackC.Create( "oil", origin );
			em.Rate = 0; em.Bursts.Add( new ParticleBurst( 0, 60 ) ); // clean cohort, same emitter numbers
			sys.AddEmitter( em );
			sys.Step( 180 ); // 3 s
			Check( sys.AliveCount == 60, $"oil cohort must still be alive at 3 s (life min 4 s), got {sys.AliveCount}" );
			var m = Mean( sys, origin );
			float rise = m.mean.Z - 50f;
			Check( rise > 2f && rise < 40f, $"oil mean rise at 3 s must be barely positive (2..40 in), got {rise}" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: lava rises hot, far faster than oil", () =>
		{
			var origin = new Vector3( 0, 0, 50 );
			var sys = Build( "lava", origin );
			sys.Step( 60 ); // 1 s
			Check( sys.AliveCount > 0, "lava must still be alive at 1 s" );
			var m = Mean( sys, origin );
			float rise = m.mean.Z - 50f;
			Check( rise > 40f, $"lava mean rise at 1 s must be hot (> 40 in), got {rise}" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: frost breath fires a forward cone, then fades fast", () =>
		{
			var origin = new Vector3( 0, 0, 50 );
			var sys = Build( "frost_breath", origin );
			sys.Step( 30 ); // 0.5 s
			Check( sys.AliveCount == 40, $"frost burst must be fully alive at 0.5 s, got {sys.AliveCount}" );
			float meanX = 0, meanAbsY = 0; int n = 0;
			foreach ( var st in sys.States )
				if ( st.Alive != 0 ) { meanX += st.Position.X - origin.X; meanAbsY += MathF.Abs( st.Position.Y - origin.Y ); n++; }
			meanX /= n; meanAbsY /= n;
			Check( meanX > 8f, $"frost mean forward (+X) travel at 0.5 s must exceed 8 in, got {meanX}" );
			Check( meanAbsY < meanX, $"frost cone must be narrower sideways than forward (mean |Y| {meanAbsY} vs mean X {meanX})" );
			sys.Step( 90 ); // 2 s total, past the 1.2 s life max
			Check( sys.AliveCount == 0, $"frost breath must be dead by 2 s (fast fade), got {sys.AliveCount} alive" );
		} );

		registry.Add( "particle-preset-pack-c", "sim: same seed gives identical pack-C states", () =>
		{
			ParticleSystem Run()
			{
				var sys = Build( "ink", new Vector3( 0, 0, 50 ), 42 );
				sys.Step( 60 );
				return sys;
			}
			var a = Run(); var b = Run();
			Check( a.AliveCount == b.AliveCount && a.AliveCount > 0, "determinism run needs live particles" );
			for ( int i = 0; i < a.HighWater; i++ )
				Check( a.States[i].Position == b.States[i].Position, $"slot {i} position differs between same-seed runs" );
		} );
	}
}
