using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Marionette.Desktop;

internal static class ParticleSchemaTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "particle-schema", name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void AssertValid( string json, string label )
		{
			var errors = ParticleSchema.Validate( json );
			Assert( errors.Count == 0, $"{label}: expected valid, got: {string.Join( "; ", errors )}" );
		}
		void AssertInvalid( string json, string pathFragment, string label )
		{
			var errors = ParticleSchema.Validate( json );
			Assert( errors.Count > 0, $"{label}: expected errors, got none" );
			Assert( errors.Any( e => e.Path.Contains( pathFragment, StringComparison.Ordinal ) ),
				$"{label}: no error at '{pathFragment}' among: {string.Join( "; ", errors )}" );
		}
		// Lane 5's verdict on the same document: true when FromJson + Build
		// succeed (the document is a system lane 5 accepts).
		bool Lane5Accepts( string json )
		{
			try { _ = ParticleAuthoring.FromJson( json ).Build(); return true; }
			catch ( Exception ) { return false; }
		}
		void AssertAgreement( string json, bool expectValid, string label )
		{
			bool lane5 = Lane5Accepts( json );
			var errors = ParticleSchema.Validate( json );
			Assert( lane5 == expectValid, $"{label}: lane 5 verdict {lane5} != expected {expectValid}" );
			Assert( ( errors.Count == 0 ) == expectValid,
				$"{label}: validator verdict {errors.Count == 0} != expected {expectValid}; errors: {string.Join( "; ", errors )}" );
		}

		string FindSchemaFile()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					string candidate = Path.Combine( dir.FullName, "ParticleDefinition.schema.json" );
					if ( File.Exists( candidate ) )
						return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "ParticleDefinition.schema.json not found walking up from the test binary or working directory." );
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

		Check( "schema file is valid json and matches validator behaviour", () =>
		{
			string path = FindSchemaFile();
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			Assert( root.ValueKind == JsonValueKind.Object, "schema root is not an object" );
			Assert( root.GetProperty( "type" ).GetString() == "object", "schema root type is not object" );
			var version = root.GetProperty( "properties" ).GetProperty( "version" );
			Assert( version.GetProperty( "const" ).GetInt32() == ParticleSchema.CurrentVersion,
				$"schema version const {version.GetProperty( "const" )} != validator {ParticleSchema.CurrentVersion}" );
			Assert( ParticleSchema.CurrentVersion == ParticleAuthoring.CurrentVersion, "validator version drifted from lane 5" );
			var required = root.GetProperty( "required" ).EnumerateArray().Select( e => e.GetString() ).ToList();
			Assert( required.Count == 1 && required[0] == "version",
				$"schema required is [{string.Join( ", ", required )}], expected [version] only" );
			Assert( !required.Contains( "settings" ) && !required.Contains( "emitters" ),
				"schema must not require settings or emitters (lane 5 defaults them)" );
			// Behaviour probes for the required set: missing version fails,
			// missing settings/emitters pass — on both sides of the agreement.
			AssertInvalid( "{\"emitters\": []}", "$.version", "missing version" );
			AssertValid( "{\"version\": 1}", "missing settings and emitters" );
			var settings = root.GetProperty( "properties" ).GetProperty( "settings" );
			Assert( settings.GetProperty( "properties" ).TryGetProperty( "seed", out _ ), "schema settings lack seed" );
			Assert( settings.GetProperty( "properties" ).TryGetProperty( "capacity", out _ ), "schema settings lack capacity" );
			var emitter = root.GetProperty( "properties" ).GetProperty( "emitters" ).GetProperty( "items" );
			var shapes = emitter.GetProperty( "properties" ).GetProperty( "shape" ).GetProperty( "enum" )
				.EnumerateArray().Select( e => e.GetString() ).ToList();
			Assert( shapes.SequenceEqual( ParticleSchema.ShapeNames ),
				$"schema shape enum [{string.Join( ", ", shapes )}] != validator [{string.Join( ", ", ParticleSchema.ShapeNames )}]" );
			var renders = settings.GetProperty( "properties" ).GetProperty( "renderType" ).GetProperty( "enum" )
				.EnumerateArray().Select( e => e.GetString() ).ToList();
			Assert( renders.SequenceEqual( ParticleSchema.RenderTypeNames ),
				$"schema renderType enum [{string.Join( ", ", renders )}] != validator [{string.Join( ", ", ParticleSchema.RenderTypeNames )}]" );
			var meshRequired = root.GetProperty( "definitions" ).GetProperty( "mesh" ).GetProperty( "required" )
				.EnumerateArray().Select( e => e.GetString() ).ToList();
			Assert( meshRequired.Contains( "vertices" ) && meshRequired.Contains( "indices" ),
				"schema mesh must require vertices and indices" );
			Assert( root.GetProperty( "additionalProperties" ).GetBoolean(), "schema root must allow unknown fields (forward compat)" );
			Assert( emitter.GetProperty( "additionalProperties" ).GetBoolean(), "schema emitter must allow unknown fields" );
		} );

		Check( "drift guard: lane 5 round-trip documents validate clean", () =>
		{
			string sample = ParticleAuthoring.ToJson( SampleDef() );
			var built = ParticleAuthoring.FromJson( sample ).Build();
			built.Step( 1 );
			AssertValid( sample, "lane 5 sample def" );

			var mesh = new ParticleMeshDef
			{
				Vertices = new List<float[]>
				{
					new[] { 0f, 0f, 0f }, new[] { 10f, 0f, 0f },
					new[] { 10f, 10f, 0f }, new[] { 0f, 10f, 0f },
				},
				Indices = new List<int> { 0, 1, 2, 0, 2, 3 },
			};
			var meshDef = new ParticleSystemDef();
			meshDef.Settings.CollisionMesh = mesh;
			meshDef.Emitters.Add( new ParticleEmitterDef
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
			meshDef.Emitters[0].Bursts = new List<ParticleBurstDef> { new() { Step = 0, Count = 20 } };
			string meshJson = ParticleAuthoring.ToJson( meshDef );
			var meshBuilt = ParticleAuthoring.FromJson( meshJson ).Build();
			meshBuilt.Step( 1 );
			AssertValid( meshJson, "lane 5 mesh def" );

			var fromSystem = ParticleSystemDef.FromSystem( built, "rebuilt" );
			string rebuiltJson = ParticleAuthoring.ToJson( fromSystem );
			_ = ParticleAuthoring.FromJson( rebuiltJson ).Build();
			AssertValid( rebuiltJson, "lane 5 from-system def (preset dropped by design)" );

			foreach ( var name in ParticleEmitterPresets.Names )
			{
				string json = "{\"version\": 1, \"emitters\": [{\"preset\": \"" + name + "\", \"position\": [0, 0, 10]}]}";
				_ = ParticleAuthoring.FromJson( json ).Build();
				AssertValid( json, $"preset {name}" );
			}
		} );

		Check( "drift guard: lane 5 rejections fail the validator too", () =>
		{
			(string, string)[] rejects =
			{
				("unknown preset", "{\"version\": 1, \"emitters\": [{\"preset\": \"nope\"}]}"),
				("truncated json", "{"),
				("missing version", "{}"),
				("version 2", "{\"version\": 2, \"emitters\": []}"),
				("string version", "{\"version\": \"1\", \"emitters\": []}"),
				("top-level array", "[1, 2]"),
				("empty json", ""),
				("capacity 0", "{\"version\": 1, \"settings\": {\"capacity\": 0}, \"emitters\": []}"),
				("bad render type", "{\"version\": 1, \"settings\": {\"renderType\": \"Points\"}, \"emitters\": []}"),
				("2-component gravity", "{\"version\": 1, \"settings\": {\"gravity\": [1, 2]}, \"emitters\": []}"),
				("lifetime 0", "{\"version\": 1, \"emitters\": [{\"lifetimeMin\": 0}]}"),
				("spread 200", "{\"version\": 1, \"emitters\": [{\"spreadDegrees\": 200}]}"),
				("unknown shape", "{\"version\": 1, \"emitters\": [{\"shape\": \"Cone\"}]}"),
				("mesh surface without mesh", "{\"version\": 1, \"emitters\": [{\"shape\": \"MeshSurface\"}]}"),
				("negative burst", "{\"version\": 1, \"emitters\": [{\"bursts\": [{\"step\": 0, \"count\": -5}]}]}"),
				("version 1.0", "{\"version\": 1.0, \"emitters\": []}"),
				("Version capital only", "{\"Version\": 1, \"emitters\": []}"),
				("shape 5", "{\"version\": 1, \"emitters\": [{\"shape\": \"5\"}]}"),
				("seed overflow", "{\"version\": 1, \"settings\": {\"seed\": 4294967296}, \"emitters\": []}"),
				("capacity 100.0", "{\"version\": 1, \"settings\": {\"capacity\": 100.0}, \"emitters\": []}"),
				("seed null", "{\"version\": 1, \"settings\": {\"seed\": null}, \"emitters\": []}"),
				("settings null", "{\"version\": 1, \"settings\": null, \"emitters\": []}"),
				("emitters null", "{\"version\": 1, \"emitters\": null}"),
				("emitter null entry", "{\"version\": 1, \"emitters\": [null]}"),
				("bool as int", "{\"version\": 1, \"settings\": {\"groundEnabled\": 1}, \"emitters\": []}"),
				("ground normal zero enabled", "{\"version\": 1, \"settings\": {\"groundEnabled\": true, \"groundNormal\": [0, 0, 0]}, \"emitters\": []}"),
				("vortex axis zero strength 5", "{\"version\": 1, \"settings\": {\"vortexStrength\": 5, \"vortexAxis\": [0, 0, 0]}, \"emitters\": []}"),
				("strand points 1", "{\"version\": 1, \"settings\": {\"renderType\": \"Strand\", \"strandPoints\": 1}, \"emitters\": []}"),
				("mesh degenerate", "{\"version\": 1, \"emitters\": [{\"shape\": \"MeshSurface\", \"mesh\": {\"vertices\": [[0, 0, 0], [0, 0, 0], [0, 0, 0]], \"indices\": [0, 1, 2]}}]}"),
				("mesh index out of range", "{\"version\": 1, \"emitters\": [{\"shape\": \"MeshSurface\", \"mesh\": {\"vertices\": [[0, 0, 0], [10, 0, 0], [0, 10, 0]], \"indices\": [0, 1, 9]}}]}"),
				("mesh missing indices", "{\"version\": 1, \"emitters\": [{\"shape\": \"MeshSurface\", \"mesh\": {\"vertices\": [[0, 0, 0], [10, 0, 0], [0, 10, 0]]}}]}"),
				("point emitter bad mesh", "{\"version\": 1, \"emitters\": [{\"shape\": \"Point\", \"mesh\": {\"vertices\": [[0, 0, 0], [0, 0, 0], [0, 0, 0]], \"indices\": [0, 1, 2]}}]}"),
				("area parallel", "{\"version\": 1, \"emitters\": [{\"shape\": \"Area\", \"areaU\": [1, 0, 0], \"areaV\": [2, 0, 0]}]}"),
				("box negative", "{\"version\": 1, \"emitters\": [{\"shape\": \"Box\", \"boxHalfExtents\": [1, -1, 1]}]}"),
				("sphere negative", "{\"version\": 1, \"emitters\": [{\"shape\": \"Sphere\", \"sphereRadius\": -1}]}"),
				("preset lifetime max below min", "{\"version\": 1, \"emitters\": [{\"preset\": \"sparks\", \"lifetimeMax\": 0.1}]}"),
				("burst step null", "{\"version\": 1, \"emitters\": [{\"bursts\": [{\"step\": null, \"count\": 5}]}]}"),
				("render type null", "{\"version\": 1, \"settings\": {\"renderType\": null}, \"emitters\": []}"),
				("gravity null", "{\"version\": 1, \"settings\": {\"gravity\": null}, \"emitters\": []}"),
				("gravity 1e400", "{\"version\": 1, \"settings\": {\"gravity\": [0, 0, 1e400]}, \"emitters\": []}"),
				("mesh empty", "{\"version\": 1, \"emitters\": [{\"shape\": \"MeshSurface\", \"mesh\": {}}]}"),
				("dup version 1 then 2", "{\"version\": 1, \"version\": 2, \"emitters\": []}"),
				("version 1 then Version 2", "{\"version\": 1, \"Version\": 2, \"emitters\": []}"),
				("plain zero direction speed 5", "{\"version\": 1, \"emitters\": [{\"direction\": [0, 0, 0], \"speed\": 5}]}"),
				("area zero direction usn false speed 5", "{\"version\": 1, \"emitters\": [{\"shape\": \"Area\", \"direction\": [0, 0, 0], \"speed\": 5, \"useSurfaceNormal\": false}]}"),
			};
			foreach ( var (label, json) in rejects )
				AssertAgreement( json, false, label );
		} );

		Check( "drift guard: lane 5 acceptances validate clean too", () =>
		{
			(string, string)[] accepts =
			{
				("bare emitter", "{\"version\": 1, \"emitters\": [{}]}"),
				("settings missing", "{\"version\": 1}"),
				("emitters missing", "{\"version\": 1, \"settings\": {}}"),
				("shape 0 is Point", "{\"version\": 1, \"emitters\": [{\"shape\": \"0\"}]}"),
				("shape 4 is Sphere", "{\"version\": 1, \"emitters\": [{\"shape\": \"4\"}]}"),
				("shape spaces trimmed", "{\"version\": 1, \"emitters\": [{\"shape\": \" point \"}]}"),
				("render 1 is MeshInstance", "{\"version\": 1, \"settings\": {\"renderType\": \"1\"}, \"emitters\": []}"),
				("render undefined numeric accepted by lane 5", "{\"version\": 1, \"settings\": {\"renderType\": \"3\"}, \"emitters\": []}"),
				("preset fx prefix", "{\"version\": 1, \"emitters\": [{\"preset\": \"fx:sparks\"}]}"),
				("preset footstep word", "{\"version\": 1, \"emitters\": [{\"preset\": \"footstep_water_splash\"}]}"),
				("preset sparks extra word", "{\"version\": 1, \"emitters\": [{\"preset\": \"sparks_extra\"}]}"),
				("preset dust motes space", "{\"version\": 1, \"emitters\": [{\"preset\": \"dust motes\"}]}"),
				("preset spaces trimmed", "{\"version\": 1, \"emitters\": [{\"preset\": \" SPARKS \"}]}"),
				("seed 0", "{\"version\": 1, \"settings\": {\"seed\": 0}, \"emitters\": []}"),
				("seed max uint32", "{\"version\": 1, \"settings\": {\"seed\": 4294967295}, \"emitters\": []}"),
				("capacity max", "{\"version\": 1, \"settings\": {\"capacity\": 4000000}, \"emitters\": []}"),
				("fixed delta 1", "{\"version\": 1, \"settings\": {\"fixedDeltaTime\": 1}, \"emitters\": []}"),
				("ground normal zero disabled", "{\"version\": 1, \"settings\": {\"groundNormal\": [0, 0, 0]}, \"emitters\": []}"),
				("vortex axis zero strength 0", "{\"version\": 1, \"settings\": {\"vortexAxis\": [0, 0, 0]}, \"emitters\": []}"),
				("strand points 2", "{\"version\": 1, \"settings\": {\"renderType\": \"Strand\", \"strandPoints\": 2}, \"emitters\": []}"),
				("preset zero direction speed override", "{\"version\": 1, \"emitters\": [{\"preset\": \"sparks\", \"direction\": [0, 0, 0], \"speed\": 99}]}"),
				("plain zero direction speed 0", "{\"version\": 1, \"emitters\": [{\"direction\": [0, 0, 0], \"speed\": 0}]}"),
				("area zero direction usn true speed 5", "{\"version\": 1, \"emitters\": [{\"shape\": \"Area\", \"direction\": [0, 0, 0], \"speed\": 5}]}"),
				("sphere radius 0", "{\"version\": 1, \"emitters\": [{\"shape\": \"Sphere\", \"sphereRadius\": 0}]}"),
				("box extents 0", "{\"version\": 1, \"emitters\": [{\"shape\": \"Box\", \"boxHalfExtents\": [0, 0, 0]}]}"),
				("burst missing count defaults 0", "{\"version\": 1, \"emitters\": [{\"bursts\": [{\"step\": 0}]}]}"),
				("emitter rate null", "{\"version\": 1, \"emitters\": [{\"rate\": null}]}"),
				("emitter preset null", "{\"version\": 1, \"emitters\": [{\"preset\": null}]}"),
				("collision mesh null", "{\"version\": 1, \"settings\": {\"collisionMesh\": null}, \"emitters\": []}"),
				("dup version 2 then 1", "{\"version\": 2, \"version\": 1, \"emitters\": []}"),
				("Version 2 then version 1", "{\"Version\": 2, \"version\": 1, \"emitters\": []}"),
				("capacity dup last wins valid", "{\"version\": 1, \"settings\": {\"Capacity\": 0, \"capacity\": 500}, \"emitters\": []}"),
				("settings case insensitive", "{\"version\": 1, \"Settings\": {\"Capacity\": 500}, \"emitters\": []}"),
				("preset shape box override", "{\"version\": 1, \"emitters\": [{\"preset\": \"sparks\", \"shape\": \"Box\"}]}"),
			};
			foreach ( var (label, json) in accepts )
				AssertAgreement( json, true, label );
		} );

		Check( "unknown fields are tolerated at every level", () =>
		{
			string json = "{\"version\": 1, \"futureField\": 123, \"settings\": {\"yetAnother\": {\"a\": [1, 2]}, \"capacity\": 500}, \"emitters\": [{\"preset\": \"sparks\", \"zzz\": \"hi\", \"bursts\": [{\"step\": 0, \"count\": 5, \"note\": true}], \"mesh\": null}]}";
			_ = ParticleAuthoring.FromJson( json ).Build();
			AssertValid( json, "unknown fields" );
		} );

		Check( "each strict rejection class carries a useful field path", () =>
		{
			AssertInvalid( "{\"version\": 2, \"emitters\": []}", "$.version", "bad version" );
			AssertInvalid( "{\"emitters\": []}", "$.version", "missing version" );
			AssertInvalid( "{\"version\": 1, \"emitters\": [{\"preset\": \"nope\"}]}", "$.emitters[0].preset", "unknown preset" );
			AssertInvalid( "{\"version\": 1, \"emitters\": [{\"shape\": \"Cone\"}]}", "$.emitters[0].shape", "unknown shape" );
			AssertInvalid( "{\"version\": 1, \"settings\": {\"renderType\": \"Points\"}, \"emitters\": []}", "$.settings.renderType", "unknown render type" );
			AssertInvalid( "{\"version\": 1, \"settings\": {\"capacity\": 0}, \"emitters\": []}", "$.settings.capacity", "out-of-range capacity" );
			AssertInvalid( "{\"version\": 1, \"emitters\": [{\"spreadDegrees\": 200}]}", "$.emitters[0].spreadDegrees", "out-of-range spread" );
			AssertInvalid( "{\"version\": 1, \"settings\": {\"capacity\": \"500\"}, \"emitters\": []}", "$.settings.capacity", "wrong type capacity" );
			AssertInvalid( "{\"version\": 1, \"emitters\": [{\"rate\": \"5\"}]}", "$.emitters[0].rate", "wrong type rate" );
			AssertInvalid( "{", "$", "malformed json" );
			AssertInvalid( "[1, 2]", "$", "top-level array" );
			AssertInvalid( "{\"version\": 1, \"settings\": null, \"emitters\": []}", "$.settings", "settings null" );
			AssertInvalid( "{\"version\": 1, \"emitters\": null}", "$.emitters", "emitters null" );
			AssertInvalid( "{\"version\": 1, \"emitters\": [{\"shape\": \"MeshSurface\"}]}", "$.emitters[0].mesh", "mesh surface without mesh" );
			AssertInvalid( "{\"version\": 1, \"emitters\": [{\"shape\": \"MeshSurface\", \"mesh\": {\"vertices\": [[0, 0, 0], [0, 0, 0], [0, 0, 0]], \"indices\": [0, 1, 2]}}]}", "$.emitters[0].mesh", "degenerate mesh" );
			AssertInvalid( "{\"version\": 1, \"emitters\": [{\"direction\": [0, 0, 0], \"speed\": 5}]}", "$.emitters[0].direction", "zero direction" );
			AssertInvalid( "{\"version\": 1, \"emitters\": [{\"lifetimeMin\": 2, \"lifetimeMax\": 1}]}", "$.emitters[0].lifetimeMax", "lifetime max below min" );
		} );

		Check( "one bad document yields many structured errors, not one bit", () =>
		{
			var errors = ParticleSchema.Validate(
				"{\"version\": 1, \"settings\": {\"capacity\": 0}, \"emitters\": [{\"preset\": \"nope\", \"shape\": \"Cone\"}]}" );
			Assert( errors.Count >= 3, $"expected >= 3 errors, got {errors.Count}: {string.Join( "; ", errors )}" );
			Assert( errors.Any( e => e.Path.Contains( "capacity" ) ), "missing capacity error" );
			Assert( errors.Any( e => e.Path.Contains( "preset" ) ), "missing preset error" );
			Assert( errors.Any( e => e.Path.Contains( "shape" ) ), "missing shape error" );
			foreach ( var e in errors )
			{
				Assert( !string.IsNullOrWhiteSpace( e.Path ), "error with empty path" );
				Assert( !string.IsNullOrWhiteSpace( e.Reason ), "error with empty reason" );
				Assert( e.ToString().Contains( e.Path ), "error ToString must carry its path" );
			}
		} );
	}
}
