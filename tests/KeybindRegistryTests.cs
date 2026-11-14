using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy.Map;

/// <summary>
/// Tests for the keybind registry (hammer-keybind-registry):
/// default catalogue + documented Hammer hotkeys, Bind/Unbind/Remap with
/// named conflict detection, the discoverable catalogue query, and the
/// BuildScript-verbs catalogue import hook.
///
/// The catalogue is mirrored by the fixture
/// tests/fixtures/hammer/hammer-keybind-registry/catalogue.json and one
/// check asserts the in-code catalogue agrees with it exactly.
///
/// VERBS NOTE. The BuildScript verbs ticket (hammer-ai-buildscript-verbs-map)
/// had not landed when this registry was written (still todo, no
/// BuildScript.Map.cs in the tree), so the catalogue here is the
/// spec-derived one covering that ticket's scope, and the import checks run
/// against synthetic catalogues through ImportCatalogue.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// KeybindRegistryTests.Register(registry, settings); line.
/// </summary>
internal static class KeybindRegistryTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "keybindregistry", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		string FixturePath( string name )
		{
			string? dir = AppContext.BaseDirectory;
			while ( dir is not null )
			{
				string candidate = Path.Combine( dir, "tests", "fixtures", "hammer", "hammer-keybind-registry", name );
				if ( File.Exists( candidate ) ) return candidate;
				dir = Directory.GetParent( dir )?.FullName;
			}
			dir = Directory.GetCurrentDirectory();
			while ( dir is not null )
			{
				string candidate = Path.Combine( dir, "tests", "fixtures", "hammer", "hammer-keybind-registry", name );
				if ( File.Exists( candidate ) ) return candidate;
				dir = Directory.GetParent( dir )?.FullName;
			}
			throw new Exception( $"cannot locate fixture tests/fixtures/hammer/hammer-keybind-registry/{name}" );
		}

		Check( "default catalogue has 50 actions", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			Require( reg.Count == 50, $"default catalogue has {reg.Count} actions, expected 50" );
			Require( reg.Catalogue.Count == 50, $"Catalogue list has {reg.Catalogue.Count} entries, expected 50" );
		} );

		Check( "catalogue matches the fixture catalogue.json exactly", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			using var doc = JsonDocument.Parse( File.ReadAllText( FixturePath( "catalogue.json" ) ) );
			var fixture = doc.RootElement.EnumerateArray().ToList();
			Require( fixture.Count == reg.Count,
				$"fixture has {fixture.Count} actions, registry has {reg.Count}" );
			var byId = reg.Catalogue.ToDictionary( a => a.Id, StringComparer.Ordinal );
			foreach ( var item in fixture )
			{
				string id = item.GetProperty( "id" ).GetString()!;
				Require( byId.ContainsKey( id ), $"fixture action '{id}' is not in the registry" );
				var action = byId[id];
				Require( action.Name == item.GetProperty( "name" ).GetString(), $"'{id}': name '{action.Name}' vs fixture '{item.GetProperty( "name" ).GetString()}'" );
				Require( action.Category == item.GetProperty( "category" ).GetString(), $"'{id}': category '{action.Category}' vs fixture" );
				Require( action.Description == item.GetProperty( "description" ).GetString(), $"'{id}': description differs from fixture" );
				Require( action.DefaultBinding is not null && action.DefaultBinding.ToString() == item.GetProperty( "default" ).GetString(),
					$"'{id}': default '{action.DefaultBinding}' vs fixture '{item.GetProperty( "default" ).GetString()}'" );
			}
			Require( reg.Catalogue.Select( a => a.Id ).Distinct( StringComparer.Ordinal ).Count() == reg.Count,
				"registry catalogue contains duplicate action ids" );
		} );

		Check( "documented Hammer defaults: Shift+B block, Shift+X clip, Shift+V vertex, Shift+A face edit, Shift+E entity", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			Require( reg.GetBinding( "map.tool.block" )?.ToString() == "Shift+B", $"block is '{reg.GetBinding( "map.tool.block" )}', expected Shift+B" );
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Shift+X", $"clip is '{reg.GetBinding( "map.tool.clip" )}', expected Shift+X" );
			Require( reg.GetBinding( "map.tool.vertex" )?.ToString() == "Shift+V", $"vertex is '{reg.GetBinding( "map.tool.vertex" )}', expected Shift+V" );
			Require( reg.GetBinding( "map.tool.face" )?.ToString() == "Shift+A", $"face edit is '{reg.GetBinding( "map.tool.face" )}', expected Shift+A" );
			Require( reg.GetBinding( "map.tool.entity" )?.ToString() == "Shift+E", $"entity is '{reg.GetBinding( "map.tool.entity" )}', expected Shift+E" );
		} );

		Check( "every catalogue action has a binding and a description", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var entries = reg.ListBindings();
			Require( entries.Count == 50, $"ListBindings returned {entries.Count} entries, expected 50" );
			foreach ( var entry in entries )
			{
				Require( entry.Chord is not null, $"action '{entry.ActionId}' has no default binding" );
				Require( !string.IsNullOrWhiteSpace( entry.Description ), $"action '{entry.ActionId}' has no description" );
				Require( entry.IsDefault, $"action '{entry.ActionId}' is not on its default in a fresh registry" );
			}
			Require( entries.Select( e => e.Chord!.ToString() ).Distinct( StringComparer.Ordinal ).Count() == entries.Count,
				"default bindings are not unique: two actions share a chord" );
		} );

		Check( "remap Clip onto the Block key reports a conflict and refuses", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var result = reg.Remap( "map.tool.clip", "Shift+B" );
			Require( !result.Ok, "remapping Clip onto Shift+B succeeded; it must be refused" );
			Require( result.Error == KeybindErrorCode.Conflict, $"error is {result.Error}, expected Conflict" );
			Require( result.Conflict is not null, "conflict result carries no KeybindConflict" );
			Require( result.Conflict!.Chord.ToString() == "Shift+B", $"conflict chord is {result.Conflict.Chord}, expected Shift+B" );
			Require( result.Conflict.ExistingActionId == "map.tool.block", $"conflict names existing action '{result.Conflict.ExistingActionId}', expected 'map.tool.block'" );
			Require( result.Conflict.RequestedActionId == "map.tool.clip", $"conflict names requested action '{result.Conflict.RequestedActionId}', expected 'map.tool.clip'" );
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Shift+X", $"Clip binding moved to '{reg.GetBinding( "map.tool.clip" )}' after the refused remap" );
			Require( reg.GetBinding( "map.tool.block" )?.ToString() == "Shift+B", $"Block binding moved to '{reg.GetBinding( "map.tool.block" )}' after the refused remap" );
			Require( reg.BindingFor( "Shift+B" ) == "map.tool.block", "reverse lookup for Shift+B no longer resolves to Block" );
		} );

		Check( "Bind to a free key succeeds and frees the old key", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var result = reg.Remap( "map.tool.clip", "Ctrl+Alt+X" );
			Require( result.Ok, $"remap to free key failed: {result.Message}" );
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Ctrl+Alt+X", $"Clip is '{reg.GetBinding( "map.tool.clip" )}', expected Ctrl+Alt+X" );
			Require( reg.BindingFor( "Shift+X" ) is null, "Shift+X is still bound after Clip moved off it" );
			Require( reg.BindingFor( "Ctrl+Alt+X" ) == "map.tool.clip", "reverse lookup for Ctrl+Alt+X does not resolve to Clip" );
			Require( !reg.IsDefault( "map.tool.clip" ), "Clip still reports IsDefault after a remap" );
		} );

		Check( "Bind an unknown action reports UnknownAction and changes nothing", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var result = reg.Bind( "map.tool.carve", "Ctrl+Alt+C" );
			Require( !result.Ok, "binding an unknown action succeeded" );
			Require( result.Error == KeybindErrorCode.UnknownAction, $"error is {result.Error}, expected UnknownAction" );
			Require( reg.BindingFor( "Ctrl+Alt+C" ) is null, "unknown-action bind left a chord behind" );
			Require( reg.Count == 50, $"registry grew to {reg.Count} actions" );
		} );

		Check( "Bind an invalid chord reports InvalidChord", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			foreach ( string bad in new[] { "", "Shift", "Shift++B", "Ctrl+Ctrl+B", "B+Shift", "Shift+Foo", "Ctrl+Alt", "F13", "b+shift" } )
			{
				var result = reg.Bind( "map.tool.clip", bad );
				Require( !result.Ok, $"binding chord '{bad}' succeeded" );
				Require( result.Error == KeybindErrorCode.InvalidChord, $"chord '{bad}': error is {result.Error}, expected InvalidChord" );
			}
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Shift+X", "Clip binding moved after invalid binds" );
		} );

		Check( "Remap on an unbound action reports NotBound", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			Require( reg.Unbind( "map.tool.clip" ).Ok, "Unbind of Clip failed" );
			var result = reg.Remap( "map.tool.clip", "Ctrl+Alt+X" );
			Require( !result.Ok, "remapping an unbound action succeeded" );
			Require( result.Error == KeybindErrorCode.NotBound, $"error is {result.Error}, expected NotBound" );
		} );

		Check( "Unbind removes the binding and the catalogue shows it unbound; Bind restores it", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var unbound = reg.Unbind( "map.tool.clip" );
			Require( unbound.Ok, $"Unbind failed: {unbound.Message}" );
			Require( reg.GetBinding( "map.tool.clip" ) is null, "Clip still has a binding after Unbind" );
			var entry = reg.ListBindings().First( e => e.ActionId == "map.tool.clip" );
			Require( entry.Chord is null, "ListBindings still shows a chord for unbound Clip" );
			Require( !entry.IsDefault, "unbound Clip still reports IsDefault" );
			var again = reg.Unbind( "map.tool.clip" );
			Require( !again.Ok && again.Error == KeybindErrorCode.NotBound, $"second Unbind: ok={again.Ok}, error={again.Error}, expected NotBound" );
			var rebound = reg.Bind( "map.tool.clip", "Shift+X" );
			Require( rebound.Ok, $"re-binding Clip failed: {rebound.Message}" );
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Shift+X", "Clip did not return to Shift+X" );
		} );

		Check( "ResetToDefaults restores remaps and unbinds", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			reg.Remap( "map.tool.clip", "Ctrl+Alt+X" );
			reg.Unbind( "map.tool.block" );
			reg.ResetToDefaults();
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Shift+X", $"Clip is '{reg.GetBinding( "map.tool.clip" )}' after reset" );
			Require( reg.GetBinding( "map.tool.block" )?.ToString() == "Shift+B", $"Block is '{reg.GetBinding( "map.tool.block" )}' after reset" );
			Require( reg.Validate().Count == 0, $"registry unhealthy after reset: {string.Join( "; ", reg.Validate() )}" );
			var single = reg.ResetAction( "map.tool.clip" );
			Require( single.Ok, $"ResetAction failed: {single.Message}" );
		} );

		Check( "chord parsing normalises modifier order and case", () =>
		{
			Require( KeyChord.Parse( "shift+b" ).ToString() == "Shift+B", $"\"shift+b\" parsed as '{KeyChord.Parse( "shift+b" )}'" );
			Require( KeyChord.Parse( "CTRL+shift+x" ).ToString() == "Ctrl+Shift+X", $"\"CTRL+shift+x\" parsed as '{KeyChord.Parse( "CTRL+shift+x" )}'" );
			Require( KeyChord.Parse( "alt+CONTROL+f9" ).ToString() == "Ctrl+Alt+F9", $"\"alt+CONTROL+f9\" parsed as '{KeyChord.Parse( "alt+CONTROL+f9" )}'" );
			Require( KeyChord.Parse( "enter" ).ToString() == "Enter", $"\"enter\" parsed as '{KeyChord.Parse( "enter" )}'" );
			Require( KeyChord.Parse( "F9" ).ToString() == "F9", "F9 did not round-trip" );
			Require( KeyChord.Parse( "Shift+B" ).Equals( KeyChord.Parse( "shift+b" ) ), "equal chords do not compare equal" );
			Require( KeyChord.TryParse( "Shift+Foo", out _, out string? error ) == false && error is not null, "TryParse accepted an unknown key" );
		} );

		Check( "ImportCatalogue adds new actions with their defaults", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var result = reg.ImportCatalogue( new[]
			{
				new MapActionDefinition( "map.view.frame", "Frame Selection", "View",
					"Frame the selected objects in the 3D view.", KeyChord.Parse( "Ctrl+Shift+J" ) ),
			} );
			Require( result.Ok, $"import failed: {result.Message}" );
			Require( reg.Count == 51, $"registry has {reg.Count} actions after import, expected 51" );
			Require( reg.GetBinding( "map.view.frame" )?.ToString() == "Ctrl+Shift+J", "imported action did not get its default binding" );
			Require( reg.ListBindings( "View" ).Count == 1, "category filter did not find the imported View action" );
		} );

		Check( "ImportCatalogue refuses duplicate ids and preserves a user remap on overwrite", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var dup = reg.ImportCatalogue( new[]
			{
				new MapActionDefinition( "map.tool.clip", "Clip Tool", "Tool", "Duplicate.", KeyChord.Parse( "Ctrl+Alt+X" ) ),
			} );
			Require( !dup.Ok && dup.Error == KeybindErrorCode.DuplicateAction, $"duplicate import: ok={dup.Ok}, error={dup.Error}, expected DuplicateAction" );
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Shift+X", "refused duplicate import changed Clip" );

			// Overwrite with a user remap in place: new description lands, remap is kept.
			reg.Remap( "map.tool.clip", "Ctrl+Alt+X" );
			var over = reg.ImportCatalogue( new[]
			{
				new MapActionDefinition( "map.tool.clip", "Clip Tool (2-point)", "Tool",
					"Cut brushes with a 2-point clip line (revised).", KeyChord.Parse( "Shift+Y" ) ),
			}, overwriteExisting: true );
			Require( over.Ok, $"overwrite import failed: {over.Message}" );
			Require( reg.FindAction( "map.tool.clip" )?.Description.Contains( "revised" ) == true, "overwrite did not replace the description" );
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Ctrl+Alt+X", $"overwrite moved the user remap to '{reg.GetBinding( "map.tool.clip" )}'" );

			// Overwrite while still on the old default: the binding moves to the new default.
			var reg2 = KeybindRegistry.CreateDefault();
			var over2 = reg2.ImportCatalogue( new[]
			{
				new MapActionDefinition( "map.tool.clip", "Clip Tool (2-point)", "Tool",
					"Cut brushes with a 2-point clip line (revised).", KeyChord.Parse( "Shift+Y" ) ),
			}, overwriteExisting: true );
			Require( over2.Ok, $"overwrite of a default-bound action failed: {over2.Message}" );
			Require( reg2.GetBinding( "map.tool.clip" )?.ToString() == "Shift+Y", $"still-default action did not move to the new default, is '{reg2.GetBinding( "map.tool.clip" )}'" );
			Require( reg2.BindingFor( "Shift+X" ) is null, "old default Shift+X is still bound after the overwrite" );
		} );

		Check( "ImportCatalogue refuses a default chord that collides with another action", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var result = reg.ImportCatalogue( new[]
			{
				new MapActionDefinition( "map.view.frame", "Frame Selection", "View",
					"Frame the selected objects in the 3D view.", KeyChord.Parse( "Shift+B" ) ),
			} );
			Require( !result.Ok && result.Error == KeybindErrorCode.Conflict, $"colliding import: ok={result.Ok}, error={result.Error}, expected Conflict" );
			Require( result.Conflict?.ExistingActionId == "map.tool.block", $"conflict names '{result.Conflict?.ExistingActionId}', expected 'map.tool.block'" );
			Require( reg.Count == 50, "refused import added the action anyway" );
		} );

		Check( "ImportCatalogue overwrite where one action takes another's old default stays consistent", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var result = reg.ImportCatalogue( new[]
			{
				new MapActionDefinition( "map.tool.clip", "Clip Tool (2-point)", "Tool",
					"Cut brushes with a 2-point clip line (revised).", KeyChord.Parse( "Shift+B" ) ),
				new MapActionDefinition( "map.tool.block", "Block Tool", "Tool",
					"Create convex block brushes by dragging a box in a 2D view.", KeyChord.Parse( "Ctrl+Alt+J" ) ),
			}, overwriteExisting: true );
			Require( result.Ok, $"overwrite import failed: {result.Message}" );
			Require( reg.GetBinding( "map.tool.clip" )?.ToString() == "Shift+B", $"Clip is '{reg.GetBinding( "map.tool.clip" )}', expected Shift+B" );
			Require( reg.GetBinding( "map.tool.block" )?.ToString() == "Ctrl+Alt+J", $"Block is '{reg.GetBinding( "map.tool.block" )}', expected Ctrl+Alt+J" );
			Require( reg.BindingFor( "Shift+B" ) == "map.tool.clip", "reverse lookup for Shift+B does not resolve to Clip" );
			Require( reg.Validate().Count == 0, $"registry unhealthy after overwrite: {string.Join( "; ", reg.Validate() )}" );
		} );

		Check( "Search finds actions by id, name and description", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var clip = reg.Search( "clip" );
			Require( clip.Count == 2, $"search 'clip' found {clip.Count}, expected 2 (tool + brush)" );
			Require( clip.Any( e => e.ActionId == "map.tool.clip" ) && clip.Any( e => e.ActionId == "map.brush.clip" ), "search 'clip' missed a clip action" );
			var cubemap = reg.Search( "CUBEMAP" );
			Require( cubemap.Count == 1 && cubemap[0].ActionId == "map.bake.cubemap", "case-insensitive search 'CUBEMAP' did not find the cubemap action" );
			Require( reg.Search( "no such action" ).Count == 0, "search for nonsense returned results" );
			Require( reg.Search( "" ).Count == 50, "empty search did not list the whole catalogue" );
		} );

		Check( "BindingFor reverse lookup resolves the Block key and misses a free key", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			Require( reg.BindingFor( KeyChord.Parse( "Shift+B" ) ) == "map.tool.block", "reverse lookup for Shift+B missed Block" );
			Require( reg.BindingFor( "Shift+B" ) == "map.tool.block", "string reverse lookup for Shift+B missed Block" );
			Require( reg.BindingFor( "Ctrl+Alt+Z" ) is null, "free chord Ctrl+Alt+Z resolved to an action" );
			Require( reg.BindingFor( "not a chord" ) is null, "unparseable chord text resolved to an action" );
		} );

		Check( "ListBindings(category) filters and Validate() is clean on defaults", () =>
		{
			var reg = KeybindRegistry.CreateDefault();
			var tools = reg.ListBindings( "Tool" );
			Require( tools.Count == 8, $"Tool category has {tools.Count} entries, expected 8" );
			Require( tools.All( e => e.Category == "Tool" ), "Tool filter returned another category" );
			Require( reg.ListBindings( "tool" ).Count == 8, "category filter is not case-insensitive" );
			Require( reg.ListBindings( "Nope" ).Count == 0, "unknown category returned entries" );
			Require( reg.Validate().Count == 0, $"fresh registry is unhealthy: {string.Join( "; ", reg.Validate() )}" );
		} );
	}
}
