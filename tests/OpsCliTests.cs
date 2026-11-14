#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

internal static class OpsCliTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "ops-cli", name, test );
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
			string dir = Path.Combine( settings.WorkDirectory, "ops-cli", name );
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
		string Fixture( string name )
		{
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, "ops-cli-fixtures", name ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "ops-cli-fixtures", name ) ),
				Path.GetFullPath( Path.Combine( "tests", "ops-cli-fixtures", name ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return File.ReadAllText( candidate );
			throw new Exception( "ops-cli fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
		}
		PartStudio CubeStudio()
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f }, SizeY = { Value = 10f }, SizeZ = { Value = 10f } } );
			studio.Rebuild();
			Require( studio.Bodies.Count > 0, "cube studio must rebuild to at least one body" );
			return studio;
		}
		string SaveCube( string dir, string name = "cube.effigy" )
		{
			string path = Path.Combine( dir, name );
			StudioDocument.WriteFile( CubeStudio(), path );
			Require( File.Exists( path ), "cube .effigy must be written" );
			return path;
		}
		PolyMesh BrokenMesh()
		{
			// One duplicate face (same corners twice) + one degenerate face (repeated vertex).
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
			} );
			mesh.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			mesh.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			mesh.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			mesh.Faces.Add( new Face( new[] { 0, 1, 1 } ) );
			return mesh;
		}

		Check( "help lists all five commands", () =>
		{
			string output = CaptureOut( () => OpsCli.Run( new[] { "--help" } ) );
			Require( output == Fixture( "help.txt" ), "help output must match the pinned transcript tests/ops-cli-fixtures/help.txt" );
			foreach ( var command in new[] { "script", "check", "export", "render", "test" } )
				Require( output.Contains( command, StringComparison.Ordinal ), $"help must list {command}" );
		} );

		Check( "ops with no arguments shows help and exits 0", () =>
		{
			int code = 0;
			string output = CaptureOut( () => code = OpsCli.Run( Array.Empty<string>() ) );
			Require( code == 0, $"no-arg ops must exit 0, got {code}" );
			Require( output.Contains( "marionette-desktop ops", StringComparison.Ordinal ), "no-arg ops must print usage" );
		} );

		Check( "unknown command throws", () =>
		{
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "frobnicate" } ), "Unknown ops command" );
		} );

		Check( "unknown flags throw per command", () =>
		{
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "export", "x.effigy", "--nope" } ), "Unknown export option" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "render", "x.effigy", "--nope" } ), "Unknown render option" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "test", "--nope" } ), "Unknown test option" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "script", "x.txt", "--nope" } ), "Unknown script option" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "check", "x.effigy", "--nope" } ), "Unknown check option" );
		} );

		Check( "ParseFormat accepts the four targets", () =>
		{
			Require( OpsCli.ParseFormat( "obj" ) == ExportFormat.Obj, "obj" );
			Require( OpsCli.ParseFormat( "GLB" ) == ExportFormat.Glb, "format parse is case-insensitive" );
			Require( OpsCli.ParseFormat( "effigy" ) == ExportFormat.Effigy, "effigy" );
			Require( OpsCli.ParseFormat( "riganim" ) == ExportFormat.RigAnim, "riganim" );
		} );

		Check( "ParseFormat refuses vmdl/dmx/fbx by name", () =>
		{
			foreach ( var refused in new[] { "vmdl", "dmx", "fbx" } )
				Throws<ArgumentException>( () => OpsCli.ParseFormat( refused ), refused );
			Throws<ArgumentException>( () => OpsCli.ParseFormat( "step" ), "Supported:" );
		} );

		Check( "export refuses a bad format before loading the model", () =>
		{
			// The model does not exist; the error must still be about the format, proving
			// parse runs before load.
			Throws<ArgumentException>(
				() => OpsCli.Run( new[] { "export", "missing.effigy", "--format", "vmdl", "--out", "x.obj" } ), "vmdl" );
		} );

		Check( "check on a missing model throws", () =>
		{
			Throws<FileNotFoundException>( () => OpsCli.Run( new[] { "check", Path.Combine( Work( "missing" ), "missing.effigy" ) } ) );
		} );

		Check( "check passes a valid cube", () =>
		{
			string dir = Work( "check-pass" );
			string model = SaveCube( dir );
			int code = 0;
			string output = CaptureOut( () => code = OpsCli.Run( new[] { "check", model } ) );
			Require( code == 0, $"check on a valid cube must exit 0, got {code}: {output}" );
			using var doc = JsonDocument.Parse( output );
			var root = doc.RootElement;
			Require( root.GetProperty( "status" ).GetString() == "passed", "valid cube report must say passed" );
			Require( root.GetProperty( "bodies" ).GetInt32() > 0, "valid cube report must list bodies" );
			Require( root.GetProperty( "topology" ).GetProperty( "blocked" ).GetBoolean() == false, "valid cube topology must not block" );
			bool objCanExport = false;
			foreach ( var format in root.GetProperty( "export" ).EnumerateArray() )
				if ( format.GetProperty( "format" ).GetString() == "obj" ) objCanExport = format.GetProperty( "canExport" ).GetBoolean();
			Require( objCanExport, "valid cube must be exportable as obj" );
		} );

		Check( "check fails a deliberately broken mesh", () =>
		{
			var studio = CubeStudio();
			studio.Bodies.Add( new Body( "broken", "Broken", BrokenMesh() ) );
			var session = DocumentSession.CreateEffigy( Path.Combine( Work( "check-fail" ), "broken.effigy" ), studio );
			var meshes = new List<KeyValuePair<string, PolyMesh>>
			{
				new( "Broken", BrokenMesh() ),
			};
			var (json, blocked) = OpsCli.CheckReport( session, "broken.effigy", meshes );
			Require( blocked, "broken mesh must block the check" );
			using var doc = JsonDocument.Parse( json );
			var root = doc.RootElement;
			Require( root.GetProperty( "status" ).GetString() == "failed", "broken mesh report must say failed" );
			var body = root.GetProperty( "topology" ).GetProperty( "bodies" )[0];
			Require( body.GetProperty( "duplicateFaces" ).GetInt32() > 0, "duplicate face must be counted" );
			Require( body.GetProperty( "degenerateFaces" ).GetInt32() > 0, "degenerate face must be counted" );
			Require( body.GetProperty( "blocksExport" ).GetBoolean(), "broken mesh must block export" );
			Require( body.GetProperty( "hardFails" ).GetArrayLength() > 0, "broken mesh must name its hard fails" );
		} );

		Check( "check output matches the pinned transcript", () =>
		{
			string dir = Work( "golden-check" );
			string model = SaveCube( dir );
			int code = 0;
			string output = CaptureOut( () => code = OpsCli.Run( new[] { "check", model } ) );
			Require( code == 0, $"golden check run must exit 0, got {code}" );
			string tokenised = output.Replace( dir, "<WORKDIR>", StringComparison.Ordinal );
			Require( tokenised == Fixture( "check-pass.json" ),
				"check output must match the pinned transcript tests/ops-cli-fixtures/check-pass.json" );
		} );

		Check( "export output matches the pinned transcript", () =>
		{
			string dir = Work( "golden-export" );
			string model = SaveCube( dir );
			string outPath = Path.Combine( dir, "model.obj" );
			int code = 0;
			string output = CaptureOut( () => code = OpsCli.Run( new[] { "export", model, "--format", "obj", "--out", outPath } ) );
			Require( code == 0, $"golden export run must exit 0, got {code}" );
			string tokenised = output.Replace( dir, "<WORKDIR>", StringComparison.Ordinal );
			Require( tokenised == Fixture( "export-obj.txt" ),
				"export output must match the pinned transcript tests/ops-cli-fixtures/export-obj.txt" );
		} );

		Check( "broken check output matches the pinned transcript", () =>
		{
			string dir = Work( "golden-broken" );
			var studio = CubeStudio();
			studio.Bodies.Add( new Body( "broken", "Broken", BrokenMesh() ) );
			var session = DocumentSession.CreateEffigy( Path.Combine( dir, "broken.effigy" ), studio );
			var meshes = new List<KeyValuePair<string, PolyMesh>> { new( "Broken", BrokenMesh() ) };
			var (json, blocked) = OpsCli.CheckReport( session, Path.Combine( dir, "broken.effigy" ), meshes );
			Require( blocked, "broken golden fixture must block" );
			string tokenised = json.Replace( dir, "<WORKDIR>", StringComparison.Ordinal );
			Require( tokenised == Fixture( "check-broken.json" ),
				"broken check output must match the pinned transcript tests/ops-cli-fixtures/check-broken.json" );
		} );

		Check( "check fails a model with no geometry", () =>
		{
			var session = DocumentSession.CreateEffigy(
				Path.Combine( Work( "check-empty" ), "empty.effigy" ), new PartStudio() );
			var (json, blocked) = OpsCli.CheckReport( session, "empty.effigy", new List<KeyValuePair<string, PolyMesh>>() );
			Require( blocked, "a model with no meshes must block the check" );
			using var doc = JsonDocument.Parse( json );
			Require( doc.RootElement.GetProperty( "status" ).GetString() == "failed", "empty model report must say failed" );
		} );

		Check( "export writes a real OBJ file", () =>
		{
			string dir = Work( "export-obj" );
			string model = SaveCube( dir );
			string outPath = Path.Combine( dir, "model.obj" );
			int code = OpsCli.Run( new[] { "export", model, "--format", "obj", "--out", outPath } );
			Require( code == 0, $"obj export must exit 0, got {code}" );
			Require( File.Exists( outPath ), "obj export must write the file" );
			string obj = File.ReadAllText( outPath );
			Require( obj.Contains( "v ", StringComparison.Ordinal ), "obj export must contain vertices" );
			Require( obj.Contains( "f ", StringComparison.Ordinal ), "obj export must contain faces" );
			// Second run without --force refuses; with --force it overwrites.
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "export", model, "--format", "obj", "--out", outPath } ), "--force" );
			Require( OpsCli.Run( new[] { "export", model, "--format", "obj", "--out", outPath, "--force" } ) == 0,
				"obj export with --force must overwrite" );
		} );

		Check( "--allow-destructive is accepted as the --force alias", () =>
		{
			string dir = Work( "export-alias" );
			string model = SaveCube( dir );
			string outPath = Path.Combine( dir, "model.obj" );
			Require( OpsCli.Run( new[] { "export", model, "--format", "obj", "--out", outPath } ) == 0, "first export must succeed" );
			Require( OpsCli.Run( new[] { "export", model, "--format", "obj", "--out", outPath, "--allow-destructive" } ) == 0,
				"--allow-destructive must overwrite like --force" );
		} );

		Check( "export refuses to overwrite the source model", () =>
		{
			string dir = Work( "export-self" );
			string model = SaveCube( dir );
			Throws<ArgumentException>(
				() => OpsCli.Run( new[] { "export", model, "--format", "obj", "--out", model } ), "must not overwrite the source model" );
		} );

		Check( "script with a missing file throws", () =>
		{
			Throws<FileNotFoundException>( () => OpsCli.Run( new[] { "script", Path.Combine( Work( "script-missing" ), "missing.txt" ) } ) );
		} );

		Check( "empty script completes", () =>
		{
			string dir = Work( "script-empty" );
			string script = Path.Combine( dir, "empty.txt" );
			File.WriteAllText( script, "" );
			string runDir = Path.Combine( dir, "run" );
			int code = OpsCli.Run( new[] { "script", script, "--run", runDir } );
			Require( code == 0, $"empty script must exit 0, got {code}" );
		} );

		Check( "script with a bad verb fails nonzero", () =>
		{
			string dir = Work( "script-bad" );
			string script = Path.Combine( dir, "bad.txt" );
			File.WriteAllText( script, "frobnicate the thing\n" );
			string runDir = Path.Combine( dir, "run" );
			int code = OpsCli.Run( new[] { "script", script, "--run", runDir } );
			Require( code == 1, $"bad-verb script must exit 1, got {code}" );
		} );

		Check( "render validates its arguments without a GPU", () =>
		{
			string dir = Work( "render-args" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "render", "x.effigy" } ), "--out" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "render", "x.effigy", "--out", "x.png", "--size", "0x600" } ), "--size" );
			Throws<ArgumentException>( () => OpsCli.Run( new[] { "render", "x.effigy", "--out", "x.png", "--frames", "0" } ), "--frames" );
		} );

		Check( "test discovers no binary in an empty layout and exits 2", () =>
		{
			// In the scratch harness there is no Desktop.Tests next to the executable, so
			// discovery must return null and ops test must exit 2, not fake a green run.
			// (In the real suite the binary exists next to the host; that path is covered
			// by the --tests-bin checks below, not by this one.)
			string? binary = OpsCli.DiscoverTestsBinary( out var probed );
			if ( binary is not null ) return; // real suite: the binary is present; nothing to prove here.
			Require( probed.Count > 0, "discovery must name the paths it probed" );
			Require( OpsCli.Run( new[] { "test", "--filter", "ops-cli" } ) == 2, "ops test with no binary must exit 2" );
		} );

		Check( "test forwards the filter and returns the child exit code", () =>
		{
			string dir = Work( "test-spawn" );
			string fake = Path.Combine( dir, "fake-tests.sh" );
			string argsFile = Path.Combine( dir, "args.txt" );
			File.WriteAllText( fake, "#!/bin/sh\necho \"$@\" > \"" + argsFile + "\"\nexit 3\n" );
			Process.Start( new ProcessStartInfo { FileName = "chmod", ArgumentList = { "+x", fake }, UseShellExecute = false } )?.WaitForExit();
			int code = OpsCli.Run( new[] { "test", "--filter", "ops-cli", "--tests-bin", fake } );
			Require( code == 3, $"ops test must return the child exit code, got {code}" );
			Require( File.Exists( argsFile ), "fake test binary must have been invoked" );
			string forwarded = File.ReadAllText( argsFile );
			Require( forwarded.Contains( "--filter", StringComparison.Ordinal ) && forwarded.Contains( "ops-cli", StringComparison.Ordinal ),
				"ops test must forward --filter, got: " + forwarded );
		} );

		Check( "test with a missing --tests-bin throws", () =>
		{
			Throws<FileNotFoundException>( () => OpsCli.Run( new[] { "test", "--tests-bin", Path.Combine( Work( "test-missing-bin" ), "nope" ) } ) );
		} );
	}
}
