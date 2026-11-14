using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Tests for the Entity Report API (hammer-entity-report-find-replace):
/// query by class/keyvalue/target, SelectAllByClass, bulk keyvalue edit,
/// Find/Replace (including target renames across the I/O model) and Go to
/// Brush Number. Group "entityreport". Fixtures live in
/// tests/fixtures/hammer/hammer-entity-report-find-replace/.
/// </summary>
internal static class EntityReportTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "entityreport", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		string RepoRoot()
		{
			foreach ( string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory } )
			{
				string dir = Path.GetFullPath( start );
				while ( dir != null )
				{
					if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
					string parent = Path.GetDirectoryName( dir );
					if ( parent is null || parent == dir ) break;
					dir = parent;
				}
			}
			throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
		}

		string FixtureDir() => Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-entity-report-find-replace" );

		EntityRegistry LoadRegistry()
		{
			var reg = EntityRegistry.Load( Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" ) );
			reg.LoadAdditional( Path.Combine( FixtureDir(), "npc-registry.json" ) );
			return reg;
		}

		(List<ReportEntity> Entities, int[] WorldBrushes) LoadMap()
		{
			using var doc = JsonDocument.Parse( File.ReadAllText( Path.Combine( FixtureDir(), "report-map.json" ) ) );
			var root = doc.RootElement;
			var entities = new List<ReportEntity>();
			foreach ( var el in root.GetProperty( "entities" ).EnumerateArray() )
			{
				var entity = new ReportEntity( el.GetProperty( "id" ).GetInt32(), el.GetProperty( "classname" ).GetString() );
				foreach ( var kv in el.GetProperty( "keyvalues" ).EnumerateObject() )
					entity.SetKeyValue( kv.Name, kv.Value.GetString() ?? "" );
				if ( el.TryGetProperty( "brushes", out var brushes ) )
					foreach ( var n in brushes.EnumerateArray() )
						entity.BrushNumbers.Add( n.GetInt32() );
				if ( el.TryGetProperty( "outputs", out var outputs ) )
					foreach ( var row in outputs.EnumerateArray() )
						entity.Outputs.Add( new ReportOutput(
							row.GetProperty( "event" ).GetString(),
							row.GetProperty( "target" ).GetString(),
							row.GetProperty( "input" ).GetString(),
							row.GetProperty( "delay" ).GetDouble(),
							row.GetProperty( "times" ).GetInt32() ) );
				entities.Add( entity );
			}
			var world = new List<int>();
			foreach ( var n in root.GetProperty( "worldBrushes" ).EnumerateArray() )
				world.Add( n.GetInt32() );
			return (entities, world.ToArray());
		}

		EntityReport FreshReport( bool withRegistry = true )
		{
			var (entities, world) = LoadMap();
			var report = new EntityReport( entities, withRegistry ? LoadRegistry() : null );
			foreach ( int n in world ) report.RegisterWorldBrush( n );
			return report;
		}

		Check( "fixture map loads with 5 npc entities", () =>
		{
			var report = FreshReport();
			Require( report.Count == 11, $"report has {report.Count} entities, expected 11" );
			var npcs = report.SelectAllByClass( "npc_citizen" );
			Require( npcs.Count == 5, $"SelectAllByClass('npc_citizen') returned {npcs.Count}, expected 5" );
			for ( int i = 0; i < 5; i++ )
				Require( npcs[i].Id == i + 1 && npcs[i].TargetName == $"npc_0{i + 1}",
					$"npc {i} is id {npcs[i].Id} target '{npcs[i].TargetName}', expected id {i + 1} target 'npc_0{i + 1}'" );
		} );

		Check( "query by class is case-insensitive", () =>
		{
			var report = FreshReport();
			Require( report.QueryByClass( "NPC_CITIZEN" ).Count == 5, "QueryByClass('NPC_CITIZEN') did not return all 5 npcs" );
			Require( report.QueryByClass( "func_door" ).Count == 1, "QueryByClass('func_door') did not return 1" );
			Require( report.Query( new EntityReportQuery { ClassName = "light" } ).Count == 1, "Query{ClassName=light} did not return 1" );
		} );

		Check( "query by keyvalue substring", () =>
		{
			var report = FreshReport();
			var alpha = report.QueryByKeyValue( "squadname", "alpha" );
			Require( alpha.Count == 3, $"squadname 'alpha' matched {alpha.Count}, expected 3" );
			var beta = report.QueryByKeyValue( "SQUADNAME", "beta" );
			Require( beta.Count == 2, $"squadname 'beta' matched {beta.Count}, expected 2" );
			var model = report.QueryByKeyValue( "model", "female" );
			Require( model.Count == 2, $"model 'female' matched {model.Count}, expected 2" );
		} );

		Check( "query by keyvalue wildcard", () =>
		{
			var report = FreshReport();
			var npcs = report.QueryByKeyValue( "targetname", "npc_0?", EntityReportMatchMode.Wildcard );
			Require( npcs.Count == 5, $"wildcard 'npc_0?' matched {npcs.Count}, expected 5" );
			var one = report.QueryByKeyValue( "targetname", "npc_03", EntityReportMatchMode.Wildcard );
			Require( one.Count == 1 && one[0].TargetName == "npc_03", "wildcard 'npc_03' did not match exactly npc_03" );
		} );

		Check( "query by target pattern", () =>
		{
			var report = FreshReport();
			var doors = report.QueryByTarget( "door_*" , EntityReportMatchMode.Wildcard );
			Require( doors.Count == 1 && doors[0].TargetName == "door_01", $"target 'door_*' matched {doors.Count}, expected just door_01" );
			var all01 = report.QueryByTarget( "*_01", EntityReportMatchMode.Wildcard );
			Require( all01.Count == 6, $"target '*_01' matched {all01.Count}, expected 6" );
			var exact = report.FindByTargetName( "relay_01" );
			Require( exact.Count == 1 && exact[0].ClassName == "logic_relay", "FindByTargetName('relay_01') did not return the relay" );
		} );

		Check( "query exact mode and empty result", () =>
		{
			var report = FreshReport();
			var one = report.QueryByTarget( "npc_01", EntityReportMatchMode.Exact );
			Require( one.Count == 1, $"exact target 'npc_01' matched {one.Count}, expected 1" );
			Require( report.QueryByKeyValue( "squadname", "zzz" ).Count == 0, "squadname 'zzz' should match nothing" );
			Require( report.QueryByClass( "npc_zombie" ).Count == 0, "unknown class should match nothing" );
		} );

		Check( "select-all unknown or empty class returns empty", () =>
		{
			var report = FreshReport();
			Require( report.SelectAllByClass( "npc_zombie" ).Count == 0, "unknown class should select none" );
			Require( report.SelectAllByClass( "" ).Count == 0, "empty class should select none" );
			Require( report.SelectAllByClass( null ).Count == 0, "null class should select none" );
			Require( report.QueryByClass( null ).Count == 0, "null class query should return none" );
		} );

		Check( "bulk set keyvalue on all npcs", () =>
		{
			var report = FreshReport();
			var npcs = report.SelectAllByClass( "npc_citizen" );
			var result = report.BulkSetKeyvalue( npcs, "health", "150" );
			Require( result.Changed == 5, $"bulk set changed {result.Changed}, expected 5" );
			foreach ( var npc in npcs )
				Require( npc.GetKeyValue( "health" ) == "150", $"{npc.TargetName} health is '{npc.GetKeyValue( "health" )}', expected '150'" );
			var again = report.BulkSetKeyvalue( npcs, "health", "150" );
			Require( again.Changed == 0, "setting the same value again should change 0" );
		} );

		Check( "bulk set rejects mistyped value and unknown key", () =>
		{
			var report = FreshReport();
			var npcs = report.SelectAllByClass( "npc_citizen" );
			var badType = report.BulkSetKeyvalue( npcs, "health", "fast" );
			Require( badType.Changed == 0, $"mistyped bulk set changed {badType.Changed}, expected 0" );
			Require( badType.Entries.All( e => e.Error.Contains( "InvalidKeyValueType" ) ),
				"mistyped bulk set errors should carry InvalidKeyValueType" );
			Require( npcs.All( n => n.GetKeyValue( "health" ) != "fast" ), "mistyped value leaked into an entity" );
			var badKey = report.BulkSetKeyvalue( npcs, "nope", "1" );
			Require( badKey.Changed == 0 && badKey.Entries.All( e => e.Error.Contains( "UnknownKeyValue" ) ),
				"unknown-key bulk set should be refused with UnknownKeyValue" );
		} );

		Check( "bulk set targetname reports duplicates", () =>
		{
			var report = FreshReport();
			var npcs = report.SelectAllByClass( "npc_citizen" );
			var result = report.BulkSetKeyvalue( new[] { npcs[1] }, "targetname", "npc_01" );
			Require( result.Changed == 1, $"targetname bulk set changed {result.Changed}, expected 1" );
			Require( result.DuplicateTargetNames.Any( n => string.Equals( n, "npc_01", StringComparison.OrdinalIgnoreCase ) ),
				"renaming npc_02 to npc_01 should report 'npc_01' as a duplicate targetname" );
			Require( report.FindByTargetName( "npc_01" ).Count == 2, "two entities should now share targetname npc_01" );
		} );

		Check( "find/replace renames target across outputs and updates the I/O model", () =>
		{
			var report = FreshReport();
			var result = report.FindReplace( new FindReplaceRequest
			{
				FindTargetName = "door_01",
				ReplaceTargetName = "door_02"
			} );
			Require( result.Error == "", $"find/replace failed: {result.Error}" );
			Require( result.Matched == 1, $"matched {result.Matched}, expected 1 (the door)" );
			Require( result.Changed == 4, $"changed {result.Changed}, expected 4 (door, button, relay, spotlight)" );
			var door = report.FindByTargetName( "door_02" );
			Require( door.Count == 1 && door[0].ClassName == "func_door", "door targetname was not renamed to door_02" );
			Require( report.FindByTargetName( "door_01" ).Count == 0, "old targetname door_01 should be gone" );
			var button = report.FindByTargetName( "button_01" )[0];
			Require( button.Outputs.Count == 1 && button.Outputs[0].TargetName == "door_02",
				$"button output still targets '{button.Outputs[0].TargetName}', expected 'door_02'" );
			var relay = report.FindByTargetName( "relay_01" )[0];
			Require( relay.Outputs.Count == 1 && relay.Outputs[0].TargetName == "door_02",
				$"relay output still targets '{relay.Outputs[0].TargetName}', expected 'door_02'" );
			var spot = report.FindByTargetName( "spot_01" )[0];
			Require( spot.GetKeyValue( "target" ) == "door_02",
				$"spotlight target key is '{spot.GetKeyValue( "target" )}', expected 'door_02'" );
			var doorOut = door[0].Outputs[0];
			Require( doorOut.TargetName == "relay_01", $"door output was rewritten to '{doorOut.TargetName}', expected untouched 'relay_01'" );
			var changes = result.PerEntity.SelectMany( r => r.Changes ).ToArray();
			Require( changes.Any( c => c.Contains( "OnPressed" ) && c.Contains( "door_02" ) ),
				"per-entity result should list the button OnPressed output rewrite" );
			Require( changes.Any( c => c.Contains( "OnTrigger" ) && c.Contains( "door_02" ) ),
				"per-entity result should list the relay OnTrigger output rewrite" );
			var buttonResult = result.PerEntity.First( r => r.Entity == button );
			Require( !buttonResult.Matched && buttonResult.Changed,
				"button was not a find match but was changed by the target rename" );
		} );

		Check( "find/replace by keyvalue only touches matches", () =>
		{
			var report = FreshReport();
			var result = report.FindReplace( new FindReplaceRequest
			{
				FindKey = "squadname",
				FindValuePattern = "alpha",
				ReplaceValue = "beta"
			} );
			Require( result.Error == "", $"find/replace failed: {result.Error}" );
			Require( result.Matched == 3 && result.Changed == 3, $"matched {result.Matched} changed {result.Changed}, expected 3/3" );
			Require( report.QueryByKeyValue( "squadname", "beta" ).Count == 5, "all 5 npcs should now be squad beta" );
			Require( report.QueryByKeyValue( "squadname", "alpha" ).Count == 0, "no npc should still be squad alpha" );
		} );

		Check( "find/replace by class renames class", () =>
		{
			var report = FreshReport();
			var result = report.FindReplace( new FindReplaceRequest
			{
				FindClass = "npc_citizen",
				ReplaceClass = "npc_combine_s"
			} );
			Require( result.Error == "", $"find/replace failed: {result.Error}" );
			Require( result.Matched == 5 && result.Changed == 5, $"matched {result.Matched} changed {result.Changed}, expected 5/5" );
			Require( report.SelectAllByClass( "npc_citizen" ).Count == 0, "no npc_citizen should remain" );
			Require( report.SelectAllByClass( "npc_combine_s" ).Count == 5, "all 5 should now be npc_combine_s" );
		} );

		Check( "find/replace with no find criterion changes nothing", () =>
		{
			var report = FreshReport();
			var noFind = report.FindReplace( new FindReplaceRequest { ReplaceValue = "x" } );
			Require( noFind.Error != "" && noFind.Matched == 0 && noFind.Changed == 0,
				"a request with no find criterion should be refused with nothing changed" );
			var noReplace = report.FindReplace( new FindReplaceRequest { FindClass = "npc_citizen" } );
			Require( noReplace.Error != "" && noReplace.Changed == 0,
				"a request with nothing to replace should be refused with nothing changed" );
			Require( report.FindByTargetName( "door_01" ).Count == 1, "refused requests must leave the map untouched" );
		} );

		Check( "find/replace unknown replacement class is a per-entity error", () =>
		{
			var report = FreshReport();
			var result = report.FindReplace( new FindReplaceRequest
			{
				FindClass = "npc_citizen",
				ReplaceClass = "npc_zombie"
			} );
			Require( result.Matched == 5, $"matched {result.Matched}, expected 5" );
			Require( result.Changed == 0, $"changed {result.Changed}, expected 0" );
			Require( result.PerEntity.All( r => r.Error.Contains( "UnknownEntityClass" ) ),
				"each entity result should carry an UnknownEntityClass error" );
			Require( report.SelectAllByClass( "npc_citizen" ).Count == 5, "unknown replacement class must not rename anything" );
		} );

		Check( "brush number resolves to owning entity", () =>
		{
			var report = FreshReport();
			var doorBrush = report.BrushNumberLookup( 101 );
			Require( doorBrush.Found && doorBrush.Entity != null && doorBrush.Entity.ClassName == "func_door"
				&& doorBrush.Entity.TargetName == "door_01" && !doorBrush.IsWorldBrush,
				"brush 101 should resolve to the func_door entity" );
			var doorBrush2 = report.BrushNumberLookup( 102 );
			Require( doorBrush2.Found && doorBrush2.Entity == doorBrush.Entity, "brush 102 should resolve to the same door entity" );
			var buttonBrush = report.BrushNumberLookup( 103 );
			Require( buttonBrush.Found && buttonBrush.Entity != null && buttonBrush.Entity.TargetName == "button_01",
				"brush 103 should resolve to the func_button entity" );
			var world = report.BrushNumberLookup( 1 );
			Require( world.Found && world.IsWorldBrush && world.Entity is null, "brush 1 should resolve as a world brush" );
			var missing = report.BrushNumberLookup( 999 );
			Require( !missing.Found && missing.Reason != "", "brush 999 should be not-found with a reason" );
			var zero = report.BrushNumberLookup( 0 );
			Require( !zero.Found && zero.Reason != "", "brush 0 should be not-found with a reason" );
		} );

		Check( "summary counts by class and flags duplicates", () =>
		{
			var report = FreshReport();
			var summary = report.Summarize();
			Require( summary.Total == 11, $"summary total is {summary.Total}, expected 11" );
			Require( summary.ByClass["npc_citizen"] == 5, $"summary npc_citizen count is {summary.ByClass["npc_citizen"]}, expected 5" );
			Require( summary.ByClass["func_door"] == 1, "summary func_door count should be 1" );
			Require( summary.DuplicateTargetNames.Count == 0, "fixture map should have no duplicate targetnames" );
			Require( summary.EntitiesWithoutTargetName == 0, "every fixture entity has a targetname" );
			string text = report.ToText();
			Require( text.Contains( "Entity Report: 11 entities" ) && text.Contains( "npc_citizen: 5" ),
				$"report text is missing the headline counts: {text}" );
			var npcs = report.SelectAllByClass( "npc_citizen" );
			report.BulkSetKeyvalue( new[] { npcs[1] }, "targetname", "npc_01" );
			Require( report.Summarize().DuplicateTargetNames.Count == 1,
				"summary should flag the duplicated targetname after the rename" );
		} );

		Check( "wildcard matcher handles star, question, exact, case", () =>
		{
			Require( EntityReport.MatchesPattern( "npc_01", "npc_0?", EntityReportMatchMode.Wildcard, false ), "'npc_0?' should match 'npc_01'" );
			Require( !EntityReport.MatchesPattern( "npc_01", "npc_0?3", EntityReportMatchMode.Wildcard, false ), "'npc_0?3' should not match 'npc_01'" );
			Require( EntityReport.MatchesPattern( "door_01", "door_*", EntityReportMatchMode.Wildcard, false ), "'door_*' should match 'door_01'" );
			Require( EntityReport.MatchesPattern( "anything", "*", EntityReportMatchMode.Wildcard, false ), "'*' should match anything" );
			Require( !EntityReport.MatchesPattern( "", "?", EntityReportMatchMode.Wildcard, false ), "'?' should not match an empty value" );
			Require( EntityReport.MatchesPattern( "NPC_01", "npc_01", EntityReportMatchMode.Exact, false ), "exact match should be case-insensitive by default" );
			Require( !EntityReport.MatchesPattern( "NPC_01", "npc_01", EntityReportMatchMode.Exact, true ), "exact match should be case-sensitive when asked" );
			Require( EntityReport.MatchesPattern( "hello world", "WORLD", EntityReportMatchMode.Substring, false ), "substring should be case-insensitive by default" );
			Require( !EntityReport.MatchesPattern( "hello world", "WORLD", EntityReportMatchMode.Substring, true ), "substring should be case-sensitive when asked" );
		} );

		Check( "report builds from MapEntities with the landed API", () =>
		{
			var registry = LoadRegistry();
			var map = new MapEntities( registry );
			for ( int i = 1; i <= 5; i++ )
			{
				map.PlacePointEntity( "npc_citizen", new Vec3( i * 64, 0, 0 ),
					new Dictionary<string, string>
					{
						{ "targetname", $"npc_0{i}" },
						{ "squadname", i <= 3 ? "alpha" : "beta" },
						{ "health", "100" },
						{ "model", "models/humans/group01/male_01.mdl" }
					} );
			}
			map.PlacePointEntity( "light", new Vec3( 0, 0, 128 ),
				new Dictionary<string, string> { { "targetname", "light_01" }, { "_light", "255 240 200" } } );
			var report = EntityReport.FromMapEntities( map );
			Require( report.Count == 6, $"report from MapEntities has {report.Count} entities, expected 6" );
			var npcs = report.SelectAllByClass( "npc_citizen" );
			Require( npcs.Count == 5, $"SelectAllByClass('npc_citizen') returned {npcs.Count}, expected 5" );
			Require( npcs[0].Origin.x == 64 && npcs[0].Origin.y == 0 && npcs[0].Origin.z == 0,
				$"npc_01 origin is ({npcs[0].Origin.x}, {npcs[0].Origin.y}, {npcs[0].Origin.z}), expected (64, 0, 0)" );
			Require( report.FindByTargetName( "npc_01" ).Count == 1, "FindByTargetName('npc_01') should return 1" );
			Require( report.QueryByKeyValue( "squadname", "alpha" ).Count == 3, "squad alpha should have 3 npcs" );
			Require( report.QueryByClass( "light" ).Count == 1, "report should contain the placed light" );
			Require( map.FindByClass( "npc_citizen" ).Count == 5, "MapEntities itself should hold the 5 npcs" );
		} );

		Check( "brush entity from MapEntities resolves by brush number", () =>
		{
			var registry = LoadRegistry();
			var map = new MapEntities( registry );
			var brushes = new[] { new object(), new object() };
			var door = map.PlaceBrushEntity( "func_door", brushes,
				new Dictionary<string, string> { { "targetname", "door_01" }, { "speed", "100" } } );
			var report = EntityReport.FromMapEntities( map, null,
				entity => entity.Id == door.Id ? new[] { 101, 102 } : Array.Empty<int>() );
			var found = report.BrushNumberLookup( 101 );
			Require( found.Found && found.Entity != null && found.Entity.TargetName == "door_01"
				&& found.Entity.ClassName == "func_door",
				"brush 101 should resolve to the tied func_door entity" );
			var found2 = report.BrushNumberLookup( 102 );
			Require( found2.Found && found2.Entity == found.Entity, "brush 102 should resolve to the same door entity" );
			Require( !report.BrushNumberLookup( 103 ).Found, "brush 103 (not tied) should be not-found" );
		} );

		Check( "empty report queries and bulk edits are no-ops", () =>
		{
			var report = new EntityReport( null );
			Require( report.Count == 0, "empty report should have 0 entities" );
			Require( report.Query( new EntityReportQuery() ).Count == 0, "empty report query should return none" );
			Require( report.SelectAllByClass( "npc_citizen" ).Count == 0, "empty report select-all should return none" );
			var result = report.BulkSetKeyvalue( Array.Empty<ReportEntity>(), "health", "1" );
			Require( result.Changed == 0, "bulk set on an empty selection should change 0" );
			Require( !report.BrushNumberLookup( 5 ).Found, "empty report brush lookup should be not-found" );
			Require( report.Summarize().Total == 0, "empty report summary total should be 0" );
		} );
	}
}
