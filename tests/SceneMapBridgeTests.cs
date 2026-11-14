using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;
using Effigy.Map;
using Marionette.Desktop;

// Hammer Desktop interop: shared scene / layer model.
// Board: hammer-interop-shared-scene-layer-model. Group name 'scenemapbridge'.
//
// Fixtures live in tests/fixtures/hammer/hammer-interop-shared-scene-layer-model/
// (see that directory's README.md): a .gtscn scene that references a map and a
// layers.json with map layers + a visgroup, both in the filed-spec shapes.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and
// the final integrator adds the one SceneMapBridgeTests.Register(registry, settings);
// line. The bridge's map-side types are spec adapters (see SceneMapBridge.cs):
// the layers-model / instances workers own the canonical MapLayers / MapInstances
// types, so these tests exercise the bridge against the filed specs, the real
// UnifiedLayerStack, the real AssemblyOccurrence, and the real TextSceneDocument.
internal static class SceneMapBridgeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "scenemapbridge", "map layer round-trips through a scene entry with colour, visibility and compile flag intact", () =>
		{
			var bridge = new SceneMapBridge();
			var layer = new MapLayerData { Id = "layer-geometry", Name = "Geometry", ColorHex = "#8a5a33", Visible = true, CompileVisible = false };
			var entry = bridge.ToSceneEntry( layer );
			Assert( entry.Name == "Geometry", $"entry name must be Geometry, got '{entry.Name}'" );
			Assert( entry.Group == SceneMapBridge.MapGroupName, $"map layer entry must sit in group '{SceneMapBridge.MapGroupName}', got '{entry.Group}'" );
			Assert( entry.Enabled, "a visible layer must make an enabled entry" );
			var back = bridge.ToMapLayer( entry );
			Assert( back.Name == "Geometry" && back.Id == "Geometry", $"round-trip name/id must be Geometry, got '{back.Name}'/'{back.Id}'" );
			Assert( back.ColorHex == "#8a5a33", $"round-trip colour must be #8a5a33, got '{back.ColorHex}'" );
			Assert( back.Visible, "round-trip layer must stay visible" );
			Assert( !back.CompileVisible, "round-trip layer must stay compile-invisible" );
		} );

		registry.Add( "scenemapbridge", "hidden layer round-trips: visibility and compile flag are independent", () =>
		{
			var bridge = new SceneMapBridge();
			var layer = new MapLayerData { Id = "layer-hidden", Name = "Hidden", ColorHex = "#2e6fb0", Visible = false, CompileVisible = true };
			var back = bridge.ToMapLayer( bridge.ToSceneEntry( layer ) );
			Assert( !back.Visible, "round-trip layer must stay hidden" );
			Assert( back.CompileVisible, "a hidden layer can still be compile-visible" );
			Assert( back.ColorHex == "#2e6fb0", $"round-trip colour must be #2e6fb0, got '{back.ColorHex}'" );
		} );

		registry.Add( "scenemapbridge", "scene entry with no bridge history converts with documented defaults", () =>
		{
			var bridge = new SceneMapBridge();
			var entry = new UnifiedLayerEntry { Kind = UnifiedLayerKind.Paint, Name = "Bare", Group = SceneMapBridge.MapGroupName, Enabled = false };
			var layer = bridge.ToMapLayer( entry );
			Assert( layer.ColorHex == SceneMapBridge.DefaultColorHex, $"default colour must be {SceneMapBridge.DefaultColorHex}, got '{layer.ColorHex}'" );
			Assert( !layer.Visible, "a disabled entry must convert to a hidden layer" );
			Assert( !layer.CompileVisible, "with no history the compile flag defaults to the visibility" );
			Assert( !bridge.TryGetRecordedKind( "Bare", out _, out _, out _ ), "an entry with no bridge history must report no recorded kind" );
		} );

		registry.Add( "scenemapbridge", "visgroup round-trips through a scene entry with members intact", () =>
		{
			var bridge = new SceneMapBridge();
			var vis = new MapVisGroupData
			{
				Id = "vis-func-detail", Name = "func_detail", ColorHex = "#e0a030", Visible = true, CompileVisible = true,
				MemberObjectIds = new List<string> { "brush-001", "brush-002", "entity-light-1" },
			};
			var entry = bridge.ToSceneEntry( vis );
			Assert( entry.Group == SceneMapBridge.VisGroupName, $"visgroup entry must sit in group '{SceneMapBridge.VisGroupName}', got '{entry.Group}'" );
			Assert( entry.Enabled, "a visible visgroup must make an enabled entry" );
			Assert( bridge.TryGetRecordedKind( "func_detail", out var kind, out _, out _ ) && kind == "visgroup",
				"the bridge must record a visgroup as kind visgroup, not as a layer" );
			var back = bridge.ToMapVisGroup( entry );
			Assert( back.ColorHex == "#e0a030" && back.Visible && back.CompileVisible, "visgroup colour/visibility/compile flag must round-trip" );
			Assert( back.MemberObjectIds.Count == 3 && back.MemberObjectIds[2] == "entity-light-1",
				$"visgroup members must round-trip, got [{string.Join( ",", back.MemberObjectIds )}]" );
		} );

		registry.Add( "scenemapbridge", "adding converted entries to a stack registers the group and rejects a duplicate name", () =>
		{
			var bridge = new SceneMapBridge();
			var stack = new UnifiedLayerStack();
			SceneMapBridge.AddToStack( stack, bridge.ToSceneEntry( new MapLayerData { Name = "Geometry", ColorHex = "#8a5a33" } ) );
			SceneMapBridge.AddToStack( stack, bridge.ToSceneEntry( new MapVisGroupData { Name = "func_detail", ColorHex = "#e0a030" } ) );
			Assert( stack.Count == 2, $"stack must hold 2 entries, got {stack.Count}" );
			Assert( stack.Groups.Contains( SceneMapBridge.MapGroupName ) && stack.Groups.Contains( SceneMapBridge.VisGroupName ),
				$"stack groups must be Map + VisGroup, got [{string.Join( ",", stack.Groups )}]" );
			Assert( stack.Find( "Geometry" ) is not null && stack.Find( "func_detail" ) is not null, "both converted entries must be findable by name" );
			var duplicate = Throws<ArgumentException>( () =>
				SceneMapBridge.AddToStack( stack, bridge.ToSceneEntry( new MapLayerData { Name = "Geometry", ColorHex = "#ffffff" } ) ) );
			Assert( duplicate.Message.Contains( "duplicate-name" ), $"duplicate entry must fail with duplicate-name, got '{duplicate.Message}'" );
		} );

		registry.Add( "scenemapbridge", "assembly occurrence becomes a map instance with prefix and exact transform", () =>
		{
			var occurrence = new AssemblyOccurrence
			{
				Id = "occ-1", DefinitionId = "def-room", Name = "Room", Visible = true,
				Transform = new AssemblyTransform
				{
					X = new[] { 0f, 1f, 0f }, Y = new[] { -1f, 0f, 0f }, Z = new[] { 0f, 0f, 1f },
					Origin = new[] { 10f, 20f, 30f },
				},
			};
			var instance = SceneMapBridge.OccurrenceToInstance( occurrence, "maps/room01.gmap" );
			Assert( instance.Id == "occ-1" && instance.Name == "Room", "instance must keep the occurrence id and name" );
			Assert( instance.SubMapPath == "maps/room01.gmap", $"instance sub-map must be maps/room01.gmap, got '{instance.SubMapPath}'" );
			Assert( instance.NamePrefix == "Room_", $"instance prefix must be Room_, got '{instance.NamePrefix}'" );
			AssertVec3( instance.BasisX, new[] { 0f, 1f, 0f }, "instance basis X" );
			AssertVec3( instance.BasisY, new[] { -1f, 0f, 0f }, "instance basis Y" );
			AssertVec3( instance.Origin, new[] { 10f, 20f, 30f }, "instance origin" );
		} );

		registry.Add( "scenemapbridge", "map instance becomes an assembly occurrence; occurrence-instance-occurrence is exact", () =>
		{
			var occurrence = new AssemblyOccurrence
			{
				Id = "occ-2", DefinitionId = "def-room", Name = "Room", Visible = false,
				Transform = new AssemblyTransform { Origin = new[] { 5f, 6f, 7f } },
			};
			var instance = SceneMapBridge.OccurrenceToInstance( occurrence, "maps/room01.gmap" );
			var back = SceneMapBridge.InstanceToOccurrence( instance, "def-room" );
			Assert( back.Id == "occ-2" && back.DefinitionId == "def-room" && back.Name == "Room",
				"occurrence id, definition id and name must survive the round-trip" );
			Assert( !back.Visible, "occurrence visibility must survive the round-trip" );
			AssertVec3( back.Transform.Origin, new[] { 5f, 6f, 7f }, "occurrence origin" );
			AssertVec3( back.Transform.X, new[] { 1f, 0f, 0f }, "occurrence basis X" );
		} );

		registry.Add( "scenemapbridge", "target fix-up prefixes copies, never renames the originals, and never double-prefixes", () =>
		{
			var originals = new List<string> { "light_main", "door_1", "Room_door_2" };
			var fixedUp = SceneMapBridge.FixUpTargetNames( originals, "Room_" );
			Assert( fixedUp.Count == 3 && fixedUp[0] == "Room_light_main" && fixedUp[1] == "Room_door_1" && fixedUp[2] == "Room_door_2",
				$"fix-up must prefix each target once, got [{string.Join( ",", fixedUp )}]" );
			Assert( originals[0] == "light_main" && originals[1] == "door_1",
				"the original target names must be untouched by fix-up" );
			Assert( SceneMapBridge.ApplyNamePrefix( "Room_light_main", "Room_" ) == "Room_light_main",
				"applying the same prefix twice must not double it" );
			Assert( SceneMapBridge.ApplyNamePrefix( "light_main", "" ) == "light_main",
				"an empty prefix must be a no-op" );
		} );

		registry.Add( "scenemapbridge", "occurrence placed as a map instance resolves with prefixed targets", () =>
		{
			var occurrence = new AssemblyOccurrence { Id = "occ-3", DefinitionId = "def-room", Name = "Room" };
			var resolved = SceneMapBridge.ResolveOccurrence( occurrence, "maps/room01.gmap",
				path => path == "maps/room01.gmap", new[] { "light_main", "door_1" } );
			Assert( resolved.Exists, $"instance of an existing sub-map must resolve, got '{resolved.Reason}'" );
			Assert( resolved.Instance.NamePrefix == "Room_", $"resolved instance prefix must be Room_, got '{resolved.Instance.NamePrefix}'" );
			Assert( resolved.ResolvedTargetNames.Count == 2
				&& resolved.ResolvedTargetNames[0] == "Room_light_main"
				&& resolved.ResolvedTargetNames[1] == "Room_door_1",
				$"resolved targets must be prefixed, got [{string.Join( ",", resolved.ResolvedTargetNames )}]" );
		} );

		registry.Add( "scenemapbridge", "instance of a missing sub-map does not resolve and names the path", () =>
		{
			var instance = new MapInstanceData { Id = "inst-1", Name = "Room", SubMapPath = "maps/missing.gmap", NamePrefix = "Room_" };
			var resolved = SceneMapBridge.ResolveInstance( instance, path => false, new[] { "light_main" } );
			Assert( !resolved.Exists, "an instance of a missing sub-map must not resolve" );
			Assert( resolved.Reason.Contains( "missing-sub-map" ) && resolved.Reason.Contains( "maps/missing.gmap" ),
				$"the reason must name the missing path, got '{resolved.Reason}'" );
			Assert( resolved.ResolvedTargetNames.Count == 0, "an unresolved instance must resolve no targets" );
		} );

		registry.Add( "scenemapbridge", "map reference lines round-trip through the real text scene document", () =>
		{
			var reference = new MapReference
			{
				AssetId = "room01", Name = "Room 01", Path = "maps/room01.gmap", LayerName = "Map", NodeId = "room01-node",
				Position = new[] { 10f, 20f, 30f }, RotationDegrees = new[] { 0f, 0f, 90f }, Scale = new[] { 2f, 2f, 2f },
			};
			var lines = SceneMapBridge.ToGtscnLines( reference );
			Assert( lines.Length == 3, $"a placed reference must emit asset + node + xform lines, got {lines.Length}" );
			Assert( lines[0] == "asset\troom01\tmap\tmaps/room01.gmap", $"asset line must be canonical, got '{lines[0]}'" );
			var text = "gtscn 1\nlayer\tMap\tpooh\tmapping\n" + string.Join( "\n", lines ) + "\n";
			var doc = TextSceneDocument.Parse( text );
			Assert( doc.Assets.Count == 1 && doc.Assets[0].Kind == "map" && doc.Assets[0].Path == "maps/room01.gmap",
				"the text scene must hold the map asset with kind map" );
			Assert( doc.Nodes.Count == 1 && doc.Nodes[0].Name == "room01" && doc.Nodes[0].Layer == "Map",
				"the placing node must be named for the asset and sit on the Map layer" );
			Assert( doc.Transforms.Count == 1, "the placing node must carry its xform" );
			AssertVec3( doc.Transforms[0].Position, new[] { 10f, 20f, 30f }, "scene xform position" );
			Assert( doc.ToText() == text, "the text scene must re-emit byte-identical canonical text" );
			var back = SceneMapBridge.ParseGtscnMapReferences( doc.ToText() );
			Assert( back.Count == 1, $"the bridge must find exactly one map reference, got {back.Count}" );
			Assert( back[0].AssetId == "room01" && back[0].Path == "maps/room01.gmap" && back[0].NodeId == "room01-node" && back[0].LayerName == "Map",
				"the parsed reference must carry asset id, path, node id and layer" );
			AssertVec3( back[0].Position, new[] { 10f, 20f, 30f }, "parsed reference position" );
			AssertVec3( back[0].RotationDegrees, new[] { 0f, 0f, 90f }, "parsed reference rotation" );
			AssertVec3( back[0].Scale, new[] { 2f, 2f, 2f }, "parsed reference scale" );
			var unplaced = SceneMapBridge.ParseGtscnMapReferences( "asset\troom02\tmap\tmaps/room02.gmap\n" );
			Assert( unplaced.Count == 1 && unplaced[0].AssetId == "room02" && unplaced[0].NodeId == "",
				"an asset-only reference must parse without a header or a node" );
		} );

		registry.Add( "scenemapbridge", "fixture scene and layers convert end to end", () =>
		{
			var dir = FixtureDir();
			var sceneText = File.ReadAllText( Path.Combine( dir, "scene-with-map-ref.gtscn" ) );
			var doc = TextSceneDocument.Parse( sceneText );
			var refs = SceneMapBridge.ParseGtscnMapReferences( sceneText );
			Assert( refs.Count == 1 && refs[0].AssetId == "room01" && refs[0].Path == "maps/room01.gmap",
				"the fixture scene must reference maps/room01.gmap" );
			Assert( refs[0].NodeId == "room01-node" && refs[0].LayerName == "Map",
				"the fixture reference must be placed on the Map layer" );
			Assert( doc.ToText() == sceneText, "the fixture scene must already be canonical text" );

			var bridge = new SceneMapBridge();
			var stack = new UnifiedLayerStack();
			using var json = JsonDocument.Parse( File.ReadAllText( Path.Combine( dir, "layers.json" ) ) );
			foreach ( var element in json.RootElement.GetProperty( "layers" ).EnumerateArray() )
			{
				var layer = new MapLayerData
				{
					Id = element.GetProperty( "id" ).GetString(),
					Name = element.GetProperty( "name" ).GetString(),
					ColorHex = element.GetProperty( "color" ).GetString(),
					Visible = element.GetProperty( "visible" ).GetBoolean(),
					CompileVisible = element.GetProperty( "compileVisible" ).GetBoolean(),
				};
				var back = bridge.ToMapLayer( bridge.ToSceneEntry( layer ) );
				Assert( back.ColorHex == layer.ColorHex && back.Visible == layer.Visible && back.CompileVisible == layer.CompileVisible,
					$"fixture layer '{layer.Name}' must round-trip exactly" );
				SceneMapBridge.AddToStack( stack, bridge.ToSceneEntry( layer ) );
			}
			foreach ( var element in json.RootElement.GetProperty( "visgroups" ).EnumerateArray() )
			{
				var vis = new MapVisGroupData
				{
					Id = element.GetProperty( "id" ).GetString(),
					Name = element.GetProperty( "name" ).GetString(),
					ColorHex = element.GetProperty( "color" ).GetString(),
					Visible = element.GetProperty( "visible" ).GetBoolean(),
					CompileVisible = element.GetProperty( "compileVisible" ).GetBoolean(),
					MemberObjectIds = element.GetProperty( "members" ).EnumerateArray().Select( m => m.GetString() ).ToList(),
				};
				var back = bridge.ToMapVisGroup( bridge.ToSceneEntry( vis ) );
				Assert( back.MemberObjectIds.Count == vis.MemberObjectIds.Count && back.ColorHex == vis.ColorHex,
					$"fixture visgroup '{vis.Name}' must round-trip with its members" );
				SceneMapBridge.AddToStack( stack, bridge.ToSceneEntry( vis ) );
			}
			Assert( stack.Count == 3, $"fixture stack must hold 2 layers + 1 visgroup, got {stack.Count}" );
			Assert( stack.Find( "Detail" ) is not null && !stack.Find( "Detail" ).Enabled,
				"the hidden fixture layer must land disabled in the stack" );
		} );

		registry.Add( "scenemapbridge", "invalid inputs fail with named reasons, never silently", () =>
		{
			var noName = Throws<ArgumentException>( () =>
				new SceneMapBridge().ToSceneEntry( new MapLayerData { Name = "", ColorHex = "#ffffff" } ) );
			Assert( noName.Message.Contains( "missing-name" ), $"empty layer name must fail with missing-name, got '{noName.Message}'" );
			var badColour = Throws<ArgumentException>( () =>
				new SceneMapBridge().ToSceneEntry( new MapLayerData { Name = "Geometry", ColorHex = "red" } ) );
			Assert( badColour.Message.Contains( "bad-colour" ), $"a non-hex colour must fail with bad-colour, got '{badColour.Message}'" );
			var noPath = Throws<ArgumentException>( () =>
				SceneMapBridge.OccurrenceToInstance( new AssemblyOccurrence { Name = "Room" }, "" ) );
			Assert( noPath.Message.Contains( "missing-path" ), $"an empty sub-map path must fail with missing-path, got '{noPath.Message}'" );
			var tabInField = Throws<FormatException>( () =>
				SceneMapBridge.ToGtscnLines( new MapReference { AssetId = "room01", Name = "Room", Path = "maps/ro\tom.gmap" } ) );
			Assert( tabInField.Message.Contains( "tabs" ), $"a tab in a .gtscn field must be rejected, got '{tabInField.Message}'" );
			var issues = SceneMapBridge.ValidateLayer( new MapLayerData { Id = "", Name = "", ColorHex = "#12" } );
			Assert( issues.Any( i => i.StartsWith( "missing-name" ) ) && issues.Any( i => i.StartsWith( "bad-colour" ) ) && issues.Any( i => i.StartsWith( "missing-id" ) ),
				$"ValidateLayer must name every defect, got [{string.Join( "; ", issues )}]" );
			var prefixIssues = SceneMapBridge.ValidateInstance( new MapInstanceData { Id = "i", Name = "Room", SubMapPath = "maps/r.gmap", NamePrefix = "Room " } );
			Assert( prefixIssues.Any( i => i.StartsWith( "bad-prefix" ) ),
				$"a prefix with whitespace must fail validation, got [{string.Join( "; ", prefixIssues )}]" );
			Assert( SceneMapBridge.ValidateLayer( new MapLayerData { Id = "l", Name = "Ok", ColorHex = "#abcdef" } ).Count == 0,
				"a well-formed layer must validate clean" );
		} );
	}

	static string FixtureDir()
	{
		var dir = new DirectoryInfo( AppContext.BaseDirectory );
		while ( dir is not null )
		{
			var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer",
				"hammer-interop-shared-scene-layer-model" );
			if ( Directory.Exists( candidate ) ) return candidate;
			dir = dir.Parent;
		}
		throw new InvalidOperationException( "Cannot find the hammer-interop-shared-scene-layer-model fixture directory." );
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( "assert failed: " + message );
	}

	static void AssertVec3( float[] actual, float[] expected, string what )
	{
		Assert( actual is not null && actual.Length == 3, $"{what} must be a 3-vector" );
		for ( var i = 0; i < 3; i++ )
			Assert( Math.Abs( actual[i] - expected[i] ) < 1e-6f,
				$"{what}[{i}] must be {expected[i]}, got {actual[i]}" );
	}

	static T Throws<T>( Action action ) where T : Exception
	{
		try { action(); }
		catch ( T ex ) { return ex; }
		throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
	}
}
