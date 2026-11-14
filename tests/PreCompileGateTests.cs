// Pre-compile safety-net gate tests (group "precompilegate").
//
// Hammer Desktop P2b: hammer-compile-safety-net-gate (worker daxter-hammer-compile-safetynet).
// The gate runs the full Check-for-Problems pass before every compile
// preset: errors block with the named issue, warnings are logged and the
// compile proceeds, a caller override skips the gate and is logged, and a
// giant skybox box warns. Fixtures: tests/fixtures/hammer/
// hammer-compile-safety-net-gate/ (see its README.md). The sealed-room
// inputs mirror tests/fixtures/hammer/hammer-compile-native-pipeline/
// sealed-room.json (288x288 room, floor z -16..0, ceiling z 128..144,
// player start (0,0,32)); the leaking-room input mirrors leaking-room.json
// (east wall split with a 1-unit gap at y -0.5..0.5).
// NOT registered in TestRegistry.cs yet — the final integrator adds the one line.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Effigy;
using Effigy.Map;

internal static class PreCompileGateTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "precompilegate", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: wrong exception {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name }, got silent success" );
		}

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-compile-safety-net-gate" );
					if ( File.Exists( Path.Combine( candidate, "map-clean.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "precompilegate fixture dir not found (map-clean.json missing above cwd / BaseDirectory)" );
		}

		string WorkDir( string name )
		{
			var dir = Path.Combine( settings.WorkDirectory, "precompilegate", name );
			if ( Directory.Exists( dir ) ) Directory.Delete( dir, recursive: true );
			Directory.CreateDirectory( dir );
			return dir;
		}

		InMemoryMapCheckDocument LoadDoc( string file ) =>
			MapCheckDocumentJson.LoadFromFile( Path.Combine( FixtureDir(), file ) );

		CheckBrush RoomBrush( string id, Vec3 min, Vec3 max, string material = "concrete" ) =>
			new CheckBrush { Id = id, Min = min, Max = max, Material = material, FaceCount = 6 };

		InMemoryMapCheckDocument SealedRoomDoc()
		{
			var doc = new InMemoryMapCheckDocument { Name = "sealed-room" };
			doc.AddBrush( RoomBrush( "floor", new Vec3( -144, -144, -16 ), new Vec3( 144, 144, 0 ) ) );
			doc.AddBrush( RoomBrush( "ceiling", new Vec3( -144, -144, 128 ), new Vec3( 144, 144, 144 ) ) );
			doc.AddBrush( RoomBrush( "wall_west", new Vec3( -144, -144, 0 ), new Vec3( -128, 144, 128 ) ) );
			doc.AddBrush( RoomBrush( "wall_east", new Vec3( 128, -144, 0 ), new Vec3( 144, 144, 128 ) ) );
			doc.AddBrush( RoomBrush( "wall_south", new Vec3( -128, -144, 0 ), new Vec3( 128, -128, 128 ) ) );
			doc.AddBrush( RoomBrush( "wall_north", new Vec3( -128, 128, 0 ), new Vec3( 128, 144, 128 ) ) );
			doc.AddEntity( new CheckEntity { Id = "player", ClassName = "info_player_start", Origin = new Vec3( 0, 0, 32 ) } );
			doc.AddEntity( new CheckEntity { Id = "light", ClassName = "light", Origin = new Vec3( 0, 0, 96 ) } );
			doc.HasPlayerStart = true;
			doc.PlayerStart = new Vec3( 0, 0, 32 );
			return doc;
		}

		CompileInput SealedRoomInput()
		{
			var input = new CompileInput( "sealed-room" );
			input.Brushes.Add( CompileBrush.Box( "floor", 0, new Vec3( -144, -144, -16 ), new Vec3( 144, 144, 0 ), "concrete" ) );
			input.Brushes.Add( CompileBrush.Box( "ceiling", 1, new Vec3( -144, -144, 128 ), new Vec3( 144, 144, 144 ), "concrete" ) );
			input.Brushes.Add( CompileBrush.Box( "wall_west", 2, new Vec3( -144, -144, 0 ), new Vec3( -128, 144, 128 ), "concrete" ) );
			input.Brushes.Add( CompileBrush.Box( "wall_east", 3, new Vec3( 128, -144, 0 ), new Vec3( 144, 144, 128 ), "concrete" ) );
			input.Brushes.Add( CompileBrush.Box( "wall_south", 4, new Vec3( -128, -144, 0 ), new Vec3( 128, -128, 128 ), "concrete" ) );
			input.Brushes.Add( CompileBrush.Box( "wall_north", 5, new Vec3( -128, 128, 0 ), new Vec3( 128, 144, 128 ), "concrete" ) );
			input.PlayerStart = new Vec3( 0, 0, 32 );
			input.Lights.Add( new CompileLight( new Vec3( 0, 0, 96 ), 1f, new Vec3( 1, 1, 1 ) ) );
			return input;
		}

		CompileInput LeakingRoomInput()
		{
			var input = SealedRoomInput();
			input.Name = "leaking-room";
			input.Brushes.RemoveAll( b => b.Name == "wall_east" );
			input.Brushes.Add( CompileBrush.Box( "wall_east_south", 3, new Vec3( 128, -144, 0 ), new Vec3( 144, -0.5f, 128 ), "concrete" ) );
			input.Brushes.Add( CompileBrush.Box( "wall_east_north", 6, new Vec3( 128, 0.5f, 0 ), new Vec3( 144, 144, 128 ), "concrete" ) );
			return input;
		}

		CompileResult Run( CompileInput input, CompilePreset preset, string workName, CompileOptions options = null )
		{
			options ??= new CompileOptions { OutputFolder = WorkDir( workName ), SingleThreaded = true };
			if ( options.OutputFolder is null ) options.OutputFolder = WorkDir( workName );
			return CompileRunner.Run( input, preset, options );
		}

		// ---- gate level: the document check --------------------------------

		Check( "clean sealed-room document passes the gate with no issues", () =>
		{
			var gate = PreCompileGate.Check( SealedRoomDoc() );
			Require( gate.Ran && !gate.Overridden, "gate did not run" );
			Require( !gate.Blocked && !gate.HasErrors, "clean map blocked: " + gate.Summary );
			Require( gate.Issues.Count == 0, "clean map produced issues: " + string.Join( "; ", gate.Issues.Select( i => i.ToString() ) ) );
			Require( gate.SkippedRules.Contains( "leak" ), "gate must skip the leak rule without a portal graph: " + string.Join( ",", gate.SkippedRules ) );
			Require( gate.SkippedRules.Contains( "unsealed-map" ), "gate must skip the unsealed-map probe rule: " + string.Join( ",", gate.SkippedRules ) );
			Require( gate.Summary.Contains( "passed", StringComparison.Ordinal ), "summary does not say passed: " + gate.Summary );
		} );

		Check( "zero-volume brush blocks the gate with the named invalid-brush issue", () =>
		{
			var doc = SealedRoomDoc();
			doc.AddBrush( new CheckBrush { Id = "bad", Min = new Vec3( 0, 0, 0 ), Max = new Vec3( 0, 0, 0 ), Material = "concrete", FaceCount = 6 } );
			var gate = PreCompileGate.Check( doc );
			Require( gate.Blocked && gate.HasErrors, "zero-volume brush did not block: " + gate.Summary );
			Require( gate.ErrorCount == 1, $"expected 1 error, got {gate.ErrorCount}: " + string.Join( "; ", gate.Issues.Select( i => i.ToString() ) ) );
			var issue = gate.Issues[0];
			Require( issue.Type == MapIssueType.InvalidBrush, "issue is not InvalidBrush: " + issue.Type );
			Require( issue.Severity == MapIssueSeverity.Error, "invalid brush is not an error" );
			Require( issue.BrushId == "bad", $"issue does not name brush 'bad': {issue.BrushId}" );
			Require( issue.Message.Contains( "zero-volume", StringComparison.Ordinal ), "issue does not name the defect: " + issue.Message );
			Require( issue.Id == "CHK-0001", "issue id is not CHK-0001: " + issue.Id );
		} );

		Check( "missing-target entity blocks the gate with the target named", () =>
		{
			var doc = SealedRoomDoc();
			var trigger = new CheckEntity { Id = "trig1", ClassName = "trigger_multiple", Origin = new Vec3( 0, 0, 64 ) };
			trigger.Properties["targetname"] = "trig1";
			trigger.Properties["target"] = "missing_door";
			doc.AddEntity( trigger );
			var gate = PreCompileGate.Check( doc );
			Require( gate.Blocked, "missing target did not block" );
			var issue = gate.Issues.FirstOrDefault( i => i.Type == MapIssueType.MissingTarget );
			Require( issue is not null, "no MissingTarget issue: " + string.Join( "; ", gate.Issues.Select( i => i.ToString() ) ) );
			Require( issue!.Severity == MapIssueSeverity.Error, "missing target is not an error" );
			Require( issue.EntityId == "trig1", "issue does not name the source entity: " + issue.EntityId );
			Require( issue.Detail == "missing_door", "issue detail does not name the missing target: " + issue.Detail );
			Require( issue.Key == "target", "issue does not name the referencing key: " + issue.Key );
		} );

		Check( "duplicate targetname warns but does not block", () =>
		{
			var doc = SealedRoomDoc();
			var a = new CheckEntity { Id = "door_a", ClassName = "func_door", Origin = new Vec3( 120, 0, 64 ) };
			a.Properties["targetname"] = "door1";
			var b = new CheckEntity { Id = "door_b", ClassName = "func_door", Origin = new Vec3( -120, 0, 64 ) };
			b.Properties["targetname"] = "door1";
			doc.AddEntity( a );
			doc.AddEntity( b );
			var gate = PreCompileGate.Check( doc );
			Require( !gate.Blocked && !gate.HasErrors, "duplicate targetname blocked: " + gate.Summary );
			Require( gate.WarningCount == 1, $"expected 1 warning, got {gate.WarningCount}" );
			var issue = gate.Issues[0];
			Require( issue.Type == MapIssueType.DuplicateTargetName, "issue is not DuplicateTargetName: " + issue.Type );
			Require( issue.Severity == MapIssueSeverity.Warning, "duplicate targetname is not a warning" );
			Require( issue.Message.Contains( "door1", StringComparison.Ordinal ), "issue does not name the duplicated targetname: " + issue.Message );
		} );

		Check( "bad keyvalue warns but does not block", () =>
		{
			var doc = SealedRoomDoc();
			var relay = new CheckEntity { Id = "relay1", ClassName = "logic_relay", Origin = new Vec3( 0, 0, 48 ) };
			relay.Properties["targetname"] = "relay1";
			relay.Properties["speed"] = "fast";
			doc.AddEntity( relay );
			var gate = PreCompileGate.Check( doc );
			Require( !gate.Blocked, "bad keyvalue blocked: " + gate.Summary );
			var issue = gate.Issues.FirstOrDefault( i => i.Type == MapIssueType.BadKeyValue );
			Require( issue is not null, "no BadKeyValue issue" );
			Require( issue!.Severity == MapIssueSeverity.Warning, "bad keyvalue is not a warning" );
			Require( issue.Key == "speed", "issue does not name the bad key: " + issue.Key );
		} );

		Check( "giant skybox warns but does not block, and a modest skybox does not warn", () =>
		{
			var doc = SealedRoomDoc();
			doc.AddBrush( new CheckBrush { Id = "skybox", Min = new Vec3( -2048, -2048, -256 ), Max = new Vec3( 2048, 2048, 2048 ), Material = "tools/toolsskybox", FaceCount = 6 } );
			var gate = PreCompileGate.Check( doc );
			Require( !gate.Blocked && !gate.HasErrors, "giant skybox blocked: " + gate.Summary );
			var issue = gate.Issues.FirstOrDefault( i => i.Type == MapIssueType.GiantSkybox );
			Require( issue is not null, "no GiantSkybox issue: " + string.Join( "; ", gate.Issues.Select( i => i.ToString() ) ) );
			Require( issue!.Severity == MapIssueSeverity.Warning, "giant skybox is not a warning" );
			Require( issue.BrushId == "skybox", "issue does not name the skybox brush: " + issue.BrushId );
			Require( issue.Message.Contains( "14.2x", StringComparison.Ordinal ), "issue does not name the size ratio: " + issue.Message );

			var modest = SealedRoomDoc();
			modest.AddBrush( new CheckBrush { Id = "skybox", Min = new Vec3( -256, -256, -64 ), Max = new Vec3( 256, 256, 256 ), Material = "tools/toolsskybox", FaceCount = 6 } );
			var modestGate = PreCompileGate.Check( modest );
			Require( modestGate.Issues.Count == 0, "modest skybox warned: " + string.Join( "; ", modestGate.Issues.Select( i => i.ToString() ) ) );
		} );

		Check( "skybox box at exactly 4x the map does not warn; just over 4x does", () =>
		{
			// Map longest side is 288 (the room is 288x288); 4x is 1152.
			var atLimit = SealedRoomDoc();
			atLimit.AddBrush( new CheckBrush { Id = "skybox", Min = new Vec3( -576, -576, -64 ), Max = new Vec3( 576, 576, 576 ), Material = "tools/toolsskybox", FaceCount = 6 } );
			Require( PreCompileGate.Check( atLimit ).Issues.Count( i => i.Type == MapIssueType.GiantSkybox ) == 0,
				"skybox at exactly 4x warned" );
			var overLimit = SealedRoomDoc();
			overLimit.AddBrush( new CheckBrush { Id = "skybox", Min = new Vec3( -648, -648, -64 ), Max = new Vec3( 648, 648, 648 ), Material = "tools/toolsskybox", FaceCount = 6 } );
			Require( PreCompileGate.Check( overLimit ).Issues.Count( i => i.Type == MapIssueType.GiantSkybox ) == 1,
				"skybox at 4.5x did not warn" );
		} );

		Check( "fixture maps load and gate as documented", () =>
		{
			var clean = PreCompileGate.Check( LoadDoc( "map-clean.json" ) );
			Require( !clean.Blocked && clean.Issues.Count == 0, "clean fixture blocked: " + clean.Summary );

			var invalid = PreCompileGate.Check( LoadDoc( "map-invalid-brush.json" ) );
			Require( invalid.Blocked, "invalid-brush fixture did not block" );
			Require( invalid.Issues.Any( i => i.Type == MapIssueType.InvalidBrush && i.BrushId == "bad" ), "invalid-brush fixture has no named issue" );

			var skybox = PreCompileGate.Check( LoadDoc( "map-giant-skybox.json" ) );
			Require( !skybox.Blocked, "giant-skybox fixture blocked" );
			Require( skybox.Issues.Any( i => i.Type == MapIssueType.GiantSkybox ), "giant-skybox fixture has no warning" );

			var warnings = PreCompileGate.Check( LoadDoc( "map-warnings-only.json" ) );
			Require( !warnings.Blocked && warnings.HasWarnings, "warnings-only fixture blocked or has no warnings" );
			Require( warnings.Issues.Any( i => i.Type == MapIssueType.DuplicateTargetName ), "warnings-only fixture has no duplicate-target warning" );
			Require( warnings.Issues.Any( i => i.Type == MapIssueType.BadKeyValue ), "warnings-only fixture has no bad-keyvalue warning" );
		} );

		Check( "mixed-issues fixture with a leak graph blocks on the real leak verdict", () =>
		{
			var doc = MapCheckDocumentJson.LoadFromFile( Path.Combine( FixtureDir(), "..", "hammer-check-problems-fix-actions", "map-mixed-issues.json" ) );
			var gate = PreCompileGate.Check( doc );
			Require( gate.Blocked, "mixed-issues fixture did not block" );
			var leak = gate.Issues.FirstOrDefault( i => i.Type == MapIssueType.Leak );
			Require( leak is not null, "no Leak issue from the leak graph: " + string.Join( "; ", gate.Issues.Select( i => i.ToString() ) ) );
			Require( leak!.Severity == MapIssueSeverity.Error, "leak is not an error" );
			Require( !string.IsNullOrEmpty( leak.GapPointFile ), "leak issue carries no point file" );
			Require( gate.Issues.Count( i => i.Type == MapIssueType.InvalidBrush ) == 2, "expected 2 invalid-brush issues" );
			Require( gate.Issues.Count( i => i.Type == MapIssueType.MissingMaterial ) == 1, "expected 1 missing-material issue (unknown material)" );
		} );

		Check( "gate rejects a null document", () =>
		{
			Throws<ArgumentNullException>( () => PreCompileGate.Check( (IMapCheckDocument)null! ), "null document" );
			Throws<ArgumentNullException>( () => PreCompileGate.Check( (CompileInput)null! ), "null input" );
		} );

		// ---- runner level: the gate wired into every preset ---------------

		Check( "invalid-brush map is blocked by the gate with the named issue", () =>
		{
			var dir = WorkDir( "gate-invalid-brush" );
			var input = SealedRoomInput();
			input.Brushes.Add( CompileBrush.Box( "bad", 99, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ) ) );
			var result = Run( input, CompilePreset.Final, "gate-invalid-brush" );
			Require( !result.Succeeded, "map with a zero-volume brush compiled" );
			Require( result.Gate is not null && result.Gate.Blocked, "gate did not block" );
			Require( result.Gate!.ErrorCount == 1, $"gate error count {result.Gate.ErrorCount}" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild (gate is a pre-stage)" );
			var issue = result.Issues.FirstOrDefault( i => i.Code == "invalid-brush" );
			Require( issue is not null, "no 'invalid-brush' issue: " + string.Join( "; ", result.Issues ) );
			Require( issue!.Subject == "bad", $"issue subject '{issue.Subject}', expected the brush name 'bad'" );
			Require( issue.Message.Contains( "zero-volume", StringComparison.Ordinal ), "issue does not name the defect: " + issue.Message );
			Require( result.Log.Any( l => l.Contains( "[gate]", StringComparison.Ordinal ) ), "log has no gate lines" );
			Require( result.Log.Any( l => l.Contains( "blocked", StringComparison.Ordinal ) ), "log does not record the block" );
			Require( !File.Exists( Path.Combine( dir, "sealed-room.bsp" ) ), "blocked run wrote a .bsp" );
		} );

		Check( "warnings-only map compiles and logs its warnings", () =>
		{
			var dir = WorkDir( "gate-skybox" );
			var input = SealedRoomInput();
			// Detached skybox box (a 3D-skybox-style section in its own
			// space, not enclosing the room): an enclosing solid box would
			// put the player start inside solid and the BSP would rightly
			// refuse it, which is not what this test is about. Longest
			// side 4096 vs the map's 288 = 14.2x the 4x limit, so the gate
			// still warns.
			input.Brushes.Add( CompileBrush.Box( "skybox", 90, new Vec3( 1024, 1024, 0 ), new Vec3( 5120, 5120, 4096 ), "tools/toolsskybox" ) );
			var result = Run( input, CompilePreset.Final, "gate-skybox" );
			Require( result.Succeeded, "warnings-only map failed: " + result.Error );
			Require( result.Gate is not null && !result.Gate.Blocked && result.Gate.HasWarnings, "gate did not pass with warnings" );
			var warning = result.Issues.FirstOrDefault( i => i.Code == "giant-skybox" );
			Require( warning is not null, "no 'giant-skybox' warning in issues: " + string.Join( "; ", result.Issues ) );
			Require( warning!.Severity == CompileSeverity.Warning, "giant skybox is not a warning" );
			Require( result.Log.Any( l => l.Contains( "giant-skybox", StringComparison.Ordinal ) ), "log has no giant-skybox warning" );
			Require( File.Exists( Path.Combine( dir, "sealed-room.bsp" ) ), "warnings-only run wrote no .bsp" );
		} );

		Check( "override skips the gate and is recorded in the log", () =>
		{
			var input = SealedRoomInput();
			input.Brushes.Add( CompileBrush.Box( "bad", 99, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ) ) );
			var options = new CompileOptions { OutputFolder = WorkDir( "gate-override" ), SingleThreaded = true, SkipPreCompileGate = true };
			var result = CompileRunner.Run( input, CompilePreset.Final, options );
			Require( result.Gate is not null && result.Gate.Overridden && !result.Gate.Ran, "gate was not skipped by override" );
			Require( result.Log.Any( l => l.Contains( "SKIPPED", StringComparison.Ordinal ) && l.Contains( "override", StringComparison.Ordinal ) ),
				"log does not record the override: " + string.Join( "; ", result.Log ) );
			// The override skips ONLY the gate: the BSP stage still blocks the invalid brush.
			Require( !result.Succeeded, "override let an invalid brush compile" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			Require( result.Issues.Any( i => i.Code == "invalid-brush" ), "BSP stage did not report the invalid brush" );
		} );

		Check( "override on a warnings-only map lets the compile succeed", () =>
		{
			var input = SealedRoomInput();
			input.Brushes.Add( CompileBrush.Box( "skybox", 90, new Vec3( 1024, 1024, 0 ), new Vec3( 5120, 5120, 4096 ), "tools/toolsskybox" ) );
			var options = new CompileOptions { OutputFolder = WorkDir( "gate-override-skybox" ), SingleThreaded = true, SkipPreCompileGate = true };
			var result = CompileRunner.Run( input, CompilePreset.Final, options );
			Require( result.Succeeded, "override run failed: " + result.Error );
			Require( result.Gate is not null && result.Gate.Overridden, "gate was not skipped by override" );
			Require( result.Log.Any( l => l.Contains( "SKIPPED", StringComparison.Ordinal ) ), "log does not record the override" );
			Require( result.Issues.Count == 0, "skipped gate still reported issues: " + string.Join( "; ", result.Issues ) );
		} );

		Check( "leaking map is not gate-blocked and still fails at the BSP stage with a leak and point file", () =>
		{
			var dir = WorkDir( "gate-leak" );
			var input = LeakingRoomInput();
			var result = Run( input, CompilePreset.Final, "gate-leak" );
			Require( result.Gate is not null && !result.Gate.Blocked, "gate blocked a leaking map (the BSP stage owns the leak verdict)" );
			Require( result.Gate!.SkippedRules.Contains( "leak" ), "gate did not record the skipped leak rule" );
			Require( !result.Succeeded, "leaking map compiled" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			Require( result.Issues.Any( i => i.Code == "leak" ), "no 'leak' issue: " + string.Join( "; ", result.Issues ) );
			Require( result.Leak is not null && result.Leak.IsLeaked, "no leak result recorded" );
			var lin = Path.Combine( dir, "leaking-room.lin" );
			Require( File.Exists( lin ), "no leak point file written" );
			Require( result.OutputFiles.Contains( lin ), ".lin is not listed in OutputFiles" );
		} );

		Check( "clean sealed-room compile passes the gate with zero issues", () =>
		{
			var result = Run( SealedRoomInput(), CompilePreset.Final, "gate-clean" );
			Require( result.Succeeded, "clean run failed: " + result.Error );
			Require( result.Gate is not null && !result.Gate.Blocked && result.Gate.Issues.Count == 0, "gate did not pass clean" );
			Require( result.Issues.Count == 0, "clean run produced issues: " + string.Join( "; ", result.Issues ) );
			Require( result.Log.Any( l => l.Contains( "gate passed", StringComparison.Ordinal ) ), "log has no gate pass line" );
		} );

		Check( "over-limit brush count is blocked by the gate", () =>
		{
			var input = SealedRoomInput();
			var options = new CompileOptions { OutputFolder = WorkDir( "gate-over-limit" ), SingleThreaded = true, MaxBrushes = 2 };
			var result = CompileRunner.Run( input, CompilePreset.Final, options );
			Require( !result.Succeeded, "over-limit map compiled" );
			Require( result.Gate is not null && result.Gate.Blocked, "gate did not block the over-limit map" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			Require( result.Issues.Any( i => i.Code == "limit-exceeded" ), "no 'limit-exceeded' issue: " + string.Join( "; ", result.Issues ) );
		} );

		Check( "empty map is not gate-blocked; the BSP stage owns empty-map", () =>
		{
			var result = Run( new CompileInput( "empty" ), CompilePreset.Final, "gate-empty" );
			Require( result.Gate is not null && !result.Gate.Blocked, "gate blocked an empty map (it has nothing to check)" );
			Require( !result.Succeeded, "empty map compiled" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			Require( result.Issues.Any( i => i.Code == "empty-map" ), "no 'empty-map' issue" );
		} );

		Check( "gate runs first in every preset, including QuickPlay", () =>
		{
			var input = SealedRoomInput();
			input.Brushes.Add( CompileBrush.Box( "bad", 99, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ) ) );
			var result = Run( input, CompilePreset.QuickPlay, "gate-quickplay" );
			Require( !result.Succeeded, "QuickPlay compiled an invalid map" );
			Require( result.Gate is not null && result.Gate.Blocked, "gate did not run on QuickPlay" );
			Require( result.CompletedStages.Count == 0, "blocked QuickPlay run completed stages" );
		} );

		Check( "duplicate targetname entities warn on the runner path and still compile", () =>
		{
			var input = SealedRoomInput();
			input.Entities.Add( new CompileEntity( 1, "logic_relay", new Vec3( 120, 0, 64 ), "door1" ) );
			input.Entities.Add( new CompileEntity( 2, "logic_relay", new Vec3( -120, 0, 64 ), "door1" ) );
			var result = Run( input, CompilePreset.Final, "gate-dup-target" );
			Require( result.Succeeded, "duplicate-targetname run failed: " + result.Error );
			Require( result.Gate is not null && !result.Gate.Blocked && result.Gate.HasWarnings, "gate did not pass with warnings" );
			Require( result.Issues.Any( i => i.Code == "duplicate-target-name" && i.Severity == CompileSeverity.Warning ),
				"no duplicate-target-name warning: " + string.Join( "; ", result.Issues ) );
			Require( result.Log.Any( l => l.Contains( "duplicate-target-name", StringComparison.Ordinal ) ), "log has no duplicate-target-name warning" );
		} );

		Check( "entity targeting a missing target is blocked by the gate on the runner path", () =>
		{
			var dir = WorkDir( "gate-missing-target" );
			var input = SealedRoomInput();
			var relay = new CompileEntity( 1, "logic_relay", new Vec3( 0, 0, 48 ), "relay1" );
			relay.Keyvalues["target"] = "ghost_relay";
			input.Entities.Add( relay );
			var result = Run( input, CompilePreset.Final, "gate-missing-target" );
			Require( !result.Succeeded, "missing-target map compiled" );
			Require( result.Gate is not null && result.Gate.Blocked, "gate did not block the missing-target map" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			Require( result.Issues.Any( i => i.Code == "missing-target" ), "no 'missing-target' issue: " + string.Join( "; ", result.Issues ) );
			Require( !File.Exists( Path.Combine( dir, "sealed-room.bsp" ) ), "blocked run wrote a .bsp" );
		} );

		Check( "duplicate brush ids are blocked by the gate as a named duplicate-id error, not a throw", () =>
		{
			var input = SealedRoomInput();
			input.Brushes.Add( CompileBrush.Box( "wall_east_copy", 4, new Vec3( 128, -144, 0 ), new Vec3( 144, 144, 128 ), "concrete" ) );
			var result = Run( input, CompilePreset.Final, "gate-dup-id" );
			Require( !result.Succeeded, "duplicate-id map compiled" );
			Require( result.Gate is not null && result.Gate.Blocked, "gate did not block the duplicate-id map" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			Require( result.Issues.Any( i => i.Code == "duplicate-id" ), "no 'duplicate-id' issue: " + string.Join( "; ", result.Issues ) );
			Require( result.Error is not null && result.Error.Contains( "duplicate", StringComparison.OrdinalIgnoreCase ), "block error does not name the duplicate: " + result.Error );
		} );
	}
}
