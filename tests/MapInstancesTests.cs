#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

/// <summary>
/// Sprinkles / prefabs / func_instance — native, no Hammer (see
/// kernel/Effigy/Map/MapInstances.cs, board
/// hammer-sprinkles-prefabs-instances).
///
/// The acceptance numbers live in the fixtures
/// tests/fixtures/hammer/hammer-sprinkles-prefabs-instances/instances.json
/// (barrel-cluster preset, door-frame prefab, room sub-map "sm-1" with one
/// instance "mi-1" + one IO proxy "iop-1") and instances-roundtrip.json
/// (a second store the round-trip check loads, re-serialises and reloads);
/// the rest build the same shapes synthetically so a missing fixture can
/// never hide a fix-up failure.
///
/// DEPENDENCY NOTE. MapDocument (hammer-map-document-model) has not landed,
/// so sub-maps are registered in-memory content (RegisterSubMap) bound by
/// string id + source path, and PlacePreset returns PlacedSprinkle data for
/// the MapEntities/PropPlacement adapter to seat — see the header of
/// MapInstances.cs.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// MapInstancesTests.Register(registry, settings); line.
/// </summary>
internal static class MapInstancesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mapinstances", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireReason( MapInstancesResult result, string reason, string message )
		{
			Require( !result.Ok, $"{message}: expected failure, got success" );
			Require( result.Reason == reason, $"{message}: reason '{result.Reason}' vs expected '{reason}' (message: {result.Message})" );
		}

		static InstanceMember Box( string id, string name, Vec3 min, Vec3 size, string material )
		{
			var m = new InstanceMember
			{
				Id = id,
				Kind = InstanceMemberKind.Brush,
				Name = name,
				Material = material,
			};
			float x0 = min.x, y0 = min.y, z0 = min.z;
			float x1 = min.x + size.x, y1 = min.y + size.y, z1 = min.z + size.z;
			m.Vertices.AddRange( new[]
			{
				new Vec3( x0, y0, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x0, y1, z0 ),
				new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ),
			} );
			return m;
		}

		static InstanceMember Lamp( string id, string name, Vec3 origin, string targetName )
		{
			var m = new InstanceMember
			{
				Id = id,
				Kind = InstanceMemberKind.Entity,
				Name = name,
				Origin = origin,
			};
			m.KeyValues["classname"] = "light";
			m.KeyValues["targetname"] = targetName;
			return m;
		}

		static InstanceMember Relay( string id, string name, Vec3 origin, string targetName, string outputTarget )
		{
			var m = new InstanceMember
			{
				Id = id,
				Kind = InstanceMemberKind.Entity,
				Name = name,
				Origin = origin,
			};
			m.KeyValues["classname"] = "logic_relay";
			m.KeyValues["targetname"] = targetName;
			// EntityIo comma-form output: Target,Input,Parameter,Delay,Fires.
			m.KeyValues["OnTrigger"] = $"{outputTarget},TurnOn,,0,-1";
			return m;
		}

		/// <summary>A room sub-map: floor + wall brushes, a light, and a relay that turns the light on.</summary>
		static SubMapContent RoomSubMap( string id = "sm-1", string path = "maps/rooms/room_a.vmf" )
		{
			var sub = new SubMapContent { Id = id, SourcePath = path, Name = "room_a" };
			sub.Members.Add( Box( "m-floor", "floor", new Vec3( 0, 0, 0 ), new Vec3( 256, 256, 8 ), "dev/dev_measuregeneric01" ) );
			sub.Members.Add( Box( "m-wall", "wall", new Vec3( 0, 0, 8 ), new Vec3( 8, 256, 128 ), "dev/dev_measuregeneric01b" ) );
			sub.Members.Add( Lamp( "m-light", "light", new Vec3( 128, 128, 120 ), "room_light" ) );
			sub.Members.Add( Relay( "m-relay", "relay", new Vec3( 64, 64, 64 ), "room_relay", "room_light" ) );
			// A reference OUTWARD: the relay also names a main-map target. Fix-up must leave it alone.
			sub.Members[3].KeyValues["target"] = "main_door";
			// An engine special: never prefixed.
			sub.Members[3].KeyValues["OnTrigger2"] = "!activator,Trigger,,0,-1";
			return sub;
		}

		static MapInstanceStore RoomStore()
		{
			var store = new MapInstanceStore();
			var registered = store.RegisterSubMap( RoomSubMap() );
			if ( !registered.Ok || registered.SubMap is null ) throw new Exception( $"room fixture sub-map failed: {registered.Reason}" );
			return store;
		}

		static InstanceMember Resolved( ResolveInstanceResult result, string memberId )
		{
			var found = result.Members.FirstOrDefault( m => m.Id == memberId );
			if ( found is null ) throw new Exception( $"resolve produced no member '{memberId}'" );
			return found;
		}

		void RequireVertex( InstanceMember member, Vec3 expected, string message )
		{
			Require( member.Vertices.Any( v => v.AlmostEquals( expected ) ),
				$"{message}: no vertex at {expected} among [{string.Join( ", ", member.Vertices )}]" );
		}

		void RequireNoVertex( InstanceMember member, Vec3 unexpected, string message )
		{
			Require( !member.Vertices.Any( v => v.AlmostEquals( unexpected ) ),
				$"{message}: unexpected vertex at {unexpected} still present" );
		}

		static string FindFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-sprinkles-prefabs-instances", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			throw new Exception( $"fixture not found: tests/fixtures/hammer/hammer-sprinkles-prefabs-instances/{name}" );
		}

		static List<SprinkleItem> BarrelItems() => new()
		{
			new SprinkleItem { Model = "models/props_c17/oildrum001.mdl", Offset = new Vec3( 0, 0, 0 ) },
			new SprinkleItem { Model = "models/props_c17/oildrum001.mdl", Offset = new Vec3( 64, 0, 0 ), Angles = new Vec3( 0, 90, 0 ) },
			new SprinkleItem { Model = "models/props_junk/wood_crate001a.mdl", ClassName = "prop_physics", Offset = new Vec3( 32, 64, 0 ), Scale = 2f },
		};

		Check( "define preset stores items in order with defaults", () =>
		{
			var store = new MapInstanceStore();
			var defined = store.DefinePreset( "barrel-cluster", BarrelItems() );
			Require( defined.Ok && defined.Preset is not null, $"define failed: {defined.Reason}" );
			var p = defined.Preset!;
			Require( p.Id == "sp-1", $"preset id '{p.Id}'" );
			Require( p.Items.Count == 3, $"expected 3 items, got {p.Items.Count}" );
			Require( p.Items[0].ClassName == "prop_static", "blank classname did not default to prop_static" );
			Require( p.Items[1].Angles.AlmostEquals( new Vec3( 0, 90, 0 ) ), "item angles lost" );
			Require( p.Items[2].ClassName == "prop_physics" && p.Items[2].Scale == 2f, "item classname/scale lost" );
			Require( store.Presets.Count == 1, "preset was not stored" );
		} );

		Check( "define preset refuses bad input", () =>
		{
			var store = new MapInstanceStore();
			RequireReason( store.DefinePreset( "   ", BarrelItems() ), "mapinstances-invalid-name", "blank name" );
			RequireReason( store.DefinePreset( "empty", Array.Empty<SprinkleItem>() ), "mapinstances-empty-preset", "empty items" );
			var withNull = new List<SprinkleItem> { null! };
			RequireReason( store.DefinePreset( "null-item", withNull ), "mapinstances-empty-preset", "null item" );
			RequireReason( store.DefinePreset( "no-model", new[] { new SprinkleItem { Model = "  " } } ),
				"mapinstances-invalid-item", "blank model" );
			foreach ( float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity } )
				RequireReason( store.DefinePreset( "bad-scale", new[] { new SprinkleItem { Model = "models/x.mdl", Scale = bad } } ),
					"mapinstances-invalid-item", $"scale {bad}" );
			RequireReason( store.DefinePreset( "bad-offset", new[] { new SprinkleItem { Model = "models/x.mdl", Offset = new Vec3( float.NaN, 0, 0 ) } } ),
				"mapinstances-invalid-item", "non-finite offset" );
			Require( store.Presets.Count == 0, "refused presets were stored anyway" );
		} );

		Check( "define preset deep-copies items", () =>
		{
			var items = BarrelItems();
			var store = new MapInstanceStore();
			var defined = store.DefinePreset( "barrels", items );
			Require( defined.Ok && defined.Preset is not null, "define failed" );
			items[0].Model = "models/changed.mdl";
			items[0].Offset = new Vec3( 999, 999, 999 );
			Require( defined.Preset!.Items[0].Model == "models/props_c17/oildrum001.mdl", "caller mutation leaked into the preset" );
			Require( defined.Preset.Items[0].Offset.AlmostEquals( Vec3.Zero ), "caller offset mutation leaked" );
		} );

		Check( "place preset resolves world origins and angles with origin and yaw", () =>
		{
			var store = new MapInstanceStore();
			var defined = store.DefinePreset( "barrel-cluster", BarrelItems() );
			Require( defined.Ok && defined.Preset is not null, "define failed" );
			var placed = store.PlacePreset( defined.Preset!.Id, new Vec3( 1000, 0, 0 ), yawDegrees: 90f );
			Require( placed.Ok, $"place failed: {placed.Reason} {placed.Message}" );
			Require( placed.Placed.Count == 3, $"expected 3 placed items, got {placed.Placed.Count}" );
			// Offset (0,0,0) stays at the placement origin.
			Require( placed.Placed[0].Origin.AlmostEquals( new Vec3( 1000, 0, 0 ) ), "first barrel origin" );
			// Offset (64,0,0) yawed 90 degrees about Z lands at (0,64,0) from the origin.
			Require( placed.Placed[1].Origin.AlmostEquals( new Vec3( 1000, 64, 0 ) ), "second barrel origin was not yaw-rotated" );
			Require( placed.Placed[1].Angles.AlmostEquals( new Vec3( 0, 180, 0 ) ), "second barrel angles did not gain the placement yaw" );
			Require( placed.Placed[2].Origin.AlmostEquals( new Vec3( 936, 32, 0 ) ), "third barrel origin was not yaw-rotated" );
			Require( placed.Placed[2].Scale == 2f && placed.Placed[2].ClassName == "prop_physics", "third barrel scale/class lost" );
			RequireReason( store.PlacePreset( "sp-404", Vec3.Zero ), "mapinstances-preset-not-found", "unknown preset" );
			RequireReason( store.PlacePreset( defined.Preset.Id, new Vec3( float.NaN, 0, 0 ) ),
				"mapinstances-invalid-origin", "non-finite placement origin" );
		} );

		Check( "save prefab and instantiate copies with auto-prefix fix-up", () =>
		{
			var store = new MapInstanceStore();
			var saved = store.SavePrefab( "door-frame", new[]
			{
				Box( "m-frame", "frame", new Vec3( 0, 0, 0 ), new Vec3( 8, 128, 128 ), "dev/dev_measuregeneric01" ),
				Lamp( "m-lamp", "lamp", new Vec3( 32, 64, 96 ), "frame_lamp" ),
			}, layer: "detail" );
			Require( saved.Ok && saved.Definition is not null, $"save failed: {saved.Reason}" );
			var a = store.InstantiatePrefab( saved.Definition!.Id, Vec3.Zero );
			var b = store.InstantiatePrefab( saved.Definition.Id, new Vec3( 256, 0, 0 ) );
			Require( a.Ok && a.Instance is not null && b.Ok && b.Instance is not null, "instantiate failed" );
			Require( a.Instance!.Prefix != b.Instance!.Prefix, "two instantiations share a prefix" );
			var lampA = a.Instance.Members.First( m => m.Id == "m-lamp" );
			var lampB = b.Instance.Members.First( m => m.Id == "m-lamp" );
			Require( lampA.TargetName == a.Instance.Prefix + "frame_lamp", $"copy A targetname '{lampA.TargetName}'" );
			Require( lampB.TargetName == b.Instance.Prefix + "frame_lamp", $"copy B targetname '{lampB.TargetName}'" );
			Require( lampA.TargetName != lampB.TargetName, "two copies share a targetname" );
			Require( lampA.Origin.AlmostEquals( new Vec3( 32, 64, 96 ) ), "copy A lamp origin" );
			Require( lampB.Origin.AlmostEquals( new Vec3( 288, 64, 96 ) ), "copy B lamp origin was not offset" );
			RequireVertex( a.Instance.Members.First( m => m.Id == "m-frame" ), new Vec3( 0, 0, 0 ), "copy A frame" );
			RequireVertex( b.Instance.Members.First( m => m.Id == "m-frame" ), new Vec3( 256, 0, 0 ), "copy B frame" );
			// The definition itself was never renamed.
			Require( saved.Definition.FindMember( "m-lamp" )!.TargetName == "frame_lamp", "instantiation renamed the definition" );
			// Copies are independent: mutating one never touches the other or the definition.
			lampA.KeyValues["targetname"] = "hacked";
			Require( b.Instance.Members.First( m => m.Id == "m-lamp" ).TargetName == b.Instance.Prefix + "frame_lamp",
				"editing copy A leaked into copy B" );
			Require( saved.Definition.FindMember( "m-lamp" )!.TargetName == "frame_lamp", "editing a copy leaked into the definition" );
		} );

		Check( "instantiate with explicit prefix and surviving collision fails without renaming the original", () =>
		{
			var store = new MapInstanceStore();
			var saved = store.SavePrefab( "door-frame", new[] { Lamp( "m-lamp", "lamp", new Vec3( 32, 64, 96 ), "frame_lamp" ) } );
			Require( saved.Ok && saved.Definition is not null, "save failed" );
			var prefixed = store.InstantiatePrefab( saved.Definition!.Id, Vec3.Zero, prefix: "east_" );
			Require( prefixed.Ok && prefixed.Instance is not null, "explicit-prefix instantiate failed" );
			Require( prefixed.Instance!.Members[0].TargetName == "east_frame_lamp", "explicit prefix was not applied" );
			// The prefixed name is already taken in the main map: a named failure, not a silent rename.
			var collided = store.InstantiatePrefab( saved.Definition.Id, Vec3.Zero, prefix: "east_",
				mainMapTargetNames: new[] { "east_frame_lamp" } );
			RequireReason( collided, "mapinstances-target-collision", "surviving collision" );
			Require( collided.Instance is null, "failed instantiation returned an instance anyway" );
			Require( saved.Definition.FindMember( "m-lamp" )!.TargetName == "frame_lamp", "failed instantiation renamed the definition" );
			RequireReason( store.InstantiatePrefab( "pf-404", Vec3.Zero ), "mapinstances-prefab-not-found", "unknown prefab" );
		} );

		Check( "save prefab refuses blank name, empty members and duplicate member ids", () =>
		{
			var store = new MapInstanceStore();
			RequireReason( store.SavePrefab( "   ", new[] { Box( "m-1", "b", Vec3.Zero, new Vec3( 8, 8, 8 ), "dev/x" ) } ),
				"mapinstances-invalid-name", "blank name" );
			RequireReason( store.SavePrefab( "empty", Array.Empty<InstanceMember>() ), "mapinstances-empty-prefab", "empty members" );
			RequireReason( store.SavePrefab( "dup", new[]
			{
				Box( "m-1", "a", Vec3.Zero, new Vec3( 8, 8, 8 ), "dev/x" ),
				Box( "m-1", "b", Vec3.Zero, new Vec3( 8, 8, 8 ), "dev/x" ),
			} ), "mapinstances-duplicate-member-id", "duplicate member ids" );
			Require( store.Prefabs.Count == 0, "refused prefabs were stored" );
		} );

		Check( "instance of a room sub-map resolves with prefixed target names", () =>
		{
			var store = RoomStore();
			var placed = store.PlaceInstance( "sm-1", new Vec3( 512, 0, 0 ), prefix: "room_a_" );
			Require( placed.Ok && placed.Instance is not null, $"place failed: {placed.Reason}" );
			Require( placed.Instance!.Name == "room_a", "instance name did not default to the sub-map name" );
			Require( placed.Instance.SourcePath == "maps/rooms/room_a.vmf", "instance source path was not copied from the sub-map" );
			var resolved = store.ResolveInstance( placed.Instance.Id );
			Require( resolved.Ok, $"resolve failed: {resolved.Reason} {resolved.Message}" );
			Require( resolved.Prefix == "room_a_", $"effective prefix '{resolved.Prefix}'" );
			Require( resolved.Collisions.Count == 0, "clean resolve reported collisions" );
			var light = Resolved( resolved, "m-light" );
			var relay = Resolved( resolved, "m-relay" );
			Require( light.TargetName == "room_a_room_light", $"light targetname '{light.TargetName}'" );
			Require( relay.TargetName == "room_a_room_relay", $"relay targetname '{relay.TargetName}'" );
			// The relay's comma-form output names the light inside the sub-map: prefixed.
			Require( relay.KeyValues["OnTrigger"] == "room_a_room_light,TurnOn,,0,-1",
				$"relay output '{relay.KeyValues["OnTrigger"]}' was not fixed up" );
			// The outward reference names a main-map target: untouched.
			Require( relay.KeyValues["target"] == "main_door", $"outward reference '{relay.KeyValues["target"]}' was prefixed" );
			// The engine special is never prefixed.
			Require( relay.KeyValues["OnTrigger2"] == "!activator,Trigger,,0,-1", "engine special was prefixed" );
			Require( light.Origin.AlmostEquals( new Vec3( 640, 128, 120 ) ), "light origin was not offset" );
			RequireVertex( Resolved( resolved, "m-floor" ), new Vec3( 512, 0, 0 ), "floor at placement" );
			// The registered sub-map itself was never renamed.
			var sub = store.FindSubMap( "sm-1" )!;
			Require( sub.FindMember( "m-light" )!.TargetName == "room_light", "resolve renamed the sub-map" );
			Require( sub.FindMember( "m-relay" )!.KeyValues["OnTrigger"] == "room_light,TurnOn,,0,-1", "resolve rewrote the sub-map output" );
			// Resolve returns fresh clones: mutating the result never corrupts the sub-map.
			light.KeyValues["targetname"] = "hacked";
			var again = store.ResolveInstance( placed.Instance.Id );
			Require( Resolved( again, "m-light" ).TargetName == "room_a_room_light", "mutating a resolve leaked into the sub-map" );
			Require( store.TryResolveInstance( placed.Instance.Id, out var members, out string reason ) && reason == "",
				"TryResolve failed on a good instance" );
			Require( members.Count == 4, "TryResolve member count" );
			Require( !store.TryResolveInstance( "mi-404", out _, out reason ) && reason == "mapinstances-instance-not-found",
				"TryResolve did not report the unknown instance" );
		} );

		Check( "fix-up off passes names through and validate reports the collision", () =>
		{
			var store = RoomStore();
			var placed = store.PlaceInstance( "sm-1", new Vec3( 512, 0, 0 ), fixUp: false );
			Require( placed.Ok && placed.Instance is not null, "place failed" );
			var resolved = store.ResolveInstance( placed.Instance!.Id, mainMapTargetNames: new[] { "room_light" } );
			Require( resolved.Ok, "resolve with fix-up off failed" );
			Require( resolved.Prefix == "", "fix-up off still applied a prefix" );
			Require( Resolved( resolved, "m-light" ).TargetName == "room_light", "fix-up off renamed a target" );
			Require( resolved.Collisions.Count == 1 && resolved.Collisions[0] == "room_light",
				"fix-up off did not report the collision" );
			var issues = store.ValidateInstance( placed.Instance.Id, new[] { "room_light" } );
			Require( issues.Count == 1 && issues[0].Code == MapInstanceIssue.CodeTargetCollision,
				$"validate did not flag the collision: [{string.Join( "; ", issues )}]" );
			// Same instance with fix-up on (fresh store): no collision, no issues.
			var fixedStore = RoomStore();
			var fixedPlaced = fixedStore.PlaceInstance( "sm-1", new Vec3( 512, 0, 0 ), prefix: "room_a_" );
			Require( fixedPlaced.Ok && fixedPlaced.Instance is not null, "fix-up place failed" );
			var fixedResolved = fixedStore.ResolveInstance( fixedPlaced.Instance!.Id, new[] { "room_light" } );
			Require( fixedResolved.Collisions.Count == 0, "prefixed resolve still collided" );
			Require( fixedStore.ValidateInstance( fixedPlaced.Instance.Id, new[] { "room_light" } ).Count == 0,
				"clean instance reported issues" );
		} );

		Check( "resolve with auto prefix uses the instance id", () =>
		{
			var store = RoomStore();
			var placed = store.PlaceInstance( "sm-1", Vec3.Zero );
			Require( placed.Ok && placed.Instance is not null, "place failed" );
			var resolved = store.ResolveInstance( placed.Instance!.Id );
			Require( resolved.Prefix == placed.Instance.Id + "_", $"auto prefix '{resolved.Prefix}'" );
			Require( Resolved( resolved, "m-light" ).TargetName == placed.Instance.Id + "_room_light", "auto prefix not applied" );
		} );

		Check( "create instance from selection rebases and resolves back to the original", () =>
		{
			var store = new MapInstanceStore();
			var selection = new[]
			{
				Box( "m-floor", "floor", new Vec3( 100, 0, 0 ), new Vec3( 256, 256, 8 ), "dev/dev_measuregeneric01" ),
				Lamp( "m-light", "light", new Vec3( 228, 128, 120 ), "room_light" ),
			};
			var created = store.CreateInstanceFromSelection( "room_b", selection, new Vec3( 100, 0, 0 ),
				sourcePath: "maps/rooms/room_b.vmf", prefix: "room_b_" );
			Require( created.Ok && created.Instance is not null && created.SubMap is not null,
				$"create failed: {created.Reason} {created.Message}" );
			// The selection was rebased to sub-map-local: floor starts at the local origin.
			RequireVertex( created.SubMap!.Members.First( m => m.Id == "m-floor" ), new Vec3( 0, 0, 0 ),
				"sub-map floor was not rebased to local coordinates" );
			Require( created.SubMap.Members.First( m => m.Id == "m-light" ).Origin.AlmostEquals( new Vec3( 128, 128, 120 ) ),
				"sub-map light was not rebased" );
			// ...and the instance placed back at the origin resolves to the original selection exactly.
			var resolved = store.ResolveInstance( created.Instance!.Id );
			Require( resolved.Ok, "resolve of the created instance failed" );
			RequireVertex( Resolved( resolved, "m-floor" ), new Vec3( 100, 0, 0 ), "resolved floor" );
			Require( Resolved( resolved, "m-light" ).Origin.AlmostEquals( new Vec3( 228, 128, 120 ) ), "resolved light" );
			Require( Resolved( resolved, "m-light" ).TargetName == "room_b_room_light", "resolved light targetname" );
			// The caller's selection was never renamed or moved.
			Require( selection[1].TargetName == "room_light", "create renamed the original selection" );
			Require( selection[1].Origin.AlmostEquals( new Vec3( 228, 128, 120 ) ), "create moved the original selection" );
			RequireReason( store.CreateInstanceFromSelection( "empty", Array.Empty<InstanceMember>(), Vec3.Zero ),
				"mapinstances-empty-submap", "empty selection" );
			RequireReason( store.CreateInstanceFromSelection( "   ", selection, Vec3.Zero ),
				"mapinstances-invalid-name", "blank name" );
		} );

		Check( "edit sub-map member propagates to all instances", () =>
		{
			var store = RoomStore();
			var a = store.PlaceInstance( "sm-1", Vec3.Zero, prefix: "a_" ).Instance!;
			var b = store.PlaceInstance( "sm-1", new Vec3( 512, 0, 0 ), prefix: "b_" ).Instance!;
			var edit = store.EditSubMapMember( "sm-1", "m-floor", InstanceMemberEdit.Move( new Vec3( 0, 0, 16 ) ) );
			Require( edit.Ok, $"edit failed: {edit.Reason} {edit.Message}" );
			Require( edit.AffectedInstances == 2, $"expected 2 affected instances, got {edit.AffectedInstances}" );
			Require( edit.AffectedInstanceIds.Contains( a.Id ) && edit.AffectedInstanceIds.Contains( b.Id ), "affected ids" );
			RequireVertex( Resolved( store.ResolveInstance( a.Id ), "m-floor" ), new Vec3( 0, 0, 16 ), "instance A after move" );
			RequireVertex( Resolved( store.ResolveInstance( b.Id ), "m-floor" ), new Vec3( 512, 0, 16 ), "instance B after move" );
			RequireNoVertex( Resolved( store.ResolveInstance( a.Id ), "m-floor" ), new Vec3( 0, 0, 0 ), "instance A kept the old vertex" );
			// Edit through one instance reaches the other (in-place edit with the main map as context).
			var via = store.EditSubMapMemberViaInstance( b.Id, "m-light",
				InstanceMemberEdit.SetKeyValue( "targetname", "room_lamp" ) );
			Require( via.Ok, $"edit via instance failed: {via.Reason}" );
			Require( Resolved( store.ResolveInstance( a.Id ), "m-light" ).TargetName == "a_room_lamp",
				"instance A did not pick up the targetname edited through instance B" );
			Require( Resolved( store.ResolveInstance( b.Id ), "m-light" ).TargetName == "b_room_lamp",
				"instance B did not pick up its own targetname edit" );
		} );

		Check( "edit refuses wrong-kind edits and unknown ids without changing anything", () =>
		{
			var store = RoomStore();
			var placed = store.PlaceInstance( "sm-1", Vec3.Zero, prefix: "a_" ).Instance!;
			RequireReason( store.EditSubMapMember( "sm-1", "m-light", InstanceMemberEdit.SetMaterial( "dev/x" ) ),
				"mapinstances-edit-not-applicable", "SetMaterial on an entity" );
			RequireReason( store.EditSubMapMember( "sm-1", "m-floor", InstanceMemberEdit.SetKeyValue( "targetname", "x" ) ),
				"mapinstances-edit-not-applicable", "SetKeyValue on a brush" );
			RequireReason( store.EditSubMapMember( "sm-1", "m-nope", InstanceMemberEdit.Move( Vec3.One ) ),
				"mapinstances-member-not-found", "unknown member" );
			RequireReason( store.EditSubMapMember( "sm-404", "m-floor", InstanceMemberEdit.Move( Vec3.One ) ),
				"mapinstances-submap-not-found", "unknown sub-map" );
			RequireReason( store.EditSubMapMemberViaInstance( "mi-404", "m-floor", InstanceMemberEdit.Move( Vec3.One ) ),
				"mapinstances-instance-not-found", "unknown instance" );
			Require( Resolved( store.ResolveInstance( placed.Id ), "m-light" ).TargetName == "a_room_light",
				"a refused edit changed the sub-map" );
		} );

		Check( "io proxy resolves inner targets with the prefix and leaves proxy ports alone", () =>
		{
			var store = RoomStore();
			var placed = store.PlaceInstance( "sm-1", Vec3.Zero, prefix: "room_a_" ).Instance!;
			var added = store.AddIoProxy( placed.Id,
				inputs: new[]
				{
					new InstanceIoRoute { ProxyPort = "OpenRoom", InnerTarget = "room_relay", InnerPort = "Trigger" },
				},
				outputs: new[]
				{
					new InstanceIoRoute { ProxyPort = "RoomLit", InnerTarget = "room_light", InnerPort = "OnTurnOn", Parameter = "1" },
				} );
			Require( added.Ok && added.Proxy is not null, $"add proxy failed: {added.Reason} {added.Message}" );
			var resolved = store.ResolveProxy( added.Proxy!.Id );
			Require( resolved.Ok && resolved.Proxy is not null, $"resolve proxy failed: {resolved.Reason}" );
			Require( resolved.Proxy!.Prefix == "room_a_", "proxy prefix" );
			Require( resolved.Proxy.Inputs.Count == 1 && resolved.Proxy.Inputs[0].InnerTarget == "room_a_room_relay",
				"proxy input inner target was not prefixed" );
			Require( resolved.Proxy.Inputs[0].ProxyPort == "OpenRoom", "proxy input port was prefixed" );
			Require( resolved.Proxy.Outputs.Count == 1 && resolved.Proxy.Outputs[0].InnerTarget == "room_a_room_light",
				"proxy output inner target was not prefixed" );
			Require( resolved.Proxy.Outputs[0].ProxyPort == "RoomLit" && resolved.Proxy.Outputs[0].Parameter == "1",
				"proxy output port/parameter changed" );
			// The stored proxy keeps unprefixed inner targets, so a re-prefix never double-prefixes.
			Require( added.Proxy.Inputs[0].InnerTarget == "room_relay", "stored proxy was prefixed in place" );
			var again = store.ResolveProxy( added.Proxy.Id );
			Require( again.Proxy!.Inputs[0].InnerTarget == "room_a_room_relay", "second resolve double-prefixed" );
			RequireReason( store.AddIoProxy( placed.Id,
				inputs: new[] { new InstanceIoRoute { ProxyPort = "Bad", InnerTarget = "nope", InnerPort = "Trigger" } },
				outputs: Array.Empty<InstanceIoRoute>() ), "mapinstances-proxy-target-not-found", "unknown inner target" );
			RequireReason( store.AddIoProxy( placed.Id,
				inputs: new[] { new InstanceIoRoute { ProxyPort = "  ", InnerTarget = "room_relay", InnerPort = "Trigger" } },
				outputs: Array.Empty<InstanceIoRoute>() ), "mapinstances-invalid-proxy-port", "blank proxy port" );
			RequireReason( store.AddIoProxy( "mi-404", inputs: Array.Empty<InstanceIoRoute>(), outputs: Array.Empty<InstanceIoRoute>() ),
				"mapinstances-instance-not-found", "unknown instance" );
			RequireReason( store.ResolveProxy( "iop-404" ), "mapinstances-proxy-not-found", "unknown proxy" );
			// Removing the instance removes its proxies.
			Require( store.RemoveInstance( placed.Id ).Ok, "remove instance failed" );
			Require( store.Proxies.Count == 0, "instance removal left its proxies behind" );
			RequireReason( store.RemoveInstance( "mi-404" ), "mapinstances-instance-not-found", "double remove" );
		} );

		Check( "fixture instances.json loads with preset, prefab, sub-map, instance and proxy", () =>
		{
			var store = MapInstanceStore.FromJson( File.ReadAllText( FindFixture( "instances.json" ) ) );
			Require( store.Presets.Count == 1 && store.Prefabs.Count == 1 && store.SubMaps.Count == 1
				&& store.Instances.Count == 1 && store.Proxies.Count == 1,
				$"fixture counts: presets {store.Presets.Count}, prefabs {store.Prefabs.Count}, sub-maps {store.SubMaps.Count}, instances {store.Instances.Count}, proxies {store.Proxies.Count}" );
			Require( store.Presets[0].Name == "barrel-cluster" && store.Presets[0].Items.Count == 3, "fixture preset" );
			Require( store.Prefabs[0].Name == "door-frame" && store.Prefabs[0].Members.Count == 2, "fixture prefab" );
			var sub = store.SubMaps[0];
			Require( sub.Id == "sm-1" && sub.SourcePath == "maps/rooms/room_a.vmf" && sub.Members.Count == 4, "fixture sub-map" );
			var instance = store.Instances[0];
			Require( instance.Id == "mi-1" && instance.SubMapId == "sm-1" && instance.Prefix == "room_a_", "fixture instance" );
			var resolved = store.ResolveInstance( "mi-1" );
			Require( resolved.Ok, $"fixture resolve failed: {resolved.Reason}" );
			Require( Resolved( resolved, "m-light" ).TargetName == "room_a_room_light", "fixture light fix-up" );
			Require( Resolved( resolved, "m-relay" ).KeyValues["OnTrigger"] == "room_a_room_light,TurnOn,,0,-1",
				"fixture relay output fix-up" );
			Require( Resolved( resolved, "m-relay" ).KeyValues["target"] == "main_door", "fixture outward reference" );
			Require( Resolved( resolved, "m-light" ).Origin.AlmostEquals( new Vec3( 640, 128, 120 ) ), "fixture light origin" );
			var proxy = store.ResolveProxy( "iop-1" );
			Require( proxy.Ok && proxy.Proxy is not null, "fixture proxy resolve failed" );
			Require( proxy.Proxy!.Inputs[0].InnerTarget == "room_a_room_relay", "fixture proxy input" );
			Require( proxy.Proxy.Outputs[0].InnerTarget == "room_a_room_light", "fixture proxy output" );
			Require( store.ValidateInstance( "mi-1", new[] { "room_light" } ).Count == 0, "fixture instance reported issues" );
		} );

		Check( "round-trip through the map document preserves the store and keeps ids unique", () =>
		{
			var before = MapInstanceStore.FromJson( File.ReadAllText( FindFixture( "instances-roundtrip.json" ) ) );
			Require( before.Presets.Count == 1 && before.Prefabs.Count == 1 && before.SubMaps.Count == 1
				&& before.Instances.Count == 2 && before.Proxies.Count == 1, "round-trip fixture shape" );
			string json = before.ToJson();
			var after = MapInstanceStore.FromJson( json );
			Require( after.ToJson() == json, "second serialisation differs from the first (round-trip is not stable)" );
			Require( after.Instances.Count == 2 && after.Proxies.Count == 1, "round-trip counts" );
			foreach ( var instance in before.Instances )
			{
				var beforeMembers = before.ResolveInstance( instance.Id ).Members;
				var afterMembers = after.ResolveInstance( instance.Id ).Members;
				Require( afterMembers.Count == beforeMembers.Count, $"instance {instance.Id} lost members in the round-trip" );
				for ( int i = 0; i < beforeMembers.Count; i++ )
				{
					Require( afterMembers[i].Vertices.Count == beforeMembers[i].Vertices.Count
						&& afterMembers[i].Vertices.Zip( beforeMembers[i].Vertices, ( x, y ) => x.AlmostEquals( y ) ).All( same => same ),
						$"instance {instance.Id} member {beforeMembers[i].Id} vertices changed in the round-trip" );
					Require( afterMembers[i].Origin.AlmostEquals( beforeMembers[i].Origin ), $"instance {instance.Id} member origin changed" );
					Require( afterMembers[i].TargetName == beforeMembers[i].TargetName, $"instance {instance.Id} member targetname changed" );
				}
			}
			// Ids stay unique after a reload: the next preset is sp-2, the next instance mi-3.
			var extraPreset = after.DefinePreset( "extra", new[] { new SprinkleItem { Model = "models/x.mdl" } } );
			Require( extraPreset.Ok && extraPreset.Preset!.Id == "sp-2", $"next preset id after reload was '{extraPreset.Preset?.Id}'" );
			var extraInstance = after.PlaceInstance( "sm-1", Vec3.Zero );
			Require( extraInstance.Ok && extraInstance.Instance!.Id == "mi-3", $"next instance id after reload was '{extraInstance.Instance?.Id}'" );
		} );

		Check( "from-json rejects corrupt documents with a named field", () =>
		{
			void RequireThrows( string json, string fragment, string message )
			{
				try { MapInstanceStore.FromJson( json ); }
				catch ( ArgumentException ex )
				{
					Require( ex.Message.Contains( fragment, StringComparison.Ordinal ),
						$"{message}: error '{ex.Message}' does not name '{fragment}'" );
					return;
				}
				throw new Exception( $"{message}: corrupt JSON was accepted" );
			}
			RequireThrows( "", "empty", "empty JSON" );
			RequireThrows( "{ not json", "not valid JSON", "malformed JSON" );
			RequireThrows( "{\"version\":2,\"presets\":[],\"prefabs\":[],\"submaps\":[],\"instances\":[],\"proxies\":[]}",
				"unsupported map_instances version 2", "version 2" );
			RequireThrows( "{\"version\":1,\"presets\":[],\"prefabs\":[],\"submaps\":[],\"instances\":[{\"id\":\"mi-1\",\"name\":\"x\",\"submap_id\":\"sm-404\",\"source_path\":\"\",\"prefix\":\"\",\"fixup\":true,\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"yaw_degrees\":0,\"layer\":\"\",\"visgroup\":\"\"}],\"proxies\":[]}",
				"missing sub-map 'sm-404'", "instance pointing at a missing sub-map" );
			RequireThrows( "{\"version\":1,\"presets\":[{\"id\":\"sp-1\",\"name\":\"a\",\"items\":[{\"model\":\"models/x.mdl\",\"classname\":\"prop_static\",\"offset\":{\"x\":0,\"y\":0,\"z\":0},\"angles\":{\"x\":0,\"y\":0,\"z\":0},\"scale\":1,\"key_values\":{}}]},{\"id\":\"sp-1\",\"name\":\"b\",\"items\":[{\"model\":\"models/y.mdl\",\"classname\":\"prop_static\",\"offset\":{\"x\":0,\"y\":0,\"z\":0},\"angles\":{\"x\":0,\"y\":0,\"z\":0},\"scale\":1,\"key_values\":{}}]}],\"prefabs\":[],\"submaps\":[],\"instances\":[],\"proxies\":[]}",
				"preset id 'sp-1' appears twice", "duplicate preset id" );
			RequireThrows( "{\"version\":1,\"presets\":[{\"id\":\"sp-1\",\"name\":\"a\",\"items\":[{\"model\":\"models/x.mdl\",\"classname\":\"prop_static\",\"offset\":{\"x\":0,\"y\":0,\"z\":0},\"angles\":{\"x\":0,\"y\":0,\"z\":0},\"scale\":0,\"key_values\":{}}]}],\"prefabs\":[],\"submaps\":[],\"instances\":[],\"proxies\":[]}",
				"invalid scale 0", "zero scale in JSON" );
			RequireThrows( "{\"version\":1,\"presets\":[],\"prefabs\":[{\"id\":\"pf-1\",\"name\":\"x\",\"layer\":\"\",\"visgroup\":\"\",\"members\":[]}],\"submaps\":[],\"instances\":[],\"proxies\":[]}",
				"prefab 'pf-1' has no members", "prefab with no members" );
			RequireThrows( "{\"version\":1,\"presets\":[],\"prefabs\":[],\"submaps\":[],\"instances\":[],\"proxies\":[{\"id\":\"iop-1\",\"instance_id\":\"mi-404\",\"inputs\":[],\"outputs\":[]}]}",
				"missing instance 'mi-404'", "proxy pointing at a missing instance" );
		} );

		Check( "func_instance records rebuild instances against registered sub-maps", () =>
		{
			var store = RoomStore();
			var a = store.PlaceInstance( "sm-1", Vec3.Zero, prefix: "a_" ).Instance!;
			var b = store.PlaceInstance( "sm-1", new Vec3( 512, 0, 0 ), prefix: "b_" ).Instance!;
			var records = store.ToFuncInstanceRecords();
			Require( records.Count == 2, $"expected 2 records, got {records.Count}" );
			Require( records.All( r => r.SubMapId == "sm-1" && r.SubMapPath == "maps/rooms/room_a.vmf" ), "records lost the sub-map" );
			Require( records.First( r => r.InstanceId == b.Id ).Origin.AlmostEquals( new Vec3( 512, 0, 0 ) ), "record origin" );
			var rebuilt = new MapInstanceStore();
			var registered = rebuilt.RegisterSubMap( RoomSubMap() );
			Require( registered.Ok, "rebuilt sub-map failed" );
			var loaded = rebuilt.FromFuncInstanceRecords( records.Select( r => new FuncInstanceRecord
			{
				InstanceId = r.InstanceId,
				SubMapId = r.SubMapId,
				SubMapPath = r.SubMapPath,
				Prefix = r.Prefix,
				FixUp = r.FixUp,
				OriginX = r.OriginX, OriginY = r.OriginY, OriginZ = r.OriginZ,
				YawDegrees = r.YawDegrees,
				Layer = r.Layer, VisGroup = r.VisGroup,
			} ).ToList() );
			Require( loaded.Ok, $"records rebuild failed: {loaded.Reason} {loaded.Message}" );
			Require( rebuilt.Instances.Count == 2, "rebuilt instance count" );
			Require( Resolved( rebuilt.ResolveInstance( b.Id ), "m-light" ).TargetName == "b_room_light",
				"rebuilt copy B light fix-up" );
			RequireVertex( Resolved( rebuilt.ResolveInstance( b.Id ), "m-floor" ), new Vec3( 512, 0, 0 ), "rebuilt copy B floor" );
			RequireReason( rebuilt.FromFuncInstanceRecords( new[]
			{
				new FuncInstanceRecord { InstanceId = "mi-99", SubMapId = "sm-404" },
			} ), "mapinstances-submap-not-found", "record pointing at a missing sub-map" );
			Require( rebuilt.Instances.Count == 2, "failed rebuild added instances anyway" );
			RequireReason( rebuilt.FromFuncInstanceRecords( new[]
			{
				new FuncInstanceRecord { InstanceId = a.Id, SubMapId = "sm-1" },
			} ), "mapinstances-duplicate-instance-id", "record reusing an instance id" );
		} );

		Check( "validate instance reports missing sub-map, collision and bad proxy target", () =>
		{
			var store = RoomStore();
			var issues = store.ValidateInstance( "mi-404" );
			Require( issues.Count == 1 && issues[0].Code == MapInstanceIssue.CodeInstanceNotFound,
				$"unknown instance validation: [{string.Join( "; ", issues )}]" );
			// A proxy leg pointing at a target the sub-map does not define: build it
			// through JSON, because AddIoProxy refuses it up front.
			const string badProxyJson = "{\"version\":1,\"presets\":[],\"prefabs\":[],\"submaps\":[{\"id\":\"sm-1\",\"source_path\":\"maps/rooms/room_a.vmf\",\"name\":\"room_a\",\"members\":[{\"id\":\"m-light\",\"kind\":\"entity\",\"name\":\"light\",\"material\":\"\",\"origin\":{\"x\":128,\"y\":128,\"z\":120},\"angles\":{\"x\":0,\"y\":0,\"z\":0},\"vertices\":[],\"key_values\":{\"classname\":\"light\",\"targetname\":\"room_light\"}}]}],\"instances\":[{\"id\":\"mi-1\",\"name\":\"room_a\",\"submap_id\":\"sm-1\",\"source_path\":\"maps/rooms/room_a.vmf\",\"prefix\":\"room_a_\",\"fixup\":true,\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"yaw_degrees\":0,\"layer\":\"\",\"visgroup\":\"\"}],\"proxies\":[{\"id\":\"iop-1\",\"instance_id\":\"mi-1\",\"inputs\":[{\"proxy_port\":\"Open\",\"inner_target\":\"ghost\",\"inner_port\":\"Trigger\",\"parameter\":\"\"}],\"outputs\":[]}]}";
			var badStore = MapInstanceStore.FromJson( badProxyJson );
			var badIssues = badStore.ValidateInstance( "mi-1" );
			Require( badIssues.Count == 1 && badIssues[0].Code == MapInstanceIssue.CodeProxyTargetNotFound,
				$"bad proxy validation: [{string.Join( "; ", badIssues )}]" );
		} );

		Check( "fix-up values: exact, comma-form, specials and case", () =>
		{
			var defined = new HashSet<string>( new[] { "room_light", "room_relay" }, StringComparer.OrdinalIgnoreCase );
			Require( InstanceNameFixUp.FixUpValue( "room_light", defined, "a_" ) == "a_room_light", "exact match" );
			Require( InstanceNameFixUp.FixUpValue( "ROOM_LIGHT", defined, "a_" ) == "a_ROOM_LIGHT", "case-insensitive match keeps the value's case" );
			Require( InstanceNameFixUp.FixUpValue( "room_light,TurnOn,,0,-1", defined, "a_" ) == "a_room_light,TurnOn,,0,-1", "comma-form head" );
			Require( InstanceNameFixUp.FixUpValue( "main_door,Trigger,,0,-1", defined, "a_" ) == "main_door,Trigger,,0,-1", "comma-form outward reference" );
			Require( InstanceNameFixUp.FixUpValue( "!activator", defined, "a_" ) == "!activator", "engine special" );
			Require( InstanceNameFixUp.FixUpValue( "main_door", defined, "a_" ) == "main_door", "outward reference" );
			Require( InstanceNameFixUp.FixUpValue( "room_light", defined, "" ) == "room_light", "empty prefix" );
			Require( InstanceNameFixUp.FixUpValue( "", defined, "a_" ) == "", "empty value" );
			Require( InstanceNameFixUp.DefinedTargetNames( RoomSubMap().Members ).SetEquals( new[] { "room_light", "room_relay" } ),
				"defined target names of the room sub-map" );
			Require( InstanceNameFixUp.FindCollisions(
				new[] { Lamp( "m-1", "l", Vec3.Zero, "room_light" ) },
				new[] { "ROOM_LIGHT" } ).Count == 1, "collision match is case-insensitive" );
		} );

		Check( "remove sub-map refuses while in use and succeeds after instances are gone", () =>
		{
			var store = RoomStore();
			var placed = store.PlaceInstance( "sm-1", Vec3.Zero ).Instance!;
			RequireReason( store.RemoveSubMap( "sm-1" ), "mapinstances-submap-in-use", "sub-map in use" );
			Require( store.SubMaps.Count == 1, "refused removal dropped the sub-map" );
			Require( store.RemoveInstance( placed.Id ).Ok, "remove instance failed" );
			Require( store.RemoveSubMap( "sm-1" ).Ok, "remove after instances failed" );
			Require( store.SubMaps.Count == 0, "sub-map was not removed" );
			RequireReason( store.RemoveSubMap( "sm-404" ), "mapinstances-submap-not-found", "unknown sub-map" );
			var firstRegister = store.RegisterSubMap( RoomSubMap() );
			Require( firstRegister.Ok, $"re-register after removal failed: {firstRegister.Reason}" );
			RequireReason( store.RegisterSubMap( RoomSubMap() ), "mapinstances-submap-duplicate-id", "duplicate sub-map id" );
		} );

		Check( "resolve with yaw rotates brushes, origins and entity angles", () =>
		{
			var store = RoomStore();
			var placed = store.PlaceInstance( "sm-1", new Vec3( 100, 0, 0 ), prefix: "r_", yawDegrees: 90f ).Instance!;
			var resolved = store.ResolveInstance( placed.Id );
			Require( resolved.Ok, "yaw resolve failed" );
			// Local light (128,128,120) yawed 90 degrees -> (-128,128,120), then + (100,0,0).
			Require( Resolved( resolved, "m-light" ).Origin.AlmostEquals( new Vec3( -28, 128, 120 ) ),
				$"yawed light origin {Resolved( resolved, "m-light" ).Origin}" );
			// Floor corner (256,256,0) -> (-256,256,0) + (100,0,0).
			RequireVertex( Resolved( resolved, "m-floor" ), new Vec3( -156, 256, 0 ), "yawed floor corner" );
			RequireVertex( Resolved( resolved, "m-floor" ), new Vec3( 100, 0, 0 ), "yawed floor origin corner" );
			// Entity angles gain the yaw.
			var angled = new MapInstanceStore();
			var sub = new SubMapContent { Id = "sm-1", SourcePath = "", Name = "angled" };
			var prop = new InstanceMember { Id = "m-1", Kind = InstanceMemberKind.Entity, Name = "prop", Origin = new Vec3( 64, 0, 0 ), Angles = new Vec3( 0, 30, 0 ) };
			prop.KeyValues["classname"] = "prop_static";
			sub.Members.Add( prop );
			Require( angled.RegisterSubMap( sub ).Ok, "angled sub-map failed" );
			var angledPlaced = angled.PlaceInstance( "sm-1", Vec3.Zero, yawDegrees: 90f ).Instance!;
			var angledResolved = angled.ResolveInstance( angledPlaced.Id );
			Require( Resolved( angledResolved, "m-1" ).Origin.AlmostEquals( new Vec3( 0, 64, 0 ) ), "yawed entity origin" );
			Require( Resolved( angledResolved, "m-1" ).Angles.AlmostEquals( new Vec3( 0, 120, 0 ) ), "yawed entity angles" );
		} );
	}
}
