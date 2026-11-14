using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

/// <summary>
/// Tests for triggers, doors, buttons, movers, breakables, spawns, items/weapons and
/// NPC nodes (hammer-triggers-volumes-gameplay-entities): the 42-class gameplay
/// catalog against the real core registry, trigger-volume placement with the
/// toolstrigger face contract, validated door/button/trigger I/O wiring, node/hint
/// placement and listing, and the gameplay validator (testable map, unwired-trigger
/// and unwired-button warnings, no-player-start error). Brushes are TestBrush
/// stand-ins: the placement seam is generic over TBrush (see MapEntities.cs header),
/// so the same calls take IReadOnlyList&lt;MapBrush&gt; once callers pass real brushes.
/// Fixtures live in tests/fixtures/hammer/hammer-triggers-volumes-gameplay-entities/.
/// </summary>
internal static class TriggersVolumesTests
{
	sealed class TestBrush
	{
		public string Name = "";
		public TestBrush( string name ) { Name = name; }
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "triggersvolumes", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		T Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ex ) { return ex; }
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}

		string RepoRoot()
		{
			foreach ( string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory } )
			{
				string dir = Path.GetFullPath( start );
				while ( dir is not null )
				{
					if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
					dir = Directory.GetParent( dir )?.FullName;
				}
			}
			throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
		}

		string FixtureDir() => Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-triggers-volumes-gameplay-entities" );
		string CorePath() => Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" );
		EntityRegistry LoadCore() => EntityRegistry.Load( CorePath() );

		/// <summary>A MapEntities whose registry is core + the 42 gameplay classes.</summary>
		MapEntities NewEntities()
		{
			var entities = new MapEntities( LoadCore() );
			GameplayEntityCatalog.EnsureGameplayClasses( entities.Registry );
			return entities;
		}

		TestBrush Brush( string name ) => new TestBrush( name );

		Dictionary<string, string> Kv( params (string Key, string Value)[] pairs )
		{
			var dict = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var (key, value) in pairs ) dict[key] = value;
			return dict;
		}

		MapEntity PlaceStart( MapEntities entities, Vec3 position ) =>
			entities.PlacePointEntity( "info_player_start", position );

		// ---- Catalog ----

		Check( "catalog loads 42 classes: 9 brush, 33 point, no duplicates, every class has an origin helper", () =>
		{
			var catalog = GameplayEntityCatalog.LoadRegistry();
			Require( catalog.Count == 42, $"catalog count is {catalog.Count}, expected 42" );
			Require( GameplayEntityCatalog.Count == 42, $"static Count is {GameplayEntityCatalog.Count}, expected 42" );
			int brush = catalog.Classes.Count( c => c.Kind == EntityKind.Brush );
			int point = catalog.Classes.Count( c => c.Kind == EntityKind.Point );
			Require( brush == 9, $"brush classes: {brush}, expected 9" );
			Require( point == 33, $"point classes: {point}, expected 33" );
			var names = catalog.Classes.Select( c => c.Name ).ToArray();
			Require( names.Distinct( StringComparer.OrdinalIgnoreCase ).Count() == names.Length, "catalog has duplicate class names" );
			foreach ( var cls in catalog.Classes )
				Require( cls.HasHelper( EntityHelperKind.Origin ), $"class '{cls.Name}' has no origin helper" );
			Require( catalog.Name == "source1-gameplay", $"catalog name is '{catalog.Name}', expected 'source1-gameplay'" );
			Require( catalog.SchemaVersion == 1, $"catalog schemaVersion is {catalog.SchemaVersion}, expected 1" );
		} );

		Check( "gameplay-entities.json fixture is the catalog JSON verbatim", () =>
		{
			string fixture = File.ReadAllText( Path.Combine( FixtureDir(), "gameplay-entities.json" ) );
			Require( fixture.Trim() == GameplayEntityCatalog.GameplayEntitiesJson.Trim(),
				"fixture gameplay-entities.json differs from GameplayEntityCatalog.GameplayEntitiesJson" );
			Require( GameplayEntityCatalog.ToRegistryJson() == GameplayEntityCatalog.GameplayEntitiesJson,
				"ToRegistryJson must return the canonical JSON verbatim" );
		} );

		Check( "ToRegistryJson round-trips: loading it yields the same 42 class names", () =>
		{
			var back = EntityRegistry.LoadFromJson( GameplayEntityCatalog.ToRegistryJson(), "round-trip" );
			Require( back.Classes.Select( c => c.Name ).SequenceEqual( GameplayEntityCatalog.AllClassNames ),
				"round-tripped class names differ from the catalog" );
			var trigger = back.Get( "trigger_push" );
			Require( trigger.Kind == EntityKind.Brush, "trigger_push must be a brush class" );
			Require( trigger.FindKeyValue( "speed" )?.Type == KeyValueType.Float, "trigger_push speed must be float" );
			Require( trigger.FindOutput( "OnStartTouch" ) is not null, "trigger_push must declare OnStartTouch" );
			Require( back.Get( "npc_citizen" ).FindInput( "Wake" ) is not null, "npc_citizen must accept Wake" );
			Require( back.Get( "path_track" ).FindOutput( "OnPass" ) is not null, "path_track must fire OnPass" );
		} );

		Check( "EnsureGameplayClasses merges 42 into core (14 -> 56) and is idempotent", () =>
		{
			var core = LoadCore();
			Require( core.Count == 14, $"core count is {core.Count}, expected 14" );
			var added = GameplayEntityCatalog.EnsureGameplayClasses( core );
			Require( added.Count == 42, $"first merge added {added.Count}, expected 42" );
			Require( core.Count == 56, $"merged count is {core.Count}, expected 56" );
			var addedAgain = GameplayEntityCatalog.EnsureGameplayClasses( core );
			Require( addedAgain.Count == 0, $"second merge added {addedAgain.Count}, expected 0" );
			Require( core.Count == 56, "second merge must not change the count" );
			var catalogOnly = GameplayEntityCatalog.LoadRegistry();
			Require( GameplayEntityCatalog.EnsureGameplayClasses( catalogOnly ).Count == 0,
				"merging into a registry that already has all 42 must add 0" );
		} );

		Check( "category checks cover core and catalog classes and reject unrelated classes", () =>
		{
			Require( GameplayEntityCatalog.IsTrigger( "trigger_multiple" ), "trigger_multiple must be a trigger" );
			Require( GameplayEntityCatalog.IsTrigger( "trigger_push" ), "trigger_push must be a trigger" );
			Require( GameplayEntityCatalog.IsTrigger( "TRIGGER_TELEPORT" ), "category checks must be case-insensitive" );
			Require( !GameplayEntityCatalog.IsTrigger( "func_door" ), "func_door must not be a trigger" );
			Require( GameplayEntityCatalog.IsDoor( "func_door" ) && GameplayEntityCatalog.IsDoor( "func_door_rotating" ), "door set is wrong" );
			Require( GameplayEntityCatalog.IsButton( "func_button" ) && GameplayEntityCatalog.IsButton( "func_rot_button" ), "button set is wrong" );
			Require( GameplayEntityCatalog.IsMover( "func_movelinear" ) && GameplayEntityCatalog.IsMover( "func_tracktrain" ) && GameplayEntityCatalog.IsMover( "func_plat" ), "mover set is wrong" );
			Require( GameplayEntityCatalog.IsBreakable( "func_breakable" ) && GameplayEntityCatalog.IsBreakable( "func_pushable" ), "breakable set is wrong" );
			Require( GameplayEntityCatalog.IsSpawn( "info_player_start" ) && GameplayEntityCatalog.IsSpawn( "info_player_teamspawn" ) && GameplayEntityCatalog.IsSpawn( "info_player_deathmatch" ), "spawn set is wrong" );
			Require( GameplayEntityCatalog.IsItem( "item_healthkit" ) && GameplayEntityCatalog.IsWeapon( "weapon_pistol" ), "item/weapon sets are wrong" );
			Require( GameplayEntityCatalog.IsItemOrWeapon( "item_battery" ) && GameplayEntityCatalog.IsItemOrWeapon( "weapon_rpg" ), "IsItemOrWeapon is wrong" );
			Require( !GameplayEntityCatalog.IsItemOrWeapon( "npc_citizen" ), "an NPC must not be an item/weapon" );
			Require( GameplayEntityCatalog.IsNpc( "npc_citizen" ) && GameplayEntityCatalog.IsNpc( "npc_combine_s" ), "NPC set is wrong" );
			Require( GameplayEntityCatalog.IsNodeOrHint( "info_node" ) && GameplayEntityCatalog.IsNodeOrHint( "info_node_hint" ), "node set is wrong" );
			Require( GameplayEntityCatalog.IsTarget( "info_target" ) && GameplayEntityCatalog.IsTarget( "info_teleport_destination" ), "target set is wrong" );
			Require( GameplayEntityCatalog.IsPathTrack( "path_track" ), "path_track must be a path class" );
			Require( GameplayEntityCatalog.IsGameplayEntity( "trigger_multiple" ) && GameplayEntityCatalog.IsGameplayEntity( "weapon_bugbait" ), "IsGameplayEntity misses a gameplay class" );
			Require( !GameplayEntityCatalog.IsGameplayEntity( "light" ), "light must not be a gameplay entity" );
			Require( !GameplayEntityCatalog.IsGameplayEntity( "logic_relay" ), "logic_relay must not be a gameplay entity" );
			Require( !GameplayEntityCatalog.IsGameplayEntity( "" ) && !GameplayEntityCatalog.IsGameplayEntity( null ), "blank class names must not be gameplay entities" );
		} );

		// ---- Trigger volumes ----

		Check( "PlaceTrigger makes a trigger volume: trigger-flagged, non-solid, invisible, brushes kept", () =>
		{
			var entities = NewEntities();
			var volume = GameplayEntities.PlaceTrigger( entities, "trigger_multiple",
				new[] { Brush( "vol-a" ), Brush( "vol-b" ) },
				Kv( ("targetname", "trigger_01") ), new Vec3( 0, 0, 64 ) );
			Require( volume.Entity.IsBrushEntity, "a trigger volume must be a brush entity" );
			Require( volume.BrushCount == 2, $"volume has {volume.BrushCount} brushes, expected 2" );
			Require( volume.TargetName == "trigger_01", $"targetname is '{volume.TargetName}'" );
			Require( volume.Origin.AlmostEquals( new Vec3( 0, 0, 64 ) ), $"origin is {volume.Origin}" );
			Require( volume.FaceMaterial == "tools/toolstrigger", $"face material is '{volume.FaceMaterial}'" );
			Require( volume.HasTriggerFlag, "the volume's faces must carry the Trigger flag" );
			Require( volume.IsNonSolid, "the volume's faces must carry the NonSolid flag" );
			Require( volume.IsInvisible, "the volume's faces must be a trigger tool texture (never drawn)" );
			Require( volume.IsValidVolume, "the volume contract must hold end to end" );
			Require( GameplayEntities.TriggerFaceFlags == ( ToolCompileFlags.Trigger | ToolCompileFlags.NonSolid ),
				"TriggerFaceFlags must be Trigger | NonSolid" );
			var texture = ToolTexture.Lookup( "tools/toolstrigger" );
			Require( texture is not null && !texture.IsSolid, "tools/toolstrigger must be known and non-solid" );
			Require( texture.CompileFlags == GameplayEntities.TriggerFaceFlags, "the tool texture's flags must match TriggerFaceFlags" );
		} );

		Check( "PlaceTrigger rejects a non-trigger class and an unmerged gameplay class, by name", () =>
		{
			var entities = NewEntities();
			var notTrigger = Throws<TriggersVolumesException>( () =>
				GameplayEntities.PlaceTrigger( entities, "func_door", new[] { Brush( "d" ) } ) );
			Require( notTrigger.Code == TriggersVolumesException.CodeNotTriggerClass,
				$"func_door as trigger must be NotTriggerClass, got {notTrigger.Code}" );
			var bare = new MapEntities( LoadCore() );
			var unmerged = Throws<TriggersVolumesException>( () =>
				GameplayEntities.PlaceTrigger( bare, "trigger_push", new[] { Brush( "p" ) } ) );
			Require( unmerged.Code == TriggersVolumesException.CodeGameplayClassesNotMerged,
				$"unmerged trigger_push must be GameplayClassesNotMerged, got {unmerged.Code}" );
			Require( unmerged.Message.Contains( "EnsureGameplayClasses" ), "the unmerged error must name the fix" );
			var coreTrigger = GameplayEntities.PlaceTrigger( bare, "trigger_multiple", new[] { Brush( "m" ) } );
			Require( coreTrigger.IsValidVolume, "a core trigger class needs no merge to place" );
		} );

		Check( "DescribeTrigger works on a directly placed entity and rejects a door", () =>
		{
			var entities = NewEntities();
			var placed = entities.PlaceBrushEntity( "trigger_hurt", new[] { Brush( "h" ) }, Kv( ("damage", "25") ) );
			var volume = GameplayEntities.DescribeTrigger( placed );
			Require( volume.ClassName == "trigger_hurt" && volume.IsValidVolume, "DescribeTrigger must accept a trigger_hurt entity" );
			var door = entities.PlaceBrushEntity( "func_door", new[] { Brush( "d" ) } );
			var error = Throws<TriggersVolumesException>( () => GameplayEntities.DescribeTrigger( door ) );
			Require( error.Code == TriggersVolumesException.CodeNotTriggerClass,
				$"a door described as a trigger must be NotTriggerClass, got {error.Code}" );
		} );

		Check( "registry type checks flow through trigger placement (unknown key, mistyped speed)", () =>
		{
			var entities = NewEntities();
			var unknown = Throws<EntityRegistryException>( () =>
				GameplayEntities.PlaceTrigger( entities, "trigger_push", new[] { Brush( "p" ) }, Kv( ("nope", "1") ) ) );
			Require( unknown.Code == EntityRegistry.CodeUnknownKeyValue,
				$"unknown key must be UnknownKeyValue, got {unknown.Code}" );
			var mistyped = Throws<EntityRegistryException>( () =>
				GameplayEntities.PlaceTrigger( entities, "trigger_push", new[] { Brush( "p" ) }, Kv( ("speed", "fast") ) ) );
			Require( mistyped.Code == EntityRegistry.CodeInvalidKeyValueType,
				$"mistyped speed must be InvalidKeyValueType, got {mistyped.Code}" );
		} );

		// ---- Doors / buttons ----

		Check( "PlaceDoor and PlaceButton enforce their categories", () =>
		{
			var entities = NewEntities();
			var door = GameplayEntities.PlaceDoor( entities, "func_door_rotating",
				new[] { Brush( "door" ) }, Kv( ("targetname", "door_01"), ("distance", "90") ) );
			Require( door.IsBrushEntity && door.TargetName == "door_01", "rotating door placement failed" );
			Require( entities.GetKeyvalue( door, "distance" ) == "90", "distance keyvalue did not round-trip" );
			var notDoor = Throws<TriggersVolumesException>( () =>
				GameplayEntities.PlaceDoor( entities, "func_button", new[] { Brush( "b" ) } ) );
			Require( notDoor.Code == TriggersVolumesException.CodeNotDoorClass,
				$"func_button as door must be NotDoorClass, got {notDoor.Code}" );
			var button = GameplayEntities.PlaceButton( entities, "func_rot_button",
				new[] { Brush( "btn" ) }, Kv( ("targetname", "button_01") ) );
			Require( button.IsBrushEntity && button.TargetName == "button_01", "rotating button placement failed" );
			var notButton = Throws<TriggersVolumesException>( () =>
				GameplayEntities.PlaceButton( entities, "func_door", new[] { Brush( "d" ) } ) );
			Require( notButton.Code == TriggersVolumesException.CodeNotButtonClass,
				$"func_door as button must be NotButtonClass, got {notButton.Code}" );
		} );

		// ---- Nodes / hints / targets ----

		Check( "nodes and hints are placed and listed in order with hint text", () =>
		{
			var entities = NewEntities();
			GameplayEntities.PlaceNode( entities, new Vec3( 0, 0, 0 ) );
			var hint = GameplayEntities.PlaceNodeHint( entities, new Vec3( 128, 0, 0 ), "Crouch Cover", 7 );
			GameplayEntities.PlaceNode( entities, new Vec3( 256, 0, 0 ) );
			Require( entities.GetKeyvalue( hint, "hint" ) == "Crouch Cover", "hint keyvalue did not round-trip" );
			Require( entities.GetKeyvalue( hint, "nodeid" ) == "7", "nodeid keyvalue did not round-trip" );
			var nodes = GameplayEntities.ListNodes( entities );
			Require( nodes.Count == 3, $"ListNodes found {nodes.Count}, expected 3" );
			Require( !nodes[0].IsHint && nodes[0].Hint == "", "a plain node must have no hint text" );
			Require( nodes[1].IsHint && nodes[1].Hint == "Crouch Cover", $"hint entry is '{nodes[1].Hint}'" );
			Require( nodes[1].Origin.AlmostEquals( new Vec3( 128, 0, 0 ) ), $"hint origin is {nodes[1].Origin}" );
			Require( nodes[2].EntityId == 3 && nodes[2].ClassName == "info_node", "third node is wrong" );
			var target = GameplayEntities.PlaceTarget( entities, new Vec3( 512, 0, 64 ), Kv( ("targetname", "aim_01") ) );
			Require( target.TargetName == "aim_01", "info_target placement failed" );
			var dest = GameplayEntities.PlaceTeleportDestination( entities, new Vec3( 1024, 0, 64 ), Kv( ("targetname", "dest_01") ) );
			Require( dest.TargetName == "dest_01", "teleport destination placement failed" );
			Require( GameplayEntities.ListNodes( entities ).Count == 3, "targets must not appear in the node list" );
		} );

		Check( "PlaceNodeHint keeps a caller-supplied hint and nodeid instead of overwriting them", () =>
		{
			var entities = NewEntities();
			var hint = GameplayEntities.PlaceNodeHint( entities, new Vec3( 0, 0, 0 ), "Sniper", 3,
				Kv( ("hint", "Guard Position"), ("nodeid", "9") ) );
			Require( entities.GetKeyvalue( hint, "hint" ) == "Guard Position", "caller hint must win" );
			Require( entities.GetKeyvalue( hint, "nodeid" ) == "9", "caller nodeid must win" );
		} );

		// ---- I/O wiring ----

		Check( "WireButtonToDoor writes the OnPressed -> Open default and resolves cleanly", () =>
		{
			var entities = NewEntities();
			GameplayEntities.PlaceButton( entities, "func_button", new[] { Brush( "btn" ) }, Kv( ("targetname", "button_01") ) );
			GameplayEntities.PlaceDoor( entities, "func_door", new[] { Brush( "door" ) }, Kv( ("targetname", "door_01") ) );
			var io = new EntityIoList();
			var row = GameplayEntities.WireButtonToDoor( entities, io, "button_01", "door_01", 0.5 );
			Require( row.SourceEntity == "button_01" && row.Event == "OnPressed" && row.TargetName == "door_01"
				&& row.Input == "Open" && row.DelaySeconds == 0.5 && row.MaxFires == -1,
				"button->door row fields are wrong: " + row.ToVmfConnection() );
			Require( io.Count == 1, $"io list has {io.Count} rows, expected 1" );
			var resolution = io.ResolveTargets( name => entities.FindByTargetName( name ).Count );
			Require( resolution.IsClean, "button->door wiring must resolve cleanly" );
			var inputs = io.InputsFor( "door_01" );
			Require( inputs.Count == 1 && inputs[0].Input == "Open" && inputs[0].SourceEntity == "button_01",
				"InputsFor('door_01') must show the button's Open input" );
		} );

		Check( "WireTriggerToDoor writes OnStartTouch -> Open", () =>
		{
			var entities = NewEntities();
			GameplayEntities.PlaceTrigger( entities, "trigger_multiple", new[] { Brush( "vol" ) }, Kv( ("targetname", "trigger_01") ) );
			GameplayEntities.PlaceDoor( entities, "func_door", new[] { Brush( "door" ) }, Kv( ("targetname", "door_01") ) );
			var io = new EntityIoList();
			var row = GameplayEntities.WireTriggerToDoor( entities, io, "trigger_01", "door_01" );
			Require( row.Event == "OnStartTouch" && row.Input == "Open" && row.TargetName == "door_01",
				"trigger->door row is wrong: " + row.ToVmfConnection() );
			Require( io.ResolveTargets( name => entities.FindByTargetName( name ).Count ).IsClean,
				"trigger->door wiring must resolve cleanly" );
		} );

		Check( "Wire rejects an unknown output, unknown input, unknown source and unknown target by name", () =>
		{
			var entities = NewEntities();
			GameplayEntities.PlaceButton( entities, "func_button", new[] { Brush( "btn" ) }, Kv( ("targetname", "button_01") ) );
			GameplayEntities.PlaceDoor( entities, "func_door", new[] { Brush( "door" ) }, Kv( ("targetname", "door_01") ) );
			var io = new EntityIoList();
			var badOutput = Throws<TriggersVolumesException>( () =>
				GameplayEntities.Wire( entities, io, "button_01", "OnBananas", "door_01", "Open" ) );
			Require( badOutput.Code == TriggersVolumesException.CodeUnknownOutput,
				$"unknown output must be UnknownOutput, got {badOutput.Code}" );
			var badInput = Throws<TriggersVolumesException>( () =>
				GameplayEntities.Wire( entities, io, "button_01", "OnPressed", "door_01", "Explode" ) );
			Require( badInput.Code == TriggersVolumesException.CodeUnknownInput,
				$"unknown input must be UnknownInput, got {badInput.Code}" );
			var badSource = Throws<TriggersVolumesException>( () =>
				GameplayEntities.Wire( entities, io, "button_99", "OnPressed", "door_01", "Open" ) );
			Require( badSource.Code == TriggersVolumesException.CodeUnknownSourceEntity,
				$"unknown source must be UnknownSourceEntity, got {badSource.Code}" );
			var badTarget = Throws<TriggersVolumesException>( () =>
				GameplayEntities.Wire( entities, io, "button_01", "OnPressed", "door_99", "Open" ) );
			Require( badTarget.Code == TriggersVolumesException.CodeUnknownTargetEntity,
				$"unknown target must be UnknownTargetEntity, got {badTarget.Code}" );
			Require( io.Count == 0, "failed wires must leave the list unchanged" );
		} );

		Check( "Wire rejects wiring a button to a light: the light has no Open input", () =>
		{
			var entities = NewEntities();
			GameplayEntities.PlaceButton( entities, "func_button", new[] { Brush( "btn" ) }, Kv( ("targetname", "button_01") ) );
			entities.PlacePointEntity( "light", new Vec3( 0, 0, 128 ), Kv( ("targetname", "light_01") ) );
			var io = new EntityIoList();
			var error = Throws<TriggersVolumesException>( () =>
				GameplayEntities.Wire( entities, io, "button_01", "OnPressed", "light_01", "Open" ) );
			Require( error.Code == TriggersVolumesException.CodeUnknownInput,
				$"button->light Open must be UnknownInput, got {error.Code}" );
		} );

		Check( "Wire allows an engine special target (!player) without input validation", () =>
		{
			var entities = NewEntities();
			GameplayEntities.PlaceButton( entities, "func_button", new[] { Brush( "btn" ) }, Kv( ("targetname", "button_01") ) );
			var io = new EntityIoList();
			var row = GameplayEntities.Wire( entities, io, "button_01", "OnPressed", "!player", "Toggle" );
			Require( row.TargetName == "!player", "special-target row must keep its target" );
			Require( io.ResolveTargets( name => entities.FindByTargetName( name ).Count ).IsClean,
				"a special target must resolve cleanly" );
		} );

		// ---- Fixtures ----

		Check( "door-button-trigger JSON and VMF fixtures load to the same output rows", () =>
		{
			var fromJson = EntityIoList.FromJson( File.ReadAllText( Path.Combine( FixtureDir(), "door-button-trigger.json" ) ) );
			var fromVmf = EntityIoList.FromVmfConnectionLines( "button_01",
				File.ReadAllLines( Path.Combine( FixtureDir(), "door-button-trigger-connections.vmf" ) ) );
			Require( fromJson.Count == 2, $"JSON fixture has {fromJson.Count} rows, expected 2" );
			Require( fromVmf.Count == 1, $"VMF fixture has {fromVmf.Count} rows, expected 1" );
			Require( fromJson.Outputs[0].Equals( fromVmf.Outputs[0] ), "JSON and VMF fixtures describe different button->door rows" );
			Require( fromJson.Outputs[0].Equals( new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open", 0.5 ) ),
				"button->door fixture row fields are wrong" );
			Require( fromJson.Outputs[1].Equals( new EntityIoOutput( "trigger_01", "OnStartTouch", "door_01", "Open" ) ),
				"trigger->door fixture row fields are wrong" );
			Require( fromJson.Outputs[1].SourceEntity == "trigger_01", "second fixture row must come from the trigger" );
		} );

		// ---- Validator: the testable map ----

		Check( "player start + door + button + trigger, wired, is testable with no issues", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceDoor( entities, "func_door", new[] { Brush( "door" ) }, Kv( ("targetname", "door_01") ) );
			GameplayEntities.PlaceButton( entities, "func_button", new[] { Brush( "btn" ) }, Kv( ("targetname", "button_01") ) );
			GameplayEntities.PlaceTrigger( entities, "trigger_multiple", new[] { Brush( "vol" ) }, Kv( ("targetname", "trigger_01") ) );
			var io = new EntityIoList();
			GameplayEntities.WireButtonToDoor( entities, io, "button_01", "door_01" );
			GameplayEntities.WireTriggerToDoor( entities, io, "trigger_01", "door_01" );
			var issues = GameplayMapValidator.Validate( entities, io );
			Require( issues.Count == 0, "a wired testable map must have no gameplay issues, got: " + string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			Require( GameplayMapValidator.IsTestable( entities, io ), "a wired testable map must be testable" );
			Require( entities.Validate().Count == 0, "MapEntities.Validate must also be clean on the wired map" );
			Require( io.ResolveTargets( name => entities.FindByTargetName( name ).Count ).IsClean, "I/O targets must resolve cleanly" );
		} );

		Check( "a map with no player start errors NoPlayerStart and is not testable", () =>
		{
			var entities = NewEntities();
			GameplayEntities.PlaceDoor( entities, "func_door", new[] { Brush( "door" ) }, Kv( ("targetname", "door_01") ) );
			var io = new EntityIoList();
			var issues = GameplayMapValidator.Validate( entities, io );
			Require( issues.Count == 1 && issues[0].Code == GameplayIssue.CodeNoPlayerStart,
				"expected exactly one NoPlayerStart issue, got: " + string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			Require( issues[0].Severity == MapEntityIssueSeverity.Error, "NoPlayerStart must be an error" );
			Require( !GameplayMapValidator.IsTestable( entities, io ), "a map with no start must not be testable" );
		} );

		Check( "a team spawn does not satisfy the player-start rule; two starts warn", () =>
		{
			var entities = NewEntities();
			entities.PlacePointEntity( "info_player_teamspawn", new Vec3( 0, 0, 0 ), Kv( ("TeamNum", "2") ) );
			var io = new EntityIoList();
			var issues = GameplayMapValidator.Validate( entities, io );
			Require( issues.Any( i => i.Code == GameplayIssue.CodeNoPlayerStart ),
				"a team spawn alone must still error NoPlayerStart" );
			PlaceStart( entities, new Vec3( 64, 0, 0 ) );
			PlaceStart( entities, new Vec3( 128, 0, 0 ) );
			issues = GameplayMapValidator.Validate( entities, io );
			Require( issues.Any( i => i.Code == GameplayIssue.CodeMultiplePlayerStarts && i.Severity == MapEntityIssueSeverity.Warning ),
				"two player starts must warn MultiplePlayerStarts" );
			Require( !issues.Any( i => i.Severity == MapEntityIssueSeverity.Error ),
				"two starts must warn, not error: " + string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			Require( GameplayMapValidator.IsTestable( entities, io ), "two starts must still be testable" );
		} );

		// ---- Validator: trigger warnings ----

		Check( "a trigger_multiple with no target warns TriggerWithoutTarget; wiring it clears the warning", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceTrigger( entities, "trigger_multiple", new[] { Brush( "vol" ) }, Kv( ("targetname", "trigger_01") ) );
			GameplayEntities.PlaceDoor( entities, "func_door", new[] { Brush( "door" ) }, Kv( ("targetname", "door_01") ) );
			var io = new EntityIoList();
			var issues = GameplayMapValidator.Validate( entities, io );
			Require( issues.Count == 1 && issues[0].Code == GameplayIssue.CodeTriggerWithoutTarget
				&& issues[0].Severity == MapEntityIssueSeverity.Warning,
				"an unwired trigger must warn exactly once, got: " + string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			Require( issues[0].EntityId == 2, $"the warning must name entity #2, got #{issues[0].EntityId}" );
			GameplayEntities.WireTriggerToDoor( entities, io, "trigger_01", "door_01" );
			Require( GameplayMapValidator.Validate( entities, io ).Count == 0, "wiring the trigger must clear the warning" );
		} );

		Check( "a trigger with only a filtername still warns: a filter is not a target", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceDoor( entities, "func_door", new[] { Brush( "door" ) }, Kv( ("targetname", "door_01") ) );
			GameplayEntities.PlaceTrigger( entities, "trigger_multiple", new[] { Brush( "vol" ) },
				Kv( ("targetname", "trigger_01"), ("filtername", "door_01") ) );
			var issues = GameplayMapValidator.Validate( entities, new EntityIoList() );
			Require( issues.Any( i => i.Code == GameplayIssue.CodeTriggerWithoutTarget ),
				"a filter-only trigger must still warn TriggerWithoutTarget" );
		} );

		Check( "trigger_teleport warns with no destination and stays quiet with one", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceTrigger( entities, "trigger_teleport", new[] { Brush( "vol" ) }, Kv( ("targetname", "tp_01") ) );
			var issues = GameplayMapValidator.Validate( entities, new EntityIoList() );
			Require( issues.Any( i => i.Code == GameplayIssue.CodeTriggerWithoutTarget && i.EntityId == 2 ),
				"a teleport trigger with no destination must warn" );
			GameplayEntities.PlaceTeleportDestination( entities, new Vec3( 512, 0, 64 ), Kv( ("targetname", "dest_01") ) );
			entities.SetKeyvalue( entities.FindById( 2 ), "target", "dest_01" );
			Require( GameplayMapValidator.Validate( entities, new EntityIoList() ).Count == 0,
				"a teleport trigger with a destination must not warn" );
		} );

		Check( "trigger_teleport naming a missing destination is MapEntities' MissingTarget error, not a gameplay warning", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceTrigger( entities, "trigger_teleport", new[] { Brush( "vol" ) },
				Kv( ("targetname", "tp_01"), ("target", "dest_missing") ) );
			var gameplay = GameplayMapValidator.Validate( entities, new EntityIoList() );
			Require( gameplay.Count == 0, "the gameplay validator must not duplicate a missing-target error, got: "
				+ string.Join( "; ", gameplay.Select( i => i.ToString() ) ) );
			var entityIssues = entities.Validate();
			Require( entityIssues.Count == 1 && entityIssues[0].Code == MapEntityIssue.CodeMissingTarget,
				"MapEntities.Validate must report the missing destination, got: "
				+ string.Join( "; ", entityIssues.Select( i => i.ToString() ) ) );
		} );

		Check( "trigger_hurt uses the registry default damage: unset is fine, 0 warns, 25 is fine", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceTrigger( entities, "trigger_hurt", new[] { Brush( "h1" ) }, Kv( ("targetname", "hurt_default") ) );
			Require( !GameplayMapValidator.Validate( entities, new EntityIoList() )
				.Any( i => i.Code == GameplayIssue.CodeTriggerWithoutEffect ),
				"a hurt trigger with unset damage must use the registry default (10) and not warn" );
			GameplayEntities.PlaceTrigger( entities, "trigger_hurt", new[] { Brush( "h2" ) },
				Kv( ("targetname", "hurt_zero"), ("damage", "0") ) );
			var issues = GameplayMapValidator.Validate( entities, new EntityIoList() );
			Require( issues.Count == 1 && issues[0].Code == GameplayIssue.CodeTriggerWithoutEffect,
				"a hurt trigger with damage 0 must warn exactly once, got: " + string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			GameplayEntities.PlaceTrigger( entities, "trigger_hurt", new[] { Brush( "h3" ) },
				Kv( ("targetname", "hurt_25"), ("damage", "25") ) );
			issues = GameplayMapValidator.Validate( entities, new EntityIoList() );
			Require( issues.Count == 1, "a hurt trigger with damage 25 must not add a warning, got: "
				+ string.Join( "; ", issues.Select( i => i.ToString() ) ) );
		} );

		Check( "trigger_push uses its default speed: unset is fine, 0 warns", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceTrigger( entities, "trigger_push", new[] { Brush( "p1" ) },
				Kv( ("targetname", "push_default"), ("pushdir", "0 90 0") ) );
			Require( !GameplayMapValidator.Validate( entities, new EntityIoList() )
				.Any( i => i.Code == GameplayIssue.CodeTriggerWithoutEffect ),
				"a push trigger with unset speed must use the registry default (100) and not warn" );
			GameplayEntities.PlaceTrigger( entities, "trigger_push", new[] { Brush( "p2" ) },
				Kv( ("targetname", "push_zero"), ("pushdir", "0 90 0"), ("speed", "0") ) );
			var issues = GameplayMapValidator.Validate( entities, new EntityIoList() );
			Require( issues.Any( i => i.Code == GameplayIssue.CodeTriggerWithoutEffect ),
				"a push trigger with speed 0 must warn TriggerWithoutEffect" );
		} );

		Check( "a button with no outputs warns ButtonWithoutTarget; wiring it clears the warning", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceButton( entities, "func_button", new[] { Brush( "btn" ) }, Kv( ("targetname", "button_01") ) );
			GameplayEntities.PlaceDoor( entities, "func_door", new[] { Brush( "door" ) }, Kv( ("targetname", "door_01") ) );
			var io = new EntityIoList();
			var issues = GameplayMapValidator.Validate( entities, io );
			Require( issues.Any( i => i.Code == GameplayIssue.CodeButtonWithoutTarget ),
				"an unwired button must warn ButtonWithoutTarget" );
			GameplayEntities.WireButtonToDoor( entities, io, "button_01", "door_01" );
			Require( GameplayMapValidator.Validate( entities, io ).Count == 0, "wiring the button must clear the warning" );
		} );

		Check( "a tracktrain with no route warns MoverWithoutRoute; giving it a path_track clears it", () =>
		{
			var entities = NewEntities();
			PlaceStart( entities, new Vec3( 0, 0, 0 ) );
			var train = entities.PlaceBrushEntity( "func_tracktrain", new[] { Brush( "train" ) }, Kv( ("targetname", "train_01") ) );
			var issues = GameplayMapValidator.Validate( entities, new EntityIoList() );
			Require( issues.Any( i => i.Code == GameplayIssue.CodeMoverWithoutRoute ),
				"a tracktrain with no target must warn MoverWithoutRoute" );
			var track = entities.PlacePointEntity( "path_track", new Vec3( 0, 0, 0 ), Kv( ("targetname", "track_01") ) );
			entities.SetKeyvalue( train, "target", "track_01" );
			Require( GameplayMapValidator.Validate( entities, new EntityIoList() ).Count == 0,
				"a tracktrain with a route must not warn" );
			Require( entities.Validate().Count == 0, "the train -> path_track target must validate cleanly" );
			Require( track.ClassName == "path_track", "path_track placement failed" );
		} );

		// ---- Spawns / items / weapons / NPCs / movers as placed entities ----

		Check( "NPC nodes are placed and listed; an NPC is placed beside them", () =>
		{
			var entities = NewEntities();
			GameplayEntities.PlaceNode( entities, new Vec3( 0, 0, 0 ) );
			GameplayEntities.PlaceNodeHint( entities, new Vec3( 128, 0, 0 ), "Guard Position" );
			GameplayEntities.PlaceNode( entities, new Vec3( 256, 0, 0 ) );
			var npc = entities.PlacePointEntity( "npc_citizen", new Vec3( 128, 64, 0 ),
				Kv( ("targetname", "citizen_01"), ("squadname", "resistance") ) );
			Require( npc.ClassName == "npc_citizen" && npc.TargetName == "citizen_01", "NPC placement failed" );
			Require( entities.GetKeyvalue( npc, "squadname" ) == "resistance", "squadname did not round-trip" );
			var nodes = GameplayEntities.ListNodes( entities );
			Require( nodes.Count == 3, $"ListNodes found {nodes.Count}, expected 3" );
			Require( nodes[1].Hint == "Guard Position", $"second node hint is '{nodes[1].Hint}'" );
			Require( entities.FindByClass( "npc_citizen" ).Count == 1, "FindByClass('npc_citizen') must find the NPC" );
			Require( entities.FindByClass( "info_node" ).Count == 2, "FindByClass('info_node') must find both nodes" );
			var soldier = entities.PlacePointEntity( "npc_combine_s", new Vec3( 512, 0, 0 ), Kv( ("targetname", "soldier_01") ) );
			Require( soldier.Kind == EntityKind.Point, "npc_combine_s must be a point entity" );
		} );

		Check( "spawns, items and weapons place after the merge and are findable by class", () =>
		{
			var entities = NewEntities();
			var team = entities.PlacePointEntity( "info_player_teamspawn", new Vec3( 0, 0, 0 ), Kv( ("TeamNum", "2") ) );
			Require( entities.GetKeyvalue( team, "TeamNum" ) == "2", "TeamNum did not round-trip" );
			entities.PlacePointEntity( "info_player_deathmatch", new Vec3( 64, 0, 0 ) );
			var kit = entities.PlacePointEntity( "item_healthkit", new Vec3( 128, 0, 0 ) );
			var gun = entities.PlacePointEntity( "weapon_pistol", new Vec3( 192, 0, 0 ) );
			var ammo = entities.PlacePointEntity( "item_ammo_smg1", new Vec3( 256, 0, 0 ) );
			Require( kit.Kind == EntityKind.Point && gun.Kind == EntityKind.Point && ammo.Kind == EntityKind.Point,
				"items/weapons must be point entities" );
			Require( entities.FindByClass( "item_healthkit" ).Count == 1, "item_healthkit must be findable" );
			Require( entities.FindByClass( "weapon_pistol" ).Count == 1, "weapon_pistol must be findable" );
			Require( entities.FindByClass( "info_player_teamspawn" ).Count == 1, "team spawn must be findable" );
			Require( entities.FindByClass( "item_ammo_smg1" ).Count == 1, "ammo must be findable" );
		} );

		Check( "movers place with their keyvalues: movelinear, plat and tracktrain + path_track route", () =>
		{
			var entities = NewEntities();
			var mover = entities.PlaceBrushEntity( "func_movelinear", new[] { Brush( "m" ) },
				Kv( ("targetname", "lift_01"), ("movedir", "0 0 1"), ("movedistance", "256"), ("speed", "64") ) );
			Require( entities.GetKeyvalue( mover, "movedistance" ) == "256", "movedistance did not round-trip" );
			Require( entities.GetKeyvalue( mover, "movedir" ) == "0 0 1", "movedir did not round-trip" );
			var plat = entities.PlaceBrushEntity( "func_plat", new[] { Brush( "p" ) },
				Kv( ("targetname", "plat_01"), ("height", "128") ) );
			Require( entities.GetKeyvalue( plat, "height" ) == "128", "plat height did not round-trip" );
			var track = entities.PlacePointEntity( "path_track", new Vec3( 0, 0, 0 ), Kv( ("targetname", "track_01") ) );
			var train = entities.PlaceBrushEntity( "func_tracktrain", new[] { Brush( "t" ) },
				Kv( ("targetname", "train_01"), ("target", "track_01"), ("speed", "50") ) );
			Require( entities.Validate().Count == 0, "mover + route map must validate cleanly, got: "
				+ string.Join( "; ", entities.Validate().Select( i => i.ToString() ) ) );
			Require( track.Id != train.Id, "track and train must be distinct entities" );
			var registry = entities.Registry;
			Require( registry.Get( "func_tracktrain" ).FindInput( "StartForward" ) is not null, "tracktrain must accept StartForward" );
			Require( registry.Get( "func_movelinear" ).FindInput( "SetPosition" ) is not null, "movelinear must accept SetPosition" );
			Require( registry.Get( "func_plat" ).FindInput( "GoUp" ) is not null, "plat must accept GoUp" );
			Require( registry.Get( "func_tracktrain" ).Outputs.Length == 0, "tracktrain declares no outputs (path_tracks fire OnPass)" );
		} );

		Check( "a tracktrain naming a missing path_track is a MissingTarget error from MapEntities.Validate", () =>
		{
			var entities = NewEntities();
			entities.PlaceBrushEntity( "func_tracktrain", new[] { Brush( "t" ) },
				Kv( ("targetname", "train_01"), ("target", "track_missing") ) );
			var issues = entities.Validate();
			Require( issues.Count == 1 && issues[0].Code == MapEntityIssue.CodeMissingTarget,
				"a missing route target must be one MissingTarget error, got: "
				+ string.Join( "; ", issues.Select( i => i.ToString() ) ) );
		} );
	}
}
