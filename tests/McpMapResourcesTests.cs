using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

/// <summary>Hammer Desktop AI: MCP map resources + prompts
/// (hammer-ai-mcp-map-resources-prompts). Extends the existing McpResources
/// system (mcp-resources-prompts-document-mesh-tests) with the map document,
/// the entity registry, the live issue list and the compile log, plus the
/// three map prompts. Fixture maps are built in code below (a sealed
/// 256x256x128 room, a room with a 1-unit gap in the +X wall, a room missing
/// the whole +X wall) — the same shapes IssueEngineTests uses; the fixture
/// registry is tests/fixtures/hammer/hammer-ai-mcp-map-resources-prompts/
/// registry.json. Run with:
/// dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter mcpmapresources
/// (from the repo root; the Register line in tests/TestRegistry.cs is
/// added by the final integrator — see the ticket handoff).</summary>
internal static class McpMapResourcesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mcpmapresources", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		JsonElement Json( string text )
		{
			using var doc = JsonDocument.Parse( text );
			return doc.RootElement.Clone();
		}

		string RepoRoot()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					if ( File.Exists( Path.Combine( dir.FullName, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir.FullName;
					dir = dir.Parent;
				}
			}
			throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
		}

		string Fixture( string name ) => Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-ai-mcp-map-resources-prompts", name );
		string CoreRegistryPath() => Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" );

		// ---- fixture maps: axis-aligned boxes, outward-wound ----
		// Face order: -X, +X, -Y, +Y, -Z, +Z. Same builder shape as
		// IssueEngineTests so the two groups agree on what a sealed room is.
		IssueBrush Box( int id, float x0, float y0, float z0, float x1, float y1, float z1, string material = "concrete/wall", bool isDetail = false )
		{
			IssueFace Face( Vec3 normal, params Vec3[] verts ) =>
				new IssueFace( normal, Vec3.Dot( normal, verts[0] ), material, verts );
			var faces = new[]
			{
				Face( new Vec3( -1, 0, 0 ), new Vec3( x0, y0, z0 ), new Vec3( x0, y0, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y1, z0 ) ),
				Face( new Vec3( 1, 0, 0 ), new Vec3( x1, y1, z0 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y0, z0 ) ),
				Face( new Vec3( 0, -1, 0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y0, z1 ), new Vec3( x0, y0, z1 ), new Vec3( x0, y0, z0 ) ),
				Face( new Vec3( 0, 1, 0 ), new Vec3( x0, y1, z0 ), new Vec3( x0, y1, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y1, z0 ) ),
				Face( new Vec3( 0, 0, -1 ), new Vec3( x0, y1, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x0, y0, z0 ) ),
				Face( new Vec3( 0, 0, 1 ), new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ) ),
			};
			return new IssueBrush( id, faces, isDetail );
		}

		IssueEntity PlayerStart() => new IssueEntity( 100, "info_player_start", new Vec3( 0, 0, 32 ) );
		IssueEntity Light() => new IssueEntity( 101, "light", new Vec3( 0, 0, 100 ) );

		// Sealed room, interior 256x256, floor z=0, ceiling z=128,
		// 16-unit walls. Brush ids: 1 floor, 2 ceiling, 3 -X, 4 +X,
		// 5 -Y, 6 +Y.
		IssueDocument SealedRoom()
		{
			var doc = new IssueDocument { Name = "clean-room" };
			doc.Brushes.Add( Box( 1, -128, -128, -16, 128, 128, 0 ) );
			doc.Brushes.Add( Box( 2, -128, -128, 128, 128, 128, 144 ) );
			doc.Brushes.Add( Box( 3, -144, -144, 0, -128, 144, 128 ) );
			doc.Brushes.Add( Box( 4, 128, -144, 0, 144, 144, 128 ) );
			doc.Brushes.Add( Box( 5, -128, -144, 0, 128, -128, 128 ) );
			doc.Brushes.Add( Box( 6, -128, 128, 0, 128, 144, 128 ) );
			doc.Entities.Add( PlayerStart() );
			doc.Entities.Add( Light() );
			return doc;
		}

		// Same room, but the +X wall is two brushes leaving a 1-unit
		// gap at y in [-0.5, 0.5] — the leak ticket's 1-unit gap.
		IssueDocument LeakRoom()
		{
			var doc = SealedRoom();
			doc.Name = "leak-room";
			doc.Brushes.RemoveAll( b => b.Id == 4 );
			doc.Brushes.Add( Box( 4, 128, 0.5f, 0, 144, 144, 128 ) );
			doc.Brushes.Add( Box( 7, 128, -144, 0, 144, -0.5f, 128 ) );
			return doc;
		}

		// Same room with the whole +X wall missing.
		IssueDocument UnsealedRoom()
		{
			var doc = SealedRoom();
			doc.Name = "unsealed-room";
			doc.Brushes.RemoveAll( b => b.Id == 4 );
			return doc;
		}

		// The compile runner's input model, projected from the fixture map
		// (faces as planes: outward normal + distance + material). This is
		// the one-projection seam the CompileRunner header names: when
		// MapDocument lands, CompileInput.FromDocument replaces this helper.
		CompileInput ToCompileInput( IssueDocument doc )
		{
			var input = new CompileInput( doc.Name );
			foreach ( var b in doc.Brushes )
			{
				var faces = b.Faces.Select( f => new CompileFace( f.Normal, (float)f.Distance, f.Material ) ).ToArray();
				input.Brushes.Add( new CompileBrush( "brush-" + b.Id, b.Id, faces, b.IsDetail ) );
			}
			var start = doc.Entities.FirstOrDefault( e => e.ClassName == "info_player_start" );
			if ( start is not null )
				input.PlayerStart = start.Origin;
			foreach ( var e in doc.Entities.Where( e => e.ClassName == "light" ) )
				input.Lights.Add( new CompileLight( e.Origin ) );
			return input;
		}

		CompileResult QuickPlay( IssueDocument doc, string workName )
		{
			var dir = Path.Combine( settings.WorkDirectory, "mcpmapresources", workName );
			return CompileRunner.Run( ToCompileInput( doc ), CompilePreset.QuickPlay,
				new CompileOptions { OutputFolder = dir } );
		}

		McpDocumentSource MapSource( IssueDocument map ) => new() { CurrentMap = () => map };
		McpDocumentSource RegistrySource( EntityRegistry reg ) => new() { CurrentRegistry = () => reg };

		Check( "list-resources returns the eight geppetto URIs", () =>
		{
			var list = McpResources.ListResources();
			Require( list.Count == 8, $"expected 8 resources, got {list.Count}" );
			Require( list[0].Uri == "geppetto://document", $"first resource is {list[0].Uri}" );
			Require( list[1].Uri == "geppetto://document/mesh", $"second resource is {list[1].Uri}" );
			Require( list[2].Uri == "geppetto://tests/latest", $"third resource is {list[2].Uri}" );
			Require( list[3].Uri == "geppetto://catalog", $"fourth resource is {list[3].Uri}" );
			Require( list[4].Uri == "geppetto://map", $"fifth resource is {list[4].Uri}" );
			Require( list[5].Uri == "geppetto://map/entities", $"sixth resource is {list[5].Uri}" );
			Require( list[6].Uri == "geppetto://map/issues", $"seventh resource is {list[6].Uri}" );
			Require( list[7].Uri == "geppetto://map/compile-log", $"eighth resource is {list[7].Uri}" );
			Require( list.All( r => r.MimeType == "application/json" ), "all resources are JSON" );
		} );

		Check( "map resource with no map reports open false", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://map" ).Text );
			Require( root.GetProperty( "open" ).GetBoolean() == false, "open is false with no source" );
			Require( root.TryGetProperty( "note", out _ ), "no-map note explains why" );
		} );

		Check( "map resource returns the fixture map's brushes and entities", () =>
		{
			var map = SealedRoom();
			var root = Json( McpResources.ReadResource( "geppetto://map", MapSource( map ) ).Text );
			Require( root.GetProperty( "open" ).GetBoolean(), "open is true" );
			Require( root.GetProperty( "name" ).GetString() == "clean-room", "map name matches the fixture" );
			var counts = root.GetProperty( "counts" );
			Require( counts.GetProperty( "brushes" ).GetInt64() == 6, $"expected 6 brushes, got {counts.GetProperty( "brushes" )}" );
			Require( counts.GetProperty( "faces" ).GetInt64() == 36, $"expected 36 faces, got {counts.GetProperty( "faces" )}" );
			Require( counts.GetProperty( "entities" ).GetInt64() == 2, $"expected 2 entities, got {counts.GetProperty( "entities" )}" );
			var brushes = root.GetProperty( "brushes" );
			Require( brushes[0].GetProperty( "id" ).GetInt64() == 1, "first brush id is 1" );
			Require( brushes[0].GetProperty( "faceCount" ).GetInt64() == 6, "a box brush has 6 faces" );
			Require( brushes[0].GetProperty( "volume" ).GetDouble() > 0, "floor brush volume is positive" );
			Require( brushes[0].GetProperty( "materials" )[0].GetString() == "concrete/wall", "brush material is concrete/wall" );
			Require( brushes[0].GetProperty( "isDetail" ).GetBoolean() == false, "sealing brushes are not detail" );
			var entities = root.GetProperty( "entities" );
			Require( entities[0].GetProperty( "className" ).GetString() == "info_player_start", "first entity is the player start" );
			Require( entities[0].GetProperty( "origin" )[2].GetDouble() == 32, "player start is 32 above the floor" );
			Require( entities[1].GetProperty( "className" ).GetString() == "light", "second entity is the light" );
			var limits = root.GetProperty( "limits" );
			Require( limits.GetProperty( "maxBrushes" ).GetInt64() == 8192, "default brush limit is 8192" );
		} );

		Check( "map resource reads are deterministic", () =>
		{
			var source = MapSource( SealedRoom() );
			var first = McpResources.ReadResource( "geppetto://map", source ).Text;
			var second = McpResources.ReadResource( "geppetto://map", source ).Text;
			Require( first == second, "two reads of the same map differ" );
		} );

		Check( "registry resource with no registry reports registered false", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://map/entities" ).Text );
			Require( root.GetProperty( "registered" ).GetBoolean() == false, "registered is false with no source" );
			Require( root.TryGetProperty( "note", out _ ), "no-registry note explains why" );
		} );

		Check( "registry resource returns the fixture registry classes", () =>
		{
			var reg = EntityRegistry.Load( Fixture( "registry.json" ) );
			var root = Json( McpResources.ReadResource( "geppetto://map/entities", RegistrySource( reg ) ).Text );
			Require( root.GetProperty( "registered" ).GetBoolean(), "registered is true" );
			Require( root.GetProperty( "name" ).GetString() == "mcp-map-test", "registry name matches the fixture" );
			Require( root.GetProperty( "schemaVersion" ).GetInt64() == 1, "schema version is 1" );
			Require( root.GetProperty( "classCount" ).GetInt64() == 3, $"expected 3 classes, got {root.GetProperty( "classCount" )}" );
			var classes = root.GetProperty( "classes" );
			var gadget = classes.EnumerateArray().First( c => c.GetProperty( "name" ).GetString() == "prop_custom_gadget" );
			Require( gadget.GetProperty( "kind" ).GetString() == "Point", "gadget is a point entity" );
			var keyValues = gadget.GetProperty( "keyValues" );
			var power = keyValues.EnumerateArray().First( kv => kv.GetProperty( "name" ).GetString() == "gadget_power" );
			Require( power.GetProperty( "type" ).GetString() == "Float", "gadget_power is a float" );
			Require( power.GetProperty( "default" ).GetString() == "1.5", "gadget_power default is 1.5" );
			var mode = keyValues.EnumerateArray().First( kv => kv.GetProperty( "name" ).GetString() == "gadget_mode" );
			Require( mode.GetProperty( "choices" ).GetArrayLength() == 3, "gadget_mode has 3 choices" );
			var flags = gadget.GetProperty( "spawnflags" );
			Require( flags.GetArrayLength() == 1 && flags[0].GetProperty( "name" ).GetString() == "Starts Charged", "gadget spawnflag is listed" );
			Require( gadget.GetProperty( "inputs" ).GetArrayLength() == 2, "gadget has 2 inputs" );
			Require( gadget.GetProperty( "outputs" )[0].GetProperty( "name" ).GetString() == "OnFullyCharged", "gadget output is listed" );
			var implicitKeyValues = root.GetProperty( "implicitKeyValues" );
			Require( implicitKeyValues.EnumerateArray().Any( kv => kv.GetProperty( "name" ).GetString() == "targetname" ), "implicit keyvalues list targetname" );
		} );

		Check( "registry resource renders the shipped core registry", () =>
		{
			var reg = EntityRegistry.Load( CoreRegistryPath() );
			var root = Json( McpResources.ReadResource( "geppetto://map/entities", RegistrySource( reg ) ).Text );
			Require( root.GetProperty( "name" ).GetString() == "source1-core", "core registry name" );
			Require( root.GetProperty( "classCount" ).GetInt64() == 14, $"expected 14 core classes, got {root.GetProperty( "classCount" )}" );
			var names = root.GetProperty( "classes" ).EnumerateArray().Select( c => c.GetProperty( "name" ).GetString() ).ToArray();
			Require( names.Contains( "info_player_start" ), "core registry has info_player_start" );
			Require( names.Contains( "func_door" ), "core registry has func_door" );
		} );

		Check( "issues resource with no map reports open false", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://map/issues" ).Text );
			Require( root.GetProperty( "open" ).GetBoolean() == false, "open is false with no source" );
		} );

		Check( "issues resource on the sealed room reports zero issues", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://map/issues", MapSource( SealedRoom() ) ).Text );
			Require( root.GetProperty( "open" ).GetBoolean(), "open is true" );
			var counts = root.GetProperty( "counts" );
			Require( counts.GetProperty( "total" ).GetInt64() == 0, $"expected 0 issues, got {counts.GetProperty( "total" )}" );
			Require( counts.GetProperty( "errors" ).GetInt64() == 0, "no errors on a sealed room" );
			var stats = root.GetProperty( "stats" );
			Require( stats.GetProperty( "rulesRun" ).GetInt64() == 8, $"expected 8 rules, got {stats.GetProperty( "rulesRun" )}" );
			Require( stats.GetProperty( "brushesChecked" ).GetInt64() == 6, "6 brushes checked" );
			Require( stats.GetProperty( "entitiesChecked" ).GetInt64() == 2, "2 entities checked" );
			Require( stats.GetProperty( "wasIncremental" ).GetBoolean() == false, "a resource read is a full run" );
		} );

		Check( "issues resource on the leak room reports a leak", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://map/issues", MapSource( LeakRoom() ) ).Text );
			var counts = root.GetProperty( "counts" );
			Require( counts.GetProperty( "total" ).GetInt64() >= 1, "a leak room has issues" );
			Require( counts.GetProperty( "byKind" ).TryGetProperty( "Leak", out var leakCount ) && leakCount.GetInt64() == 1,
				$"expected exactly 1 leak issue, got {counts.GetProperty( "byKind" )}" );
			var leak = root.GetProperty( "issues" ).EnumerateArray().First( i => i.GetProperty( "code" ).GetString() == "LEAK" );
			Require( leak.GetProperty( "severity" ).GetString() == "Error", "a leak is an error" );
			Require( leak.GetProperty( "hasLocation" ).GetBoolean(), "a leak carries its gap location" );
			Require( leak.GetProperty( "location" )[0].GetDouble() > 128, "gap location is at/through the +X wall" );
			Require( !string.IsNullOrEmpty( leak.GetProperty( "fixHint" ).GetString() ), "a leak names its fix" );
		} );

		Check( "issues resource on the unsealed room reports unsealed and no leak", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://map/issues", MapSource( UnsealedRoom() ) ).Text );
			var counts = root.GetProperty( "counts" );
			Require( counts.GetProperty( "byKind" ).TryGetProperty( "UnsealedMap", out var unsealed ) && unsealed.GetInt64() == 1,
				$"expected 1 unsealed issue, got {counts.GetProperty( "byKind" )}" );
			Require( !counts.GetProperty( "byKind" ).TryGetProperty( "Leak", out _ ), "a missing wall is unsealed, not a leak" );
		} );

		Check( "compile-log with no compile reports recorded false", () =>
		{
			var root = Json( McpResources.ReadResource( "geppetto://map/compile-log" ).Text );
			Require( root.GetProperty( "recorded" ).GetBoolean() == false, "recorded is false with no run" );
			Require( root.TryGetProperty( "howToRun", out var how ) && how.GetString()!.Contains( "map_compile" ), "howToRun names the map_compile tool" );
		} );

		Check( "compile-log reports a real QuickPlay compile of the fixture map", () =>
		{
			var result = QuickPlay( SealedRoom(), "quickplay" );
			Require( result.Succeeded, "QuickPlay failed: " + result.Error );
			var source = new McpDocumentSource { LatestCompile = () => result };
			var root = Json( McpResources.ReadResource( "geppetto://map/compile-log", source ).Text );
			Require( root.GetProperty( "recorded" ).GetBoolean(), "recorded is true" );
			Require( root.GetProperty( "preset" ).GetString() == "QuickPlay", "preset is QuickPlay" );
			Require( root.GetProperty( "succeeded" ).GetBoolean(), "compile succeeded" );
			var stages = root.GetProperty( "completedStages" );
			Require( stages.GetArrayLength() == 1 && stages[0].GetString() == "BspBuild", "QuickPlay completes only the BSP stage" );
			var bsp = root.GetProperty( "bsp" );
			Require( bsp.GetProperty( "leafCount" ).GetInt64() > 0, "BSP has leaves" );
			Require( bsp.GetProperty( "portalCount" ).GetInt64() >= 0, "portal count is present" );
			var leak = root.GetProperty( "leak" );
			Require( leak.ValueKind == JsonValueKind.Object && leak.GetProperty( "isLeaked" ).GetBoolean() == false, "sealed room does not leak" );
			var log = root.GetProperty( "log" );
			Require( log.GetArrayLength() > 0, "compile log lines are present" );
			Require( log[0].GetString()!.Contains( "Compile started" ), "first log line starts the compile" );
			Require( root.GetProperty( "outputFiles" ).GetArrayLength() >= 1, "a successful compile writes products" );
		} );

		Check( "compile-log reports a leaking compile as failed with a leak", () =>
		{
			var result = QuickPlay( LeakRoom(), "leak" );
			Require( !result.Succeeded, "leaking map compiled successfully" );
			var source = new McpDocumentSource { LatestCompile = () => result };
			var root = Json( McpResources.ReadResource( "geppetto://map/compile-log", source ).Text );
			Require( root.GetProperty( "succeeded" ).GetBoolean() == false, "leaking compile did not succeed" );
			var leak = root.GetProperty( "leak" );
			Require( leak.ValueKind == JsonValueKind.Object && leak.GetProperty( "isLeaked" ).GetBoolean(), "leak result reports leaked" );
			Require( leak.GetProperty( "gapPortalId" ).GetInt64() >= 0, "leak names its gap portal" );
			Require( root.GetProperty( "issues" ).GetArrayLength() >= 1, "a failed compile lists issues" );
		} );

		Check( "list-prompts returns the seven prompts", () =>
		{
			var list = McpResources.ListPrompts();
			Require( list.Count == 7, $"expected 7 prompts, got {list.Count}" );
			var names = list.Select( p => p.Name ).ToArray();
			Require( names.Contains( "sculpt-face-from-reference" ), "sculpt prompt listed" );
			Require( names.Contains( "fix-topology-report" ), "fix prompt listed" );
			Require( names.Contains( "export-for-godot" ), "export prompt listed" );
			Require( names.Contains( "review-model-sheet" ), "review prompt listed" );
			Require( names.Contains( "build-a-sealed-room" ), "sealed-room prompt listed" );
			Require( names.Contains( "find-the-leak" ), "leak prompt listed" );
			Require( names.Contains( "optimise-this-map" ), "optimise prompt listed" );
			var build = list.First( p => p.Name == "build-a-sealed-room" );
			Require( build.Arguments.Any( a => a.Name == "size" && !a.Required ), "size is an optional build argument" );
			Require( build.Arguments.Any( a => a.Name == "material" && !a.Required ), "material is an optional build argument" );
		} );

		Check( "each map prompt resolves to the right map tool calls", () =>
		{
			var build = McpResources.GetPrompt( "build-a-sealed-room", new Dictionary<string, string> { ["size"] = "256x256x128", ["material"] = "concrete/wall", ["name"] = "room" } );
			Require( build.Messages.Count == 1 && build.Messages[0].Role == "user", "build renders one user message" );
			Require( build.Messages[0].Text.Contains( "map_brush_create" ), "build leans on map_brush_create" );
			Require( build.Messages[0].Text.Contains( "map_entity_place" ), "build leans on map_entity_place" );
			Require( build.Messages[0].Text.Contains( "map_validate" ), "build leans on map_validate" );
			Require( build.Messages[0].Text.Contains( "map_compile" ), "build leans on map_compile" );
			Require( build.Messages[0].Text.Contains( "geppetto://map/entities" ), "build checks the registry resource" );

			var leak = McpResources.GetPrompt( "find-the-leak", new Dictionary<string, string>() );
			Require( leak.Messages[0].Text.Contains( "map_validate" ), "leak leans on map_validate" );
			Require( leak.Messages[0].Text.Contains( "geppetto://map/issues" ), "leak reads the live issues resource" );
			Require( leak.Messages[0].Text.Contains( "map_compile" ), "leak leans on map_compile" );
			Require( leak.Messages[0].Text.Contains( "map_brush_create" ), "leak plugs with map_brush_create" );

			var optimise = McpResources.GetPrompt( "optimise-this-map", new Dictionary<string, string> { ["focus"] = "compile-time and visibility" } );
			Require( optimise.Messages[0].Text.Contains( "map_compile" ), "optimise leans on map_compile" );
			Require( optimise.Messages[0].Text.Contains( "map_bake_lightmap" ), "optimise leans on map_bake_lightmap" );
			Require( optimise.Messages[0].Text.Contains( "map_brush_edit" ), "optimise details with map_brush_edit" );
			Require( optimise.Messages[0].Text.Contains( "geppetto://map/compile-log" ), "optimise reads the compile-log resource" );
			Require( optimise.Messages[0].Text.Contains( "compile-time and visibility" ), "optimise echoes the focus" );
		} );

		Check( "unknown map resource and prompt throw ArgumentException", () =>
		{
			try
			{
				McpResources.ReadResource( "geppetto://map/nope" );
				throw new Exception( "expected ArgumentException for an unknown map resource" );
			}
			catch ( ArgumentException ex )
			{
				Require( ex.Message.Contains( "geppetto://map/nope" ), "error names the unknown URI" );
				Require( ex.Message.Contains( "geppetto://map" ), "error lists the known map resources" );
			}
			try
			{
				McpResources.GetPrompt( "nope-map" );
				throw new Exception( "expected ArgumentException for an unknown prompt" );
			}
			catch ( ArgumentException ex )
			{
				Require( ex.Message.Contains( "nope-map" ), "error names the unknown prompt" );
				Require( ex.Message.Contains( "build-a-sealed-room" ), "error lists the known map prompts" );
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
