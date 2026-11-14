using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

// Entity I/O logic list model: hammer-entity-io-logic-list-model.
// Group "entityio". Fixtures live in tests/fixtures/hammer/hammer-entity-io-logic-list-model/.
// Row types are EntityIoOutput / EntityIoInput: EntityOutput / EntityInput already
// belong to the landed EntityRegistry (FGD input/output declarations).
internal static class EntityIoTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "entityio", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}

		EntityIoOutput ButtonDoor( double delay = 0.5, int maxFires = -1 ) =>
			new( "button_01", "OnPressed", "door_01", "Open", delay, maxFires );

		// Walk up from the cwd and from the test binary looking for a repo-relative path.
		string FindRepoPath( string relative )
		{
			foreach ( string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory } )
			{
				string dir = Path.GetFullPath( start );
				while ( dir is not null )
				{
					string candidate = Path.Combine( dir, relative );
					if ( File.Exists( candidate ) || Directory.Exists( candidate ) ) return candidate;
					string? parent = Path.GetDirectoryName( dir );
					if ( parent is null || parent == dir ) break;
					dir = parent;
				}
			}
			throw new Exception( $"repo path not found under cwd or AppContext.BaseDirectory: {relative}" );
		}

		string FixtureDir() => FindRepoPath( "tests/fixtures/hammer/hammer-entity-io-logic-list-model" );

		void RequireSameDefinitions( IReadOnlyList<LogicEntityDefinition> a, IReadOnlyList<LogicEntityDefinition> b, string label )
		{
			Require( a.Count == b.Count, $"{label}: definition count {a.Count} != {b.Count}" );
			for ( int i = 0; i < a.Count; i++ )
			{
				Require( a[i].ClassName == b[i].ClassName, $"{label}: class {i} is {a[i].ClassName}, expected {b[i].ClassName}" );
				Require( a[i].Kind == b[i].Kind, $"{label}: {a[i].ClassName} kind differs" );
				Require( a[i].BaseClasses.SequenceEqual( b[i].BaseClasses ), $"{label}: {a[i].ClassName} base classes differ" );
				Require( a[i].Inputs.Select( p => p.Name + ":" + p.ArgumentType ).SequenceEqual( b[i].Inputs.Select( p => p.Name + ":" + p.ArgumentType ) ),
					$"{label}: {a[i].ClassName} inputs differ" );
				Require( a[i].Outputs.Select( p => p.Name + ":" + p.ArgumentType ).SequenceEqual( b[i].Outputs.Select( p => p.Name + ":" + p.ArgumentType ) ),
					$"{label}: {a[i].ClassName} outputs differ" );
				Require( a[i].KeyValues.Select( k => k.Name + "=" + k.Type + "=" + k.DefaultValue )
					.SequenceEqual( b[i].KeyValues.Select( k => k.Name + "=" + k.Type + "=" + k.DefaultValue ) ),
					$"{label}: {a[i].ClassName} keyvalues differ" );
				Require( a[i].Helpers.SequenceEqual( b[i].Helpers ), $"{label}: {a[i].ClassName} helpers differ" );
			}
		}

		Check( "button->door output round-trips through map-document JSON with every field intact", () =>
		{
			var list = new EntityIoList();
			Require( list.Add( ButtonDoor() ) is null, "valid output was rejected" );
			string json = list.ToJson();
			var back = EntityIoList.FromJson( json );
			Require( back.Count == 1, $"round trip count {back.Count}" );
			Require( back.Outputs[0].Equals( ButtonDoor() ), "round-tripped output differs" );
			Require( back.Outputs[0].SourceEntity == "button_01" && back.Outputs[0].Event == "OnPressed"
				&& back.Outputs[0].TargetName == "door_01" && back.Outputs[0].Input == "Open"
				&& back.Outputs[0].DelaySeconds == 0.5 && back.Outputs[0].MaxFires == -1, "round-tripped fields differ" );
		} );

		Check( "button->door output round-trips through a VMF connections block in exact VMF format", () =>
		{
			var list = new EntityIoList();
			Require( list.Add( ButtonDoor() ) is null, "valid output was rejected" );
			var lines = list.ToVmfConnectionsBlock( "button_01" );
			Require( lines.Count == 4 && lines[0] == "connections" && lines[1] == "{" && lines[3] == "}",
				"connections block wrapper is wrong: " + string.Join( " / ", lines ) );
			Require( lines[2] == "\"OnPressed\" \"door_01,Open,,0.5,-1\"",
				"VMF connection line is wrong: " + lines[2] );
			var back = EntityIoList.FromVmfConnectionLines( "button_01", lines );
			Require( back.Count == 1 && back.Outputs[0].Equals( ButtonDoor() ), "VMF round trip differs" );
		} );

		Check( "the button->door JSON and VMF fixtures load to the same output row", () =>
		{
			var fromJson = EntityIoList.FromJson( File.ReadAllText( Path.Combine( FixtureDir(), "button-door.json" ) ) );
			var fromVmf = EntityIoList.FromVmfConnectionLines( "button_01",
				File.ReadAllLines( Path.Combine( FixtureDir(), "button-door-connections.vmf" ) ) );
			Require( fromJson.Count == 1 && fromVmf.Count == 1, "fixture row counts differ" );
			Require( fromJson.Outputs[0].Equals( fromVmf.Outputs[0] ), "JSON and VMF fixtures describe different rows" );
			Require( fromJson.Outputs[0].Equals( ButtonDoor() ), "fixtures do not describe the button->door row" );
		} );

		Check( "a missing target is reported by name (the red-target case)", () =>
		{
			var list = new EntityIoList();
			Require( list.Add( ButtonDoor() ) is null, "valid output was rejected" );
			Require( list.Add( new EntityIoOutput( "button_01", "OnPressed", "door_99", "Open" ) ) is null, "valid output was rejected" );
			var resolution = list.ResolveTargets( new[] { "button_01", "door_01" } );
			Require( resolution.HasProblems, "missing target was not reported" );
			Require( resolution.Missing.Count == 1 && resolution.Missing[0] == "door_99",
				"missing list is wrong: " + string.Join( ",", resolution.Missing ) );
			Require( resolution.MissingOutputs.Count == 1 && resolution.MissingOutputs[0].TargetName == "door_99",
				"missing output was not named" );
			Require( resolution.ResolvedOutputs.Count == 1 && resolution.ResolvedOutputs[0].TargetName == "door_01",
				"the good output was not resolved" );
			Require( resolution.Ambiguous.Count == 0, "nothing should be ambiguous here" );
		} );

		Check( "a target name shared by two entities is ambiguous (the bold case)", () =>
		{
			var list = new EntityIoList();
			Require( list.Add( ButtonDoor() ) is null, "valid output was rejected" );
			var resolution = list.ResolveTargets( new[] { "button_01", "door_01", "door_01" } );
			Require( resolution.HasProblems, "duplicate target name was not reported" );
			Require( resolution.Ambiguous.Count == 1 && resolution.Ambiguous[0] == "door_01",
				"ambiguous list is wrong: " + string.Join( ",", resolution.Ambiguous ) );
			Require( resolution.Missing.Count == 0, "nothing should be missing here" );
			Require( resolution.AmbiguousOutputs.Count == 1, "ambiguous output was not listed" );
			Require( resolution.ResolvedOutputs.Count == 0, "an ambiguous output must not count as resolved" );
			// The Func overload (MapEntities backs this with FindByTargetName(name).Count) agrees.
			var byFunc = list.ResolveTargets( name => name == "door_01" ? 2 : 1 );
			Require( byFunc.Ambiguous.SequenceEqual( new[] { "door_01" } ), "Func overload disagrees on ambiguity" );
			Require( list.ResolveTargets( new[] { "button_01", "door_01" } ).IsClean, "one of each name must be clean" );
		} );

		Check( "math_counter with two inputs models an AND-gate: OnHitMax fires only after both", () =>
		{
			var gate = MathCounterState.AndGate( 2 );
			Require( gate.Value == 0 && !gate.AtMax, "fresh AND-gate must start at 0, not at max" );
			gate.Add( 1 );
			Require( gate.Value == 1 && !gate.AtMax, "one input must not fire the gate" );
			Require( !gate.LastOutputsFired.Contains( "OnHitMax" ), "one input fired OnHitMax" );
			// The counter starts at its minimum, so the first Add leaves the minimum
			// and fires OnChangedFromMin (faithful Source behaviour) — the gate still
			// has not fired, because the door is wired to OnHitMax, not to this.
			Require( gate.LastOutputsFired.SequenceEqual( new[] { "OnChangedFromMin" } ),
				"first input must fire OnChangedFromMin: " + string.Join( ",", gate.LastOutputsFired ) );
			gate.Add( 1 );
			Require( gate.Value == 2 && gate.AtMax, "two inputs must reach max" );
			Require( gate.LastOutputsFired.SequenceEqual( new[] { "OnHitMax" } ),
				"second input must fire OnHitMax: " + string.Join( ",", gate.LastOutputsFired ) );
			gate.Subtract( 1 );
			Require( gate.Value == 1 && !gate.AtMax, "subtracting must leave max" );
			Require( gate.LastOutputsFired.SequenceEqual( new[] { "OnChangedFromMax" } ),
				"leaving max must fire OnChangedFromMax: " + string.Join( ",", gate.LastOutputsFired ) );
			// The list form: two outputs feed the counter, one output leaves it for the door.
			var list = new EntityIoList();
			Require( list.Add( new EntityIoOutput( "button_a", "OnPressed", "counter_01", "Add", 0, -1, "1" ) ) is null, "counter input A rejected" );
			Require( list.Add( new EntityIoOutput( "button_b", "OnPressed", "counter_01", "Add", 0, -1, "1" ) ) is null, "counter input B rejected" );
			Require( list.Add( new EntityIoOutput( "counter_01", "OnHitMax", "door_01", "Open" ) ) is null, "counter output rejected" );
			Require( list.InputsFor( "counter_01" ).Count == 2, "counter must see two input rows" );
			Require( list.OutputsFrom( "counter_01" ).Count == 1 && list.OutputsFrom( "counter_01" )[0].Event == "OnHitMax",
				"counter must fire OnHitMax at the door" );
			Require( list.ResolveTargets( new[] { "button_a", "button_b", "counter_01", "door_01" } ).IsClean, "gate wiring must resolve clean" );
		} );

		Check( "logic_auto fires-on-load persists through JSON and VMF round-trips", () =>
		{
			var load = new EntityIoOutput( "auto_01", "OnLoadGame", "relay_01", "Trigger" );
			Require( load.IsLoadEvent, "OnLoadGame must be a load event" );
			Require( new EntityIoOutput( "auto_01", "OnBackgroundMap", "relay_01", "Trigger" ).IsLoadEvent, "OnBackgroundMap must be a load event" );
			Require( !ButtonDoor().IsLoadEvent, "OnPressed must not be a load event" );
			var list = new EntityIoList();
			Require( list.Add( load ) is null, "load output rejected" );
			var fromJson = EntityIoList.FromJson( list.ToJson() );
			Require( fromJson.Outputs[0].Equals( load ) && fromJson.Outputs[0].IsLoadEvent, "load flag did not survive JSON" );
			var fromVmf = EntityIoList.FromVmfConnectionLines( "auto_01", list.ToVmfConnectionsBlock( "auto_01" ) );
			Require( fromVmf.Outputs[0].Equals( load ) && fromVmf.Outputs[0].IsLoadEvent, "load flag did not survive VMF" );
			Require( LogicEntityCatalog.TryGet( "logic_auto", out var auto ) && auto is not null
				&& auto.OutputNames.Contains( "OnLoadGame" ) && auto.OutputNames.Contains( "OnMapSpawn" )
				&& auto.OutputNames.Contains( "OnBackgroundMap" ) && auto.Inputs.Count == 0,
				"logic_auto must define OnLoadGame, OnMapSpawn and OnBackgroundMap outputs and no inputs" );
		} );

		Check( "the catalog holds all nine logic entities with their defining I/O", () =>
		{
			var all = LogicEntityCatalog.All;
			Require( all.Count == 9, $"catalog has {all.Count} entities, expected 9" );
			foreach ( string name in new[] { "logic_auto", "logic_relay", "logic_timer", "logic_branch", "logic_case", "math_counter", "logic_compare", "env_global", "point_template" } )
				Require( LogicEntityCatalog.IsLogicEntity( name ), $"catalog is missing {name}" );
			Require( !LogicEntityCatalog.IsLogicEntity( "func_button" ), "func_button is not a logic entity" );
			Require( all.All( d => d.Kind == "point" ), "every logic entity must be a point entity" );
			Require( all.All( d => d.BaseClasses.SequenceEqual( new[] { "Targetname" } ) ), "every logic entity must inherit Targetname (targetname is implicit, never redeclared)" );
			Require( all.All( d => d.Helpers.SequenceEqual( new[] { "origin" } ) ), "every logic entity must carry the origin helper" );
			Require( LogicEntityCatalog.TryGet( "logic_relay", out var relay ) && relay is not null
				&& relay.InputNames.Contains( "Trigger" ) && relay.OutputNames.Contains( "OnTrigger" )
				&& relay.KeyValues.Any( k => k.Name == "StartDisabled" && k.Type == "int" && k.DefaultValue == "0" ),
				"logic_relay I/O is wrong" );
			Require( LogicEntityCatalog.TryGet( "math_counter", out var counter ) && counter is not null
				&& counter.InputNames.Contains( "Add" ) && counter.OutputNames.Contains( "OnHitMax" )
				&& counter.KeyValues.Any( k => k.Name == "max" && k.Type == "float" ), "math_counter I/O is wrong" );
			Require( LogicEntityCatalog.TryGet( "logic_timer", out var timer ) && timer is not null
				&& timer.OutputNames.SequenceEqual( new[] { "OnTimer", "OnTimerHigh", "OnTimerLow" } )
				&& timer.Inputs.Any( p => p.Name == "RefireTime" && p.ArgumentType == "float" )
				&& timer.KeyValues.Any( k => k.Name == "RefireTime" && k.Type == "float" && k.DefaultValue == "1" ),
				"logic_timer I/O is wrong" );
			Require( LogicEntityCatalog.TryGet( "logic_case", out var kase ) && kase is not null
				&& kase.Outputs.Count == 17 && kase.OutputNames.Contains( "OnCase16" ) && kase.OutputNames.Contains( "OnDefault" )
				&& kase.KeyValues.Count( k => k.Name.StartsWith( "Case" ) ) == 16, "logic_case must have 16 cases" );
			Require( LogicEntityCatalog.TryGet( "point_template", out var template ) && template is not null
				&& template.KeyValues.Count( k => k.Name.StartsWith( "Template" ) ) == 16, "point_template must have 16 templates" );
			Require( LogicEntityCatalog.TryGet( "logic_branch", out var branch ) && branch is not null
				&& branch.InputNames.Contains( "SetValueTest" ) && branch.OutputNames.SequenceEqual( new[] { "OnTrue", "OnFalse" } ),
				"logic_branch I/O is wrong" );
			Require( LogicEntityCatalog.TryGet( "env_global", out var global ) && global is not null
				&& global.InputNames.Contains( "TurnOn" ) && global.KeyValues.Any( k => k.Name == "GlobalState" ),
				"env_global I/O is wrong" );
		} );

		Check( "the catalog loads from the logic-entities.json fixture and matches the built-in definitions", () =>
		{
			string json = File.ReadAllText( Path.Combine( FixtureDir(), "logic-entities.json" ) );
			var fromFixture = LogicEntityCatalog.FromJson( json );
			RequireSameDefinitions( fromFixture, LogicEntityCatalog.Default, "fixture vs built-in" );
			var fromEmitted = LogicEntityCatalog.FromJson( LogicEntityCatalog.ToRegistryJson() );
			RequireSameDefinitions( fromEmitted, LogicEntityCatalog.Default, "emitted registry JSON vs built-in" );
			Throws<ArgumentException>( () => LogicEntityCatalog.FromJson( "{\"schemaVersion\": 2, \"classes\": []}" ) );
			Throws<ArgumentException>( () => LogicEntityCatalog.FromJson( "{\"schemaVersion\": 1, \"classes\": [{\"name\": \"logic_auto\"}, {\"name\": \"logic_auto\"}]}" ) );
			Throws<ArgumentException>( () => LogicEntityCatalog.FromJson( "{\"schemaVersion\": 1, \"classes\": [{\"name\": \"logic_relay\", \"keyvalues\": [{\"name\": \"StartDisabled\", \"type\": \"boolean\", \"default\": \"0\"}]}]}" ) );
			Throws<ArgumentException>( () => LogicEntityCatalog.FromJson( "not json" ) );
		} );

		Check( "the emitted registry JSON loads through the real EntityRegistry with all nine classes", () =>
		{
			var registry = EntityRegistry.LoadFromJson( LogicEntityCatalog.ToRegistryJson(), "source1-logic" );
			Require( registry.Count == 9, $"registry loaded {registry.Count} classes, expected 9" );
			Require( registry.Contains( "math_counter" ) && registry.Contains( "logic_case" ) && registry.Contains( "env_global" ),
				"registry is missing logic classes" );
			var counter = registry.Get( "math_counter" );
			Require( counter.FindInput( "Add" ) is not null && counter.FindInput( "Add" ).ArgumentType == "float", "math_counter Add input is wrong" );
			Require( counter.FindOutput( "OnHitMax" ) is not null, "math_counter OnHitMax output is missing" );
			Require( counter.FindKeyValue( "max" ) is not null && counter.FindKeyValue( "max" ).Type == KeyValueType.Float, "math_counter max keyvalue is wrong" );
			var relay = registry.Get( "logic_relay" );
			Require( relay.FindKeyValue( "StartDisabled" ) is not null && relay.FindKeyValue( "StartDisabled" ).Type == KeyValueType.Int,
				"logic_relay StartDisabled must be int" );
			Require( registry.ValidateKeyValue( "logic_relay", "StartDisabled", "1" ) is null, "StartDisabled=1 must validate" );
			Require( registry.ValidateKeyValue( "logic_relay", "StartDisabled", "yes" ) is not null, "StartDisabled=yes must not validate" );
			Require( registry.ValidateEntity( "logic_timer", new Dictionary<string, string> { ["RefireTime"] = "2.5" } ).Count == 0,
				"a valid logic_timer entity must validate" );
			Require( registry.ValidateEntity( "logic_timer", new Dictionary<string, string> { ["RefireTime"] = "soon" } ).Count == 1,
				"a mistyped logic_timer value must fail validation" );
		} );

		Check( "the catalog agrees with core-source1.json on the three logic classes already there, and names the six missing ones", () =>
		{
			string corePath = FindRepoPath( "kernel/Effigy/Map/registry/core-source1.json" );
			var core = EntityRegistry.Load( corePath );
			foreach ( string name in new[] { "logic_auto", "logic_relay", "logic_timer" } )
			{
				Require( core.Contains( name ), $"core registry is missing {name}" );
				Require( LogicEntityCatalog.TryGet( name, out var mine ) && mine is not null, $"catalog is missing {name}" );
				var theirs = core.Get( name );
				Require( mine.InputNames.SequenceEqual( theirs.Inputs.Select( i => i.Name ) ), $"{name} inputs disagree with core-source1.json" );
				Require( mine.OutputNames.SequenceEqual( theirs.Outputs.Select( o => o.Name ) ), $"{name} outputs disagree with core-source1.json" );
				Require( mine.KeyValues.Select( k => k.Name ).SequenceEqual( theirs.KeyValues.Select( k => k.Name ) ),
					$"{name} keyvalue names disagree with core-source1.json" );
				foreach ( var key in mine.KeyValues )
				{
					var theirKey = theirs.FindKeyValue( key.Name );
					Require( theirKey is not null, $"{name} core is missing keyvalue {key.Name}" );
					Require( EntityRegistry.ParseKeyValueType( key.Type ) == theirKey.Type,
						$"{name} keyvalue {key.Name} type disagrees with core-source1.json" );
				}
			}
			foreach ( string name in new[] { "logic_branch", "logic_case", "math_counter", "logic_compare", "env_global", "point_template" } )
				Require( !core.Contains( name ), $"core registry already has {name}; the missing-six list is stale" );
		} );

		Check( "Add rejects bad rows with named errors and leaves the list unchanged", () =>
		{
			var list = new EntityIoList();
			string? error;
			error = list.Add( new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open" ) );
			Require( error is null, "valid row rejected: " + error );
			Throws<ArgumentException>( () => new EntityIoOutput( "", "OnPressed", "door_01", "Open" ) );
			Throws<ArgumentException>( () => new EntityIoOutput( "button_01", "", "door_01", "Open" ) );
			Throws<ArgumentException>( () => new EntityIoOutput( "button_01", "OnPressed", "", "Open" ) );
			Throws<ArgumentException>( () => new EntityIoOutput( "button_01", "OnPressed", "door_01", "" ) );
			Throws<ArgumentException>( () => new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open", -1 ) );
			Throws<ArgumentException>( () => new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open", 0, -2 ) );
			Throws<ArgumentException>( () => new EntityIoOutput( "button_01", "OnPressed", "door_01", "Op,en" ) );
			Throws<ArgumentException>( () => new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open", 0, -1, "a,b" ) );
			Require( list.Count == 1, $"rejected rows changed the list: {list.Count}" );
			Throws<ArgumentException>( () => EntityIoList.FromJson( "{\"version\": 1, \"outputs\": [{\"source_entity\": \"b\", \"event\": \"\", \"target\": \"d\", \"input\": \"Open\"}]}" ) );
			Throws<ArgumentException>( () => EntityIoList.FromJson( "{\"version\": 9, \"outputs\": []}" ) );
			Throws<ArgumentException>( () => EntityIoList.FromJson( "{" ) );
		} );

		Check( "Add/Remove/Copy: a copy duplicates the row and removing takes one copy out", () =>
		{
			var list = new EntityIoList();
			var row = ButtonDoor();
			Require( list.Add( row ) is null, "add rejected" );
			var copy = list.Copy( row );
			Require( list.Count == 2, $"copy did not add a row: {list.Count}" );
			Require( copy.Equals( row ) && !ReferenceEquals( copy, row ), "copy must equal the row but be a distinct object" );
			Require( list.Remove( row ), "remove of a present row returned false" );
			Require( list.Count == 1 && list.Outputs[0].Equals( row ), "remove took the wrong row" );
			Require( !list.Remove( new EntityIoOutput( "button_01", "OnPressed", "door_02", "Open" ) ), "remove of an absent row returned true" );
			Require( list.Count == 1, "absent remove changed the list" );
			Throws<ArgumentNullException>( () => list.Copy( null! ) );
		} );

		Check( "InputsFor derives the door's input row from the button's output", () =>
		{
			var list = new EntityIoList();
			Require( list.Add( ButtonDoor() ) is null, "add rejected" );
			var inputs = list.InputsFor( "door_01" );
			Require( inputs.Count == 1, $"door should see 1 input, saw {inputs.Count}" );
			Require( inputs[0].TargetEntity == "door_01" && inputs[0].Input == "Open"
				&& inputs[0].SourceEntity == "button_01" && inputs[0].Event == "OnPressed"
				&& inputs[0].DelaySeconds == 0.5, "derived input row is wrong" );
			Require( list.InputsFor( "door_02" ).Count == 0, "an untargeted entity must see no inputs" );
			Require( list.OutputsTo( "DOOR_01" ).Count == 1, "target lookup must be case-insensitive" );
			Require( list.OutputsFrom( "button_01" ).Count == 1 && list.OutputsFrom( "button_02" ).Count == 0, "source lookup is wrong" );
			var standalone = new EntityIoInput( "door_01", "Close" );
			Require( standalone.SourceEntity == "" && standalone.Event == "", "standalone input row defaults are wrong" );
			Require( EntityIoInput.FromOutput( ButtonDoor() ).Equals( inputs[0] ), "FromOutput disagrees with InputsFor" );
		} );

		Check( "engine special targets (!activator, !self) are never missing or ambiguous", () =>
		{
			var list = new EntityIoList();
			Require( list.Add( new EntityIoOutput( "button_01", "OnPressed", "!activator", "RunScriptCode" ) ) is null, "special-target row rejected" );
			Require( list.Add( new EntityIoOutput( "button_01", "OnPressed", "!self", "Kill" ) ) is null, "special-target row rejected" );
			var resolution = list.ResolveTargets( new[] { "button_01" } );
			Require( resolution.IsClean, "special targets must resolve clean" );
			Require( resolution.ResolvedOutputs.Count == 2, "special-target outputs must count as resolved" );
			Require( EntityIoList.IsSpecialTarget( "!player" ) && !EntityIoList.IsSpecialTarget( "door_01" ), "special-target check is wrong" );
		} );

		Check( "MarkCandidates offers the matching targetnames, case-insensitive and sorted", () =>
		{
			var known = new[] { "door_02", "button_01", "door_01", "relay_01", "door_01" };
			var hits = EntityIoList.MarkCandidates( known, "door" );
			Require( hits.SequenceEqual( new[] { "door_01", "door_02" } ), "Mark results are wrong: " + string.Join( ",", hits ) );
			Require( EntityIoList.MarkCandidates( known, "DOOR" ).SequenceEqual( hits ), "Mark lookup must be case-insensitive" );
			Require( EntityIoList.MarkCandidates( known, "" ).SequenceEqual( new[] { "button_01", "door_01", "door_02", "relay_01" } ),
				"empty Mark query must return every known name, sorted and distinct" );
			Require( EntityIoList.MarkCandidates( known, "zzz" ).Count == 0, "Mark must return nothing for no match" );
		} );

		Check( "fire-once and fire-count persist through JSON and VMF", () =>
		{
			var once = new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open", 0, 1 );
			Require( once.IsFireOnce, "MaxFires 1 must be fire-once" );
			Require( !ButtonDoor().IsFireOnce, "MaxFires -1 must not be fire-once" );
			Require( once.ToVmfConnection() == "\"OnPressed\" \"door_01,Open,,0,1\"", "fire-once VMF line is wrong: " + once.ToVmfConnection() );
			var list = new EntityIoList();
			Require( list.Add( once ) is null, "add rejected" );
			Require( list.Add( new EntityIoOutput( "button_01", "OnPressed", "relay_01", "Trigger", 0.25, 3 ) ) is null, "add rejected" );
			var fromJson = EntityIoList.FromJson( list.ToJson() );
			Require( fromJson.Outputs[0].MaxFires == 1 && fromJson.Outputs[1].MaxFires == 3 && fromJson.Outputs[1].DelaySeconds == 0.25,
				"fire counts did not survive JSON" );
			var fromVmf = EntityIoList.FromVmfConnectionLines( "button_01", list.ToVmfConnectionsBlock( "button_01" ) );
			Require( fromVmf.Outputs[0].MaxFires == 1 && fromVmf.Outputs[1].MaxFires == 3, "fire counts did not survive VMF" );
		} );

		Check( "VMF parsing rejects bad lines with named errors and skips the block wrapper", () =>
		{
			Require( !EntityIoList.TryParseVmfConnectionLine( "not quoted", "button_01", out _, out string? e1 ) && e1 is not null, "unquoted line accepted" );
			Require( !EntityIoList.TryParseVmfConnectionLine( "\"OnPressed\" \"door_01,Open\"", "button_01", out _, out string? e2 ) && e2 is not null, "short value accepted" );
			Require( !EntityIoList.TryParseVmfConnectionLine( "\"OnPressed\" \"door_01,Open,,soon,-1\"", "button_01", out _, out _ ), "bad delay accepted" );
			Require( !EntityIoList.TryParseVmfConnectionLine( "\"OnPressed\" \"door_01,Open,,0,often\"", "button_01", out _, out _ ), "bad fire count accepted" );
			Require( !EntityIoList.TryParseVmfConnectionLine( "\"OnPressed\" \"door_01,Open,,-1,-1\"", "button_01", out _, out _ ), "negative delay accepted" );
			Throws<ArgumentException>( () => EntityIoList.FromVmfConnectionLines( "button_01", new[] { "\"OnPressed\" \"door_01,Open\"" } ) );
			var withWrapper = EntityIoList.FromVmfConnectionLines( "button_01", new[]
			{
				"connections", "{", "", "\"OnPressed\" \"door_01,Open,,0.5,-1\"", "}",
			} );
			Require( withWrapper.Count == 1 && withWrapper.Outputs[0].Equals( ButtonDoor() ), "wrapper lines were not skipped" );
		} );

		Check( "a disabled output with a parameter and a delay round-trips through JSON", () =>
		{
			var row = new EntityIoOutput( "button_a", "OnPressed", "counter_01", "Add", 1.25, -1, "2", enabled: false );
			var list = new EntityIoList();
			Require( list.Add( row ) is null, "add rejected" );
			var back = EntityIoList.FromJson( list.ToJson() );
			Require( back.Outputs[0].Equals( row ), "disabled/parameter row did not survive JSON" );
			Require( !back.Outputs[0].Enabled && back.Outputs[0].Parameter == "2" && back.Outputs[0].DelaySeconds == 1.25,
				"disabled/parameter fields are wrong after round trip" );
			var fromVmf = EntityIoList.FromVmfConnectionLines( "button_a", list.ToVmfConnectionsBlock( "button_a" ) );
			Require( fromVmf.Outputs[0].Parameter == "2" && fromVmf.Outputs[0].DelaySeconds == 1.25,
				"parameter/delay did not survive VMF (Enabled is JSON-only by design)" );
		} );
	}
}
