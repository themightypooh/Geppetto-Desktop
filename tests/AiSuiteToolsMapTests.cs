#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

// hammer-ai-aisuitetools-apicatalog-map (muse-3-desktop-hammer-69):
// AiSuiteTools + ApiCatalog must list exactly the real native map ops —
// no phantom ops, no missing ops. The capability listing is generated from
// the registry (BuildScript.MapVerbNames, via ApiCatalog.MapVerbNames),
// never hand-written. The canonical verb list lives in
// BuildScript.Map.cs (hammer-ai-buildscript-verbs-map, worker 64); until
// it lands, these checks run against the spec stub in the isolated
// harness (see tests/fixtures/hammer/hammer-ai-aisuitetools-apicatalog-map/SEAM.md).
// Execution of the verbs (sealed-room script, validation + Quick Play) is
// worker 64's acceptance, not this file's: this file pins the wiring.
internal static class AiSuiteToolsMapTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "aisuitetoolsmap", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		// The filed spec verb set (hammer-ai-buildscript-verbs-map scope, one
		// verb per capability, in spec order). If worker 64 lands a different
		// set, check 1 fails and the coordinator reconciles the spec instead
		// of silently shipping a drifted catalog.
		string[] SpecVerbs = {
			"map_brush_create", "map_brush_edit", "map_brush_clip", "map_brush_vertex",
			"map_displacement_create", "map_displacement_sculpt",
			"map_face_material", "map_face_uv",
			"map_entity_place", "map_entity_edit", "map_entity_io_add",
			"map_group", "map_visgroup",
			"map_import", "map_export",
			"map_compile", "map_validate", "map_lightmap_bake",
			"map_navmesh", "map_skybox",
		};

		AiSuiteTools NewTools( string tag ) =>
			new( Path.Combine( settings.WorkDirectory, "aisuitetools-map-" + tag + "-" + Guid.NewGuid().ToString( "N" ) ) );

		// 1. Registered verbs equal the filed spec list, in order.
		Check( "map verbs equal the filed spec list", () =>
		{
			var registered = ApiCatalog.MapVerbNames();
			Require( registered.SequenceEqual( SpecVerbs ),
				$"Registered map verbs differ from the spec list. Registered: {string.Join( ", ", registered )}. Spec: {string.Join( ", ", SpecVerbs )}." );
		} );

		// 2. Verb names are well-formed: lowercase, map_-prefixed, unique.
		Check( "map verb names are lowercase map_-prefixed and unique", () =>
		{
			var verbs = ApiCatalog.MapVerbNames();
			Require( verbs.Count == verbs.Distinct( StringComparer.Ordinal ).Count(), "Duplicate map verb." );
			foreach ( var verb in verbs )
			{
				Require( verb.StartsWith( "map_", StringComparison.Ordinal ), $"Verb '{verb}' must start with map_." );
				Require( verb == verb.ToLowerInvariant(), $"Verb '{verb}' must be lowercase." );
				Require( !verb.Contains( ' ' ), $"Verb '{verb}' must be a single token (underscores, not spaces)." );
			}
		} );

		// 3. Capability list equals the registered verb set: the AI surface
		// reads the same registry as the catalog, and Definitions carries
		// exactly one map_command tool taking a script.
		Check( "capability list equals the registered verb set", () =>
		{
			var fromTools = AiSuiteTools.MapVerbs;
			var fromCatalog = ApiCatalog.MapVerbNames();
			Require( new HashSet<string>( fromTools, StringComparer.Ordinal ).SetEquals( fromCatalog ),
				$"AiSuiteTools.MapVerbs differs from ApiCatalog.MapVerbNames. Tools: {string.Join( ", ", fromTools )}. Catalog: {string.Join( ", ", fromCatalog )}." );
			var mapTools = AiSuiteTools.Definitions
				.Select( t => t!.AsObject() )
				.Where( t => t["name"]!.GetValue<string>() == "map_command" )
				.ToList();
			Require( mapTools.Count == 1, $"Expected exactly one map_command tool in Definitions, found {mapTools.Count}." );
			var required = mapTools[0]["parameters"]!["required"]!.AsArray().Select( n => n!.GetValue<string>() ).ToList();
			Require( required.Contains( "script" ), "map_command must require a script parameter." );
		} );

		// 4. Capability response contains map_brush_create etc.: every spec
		// verb in the tool surface, the generated Markdown and the JSON schema.
		Check( "capability response contains map_brush_create etc.", () =>
		{
			var verbs = AiSuiteTools.MapVerbs;
			foreach ( var verb in SpecVerbs )
				Require( verbs.Contains( verb, StringComparer.Ordinal ), $"Capability list is missing {verb}." );
			string markdown = ApiCatalog.GenerateMarkdown();
			foreach ( var verb in SpecVerbs )
				Require( markdown.Contains( "`" + verb + "`", StringComparison.Ordinal ), $"Generated Markdown is missing `{verb}`." );
			Require( markdown.Contains( "MapBrushPrimitives", StringComparison.Ordinal ), "Generated Markdown must name the brush-create backing API." );
			Require( markdown.Contains( "PlaytestLoop", StringComparison.Ordinal ), "Generated Markdown must name the navmesh backing contract." );
			using var schema = JsonDocument.Parse( ApiCatalog.GenerateJsonSchema() );
			var schemaVerbs = schema.RootElement.GetProperty( "mapVerbs" ).EnumerateArray().Select( e => e.GetString() ).ToArray();
			Require( schemaVerbs.SequenceEqual( SpecVerbs ), "JSON schema mapVerbs must equal the spec list in order." );
			var backingKeys = schema.RootElement.GetProperty( "mapVerbBacking" ).EnumerateObject().Select( p => p.Name ).ToList();
			Require( new HashSet<string>( backingKeys, StringComparer.Ordinal ).SetEquals( SpecVerbs ), "JSON schema mapVerbBacking keys must equal the spec list." );
		} );

		// 5. No op without an implementation: every verb has a backing kernel
		// type that resolves in this build. (Navmesh / cubemap are honest
		// named-capability-error paths, not fake success — see SEAM.md; their
		// backing entries are the types that define that contract.)
		Check( "no op without an implementation: every verb has a resolvable backing type", () =>
		{
			var backing = ApiCatalog.MapVerbBacking();
			foreach ( var verb in SpecVerbs )
			{
				Require( backing.TryGetValue( verb, out var typeName ), $"No backing type registered for {verb}." );
				Require( ApiCatalog.BackingTypeExists( typeName ),
					$"Backing type '{typeName}' for {verb} does not resolve in this build — phantom op." );
			}
		} );

		// 6. Backing table covers every verb and no extra.
		Check( "backing table covers every verb and no extra", () =>
		{
			var keys = ApiCatalog.MapVerbBacking().Keys;
			Require( new HashSet<string>( keys, StringComparer.Ordinal ).SetEquals( SpecVerbs ),
				$"Backing table keys differ from the verb set. Keys: {string.Join( ", ", keys )}. Verbs: {string.Join( ", ", SpecVerbs )}." );
		} );

		// 7. map_command rejects unknown verbs with the allowed list.
		Check( "map_command rejects unknown verbs with the allowed list", () =>
		{
			var tools = NewTools( "reject" );
			bool threw = false;
			try
			{
				tools.ExecuteAsync( "map_command", new JsonObject { ["script"] = "frobnicate x=1" }, default ).GetAwaiter().GetResult();
			}
			catch ( ArgumentException ex )
			{
				threw = true;
				Require( ex.Message.Contains( "not a map_command verb", StringComparison.Ordinal ), $"Rejection must name the tool: {ex.Message}" );
				Require( ex.Message.Contains( "map_brush_create", StringComparison.Ordinal ), $"Rejection must list allowed verbs: {ex.Message}" );
			}
			Require( threw, "map_command must reject an unknown verb." );
		} );

		// 8. map_command accepts every registered verb at the gate: no verb
		// may be refused as "not a map_command verb". Past the gate the call
		// may succeed (verbs landed) or fail with the BuildScript named
		// error (verbs not landed yet) — both are accepted here; execution
		// is worker 64's acceptance, not this check's.
		Check( "map_command accepts every registered verb at the gate", () =>
		{
			var tools = NewTools( "gate" );
			foreach ( var verb in SpecVerbs )
			{
				try
				{
					tools.ExecuteAsync( "map_command", new JsonObject { ["script"] = verb }, default ).GetAwaiter().GetResult();
				}
				catch ( ArgumentException ex ) when ( ex.Message.Contains( "not a map_command verb", StringComparison.Ordinal ) )
				{
					throw new Exception( $"Registered verb {verb} was refused at the map_command gate: {ex.Message}" );
				}
				catch ( InvalidOperationException )
				{
					// Named BuildScript/verb error past the gate: accepted.
				}
			}
		} );

		// 9. Docs gate covers map verbs: a doc naming them all reports no
		// missing map verb; a doc missing one names it.
		Check( "docs gate covers map verbs", () =>
		{
			string full = string.Join( "\n", SpecVerbs.Select( v => "- `" + v + "`" ) );
			var missingFull = ApiCatalog.VerifyDocsInSync( full );
			Require( !missingFull.Any( m => m.StartsWith( "map verb ", StringComparison.Ordinal ) ),
				$"Doc naming all map verbs must report no missing map verb, got: {string.Join( "; ", missingFull )}." );
			string missingOne = string.Join( "\n", SpecVerbs.Where( v => v != "map_validate" ).Select( v => "- `" + v + "`" ) );
			var missingOneResult = ApiCatalog.VerifyDocsInSync( missingOne );
			Require( missingOneResult.Contains( "map verb map_validate" ),
				$"Doc missing map_validate must report it, got: {string.Join( "; ", missingOneResult )}." );
		} );
	}
}
