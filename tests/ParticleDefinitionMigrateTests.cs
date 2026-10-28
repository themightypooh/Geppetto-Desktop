using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Marionette.Desktop;

internal static class ParticleDefinitionMigrateTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		string work = settings.WorkDirectory;
		void Check( string name, Action test ) => registry.Add( "particle-definition-migrate", name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void ThrowsArg( Action action, string label )
		{
			try { action(); }
			catch ( ArgumentException ) { return; }
			catch ( Exception e ) { throw new Exception( $"expected ArgumentException for {label}, got {e.GetType().Name}: {e.Message}" ); }
			throw new Exception( $"expected an error for {label}, got silent success" );
		}
		JsonElement Parse( string json )
		{
			using var doc = JsonDocument.Parse( json );
			return doc.RootElement.Clone();
		}
		void AssertRaw( JsonElement element, string expectedRaw, string label )
		{
			// Valid for scalars only: composites are re-indented by the
			// migrator's serializer (whitespace only), so compare them
			// with AssertDeepEqual instead.
			Assert( element.GetRawText() == expectedRaw,
				$"{label}: raw text '{element.GetRawText()}' != expected '{expectedRaw}'" );
		}
		void AssertDeepEqual( JsonElement a, JsonElement b, string path )
		{
			Assert( a.ValueKind == b.ValueKind, $"{path}: kind {a.ValueKind} != {b.ValueKind}" );
			if ( a.ValueKind == JsonValueKind.Object )
			{
				var pa = new List<JsonProperty>();
				var pb = new List<JsonProperty>();
				foreach ( var p in a.EnumerateObject() ) pa.Add( p );
				foreach ( var p in b.EnumerateObject() ) pb.Add( p );
				Assert( pa.Count == pb.Count, $"{path}: key count {pa.Count} != {pb.Count}" );
				for ( int i = 0; i < pa.Count; i++ )
				{
					Assert( pa[i].Name == pb[i].Name, $"{path}: key order/name [{i}] '{pa[i].Name}' != '{pb[i].Name}'" );
					AssertDeepEqual( pa[i].Value, pb[i].Value, path + "." + pa[i].Name );
				}
				return;
			}
			if ( a.ValueKind == JsonValueKind.Array )
			{
				Assert( a.GetArrayLength() == b.GetArrayLength(), $"{path}: array length differs" );
				for ( int i = 0; i < a.GetArrayLength(); i++ ) AssertDeepEqual( a[i], b[i], $"{path}[{i}]" );
				return;
			}
			Assert( a.GetRawText() == b.GetRawText(), $"{path}: value '{a.GetRawText()}' != '{b.GetRawText()}'" );
		}
		int Alive( ParticleSystem system )
		{
			int n = 0;
			foreach ( var s in system.States ) if ( s.Alive != 0 ) n++;
			return n;
		}

		// Full v1 sample: every lane-5 field family + unknown fields at
		// every level + inline mesh geometry, with awkward-but-legal
		// number spellings whose raw text must survive migration.
		string V1Sample() =>
			"{\"version\": 1, \"name\": \"ember-fountain\", \"futureTop\": {\"nested\": [1, 2.50, null]}, "
			+ "\"settings\": {\"capacity\": 4000, \"seed\": 7, \"fixedDeltaTime\": 0.0166667, "
			+ "\"gravity\": [0, 0, -386.0886], \"drag\": 0.4, \"groundEnabled\": true, \"groundOffset\": 0, "
			+ "\"restitution\": 0.35, \"renderType\": \"Billboard\", \"strandPoints\": 0, \"futureSetting\": \"keep-me\", "
			+ "\"collisionMesh\": {\"vertices\": [[0, 0, 0], [10, 0, 0], [0, 10, 0]], \"indices\": [0, 1, 2], \"note\": \"mesh-unknown\"}}, "
			+ "\"emitters\": ["
			+ "{\"name\": \"main jet\", \"preset\": \"sparks\", \"position\": [0, 0, 10], \"futureEmitter\": [true, null]}, "
			+ "{\"shape\": \"Box\", \"position\": [0, 0, 0], \"boxHalfExtents\": [30, 30, 10], \"rate\": 12, "
			+ "\"lifetimeMin\": 3, \"lifetimeMax\": 6, \"sizeMin\": 0.2, \"sizeMax\": 0.6, \"speed\": 5, "
			+ "\"bursts\": [{\"step\": 0, \"count\": 25}, {\"step\": 30, \"count\": 25}]}, "
			+ "{\"shape\": \"MeshSurface\", \"mesh\": {\"vertices\": [[0, 0, 0], [10, 0, 0], [0, 10, 0]], \"indices\": [0, 1, 2]}}, "
			+ "{\"shape\": \"Sphere\", \"sphereRadius\": 4, \"rate\": 3}]}";

		Check( "v1 to v2 preserves every v1 field value exactly", () =>
		{
			var root = Parse( ParticleDefinitionMigrate.Migrate( V1Sample() ) );
			Assert( root.GetProperty( "version" ).GetInt32() == 2, "version not upgraded to 2" );
			Assert( root.GetProperty( "migratedFrom" ).GetInt32() == 1, "migratedFrom is not 1" );
			Assert( root.GetProperty( "name" ).GetString() == "ember-fountain", "name changed" );
			AssertDeepEqual( root.GetProperty( "futureTop" ), Parse( "{\"nested\": [1, 2.50, null]}" ), "unknown top-level field" );
			var settings = root.GetProperty( "settings" );
			AssertRaw( settings.GetProperty( "capacity" ), "4000", "settings.capacity" );
			AssertRaw( settings.GetProperty( "seed" ), "7", "settings.seed" );
			AssertRaw( settings.GetProperty( "fixedDeltaTime" ), "0.0166667", "settings.fixedDeltaTime" );
			AssertDeepEqual( settings.GetProperty( "gravity" ), Parse( "[0, 0, -386.0886]" ), "settings.gravity" );
			AssertRaw( settings.GetProperty( "drag" ), "0.4", "settings.drag" );
			Assert( settings.GetProperty( "groundEnabled" ).GetBoolean(), "settings.groundEnabled changed" );
			AssertRaw( settings.GetProperty( "groundOffset" ), "0", "settings.groundOffset" );
			AssertRaw( settings.GetProperty( "restitution" ), "0.35", "settings.restitution" );
			Assert( settings.GetProperty( "renderType" ).GetString() == "Billboard", "settings.renderType changed" );
			AssertRaw( settings.GetProperty( "strandPoints" ), "0", "settings.strandPoints" );
			Assert( settings.GetProperty( "futureSetting" ).GetString() == "keep-me", "unknown settings field dropped" );
			var mesh = settings.GetProperty( "collisionMesh" );
			AssertDeepEqual( mesh.GetProperty( "vertices" ), Parse( "[[0, 0, 0], [10, 0, 0], [0, 10, 0]]" ), "settings collision mesh vertices" );
			AssertDeepEqual( mesh.GetProperty( "indices" ), Parse( "[0, 1, 2]" ), "settings collision mesh indices" );
			Assert( mesh.GetProperty( "note" ).GetString() == "mesh-unknown", "unknown mesh-nested field dropped" );
			var emitters = root.GetProperty( "emitters" );
			Assert( emitters.GetArrayLength() == 4, "emitter count changed" );
			Assert( emitters[0].GetProperty( "preset" ).GetString() == "sparks", "emitter 0 preset lost" );
			AssertDeepEqual( emitters[0].GetProperty( "position" ), Parse( "[0, 0, 10]" ), "emitter 0 position" );
			AssertDeepEqual( emitters[0].GetProperty( "futureEmitter" ), Parse( "[true, null]" ), "unknown emitter field" );
			AssertDeepEqual( emitters[1].GetProperty( "boxHalfExtents" ), Parse( "[30, 30, 10]" ), "emitter 1 boxHalfExtents" );
			AssertDeepEqual( emitters[1].GetProperty( "bursts" ), Parse( "[{\"step\": 0, \"count\": 25}, {\"step\": 30, \"count\": 25}]" ), "emitter 1 bursts" );
			AssertRaw( emitters[1].GetProperty( "sizeMax" ), "0.6", "emitter 1 sizeMax" );
			AssertDeepEqual( emitters[2].GetProperty( "mesh" ), Parse( "{\"vertices\": [[0, 0, 0], [10, 0, 0], [0, 10, 0]], \"indices\": [0, 1, 2]}" ), "emitter 2 inline mesh" );
			AssertRaw( emitters[3].GetProperty( "sphereRadius" ), "4", "emitter 3 sphereRadius" );
			AssertRaw( emitters[3].GetProperty( "rate" ), "3", "emitter 3 rate" );
		} );

		Check( "v1 to v2 label rules: name wins, preset fallback, neither omitted, existing kept", () =>
		{
			var root = Parse( ParticleDefinitionMigrate.Migrate( V1Sample() ) );
			var emitters = root.GetProperty( "emitters" );
			Assert( emitters[0].GetProperty( "label" ).GetString() == "main jet", "label did not take name over preset" );
			Assert( emitters[1].TryGetProperty( "label", out _ ) == false, "label invented for a nameless, preset-less emitter" );
			Assert( emitters[2].TryGetProperty( "label", out _ ) == false, "label invented for mesh emitter" );
			Assert( emitters[3].TryGetProperty( "label", out _ ) == false, "label invented for sphere emitter" );
			var presetOnly = Parse( ParticleDefinitionMigrate.Migrate(
				"{\"version\": 1, \"emitters\": [{\"preset\": \"sparks\", \"position\": [0, 0, 10]}]}" ) );
			Assert( presetOnly.GetProperty( "emitters" )[0].GetProperty( "label" ).GetString() == "sparks",
				"label did not fall back to preset" );
			var kept = Parse( ParticleDefinitionMigrate.Migrate(
				"{\"version\": 1, \"emitters\": [{\"name\": \"real name\", \"label\": \"hand label\", \"preset\": \"sparks\"}]}" ) );
			Assert( kept.GetProperty( "emitters" )[0].GetProperty( "label" ).GetString() == "hand label",
				"existing label overwritten by name" );
			var stray = Parse( ParticleDefinitionMigrate.Migrate(
				"{\"version\": 1, \"migratedFrom\": 99, \"emitters\": []}" ) );
			Assert( stray.GetProperty( "migratedFrom" ).GetInt32() == 1,
				"stray v1 migratedFrom not corrected to 1" );
		} );

		Check( "v2 idempotent byte-for-byte on migrator output, twice equals once", () =>
		{
			string once = ParticleDefinitionMigrate.Migrate( V1Sample() );
			string twice = ParticleDefinitionMigrate.Migrate( once );
			Assert( twice == once, "migrating a v2 document changed its bytes" );
			string thrice = ParticleDefinitionMigrate.Migrate( twice );
			Assert( thrice == once, "migrate twice != migrate once" );
		} );

		Check( "v2 compact input is semantically unchanged, unicode kept literal", () =>
		{
			string compact = "{\"version\":2,\"migratedFrom\":2,\"name\":\"café ☃\",\"emitters\":[{\"label\":\"déjà\",\"rate\":12}]}";
			string migrated = ParticleDefinitionMigrate.Migrate( compact );
			AssertDeepEqual( Parse( compact ), Parse( migrated ), "compact v2" );
			Assert( migrated.Contains( "café ☃", StringComparison.Ordinal ), "unicode name was escaped instead of kept literal" );
			Assert( migrated.Contains( "\"déjà\"", StringComparison.Ordinal ), "unicode label was escaped instead of kept literal" );
			string remigrated = ParticleDefinitionMigrate.Migrate( migrated );
			Assert( remigrated == migrated, "indented v2 output was not byte-stable" );
		} );

		Check( "native v2 with migratedFrom 2 and no labels is valid and unchanged", () =>
		{
			string v2 = ParticleDefinitionMigrate.Migrate(
				"{\"version\": 2, \"migratedFrom\": 2, \"name\": \"native\", \"emitters\": [{\"shape\": \"Box\"}]}" );
			var root = Parse( v2 );
			Assert( root.GetProperty( "version" ).GetInt32() == 2, "native v2 version changed" );
			Assert( root.GetProperty( "migratedFrom" ).GetInt32() == 2, "native v2 migratedFrom changed" );
			Assert( root.GetProperty( "emitters" )[0].TryGetProperty( "label", out _ ) == false,
				"label invented for a native v2 emitter" );
		} );

		Check( "unknown fields survive at every depth, including nulls and arrays", () =>
		{
			string v1 = "{\"version\": 1, \"zzz\": {\"deep\": {\"list\": [{\"a\": null}, 2.50, \"x\"]}}, "
				+ "\"settings\": {\"odd\": {\"inner\": [1, {\"b\": false}]}}, "
				+ "\"emitters\": [{\"shape\": \"Box\", \"odd\": {\"x\": 1}}]}";
			var root = Parse( ParticleDefinitionMigrate.Migrate( v1 ) );
			AssertDeepEqual( root.GetProperty( "zzz" ), Parse( "{\"deep\": {\"list\": [{\"a\": null}, 2.50, \"x\"]}}" ), "deep unknown top-level" );
			AssertDeepEqual( root.GetProperty( "settings" ).GetProperty( "odd" ), Parse( "{\"inner\": [1, {\"b\": false}]}" ), "unknown settings subtree" );
			AssertDeepEqual( root.GetProperty( "emitters" )[0].GetProperty( "odd" ), Parse( "{\"x\": 1}" ), "unknown emitter subtree" );
		} );

		Check( "version 0 fails loudly", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 0, \"emitters\": []}" ), "version 0" );
		} );

		Check( "version 3 fails loudly as newer-than-known", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 3, \"emitters\": []}" ), "version 3" );
		} );

		Check( "version 99 fails loudly as newer-than-known", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 99, \"emitters\": []}" ), "version 99" );
		} );

		Check( "missing version fails loudly", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"name\": \"no version\", \"emitters\": []}" ), "missing version" );
		} );

		Check( "malformed JSON fails loudly", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{" ), "truncated json" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 1} garbage" ), "trailing garbage" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "" ), "empty json" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "   " ), "whitespace json" );
		} );

		Check( "non-object top level fails loudly", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "[1, 2]" ), "top-level array" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "\"just a string\"" ), "top-level string" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "42" ), "top-level number" );
		} );

		Check( "non-integer version fails loudly, never coerced", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": \"1\", \"emitters\": []}" ), "string version" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 1.5, \"emitters\": []}" ), "float version" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 1.0, \"emitters\": []}" ), "whole-float version" );
		} );

		Check( "v2 missing migratedFrom fails loudly", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 2, \"emitters\": []}" ), "v2 without migratedFrom" );
		} );

		Check( "v2 bad migratedFrom fails loudly", () =>
		{
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 2, \"migratedFrom\": 0, \"emitters\": []}" ), "migratedFrom 0" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 2, \"migratedFrom\": 3, \"emitters\": []}" ), "migratedFrom 3" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 2, \"migratedFrom\": \"1\", \"emitters\": []}" ), "migratedFrom string" );
			ThrowsArg( () => ParticleDefinitionMigrate.Migrate( "{\"version\": 2, \"migratedFrom\": 1.5, \"emitters\": []}" ), "migratedFrom float" );
		} );

		Check( "lane 5 Build rejects v2 while the v1 original builds (verdict, not a faked build)", () =>
		{
			string v1 = "{\"version\": 1, \"settings\": {\"capacity\": 500, \"seed\": 42}, "
				+ "\"emitters\": [{\"preset\": \"sparks\", \"position\": [0, 0, 10]}]}";
			var control = ParticleAuthoring.FromJson( v1 );
			var system = control.Build();
			system.Step( 1 );
			Assert( Alive( system ) > 0, "control v1 system emitted nothing" );
			string v2 = ParticleDefinitionMigrate.Migrate( v1 );
			Assert( Parse( v2 ).GetProperty( "version" ).GetInt32() == 2, "migration did not produce v2" );
			ThrowsArg( () => ParticleAuthoring.FromJson( v2 ), "lane 5 FromJson accepting v2" );
			ThrowsArg( () => new ParticleSystemDef { Version = 2 }.Build(), "lane 5 Build accepting version 2" );
		} );

		Check( "MigrateFile round-trips through files and nothing else", () =>
		{
			string input = Path.Combine( work, "p50-in.json" );
			string output = Path.Combine( work, "p50-out.json" );
			File.WriteAllText( input, V1Sample() );
			ParticleDefinitionMigrate.MigrateFile( input, output );
			string fromFile = File.ReadAllText( output );
			Assert( fromFile == ParticleDefinitionMigrate.Migrate( V1Sample() ), "MigrateFile output differs from Migrate output" );
			Assert( Parse( fromFile ).GetProperty( "version" ).GetInt32() == 2, "MigrateFile output is not v2" );
			ThrowsArg( () => ParticleDefinitionMigrate.MigrateFile( Path.Combine( work, "p50-missing.json" ), output ),
				"missing input file" );
		} );

		Check( "version constants are exactly first 1, current 2", () =>
		{
			Assert( ParticleDefinitionMigrate.FirstVersion == 1, $"FirstVersion is {ParticleDefinitionMigrate.FirstVersion}, expected 1" );
			Assert( ParticleDefinitionMigrate.CurrentVersion == 2, $"CurrentVersion is {ParticleDefinitionMigrate.CurrentVersion}, expected 2" );
		} );
	}
}
