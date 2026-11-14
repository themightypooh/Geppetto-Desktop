// McpModelingToolsTests.cs — MCP modeling tools (task mcp-tools-modeling-operations).
//
// Exercises the tools layer the MCP transport calls, without the transport:
// ListTools is the definitions verbatim plus run_script; CallAsync routes
// every mapped tool through the existing AiSuiteTools execution path; the
// run_script tool runs BuildScript lines and rejects an unknown verb with
// the BuildScript error; export capability is honest per format (a real
// file for a supported format, an honest refusal and no file for the rest).
//
// The sweep runs against a box model that the fixture check also writes to
// disk as fixture.effigy and reloads with StudioDocument — there is no
// small checked-in .effigy in tests/ (the repo-root .effigy files are stray
// work artifacts, not fixtures). render is excluded from the sweep (needs
// working EGL); the five research tools are exercised only through the
// Research-disabled refusal (they need network + Research). A tool that
// throws anything other than ArgumentException/InvalidOperationException in
// the sweep is a defect in that tool, not in this mapping — the sweep names
// the tool. NOT registered in TestRegistry.cs — the integrator adds the line.
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

internal static class McpModelingToolsTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "mcp-tools", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		JsonElement Json( string text )
		{
			using var doc = JsonDocument.Parse( text );
			return doc.RootElement.Clone();
		}
		McpModelingTools NewMcp( string dir ) => new( new AiSuiteTools( Path.Combine( settings.WorkDirectory, dir ) ) );
		McpModelingTools.McpToolCallResult Call( McpModelingTools mcp, string name, JsonNode? args )
			=> mcp.CallAsync( name, args, default ).GetAwaiter().GetResult();
		void ThrowsArg( Action body, string mustContain )
		{
			try { body(); }
			catch ( ArgumentException e )
			{
				Require( e.Message.Contains( mustContain, StringComparison.Ordinal ), $"ArgumentException '{e.Message}' does not mention '{mustContain}'" );
				return;
			}
			throw new Exception( $"Expected ArgumentException mentioning '{mustContain}'" );
		}

		Check( "tools list is the definitions verbatim plus run_script", () =>
		{
			var mcp = NewMcp( "mcp-tools-list" );
			var list = mcp.ListTools();
			var defs = AiSuiteTools.Definitions;
			Require( list.Count == defs.Count + 1, $"expected {defs.Count + 1} tools, got {list.Count}" );
			for ( int i = 0; i < defs.Count; i++ )
			{
				var def = defs[i]!.AsObject();
				var tool = list[i]!.AsObject();
				Require( tool["name"]?.GetValue<string>() == def["name"]?.GetValue<string>(), $"tool {i} name differs" );
				Require( tool["description"]?.GetValue<string>() == def["description"]?.GetValue<string>(), $"tool {i} description differs" );
				Require( tool["inputSchema"]?.ToJsonString() == def["parameters"]?.ToJsonString(), $"tool {i} inputSchema is not the definition's parameters verbatim" );
			}
			Require( list[list.Count - 1]!["name"]?.GetValue<string>() == "run_script", "run_script is not last" );
		} );

		Check( "run_script definition takes one required string script", () =>
		{
			var def = McpModelingTools.RunScriptDefinition();
			Require( def["name"]?.GetValue<string>() == "run_script", "run_script name" );
			var schema = def["inputSchema"]!.AsObject();
			Require( schema["type"]?.GetValue<string>() == "object", "run_script schema is an object" );
			var required = schema["required"]!.AsArray();
			Require( required.Count == 1 && required[0]?.GetValue<string>() == "script", "run_script requires exactly script" );
			Require( schema["properties"]!["script"]!["type"]?.GetValue<string>() == "string", "script is a string" );
		} );

		Check( "script catalog is the ApiCatalog schema", () =>
		{
			var root = Json( ApiCatalog.GenerateJsonSchema() );
			Require( root.TryGetProperty( "features", out _ ), "catalog has features" );
			Require( root.TryGetProperty( "verbs", out _ ), "catalog has verbs" );
			Require( root.TryGetProperty( "rigVerbs", out _ ), "catalog has rigVerbs" );
			var verbs = root.GetProperty( "verbs" ).EnumerateArray().Select( e => e.GetString() ).ToHashSet();
			Require( verbs.Contains( "add" ) && verbs.Contains( "describe" ) && verbs.Contains( "export" ), "catalog verbs cover the run_script verbs" );
			var features = root.GetProperty( "features" ).EnumerateArray().Select( e => e.GetString() ).ToHashSet();
			Require( features.Contains( "Primitive" ), "catalog features include Primitive" );
		} );

		Check( "catalog and inspect round-trip over the MCP call path", () =>
		{
			var mcp = NewMcp( "mcp-tools-catalog-inspect" );
			var catalog = Call( mcp, "catalog", new JsonObject() );
			Require( !catalog.IsError, "catalog failed: " + catalog.Text );
			var types = Json( catalog.Text ).EnumerateArray().Select( e => e.GetString() ).ToHashSet();
			Require( types.Contains( "Primitive" ), "catalog does not list Primitive" );
			var inspect = Call( mcp, "inspect", new JsonObject() );
			Require( !inspect.IsError, "inspect failed: " + inspect.Text );
			Require( inspect.Text.Length > 0, "inspect returned empty text" );
		} );

		Check( "add, edit and remove a feature over the MCP call path", () =>
		{
			var mcp = NewMcp( "mcp-tools-add-edit-remove" );
			var added = Call( mcp, "add_feature", JsonNode.Parse( "{\"type\":\"Primitive\",\"fields\":{\"SizeX\":12,\"SizeY\":8,\"SizeZ\":4}}" )! );
			Require( !added.IsError, "add_feature failed: " + added.Text );
			Require( mcp.Tools.Studio.Features.Count == 1, "add_feature left no feature" );
			Require( mcp.Tools.Studio.Bodies.Count == 1, "add_feature left no body" );
			var id = mcp.Tools.Studio.Features[0].Id;
			Require( added.Text.Contains( id, StringComparison.Ordinal ), "add_feature return does not carry the stable ID" );
			var edited = Call( mcp, "edit_feature", new JsonObject { ["id"] = id, ["fields"] = new JsonObject { ["SizeX"] = 6 } } );
			Require( !edited.IsError, "edit_feature failed: " + edited.Text );
			Require( ( (PrimitiveFeature)mcp.Tools.Studio.Features[0] ).SizeX.Value == 6f, "edit_feature did not change SizeX to 6" );
			var removed = Call( mcp, "remove_feature", new JsonObject { ["id"] = id } );
			Require( !removed.IsError, "remove_feature failed: " + removed.Text );
			Require( mcp.Tools.Studio.Features.Count == 0, "remove_feature left the feature" );
			Require( mcp.Tools.Studio.Bodies.Count == 0, "remove_feature left the body" );
		} );

		Check( "fixture effigy round-trips through StudioDocument", () =>
		{
			var mcp = NewMcp( "mcp-tools-fixture" );
			var added = Call( mcp, "add_feature", JsonNode.Parse( "{\"type\":\"Primitive\",\"fields\":{\"SizeX\":12,\"SizeY\":8,\"SizeZ\":4}}" )! );
			Require( !added.IsError, "add_feature failed: " + added.Text );
			var path = Path.Combine( mcp.Tools.Root, "fixture.effigy" );
			File.WriteAllText( path, StudioDocument.Write( mcp.Tools.Studio ) );
			Require( new FileInfo( path ).Length > 0, "fixture.effigy is empty" );
			var loaded = StudioDocument.Read( File.ReadAllText( path ) );
			// StudioDocument.Read restores the tree, not the bodies: the studio comes
			// back NOT rebuilt by design (see Read's doc comment), and the codebase's
			// own round-trip tests rebuild before asserting bodies (SheetMetalTests,
			// AssemblyPartTests). Rebuild here, then the body the feature builds must
			// be back.
			loaded.Rebuild();
			Require( loaded.Features.Count == 1, "reloaded fixture lost the feature" );
			Require( loaded.Bodies.Count == 1, "reloaded fixture lost the body" );
		} );

		Check( "every mapped builder tool is reachable (transcript sweep)", () =>
		{
			var mcp = NewMcp( "mcp-tools-sweep" );
			var added = Call( mcp, "add_feature", JsonNode.Parse( "{\"type\":\"Primitive\",\"fields\":{\"SizeX\":12,\"SizeY\":8,\"SizeZ\":4}}" )! );
			Require( !added.IsError, "add_feature failed: " + added.Text );
			int swept = 0;
			foreach ( var node in AiSuiteTools.Definitions )
			{
				var name = node!["name"]!.GetValue<string>();
				if ( name == "render" || AiSuiteTools.IsResearchTool( name ) ) continue;
				swept++;
				try
				{
					var res = Call( mcp, name, new JsonObject() );
					Require( res.Text.Length > 0, name + " returned empty text" );
				}
				catch ( ArgumentException ) { /* missing required arg: the honest invalid-params refusal */ }
				catch ( InvalidOperationException ) { /* honest state refusal from the tool itself */ }
			}
			Require( swept == AiSuiteTools.Definitions.Count - 6, $"swept {swept} tools, expected {AiSuiteTools.Definitions.Count - 6}" );
		} );

		Check( "research tools refuse honestly without Research", () =>
		{
			var mcp = NewMcp( "mcp-tools-research" );
			var res = Call( mcp, "search", JsonNode.Parse( "{\"query\":\"chair\"}" )! );
			Require( res.IsError, "search without Research should fail" );
			Require( res.Text.Contains( "Research is disabled", StringComparison.Ordinal ), "search refusal does not say Research is disabled: " + res.Text );
		} );

		Check( "unknown tool and missing args are invalid params", () =>
		{
			var mcp = NewMcp( "mcp-tools-invalid-params" );
			ThrowsArg( () => Call( mcp, "no_such_tool", new JsonObject() ), "Unknown suite tool" );
			ThrowsArg( () => Call( mcp, "add_feature", new JsonObject() ), "'type'" );
			ThrowsArg( () => Call( mcp, "run_script", new JsonObject() ), "'script'" );
			ThrowsArg( () => Call( mcp, "catalog", JsonNode.Parse( "\"not-an-object\"" )! ), "must be a JSON object" );
		} );

		Check( "run_script runs BuildScript lines and returns the log", () =>
		{
			var mcp = NewMcp( "mcp-tools-run-script" );
			var described = Call( mcp, "run_script", JsonNode.Parse( "{\"script\":\"describe\"}" )! );
			Require( !described.IsError, "describe script failed: " + described.Text );
			Require( described.Text.Length > 0, "describe script returned empty log" );
			var added = Call( mcp, "run_script", JsonNode.Parse( "{\"script\":\"add Primitive shape=Box\"}" )! );
			Require( !added.IsError, "add script failed: " + added.Text );
			Require( added.Text.Contains( "added", StringComparison.OrdinalIgnoreCase ), "add script log does not report the add: " + added.Text );
			Require( mcp.Tools.Studio.Features.Count == 1, "run_script add left no feature" );
			Require( mcp.Tools.Studio.Bodies.Count == 1, "run_script add left no body" );
			var inspect = Call( mcp, "inspect", new JsonObject() );
			Require( !inspect.IsError, "inspect after run_script failed: " + inspect.Text );
		} );

		Check( "run_script rejects an unknown verb with the BuildScript error and rolls back", () =>
		{
			var mcp = NewMcp( "mcp-tools-run-script-reject" );
			var bad = Call( mcp, "run_script", JsonNode.Parse( "{\"script\":\"frobnicate x=1\"}" )! );
			Require( bad.IsError, "unknown verb should fail" );
			Require( bad.Text.Contains( "line 1", StringComparison.Ordinal ), "rejection does not name line 1: " + bad.Text );
			Require( bad.Text.Contains( "frobnicate", StringComparison.Ordinal ), "rejection does not name the verb: " + bad.Text );
			Require( mcp.Tools.Studio.Features.Count == 0, "failed script mutated the studio" );
			var partial = Call( mcp, "run_script", JsonNode.Parse( "{\"script\":\"add Primitive shape=Box\\nfrobnicate x=1\"}" )! );
			Require( partial.IsError, "mid-script unknown verb should fail" );
			Require( partial.Text.Contains( "line 2", StringComparison.Ordinal ), "rejection does not name line 2: " + partial.Text );
			Require( mcp.Tools.Studio.Features.Count == 0, "partial script mutated the studio" );
		} );

		Check( "validate_export is honest per format, real files for supported ones", () =>
		{
			var mcp = NewMcp( "mcp-tools-export" );
			var added = Call( mcp, "add_feature", JsonNode.Parse( "{\"type\":\"Primitive\",\"fields\":{\"SizeX\":12,\"SizeY\":8,\"SizeZ\":4}}" )! );
			Require( !added.IsError, "add_feature failed: " + added.Text );
			foreach ( var format in new[] { "obj", "glb", "effigy" } )
			{
				var res = Call( mcp, "validate_export", JsonNode.Parse( "{\"format\":\"" + format + "\"}" )! );
				Require( !res.IsError, "validate_export " + format + " failed: " + res.Text );
				var report = Json( res.Text );
				Require( report.GetProperty( "canExport" ).GetBoolean(), format + " should be exportable on a box" );
				var file = Path.Combine( mcp.Tools.Root, "export-check." + format );
				Require( File.Exists( file ), format + " produced no file" );
				Require( new FileInfo( file ).Length > 0, format + " file is empty" );
			}
			foreach ( var format in new[] { "fbx", "vmdl", "dmx" } )
			{
				var res = Call( mcp, "validate_export", JsonNode.Parse( "{\"format\":\"" + format + "\"}" )! );
				Require( !res.IsError, "validate_export " + format + " should report, not fail: " + res.Text );
				var report = Json( res.Text );
				Require( !report.GetProperty( "canExport" ).GetBoolean(), format + " claims it can export" );
				Require( report.GetProperty( "reason" ).GetString()!.Contains( "no capability-checked exporter", StringComparison.Ordinal ), format + " refusal does not explain itself" );
				Require( !File.Exists( Path.Combine( mcp.Tools.Root, "export-check." + format ) ), format + " left a file behind" );
			}
			var riganim = Call( mcp, "validate_export", JsonNode.Parse( "{\"format\":\"riganim\"}" )! );
			Require( !riganim.IsError, "validate_export riganim failed: " + riganim.Text );
			var rigReport = Json( riganim.Text );
			Require( rigReport.TryGetProperty( "canExport", out _ ), "riganim report has no canExport" );
			var rigFile = Path.Combine( mcp.Tools.Root, "export-check.riganim" );
			Require( !File.Exists( rigFile ) || new FileInfo( rigFile ).Length > 0, "riganim left an empty fake file" );
		} );

		Check( "run_script with an empty script is an honest error", () =>
		{
			var mcp = NewMcp( "mcp-tools-run-script-empty" );
			var res = Call( mcp, "run_script", JsonNode.Parse( "{\"script\":\"  \"}" )! );
			Require( res.IsError, "empty script should fail" );
			Require( res.Text.Contains( "needs a script", StringComparison.Ordinal ), "empty script error does not explain itself: " + res.Text );
		} );
	}
}
