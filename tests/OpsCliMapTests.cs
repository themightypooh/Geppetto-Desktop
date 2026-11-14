#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

internal static class OpsCliMapTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "opsclimap", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string? contains = null ) where T : Exception
		{
			try { action(); }
			catch ( T ex )
			{
				if ( contains is not null ) Require( ex.Message.Contains( contains, StringComparison.Ordinal ), $"error '{ex.Message}' must mention '{contains}'" );
				return;
			}
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}
		string Work( string name )
		{
			string dir = Path.Combine( settings.WorkDirectory, "opsclimap", name );
			Directory.CreateDirectory( dir );
			return dir;
		}
		string CaptureOut( Func<int> run )
		{
			var writer = new StringWriter();
			var saved = Console.Out;
			Console.SetOut( writer );
			try { run(); }
			finally { Console.SetOut( saved ); }
			return writer.ToString();
		}
		(int Code, JsonElement Json) RunJson( string[] args )
		{
			int code = 0;
			string output = CaptureOut( () => code = OpsCli.Run( args ) );
			using var parsed = JsonDocument.Parse( output );
			return (code, parsed.RootElement.Clone());
		}
		string Fixture( string name )
		{
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, "fixtures", "hammer", "hammer-ai-ops-cli-map-commands", name ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", "hammer-ai-ops-cli-map-commands", name ) ),
				Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", "hammer-ai-ops-cli-map-commands", name ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return candidate;
			throw new Exception( "opsclimap fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
		}
		bool HasIssue( JsonElement json, string code ) =>
			json.GetProperty( "issues" ).EnumerateArray()
				.Any( i => i.GetProperty( "code" ).GetString() == code );

		Check( "map help lists all five subcommands", () =>
		{
			string output = CaptureOut( () => OpsCli.Run( new[] { "map", "--help" } ) );
			foreach ( var sub in new[] { "build", "validate", "compile", "export", "issues" } )
				Require( output.Contains( "map " + sub, StringComparison.Ordinal ), $"map help must list {sub}" );
			Require( output.Contains( "QuickPlay", StringComparison.Ordinal ), "map help must name the QuickPlay bake" );
		} );

		Check( "map with no subcommand shows help and exits 0", () =>
		{
			int code = 0;
			string output = CaptureOut( () => code = OpsCli.Run( new[] { "map" } ) );
			Require( code == 0, $"no-arg map must exit 0, got {code}" );
			Require( output.Contains( "ops map", StringComparison.Ordinal ), "no-arg map must print usage" );
		} );

		Check( "unknown map subcommand throws", () =>
		{
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "frobnicate" } ), "Unknown ops map command" );
		} );

		Check( "unknown map options throw per subcommand", () =>
		{
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "validate", "x.vmf", "--nope" } ), "Unknown map validate option" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "issues", "x.vmf", "--nope" } ), "Unknown map issues option" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "compile", "x.vmf", "--nope" } ), "Unknown map compile option" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "export", "x.vmf", "--nope" } ), "Unknown map export option" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "build", "x.vmf", "--nope" } ), "Unknown map build option" );
		} );

		Check( "LoadMap rejects unknown extension and missing file", () =>
		{
			Throws<ArgumentException>( () => OpsCli.LoadMap( Path.Combine( Work( "loadmap" ), "x.effigy" ) ), "Map must be" );
			Throws<FileNotFoundException>( () => OpsCli.LoadMap( Path.Combine( Work( "loadmap" ), "missing.vmf" ) ) );
		} );

		Check( "LoadMap loads sealed room vmf and map with the same counts", () =>
		{
			var vmf = OpsCli.LoadMap( Fixture( "sealed-room.vmf" ) );
			var map = OpsCli.LoadMap( Fixture( "sealed-room.map" ) );
			Require( vmf.WorldBrushes.Count == 6, $"vmf must have 6 world brushes, got {vmf.WorldBrushes.Count}" );
			Require( map.WorldBrushes.Count == 6, $"map must have 6 world brushes, got {map.WorldBrushes.Count}" );
			Require( vmf.Entities.Count == 2, $"vmf must have 2 entities, got {vmf.Entities.Count}" );
			Require( map.Entities.Count == 2, $"map must have 2 entities, got {map.Entities.Count}" );
			Require( OpsCli.PlayerStartOf( vmf ) is not null, "vmf must have a player start" );
			Require( OpsCli.PlayerStartOf( map ) is not null, "map must have a player start" );
		} );

		Check( "projections agree on the sealed room", () =>
		{
			var doc = OpsCli.LoadMap( Fixture( "sealed-room.vmf" ) );
			var issues = OpsCli.ToIssueDocument( doc );
			Require( issues.Brushes.Count == 6, $"issue doc must have 6 brushes, got {issues.Brushes.Count}" );
			Require( issues.Entities.Count == 2, $"issue doc must have 2 entities, got {issues.Entities.Count}" );
			var compile = OpsCli.ToCompileInput( doc );
			Require( compile.Brushes.Count == 6, $"compile input must have 6 brushes, got {compile.Brushes.Count}" );
			Require( compile.PlayerStart is not null, "compile input must have a player start" );
			Require( compile.Lights.Count == 1, $"compile input must have 1 light, got {compile.Lights.Count}" );
			var light = compile.Lights[0];
			Require( Math.Abs( light.Intensity - 400f ) < 1e-3f,
				$"compile light intensity must be the _light 4th component (400), got {light.Intensity}" );
			Require( Math.Abs( light.Color.x - 1f ) < 1e-3f && Math.Abs( light.Color.y - 1f ) < 1e-3f
					&& Math.Abs( light.Color.z - 220f / 255f ) < 1e-3f,
				$"compile light colour must be _light rgb/255 (255 255 220), got {light.Color}" );
			var quick = OpsCli.ToQuickPlayMap( doc );
			Require( quick.Brushes.Count == 6, $"quickplay map must have 6 brushes, got {quick.Brushes.Count}" );
			Require( quick.PlayerStart is not null, "quickplay map must have a player start" );
			var floor = quick.Brushes[0];
			Require( Math.Abs( floor.Min.z - ( -16 ) ) < 1e-3f && Math.Abs( floor.Max.z - 0 ) < 1e-3f,
				$"first brush must be the floor (-16..0), got {floor.Min.z}..{floor.Max.z}" );
		} );

		Check( "ToQuickPlayMap rejects a non-axis-aligned brush", () =>
		{
			var doc = new VmfDocument();
			var brush = new VmfBrush { Id = 1 };
			// Six faces, but the sixth plane is diagonal: its derived normal is
			// (0.577, 0.577, 0.577), not an axis normal, so this is not a box.
			VmfFace Face( VmfVec a, VmfVec b, VmfVec c ) => new VmfFace
			{
				PlanePoints = new[] { a, b, c },
				Material = "BRICK/BRICKWALL001A",
			};
			brush.Faces.AddRange( new[]
			{
				Face( new VmfVec( 144, -144, -16 ), new VmfVec( 144, 144, -16 ), new VmfVec( 144, 144, 0 ) ),
				Face( new VmfVec( -144, 144, -16 ), new VmfVec( -144, -144, -16 ), new VmfVec( -144, -144, 0 ) ),
				Face( new VmfVec( 144, 144, -16 ), new VmfVec( -144, 144, -16 ), new VmfVec( -144, 144, 0 ) ),
				Face( new VmfVec( -144, -144, -16 ), new VmfVec( 144, -144, -16 ), new VmfVec( 144, -144, 0 ) ),
				Face( new VmfVec( -144, -144, 144 ), new VmfVec( 144, -144, 144 ), new VmfVec( 144, 144, 144 ) ),
				Face( new VmfVec( 100, 0, 0 ), new VmfVec( 0, 100, 0 ), new VmfVec( 0, 0, 100 ) ),
			} );
			doc.WorldBrushes.Add( brush );
			Throws<OpsCli.MapCommandException>( () => OpsCli.ToQuickPlayMap( doc ), "non-axis-aligned-brush" );
		} );

		Check( "validate sealed room exits 0 with status passed", () =>
		{
			var (code, json) = RunJson( new[] { "map", "validate", Fixture( "sealed-room.vmf" ) } );
			Require( code == 0, $"sealed validate must exit 0, got {code}: {json}" );
			Require( json.GetProperty( "status" ).GetString() == "passed", "sealed validate status must be passed" );
			Require( json.GetProperty( "errors" ).GetInt32() == 0, "sealed validate must report 0 errors" );
		} );

		Check( "validate leaking room exits 1 with a LEAK error", () =>
		{
			var (code, json) = RunJson( new[] { "map", "validate", Fixture( "leaking-room.vmf" ) } );
			Require( code == 1, $"leaking validate must exit 1, got {code}: {json}" );
			Require( json.GetProperty( "status" ).GetString() == "failed", "leaking validate status must be failed" );
			Require( HasIssue( json, "LEAK" ), "leaking validate must name the LEAK issue" );
			var leak = json.GetProperty( "issues" ).EnumerateArray().First( i => i.GetProperty( "code" ).GetString() == "LEAK" );
			Require( leak.GetProperty( "severity" ).GetString() == "Error", "LEAK must be error severity" );
		} );

		Check( "issues lists the leak but exits 0", () =>
		{
			var (sealedCode, sealedJson) = RunJson( new[] { "map", "issues", Fixture( "sealed-room.vmf" ) } );
			Require( sealedCode == 0, $"sealed issues must exit 0, got {sealedCode}" );
			Require( sealedJson.GetProperty( "count" ).GetInt32() == 0, "sealed issues must list 0 issues" );
			var (leakCode, leakJson) = RunJson( new[] { "map", "issues", Fixture( "leaking-room.vmf" ) } );
			Require( leakCode == 0, "issues is a listing, not a gate: leaking issues must still exit 0" );
			Require( leakJson.GetProperty( "count" ).GetInt32() >= 1, "leaking issues must list at least the leak" );
			Require( HasIssue( leakJson, "LEAK" ), "leaking issues must name the LEAK issue" );
		} );

		Check( "validate sealed-room.map exits 0", () =>
		{
			var (code, json) = RunJson( new[] { "map", "validate", Fixture( "sealed-room.map" ) } );
			Require( code == 0, $"sealed .map validate must exit 0, got {code}: {json}" );
			Require( json.GetProperty( "status" ).GetString() == "passed", "sealed .map validate status must be passed" );
		} );

		Check( "build sealed room exits 0 sealed spawn-ok walk-clear", () =>
		{
			var (code, json) = RunJson( new[] { "map", "build", Fixture( "sealed-room.vmf" ) } );
			Require( code == 0, $"sealed build must exit 0, got {code}: {json}" );
			Require( json.GetProperty( "sealedMap" ).GetBoolean(), "sealed build must report sealed" );
			Require( json.GetProperty( "spawn" ).GetProperty( "ok" ).GetBoolean(), "sealed build spawn must be ok" );
			Require( !json.GetProperty( "walk" ).GetProperty( "blocked" ).GetBoolean(), "sealed build walk must be unblocked" );
			Require( json.GetProperty( "leaves" ).GetProperty( "empty" ).GetInt32() == 1,
				"sealed room bakes to exactly one empty leaf" );
			Require( json.GetProperty( "leaves" ).GetProperty( "portals" ).GetInt32() == 0,
				"sealed room bakes to zero portals" );
		} );

		Check( "build leaking room exits 1 with error leak", () =>
		{
			var (code, json) = RunJson( new[] { "map", "build", Fixture( "leaking-room.vmf" ) } );
			Require( code == 1, $"leaking build must exit 1, got {code}: {json}" );
			Require( json.GetProperty( "error" ).GetProperty( "code" ).GetString() == "leak",
				"leaking build must name the leak error" );
			Require( !json.GetProperty( "sealedMap" ).GetBoolean(), "leaking build must report unsealed" );
		} );

		Check( "build no-spawn room exits 1 with error no-player-start", () =>
		{
			var (code, json) = RunJson( new[] { "map", "build", Fixture( "no-spawn-room.vmf" ) } );
			Require( code == 1, $"no-spawn build must exit 1, got {code}: {json}" );
			Require( json.GetProperty( "error" ).GetProperty( "code" ).GetString() == "no-player-start",
				"no-spawn build must name the no-player-start error" );
		} );

		Check( "build empty script exits 0", () =>
		{
			string dir = Work( "build-script-empty" );
			string script = Path.Combine( dir, "empty.txt" );
			File.WriteAllText( script, "" );
			string runDir = Path.Combine( dir, "run" );
			int code = OpsCli.Run( new[] { "map", "build", script, "--run", runDir } );
			Require( code == 0, $"empty script build must exit 0, got {code}" );
		} );

		Check( "build bad-verb script exits 1", () =>
		{
			string dir = Work( "build-script-bad" );
			string script = Path.Combine( dir, "bad.txt" );
			File.WriteAllText( script, "frobnicate the thing\n" );
			string runDir = Path.Combine( dir, "run" );
			int code = OpsCli.Run( new[] { "map", "build", script, "--run", runDir } );
			Require( code == 1, $"bad-verb script build must exit 1, got {code}" );
		} );

		Check( "compile sealed room quick exits 0 and writes .bsp", () =>
		{
			string dir = Work( "compile-quick" );
			string outDir = Path.Combine( dir, "out" );
			var (code, json) = RunJson( new[] { "map", "compile", Fixture( "sealed-room.vmf" ), "--preset", "quick", "--out", outDir } );
			Require( code == 0, $"quick compile must exit 0, got {code}: {json}" );
			Require( json.GetProperty( "status" ).GetString() == "succeeded", "quick compile status must be succeeded" );
			Require( json.GetProperty( "preset" ).GetString() == "quick", "quick compile must report its preset" );
			Require( json.GetProperty( "bsp" ).GetProperty( "leaves" ).GetInt32() >= 1, "quick compile must build leaves" );
			string bsp = Path.Combine( outDir, "sealed-room.bsp" );
			Require( File.Exists( bsp ), ".bsp must be written to --out" );
			Require( json.GetProperty( "outputFiles" ).EnumerateArray().Any( f => f.GetString() == bsp ),
				"compile JSON must list the .bsp" );
		} );

		Check( "compile sealed room preview exits 0 and writes .pvs and .lightmap", () =>
		{
			string dir = Work( "compile-preview" );
			string outDir = Path.Combine( dir, "out" );
			var (code, json) = RunJson( new[] { "map", "compile", Fixture( "sealed-room.vmf" ), "--preset", "preview", "--out", outDir } );
			Require( code == 0, $"preview compile must exit 0, got {code}: {json}" );
			Require( json.GetProperty( "status" ).GetString() == "succeeded", "preview compile status must be succeeded" );
			Require( File.Exists( Path.Combine( outDir, "sealed-room.pvs" ) ), ".pvs must be written for preview" );
			Require( File.Exists( Path.Combine( outDir, "sealed-room.lightmap" ) ), ".lightmap must be written for preview" );
		} );

		Check( "compile sealed room final exits 0", () =>
		{
			string dir = Work( "compile-final" );
			var (code, json) = RunJson( new[] { "map", "compile", Fixture( "sealed-room.vmf" ), "--preset", "final", "--out", Path.Combine( dir, "out" ) } );
			Require( code == 0, $"final compile must exit 0, got {code}: {json}" );
			Require( json.GetProperty( "status" ).GetString() == "succeeded", "final compile status must be succeeded" );
			Require( json.GetProperty( "lightmaps" ).GetProperty( "resolution" ).GetInt32() == 16,
				"final compile must bake full-res lightmaps" );
		} );

		Check( "compile no-spawn room exits 1 with missing-player-start", () =>
		{
			var (code, json) = RunJson( new[] { "map", "compile", Fixture( "no-spawn-room.vmf" ), "--preset", "quick" } );
			Require( code == 1, $"no-spawn compile must exit 1, got {code}: {json}" );
			Require( json.GetProperty( "error" ).GetProperty( "code" ).GetString() == "missing-player-start",
				"no-spawn compile must name the missing-player-start error" );
		} );

		Check( "compile leaking room exits 1 with error leak", () =>
		{
			var (code, json) = RunJson( new[] { "map", "compile", Fixture( "leaking-room.vmf" ), "--preset", "quick" } );
			Require( code == 1, $"leaking compile must exit 1, got {code}: {json}" );
			Require( json.GetProperty( "error" ).GetProperty( "code" ).GetString() == "leak",
				"leaking compile must name the leak error" );
		} );

		Check( "compile requires --preset and rejects unknown presets", () =>
		{
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "compile", Fixture( "sealed-room.vmf" ) } ), "--preset" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "compile", Fixture( "sealed-room.vmf" ), "--preset", "ultra" } ), "Unknown --preset" );
		} );

		Check( "export vmf round-trips", () =>
		{
			string dir = Work( "export-vmf" );
			string outPath = Path.Combine( dir, "roundtrip.vmf" );
			var (code, json) = RunJson( new[] { "map", "export", Fixture( "sealed-room.vmf" ), "--format", "vmf", "--out", outPath } );
			Require( code == 0, $"vmf export must exit 0, got {code}: {json}" );
			Require( File.Exists( outPath ), "vmf export must write the file" );
			var roundtrip = OpsCli.LoadMap( outPath );
			Require( roundtrip.WorldBrushes.Count == 6, $"round-tripped vmf must have 6 brushes, got {roundtrip.WorldBrushes.Count}" );
			Require( roundtrip.Entities.Count == 2, $"round-tripped vmf must have 2 entities, got {roundtrip.Entities.Count}" );
		} );

		Check( "export map round-trips", () =>
		{
			string dir = Work( "export-map" );
			string outPath = Path.Combine( dir, "roundtrip.map" );
			var (code, json) = RunJson( new[] { "map", "export", Fixture( "sealed-room.vmf" ), "--format", "map", "--out", outPath } );
			Require( code == 0, $"map export must exit 0, got {code}: {json}" );
			Require( File.Exists( outPath ), "map export must write the file" );
			var roundtrip = Map220Io.ParseFile( outPath );
			Require( roundtrip.WorldBrushes.Count == 6, $"round-tripped map must have 6 brushes, got {roundtrip.WorldBrushes.Count}" );
			Require( roundtrip.Entities.Count == 2, $"round-tripped map must have 2 entities, got {roundtrip.Entities.Count}" );
		} );

		Check( "export obj writes .obj and .mtl", () =>
		{
			string dir = Work( "export-obj" );
			string outPath = Path.Combine( dir, "room.obj" );
			var (code, json) = RunJson( new[] { "map", "export", Fixture( "sealed-room.vmf" ), "--format", "obj", "--out", outPath } );
			Require( code == 0, $"obj export must exit 0, got {code}: {json}" );
			Require( File.Exists( outPath ), "obj export must write the .obj" );
			Require( File.Exists( Path.Combine( dir, "room.mtl" ) ), "obj export must write the .mtl" );
			Require( new FileInfo( outPath ).Length > 0, ".obj must be non-empty" );
		} );

		Check( "export glb writes .glb and repeats the minimal-GLB note", () =>
		{
			string dir = Work( "export-glb" );
			string outPath = Path.Combine( dir, "room.glb" );
			var (code, json) = RunJson( new[] { "map", "export", Fixture( "sealed-room.vmf" ), "--format", "glb", "--out", outPath } );
			Require( code == 0, $"glb export must exit 0, got {code}: {json}" );
			Require( File.Exists( outPath ), "glb export must write the .glb" );
			string note = json.GetProperty( "note" ).GetString() ?? "";
			Require( note.Contains( "minimal GLB", StringComparison.Ordinal ),
				"glb export must repeat the minimal-GLB note, got: " + note );
		} );

		Check( "export refuses to overwrite the source and existing --out without --force", () =>
		{
			string dir = Work( "export-guards" );
			string source = Fixture( "sealed-room.vmf" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "export", source, "--format", "vmf", "--out", source } ), "--out must not overwrite" );
			string existing = Path.Combine( dir, "exists.vmf" );
			File.WriteAllText( existing, "sentinel" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "map", "export", source, "--format", "vmf", "--out", existing } ), "Pass --force" );
			var (code, _) = RunJson( new[] { "map", "export", source, "--format", "vmf", "--out", existing, "--force" } );
			Require( code == 0, $"--force export must exit 0, got {code}" );
			Require( File.ReadAllText( existing ).Contains( "versioninfo", StringComparison.Ordinal ),
				"--force must actually overwrite the existing file" );
		} );

		Check( "ParsePreset and ParseMapFormat accept the documented values and reject the rest", () =>
		{
			Require( OpsCli.ParsePreset( "quick" ) == CompilePreset.QuickPlay, "quick" );
			Require( OpsCli.ParsePreset( "preview" ) == CompilePreset.Preview, "preview" );
			Require( OpsCli.ParsePreset( "final" ) == CompilePreset.Final, "final" );
			Throws<ArgumentException>( () => OpsCli.ParsePreset( "ultra" ), "Unknown --preset" );
			Require( OpsCli.ParseMapFormat( "vmf" ) == OpsCli.MapExportFormat.Vmf, "vmf" );
			Require( OpsCli.ParseMapFormat( "map" ) == OpsCli.MapExportFormat.Map, "map" );
			Require( OpsCli.ParseMapFormat( "obj" ) == OpsCli.MapExportFormat.Obj, "obj" );
			Require( OpsCli.ParseMapFormat( "glb" ) == OpsCli.MapExportFormat.Glb, "glb" );
			Throws<ArgumentException>( () => OpsCli.ParseMapFormat( "fbx" ), "Unknown map --format" );
		} );
	}
}
