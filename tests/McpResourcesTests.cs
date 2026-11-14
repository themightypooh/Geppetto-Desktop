using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

internal static class McpResourcesTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "mcp-resources", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		JsonElement Json( string text )
		{
			using var doc = JsonDocument.Parse( text );
			return doc.RootElement.Clone();
		}

		PartStudio MakeStudio( string name = "box" )
		{
			var studio = new PartStudio();
			var prim = new PrimitiveFeature();
			prim.Name = name;
			studio.Add( prim );
			studio.Rebuild();
			return studio;
		}

		DocumentSession MakeSession( PartStudio studio, string path = "doc.effigy" )
			=> DocumentSession.CreateEffigy( path, studio );

		Check( "list-resources returns all eight geppetto URIs", () =>
		{
			var list = McpResources.ListResources();
			Require( list.Count == 8, $"expected 8 resources, got {list.Count}" );
			Require( list[0].Uri == "geppetto://document", $"first resource is {list[0].Uri}" );
			Require( list[1].Uri == "geppetto://document/mesh", $"second resource is {list[1].Uri}" );
			Require( list[2].Uri == "geppetto://tests/latest", $"third resource is {list[2].Uri}" );
			Require( list[3].Uri == "geppetto://catalog", $"fourth resource is {list[3].Uri}" );
			Require( list.All( r => r.MimeType == "application/json" ), "all resources are JSON" );
		} );

		Check( "catalog is the API catalog JSON", () =>
		{
			var content = McpResources.ReadResource( "geppetto://catalog" );
			Require( content.MimeType == "application/json", "catalog mime type" );
			var root = Json( content.Text );
			Require( root.TryGetProperty( "features", out _ ), "catalog has features" );
			Require( root.TryGetProperty( "verbs", out _ ), "catalog has verbs" );
			Require( root.TryGetProperty( "rigVerbs", out _ ), "catalog has rigVerbs" );
			Require( root.GetProperty( "verbs" ).GetArrayLength() > 0, "catalog verbs are non-empty" );
		} );

		Check( "document with no open document reports open false", () =>
		{
			var content = McpResources.ReadResource( "geppetto://document" );
			var root = Json( content.Text );
			Require( root.GetProperty( "open" ).GetBoolean() == false, "open is false with no source" );
			Require( root.TryGetProperty( "note", out _ ), "no-document note explains why" );
		} );

		Check( "document reports the open session", () =>
		{
			var studio = MakeStudio();
			var session = MakeSession( studio );
			var source = new McpDocumentSource { CurrentDocument = () => session };
			var root = Json( McpResources.ReadResource( "geppetto://document", source ).Text );
			Require( root.GetProperty( "open" ).GetBoolean(), "open is true" );
			Require( root.GetProperty( "documentId" ).GetString() == session.DocumentId, "document id matches the session" );
			Require( root.GetProperty( "sourceKind" ).GetString() == "Effigy", "source kind is Effigy" );
			Require( root.GetProperty( "isDirty" ).GetBoolean() == false, "fresh session is not dirty" );
			var features = root.GetProperty( "features" );
			Require( features.GetArrayLength() == 1, $"expected 1 feature, got {features.GetArrayLength()}" );
			Require( features[0].GetProperty( "name" ).GetString() == "box", "feature name is box" );
			Require( features[0].GetProperty( "suppressed" ).GetBoolean() == false, "feature is not suppressed" );
			Require( root.GetProperty( "bodies" ).GetArrayLength() == studio.Bodies.Count, "body list matches the studio" );
		} );

		Check( "mesh totals match the studio", () =>
		{
			var studio = MakeStudio();
			var session = MakeSession( studio );
			var source = new McpDocumentSource { CurrentDocument = () => session };
			var root = Json( McpResources.ReadResource( "geppetto://document/mesh", source ).Text );
			Require( root.GetProperty( "open" ).GetBoolean(), "open is true" );
			var totals = root.GetProperty( "totals" );
			Require( totals.GetProperty( "vertices" ).GetInt64() == studio.TotalVertexCount, "vertex total matches the studio" );
			Require( totals.GetProperty( "faces" ).GetInt64() == studio.TotalFaceCount, "face total matches the studio" );
			Require( totals.GetProperty( "vertices" ).GetInt64() > 0, "mesh has vertices" );
			Require( totals.GetProperty( "faces" ).GetInt64() > 0, "mesh has faces" );
			var bodies = root.GetProperty( "bodies" );
			Require( bodies.GetArrayLength() == studio.Bodies.Count, "per-body list matches the studio" );
			Require( bodies[0].GetProperty( "boundsDiagonal" ).GetSingle() > 0, "bounds diagonal is positive" );
		} );

		Check( "mesh with no open document reports open false", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://document/mesh" ).Text );
			Require( root.GetProperty( "open" ).GetBoolean() == false, "open is false with no source" );
		} );

		Check( "unknown resource throws ArgumentException", () =>
		{
			try
			{
				McpResources.ReadResource( "geppetto://nope" );
				throw new Exception( "expected ArgumentException for an unknown resource" );
			}
			catch ( ArgumentException ex )
			{
				Require( ex.Message.Contains( "geppetto://nope" ), "error names the unknown URI" );
			}
		} );

		Check( "tests-latest with no run reports recorded false", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://tests/latest" ).Text );
			Require( root.GetProperty( "recorded" ).GetBoolean() == false, "recorded is false with no run" );
			Require( root.TryGetProperty( "howToRun", out var how ) && how.GetString()!.Contains( "ops test" ), "howToRun names the ops test command" );
		} );

		Check( "tests-latest reports a recorded run", () =>
		{
			var run = new McpTestRun( new[]
			{
				new McpTestOutcome( "g", "a", "PASS", null, 1.0 ),
				new McpTestOutcome( "g", "b", "PASS", null, 2.0 ),
				new McpTestOutcome( "g", "c", "FAIL", "boom", 3.0 ),
			}, DateTimeOffset.UtcNow );
			var source = new McpDocumentSource { LatestTestRun = () => run };
			var root = Json( McpResources.ReadResource( "geppetto://tests/latest", source ).Text );
			Require( root.GetProperty( "recorded" ).GetBoolean(), "recorded is true" );
			var counts = root.GetProperty( "counts" );
			Require( counts.GetProperty( "total" ).GetInt64() == 3, "total is 3" );
			Require( counts.GetProperty( "passed" ).GetInt64() == 2, "passed is 2" );
			Require( counts.GetProperty( "failed" ).GetInt64() == 1, "failed is 1" );
			var failures = root.GetProperty( "failures" );
			Require( failures.GetArrayLength() == 1 && failures[0].GetProperty( "name" ).GetString() == "c", "failure list names c" );
			Require( failures[0].GetProperty( "reason" ).GetString() == "boom", "failure reason is kept" );
			Require( root.GetProperty( "outcomes" ).GetArrayLength() == 3, "all outcomes are listed" );
		} );

		Check( "list-prompts returns all seven prompts", () =>
		{
			var list = McpResources.ListPrompts();
			Require( list.Count == 7, $"expected 7 prompts, got {list.Count}" );
			var names = list.Select( p => p.Name ).ToArray();
			Require( names.Contains( "sculpt-face-from-reference" ), "sculpt prompt listed" );
			Require( names.Contains( "fix-topology-report" ), "fix prompt listed" );
			Require( names.Contains( "export-for-godot" ), "export prompt listed" );
			Require( names.Contains( "review-model-sheet" ), "review prompt listed" );
			Require( list.First( p => p.Name == "sculpt-face-from-reference" ).Arguments.Any( a => a.Name == "reference" && a.Required ), "reference is a required sculpt argument" );
		} );

		Check( "each prompt renders grounded instructions", () =>
		{
			var sculpt = McpResources.GetPrompt( "sculpt-face-from-reference", new Dictionary<string, string> { ["reference"] = "refs/face-front.png", ["view"] = "front" } );
			Require( sculpt.Messages.Count == 1 && sculpt.Messages[0].Role == "user", "sculpt renders one user message" );
			Require( sculpt.Messages[0].Text.Contains( "save_image" ), "sculpt leans on save_image" );
			Require( sculpt.Messages[0].Text.Contains( "refs/face-front.png" ), "sculpt echoes the reference" );

			var fix = McpResources.GetPrompt( "fix-topology-report", new Dictionary<string, string>() );
			Require( fix.Messages[0].Text.Contains( "validate_topology" ), "fix leans on validate_topology" );
			Require( fix.Messages[0].Text.Contains( "heal_mesh" ), "fix leans on heal_mesh" );

			var export = McpResources.GetPrompt( "export-for-godot", new Dictionary<string, string> { ["format"] = "glb" } );
			Require( export.Messages[0].Text.Contains( "glb" ), "export echoes the format" );
			Require( export.Messages[0].Text.Contains( "validate_export" ), "export leans on validate_export" );

			var review = McpResources.GetPrompt( "review-model-sheet", new Dictionary<string, string>() );
			Require( review.Messages[0].Text.Contains( "write_sheet" ), "review leans on write_sheet" );
			Require( review.Messages[0].Text.Contains( "sample_colors" ), "review leans on sample_colors" );
		} );

		Check( "unknown prompt and missing required argument throw", () =>
		{
			try
			{
				McpResources.GetPrompt( "nope" );
				throw new Exception( "expected ArgumentException for an unknown prompt" );
			}
			catch ( ArgumentException ex )
			{
				Require( ex.Message.Contains( "nope" ), "error names the unknown prompt" );
			}
			try
			{
				McpResources.GetPrompt( "sculpt-face-from-reference", new Dictionary<string, string>() );
				throw new Exception( "expected ArgumentException for a missing reference" );
			}
			catch ( ArgumentException ex )
			{
				Require( ex.Message.Contains( "reference" ), "error names the missing argument" );
			}
		} );
	}
}
