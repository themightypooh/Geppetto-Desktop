using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleAuthoringTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		string work = settings.WorkDirectory;
		void Check( string name, Action test ) => registry.Add( "particle-authoring", name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Func<object?> action, string label )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"expected an error for {label}, got silent success" );
		}
		int Alive( ParticleSystem system )
		{
			int n = 0;
			foreach ( var s in system.States ) if ( s.Alive != 0 ) n++;
			return n;
		}
		void AssertSameSim( ParticleSystem a, ParticleSystem b, string label )
		{
			var sa = a.States;
			var sb = b.States;
			Assert( sa.Length == sb.Length, $"{label}: state length {sa.Length} != {sb.Length}" );
			for ( int i = 0; i < sa.Length; i++ )
			{
				Assert( sa[i].Position == sb[i].Position, $"{label}: slot {i} position differs" );
				Assert( sa[i].Velocity == sb[i].Velocity, $"{label}: slot {i} velocity differs" );
				Assert( sa[i].Age == sb[i].Age, $"{label}: slot {i} age differs" );
				Assert( sa[i].Alive == sb[i].Alive, $"{label}: slot {i} alive differs" );
			}
		}
		ParticleSystemDef SampleDef()
		{
			var def = new ParticleSystemDef
			{
				Name = "sample",
				Settings = new ParticleSettingsDef
				{
					Capacity = 500,
					Seed = 42,
					Drag = 0.4f,
					GroundEnabled = true,
					GroundOffset = -2f,
					Restitution = 0.35f,
					RenderType = "Billboard",
					StrandPoints = 4,
				},
			};
			def.Emitters.Add( new ParticleEmitterDef
			{
				Preset = "sparks",
				Position = new[] { 1f, 2f, 3f },
			} );
			def.Emitters.Add( new ParticleEmitterDef
			{
				Shape = "Box",
				Position = new[] { 0f, 0f, 0f },
				BoxHalfExtents = new[] { 30f, 30f, 10f },
				Rate = 12f,
				LifetimeMin = 3f,
				LifetimeMax = 6f,
				SizeMin = 0.2f,
				SizeMax = 0.6f,
				Direction = new[] { 0f, 0f, 1f },
				Speed = 5f,
				SpreadDegrees = 180f,
			} );
			def.Emitters[1].Bursts = new List<ParticleBurstDef>
			{
				new() { Step = 0, Count = 25 },
				new() { Step = 30, Count = 25 },
			};
			return def;
		}

		Check( "json round-trip is byte-identical", () =>
		{
			var def = SampleDef();
			string json = ParticleAuthoring.ToJson( def );
			var back = ParticleAuthoring.FromJson( json );
			Assert( ParticleAuthoring.ToJson( back ) == json, "re-serialized def differs from the original" );
			Assert( back.Version == 1 && back.Name == "sample", "version/name lost in round trip" );
			Assert( back.Settings.Capacity == 500 && back.Settings.Seed == 42, "settings lost in round trip" );
			Assert( back.Emitters.Count == 2 && back.Emitters[0].Preset == "sparks", "emitters lost in round trip" );
			Assert( back.Emitters[1].Bursts is { Count: 2 }, "bursts lost in round trip" );
		} );

		Check( "all six presets build and emit", () =>
		{
			foreach ( var name in ParticleEmitterPresets.Names )
			{
				var def = new ParticleSystemDef();
				def.Emitters.Add( new ParticleEmitterDef { Preset = name, Position = new[] { 0f, 0f, 10f } } );
				var system = def.Build();
				Assert( system.Emitters.Count == 1, $"{name}: emitter missing after build" );
				system.Step( 10 );
				Assert( Alive( system ) > 0, $"{name}: nothing alive after 10 steps" );
			}
		} );

		Check( "same def builds bit-identical simulations", () =>
		{
			var a = SampleDef().Build();
			var b = SampleDef().Build();
			a.Step( 60 );
			b.Step( 60 );
			AssertSameSim( a, b, "determinism" );
		} );

		Check( "unknown preset fails loudly", () =>
		{
			var def = new ParticleSystemDef();
			def.Emitters.Add( new ParticleEmitterDef { Preset = "nope" } );
			Throws( () => def.Build(), "unknown preset in def" );
			Throws( () => ParticleVerb.ParseLine( "particle preset=nope" ), "unknown preset in verb" );
			Throws( () => ParticleAuthoring.FromJson(
				"{\"version\": 1, \"emitters\": [{\"preset\": \"nope\"}]}" ).Build(),
				"unknown preset in json" );
		} );

		Check( "malformed json and bad versions fail loudly", () =>
		{
			Throws( () => ParticleAuthoring.FromJson( "{" ), "truncated json" );
			Throws( () => ParticleAuthoring.FromJson( "{}" ), "missing version" );
			Throws( () => ParticleAuthoring.FromJson( "{\"version\": 2, \"emitters\": []}" ), "version 2" );
			Throws( () => ParticleAuthoring.FromJson( "{\"version\": \"1\", \"emitters\": []}" ), "string version" );
			Throws( () => ParticleAuthoring.FromJson( "[1, 2]" ), "top-level array" );
			Throws( () => ParticleAuthoring.FromJson( "" ), "empty json" );
		} );

		Check( "invalid values fail loudly", () =>
		{
			ParticleSystemDef WithSettings( Action<ParticleSettingsDef> change )
			{
				var def = new ParticleSystemDef();
				change( def.Settings );
				return def;
			}
			Throws( () => WithSettings( s => s.Capacity = 0 ).Build(), "capacity 0" );
			Throws( () => WithSettings( s => s.RenderType = "Points" ).Build(), "bad render type" );
			Throws( () => WithSettings( s => s.Gravity = new[] { 1f, 2f } ).Build(), "2-component gravity" );
			ParticleSystemDef WithEmitter( Action<ParticleEmitterDef> change )
			{
				var def = new ParticleSystemDef();
				var em = new ParticleEmitterDef();
				change( em );
				def.Emitters.Add( em );
				return def;
			}
			Throws( () => WithEmitter( e => e.LifetimeMin = 0 ).Build(), "lifetime 0" );
			Throws( () => WithEmitter( e => e.SpreadDegrees = 200 ).Build(), "spread 200" );
			Throws( () => WithEmitter( e => e.Shape = "Cone" ).Build(), "unknown shape" );
			Throws( () => WithEmitter( e => { e.Shape = "MeshSurface"; } ).Build(), "mesh surface without mesh" );
			Throws( () => WithEmitter( e => e.Bursts = new List<ParticleBurstDef> { new() { Step = 0, Count = -5 } } ).Build(), "negative burst" );
		} );

		Check( "unknown json fields are ignored for forward compat", () =>
		{
			var def = ParticleAuthoring.FromJson(
				"{\"version\": 1, \"futureField\": 123, \"emitters\": [{\"preset\": \"sparks\", \"yetAnother\": true}]}" );
			var system = def.Build();
			system.Step( 1 );
			Assert( Alive( system ) == 70, $"expected the 70-particle sparks burst, got {Alive( system )}" );
		} );

		Check( "verb preset line builds with overrides", () =>
		{
			var result = ParticleVerb.Run( "particle preset=sparks at=1,2,3 rate=10 speed=100" );
			Assert( result.System is not null, "verb did not build a system" );
			var em = result.System!.Emitters[0];
			Assert( em.Position == new Vector3( 1, 2, 3 ), $"emitter at {em.Position}, expected (1, 2, 3)" );
			Assert( em.Rate == 10f, $"rate {em.Rate}, expected 10" );
			Assert( em.Speed == 100f, $"speed {em.Speed}, expected 100" );
			Assert( result.System.Settings.Drag == 0.05f, $"sparks drag {result.System.Settings.Drag}, expected 0.05" );
			result.System.Step( 1 );
			Assert( Alive( result.System ) == 70, $"expected 70 burst particles, got {Alive( result.System )}" );
		} );

		Check( "verb preset line applies the preset medium", () =>
		{
			var result = ParticleVerb.Run( "particle preset=bubbles at=0,0,0" );
			Assert( result.System is not null, "verb did not build a system" );
			Assert( result.System!.Settings.Gravity.Z == 60f, $"bubbles gravity Z {result.System.Settings.Gravity.Z}, expected 60 (rising)" );
			Assert( result.System.Settings.Drag == 0.8f, $"bubbles drag {result.System.Settings.Drag}, expected 0.8" );
		} );

		Check( "verb bad lines fail loudly", () =>
		{
			Throws( () => ParticleVerb.Run( "particle preset=sparks bogus=1" ), "unknown verb key" );
			Throws( () => ParticleVerb.Run( "particle preset=sparks file=x.json" ), "preset plus file" );
			Throws( () => ParticleVerb.Run( "particle sparks" ), "bare word, not key=value" );
			Throws( () => ParticleVerb.Run( "particle" ), "neither preset nor file" );
			Throws( () => ParticleVerb.Run( "particle preset=sparks preset=sparks" ), "duplicate key" );
			Throws( () => ParticleVerb.Run( "particle preset=sparks rate=abc" ), "non-numeric rate" );
			Throws( () => ParticleVerb.Run( "particle preset=sparks at=1,2" ), "2-component at" );
			Throws( () => ParticleVerb.Run( "particle preset=sparks burst=0" ), "burst without count" );
		} );

		Check( "verb file form with one emitter overrides position", () =>
		{
			Directory.CreateDirectory( work );
			var def = new ParticleSystemDef();
			def.Emitters.Add( new ParticleEmitterDef { Preset = "sparks" } );
			string path = Path.Combine( work, "one.json" );
			ParticleAuthoring.SaveFile( def, path );
			var result = ParticleVerb.Run( $"particle file={path} at=5,5,5" );
			Assert( result.System is not null, "file verb did not build" );
			Assert( result.System!.Emitters[0].Position == new Vector3( 5, 5, 5 ), "at= override did not move the emitter" );
			Throws( () => ParticleVerb.Run( $"particle file={path} rate=9 rate=9" ), "duplicate key on file form" );
		} );

		Check( "verb multi-emitter override fails instead of guessing", () =>
		{
			Directory.CreateDirectory( work );
			string path = Path.Combine( work, "two.json" );
			ParticleAuthoring.SaveFile( SampleDef(), path );
			Throws( () => ParticleVerb.Run( $"particle file={path} at=5,5,5" ), "emitter override on two-emitter def" );
		} );

		Check( "verb save form writes a loadable def and builds nothing", () =>
		{
			Directory.CreateDirectory( work );
			string path = Path.Combine( work, "dust.json" );
			var result = ParticleVerb.Run( $"particle preset=dust_motes save={path}" );
			Assert( result.System is null, "save form should not build a system" );
			Assert( result.SavedPath == path, "saved path not reported" );
			Assert( File.Exists( path ), "def file was not written" );
			var loaded = ParticleAuthoring.LoadFile( path );
			Assert( loaded.Emitters.Count == 1 && loaded.Emitters[0].Preset == "dust_motes", "saved def does not carry the preset" );
			var system = loaded.Build();
			system.Step( 60 );
			Assert( Alive( system ) > 0, "saved dust_motes def emits nothing" );
		} );

		Check( "mesh emitter and collision mesh round-trip", () =>
		{
			var mesh = new ParticleMeshDef
			{
				Vertices = new List<float[]>
				{
					new[] { 0f, 0f, 0f }, new[] { 10f, 0f, 0f },
					new[] { 10f, 10f, 0f }, new[] { 0f, 10f, 0f },
				},
				Indices = new List<int> { 0, 1, 2, 0, 2, 3 },
			};
			var def = new ParticleSystemDef();
			def.Settings.CollisionMesh = mesh;
			def.Emitters.Add( new ParticleEmitterDef
			{
				Shape = "MeshSurface",
				Mesh = mesh,
				Direction = new[] { 0f, 0f, 1f },
				Speed = 10f,
				LifetimeMin = 1f,
				LifetimeMax = 1f,
				SizeMin = 1f,
				SizeMax = 1f,
			} );
			def.Emitters[0].Bursts = new List<ParticleBurstDef> { new() { Step = 0, Count = 20 } };
			var system = def.Build();
			Assert( system.Emitters[0].Mesh is { TriangleCount: 2 }, "emitter mesh triangle count lost" );
			Assert( system.Settings.CollisionMesh is { TriangleCount: 2 }, "collision mesh triangle count lost" );
			system.Step( 5 );
			Assert( Alive( system ) > 0, "mesh-surface emitter emitted nothing" );
		} );

		Check( "from-system rebuild steps identically, preset name dropped by design", () =>
		{
			var def = new ParticleSystemDef();
			def.Emitters.Add( new ParticleEmitterDef { Preset = "sparks", Position = new[] { 0f, 0f, 5f } } );
			var a = def.Build();
			var back = ParticleSystemDef.FromSystem( a, "rebuilt" );
			Assert( back.Emitters[0].Preset is null, "preset name should not survive FromSystem (it is a shortcut, not state)" );
			Assert( !ParticleAuthoring.ToJson( back ).Contains( "sparks" ), "preset name leaked into the from-system def" );
			var b = back.Build();
			a.Step( 30 );
			b.Step( 30 );
			AssertSameSim( a, b, "from-system rebuild" );
		} );
	}
}
