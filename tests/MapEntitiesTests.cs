using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Tests for entity place/edit/validate + tie/untie (hammer-entity-place-edit-validate):
/// point/brush placement against the real core registry, origin/angles helpers,
/// registry type checks with named rejections, tie/untie preserving brush data,
/// target-name validation (missing target = error, duplicate targetname = warning),
/// and search by class/targetname. Brushes are TestBrush stand-ins: MapBrush
/// (hammer-brush-core-plane-based-brushes) has not landed yet, so the generic
/// TBrush seam is exercised with a small payload type; the same calls take
/// IReadOnlyList&lt;MapBrush&gt; once it lands (see MapEntities.cs header).
/// </summary>
internal static class MapEntitiesTests
{
	sealed class TestBrush
	{
		public string Name = "";
		public float Volume;
		public TestBrush( string name, float volume ) { Name = name; Volume = volume; }
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mapentities", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		MapEntityException ThrowsMap( Action action )
		{
			try { action(); }
			catch ( MapEntityException ex ) { return ex; }
			throw new Exception( "expected MapEntityException, got silent success" );
		}
		EntityRegistryException ThrowsRegistry( Action action )
		{
			try { action(); }
			catch ( EntityRegistryException ex ) { return ex; }
			throw new Exception( "expected EntityRegistryException, got silent success" );
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
		string Fixture( string name ) => Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-entity-place-edit-validate", name );
		EntityRegistry LoadCore() => EntityRegistry.Load( CorePath() );
		MapEntities NewEntities() => new MapEntities( LoadCore() );
		TestBrush Brush( string name ) => new TestBrush( name, 1.0f );

		Check( "place info_player_start at a position with defaults", () =>
		{
			var entities = NewEntities();
			var start = entities.PlacePointEntity( "info_player_start", new Vec3( 0, 0, 0 ) );
			Require( start.Id == 1, $"first id is {start.Id}, expected 1" );
			Require( start.ClassName == "info_player_start", $"class is '{start.ClassName}'" );
			Require( start.Kind == EntityKind.Point, $"kind is {start.Kind}, expected Point" );
			Require( !start.IsBrushEntity, "player start must not be a brush entity" );
			Require( start.Origin.AlmostEquals( new Vec3( 0, 0, 0 ) ), $"origin is {start.Origin}" );
			Require( start.Angles.AlmostEquals( Vec3.Zero ), $"angles are {start.Angles}, expected (0, 0, 0)" );
			Require( start.Brushes.Count == 0, "point entity must have no brushes" );
			Require( start.TargetName == "", $"targetname is '{start.TargetName}', expected empty" );
			Require( entities.Count == 1, $"count is {entities.Count}, expected 1" );
			entities.SetOrigin( start, new Vec3( 64, 64, 0 ) );
			Require( start.Origin.AlmostEquals( new Vec3( 64, 64, 0 ) ), "SetOrigin did not move the entity" );
			Require( entities.Remove( start ), "Remove must report true for an owned entity" );
			Require( entities.Count == 0, "Remove must drop the entity" );
			Require( !entities.Remove( start ), "Remove must report false the second time" );
		} );

		Check( "place light with colour and float keyvalues", () =>
		{
			var entities = NewEntities();
			var light = entities.PlacePointEntity( "light", new Vec3( 128, 64, 128 ),
				new Dictionary<string, string> { ["_light"] = "255 200 100", ["_distance"] = "512" } );
			Require( entities.GetKeyvalue( light, "_light" ) == "255 200 100", "colour keyvalue did not round-trip" );
			Require( entities.GetKeyvalue( light, "_distance" ) == "512", "float keyvalue did not round-trip" );
			Require( entities.FindByClass( "light" ).Count == 1, "FindByClass('light') must find the light" );
			Require( entities.FindByClass( "light" )[0].Id == light.Id, "FindByClass returned the wrong entity" );
		} );

		Check( "place prop_static with a model path; a bad model extension is rejected", () =>
		{
			var entities = NewEntities();
			var prop = entities.PlacePointEntity( "prop_static", new Vec3( 0, 0, 0 ),
				new Dictionary<string, string> { ["model"] = "models/props_c17/oildrum001.mdl", ["skin"] = "2" } );
			Require( entities.GetKeyvalue( prop, "model" ) == "models/props_c17/oildrum001.mdl", "model keyvalue did not round-trip" );
			var error = ThrowsRegistry( () => entities.PlacePointEntity( "prop_static", new Vec3( 0, 0, 0 ),
				new Dictionary<string, string> { ["model"] = "models/crate.txt" } ) );
			Require( error.Code == EntityRegistry.CodeInvalidKeyValueType,
				$"bad model must be InvalidKeyValueType, got {error.Code}" );
		} );

		Check( "angles placement mirrors into keyvalues and SetAngles keeps them in sync", () =>
		{
			var entities = NewEntities();
			var light = entities.PlacePointEntity( "light", new Vec3( 0, 0, 64 ), new Dictionary<string, string>(), new Vec3( 0, 90, 0 ) );
			Require( light.Angles.AlmostEquals( new Vec3( 0, 90, 0 ) ), $"angles are {light.Angles}, expected (0, 90, 0)" );
			Require( entities.GetKeyvalue( light, "angles" ) == "0 90 0", $"angles keyvalue is '{entities.GetKeyvalue( light, "angles" )}', expected '0 90 0'" );
			entities.SetAngles( light, new Vec3( 0, 180, 0 ) );
			Require( light.Angles.AlmostEquals( new Vec3( 0, 180, 0 ) ), "SetAngles did not update the property" );
			Require( entities.GetKeyvalue( light, "angles" ) == "0 180 0", "SetAngles did not update the keyvalue" );
			entities.SetKeyvalue( light, "angles", "0 270 0" );
			Require( light.Angles.AlmostEquals( new Vec3( 0, 270, 0 ) ), "SetKeyvalue('angles') did not update the property" );
		} );

		Check( "origin and angles format round-trips; malformed strings are named errors", () =>
		{
			var origin = new Vec3( 128.5f, -64f, 0f );
			Require( MapEntities.FormatOrigin( origin ) == "128.5 -64 0", $"FormatOrigin gave '{MapEntities.FormatOrigin( origin )}'" );
			Require( MapEntities.ParseOrigin( MapEntities.FormatOrigin( origin ) ).AlmostEquals( origin ), "origin round-trip moved" );
			Require( MapEntities.FormatAngles( new Vec3( 0, 90, 0 ) ) == "0 90 0", "FormatAngles gave the wrong string" );
			Require( MapEntities.TryParseOrigin( "1 2 3", out var parsed ) && parsed.AlmostEquals( new Vec3( 1, 2, 3 ) ), "TryParseOrigin('1 2 3') failed" );
			Require( !MapEntities.TryParseOrigin( "nope", out _ ), "TryParseOrigin must fail on 'nope'" );
			var badOrigin = ThrowsMap( () => MapEntities.ParseOrigin( "1 2" ) );
			Require( badOrigin.Code == MapEntityException.CodeInvalidOrigin, $"short origin must be InvalidOrigin, got {badOrigin.Code}" );
			var badAngles = ThrowsMap( () => MapEntities.ParseAngles( "0 90" ) );
			Require( badAngles.Code == MapEntityException.CodeInvalidAngles, $"two-part angles must be InvalidAngles, got {badAngles.Code}" );
		} );

		Check( "unknown class is rejected with the registry's named code", () =>
		{
			var entities = NewEntities();
			var error = ThrowsRegistry( () => entities.PlacePointEntity( "not_a_class", Vec3.Zero ) );
			Require( error.Code == EntityRegistry.CodeUnknownEntityClass,
				$"unknown class must be UnknownEntityClass, got {error.Code}" );
		} );

		Check( "point/brush kind mismatch is rejected on both placement paths", () =>
		{
			var entities = NewEntities();
			var asPoint = ThrowsMap( () => entities.PlacePointEntity( "func_door", Vec3.Zero ) );
			Require( asPoint.Code == MapEntityException.CodeNotPointEntity,
				$"func_door as point must be NotPointEntity, got {asPoint.Code}" );
			var asBrush = ThrowsMap( () => entities.PlaceBrushEntity( "light", new[] { Brush( "b1" ) } ) );
			Require( asBrush.Code == MapEntityException.CodeNotBrushEntity,
				$"light as brush must be NotBrushEntity, got {asBrush.Code}" );
		} );

		Check( "tie two brushes to func_door preserves the brush references", () =>
		{
			var entities = NewEntities();
			var left = Brush( "door-left" );
			var right = Brush( "door-right" );
			var door = entities.Tie( "func_door", new[] { left, right },
				new Dictionary<string, string> { ["speed"] = "100", ["targetname"] = "door_01" } );
			Require( door.IsBrushEntity, "tied func_door must be a brush entity" );
			Require( door.Brushes.Count == 2, $"tied door has {door.Brushes.Count} brushes, expected 2" );
			Require( ReferenceEquals( door.Brushes[0], left ) && ReferenceEquals( door.Brushes[1], right ),
				"tie must keep the exact brush references, in order" );
			var typed = door.GetBrushes<TestBrush>();
			Require( ReferenceEquals( typed[0], left ) && ReferenceEquals( typed[1], right ),
				"GetBrushes<TestBrush> must return the same references" );
			Require( door.TargetName == "door_01", $"targetname is '{door.TargetName}', expected 'door_01'" );
			Require( entities.GetKeyvalue( door, "speed" ) == "100", "speed keyvalue did not survive the tie" );
			Require( entities.FindByClass( "func_door" ).Count == 1, "FindByClass('func_door') must find the tied door" );
		} );

		Check( "place brush entity directly; an empty brush list is rejected", () =>
		{
			var entities = NewEntities();
			var button = entities.PlaceBrushEntity( "func_button", new[] { Brush( "button-face" ) } );
			Require( button.IsBrushEntity && button.Brushes.Count == 1, "placed func_button must hold one brush" );
			var error = ThrowsMap( () => entities.PlaceBrushEntity( "func_button", Array.Empty<TestBrush>() ) );
			Require( error.Code == MapEntityException.CodeNoBrushes,
				$"empty brush list must be NoBrushes, got {error.Code}" );
		} );

		Check( "untie returns the same brushes and removes the entity", () =>
		{
			var entities = NewEntities();
			var left = Brush( "door-left" );
			var right = Brush( "door-right" );
			var door = entities.Tie( "func_door", new[] { left, right } );
			var freed = entities.Untie<TestBrush>( door );
			Require( freed.Count == 2 && ReferenceEquals( freed[0], left ) && ReferenceEquals( freed[1], right ),
				"untie must return the exact tied brush references" );
			Require( entities.Count == 0, "untie must remove the entity" );
			Require( entities.FindById( door.Id ) is null, "untied entity must be gone by id" );
			var gone = ThrowsMap( () => entities.Untie<TestBrush>( door ) );
			Require( gone.Code == MapEntityException.CodeUnknownEntity,
				$"second untie must be UnknownEntity, got {gone.Code}" );
			var light = entities.PlacePointEntity( "light", Vec3.Zero );
			var neverTied = ThrowsMap( () => entities.Untie<TestBrush>( light ) );
			Require( neverTied.Code == MapEntityException.CodeNotTied,
				$"untie of a point entity must be NotTied, got {neverTied.Code}" );
		} );

		Check( "a mistyped keyvalue is rejected and the stored value is unchanged", () =>
		{
			var entities = NewEntities();
			var door = entities.Tie( "func_door", new[] { Brush( "d" ) },
				new Dictionary<string, string> { ["speed"] = "100" } );
			var error = ThrowsRegistry( () => entities.SetKeyvalue( door, "speed", "fast" ) );
			Require( error.Code == EntityRegistry.CodeInvalidKeyValueType,
				$"speed 'fast' must be InvalidKeyValueType, got {error.Code}" );
			Require( entities.GetKeyvalue( door, "speed" ) == "100", "rejected set must leave the stored value unchanged" );
		} );

		Check( "unknown keyvalue is rejected on set and get; a known-but-unset key reads null", () =>
		{
			var entities = NewEntities();
			var light = entities.PlacePointEntity( "light", Vec3.Zero );
			var setError = ThrowsRegistry( () => entities.SetKeyvalue( light, "nope", "1" ) );
			Require( setError.Code == EntityRegistry.CodeUnknownKeyValue,
				$"unknown key must be UnknownKeyValue, got {setError.Code}" );
			Require( entities.GetKeyvalue( light, "_distance" ) is null, "known-but-unset key must read null" );
			var getError = ThrowsRegistry( () => entities.GetKeyvalue( light, "nope" ) );
			Require( getError.Code == EntityRegistry.CodeUnknownKeyValue,
				$"get of an unknown key must be UnknownKeyValue, got {getError.Code}" );
		} );

		Check( "origin and classname behave as keyvalue helpers; classname is read-only", () =>
		{
			var entities = NewEntities();
			var light = entities.PlacePointEntity( "light", new Vec3( 0, 0, 64 ) );
			entities.SetKeyvalue( light, "origin", "10 20 30" );
			Require( light.Origin.AlmostEquals( new Vec3( 10, 20, 30 ) ), "SetKeyvalue('origin') did not move the entity" );
			Require( entities.GetKeyvalue( light, "origin" ) == "10 20 30", "GetKeyvalue('origin') must format the origin" );
			Require( entities.GetKeyvalue( light, "classname" ) == "light", "GetKeyvalue('classname') must be the class" );
			var error = ThrowsMap( () => entities.SetKeyvalue( light, "classname", "light" ) );
			Require( error.Code == MapEntityException.CodeReadOnlyKeyvalue,
				$"setting classname must be ReadOnlyKeyvalue, got {error.Code}" );
			var badOrigin = ThrowsMap( () => entities.SetKeyvalue( light, "origin", "1 2" ) );
			Require( badOrigin.Code == MapEntityException.CodeInvalidOrigin,
				$"malformed origin keyvalue must be InvalidOrigin, got {badOrigin.Code}" );
			Require( light.Origin.AlmostEquals( new Vec3( 10, 20, 30 ) ), "rejected origin set must leave the origin unchanged" );
		} );

		Check( "duplicate targetname warns but does not error", () =>
		{
			var entities = NewEntities();
			entities.Tie( "func_door", new[] { Brush( "a" ) },
				new Dictionary<string, string> { ["targetname"] = "door_main" } );
			entities.Tie( "func_door", new[] { Brush( "b" ) },
				new Dictionary<string, string> { ["targetname"] = "door_main" } );
			var issues = entities.Validate();
			Require( issues.Count == 1, $"duplicate targetname must produce exactly 1 issue, got {issues.Count}" );
			Require( issues[0].Severity == MapEntityIssueSeverity.Warning, "duplicate targetname must be a warning" );
			Require( issues[0].Code == MapEntityIssue.CodeDuplicateTargetname,
				$"warning code must be DuplicateTargetname, got {issues[0].Code}" );
		} );

		Check( "missing target errors and clears once the target is placed", () =>
		{
			var entities = NewEntities();
			var spot = entities.PlacePointEntity( "light_spot", new Vec3( 128, 64, 128 ),
				new Dictionary<string, string> { ["target"] = "ghost_sign" } );
			var issues = entities.Validate();
			Require( issues.Count == 1 && issues[0].Severity == MapEntityIssueSeverity.Error
				&& issues[0].Code == MapEntityIssue.CodeMissingTarget && issues[0].Key == "target",
				$"missing target must be one MissingTarget error, got {issues.Count} issue(s)" );
			Require( issues[0].EntityId == spot.Id, "missing-target issue must name the referencing entity" );
			entities.PlacePointEntity( "logic_relay", Vec3.Zero,
				new Dictionary<string, string> { ["targetname"] = "ghost_sign" } );
			Require( entities.Validate().Count == 0, "placing the target must clear the missing-target error" );
			entities.PlacePointEntity( "prop_static", Vec3.Zero,
				new Dictionary<string, string>
				{
					["model"] = "models/props_c17/oildrum001.mdl",
					["parentname"] = "ghost_parent",
				} );
			issues = entities.Validate();
			Require( issues.Count == 1 && issues[0].Code == MapEntityIssue.CodeMissingTarget && issues[0].Key == "parentname",
				"a missing parentname target must also error" );
			entities.PlacePointEntity( "logic_auto", Vec3.Zero,
				new Dictionary<string, string> { ["targetname"] = "ghost_parent" } );
			Require( entities.Validate().Count == 0, "placing the parent must clear the missing-target error" );
		} );

		Check( "find by class and by targetname; targetname lookup is case-insensitive", () =>
		{
			var entities = NewEntities();
			entities.PlacePointEntity( "light", Vec3.Zero,
				new Dictionary<string, string> { ["targetname"] = "lamp_1" } );
			entities.Tie( "func_door", new[] { Brush( "d" ) },
				new Dictionary<string, string> { ["targetname"] = "door_1" } );
			entities.PlacePointEntity( "light", new Vec3( 64, 0, 0 ) );
			Require( entities.FindByClass( "light" ).Count == 2, "FindByClass('light') must find both lights" );
			Require( entities.FindByClass( "func_door" ).Count == 1, "FindByClass('func_door') must find the door" );
			Require( entities.FindByClass( "nope" ).Count == 0, "FindByClass of an unknown class must be empty" );
			Require( entities.FindByTargetName( "lamp_1" ).Count == 1, "FindByTargetName('lamp_1') must find the lamp" );
			Require( entities.FindByTargetName( "LAMP_1" ).Count == 1, "FindByTargetName must be case-insensitive" );
			Require( entities.FindByTargetName( "door_1" ).Count == 1, "FindByTargetName('door_1') must find the door" );
			Require( entities.FindByTargetName( "missing" ).Count == 0, "FindByTargetName of a missing name must be empty" );
			Require( entities.FindByTargetName( "" ).Count == 0, "FindByTargetName('') must be empty" );
		} );

		Check( "fixture placement places four entities and reports its two issues", () =>
		{
			var entities = NewEntities();
			using var doc = JsonDocument.Parse( File.ReadAllText( Fixture( "placement.json" ) ) );
			Vec3 Vec( JsonElement el ) => new Vec3( el[0].GetSingle(), el[1].GetSingle(), el[2].GetSingle() );
			foreach ( var el in doc.RootElement.GetProperty( "entities" ).EnumerateArray() )
			{
				string className = el.GetProperty( "class" ).GetString()!;
				var keyvalues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
				if ( el.TryGetProperty( "keyvalues", out var kvEl ) )
					foreach ( var kv in kvEl.EnumerateObject() ) keyvalues[kv.Name] = kv.Value.GetString() ?? "";
				Vec3? angles = el.TryGetProperty( "angles", out var anglesEl ) ? Vec( anglesEl ) : null;
				if ( el.TryGetProperty( "brushes", out var brushesEl ) )
				{
					var brushes = brushesEl.EnumerateArray().Select( b => Brush( b.GetString() ?? "" ) ).ToArray();
					entities.Tie( className, brushes, keyvalues, Vec( el.GetProperty( "origin" ) ) );
				}
				else
				{
					entities.PlacePointEntity( className, Vec( el.GetProperty( "origin" ) ), keyvalues, angles );
				}
			}
			Require( entities.Count == 4, $"fixture must place 4 entities, got {entities.Count}" );
			Require( entities.FindByTargetName( "door_main" ).Count == 2, "fixture doors must share targetname 'door_main'" );
			Require( entities.FindByTargetName( "DOOR_MAIN" ).Count == 2, "fixture targetname lookup must be case-insensitive" );
			var spot = entities.FindByClass( "light_spot" )[0];
			Require( spot.Angles.AlmostEquals( new Vec3( 0, 90, 0 ) ), "fixture light_spot angles must be (0, 90, 0)" );
			var issues = entities.Validate();
			Require( issues.Count == 2, $"fixture must report exactly 2 issues, got {issues.Count}" );
			Require( issues.Any( i => i.Severity == MapEntityIssueSeverity.Error && i.Code == MapEntityIssue.CodeMissingTarget && i.Key == "target" ),
				"fixture must report the missing 'ghost_sign' target" );
			Require( issues.Any( i => i.Severity == MapEntityIssueSeverity.Warning && i.Code == MapEntityIssue.CodeDuplicateTargetname ),
				"fixture must report the duplicate 'door_main' targetname" );
		} );

		Check( "a second registry JSON adds a brush class placeable with no code change", () =>
		{
			var registry = EntityRegistry.LoadFromJson(
				"{\"schemaVersion\":1,\"name\":\"test-custom\",\"classes\":[" +
				"{\"name\":\"func_mover\",\"kind\":\"brush\",\"keyvalues\":[" +
				"{\"name\":\"speed\",\"type\":\"float\",\"default\":\"50\"}]}]}", "test-custom" );
			var entities = new MapEntities( registry );
			var mover = entities.Tie( "func_mover", new[] { Brush( "m" ) },
				new Dictionary<string, string> { ["speed"] = "50" } );
			Require( mover.ClassName == "func_mover" && mover.IsBrushEntity, "custom brush class must be tieable" );
			Require( entities.GetKeyvalue( mover, "speed" ) == "50", "custom class keyvalue must round-trip" );
		} );

		Check( "class names are canonicalised: placing LIGHT stores light", () =>
		{
			var entities = NewEntities();
			var light = entities.PlacePointEntity( "LIGHT", Vec3.Zero );
			Require( light.ClassName == "light", $"class must be canonical 'light', got '{light.ClassName}'" );
			Require( entities.FindByClass( "Light" ).Count == 1, "FindByClass must be case-insensitive" );
		} );
	}
}
