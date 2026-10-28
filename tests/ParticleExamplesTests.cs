using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

// Particle example gallery tests (particles wave-6 lane P72).
// Every example is proven against lane 5's own code, read-only:
// ParticleAuthoring.FromJson + ParticleSystemDef.Build. No file in
// the canonical tree is edited by this lane; the sweep line at the
// bottom of this file is a held item for the coordinator, not an edit.
//
// SWEEP (held, coordinator owns — NOT added by this lane):
//   ParticleExamplesTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in
// tests/TestRegistry.cs.

internal static class ParticleExamplesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "particle-examples", name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void AssertEq<T>( T got, T want, string label )
		{
			if ( !EqualityComparer<T>.Default.Equals( got, want ) )
				throw new Exception( $"{label}: got {got}, want {want}" );
		}
		int Alive( ParticleSystem system )
		{
			int n = 0;
			foreach ( var s in system.States ) if ( s.Alive != 0 ) n++;
			return n;
		}
		ParticleSystemDef Parse( ParticleExample example ) => ParticleAuthoring.FromJson( example.Json );

		string[] expectedNames =
		{
			"fountain", "explosion_burst", "rain", "snow", "bubbles_rising",
			"fire", "smoke", "magic_sparkles", "waterfall_sheet", "tornado_vortex",
			"ground_embers", "floating_dust", "water_splash", "drifting_leaves",
			"confetti_burst", "steam_puff", "lava_rise", "ink_cloud",
			"frost_breath", "firefly_drift",
		};

		Check( "exactly 20 examples, names unique and lowercase-underscore", () =>
		{
			var all = ParticleExamples.All();
			AssertEq( all.Count, 20, "gallery count" );
			var seen = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var example in all )
			{
				Assert( !string.IsNullOrWhiteSpace( example.Name ), "blank example name" );
				Assert( example.Name == example.Name.ToLowerInvariant(), $"{example.Name}: name is not lowercase" );
				Assert( example.Name.IndexOf( ' ' ) < 0 && example.Name.IndexOf( '-' ) < 0, $"{example.Name}: name must use underscores, not spaces or dashes" );
				Assert( seen.Add( example.Name ), $"{example.Name}: duplicate name" );
			}
		} );

		Check( "All() order is the documented file order", () =>
		{
			var all = ParticleExamples.All();
			AssertEq( all.Count, expectedNames.Length, "gallery count vs expected list" );
			for ( int i = 0; i < expectedNames.Length; i++ )
				AssertEq( all[i].Name, expectedNames[i], $"position {i}" );
		} );

		Check( "all descriptions non-empty", () =>
		{
			foreach ( var example in ParticleExamples.All() )
				Assert( !string.IsNullOrWhiteSpace( example.Description ), $"{example.Name}: empty description" );
		} );

		Check( "every JSON parses under lane 5 and builds without throwing", () =>
		{
			foreach ( var example in ParticleExamples.All() )
			{
				var def = Parse( example );
				AssertEq( def.Version, 1, $"{example.Name}: version" );
				Assert( example.Json.Contains( "\"seed\"", StringComparison.Ordinal ), $"{example.Name}: seed not serialized in JSON" );
				Assert( def.Settings.Seed != 0, $"{example.Name}: parsed seed is 0" );
				var system = def.Build();
				Assert( system.Emitters.Count >= 1, $"{example.Name}: built system has no emitters" );
			}
		} );

		Check( "every JSON declares version 1 and a distinct seed", () =>
		{
			var seeds = new HashSet<uint>();
			foreach ( var example in ParticleExamples.All() )
			{
				var def = Parse( example );
				AssertEq( def.Version, 1, $"{example.Name}: version" );
				Assert( seeds.Add( def.Settings.Seed ), $"{example.Name}: seed {def.Settings.Seed} reused by another example" );
			}
		} );

		Check( "no example fakes a preset, colour, texture, or atlas field", () =>
		{
			foreach ( var example in ParticleExamples.All() )
			{
				string json = example.Json.ToLowerInvariant();
				Assert( !json.Contains( "\"preset\"" ), $"{example.Name}: JSON references a preset" );
				Assert( !json.Contains( "color" ) && !json.Contains( "colour" ), $"{example.Name}: JSON carries a colour field the sim does not have" );
				Assert( !json.Contains( "texture" ) && !json.Contains( "atlas" ), $"{example.Name}: JSON carries a texture/atlas field the sim does not have" );
			}
		} );

		Check( "Find resolves all 20 by exact name", () =>
		{
			foreach ( var name in expectedNames )
			{
				var found = ParticleExamples.Find( name );
				Assert( found is not null, $"Find({name}) returned null" );
				AssertEq( found!.Name, name, $"Find({name}) name" );
			}
		} );

		Check( "Find strips fx:/vfx:/particle: prefixes like preset cues", () =>
		{
			AssertEq( ParticleExamples.Find( "fx:fire" )?.Name, "fire", "fx: prefix" );
			AssertEq( ParticleExamples.Find( "vfx:rain" )?.Name, "rain", "vfx: prefix" );
			AssertEq( ParticleExamples.Find( "particle:snow" )?.Name, "snow", "particle: prefix" );
			AssertEq( ParticleExamples.Find( "fx_fire" )?.Name, "fire", "fx_ prefix" );
			AssertEq( ParticleExamples.Find( "  FX:FIRE  " )?.Name, "fire", "case + whitespace + prefix" );
		} );

		Check( "Find returns null for unknown, empty, and cue-word names", () =>
		{
			Assert( ParticleExamples.Find( "no_such_effect" ) is null, "unknown name should be null" );
			Assert( ParticleExamples.Find( "" ) is null, "empty name should be null" );
			Assert( ParticleExamples.Find( "   " ) is null, "blank name should be null" );
			Assert( ParticleExamples.Find( null ) is null, "null name should be null" );
			Assert( ParticleExamples.Find( "fx:no_such_effect" ) is null, "prefixed unknown name should be null" );
			// Deliberate difference from cue resolution: no underscore-word
			// containment, so a longer cue word does not hijack a gallery name.
			Assert( ParticleExamples.Find( "footstep_water_splash" ) is null, "cue-word containment must not resolve" );
		} );

		Check( "All() order is stable across calls and callers cannot disturb it", () =>
		{
			var first = ParticleExamples.All();
			var second = ParticleExamples.All();
			AssertEq( first.Count, second.Count, "All() count across calls" );
			for ( int i = 0; i < first.Count; i++ )
				AssertEq( first[i].Name, second[i].Name, $"All() position {i} across calls" );
			if ( first is List<ParticleExample> mutable ) mutable.Clear();
			AssertEq( ParticleExamples.All().Count, 20, "All() after a caller cleared a returned list" );
		} );

		Check( "measured values: bubbles, fountain, and ink settings", () =>
		{
			var bubbles = Parse( ParticleExamples.Find( "bubbles_rising" )! );
			AssertEq( bubbles.Settings.Gravity![2], 60f, "bubbles gravity Z (must rise)" );
			AssertEq( bubbles.Settings.Drag, 0.8f, "bubbles drag" );
			var fountain = Parse( ParticleExamples.Find( "fountain" )! );
			AssertEq( fountain.Emitters[0].Speed, 220f, "fountain speed" );
			AssertEq( fountain.Emitters[0].LifetimeMin, 1.2f, "fountain lifetime min" );
			AssertEq( fountain.Emitters[0].LifetimeMax, 2.0f, "fountain lifetime max" );
			Assert( fountain.Settings.GroundEnabled, "fountain ground must be enabled" );
			var ink = Parse( ParticleExamples.Find( "ink_cloud" )! );
			AssertEq( ink.Settings.Drag, 3f, "ink drag (thick water)" );
			AssertEq( ink.Settings.Gravity![2], -5f, "ink gravity Z (near-neutral)" );
		} );

		Check( "measured values: tornado, waterfall, explosion, fire", () =>
		{
			var tornado = Parse( ParticleExamples.Find( "tornado_vortex" )! );
			AssertEq( tornado.Settings.VortexStrength, 30000f, "tornado vortex strength" );
			var center = tornado.Settings.VortexCenter!;
			Assert( center.Length == 3 && center[0] == 0f && center[1] == 0f && center[2] == 60f, $"tornado vortex center [{string.Join( ",", center )}], want [0,0,60]" );
			var axis = tornado.Settings.VortexAxis!;
			Assert( axis.Length == 3 && axis[0] == 0f && axis[1] == 0f && axis[2] == 1f, $"tornado vortex axis [{string.Join( ",", axis )}], want [0,0,1]" );
			var falls = Parse( ParticleExamples.Find( "waterfall_sheet" )! );
			AssertEq( falls.Emitters[0].Shape, "Area", "waterfall shape" );
			AssertEq( falls.Emitters[0].UseSurfaceNormal, false, "waterfall must not use the surface normal" );
			var dir = falls.Emitters[0].Direction!;
			Assert( dir.Length == 3 && dir[0] == 0f && dir[1] == 0f && dir[2] == -1f, $"waterfall direction [{string.Join( ",", dir )}], want [0,0,-1]" );
			var boom = Parse( ParticleExamples.Find( "explosion_burst" )! );
			AssertEq( boom.Emitters.Count, 2, "explosion emitter count" );
			AssertEq( boom.Emitters[0].Bursts![0].Count, 180, "explosion core burst count" );
			var fire = Parse( ParticleExamples.Find( "fire" )! );
			AssertEq( fire.Settings.Gravity![2], 80f, "fire gravity Z (hot air rises)" );
		} );

		Check( "every built example actually emits", () =>
		{
			foreach ( var example in ParticleExamples.All() )
			{
				var system = Parse( example ).Build();
				bool hasBurst = false;
				foreach ( var em in system.Emitters )
					if ( em.Bursts.Count > 0 ) hasBurst = true;
				// Bursts fire on step 0; rate emitters need a full second
				// (60 steps) before a slow rate has necessarily emitted.
				system.Step( hasBurst ? 1 : 60 );
				Assert( Alive( system ) > 0, $"{example.Name}: nothing alive after stepping" );
			}
		} );

		Check( "same example builds bit-identical simulations (seeded determinism)", () =>
		{
			var example = ParticleExamples.Find( "fountain" )!;
			var a = Parse( example ).Build();
			var b = Parse( example ).Build();
			a.Step( 60 );
			b.Step( 60 );
			var sa = a.States;
			var sb = b.States;
			AssertEq( sa.Length, sb.Length, "determinism state length" );
			for ( int i = 0; i < sa.Length; i++ )
			{
				Assert( sa[i].Position == sb[i].Position, $"determinism slot {i} position" );
				Assert( sa[i].Velocity == sb[i].Velocity, $"determinism slot {i} velocity" );
				Assert( sa[i].Age == sb[i].Age, $"determinism slot {i} age" );
				Assert( sa[i].Alive == sb[i].Alive, $"determinism slot {i} alive" );
			}
		} );
	}
}
