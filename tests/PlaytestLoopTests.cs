#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// Playtest / hot-reload loop tests (hammer-playtest-hotreload-loop):
/// incremental re-bake, pre-launch validation and re-run signalling,
/// headless, over the landed <see cref="IssueDocument"/> snapshot
/// (see kernel/Effigy/Map/PlaytestLoop.cs for the dependency note:
/// MapDocument / QuickPlay / CompileRunner / LightmapBake have not
/// landed yet, so these checks run against IssueDocument and the
/// loop's own grid-leaf stand-in, not BspBuilder visleaves).
///
/// Fixtures live in
/// tests/fixtures/hammer/hammer-playtest-hotreload-loop/ and are
/// loaded by the small JSON reader below (schema
/// hammer-playtest-hotreload-loop/v1: brushes as plane faces +
/// polygon vertices, entities with classname / origin / keyvalues).
/// The acceptance numbers that do not need a file (leaf counts for
/// the sealed room, dirty-face counts) are recomputed from the
/// loaded fixture, not hardcoded from a stale run.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// PlaytestLoopTests.Register(registry, settings); line.
/// </summary>
internal static class PlaytestLoopTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "playtestloop", name, test );

		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireCode( PreLaunchResult result, string code, string message )
		{
			Require( result.HasError( code ), message + " (issues: " + string.Join( "; ", result.Issues ) + ")" );
		}

		// -- fixture loading -------------------------------------------------

		string FixturePath( string name )
		{
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, "hammer-playtest-hotreload-loop", name ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", "hammer-playtest-hotreload-loop", name ) ),
				Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", "hammer-playtest-hotreload-loop", name ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return candidate;
			throw new Exception( "playtest-loop fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
		}

		IssueDocument LoadFixture( string name )
		{
			using var stream = File.OpenRead( FixturePath( name ) );
			using var json = JsonDocument.Parse( stream );
			var root = json.RootElement;
			var document = new IssueDocument
			{
				Name = root.GetProperty( "name" ).GetString() ?? "",
			};
			foreach ( var brushJson in root.GetProperty( "brushes" ).EnumerateArray() )
			{
				var brush = new IssueBrush
				{
					Id = brushJson.GetProperty( "id" ).GetInt32(),
					IsDetail = brushJson.TryGetProperty( "detail", out var detail ) && detail.GetBoolean(),
				};
				foreach ( var faceJson in brushJson.GetProperty( "faces" ).EnumerateArray() )
				{
					var normal = ReadVec3( faceJson.GetProperty( "normal" ) );
					var face = new IssueFace(
						normal,
						faceJson.GetProperty( "distance" ).GetDouble(),
						faceJson.TryGetProperty( "material", out var material ) ? material.GetString() ?? "" : "" ,
						ReadVec3Array( faceJson.GetProperty( "vertices" ) ) );
					brush.Faces.Add( face );
				}
				document.Brushes.Add( brush );
			}
			foreach ( var entityJson in root.GetProperty( "entities" ).EnumerateArray() )
			{
				var entity = new IssueEntity(
					entityJson.GetProperty( "id" ).GetInt32(),
					entityJson.GetProperty( "classname" ).GetString() ?? "",
					ReadVec3( entityJson.GetProperty( "origin" ) ) );
				if ( entityJson.TryGetProperty( "keyvalues", out var keyValues ) )
					foreach ( var pair in keyValues.EnumerateObject() )
						entity.KeyValues[pair.Name] = pair.Value.GetString() ?? "";
				document.Entities.Add( entity );
			}
			return document;
		}

		Vec3 ReadVec3( JsonElement element )
		{
			var values = new List<float>();
			foreach ( var item in element.EnumerateArray() )
				values.Add( (float) item.GetDouble() );
			Require( values.Count == 3, "Vec3 needs exactly 3 components." );
			return new Vec3( values[0], values[1], values[2] );
		}

		List<Vec3> ReadVec3Array( JsonElement element )
		{
			var values = new List<Vec3>();
			foreach ( var item in element.EnumerateArray() )
				values.Add( ReadVec3( item ) );
			return values;
		}

		// -- synthetic document helpers --------------------------------------

		// Axis-aligned box brush, 6 outward-wound faces, same plane
		// convention as the fixtures (normal outward, Distance =
		// Dot(N, point on plane), solid = Dot(N, p) <= Distance).
		static IssueBrush BoxBrush( int id, float minX, float minY, float minZ, float maxX, float maxY, float maxZ )
		{
			var brush = new IssueBrush { Id = id };
			IssueFace Face( Vec3 normal, double distance, Vec3 a, Vec3 b, Vec3 c, Vec3 d ) =>
				new IssueFace( normal, distance, "concrete", new[] { a, b, c, d } );
			brush.Faces.Add( Face( new Vec3( 1, 0, 0 ), maxX,
				new Vec3( maxX, minY, minZ ), new Vec3( maxX, maxY, minZ ), new Vec3( maxX, maxY, maxZ ), new Vec3( maxX, minY, maxZ ) ) );
			brush.Faces.Add( Face( new Vec3( -1, 0, 0 ), -minX,
				new Vec3( minX, maxY, minZ ), new Vec3( minX, minY, minZ ), new Vec3( minX, minY, maxZ ), new Vec3( minX, maxY, maxZ ) ) );
			brush.Faces.Add( Face( new Vec3( 0, 1, 0 ), maxY,
				new Vec3( maxX, maxY, minZ ), new Vec3( minX, maxY, minZ ), new Vec3( minX, maxY, maxZ ), new Vec3( maxX, maxY, maxZ ) ) );
			brush.Faces.Add( Face( new Vec3( 0, -1, 0 ), -minY,
				new Vec3( minX, minY, minZ ), new Vec3( maxX, minY, minZ ), new Vec3( maxX, minY, maxZ ), new Vec3( minX, minY, maxZ ) ) );
			brush.Faces.Add( Face( new Vec3( 0, 0, 1 ), maxZ,
				new Vec3( minX, minY, maxZ ), new Vec3( maxX, minY, maxZ ), new Vec3( maxX, maxY, maxZ ), new Vec3( minX, maxY, maxZ ) ) );
			brush.Faces.Add( Face( new Vec3( 0, 0, -1 ), -minZ,
				new Vec3( maxX, minY, minZ ), new Vec3( minX, minY, minZ ), new Vec3( minX, maxY, minZ ), new Vec3( maxX, maxY, minZ ) ) );
			return brush;
		}

		static IssueDocument WithBrushReplaced( IssueDocument source, IssueBrush replacement )
		{
			var copy = new IssueDocument { Name = source.Name };
			foreach ( var brush in source.Brushes )
				copy.Brushes.Add( brush.Id == replacement.Id ? replacement : brush );
			copy.Entities.AddRange( source.Entities );
			return copy;
		}

		static IssueDocument WithBrushRemoved( IssueDocument source, int brushId, IssueBrush? added )
		{
			var copy = new IssueDocument { Name = source.Name };
			foreach ( var brush in source.Brushes )
				if ( brush.Id != brushId )
					copy.Brushes.Add( brush );
			if ( added != null )
				copy.Brushes.Add( added );
			copy.Entities.AddRange( source.Entities );
			return copy;
		}

		static bool ContainsLeaf( IReadOnlyList<PlaytestLeaf> leaves, int x, int y, int z )
		{
			foreach ( var leaf in leaves )
				if ( leaf.X == x && leaf.Y == y && leaf.Z == z )
					return true;
			return false;
		}

		// -- incremental re-bake ---------------------------------------------

		Check( "full-bake-sealed-room", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			var bake = PlaytestLoop.Bake( document, "maps/sealed_room.bsp" );
			Require( bake.Revision == 1, "Full bake starts at revision 1, got " + bake.Revision + "." );
			Require( bake.IsFull, "Full bake must report IsFull." );
			Require( bake.BrushCount == 6, "Sealed room has 6 brushes, got " + bake.BrushCount + "." );
			Require( bake.TotalFaces == 36, "6 brushes x 6 faces = 36 faces, got " + bake.TotalFaces + "." );
			Require( bake.TotalLeaves == 8, "256-inch room on the 256-inch grid is 2x2x2 = 8 leaves, got " + bake.TotalLeaves + "." );
			Require( bake.MapPath == "maps/sealed_room.bsp", "Bake keeps the map path." );
			Require( bake.BrushHash( 1 ) != 0, "Every brush gets a non-zero content hash." );
			Require( bake.LeavesForBrush( 1 ).Count == 4, "Floor slab touches 4 leaves, got " + bake.LeavesForBrush( 1 ).Count + "." );
		} );

		Check( "one-brush-change-rebakes-only-its-leaves", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			var bake = PlaytestLoop.Bake( document, "maps/sealed_room.bsp" );
			var oldLeaves = new HashSet<PlaytestLeaf>( bake.LeavesForBrush( 4 ) );
			// Move the +X wall (brush 4, x 120..128) 16 inches inward.
			var changed = WithBrushReplaced( document, BoxBrush( 4, 104, -128, -120, 112, 128, 120 ) );
			var result = PlaytestLoop.IncrementalRebake( changed, bake );
			Require( !result.ChangeSet.WasFull, "One-brush change must be incremental, not a full re-bake." );
			Require( result.Bake.Revision == 2, "Incremental re-bake advances the revision to 2, got " + result.Bake.Revision + "." );
			Require( result.ChangeSet.ChangedBrushIds.Count == 1 && result.ChangeSet.ChangedBrushIds[0] == 4,
				"Exactly brush 4 changed, got [" + string.Join( ",", result.ChangeSet.ChangedBrushIds ) + "]." );
			Require( result.ChangeSet.AddedBrushIds.Count == 0 && result.ChangeSet.RemovedBrushIds.Count == 0,
				"No brush was added or removed by moving one wall." );
			Require( result.ChangeSet.DirtyFaces == 6, "Only the moved wall's 6 faces are dirty, got " + result.ChangeSet.DirtyFaces + "." );
			Require( result.ChangeSet.DirtyLeaves.Count < result.ChangeSet.TotalLeaves,
				"Dirty leaves (" + result.ChangeSet.DirtyLeaves.Count + ") must be fewer than all leaves (" + result.ChangeSet.TotalLeaves + ")." );
			Require( result.ChangeSet.ReusedLeaves > 0, "Untouched leaves must be reused, got " + result.ChangeSet.ReusedLeaves + " reused." );
			Require( result.ChangeSet.ReusedFaces == 30, "The other 5 brushes' 30 faces are reused, got " + result.ChangeSet.ReusedFaces + "." );
			var newLeaves = new HashSet<PlaytestLeaf>( result.Bake.LeavesForBrush( 4 ) );
			foreach ( var leaf in result.ChangeSet.DirtyLeaves )
				Require( oldLeaves.Contains( leaf ) || newLeaves.Contains( leaf ),
					"Dirty leaf " + leaf + " belongs to neither the moved wall's old nor new leaves." );
		} );

		Check( "unchanged-document-rebake-is-noop", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			var bake = PlaytestLoop.Bake( document, "maps/sealed_room.bsp" );
			var result = PlaytestLoop.IncrementalRebake( document, bake );
			Require( !result.ChangeSet.WasFull, "Re-baking an unchanged document must stay incremental." );
			Require( result.ChangeSet.ChangedBrushIds.Count == 0, "Nothing changed, got [" + string.Join( ",", result.ChangeSet.ChangedBrushIds ) + "]." );
			Require( result.ChangeSet.DirtyLeaves.Count == 0, "No leaves are dirty for an unchanged document." );
			Require( result.ChangeSet.DirtyFaces == 0, "No faces are dirty for an unchanged document." );
			Require( result.ChangeSet.ReusedLeaves == result.ChangeSet.TotalLeaves,
				"All " + result.ChangeSet.TotalLeaves + " leaves are reused, got " + result.ChangeSet.ReusedLeaves + "." );
			Require( result.Bake.Revision == 2, "Even a no-op re-bake advances the revision, got " + result.Bake.Revision + "." );
		} );

		Check( "null-last-bake-is-full", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			var result = PlaytestLoop.IncrementalRebake( document, null );
			Require( result.ChangeSet.WasFull, "No previous bake must force a full re-bake." );
			Require( result.ChangeSet.DirtyFaces == 36, "Full re-bake dirties all 36 faces, got " + result.ChangeSet.DirtyFaces + "." );
			Require( result.ChangeSet.DirtyLeaves.Count == result.ChangeSet.TotalLeaves,
				"Full re-bake dirties every leaf." );
			Require( result.Bake.Revision == 1, "Full re-bake starts at revision 1, got " + result.Bake.Revision + "." );
		} );

		Check( "renamed-document-forces-full-rebake", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			var bake = PlaytestLoop.Bake( document, "maps/sealed_room.bsp" );
			var renamed = LoadFixture( "sealed-room.json" );
			renamed.Name = "a-different-room";
			var result = PlaytestLoop.IncrementalRebake( renamed, bake );
			Require( result.ChangeSet.WasFull, "Diffing two different documents must fall back to a full re-bake, not a wrong diff." );
			Require( result.Bake.Revision == 1, "A different document restarts its own revision line at 1, got " + result.Bake.Revision + "." );
			Require( result.Bake.DocumentName == "a-different-room", "The new bake carries the new document name." );
		} );

		Check( "added-and-removed-brush-dirties-only-their-leaves", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			var bake = PlaytestLoop.Bake( document, "maps/sealed_room.bsp" );
			// Remove the +Y wall (brush 6) and add a 64-inch block on the floor.
			var changed = WithBrushRemoved( document, 6, BoxBrush( 7, -32, -32, -120, 32, 32, -56 ) );
			var result = PlaytestLoop.IncrementalRebake( changed, bake );
			Require( !result.ChangeSet.WasFull, "Add/remove must stay incremental." );
			Require( result.ChangeSet.ChangedBrushIds.Count == 0, "No brush was modified, got [" + string.Join( ",", result.ChangeSet.ChangedBrushIds ) + "]." );
			Require( result.ChangeSet.RemovedBrushIds.Count == 1 && result.ChangeSet.RemovedBrushIds[0] == 6,
				"Exactly brush 6 was removed, got [" + string.Join( ",", result.ChangeSet.RemovedBrushIds ) + "]." );
			Require( result.ChangeSet.AddedBrushIds.Count == 1 && result.ChangeSet.AddedBrushIds[0] == 7,
				"Exactly brush 7 was added, got [" + string.Join( ",", result.ChangeSet.AddedBrushIds ) + "]." );
			Require( result.ChangeSet.DirtyFaces == 12, "Removed 6 + added 6 faces are dirty, got " + result.ChangeSet.DirtyFaces + "." );
			Require( ContainsLeaf( result.ChangeSet.DirtyLeaves, -1, 0, 0 ),
				"The removed +Y wall's leaf (-1, 0, 0) must be dirty." );
			Require( ContainsLeaf( result.ChangeSet.DirtyLeaves, -1, -1, -1 ),
				"The added block's leaf (-1, -1, -1) must be dirty." );
			Require( result.ChangeSet.ReusedLeaves > 0, "Untouched leaves must still be reused." );
		} );

		// -- pre-launch validation -------------------------------------------

		Check( "no-player-start-fails-with-named-error", () =>
		{
			var document = LoadFixture( "no-player-start.json" );
			var result = PlaytestLoop.PreLaunchValidation( document );
			Require( !result.Passed, "A map with no player start must fail pre-launch validation." );
			RequireCode( result, "NO_PLAYER_START", "Missing player start must be the named error NO_PLAYER_START." );
			Require( !result.HasPlayerStart && result.PlayerStartCount == 0, "PlayerStartCount must be 0." );
		} );

		Check( "blocked-spawn-fails-with-named-error", () =>
		{
			var document = LoadFixture( "blocked-spawn.json" );
			var result = PlaytestLoop.PreLaunchValidation( document );
			Require( !result.Passed, "A spawn inside solid geometry must fail pre-launch validation." );
			RequireCode( result, "SPAWN_INSIDE_SOLID", "A spawn inside solid must be the named error SPAWN_INSIDE_SOLID." );
			Require( result.HasPlayerStart, "The blocked map still HAS a player start — the failure is the spawn position, not its absence." );
		} );

		Check( "valid-room-passes-validation", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			var result = PlaytestLoop.PreLaunchValidation( document );
			Require( result.Passed, "The sealed room must pass: " + string.Join( "; ", result.Issues ) );
			Require( result.Issues.Count == 0, "A valid room has no findings, got " + result.Issues.Count + "." );
			Require( result.HasPlayerStart && result.PlayerStartCount == 1, "Exactly one player start." );
			Require( !result.NavmeshRequired, "A room with no NPCs does not need a navmesh." );
		} );

		Check( "npc-without-navmesh-fails-with-named-error", () =>
		{
			var document = LoadFixture( "npc-no-navmesh.json" );
			var result = PlaytestLoop.PreLaunchValidation( document, navmeshPresent: false );
			Require( !result.Passed, "An NPC map with no navmesh must fail pre-launch validation." );
			Require( result.NavmeshRequired, "The NPC map must be flagged as needing a navmesh." );
			RequireCode( result, "NAVMESH_MISSING", "A missing navmesh must be the named error NAVMESH_MISSING." );
		} );

		Check( "npc-with-navmesh-passes", () =>
		{
			var document = LoadFixture( "npc-no-navmesh.json" );
			var result = PlaytestLoop.PreLaunchValidation( document, navmeshPresent: true );
			Require( result.Passed, "The same NPC map passes once a navmesh is present: " + string.Join( "; ", result.Issues ) );
			Require( result.NavmeshRequired, "NavmeshRequired stays true; presence is what changed." );
		} );

		Check( "multiple-player-starts-is-warning-not-error", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			document.Entities.Add( new IssueEntity( 2, "info_player_start", new Vec3( 32, 0, -112 ) ) );
			var result = PlaytestLoop.PreLaunchValidation( document );
			Require( result.Passed, "Two player starts must not block a playtest (warning only): " + string.Join( "; ", result.Issues ) );
			Require( result.HasIssue( "MULTIPLE_PLAYER_STARTS" ), "Two starts must raise the MULTIPLE_PLAYER_STARTS warning." );
			Require( result.PlayerStartCount == 2, "PlayerStartCount must be 2." );
		} );

		// -- re-run signalling -----------------------------------------------

		Check( "rerun-issues-changelevel-with-map-path", () =>
		{
			var document = LoadFixture( "sealed-room.json" );
			var bake = PlaytestLoop.Bake( document, "maps/sealed_room.bsp" );
			var result = PlaytestLoop.RequestReRun( document, bake, "maps/sealed_room.bsp", navmeshPresent: false, sessionRunning: true );
			Require( result.Issued, "A valid map with a running session must issue a re-run." );
			Require( result.Command != null, "An issued re-run carries a command." );
			Require( result.Command!.Signal == ReRunSignal.ChangeLevel,
				"A running session hot-reloads via ChangeLevel, got " + result.Command.Signal + "." );
			Require( result.Command.MapPath == "maps/sealed_room.bsp", "The command carries the right map path." );
			Require( result.Command.ToConsoleCommand() == "changelevel maps/sealed_room.bsp",
				"Console command must be 'changelevel maps/sealed_room.bsp', got '" + result.Command.ToConsoleCommand() + "'." );
			Require( result.Command.BakeRevision == 2, "The re-run reloads the fresh incremental bake (revision 2), got " + result.Command.BakeRevision + "." );
			Require( result.Validation.Passed && result.Rebake != null, "Validation passed and a re-bake ran before the signal." );
		} );

		Check( "rerun-refused-when-validation-fails", () =>
		{
			var document = LoadFixture( "no-player-start.json" );
			var bake = PlaytestLoop.Bake( document, "maps/no_start.bsp" );
			var result = PlaytestLoop.RequestReRun( document, bake, "maps/no_start.bsp", navmeshPresent: false, sessionRunning: true );
			Require( !result.Issued, "A failed validation must never reach the running game." );
			Require( result.Command == null, "A refused re-run carries no command." );
			Require( result.Rebake == null, "A refused re-run runs no re-bake either." );
			Require( result.Validation.HasError( "NO_PLAYER_START" ), "The refusal names NO_PLAYER_START." );

			var fresh = PlaytestLoop.BuildReRun( "maps/sealed_room.bsp", bake, sessionRunning: false );
			Require( fresh.Signal == ReRunSignal.ReloadMap, "With no session running the signal is a fresh ReloadMap." );
			Require( fresh.ToConsoleCommand() == "map maps/sealed_room.bsp",
				"Fresh-launch console command must be 'map maps/sealed_room.bsp', got '" + fresh.ToConsoleCommand() + "'." );
			Require( fresh.IsValid, "A command with a map path is valid." );
		} );

		Check( "ops-command-names", () =>
		{
			Require( PlaytestOpsCommands.Playtest == "map playtest",
				"The ops-CLI playtest command is 'map playtest', got '" + PlaytestOpsCommands.Playtest + "'." );
			Require( PlaytestOpsCommands.ReRun == "map rerun",
				"The ops-CLI re-run command is 'map rerun', got '" + PlaytestOpsCommands.ReRun + "'." );
		} );
	}
}
