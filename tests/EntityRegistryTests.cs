using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Tests for the FGD-like entity registry (hammer-entity-registry-fgd-json):
/// core registry load, class lookup, keyvalue type checking, named-error
/// validation, and extension by a second JSON file with no code changes.
/// </summary>
internal static class EntityRegistryTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "entityregistry", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		string RepoRoot()
		{
			string? dir = AppContext.BaseDirectory;
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
				dir = Directory.GetParent( dir )?.FullName;
			}
			dir = Directory.GetCurrentDirectory();
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
				dir = Directory.GetParent( dir )?.FullName;
			}
			throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
		}

		string CorePath() => Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" );
		string Fixture( string name ) => Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-entity-registry-fgd-json", name );
		EntityRegistry LoadCore() => EntityRegistry.Load( CorePath() );

		Check( "core registry loads with 15 classes", () =>
		{
			var reg = LoadCore();
			Require( reg.Name == "source1-core", $"core registry name is '{reg.Name}', expected 'source1-core'" );
			Require( reg.SchemaVersion == 1, $"schema version is {reg.SchemaVersion}, expected 1" );
			Require( reg.Count == 15, $"core registry has {reg.Count} classes, expected 15" );
		} );

		Check( "core Source-1 set ships every expected class", () =>
		{
			var reg = LoadCore();
			string[] expected =
			{
				"info_player_start", "light", "light_spot",
				"prop_static", "prop_dynamic", "prop_physics",
				"trigger_multiple", "trigger_once", "trigger_hurt",
				"func_door", "func_button",
				"logic_relay", "logic_timer", "logic_auto",
				"sky_camera",
			};
			foreach ( string name in expected )
				Require( reg.Contains( name ), $"core registry is missing '{name}'" );
		} );

		Check( "func_door lookup returns brush class with its inputs and outputs", () =>
		{
			var reg = LoadCore();
			var door = reg.Find( "func_door" );
			Require( door is not null, "func_door not found" );
			Require( door.Kind == EntityKind.Brush, $"func_door kind is {door.Kind}, expected Brush" );
			foreach ( string input in new[] { "Open", "Close", "Toggle", "Lock", "Unlock" } )
				Require( door.FindInput( input ) is not null, $"func_door is missing input '{input}'" );
			foreach ( string output in new[] { "OnFullyOpen", "OnFullyClosed", "OnLockedUse" } )
				Require( door.FindOutput( output ) is not null, $"func_door is missing output '{output}'" );
			Require( door.FindKeyValue( "speed" )?.Type == KeyValueType.Float, "func_door 'speed' must be float" );
			Require( door.FindKeyValue( "noise1" )?.Type == KeyValueType.Sound, "func_door 'noise1' must be sound" );
		} );

		Check( "class lookup is case-insensitive", () =>
		{
			var reg = LoadCore();
			Require( reg.Find( "FUNC_DOOR" )?.Name == "func_door", "FUNC_DOOR lookup failed" );
			Require( reg.Find( "Light_Spot" )?.Name == "light_spot", "Light_Spot lookup failed" );
			Require( reg.TryGet( "LOGIC_RELAY", out var relay ) && relay.Name == "logic_relay", "TryGet LOGIC_RELAY failed" );
		} );

		Check( "light_spot keyvalues and helpers", () =>
		{
			var reg = LoadCore();
			var spot = reg.Get( "light_spot" );
			Require( spot.Kind == EntityKind.Point, "light_spot must be a point entity" );
			Require( spot.FindKeyValue( "_light" )?.Type == KeyValueType.Colour, "light_spot '_light' must be colour" );
			Require( spot.FindKeyValue( "target" )?.Type == KeyValueType.Target, "light_spot 'target' must be target" );
			Require( spot.FindKeyValue( "_cone" )?.Type == KeyValueType.Float, "light_spot '_cone' must be float" );
			Require( spot.HasHelper( EntityHelperKind.Colour ), "light_spot must have a colour helper" );
			Require( spot.HasHelper( EntityHelperKind.Target ), "light_spot must have a target helper" );
		} );

		Check( "point vs brush kinds", () =>
		{
			var reg = LoadCore();
			Require( reg.Get( "info_player_start" ).Kind == EntityKind.Point, "info_player_start must be Point" );
			Require( reg.Get( "prop_static" ).Kind == EntityKind.Point, "prop_static must be Point" );
			Require( reg.Get( "trigger_multiple" ).Kind == EntityKind.Brush, "trigger_multiple must be Brush" );
			Require( reg.Get( "func_button" ).Kind == EntityKind.Brush, "func_button must be Brush" );
			Require( reg.Get( "logic_auto" ).Kind == EntityKind.Point, "logic_auto must be Point" );
		} );

		Check( "spawnflags are declared per class", () =>
		{
			var reg = LoadCore();
			var door = reg.Get( "func_door" );
			var startsOpen = door.Spawnflags.FirstOrDefault( f => f.Value == 1 );
			Require( startsOpen is not null && startsOpen.Name == "Starts Open", "func_door must have spawnflag 1 'Starts Open'" );
			var useOnly = door.Spawnflags.FirstOrDefault( f => f.Value == 256 );
			Require( useOnly is not null && useOnly.Name == "Use Only", "func_door must have spawnflag 256 'Use Only'" );
			var trigger = reg.Get( "trigger_multiple" );
			Require( trigger.Spawnflags.Any( f => f.Value == 1 && f.Name == "Clients" ), "trigger_multiple must have spawnflag 1 'Clients'" );
		} );

		Check( "unknown class lookup is null and Get throws UnknownEntityClass", () =>
		{
			var reg = LoadCore();
			Require( reg.Find( "npc_does_not_exist" ) is null, "unknown class Find must return null" );
			Require( !reg.TryGet( "npc_does_not_exist", out _ ), "unknown class TryGet must return false" );
			try { reg.Get( "npc_does_not_exist" ); throw new Exception( "Get did not throw for unknown class" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeUnknownEntityClass, $"Get error code is '{ex.Code}', expected UnknownEntityClass" ); }
		} );

		Check( "unknown keyvalue is rejected with UnknownKeyValue", () =>
		{
			var reg = LoadCore();
			var errors = reg.ValidateEntity( "func_door", new Dictionary<string, string> { { "speed", "100" }, { "bogus_key", "1" } } );
			Require( errors.Count == 1, $"expected 1 error, got {errors.Count}" );
			Require( errors[0].Code == EntityRegistry.CodeUnknownKeyValue, $"error code is '{errors[0].Code}', expected UnknownKeyValue" );
			Require( errors[0].KeyName == "bogus_key", $"error key is '{errors[0].KeyName}', expected 'bogus_key'" );
			try { reg.CheckEntity( "func_door", new Dictionary<string, string> { { "bogus_key", "1" } } ); throw new Exception( "CheckEntity did not throw for unknown key" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeUnknownKeyValue, $"CheckEntity code is '{ex.Code}', expected UnknownKeyValue" ); }
		} );

		Check( "unknown class is rejected with UnknownEntityClass", () =>
		{
			var reg = LoadCore();
			var errors = reg.ValidateEntity( "npc_does_not_exist", new Dictionary<string, string> { { "targetname", "ghost" } } );
			Require( errors.Count == 1, $"expected 1 error, got {errors.Count}" );
			Require( errors[0].Code == EntityRegistry.CodeUnknownEntityClass, $"error code is '{errors[0].Code}', expected UnknownEntityClass" );
		} );

		Check( "invalid-unknown-key fixture is rejected", () =>
		{
			var reg = LoadCore();
			var errors = reg.ValidateEntityJson( File.ReadAllText( Fixture( "invalid-unknown-key.json" ) ) );
			Require( errors.Count == 1, $"expected 1 error, got {errors.Count}" );
			Require( errors[0].Code == EntityRegistry.CodeUnknownKeyValue, $"fixture error code is '{errors[0].Code}', expected UnknownKeyValue" );
			Require( errors[0].ClassName == "func_door", $"fixture error class is '{errors[0].ClassName}', expected 'func_door'" );
		} );

		Check( "invalid-unknown-class fixture is rejected", () =>
		{
			var reg = LoadCore();
			var errors = reg.ValidateEntityJson( File.ReadAllText( Fixture( "invalid-unknown-class.json" ) ) );
			Require( errors.Count == 1, $"expected 1 error, got {errors.Count}" );
			Require( errors[0].Code == EntityRegistry.CodeUnknownEntityClass, $"fixture error code is '{errors[0].Code}', expected UnknownEntityClass" );
		} );

		Check( "valid entity passes validation including implicit base keyvalues", () =>
		{
			var reg = LoadCore();
			var errors = reg.ValidateEntity( "func_door", new Dictionary<string, string>
			{
				{ "targetname", "door_01" }, { "parentname", "house" }, { "angles", "0 90 0" },
				{ "spawnflags", "1" }, { "speed", "100" }, { "wait", "4" }, { "lip", "8" },
				{ "dmg", "0" }, { "noise1", "sounds/door_move.wav" },
			} );
			Require( errors.Count == 0, $"valid func_door rejected: {string.Join( "; ", errors.Select( e => e.ToString() ) )}" );
			var lightErrors = reg.ValidateEntity( "light", new Dictionary<string, string> { { "_light", "255 128 0" }, { "_distance", "512.5" }, { "style", "0" } } );
			Require( lightErrors.Count == 0, $"valid light rejected: {string.Join( "; ", lightErrors.Select( e => e.ToString() ) )}" );
		} );

		Check( "keyvalue type checking accepts valid and rejects invalid values", () =>
		{
			Require( EntityRegistry.TryCheckValue( KeyValueType.String, "anything goes", out _ ), "string must accept anything" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Int, "-12", out _ ), "int must accept -12" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Int, "12.5", out _ ), "int must reject 12.5" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Int, "abc", out _ ), "int must reject abc" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Float, "1.5e3", out _ ), "float must accept 1.5e3" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Float, "NaN", out _ ), "float must reject NaN" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Colour, "255 128 0", out _ ), "colour must accept '255 128 0'" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Colour, "255 128 0 255", out _ ), "colour must accept RGBA" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Colour, "300 0 0", out _ ), "colour must reject 300" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Colour, "255 0", out _ ), "colour must reject 2 components" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Angle, "90", out _ ), "angle must accept a single yaw" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Angle, "0 90 0", out _ ), "angle must accept pitch yaw roll" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Angle, "0 90", out _ ), "angle must reject 2 components" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Target, "door_01", out _ ), "target must accept door_01" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Target, "door 01", out _ ), "target must reject whitespace" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Target, "", out _ ), "target must reject empty" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Model, "models/props/crate.mdl", out _ ), "model must accept .mdl" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Model, "models/props/crate.txt", out _ ), "model must reject .txt" );
			Require( EntityRegistry.TryCheckValue( KeyValueType.Sound, "sounds/door.wav", out _ ), "sound must accept .wav" );
			Require( !EntityRegistry.TryCheckValue( KeyValueType.Sound, "sounds/door.txt", out _ ), "sound must reject .txt" );
		} );

		Check( "mistyped values are rejected with InvalidKeyValueType", () =>
		{
			var reg = LoadCore();
			var colour = reg.ValidateEntity( "light", new Dictionary<string, string> { { "_light", "300 0 0" } } );
			Require( colour.Count == 1 && colour[0].Code == EntityRegistry.CodeInvalidKeyValueType, "bad colour must be InvalidKeyValueType" );
			var model = reg.ValidateEntity( "prop_static", new Dictionary<string, string> { { "model", "props/crate.txt" } } );
			Require( model.Count == 1 && model[0].Code == EntityRegistry.CodeInvalidKeyValueType, "bad model must be InvalidKeyValueType" );
			var choice = reg.ValidateEntity( "prop_static", new Dictionary<string, string> { { "solid", "5" } } );
			Require( choice.Count == 1 && choice[0].Code == EntityRegistry.CodeInvalidKeyValueType, "out-of-choice solid=5 must be InvalidKeyValueType" );
			var okChoice = reg.ValidateEntity( "prop_static", new Dictionary<string, string> { { "solid", "6" } } );
			Require( okChoice.Count == 0, "in-choice solid=6 must pass" );
		} );

		Check( "second registry file adds a custom entity without code changes", () =>
		{
			var reg = LoadCore();
			reg.LoadAdditional( Fixture( "custom-entity.json" ) );
			Require( reg.Count == 16, $"after LoadAdditional count is {reg.Count}, expected 16" );
			var gadget = reg.Get( "prop_custom_gadget" );
			Require( gadget.Kind == EntityKind.Point, "prop_custom_gadget must be Point" );
			Require( gadget.FindKeyValue( "gadget_power" )?.Type == KeyValueType.Float, "gadget_power must be float" );
			Require( gadget.FindKeyValue( "model" )?.Type == KeyValueType.Model, "gadget model must be model type" );
			Require( gadget.FindInput( "Charge" ) is not null, "prop_custom_gadget must have input Charge" );
			Require( gadget.FindOutput( "OnFullyCharged" ) is not null, "prop_custom_gadget must have output OnFullyCharged" );
			var errors = reg.ValidateEntity( "prop_custom_gadget", new Dictionary<string, string> { { "model", "models/gadget.mdl" }, { "gadget_power", "2.5" } } );
			Require( errors.Count == 0, $"custom gadget entity rejected: {string.Join( "; ", errors.Select( e => e.ToString() ) )}" );
			Require( reg.Contains( "func_door" ), "core classes must survive LoadAdditional" );
		} );

		Check( "duplicate class on merge is rejected with DuplicateClass", () =>
		{
			var reg = LoadCore();
			try { reg.LoadAdditional( CorePath() ); throw new Exception( "LoadAdditional did not throw for duplicate classes" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeDuplicateClass, $"error code is '{ex.Code}', expected DuplicateClass" ); }
			Require( reg.Count == 15, $"failed merge must not change the registry (count {reg.Count})" );
			var other = EntityRegistry.LoadFromJson( "{\"schemaVersion\":1,\"name\":\"dup\",\"classes\":[{\"name\":\"light\",\"kind\":\"point\"}]}", "dup" );
			try { reg.Merge( other ); throw new Exception( "Merge did not throw for duplicate class" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeDuplicateClass, $"merge error code is '{ex.Code}', expected DuplicateClass" ); }
		} );

		Check( "malformed registries are rejected with named errors", () =>
		{
			try { EntityRegistry.LoadFromJson( "{not json", "bad" ); throw new Exception( "malformed JSON did not throw" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeInvalidRegistry, $"malformed JSON code is '{ex.Code}', expected InvalidRegistry" ); }
			try { EntityRegistry.LoadFromJson( "{\"schemaVersion\":99,\"classes\":[]}", "bad" ); throw new Exception( "bad schemaVersion did not throw" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeInvalidRegistry, $"bad schema code is '{ex.Code}', expected InvalidRegistry" ); }
			try { EntityRegistry.LoadFromJson( "{\"schemaVersion\":1,\"classes\":[{\"name\":\"x\",\"kind\":\"volume\"}]}", "bad" ); throw new Exception( "bad kind did not throw" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeInvalidEntityKind, $"bad kind code is '{ex.Code}', expected InvalidEntityKind" ); }
			try { EntityRegistry.LoadFromJson( File.ReadAllText( Fixture( "invalid-registry-unknown-type.json" ) ), "bad-type" ); throw new Exception( "unknown keyvalue type did not throw" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeUnknownKeyValueType, $"unknown type code is '{ex.Code}', expected UnknownKeyValueType" ); }
			try { EntityRegistry.LoadFromJson( "{\"schemaVersion\":1,\"classes\":[{\"name\":\"a\",\"kind\":\"point\"},{\"name\":\"A\",\"kind\":\"point\"}]}", "bad" ); throw new Exception( "duplicate class in one file did not throw" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeDuplicateClass, $"in-file duplicate code is '{ex.Code}', expected DuplicateClass" ); }
		} );

		Check( "registry directory loads its files", () =>
		{
			var reg = EntityRegistry.LoadDirectory( Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry" ) );
			Require( reg.Count == 15, $"LoadDirectory count is {reg.Count}, expected 15" );
			Require( reg.Contains( "func_door" ) && reg.Contains( "logic_auto" ), "LoadDirectory must include core classes" );
		} );

		Check( "helpers are declared per class", () =>
		{
			var reg = LoadCore();
			Require( reg.Get( "light" ).HasHelper( EntityHelperKind.Colour ), "light must have a colour helper" );
			Require( reg.Get( "info_player_start" ).HasHelper( EntityHelperKind.Origin ), "info_player_start must have an origin helper" );
			Require( reg.Get( "info_player_start" ).HasHelper( EntityHelperKind.Angles ), "info_player_start must have an angles helper" );
			Require( reg.Get( "func_door" ).HasHelper( EntityHelperKind.Angles ), "func_door must have an angles helper" );
			Require( !reg.Get( "logic_auto" ).HasHelper( EntityHelperKind.Colour ), "logic_auto must not have a colour helper" );
		} );
	}
}
