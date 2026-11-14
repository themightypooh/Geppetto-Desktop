using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

// Entity I/O node-graph data model + entity scripting hooks:
// hammer-io-node-graph-scripting-hooks. Group "entityiograph". Fixtures live in
// tests/fixtures/hammer/hammer-io-node-graph-scripting-hooks/.
// The graph and the list model (EntityIoList, group "entityio") edit the same
// data: every round-trip test below compares full rows, not just counts.
internal static class EntityIoGraphTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "entityiograph", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}

		// The acceptance map: a button fires a relay, the relay opens a door and
		// turns on a light; a logic_auto fires the relay on map spawn.
		EntityIoList ButtonDoorRelayList()
		{
			var list = new EntityIoList();
			Require( list.Add( new EntityIoOutput( "button_01", "OnPressed", "relay_01", "Trigger", 0.25 ) ) is null, "button->relay row rejected" );
			Require( list.Add( new EntityIoOutput( "auto_01", "OnMapSpawn", "relay_01", "Trigger", 0, 1 ) ) is null, "auto->relay row rejected" );
			Require( list.Add( new EntityIoOutput( "relay_01", "OnTrigger", "door_01", "Open", 0.5 ) ) is null, "relay->door row rejected" );
			Require( list.Add( new EntityIoOutput( "relay_01", "OnTrigger", "light_01", "TurnOn" ) ) is null, "relay->light row rejected" );
			return list;
		}

		string RelayClass( string entity ) => entity == "relay_01" ? "logic_relay" : "";

		string RowKey( EntityIoOutput row ) =>
			$"{row.SourceEntity}|{row.Event}|{row.TargetName}|{row.Input}|{row.Parameter}|{row.DelaySeconds}|{row.MaxFires}|{row.Enabled}";

		void RequireSameRows( EntityIoList a, EntityIoList b, string label )
		{
			var aKeys = a.Outputs.Select( RowKey ).OrderBy( k => k ).ToList();
			var bKeys = b.Outputs.Select( RowKey ).OrderBy( k => k ).ToList();
			Require( aKeys.SequenceEqual( bKeys ),
				$"{label}: rows differ.\n  left:  {string.Join( "; ", aKeys )}\n  right: {string.Join( "; ", bKeys )}" );
		}

		// Walk up from the cwd and from the test binary looking for a repo-relative path.
		string FindRepoPath( string relative )
		{
			foreach ( string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory } )
			{
				string dir = Path.GetFullPath( start );
				while ( dir is not null )
				{
					string candidate = Path.Combine( dir, relative );
					if ( File.Exists( candidate ) || Directory.Exists( candidate ) ) return candidate;
					string parent = Path.GetDirectoryName( dir );
					if ( parent is null || parent == dir ) break;
					dir = parent;
				}
			}
			throw new Exception( $"repo path not found under cwd or AppContext.BaseDirectory: {relative}" );
		}

		string FixtureDir() => FindRepoPath( "tests/fixtures/hammer/hammer-io-node-graph-scripting-hooks" );

		EntityScriptHook ValidHook( string entity = "button_01", string entryPoint = "OnPressed" ) =>
			new( entity, entryPoint, "lua", "function OnPressed()\n  Fire(\"door_01\", \"Open\")\nend\n",
				false, new List<string> { "Open" } );

		Check( "button->door + relay list round-trips through the graph with the relay as a sub-graph", () =>
		{
			var list = ButtonDoorRelayList();
			var graph = EntityIoGraph.FromList( list, RelayClass );
			Require( graph.IsValid, "graph built from a valid list is invalid: " + string.Join( "; ", graph.Validate() ) );
			Require( graph.NodeCount == 11, $"node count {graph.NodeCount}, expected 11 (5 entities + 3 events + 3 inputs)" );
			Require( graph.EdgeCount == 4, $"edge count {graph.EdgeCount}, expected 4" );
			Require( graph.SubGraphCount == 1, $"sub-graph count {graph.SubGraphCount}, expected 1 (relay_01)" );
			var relay = graph.SubGraphs[0];
			Require( relay.RelayEntity == "relay_01" && relay.Name == "relay_01", "sub-graph is not the relay" );
			Require( relay.EntryNodeId == "input:relay_01:Trigger", "relay entry is not the Trigger input node: " + relay.EntryNodeId );
			Require( relay.OutputNodeId == "event:relay_01:OnTrigger", "relay exit is not the OnTrigger event node: " + relay.OutputNodeId );
			Require( relay.MemberNodeIds.Count == 3
				&& relay.MemberNodeIds.Contains( "entity:relay_01" )
				&& relay.MemberNodeIds.Contains( "event:relay_01:OnTrigger" )
				&& relay.MemberNodeIds.Contains( "input:relay_01:Trigger" ),
				"relay sub-graph members are wrong: " + string.Join( ",", relay.MemberNodeIds ) );
			var back = graph.ToList();
			RequireSameRows( list, back, "list->graph->list" );
		} );

		Check( "graph JSON round-trips nodes, edges, sub-graphs and scripts", () =>
		{
			var graph = EntityIoGraph.FromList( ButtonDoorRelayList(), RelayClass );
			Require( graph.ScriptHooks.Add( ValidHook() ) is null, "valid hook was rejected" );
			string json = graph.ToJson();
			var back = EntityIoGraph.FromJson( json );
			Require( back.NodeCount == graph.NodeCount && back.EdgeCount == graph.EdgeCount && back.SubGraphCount == graph.SubGraphCount,
				"graph JSON round trip changed counts" );
			Require( back.ScriptHooks.Count == 1 && back.ScriptHooks.Find( "button_01", "OnPressed" ) is not null,
				"graph JSON round trip lost the script hook" );
			Require( back.Nodes.Select( n => n.Id ).OrderBy( id => id ).SequenceEqual( graph.Nodes.Select( n => n.Id ).OrderBy( id => id ) ),
				"graph JSON round trip changed node ids" );
			var layout = back.FindNode( "event:button_01:OnPressed" );
			var original = graph.FindNode( "event:button_01:OnPressed" );
			Require( layout.X == original.X && layout.Y == original.Y, "graph JSON round trip lost layout" );
			RequireSameRows( ButtonDoorRelayList(), back.ToList(), "graph JSON -> list" );
			Require( back.ToJson() == json, "graph JSON is not stable across a round trip" );
		} );

		Check( "disabled rows, delays, parameters and fire-once survive the round-trip", () =>
		{
			var list = new EntityIoList();
			Require( list.Add( new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open", 1.25, 1, "fast", false ) ) is null, "row rejected" );
			Require( list.Add( new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open", 1.25, 1, "fast", false ) ) is null, "duplicate row rejected" );
			var graph = EntityIoGraph.FromList( list );
			Require( graph.EdgeCount == 2, "duplicate rows must stay two edges" );
			var back = graph.ToList();
			RequireSameRows( list, back, "round trip" );
			Require( back.Outputs.All( o => !o.Enabled && o.DelaySeconds == 1.25 && o.MaxFires == 1 && o.Parameter == "fast" ),
				"round trip lost disabled/delay/parameter/max-fires" );
		} );

		Check( "the button-door-relay fixtures load and agree (list JSON, graph JSON)", () =>
		{
			var fromListJson = EntityIoList.FromJson( File.ReadAllText( Path.Combine( FixtureDir(), "button-door-relay.json" ) ) );
			var fromGraphJson = EntityIoGraph.FromJson( File.ReadAllText( Path.Combine( FixtureDir(), "button-door-relay-graph.json" ) ) );
			Require( fromListJson.Count == 4, $"list fixture has {fromListJson.Count} rows, expected 4" );
			Require( fromGraphJson.EdgeCount == 4 && fromGraphJson.SubGraphCount == 1, "graph fixture shape is wrong" );
			RequireSameRows( fromListJson, fromGraphJson.ToList(), "list fixture vs graph fixture" );
			RequireSameRows( ButtonDoorRelayList(), fromListJson, "list fixture vs built list" );
			var rebuilt = EntityIoGraph.FromList( fromListJson, RelayClass );
			Require( rebuilt.SubGraphs.Count == 1 && rebuilt.SubGraphs[0].RelayEntity == "relay_01", "rebuilt graph lost the relay sub-graph" );
			RequireSameRows( fromListJson, rebuilt.ToList(), "fixture list -> graph -> list" );
		} );

		Check( "a script hook calling os.execute is rejected (disallowed-hook.lua fixture)", () =>
		{
			string source = File.ReadAllText( Path.Combine( FixtureDir(), "disallowed-hook.lua" ) );
			Require( source.Contains( "os.execute" ), "fixture does not contain os.execute" );
			var registry = new EntityScriptHookRegistry();
			var hook = new EntityScriptHook( "button_01", "OnPressed", "lua", source );
			string error = registry.Add( hook );
			Require( error is not null, "os.execute hook was accepted" );
			Require( error.Contains( "os.execute" ), "rejection does not name the forbidden operation: " + error );
			Require( registry.Count == 0, "rejected hook was stored" );
			Require( registry.AuditLog.Count == 1 && registry.AuditLog[0].Contains( "rejected" ), "rejection was not audit-logged" );
			Require( !registry.AuditLog[0].Contains( "pwned" ), "audit log leaked script content" );
		} );

		Check( "other forbidden script calls are rejected: io.popen, loadfile, dofile, shell_exec, require(os)", () =>
		{
			var registry = new EntityScriptHookRegistry();
			var cases = new Dictionary<string, string>
			{
				{ "io.popen", "local f = io.popen(\"ls\")" },
				{ "loadfile", "local f = loadfile(\"/tmp/evil.lua\")" },
				{ "dofile", "dofile(\"/tmp/evil.lua\")" },
				{ "shell_exec", "shell_exec(\"id\")" },
				{ "require(os)", "local os = require(\"os\")" },
				{ "os.remove", "os.remove(\"/tmp/x\")" },
			};
			foreach ( var pair in cases )
			{
				string error = registry.Add( new EntityScriptHook( "button_01", "OnPressed", "lua", pair.Value ) );
				Require( error is not null, $"{pair.Key} was accepted" );
				Require( error.Contains( "forbidden operation" ), $"{pair.Key} rejection does not say forbidden: {error}" );
			}
			Require( registry.Count == 0, "a rejected hook was stored" );
			Require( registry.AuditLog.Count == cases.Count, "not every rejection was audit-logged" );
		} );

		Check( "a destructive script operation needs the explicit flag", () =>
		{
			var registry = new EntityScriptHookRegistry();
			var hook = new EntityScriptHook( "button_01", "OnPressed", "lua", "function OnPressed() end",
				false, new List<string> { "Remove" } );
			string error = registry.Add( hook );
			Require( error is not null, "destructive operation without the flag was accepted" );
			Require( error.Contains( "Remove" ) && error.Contains( "allow_destructive" ), "rejection does not name the op and the flag: " + error );
			Require( registry.Count == 0, "rejected hook was stored" );
			var flagged = new EntityScriptHook( "button_01", "OnPressed", "lua", "function OnPressed() end",
				true, new List<string> { "Remove" } );
			Require( registry.Add( flagged ) is null, "destructive operation WITH the flag was rejected" );
			Require( registry.Count == 1, "flagged hook was not stored" );
			Require( registry.AuditLog.Any( entry => entry.Contains( "accepted" ) && entry.Contains( "allow_destructive=True" ) ),
				"flagged acceptance was not audit-logged with the flag" );
		} );

		Check( "a script containing a secret value is rejected without echoing the value", () =>
		{
			var registry = new EntityScriptHookRegistry();
			const string fakeKey = "sk-abcdefghij1234567890";
			var cases = new Dictionary<string, string>
			{
				{ "api_key assignment", "local api_key = \"hunter2-secret-value\"" },
				{ "sk- key", "local k = \"" + fakeKey + "\"" },
				{ "GitHub token", "local t = \"ghp_abcdefghij1234567890\"" },
			};
			foreach ( var pair in cases )
			{
				string error = registry.Add( new EntityScriptHook( "button_01", "OnPressed", "lua", pair.Value ) );
				Require( error is not null, $"{pair.Key} was accepted" );
				Require( error.Contains( "secret" ), $"{pair.Key} rejection does not say secret: {error}" );
				Require( !error.Contains( "hunter2" ) && !error.Contains( fakeKey ) && !error.Contains( "ghp_abcdefghij" ),
					$"{pair.Key} rejection echoed the secret value" );
			}
			Require( registry.Count == 0, "a secret-bearing hook was stored" );
		} );

		Check( "only Lua/VScript scripts are accepted; vscript normalises to lua", () =>
		{
			var registry = new EntityScriptHookRegistry();
			foreach ( string language in new[] { "python", "bash", "javascript", "" } )
			{
				string error = registry.Add( new EntityScriptHook( "button_01", "OnPressed", language, "x = 1" ) );
				Require( error is not null, $"language '{language}' was accepted" );
				Require( error.Contains( "Lua/VScript" ), $"language '{language}' rejection does not explain the rule: {error}" );
			}
			Require( registry.Add( new EntityScriptHook( "button_01", "OnPressed", "vscript", "function OnPressed() end" ) ) is null,
				"vscript was rejected" );
			Require( registry.Find( "button_01", "OnPressed" ).Language == "lua", "vscript was not normalised to lua" );
			Require( registry.Count == 1, "only the vscript hook should be stored" );
		} );

		Check( "a valid hook is stored, listed per entity, and survives JSON", () =>
		{
			var registry = new EntityScriptHookRegistry();
			Require( registry.Add( ValidHook() ) is null, "valid hook was rejected" );
			Require( registry.Add( new EntityScriptHook( "door_01", "OnOpen", "lua", "function OnOpen() end" ) ) is null, "second hook rejected" );
			Require( registry.Count == 2, "hook count is wrong" );
			Require( registry.HooksFor( "button_01" ).Count == 1 && registry.HooksFor( "door_01" ).Count == 1, "HooksFor is wrong" );
			Require( registry.HooksFor( "relay_01" ).Count == 0, "HooksFor returned hooks for the wrong entity" );
			var back = EntityScriptHookRegistry.FromJson( registry.ToJson() );
			Require( back.Count == 2, "registry JSON round trip changed the count" );
			Require( back.Find( "button_01", "OnPressed" ).Equals( ValidHook() ), "registry JSON round trip changed the hook" );
			Require( back.Remove( "button_01", "OnPressed" ), "remove failed" );
			Require( back.Count == 1 && back.Find( "button_01", "OnPressed" ) is null, "removed hook is still there" );
			Require( !back.Remove( "button_01", "OnPressed" ), "removing a missing hook reported success" );
			Throws<ArgumentException>( () => EntityScriptHookRegistry.FromJson( "{\"version\": 2, \"hooks\": []}" ) );
			Throws<ArgumentException>( () => EntityScriptHookRegistry.FromJson( "not json" ) );
		} );

		Check( "graph validation names dangling edges, wrong endpoint kinds and duplicate node ids", () =>
		{
			var graph = EntityIoGraph.FromList( ButtonDoorRelayList(), RelayClass );
			Require( graph.IsValid, "built graph should be valid" );
			Require( graph.AddEdge( new EntityIoEdge( "e99", "event:button_01:OnPressed", "input:door_99:Open" ) ) is not null,
				"edge to a missing node was added" );
			Require( graph.AddEdge( new EntityIoEdge( "e99", "entity:button_01", "input:door_01:Open" ) ) is not null,
				"edge from an entity node (not an event node) was added" );
			Require( graph.AddNode( new EntityIoNode( "entity:button_01", EntityIoNodeKind.Entity, "button_01" ) ) is not null,
				"duplicate node id was added" );
			Require( graph.IsValid, "rejected adds must not invalidate the graph" );
			Require( graph.TryToList( out _, out _ ), "valid graph should convert" );
			var broken = EntityIoGraph.FromList( ButtonDoorRelayList(), RelayClass );
			Require( broken.RemoveNode( "input:light_01:TurnOn" ), "remove failed" );
			Require( broken.IsValid, "removing a node must remove its incident edges and stay valid" );
			Require( broken.EdgeCount == 3, "the incident edge was not removed with the node" );
			Require( broken.ToList().Count == 3, "the removed edge still converts" );
			// A dangling edge cannot be built through AddEdge (it rejects missing
			// endpoints), so reach it the way mutable rows allow: rename a node
			// after construction, orphaning the edge that pointed at it.
			var dangling = EntityIoGraph.FromList( ButtonDoorRelayList(), RelayClass );
			dangling.FindNode( "input:door_01:Open" ).Id = "input:door_01:Renamed";
			Require( !dangling.IsValid, "graph with a dangling edge validated clean" );
			Require( dangling.Validate().Any( e => e.Contains( "does not exist" ) ), "validation does not name the dangling edge: " + string.Join( "; ", dangling.Validate() ) );
			Require( !dangling.TryToList( out _, out string error ) && error.Contains( "does not exist" ),
				"TryToList did not report the dangling edge" );
			Throws<ArgumentException>( () => dangling.ToList() );
			Throws<ArgumentException>( () => EntityIoGraph.FromJson( "{\"version\": 2, \"nodes\": [], \"edges\": []}" ) );
		} );

		Check( "no class resolver means no sub-graphs; CreateRelaySubGraph reports a missing relay", () =>
		{
			var graph = EntityIoGraph.FromList( ButtonDoorRelayList() );
			Require( graph.SubGraphCount == 0, "sub-graphs appeared without a class resolver" );
			RequireSameRows( ButtonDoorRelayList(), graph.ToList(), "no-resolver round trip" );
			string error = graph.CreateRelaySubGraph( "door_99", out EntityIoSubGraph missing );
			Require( error is not null && missing is null, "missing relay was grouped without an error" );
			Require( error.Contains( "door_99" ), "error does not name the relay: " + error );
			Require( graph.CreateRelaySubGraph( "relay_01", out EntityIoSubGraph relay ) is null, "relay grouping failed: " + error );
			Require( relay is not null && graph.SubGraphCount == 1, "relay sub-graph was not added" );
			Require( graph.CreateRelaySubGraph( "relay_01", out _ ) is not null, "grouping the same relay twice succeeded" );
			Require( graph.CreateRelaySubGraph( "door_01", out EntityIoSubGraph door ) is null, "a non-relay entity should still group (resolver is the caller's choice)" );
			Require( door.EntryNodeId == "" && door.OutputNodeId == "", "door sub-graph should have no Trigger/OnTrigger ports" );
		} );

		Check( "hook names, entry points and oversized sources are rejected", () =>
		{
			var registry = new EntityScriptHookRegistry();
			Require( registry.Add( new EntityScriptHook( "", "OnPressed", "lua", "x = 1" ) ) is not null, "empty entity accepted" );
			Require( registry.Add( new EntityScriptHook( "button_01", "On Pressed", "lua", "x = 1" ) ) is not null, "entry point with a space accepted" );
			Require( registry.Add( new EntityScriptHook( "button_01", "1Pressed", "lua", "x = 1" ) ) is not null, "entry point starting with a digit accepted" );
			Require( registry.Add( new EntityScriptHook( "button_01", "OnPressed", "lua", new string( 'x', EntityScriptHookRegistry.MaxSourceChars + 1 ) ) ) is not null,
				"oversized source accepted" );
			Require( registry.Add( new EntityScriptHook( "button_01", "OnPressed", "lua", "" ) ) is not null, "empty source accepted" );
			Require( registry.Count == 0, "a rejected hook was stored" );
			Require( registry.Add( ValidHook() ) is null, "valid hook rejected after rejections" );
			Require( registry.Add( ValidHook() ) is null, "re-adding the same hook (replace) failed" );
			Require( registry.Count == 1, "replace duplicated the hook" );
		} );
	}
}
