// McpMapToolsTests.cs — MCP tools for native map operations (task hammer-ai-mcp-map-tools).
//
// Exercises the map-tools layer the MCP transport calls, without the
// transport: ListTools is the 18 map_* definitions; CallAsync routes one
// tool per group over a fixture map (a sealed room built through the
// tools themselves); an invalid tool call returns a named error, not a
// crash. Registered in TestRegistry.cs (group mcpmaptools).
//
// Fixtures live in tests/fixtures/hammer/hammer-ai-mcp-map-tools/:
// entity-registry.json (the 10 classes the tools need; the embedded
// DefaultRegistryJson is the same set). The sealed room is built by the
// tests, not checked in as a .vmf: export/import round-trips through
// inline text instead.
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

internal static class McpMapToolsTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "mcpmaptools", name, test );
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
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-ai-mcp-map-tools" );
					if ( Directory.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}
		McpMapTools NewMcp( string dir )
		{
			var fixtureDir = FindFixtureDir();
			var registry = fixtureDir is null
				? null
				: EntityRegistry.Load( Path.Combine( fixtureDir, "entity-registry.json" ) );
			return new McpMapTools( registry, Path.Combine( settings.WorkDirectory, dir ) );
		}
		McpModelingTools.McpToolCallResult Call( McpMapTools mcp, string name, JsonNode? args )
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

		Check( "tools list is the 18 map tools with schemas", () =>
		{
			var mcp = NewMcp( "mcp-map-tools-list" );
			var list = mcp.ListTools();
			var expected = new[]
			{
				"map_brush_create", "map_brush_edit", "map_brush_clip", "map_brush_vertex",
				"map_displacement_create", "map_displacement_sculpt", "map_face_material",
				"map_entity_place", "map_entity_edit", "map_io_add", "map_group", "map_visgroup",
				"map_import_export", "map_compile", "map_validate", "map_bake_lightmap",
				"map_navmesh", "map_skybox_cubemap",
			};
			Require( list.Count == expected.Length, $"expected {expected.Length} map tools, got {list.Count}" );
			for ( var i = 0; i < expected.Length; i++ )
			{
				var tool = list[i]!.AsObject();
				Require( tool["name"]?.GetValue<string>() == expected[i], $"tool {i} is not {expected[i]}" );
				var schema = tool["inputSchema"]!.AsObject();
				Require( schema["type"]?.GetValue<string>() == "object", expected[i] + " schema is not an object" );
				Require( schema["properties"] is JsonObject, expected[i] + " schema has no properties" );
				Require( schema["required"] is JsonArray, expected[i] + " schema has no required array" );
				Require( McpMapTools.BuildScriptVerbFor( expected[i] ) == expected[i], expected[i] + " has no BuildScript verb seam" );
			}
		} );

		Check( "fixture registry loads and matches the embedded default", () =>
		{
			var fixtureDir = FindFixtureDir();
			Require( fixtureDir is not null, "fixture dir tests/fixtures/hammer/hammer-ai-mcp-map-tools not found" );
			var fromFile = EntityRegistry.Load( Path.Combine( fixtureDir!, "entity-registry.json" ) );
			Require( fromFile.Count == 10, $"fixture registry has {fromFile.Count} classes, expected 10" );
			Require( fromFile.Find( "info_player_start" ) is not null, "fixture registry lacks info_player_start" );
			Require( fromFile.Find( "func_door" ) is not null, "fixture registry lacks func_door" );
			var embedded = new McpMapTools();
			Require( embedded.Registry.Count == fromFile.Count, $"embedded registry has {embedded.Registry.Count} classes, fixture has {fromFile.Count}" );
		} );

		Check( "brush create box has exact volume and 6 faces", () =>
		{
			var mcp = NewMcp( "mcp-map-box" );
			var res = Call( mcp, "map_brush_create", JsonNode.Parse( "{\"shape\":\"box\",\"min\":[0,0,0],\"max\":[12,8,4],\"material\":\"brick/brickwall001\"}" )! );
			Require( !res.IsError, "box create failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "faces" ).GetInt32() == 6, "box has " + json.GetProperty( "faces" ).GetInt32() + " faces" );
			Require( Math.Abs( json.GetProperty( "volume" ).GetDouble() - 384.0 ) < 1e-3, "box volume is not 384: " + res.Text );
			Require( json.GetProperty( "valid" ).GetBoolean(), "box is not valid" );
			Require( mcp.BrushCount == 1, "session has no brush" );
			Require( mcp.FindBrush( json.GetProperty( "brushId" ).GetString()! )!.Volume == 384f, "stored brush volume is not 384" );
		} );

		Check( "brush create wedge cylinder sphere arch have the plane-built volumes", () =>
		{
			var mcp = NewMcp( "mcp-map-prims" );
			var wedge = Call( mcp, "map_brush_create", JsonNode.Parse( "{\"shape\":\"wedge\",\"size\":[12,8,4]}" )! );
			Require( !wedge.IsError, "wedge failed: " + wedge.Text );
			Require( Math.Abs( Json( wedge.Text ).GetProperty( "volume" ).GetDouble() - 192.0 ) < 1e-2, "wedge volume is not 192: " + wedge.Text );
			var cylinder = Call( mcp, "map_brush_create", JsonNode.Parse( "{\"shape\":\"cylinder\",\"radius\":8,\"height\":16,\"segments\":16}" )! );
			Require( !cylinder.IsError, "cylinder failed: " + cylinder.Text );
			var cylinderJson = Json( cylinder.Text );
			Require( cylinderJson.GetProperty( "faces" ).GetInt32() == 18, "cylinder has " + cylinderJson.GetProperty( "faces" ).GetInt32() + " faces, expected 18" );
			Require( Math.Abs( cylinderJson.GetProperty( "volume" ).GetDouble() - 3258.4 ) < 1.0, "cylinder volume is off: " + cylinder.Text );
			var sphere = Call( mcp, "map_brush_create", JsonNode.Parse( "{\"shape\":\"sphere\",\"radius\":8,\"divisions\":4,\"segments\":16}" )! );
			Require( !sphere.IsError, "sphere failed: " + sphere.Text );
			var sphereJson = Json( sphere.Text );
			Require( sphereJson.GetProperty( "faces" ).GetInt32() == 50, "sphere has " + sphereJson.GetProperty( "faces" ).GetInt32() + " faces, expected 50" );
			Require( sphereJson.GetProperty( "volume" ).GetDouble() > 2144.0, "sphere volume is below the analytic sphere: " + sphere.Text );
			var arch = Call( mcp, "map_brush_create", JsonNode.Parse( "{\"shape\":\"arch\",\"radius\":8,\"height\":16,\"sweepDegrees\":180,\"segments\":8}" )! );
			Require( !arch.IsError, "arch failed: " + arch.Text );
			var archJson = Json( arch.Text );
			Require( archJson.GetProperty( "faces" ).GetInt32() == 11, "arch has " + archJson.GetProperty( "faces" ).GetInt32() + " faces, expected 11" );
			Require( Math.Abs( archJson.GetProperty( "volume" ).GetDouble() - (7 * 8 * 8 * Math.Tan(Math.PI / 14) * 16) ) < 0.01, "arch volume is off: " + arch.Text );
		} );

		Check( "brush create planes builds a box and rejects a degenerate one", () =>
		{
			var mcp = NewMcp( "mcp-map-planes" );
			var box = Call( mcp, "map_brush_create", JsonNode.Parse( "{\"shape\":\"planes\",\"faces\":[{\"normal\":[-1,0,0],\"distance\":0},{\"normal\":[1,0,0],\"distance\":12},{\"normal\":[0,-1,0],\"distance\":0},{\"normal\":[0,1,0],\"distance\":8},{\"normal\":[0,0,-1],\"distance\":0},{\"normal\":[0,0,1],\"distance\":4}]}" )! );
			Require( !box.IsError, "planes box failed: " + box.Text );
			Require( Math.Abs( Json( box.Text ).GetProperty( "volume" ).GetDouble() - 384.0 ) < 1e-3, "planes box volume is not 384: " + box.Text );
			var flat = Call( mcp, "map_brush_create", JsonNode.Parse( "{\"shape\":\"box\",\"min\":[0,0,0],\"max\":[12,8,0]}" )! );
			Require( flat.IsError, "degenerate box should fail" );
			Require( flat.Text.Contains( "brush-invalid", StringComparison.Ordinal ), "degenerate box error is not named brush-invalid: " + flat.Text );
			Require( flat.Text.Contains( "ZeroVolume", StringComparison.Ordinal ), "degenerate box error does not name ZeroVolume: " + flat.Text );
			Require( mcp.BrushCount == 1, "failed create left a brush behind" );
		} );

		Check( "brush create unknown shape is invalid params", () =>
		{
			var mcp = NewMcp( "mcp-map-bad-shape" );
			ThrowsArg( () => Call( mcp, "map_brush_create", JsonNode.Parse( "{\"shape\":\"torus\"}" )! ), "Unknown brush shape" );
		} );

		Check( "brush edit translate moves the centroid and setMaterial renames a face", () =>
		{
			var mcp = NewMcp( "mcp-map-edit" );
			var id = AddBox( mcp, 0, 0, 0, 12, 8, 4 );
			var moved = Call( mcp, "map_brush_edit", JsonNode.Parse( $"{{\"brushId\":\"{id}\",\"op\":\"translate\",\"delta\":[10,20,30]}}" )! );
			Require( !moved.IsError, "translate failed: " + moved.Text );
			var centroid = Json( moved.Text ).GetProperty( "centroid" );
			Require( Math.Abs( centroid[0].GetDouble() - 16.0 ) < 1e-3, "centroid x did not move to 16: " + moved.Text );
			Require( Math.Abs( centroid[1].GetDouble() - 24.0 ) < 1e-3, "centroid y did not move to 24: " + moved.Text );
			Require( Math.Abs( centroid[2].GetDouble() - 32.0 ) < 1e-3, "centroid z did not move to 32: " + moved.Text );
			var painted = Call( mcp, "map_brush_edit", JsonNode.Parse( $"{{\"brushId\":\"{id}\",\"op\":\"setMaterial\",\"face\":0,\"material\":\"brick/brickwall001\"}}" )! );
			Require( !painted.IsError, "setMaterial failed: " + painted.Text );
			Require( mcp.FindBrush( id )!.Faces[0].MaterialId == "brick/brickwall001", "face 0 material did not change" );
			Require( mcp.FindBrush( id )!.Faces[1].MaterialId != "brick/brickwall001", "setMaterial leaked to face 1" );
		} );

		Check( "brush clip cut halves the box, split yields two, a miss is a named no-op", () =>
		{
			var mcp = NewMcp( "mcp-map-clip" );
			var id = AddBox( mcp, -64, -64, -64, 64, 64, 64 );
			var cut = Call( mcp, "map_brush_clip", JsonNode.Parse( $"{{\"brushId\":\"{id}\",\"a\":[0,-64,0],\"b\":[0,64,0],\"viewAxis\":\"z\"}}" )! );
			Require( !cut.IsError, "clip cut failed: " + cut.Text );
			var cutJson = Json( cut.Text );
			Require( cutJson.GetProperty( "outcome" ).GetString() == "Cut", "clip outcome is not Cut: " + cut.Text );
			Require( Math.Abs( cutJson.GetProperty( "volume" ).GetDouble() - 1048576.0 ) < 1.0, "cut volume is not half: " + cut.Text );
			var split = Call( mcp, "map_brush_clip", JsonNode.Parse( $"{{\"brushId\":\"{id}\",\"a\":[32,-64,0],\"b\":[32,64,0],\"viewAxis\":\"z\",\"mode\":\"split\"}}" )! );
			Require( !split.IsError, "clip split failed: " + split.Text );
			var splitJson = Json( split.Text );
			Require( splitJson.GetProperty( "outcome" ).GetString() == "Split", "split outcome is not Split: " + split.Text );
			Require( splitJson.GetProperty( "secondBrushId" ).GetString() is string second && second.Length > 0, "split returned no second brush: " + split.Text );
			Require( mcp.BrushCount == 2, "split did not leave two brushes" );
			var miss = Call( mcp, "map_brush_clip", JsonNode.Parse( $"{{\"brushId\":\"{id}\",\"a\":[200,64,0],\"b\":[200,-64,0],\"viewAxis\":\"z\"}}" )! );
			Require( !miss.IsError, "a plane miss should be a named no-op, not an error: " + miss.Text );
			var missJson = Json( miss.Text );
			Require( missJson.GetProperty( "outcome" ).GetString() == "NoOpPlaneOutside", "miss outcome is not NoOpPlaneOutside: " + miss.Text );
			Require( !missJson.GetProperty( "changed" ).GetBoolean(), "miss claims it changed the brush" );
		} );

		Check( "brush vertex move reports the twisted face instead of repairing it", () =>
		{
			var mcp = NewMcp( "mcp-map-vertex" );
			var id = AddBox( mcp, -64, -64, -64, 64, 64, 64 );
			var brush = mcp.FindBrush( id )!;
			var top = brush.Vertices.OrderByDescending( v => v.z ).ThenBy( v => v.x ).ThenBy( v => v.y ).First();
			var topIndex = brush.Vertices.ToList().IndexOf( top );
			var before = brush.Volume;
			var moved = Call( mcp, "map_brush_vertex", new JsonObject
			{
				["brushId"] = id,
				["vertex"] = topIndex,
				["to"] = new JsonArray( (double)top.x, (double)top.y, (double)top.z + 64 ),
			} );
			Require( !moved.IsError, "vertex move failed: " + moved.Text );
			var json = Json( moved.Text );
			Require( !json.GetProperty( "valid" ).GetBoolean(), "moving one box corner up should twist the top face" );
			Require( json.GetProperty( "invalidFaces" ).GetArrayLength() > 0, "twisted move names no invalid faces: " + moved.Text );
			Require( json.GetProperty( "defects" ).GetArrayLength() > 0, "twisted move names no defects: " + moved.Text );
			Require( json.GetProperty( "volume" ).GetDouble() > before, "moved brush volume did not grow: " + moved.Text );
		} );

		Check( "displacement create and sculpt raise the grid", () =>
		{
			var mcp = NewMcp( "mcp-map-disp" );
			var created = Call( mcp, "map_displacement_create", JsonNode.Parse( "{\"corners\":[[0,0,16],[128,0,16],[128,128,16],[0,128,16]],\"power\":2}" )! );
			Require( !created.IsError, "displacement create failed: " + created.Text );
			var json = Json( created.Text );
			var dispId = json.GetProperty( "displacementId" ).GetString()!;
			Require( json.GetProperty( "gridSize" ).GetInt32() == 5, "power 2 grid is not 5x5: " + created.Text );
			Require( json.GetProperty( "vertexCount" ).GetInt32() == 25, "power 2 vertex count is not 25: " + created.Text );
			Require( !json.GetProperty( "sealsMap" ).GetBoolean(), "a displacement must never seal the map" );
			var sculpted = Call( mcp, "map_displacement_sculpt", JsonNode.Parse( $"{{\"displacementId\":\"{dispId}\",\"mode\":\"raise\",\"position\":[64,64,16],\"radius\":64,\"strength\":8}}" )! );
			Require( !sculpted.IsError, "sculpt failed: " + sculpted.Text );
			var sculptJson = Json( sculpted.Text );
			Require( sculptJson.GetProperty( "verticesTouched" ).GetInt32() > 0, "raise touched no vertices: " + sculpted.Text );
			Require( sculptJson.GetProperty( "maxOffsetDelta" ).GetDouble() > 0, "raise changed no offset: " + sculpted.Text );
			var badPower = Call( mcp, "map_displacement_create", JsonNode.Parse( "{\"corners\":[[0,0,16],[128,0,16],[128,128,16],[0,128,16]],\"power\":5}" )! );
			Require( badPower.IsError, "power 5 should fail" );
		} );

		Check( "face material sets one face and its UV", () =>
		{
			var mcp = NewMcp( "mcp-map-facemat" );
			var id = AddBox( mcp, 0, 0, 0, 12, 8, 4 );
			var res = Call( mcp, "map_face_material", JsonNode.Parse( $"{{\"brushId\":\"{id}\",\"face\":0,\"material\":\"brick/brickwall001\",\"uv\":{{\"scale\":[2,2]}}}}" )! );
			Require( !res.IsError, "face material failed: " + res.Text );
			var face = mcp.FindBrush( id )!.Faces[0];
			Require( face.MaterialId == "brick/brickwall001", "face material did not change" );
			Require( Math.Abs( face.Uv.Scale.x - 2f ) < 1e-6 && Math.Abs( face.Uv.Scale.y - 2f ) < 1e-6, "face UV scale did not change to 2" );
			Require( mcp.FindBrush( id )!.Faces[1].MaterialId != "brick/brickwall001", "face material leaked to face 1" );
			var all = Call( mcp, "map_face_material", JsonNode.Parse( $"{{\"brushId\":\"{id}\",\"face\":\"all\",\"material\":\"dev/dev_measuregeneric01\"}}" )! );
			Require( !all.IsError, "face material all failed: " + all.Text );
			Require( Json( all.Text ).GetProperty( "facesChanged" ).GetInt32() == 6, "all-faces change did not touch 6 faces: " + all.Text );
		} );

		Check( "entity place point and brush, unknown class is a named error", () =>
		{
			var mcp = NewMcp( "mcp-map-entity" );
			var start = Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"info_player_start\",\"position\":[256,256,64]}" )! );
			Require( !start.IsError, "player start failed: " + start.Text );
			Require( Json( start.Text ).GetProperty( "entityId" ).GetInt32() == 1, "first entity id is not 1: " + start.Text );
			var brushId = AddBox( mcp, 200, 200, 0, 264, 232, 128 );
			var door = Call( mcp, "map_entity_place", JsonNode.Parse( $"{{\"class\":\"func_door\",\"brushIds\":[\"{brushId}\"],\"keyvalues\":{{\"speed\":\"100\",\"wait\":\"4\"}},\"origin\":[232,216,64]}}" )! );
			Require( !door.IsError, "door failed: " + door.Text );
			var doorJson = Json( door.Text );
			Require( doorJson.GetProperty( "kind" ).GetString() == "Brush", "door kind is not Brush: " + door.Text );
			Require( mcp.FindEntity( doorJson.GetProperty( "entityId" ).GetInt32() )!.Brushes.Count == 1, "door holds no brush" );
			var unknown = Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"npc_does_not_exist\",\"position\":[0,0,0]}" )! );
			Require( unknown.IsError, "unknown class should fail" );
			Require( unknown.Text.Contains( "UnknownEntityClass", StringComparison.Ordinal ), "unknown class error does not name UnknownEntityClass: " + unknown.Text );
			Require( mcp.EntityCount == 2, "failed place left an entity behind" );
		} );

		Check( "entity edit sets keyvalues origin and angles", () =>
		{
			var mcp = NewMcp( "mcp-map-entity-edit" );
			var placed = Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"logic_relay\",\"position\":[0,0,0],\"keyvalues\":{\"targetname\":\"relay1\"}}" )! );
			Require( !placed.IsError, "relay place failed: " + placed.Text );
			var id = Json( placed.Text ).GetProperty( "entityId" ).GetInt32();
			var edited = Call( mcp, "map_entity_edit", JsonNode.Parse( $"{{\"entityId\":{id},\"keyvalues\":{{\"targetname\":\"relay2\"}},\"origin\":[10,20,30],\"angles\":[0,90,0]}}" )! );
			Require( !edited.IsError, "entity edit failed: " + edited.Text );
			var entity = mcp.FindEntity( id )!;
			Require( entity.TargetName == "relay2", "targetname did not change" );
			Require( Math.Abs( entity.Origin.x - 10f ) < 1e-6 && Math.Abs( entity.Origin.z - 30f ) < 1e-6, "origin did not change" );
			Require( Math.Abs( entity.Angles.y - 90f ) < 1e-6, "angles did not change" );
			var badKey = Call( mcp, "map_entity_edit", JsonNode.Parse( $"{{\"entityId\":{id},\"keyvalues\":{{\"bogus_key\":\"1\"}}}}" )! );
			Require( badKey.IsError, "unknown keyvalue should fail" );
			var missing = Call( mcp, "map_entity_edit", JsonNode.Parse( "{\"entityId\":99}" )! );
			Require( missing.IsError && missing.Text.Contains( "entity-not-found", StringComparison.Ordinal ), "missing entity error is not named: " + missing.Text );
		} );

		Check( "io add resolves the target and refuses a ghost", () =>
		{
			var mcp = NewMcp( "mcp-map-io" );
			Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"logic_relay\",\"position\":[0,0,0],\"keyvalues\":{\"targetname\":\"relay1\"}}" )! );
			var brushId = AddBox( mcp, 200, 200, 0, 264, 232, 128 );
			Call( mcp, "map_entity_place", JsonNode.Parse( $"{{\"class\":\"func_door\",\"brushIds\":[\"{brushId}\"],\"keyvalues\":{{\"targetname\":\"door1\"}}}}" )! );
			var added = Call( mcp, "map_io_add", JsonNode.Parse( "{\"source\":\"relay1\",\"event\":\"OnTrigger\",\"target\":\"door1\",\"input\":\"Open\"}" )! );
			Require( !added.IsError, "io add failed: " + added.Text );
			Require( Json( added.Text ).GetProperty( "outputs" ).GetInt32() == 1, "io row was not recorded: " + added.Text );
			Require( mcp.IoCount == 1, "session io count is not 1" );
			var ghost = Call( mcp, "map_io_add", JsonNode.Parse( "{\"source\":\"relay1\",\"event\":\"OnTrigger\",\"target\":\"ghost\",\"input\":\"Open\"}" )! );
			Require( ghost.IsError && ghost.Text.Contains( "io-target-not-found", StringComparison.Ordinal ), "ghost target error is not named: " + ghost.Text );
			var special = Call( mcp, "map_io_add", JsonNode.Parse( "{\"source\":\"relay1\",\"event\":\"OnTrigger\",\"target\":\"!self\",\"input\":\"Toggle\"}" )! );
			Require( !special.IsError, "engine special target !self should resolve: " + special.Text );
		} );

		Check( "group define link editMember unlink", () =>
		{
			var mcp = NewMcp( "mcp-map-group" );
			var a = AddBox( mcp, 0, 0, 0, 64, 64, 64 );
			var b = AddBox( mcp, 128, 0, 0, 192, 64, 64 );
			var defined = Call( mcp, "map_group", JsonNode.Parse( $"{{\"op\":\"define\",\"name\":\"pillar-pair\",\"brushIds\":[\"{a}\",\"{b}\"]}}" )! );
			Require( !defined.IsError, "group define failed: " + defined.Text );
			var definitionId = Json( defined.Text ).GetProperty( "definitionId" ).GetString()!;
			Require( mcp.GroupDefinitions.Count == 1, "definition was not recorded" );
			var linked = Call( mcp, "map_group", JsonNode.Parse( $"{{\"op\":\"link\",\"definitionId\":\"{definitionId}\",\"offset\":[0,256,0]}}" )! );
			Require( !linked.IsError, "group link failed: " + linked.Text );
			var instanceId = Json( linked.Text ).GetProperty( "instanceId" ).GetString()!;
			Require( mcp.GroupInstances.Count == 1, "instance was not recorded" );
			var edited = Call( mcp, "map_group", JsonNode.Parse( $"{{\"op\":\"editMember\",\"definitionId\":\"{definitionId}\",\"memberId\":\"{a}\",\"edit\":{{\"kind\":\"move\",\"delta\":[0,0,16]}}}}" )! );
			Require( !edited.IsError, "group editMember failed: " + edited.Text );
			Require( Json( edited.Text ).GetProperty( "affectedInstances" ).GetInt32() == 1, "edit did not reach the linked copy: " + edited.Text );
			var unlinked = Call( mcp, "map_group", JsonNode.Parse( $"{{\"op\":\"unlink\",\"instanceId\":\"{instanceId}\"}}" )! );
			Require( !unlinked.IsError, "group unlink failed: " + unlinked.Text );
			Require( mcp.GroupInstances.Count == 0, "unlink left the instance linked" );
			Require( Json( unlinked.Text ).GetProperty( "members" ).GetInt32() == 2, "unlinked group lost members: " + unlinked.Text );
		} );

		Check( "visgroup define assign setVisible list", () =>
		{
			var mcp = NewMcp( "mcp-map-visgroup" );
			var id = AddBox( mcp, 0, 0, 0, 64, 64, 64 );
			var defined = Call( mcp, "map_visgroup", JsonNode.Parse( "{\"op\":\"define\",\"name\":\"Structure\",\"color\":\"255 0 0\"}" )! );
			Require( !defined.IsError, "visgroup define failed: " + defined.Text );
			var assigned = Call( mcp, "map_visgroup", JsonNode.Parse( $"{{\"op\":\"assign\",\"name\":\"Structure\",\"brushIds\":[\"{id}\"]}}" )! );
			Require( !assigned.IsError, "visgroup assign failed: " + assigned.Text );
			Require( mcp.BrushVisGroup( id ) == "Structure", "brush visgroup was not recorded" );
			var hidden = Call( mcp, "map_visgroup", JsonNode.Parse( "{\"op\":\"setVisible\",\"name\":\"Structure\",\"visible\":false}" )! );
			Require( !hidden.IsError, "visgroup setVisible failed: " + hidden.Text );
			Require( !mcp.VisGroups["Structure"].Visible, "visgroup is still visible" );
			var listed = Call( mcp, "map_visgroup", JsonNode.Parse( "{\"op\":\"list\"}" )! );
			Require( Json( listed.Text ).GetProperty( "visgroups" ).GetArrayLength() == 1, "visgroup list is wrong: " + listed.Text );
			var missing = Call( mcp, "map_visgroup", JsonNode.Parse( "{\"op\":\"setVisible\",\"name\":\"Nope\",\"visible\":false}" )! );
			Require( missing.IsError && missing.Text.Contains( "visgroup-not-found", StringComparison.Ordinal ), "missing visgroup error is not named: " + missing.Text );
		} );

		Check( "import export round-trips the sealed room as VMF and Valve 220", () =>
		{
			var mcp = NewMcp( "mcp-map-roundtrip" );
			BuildSealedRoom( mcp );
			var exported = Call( mcp, "map_import_export", JsonNode.Parse( "{\"op\":\"export\",\"format\":\"vmf\"}" )! );
			Require( !exported.IsError, "vmf export failed: " + exported.Text );
			var text = Json( exported.Text ).GetProperty( "text" ).GetString()!;
			Require( text.Contains( "world", StringComparison.Ordinal ), "vmf text has no world block" );
			Require( text.Contains( "info_player_start", StringComparison.Ordinal ), "vmf text lost the player start" );
			var fresh = NewMcp( "mcp-map-roundtrip-import" );
			var imported = Call( fresh, "map_import_export", JsonNode.Parse( JsonSerializer.Serialize( new { op = "import", format = "vmf", text } ) )! );
			Require( !imported.IsError, "vmf import failed: " + imported.Text );
			var importJson = Json( imported.Text );
			Require( importJson.GetProperty( "brushes" ).GetInt32() == 6, "imported brush count is not 6: " + imported.Text );
			Require( importJson.GetProperty( "entities" ).GetInt32() == 2, "imported entity count is not 2: " + imported.Text );
			Require( importJson.GetProperty( "warnings" ).GetArrayLength() == 0, "import reported warnings: " + imported.Text );
			var mapText = Call( mcp, "map_import_export", JsonNode.Parse( "{\"op\":\"export\",\"format\":\"map\"}" )! );
			Require( !mapText.IsError, "map export failed: " + mapText.Text );
			Require( Json( mapText.Text ).GetProperty( "text" ).GetString()!.Contains( "Half-Life 2", StringComparison.Ordinal ), "Valve 220 text lacks its header" );
			var bad = Call( fresh, "map_import_export", JsonNode.Parse( "{\"op\":\"import\",\"format\":\"vmf\",\"text\":\"this is not vmf {{{\"}" )! );
			Require( bad.IsError && bad.Text.Contains( "import-parse-failed", StringComparison.Ordinal ), "garbage import error is not named: " + bad.Text );
			Require( fresh.BrushCount == 6, "failed import clobbered the session" );
		} );

		Check( "compile quick succeeds on the sealed room", () =>
		{
			var mcp = NewMcp( "mcp-map-compile" );
			BuildSealedRoom( mcp );
			var res = Call( mcp, "map_compile", JsonNode.Parse( "{\"preset\":\"quick\"}" )! );
			Require( !res.IsError, "compile failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "succeeded" ).GetBoolean(), "quick compile did not succeed: " + res.Text );
			Require( json.GetProperty( "issues" ).GetArrayLength() == 0, "quick compile reported issues: " + res.Text );
			Require( json.GetProperty( "bspLeaves" ).GetInt32() > 0, "compile produced no BSP leaves: " + res.Text );
            ThrowsArg(() => Call(mcp, "map_compile", JsonNode.Parse("{\"preset\":\"ultra\"}")!), "Unknown compile preset");
		} );

		Check( "validate is clean on the sealed room and names a duplicate targetname", () =>
		{
			var mcp = NewMcp( "mcp-map-validate" );
			BuildSealedRoom( mcp );
			var clean = Call( mcp, "map_validate", new JsonObject() );
			Require( !clean.IsError, "validate failed: " + clean.Text );
			var cleanJson = Json( clean.Text );
			Require( cleanJson.GetProperty( "errors" ).GetInt32() == 0, "sealed room has validation errors: " + clean.Text );
			Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"logic_relay\",\"position\":[0,0,0],\"keyvalues\":{\"targetname\":\"relay1\"}}" )! );
			Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"logic_relay\",\"position\":[64,0,0],\"keyvalues\":{\"targetname\":\"relay1\"}}" )! );
			var dirty = Call( mcp, "map_validate", new JsonObject() );
			Require( !dirty.IsError, "validate after duplicates failed: " + dirty.Text );
			var found = false;
			foreach ( var issue in Json( dirty.Text ).GetProperty( "issues" ).EnumerateArray() )
				if ( issue.GetProperty( "code" ).GetString() == "DUPLICATE_TARGETNAME" ) found = true;
			Require( found, "validate did not flag DUPLICATE_TARGETNAME: " + dirty.Text );
		} );

		Check( "bake lightmap reports the room mesh and writes OBJ", () =>
		{
			var mcp = NewMcp( "mcp-map-bake" );
			BuildSealedRoom( mcp );
			var res = Call( mcp, "map_bake_lightmap", JsonNode.Parse( "{\"export\":\"obj\"}" )! );
			Require( !res.IsError, "bake failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "faces" ).GetInt32() == 36, "baked face count is not 36: " + res.Text );
			Require( json.GetProperty( "vertices" ).GetInt32() > 0, "baked vertex count is 0: " + res.Text );
			var materials = json.GetProperty( "materials" ).EnumerateArray().Select( e => e.GetString() ).ToHashSet();
			Require( materials.Contains( "dev/dev_measuregeneric01" ), "baked materials lack the room material: " + res.Text );
			Require( json.GetProperty( "objWritten" ).GetBoolean(), "OBJ was not written: " + res.Text );
			var objPath = json.GetProperty( "objPath" ).GetString()!;
			Require( File.Exists( objPath ) && new FileInfo( objPath ).Length > 0, "OBJ file is missing or empty: " + objPath );
			Require( File.Exists( Path.ChangeExtension( objPath, ".mtl" ) ), "MTL file is missing" );
		} );

		Check( "navmesh reports required and generation not-implemented", () =>
		{
			var mcp = NewMcp( "mcp-map-navmesh" );
			BuildSealedRoom( mcp );
			Call( mcp, "map_entity_place", JsonNode.Parse( "{\"class\":\"info_node\",\"position\":[256,256,32]}" )! );
			var res = Call( mcp, "map_navmesh", new JsonObject() );
			Require( !res.IsError, "navmesh check failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "navmeshRequired" ).GetBoolean(), "info_node should require a navmesh: " + res.Text );
			Require( !json.GetProperty( "passed" ).GetBoolean(), "navmesh check should not pass with no navmesh: " + res.Text );
			var found = false;
			foreach ( var issue in json.GetProperty( "issues" ).EnumerateArray() )
				if ( issue.GetProperty( "code" ).GetString() == "NAVMESH_MISSING" ) found = true;
			Require( found, "navmesh check did not report NAVMESH_MISSING: " + res.Text );
			Require( json.GetProperty( "generation" ).GetProperty( "status" ).GetString() == "not-implemented", "navmesh generation must be honestly not-implemented: " + res.Text );
			var empty = NewMcp( "mcp-map-navmesh-empty" );
			var emptyRes = Call( empty, "map_navmesh", new JsonObject() );
			Require( !Json( emptyRes.Text ).GetProperty( "navmeshRequired" ).GetBoolean(), "empty map should not require a navmesh" );
		} );

		Check( "skybox sets the sky and reports cubemap not-implemented", () =>
		{
			var mcp = NewMcp( "mcp-map-skybox" );
			BuildSealedRoom( mcp );
			var res = Call( mcp, "map_skybox_cubemap", JsonNode.Parse( "{\"skyName\":\"sky_day01\",\"placeSkyCamera\":true,\"skyCameraOrigin\":[0,0,1024],\"skyCameraScale\":16}" )! );
			Require( !res.IsError, "skybox failed: " + res.Text );
			var json = Json( res.Text );
			Require( json.GetProperty( "skyName" ).GetString() == "sky_day01", "sky name did not stick: " + res.Text );
			Require( json.GetProperty( "hasSkyCamera" ).GetBoolean(), "sky_camera was not found in the preview: " + res.Text );
			Require( json.GetProperty( "cubemap" ).GetProperty( "status" ).GetString() == "not-implemented", "cubemap capture must be honestly not-implemented: " + res.Text );
			Require( mcp.SkyName == "sky_day01", "session sky name did not stick" );
		} );

		Check( "unknown tool missing args non-object args and bad map ref are invalid params", () =>
		{
			var mcp = NewMcp( "mcp-map-invalid-params" );
			ThrowsArg( () => Call( mcp, "no_such_tool", new JsonObject() ), "Unknown map tool" );
			ThrowsArg( () => Call( mcp, "map_brush_create", new JsonObject() ), "'shape'" );
			ThrowsArg( () => Call( mcp, "map_brush_create", JsonNode.Parse( "\"not-an-object\"" )! ), "must be a JSON object" );
			ThrowsArg( () => Call( mcp, "map_validate", JsonNode.Parse( "{\"map\":\"other-map\"}" )! ), "Unknown map" );
			var clipBrush = AddBox( mcp, -64, -64, -64, 64, 64, 64 );
			ThrowsArg( () => Call( mcp, "map_brush_clip", JsonNode.Parse( $"{{\"brushId\":\"{clipBrush}\",\"a\":[0,0,0],\"b\":[0,0,0],\"viewAxis\":\"z\"}}" )! ), "clip-two-points-coincident" );
		} );

		Check( "unknown brush and entity ids are named errors, not crashes", () =>
		{
			var mcp = NewMcp( "mcp-map-not-found" );
			var brush = Call( mcp, "map_brush_edit", JsonNode.Parse( "{\"brushId\":\"brush-9\",\"op\":\"translate\",\"delta\":[1,0,0]}" )! );
			Require( brush.IsError && brush.Text.Contains( "brush-not-found", StringComparison.Ordinal ), "unknown brush error is not named: " + brush.Text );
			var disp = Call( mcp, "map_displacement_sculpt", JsonNode.Parse( "{\"displacementId\":\"disp-9\",\"mode\":\"raise\",\"position\":[0,0,0],\"radius\":16,\"strength\":1}" )! );
			Require( disp.IsError && disp.Text.Contains( "displacement-not-found", StringComparison.Ordinal ), "unknown displacement error is not named: " + disp.Text );
			Require( mcp.BrushCount == 0 && mcp.EntityCount == 0, "failed calls mutated the session" );
		} );
	}
}
