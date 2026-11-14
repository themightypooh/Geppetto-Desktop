// HammerAiPass2ToolVerbsTests.cs — AI exposure for every pass-2 map tool (task hammer-ai-pass2-tool-verbs).
//
// Every pass-2 operation is callable through the existing AI surfaces:
// BuildScript verbs (kernel), McpMapPass2Tools (MCP), OpsCliMapPass2 (CLI),
// ApiCatalogMapPass2 (catalogue). Each verb runs its real operation on a
// fixture map (a sealed room built through the pass-1 tools); a disallowed
// operation is refused by the safety gates (missing confirm, shell arg).
// No second MCP/CLI surface is created. Registered in TestRegistry.cs
// (group hammerpass2ai) by the integrator — one line, see SEAM.md.
//
// Fixtures live in tests/fixtures/hammer/hammer-ai-pass2-tool-verbs/:
// entity-registry.json (the 10 classes the tools need; same set as the
// pass-1 fixture). The sealed room is built by the tests, not checked in
// as a .vmf.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

internal static class HammerAiPass2ToolVerbsTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "hammerpass2ai", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		JsonElement Json( string text )
		{
			using var doc = JsonDocument.Parse( text );
			return doc.RootElement.Clone();
		}
		static string? FindFixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				for ( ; dir is not null; dir = dir.Parent )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-ai-pass2-tool-verbs" );
					if ( Directory.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}
		McpMapTools NewBase( string dir )
		{
			var fixtureDir = FindFixtureDir();
			var registry = fixtureDir is null
				? null
				: EntityRegistry.Load( Path.Combine( fixtureDir, "entity-registry.json" ) );
			return new McpMapTools( registry, Path.Combine( settings.WorkDirectory, dir ) );
		}
		McpMapPass2Tools NewPass2( McpMapTools mcp ) => new( mcp );
		McpModelingTools.McpToolCallResult Call( McpMapTools mcp, string name, JsonNode? args )
			=> mcp.CallAsync( name, args, default ).GetAwaiter().GetResult();
		McpModelingTools.McpToolCallResult Call2( McpMapPass2Tools mcp, string name, JsonNode? args )
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
		string AddBox( McpMapTools mcp, float x0, float y0, float z0, float x1, float y1, float z1, string material = "dev/dev_measuregeneric01" )
		{
			var res = Call( mcp, "map_brush_create", JsonNode.Parse(
				$"{{\"shape\":\"box\",\"min\":[{x0},{y0},{z0}],\"max\":[{x1},{y1},{z1}],\"material\":\"{material}\"}}" )! );
			Require( !res.IsError, "box create failed: " + res.Text );
			return Json( res.Text ).GetProperty( "brushId" ).GetString()!;
		}
		void BuildSealedRoom( McpMapTools mcp )
		{
			AddBox( mcp, 0, 0, 0, 512, 512, 16 );
			AddBox( mcp, 0, 0, 240, 512, 512, 256 );
			AddBox( mcp, 0, 0, 16, 16, 512, 240 );
			AddBox( mcp, 496, 0, 16, 512, 512, 240 );
			AddBox( mcp, 16, 0, 16, 496, 16, 240 );
			AddBox( mcp, 16, 496, 16, 496, 512, 240 );
			var start = Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"info_player_start\",\"position\":[256,256,64]}" )! );
			Require( !start.IsError, "player start failed: " + start.Text );
			var light = Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"light\",\"position\":[256,256,200]}" )! );
			Require( !light.IsError, "light failed: " + light.Text );
		}
		string ExportVmf( McpMapTools mcp )
		{
			var res = Call( mcp, "map_import_export", JsonNode.Parse( "{\"op\":\"export\",\"format\":\"vmf\",\"fileName\":\"pass2-room.vmf\"}" )! );
			Require( !res.IsError, "export failed: " + res.Text );
			var path = Json( res.Text ).GetProperty( "path" ).GetString()!;
			Require( File.Exists( path ), "exported VMF missing: " + path );
			return path;
		}
		// First world brush id of the exported room VMF (kernel verbs take VMF ids).
		int FirstBrushId( string vmfPath )
		{
			var doc = VmfIo.ParseFile( vmfPath );
			Require( doc.WorldBrushes.Count > 0, "exported VMF has no world brushes" );
			return doc.WorldBrushes[0].Id;
		}
		Dictionary<string, string> Dict( params (string Key, string Value)[] pairs )
		{
			var d = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var (key, value) in pairs ) d[key] = value;
			return d;
		}

		Check( "catalogue: 21 verbs, every backing type exists", () =>
		{
			var names = BuildScript.MapPass2VerbNames;
			Require( names.Length == 21, $"expected 21 pass-2 verbs, got {names.Length}" );
			var backing = ApiCatalogMapPass2.MapPass2VerbBacking();
			Require( backing.Count == 21, $"backing table has {backing.Count} rows, expected 21" );
			foreach ( var verb in names )
			{
				Require( backing.ContainsKey( verb ), $"verb {verb} has no backing row" );
				Require( ApiCatalog.BackingTypeExists( backing[verb] ), $"backing type {backing[verb]} for {verb} does not exist" );
				Require( ApiCatalogMapPass2.MapPass2VerbDescriptions().ContainsKey( verb ), $"verb {verb} has no catalogue description" );
			}
			var failures = ApiCatalogMapPass2.VerifyBacking();
			Require( failures.Count == 0, "catalogue check 5 failures: " + string.Join( "; ", failures ) );
			var md = ApiCatalogMapPass2.GenerateMarkdownSection();
			foreach ( var verb in names ) Require( md.Contains( verb, StringComparison.Ordinal ), $"markdown section lacks {verb}" );
		} );

		Check( "mcp tools list is the 21 pass-2 tools with schemas", () =>
		{
			var mcp = new McpMapPass2Tools();
			var list = mcp.ListTools();
			var expected = BuildScript.MapPass2VerbNames;
			Require( list.Count == expected.Length, $"expected {expected.Length} pass-2 tools, got {list.Count}" );
			for ( var i = 0; i < expected.Length; i++ )
			{
				var tool = list[i]!.AsObject();
				Require( tool["name"]?.GetValue<string>() == expected[i], $"tool {i} is not {expected[i]}" );
				var schema = tool["inputSchema"]!.AsObject();
				Require( schema["type"]?.GetValue<string>() == "object", expected[i] + " schema is not an object" );
				Require( schema["properties"] is JsonObject, expected[i] + " schema has no properties" );
				Require( schema["required"] is JsonArray, expected[i] + " schema has no required array" );
				Require( McpMapPass2Tools.BuildScriptVerbFor( expected[i] ) == expected[i], expected[i] + " has no BuildScript verb seam" );
			}
		} );

		Check( "cli has a subcommand for every verb", () =>
		{
			Require( OpsCliMapPass2.CliCommands.Count == 21, $"expected 21 CLI subcommands, got {OpsCliMapPass2.CliCommands.Count}" );
			foreach ( var verb in BuildScript.MapPass2VerbNames )
			{
				var cmd = OpsCliMapPass2.CliCommandFor( verb );
				Require( cmd is not null, $"verb {verb} has no CLI subcommand" );
				Require( OpsCliMapPass2.HasCommand( cmd! ), $"CLI subcommand {cmd} is not registered" );
			}
			Require( OpsCliMapPass2.MapPass2( Array.Empty<string>() ) == 1, "empty CLI args must exit 1" );
			Require( OpsCliMapPass2.MapPass2( new[] { "nope" } ) == 1, "unknown CLI subcommand must exit 1" );
		} );

		Check( "no second mcp or cli surface", () =>
		{
			Require( typeof( McpMapPass2Tools ).Assembly == typeof( McpMapTools ).Assembly,
				"pass-2 MCP tools live in a different assembly than the pass-1 tools" );
			Require( typeof( OpsCliMapPass2 ).Assembly == typeof( OpsCli ).Assembly,
				"pass-2 CLI lives in a different assembly than OpsCli" );
			foreach ( var bad in new[] { "Main", "Listen", "StartServer", "RunServer" } )
				Require( typeof( OpsCliMapPass2 ).GetMethod( bad ) is null, $"OpsCliMapPass2 must not define {bad}" );
			Require( typeof( McpMapPass2Tools ).GetNestedTypes().All( t => !t.Name.Contains( "Server", StringComparison.Ordinal ) ),
				"McpMapPass2Tools must not define a nested server type" );
			Require( McpMapPass2Tools.SafetyGateTools.Count == 21, "safety gate tool list must cover all 21 pass-2 tools" );
		} );

		Check( "fixture registry loads and matches the embedded default", () =>
		{
			var fixtureDir = FindFixtureDir();
			Require( fixtureDir is not null, "fixture dir tests/fixtures/hammer/hammer-ai-pass2-tool-verbs not found" );
			var fromFile = EntityRegistry.Load( Path.Combine( fixtureDir!, "entity-registry.json" ) );
			Require( fromFile.Count == 10, $"fixture registry has {fromFile.Count} classes, expected 10" );
			Require( fromFile.Find( "info_player_start" ) is not null, "fixture registry lacks info_player_start" );
			Require( fromFile.Find( "func_door" ) is not null, "fixture registry lacks func_door" );
		} );

		Check( "live-lighting evaluates the sealed room", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-live-lighting" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var res = Call2( mcp, "map_live_lighting", JsonNode.Parse( "{\"quality\":\"draft\"}" )! );
			Require( !res.IsError, "live lighting failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "status" ).GetString() == "ok", "live lighting status is not ok: " + res.Text );
			Require( json.GetProperty( "faceCount" ).GetInt32() == 36, "sealed room has 6 boxes x 6 faces = 36 faces, got " + json.GetProperty( "faceCount" ).GetInt32() );
			Require( json.GetProperty( "lightCount" ).GetInt32() == 1, "sealed room has 1 light" );
			Require( json.GetProperty( "faces" ).GetArrayLength() == 36, "live lighting returned the wrong face count" );
		} );

		Check( "skybox preview runs on the sealed room", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-skybox" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var res = Call2( mcp, "map_skybox_preview", JsonNode.Parse( "{}" )! );
			Require( !res.IsError, "skybox preview failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "status" ).GetString() == "ok", "skybox status is not ok: " + res.Text );
			Require( json.GetProperty( "skyName" ).GetString() == "", "sealed room has no skyname, got " + json.GetProperty( "skyName" ).GetString() );
			Require( json.GetProperty( "hasSkyCamera" ).GetBoolean() == false, "sealed room has no sky_camera" );
			Require( json.GetProperty( "has3DSection" ).GetBoolean() == false, "sealed room has no 3D skybox section" );
			Require( json.GetProperty( "faceCount" ).GetInt32() == 0, "sealed room has no skybox faces" );
		} );

		Check( "effects fog evaluates from the map", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-effects" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var res = Call2( mcp, "map_effects_preview", JsonNode.Parse( "{\"op\":\"fog\"}" )! );
			Require( !res.IsError, "effects fog failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "status" ).GetString() == "ok", "effects status is not ok: " + res.Text );
			Require( json.GetProperty( "op" ).GetString() == "fog", "effects op is not fog" );
			Require( json.TryGetProperty( "density", out _ ), "effects fog has no density" );
			Require( json.TryGetProperty( "factor", out _ ), "effects fog has no factor" );
			Require( json.TryGetProperty( "enabled", out _ ), "effects fog has no enabled flag" );
			var list = Call2( mcp, "map_effects_preview", JsonNode.Parse( "{\"op\":\"list\"}" )! );
			Require( !list.IsError, "effects list failed: " + list.Text );
			Require( Json( list.Text ).GetProperty( "hasFogController" ).GetBoolean() == false, "sealed room has no env_fog_controller" );
		} );

		Check( "compile preset quickplay succeeds on the sealed room", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-compile" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var res = Call2( mcp, "map_compile_preset", JsonNode.Parse( "{\"preset\":\"quickplay\"}" )! );
			Require( !res.IsError, "compile preset failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "succeeded" ).GetBoolean(), "quickplay compile did not succeed: " + res.Text );
			Require( json.GetProperty( "preset" ).GetString() == "QuickPlay", "compile preset is not QuickPlay" );
		} );

		Check( "compile only-entities reuses the base artefacts", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-compile-oe" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var res = Call2( mcp, "map_compile_only_entities", JsonNode.Parse( "{\"confirm\":true}" )! );
			Require( !res.IsError, "only-entities failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "onlyEntitiesRan" ).GetBoolean(), "only-entities did not run: " + res.Text );
			Require( json.GetProperty( "onlyEntities" ).GetProperty( "onlyEntities" ).GetBoolean(), "only-entities result is not flagged onlyEntities" );
			Require( json.GetProperty( "onlyEntities" ).GetProperty( "reusedPriorArtefacts" ).GetBoolean(), "only-entities did not reuse the base artefacts" );
		} );

		Check( "check problems finds no issues in the sealed room", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-check" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var res = Call2( mcp, "map_check_problems", JsonNode.Parse( "{}" )! );
			Require( !res.IsError, "check failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "issueCount" ).GetInt32() == 0, "sealed room has issues: " + res.Text );
			Require( json.GetProperty( "hasErrors" ).GetBoolean() == false, "sealed room check reports errors" );
		} );

		Check( "autofix list reports engine fixability, fix needs confirm", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-autofix" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var list = Call2( mcp, "map_issue_autofix", JsonNode.Parse( "{\"op\":\"list\"}" )! );
			Require( !list.IsError, "autofix list failed: " + list.Text );
			Require( Json( list.Text ).GetProperty( "engineFixability" ).ValueKind == JsonValueKind.Array, "autofix list has no engineFixability array" );
			ThrowsArg( () => Call2( mcp, "map_issue_autofix", JsonNode.Parse( "{\"op\":\"fix\",\"issue\":\"x\"}" )! ), "confirm" );
			ThrowsArg( () => Call2( mcp, "map_issue_autofix", JsonNode.Parse( "{\"op\":\"fix\",\"issue\":\"no-such-issue\",\"confirm\":true}" )! ), "no-such-issue" );
		} );

		Check( "layer define list rename remove", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-layer" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var defined = Call2( mcp, "map_layer", JsonNode.Parse( "{\"op\":\"define\",\"name\":\"test-layer\",\"colour\":\"#ff0000\"}" )! );
			Require( !defined.IsError, "layer define failed: " + defined.Text );
			Require( mcp.LayerCount == 1, "layer store has no layer" );
			var listed = Call2( mcp, "map_layer", JsonNode.Parse( "{\"op\":\"list\"}" )! );
			Require( Json( listed.Text ).GetProperty( "layers" ).EnumerateArray().Any( l => l.GetProperty( "name" ).GetString() == "test-layer" ), "layer list lacks test-layer" );
			var renamed = Call2( mcp, "map_layer", JsonNode.Parse( "{\"op\":\"rename\",\"name\":\"test-layer\",\"newName\":\"renamed-layer\"}" )! );
			Require( !renamed.IsError, "layer rename failed: " + renamed.Text );
			ThrowsArg( () => Call2( mcp, "map_layer", JsonNode.Parse( "{\"op\":\"remove\",\"name\":\"renamed-layer\"}" )! ), "confirm" );
			var removed = Call2( mcp, "map_layer", JsonNode.Parse( "{\"op\":\"remove\",\"name\":\"renamed-layer\",\"confirm\":true}" )! );
			Require( !removed.IsError, "layer remove failed: " + removed.Text );
			Require( mcp.LayerCount == 0, "layer store still has the removed layer" );
		} );

		Check( "visgroup ops define setcolour rename remove", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-visgroup" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var defined = Call2( mcp, "map_visgroup_ops", JsonNode.Parse( "{\"op\":\"define\",\"name\":\"test-vg\",\"color\":\"255 0 0\"}" )! );
			Require( !defined.IsError, "visgroup define failed: " + defined.Text );
			var coloured = Call2( mcp, "map_visgroup_ops", JsonNode.Parse( "{\"op\":\"setcolour\",\"name\":\"test-vg\",\"color\":\"#00ff00\"}" )! );
			Require( !coloured.IsError, "visgroup setcolour failed: " + coloured.Text );
			var renamed = Call2( mcp, "map_visgroup_ops", JsonNode.Parse( "{\"op\":\"rename\",\"name\":\"test-vg\",\"newName\":\"renamed-vg\"}" )! );
			Require( !renamed.IsError, "visgroup rename failed: " + renamed.Text );
			var listed = Call2( mcp, "map_visgroup_ops", JsonNode.Parse( "{\"op\":\"list\"}" )! );
			Require( Json( listed.Text ).GetProperty( "visgroups" ).EnumerateArray().Any( v => v.GetProperty( "name" ).GetString() == "renamed-vg" ), "visgroup list lacks renamed-vg" );
			ThrowsArg( () => Call2( mcp, "map_visgroup_ops", JsonNode.Parse( "{\"op\":\"remove\",\"name\":\"renamed-vg\"}" )! ), "confirm" );
			var removed = Call2( mcp, "map_visgroup_ops", JsonNode.Parse( "{\"op\":\"remove\",\"name\":\"renamed-vg\",\"confirm\":true}" )! );
			Require( !removed.IsError, "visgroup remove failed: " + removed.Text );
		} );

		Check( "lock evaluates texture locks on a face", () =>
		{
			var baseMcp = NewBase( "pass2-lock" );
			BuildSealedRoom( baseMcp );
			var vmf = ExportVmf( baseMcp );
			var brush = FirstBrushId( vmf );
			var text = BuildScript.RunMapPass2Verb( "map_lock", vmf, Dict( ("brush", brush.ToString()), ("face", "0"), ("locks", "both"), ("translate", "8 0 0") ), baseMcp.WorkDirectory );
			var json = Json( text );
			Require( json.GetProperty( "status" ).GetString() == "ok", "lock failed: " + text );
			Require( json.GetProperty( "locked" ).GetProperty( "uv" ).GetProperty( "locks" ).GetString() == "ScaleLock, UvLock", "lock did not apply both locks: " + text );
			Require( json.GetProperty( "sampleCheck" ).GetProperty( "preserved" ).GetBoolean(), "texture lock did not preserve the sampled UV: " + text );
		} );

		Check( "paste special copies a brush", () =>
		{
			var baseMcp = NewBase( "pass2-paste" );
			BuildSealedRoom( baseMcp );
			var vmf = ExportVmf( baseMcp );
			var brush = FirstBrushId( vmf );
			var text = BuildScript.RunMapPass2Verb( "map_paste_special", vmf, Dict( ("brushes", brush.ToString()), ("copies", "2"), ("confirm", "true") ), baseMcp.WorkDirectory );
			var json = Json( text );
			Require( json.GetProperty( "status" ).GetString() == "ok", "paste special failed: " + text );
			Require( json.GetProperty( "ok" ).GetBoolean(), "paste special did not succeed: " + text );
			Require( json.GetProperty( "pastedCount" ).GetInt32() == 2, "paste special pasted the wrong count: " + text );
			Require( json.GetProperty( "pasted" ).GetArrayLength() == 2, "paste special returned the wrong pasted array" );
		} );

		Check( "gizmo health check needs no map", () =>
		{
			var text = BuildScript.RunMapPass2Verb( "map_gizmo", null, Dict( ("mode", "translate"), ("space", "global"), ("snap", "true"), ("grid", "16"), ("point", "20 20 20") ), null );
			var json = Json( text );
			Require( json.GetProperty( "status" ).GetString() == "ok", "gizmo failed: " + text );
			Require( json.GetProperty( "healthPassed" ).GetBoolean(), "gizmo health check did not pass: " + text );
			Require( json.GetProperty( "snappedPoint" ).GetArrayLength() == 3, "gizmo returned no snapped point" );
			Require( Math.Abs( json.GetProperty( "snappedPoint" )[0].GetDouble() - 16.0 ) < 1e-6, "gizmo snap did not snap 20 to 16: " + text );
		} );

		Check( "direct edit extrudes a brush face", () =>
		{
			var baseMcp = NewBase( "pass2-direct-edit" );
			BuildSealedRoom( baseMcp );
			var vmf = ExportVmf( baseMcp );
			var brush = FirstBrushId( vmf );
			var text = BuildScript.RunMapPass2Verb( "map_direct_edit", vmf, Dict( ("brush", brush.ToString()), ("op", "extrude"), ("face", "0"), ("distance", "8") ), baseMcp.WorkDirectory );
			var json = Json( text );
			Require( json.GetProperty( "status" ).GetString() == "ok", "direct edit failed: " + text );
			Require( json.GetProperty( "isValid" ).GetBoolean(), "extruded brush is not valid: " + text );
			Require( json.GetProperty( "volumeDelta" ).GetDouble() > 0, "extrude did not increase the volume: " + text );
			Require( json.GetProperty( "editedBrush" ).GetProperty( "vertexCount" ).GetInt32() > 0, "direct edit returned no edited brush" );
		} );

		Check( "linked group define and list over session brush ids", () =>
		{
			var baseMcp = NewBase( "pass2-linked-group" );
			var floor = AddBox( baseMcp, 0, 0, 0, 64, 64, 16 );
			var wall = AddBox( baseMcp, 64, 0, 0, 80, 64, 64 );
			var mcp = NewPass2( baseMcp );
			var res = Call2( mcp, "map_linked_group", JsonNode.Parse(
				$"{{\"op\":\"define\",\"name\":\"crate-pair\",\"brushIds\":[\"{floor}\",\"{wall}\"]}}" )! );
			Require( !res.IsError, "linked group define failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "ok" ).GetBoolean(), "linked group define did not succeed: " + res.Text );
			Require( json.GetProperty( "definitionId" ).GetString() is not null, "linked group define returned no definition id" );
			var listed = Call2( mcp, "map_linked_group", JsonNode.Parse( "{\"op\":\"list\"}" )! );
			Require( Json( listed.Text ).GetProperty( "definitions" ).EnumerateArray()
				.Any( d => d.GetProperty( "name" ).GetString() == "crate-pair" ), "linked group list lacks crate-pair" );
			Require( mcp.LayerCount == 0, "linked group test polluted the layer store" );
		} );

		Check( "prop place seats a prop and places its entity", () =>
		{
			var baseMcp = NewBase( "pass2-prop-place" );
			BuildSealedRoom( baseMcp );
			var before = baseMcp.EntityCount;
			var mcp = NewPass2( baseMcp );
			var res = Call2( mcp, "map_prop_place", JsonNode.Parse(
				"{\"dropfrom\":[256,256,200],\"yaw\":90,\"model\":\"models/props/crate.mdl\",\"seat\":true}" )! );
			Require( !res.IsError, "prop place failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "seated" ).GetBoolean(), "prop did not seat on the floor: " + res.Text );
			Require( json.GetProperty( "seatedInSession" ).GetBoolean(), "prop was not seated in the session" );
			Require( json.TryGetProperty( "entityId", out _ ), "seated prop has no entity id" );
			Require( baseMcp.EntityCount == before + 1, "session did not gain the prop entity" );
			var pose = json.GetProperty( "pose" );
			Require( Math.Abs( pose.GetProperty( "origin" )[2].GetDouble() - 16.0 ) < 1.0, "prop did not land on the floor (z=16): " + res.Text );
		} );

		Check( "propper converts a brush to a model", () =>
		{
			var baseMcp = NewBase( "pass2-propper" );
			BuildSealedRoom( baseMcp );
			var block = AddBox( baseMcp, 128, 128, 16, 144, 144, 32 );
			var mcp = NewPass2( baseMcp );
			ThrowsArg( () => Call2( mcp, "map_propper", JsonNode.Parse( $"{{\"brushes\":[\"{block}\"]}}" )! ), "confirm" );
			var res = Call2( mcp, "map_propper", JsonNode.Parse( $"{{\"brushes\":[\"{block}\"],\"confirm\":true}}" )! );
			Require( !res.IsError, "propper failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "ok" ).GetBoolean(), "propper did not succeed: " + res.Text );
			Require( json.GetProperty( "removedBrushCount" ).GetInt32() == 1, "propper removed the wrong brush count: " + res.Text );
			Require( json.GetProperty( "sessionRemovedBrushIds" ).EnumerateArray().Any( i => i.GetString() == block ),
				"propper did not report the session brush as removed" );
			Require( mcp.PastedBrushCount == 0, "propper test polluted the paste overlay" );
		} );

		Check( "hotspot match finds a rect on a face", () =>
		{
			var baseMcp = NewBase( "pass2-hotspot" );
			BuildSealedRoom( baseMcp );
			var vmf = ExportVmf( baseMcp );
			var brush = FirstBrushId( vmf );
			var rects = "[{\"name\":\"square\",\"materialName\":\"dev/dev_measuregeneric01\",\"uMin\":0,\"vMin\":0,\"uMax\":1,\"vMax\":1},{\"name\":\"wide\",\"materialName\":\"dev/dev_measuregeneric01\",\"uMin\":0,\"vMin\":0,\"uMax\":1,\"vMax\":0.03125}]";
			var found = false;
			for ( var face = 0; face < 6 && !found; face++ )
			{
				var text = BuildScript.RunMapPass2Verb( "map_hotspot", vmf, Dict( ("op", "match"), ("brush", brush.ToString()), ("face", face.ToString()), ("hotspots", rects) ), baseMcp.WorkDirectory );
				var json = Json( text );
				Require( json.GetProperty( "status" ).GetString() == "ok", "hotspot match failed: " + text );
				found = json.GetProperty( "found" ).GetBoolean();
			}
			Require( found, "no face of the floor brush matched either hotspot rect" );
		} );

		Check( "browser searches the model registry", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-browser" ) );
			var res = Call2( mcp, "map_browser", JsonNode.Parse(
				"{\"kind\":\"model\",\"query\":\"crate\",\"entries\":[{\"name\":\"crate\",\"path\":\"models/props/crate.mdl\"}]}" )! );
			Require( !res.IsError, "browser search failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "resultCount" ).GetInt32() == 1, "browser found the wrong count: " + res.Text );
			Require( json.GetProperty( "results" )[0].GetProperty( "name" ).GetString() == "crate", "browser result is not the crate" );
		} );

		Check( "browser texture is a named capability error, not fake results", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-browser-texture" ) );
			var res = Call2( mcp, "map_browser", JsonNode.Parse( "{\"kind\":\"texture\",\"query\":\"brick\"}" )! );
			Require( res.Text.Contains( "texture-browser-no-registry", StringComparison.Ordinal ),
				"texture browser did not return the named capability error: " + res.Text );
		} );

		Check( "hotload watch and poll", () =>
		{
			var baseMcp = NewBase( "pass2-hotload" );
			var mcp = NewPass2( baseMcp );
			var file = Path.Combine( baseMcp.WorkDirectory, "models", "test.mdl" );
			Directory.CreateDirectory( Path.GetDirectoryName( file )! );
			File.WriteAllText( file, "pass2 hotload fixture" );
			var watched = Call2( mcp, "map_hotload", JsonNode.Parse( "{\"op\":\"watch\",\"kind\":\"model\",\"path\":\"models/test.mdl\"}" )! );
			Require( !watched.IsError, "hotload watch failed: " + watched.Text );
			Require( Json( watched.Text ).GetProperty( "watched" ).GetArrayLength() == 1, "hotload watch did not register the asset" );
			var polled = Call2( mcp, "map_hotload", JsonNode.Parse( "{\"op\":\"poll\"}" )! );
			Require( !polled.IsError, "hotload poll failed: " + polled.Text );
			Require( Json( polled.Text ).GetProperty( "changeCount" ).GetInt32() == 0, "hotload poll reported changes on an untouched file" );
		} );

		Check( "hotload reload needs confirm", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-hotload-confirm" ) );
			ThrowsArg( () => Call2( mcp, "map_hotload", JsonNode.Parse( "{\"op\":\"reload\"}" )! ), "confirm" );
			var reloaded = Call2( mcp, "map_hotload", JsonNode.Parse( "{\"op\":\"reload\",\"confirm\":true}" )! );
			Require( !reloaded.IsError, "hotload reload failed: " + reloaded.Text );
			Require( Json( reloaded.Text ).GetProperty( "hasIssues" ).GetBoolean() == false, "hotload reload reported issues: " + reloaded.Text );
		} );

		Check( "playtest validate and bake on the sealed room", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-playtest" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var validated = Call2( mcp, "map_playtest", JsonNode.Parse( "{\"op\":\"validate\"}" )! );
			Require( !validated.IsError, "playtest validate failed: " + validated.Text );
			var validation = Json( validated.Text ).GetProperty( "validation" );
			Require( validation.GetProperty( "hasPlayerStart" ).GetBoolean(), "playtest validation found no player start" );
			Require( validation.GetProperty( "playerStartCount" ).GetInt32() == 1, "playtest validation found the wrong player start count" );
			var baked = Call2( mcp, "map_playtest", JsonNode.Parse( "{\"op\":\"bake\"}" )! );
			Require( !baked.IsError, "playtest bake failed: " + baked.Text );
			Require( Json( baked.Text ).GetProperty( "bake" ).GetProperty( "totalFaces" ).GetInt32() == 36, "playtest bake counted the wrong faces" );
		} );

		Check( "navmesh generate covers the floor", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-navmesh" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var res = Call2( mcp, "map_navmesh_generate", JsonNode.Parse( "{}" )! );
			Require( !res.IsError, "navmesh generate failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "polyCount" ).GetInt32() > 0, "navmesh generated no polys for the sealed room floor" );
			Require( json.TryGetProperty( "cellSize", out _ ), "navmesh result has no cellSize" );
		} );

		Check( "safety: shell arg is refused, compile final needs confirm", () =>
		{
			var mcp = NewPass2( NewBase( "pass2-safety" ) );
			BuildSealedRoom( (McpMapTools)mcp.Base );
			var shelled = Call2( mcp, "map_gizmo", JsonNode.Parse( "{\"command\":\"echo hi\"}" )! );
			Require( shelled.IsError, "shell arg was not refused" );
			Require( shelled.Text.Contains( "shell-refused", StringComparison.Ordinal ), "shell refusal is not named shell-refused: " + shelled.Text );
			ThrowsArg( () => Call2( mcp, "map_compile_preset", JsonNode.Parse( "{\"preset\":\"final\"}" )! ), "confirm" );
			ThrowsArg( () => Call2( mcp, "map_paste_special", JsonNode.Parse( "{\"brushes\":[\"brush-1\"]}" )! ), "confirm" );
		} );

		Check( "kernel: unknown verb and shell arg are refused", () =>
		{
			ThrowsArg( () => BuildScript.RunMapPass2Verb( "map_nope", null, Dict(), null ), "Unknown pass-2 map verb" );
			ThrowsArg( () => BuildScript.RunMapPass2Verb( "map_gizmo", null, Dict( ("command", "echo hi") ), null ), "shell-refused" );
			ThrowsArg( () => BuildScript.RunMapPass2Verb( "map_gizmo", null, Dict( ("exec", "x") ), null ), "shell-refused" );
		} );
	}
}
