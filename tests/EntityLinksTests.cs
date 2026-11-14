using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

// Entity link lines data (Show Connections): hammer-entity-show-connections.
// Group "entitylinks". Fixtures live in tests/fixtures/hammer/hammer-entity-show-connections/.
// Backend data only: segments are (source origin -> target origin) with kind + validity;
// no viewport rendering, no UI. Origins are kernel inches, Z-up.
internal static class EntityLinksTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "entitylinks", name, test );
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

		string FixtureDir()
		{
			const string relative = "tests/fixtures/hammer/hammer-entity-show-connections";
			foreach ( string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory } )
			{
				string dir = Path.GetFullPath( start );
				while ( dir is not null )
				{
					string candidate = Path.Combine( dir, relative );
					if ( Directory.Exists( candidate ) ) return candidate;
					string? parent = Path.GetDirectoryName( dir );
					if ( parent is null || parent == dir ) break;
					dir = parent;
				}
			}
			throw new Exception( $"fixture directory not found under cwd or AppContext.BaseDirectory: {relative}" );
		}

		EntityLinkEntity Button() => new( "button_01", "func_button", new Vec3( 0, 0, 64 ), "button_01" );
		EntityLinkEntity Door() => new( "door_01", "func_door", new Vec3( 256, 0, 64 ), "door_01" );

		EntityIoList ButtonDoorIo( double delay = 0.5, int maxFires = -1, bool enabled = true )
		{
			var io = new EntityIoList();
			Require( io.Add( new EntityIoOutput( "button_01", "OnPressed", "door_01", "Open", delay, maxFires, "", enabled ) ) is null,
				"valid output was rejected" );
			return io;
		}

		void RequireOrigin( Vec3 actual, Vec3 expected, string label ) =>
			Require( actual.AlmostEquals( expected ), $"{label} is {actual}, expected {expected}" );

		Check( "a button->door output yields one segment between their origins", () =>
		{
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { Button(), Door() }, ButtonDoorIo() ) );
			Require( set.Count == 1, $"segment count {set.Count}, expected 1" );
			var link = set.Links[0];
			Require( link.Kind == EntityLinkKind.Output, $"kind is {link.Kind}, expected Output" );
			Require( link.Validity == EntityLinkValidity.Valid, $"validity is {link.Validity}, expected Valid" );
			Require( link.SourceId == "button_01" && link.SourceName == "button_01", "source is wrong: " + link.SourceId );
			Require( link.TargetId == "door_01" && link.TargetName == "door_01", "target is wrong: " + link.TargetId );
			RequireOrigin( link.Start, new Vec3( 0, 0, 64 ), "segment start" );
			RequireOrigin( link.End, new Vec3( 256, 0, 64 ), "segment end" );
			Require( Math.Abs( link.Length - 256 ) < 1e-3f, $"segment length {link.Length}, expected 256" );
			Require( link.Event == "OnPressed" && link.Input == "Open", "event/input not carried: " + link.Event + "/" + link.Input );
			Require( link.DelaySeconds == 0.5 && link.MaxFires == -1, "delay/fires not carried" );
			Require( !link.IsDegenerate && link.IsValid, "a good link must not be degenerate" );
			Require( set.IsClean && !set.HasProblems, "a good map must be clean" );
		} );

		Check( "a missing target yields a segment flagged missing, degenerate at the source", () =>
		{
			var io = new EntityIoList();
			Require( io.Add( new EntityIoOutput( "button_01", "OnPressed", "door_99", "Open" ) ) is null, "valid output was rejected" );
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { Button(), Door() }, io ) );
			Require( set.Count == 1, $"segment count {set.Count}, expected 1 (the missing target still yields a segment)" );
			var link = set.Links[0];
			Require( link.Validity == EntityLinkValidity.Missing, $"validity is {link.Validity}, expected Missing" );
			Require( link.TargetName == "door_99" && link.TargetId == "", "missing target must carry the name and no id" );
			Require( link.IsDegenerate, "a missing-target segment must be degenerate (no endpoint to draw)" );
			RequireOrigin( link.Start, new Vec3( 0, 0, 64 ), "missing segment start" );
			RequireOrigin( link.End, new Vec3( 0, 0, 64 ), "missing segment end" );
			Require( set.HasProblems && set.MissingCount == 1, "missing tally is wrong" );
			Require( set.MissingTargets.Count == 1 && set.MissingTargets[0] == "door_99",
				"missing target name is wrong: " + string.Join( ",", set.MissingTargets ) );
			Require( set.AmbiguousCount == 0, "nothing should be ambiguous here" );
		} );

		Check( "an ambiguous target yields one segment per matching entity, all flagged ambiguous", () =>
		{
			var doorB = new EntityLinkEntity( "door_b", "func_door", new Vec3( 256, 128, 64 ), "door_01" );
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { Button(), Door(), doorB }, ButtonDoorIo() ) );
			Require( set.Count == 2, $"segment count {set.Count}, expected 2 (one per door sharing the name)" );
			Require( set.Links.All( l => l.Validity == EntityLinkValidity.Ambiguous ), "both segments must be Ambiguous" );
			Require( set.Links[0].TargetId == "door_01" && set.Links[1].TargetId == "door_b",
				"ambiguous segments must hit each entity in map order" );
			RequireOrigin( set.Links[0].End, new Vec3( 256, 0, 64 ), "first ambiguous end" );
			RequireOrigin( set.Links[1].End, new Vec3( 256, 128, 64 ), "second ambiguous end" );
			Require( set.AmbiguousTargets.Count == 1 && set.AmbiguousTargets[0] == "door_01", "ambiguous name is wrong" );
			Require( set.MissingCount == 0, "nothing should be missing here" );
		} );

		Check( "a path_track chain yields linked segments in order", () =>
		{
			var nodes = new[]
			{
				new EntityLinkEntity( "node_01", "path_track", new Vec3( 0, 0, 64 ), "node_01",
					new Dictionary<string, string> { ["target"] = "node_02" } ),
				new EntityLinkEntity( "node_02", "path_track", new Vec3( 128, 0, 64 ), "node_02",
					new Dictionary<string, string> { ["target"] = "node_03" } ),
				new EntityLinkEntity( "node_03", "path_track", new Vec3( 256, 128, 64 ), "node_03" ),
			};
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( nodes, new EntityIoList() ) );
			Require( set.Count == 2, $"segment count {set.Count}, expected 2 chain edges" );
			Require( set.Links.All( l => l.Kind == EntityLinkKind.Path ), "path edges must be kind Path" );
			Require( set.Links.All( l => l.Validity == EntityLinkValidity.Valid ), "path edges must be Valid" );
			Require( set.Links[0].PathOrder == 0 && set.Links[1].PathOrder == 1,
				$"path order is {set.Links[0].PathOrder},{set.Links[1].PathOrder}, expected 0,1" );
			Require( set.Links.All( l => l.ChainHead == "node_01" ), "chain head must be node_01" );
			RequireOrigin( set.Links[0].Start, new Vec3( 0, 0, 64 ), "first edge start" );
			RequireOrigin( set.Links[0].End, new Vec3( 128, 0, 64 ), "first edge end" );
			RequireOrigin( set.Links[1].Start, new Vec3( 128, 0, 64 ), "second edge start" );
			RequireOrigin( set.Links[1].End, new Vec3( 256, 128, 64 ), "second edge end" );
			Require( set.LinksOfKind( EntityLinkKind.TargetName ).Count == 0,
				"a path node's target must not also emit a TargetName segment" );
		} );

		Check( "a path_track loop terminates with one segment per edge, each emitted once", () =>
		{
			var nodes = new[]
			{
				new EntityLinkEntity( "node_a", "path_track", new Vec3( 0, 0, 64 ), "node_a",
					new Dictionary<string, string> { ["target"] = "node_b" } ),
				new EntityLinkEntity( "node_b", "path_track", new Vec3( 128, 0, 64 ), "node_b",
					new Dictionary<string, string> { ["target"] = "node_c" } ),
				new EntityLinkEntity( "node_c", "path_track", new Vec3( 128, 128, 64 ), "node_c",
					new Dictionary<string, string> { ["target"] = "node_a" } ),
			};
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( nodes, new EntityIoList() ) );
			Require( set.Count == 3, $"segment count {set.Count}, expected 3 loop edges (a loop must terminate)" );
			Require( set.Links.Select( l => l.PathOrder ).SequenceEqual( new[] { 0, 1, 2 } ),
				"loop edges must be ordered 0,1,2" );
			Require( set.Links[2].TargetId == "node_a", "the loop must close back to node_a" );
		} );

		Check( "an entity target keyvalue yields a TargetName segment (no I/O row needed)", () =>
		{
			var trigger = new EntityLinkEntity( "trigger_01", "trigger_multiple", new Vec3( 64, 64, 64 ), "trigger_01",
				new Dictionary<string, string> { ["target"] = "door_01" } );
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { trigger, Door() }, new EntityIoList() ) );
			Require( set.Count == 1, $"segment count {set.Count}, expected 1" );
			var link = set.Links[0];
			Require( link.Kind == EntityLinkKind.TargetName, $"kind is {link.Kind}, expected TargetName" );
			Require( link.Validity == EntityLinkValidity.Valid, "targetname link must be Valid" );
			RequireOrigin( link.Start, new Vec3( 64, 64, 64 ), "targetname start" );
			RequireOrigin( link.End, new Vec3( 256, 0, 64 ), "targetname end" );
			Require( link.Event == "" && link.Input == "", "a targetname link carries no event/input" );
			Require( link.PathOrder == -1 && link.ChainHead == "", "a non-path link has no path order/head" );
		} );

		Check( "a missing target keyvalue is flagged missing, and a path node with a missing next is too", () =>
		{
			var trigger = new EntityLinkEntity( "trigger_01", "trigger_multiple", new Vec3( 64, 64, 64 ), "trigger_01",
				new Dictionary<string, string> { ["target"] = "ghost_01" } );
			var node = new EntityLinkEntity( "node_01", "path_track", new Vec3( 0, 0, 64 ), "node_01",
				new Dictionary<string, string> { ["target"] = "ghost_02" } );
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { trigger, node }, new EntityIoList() ) );
			Require( set.Count == 2, $"segment count {set.Count}, expected 2" );
			Require( set.Links[0].Kind == EntityLinkKind.TargetName && set.Links[0].Validity == EntityLinkValidity.Missing,
				"missing targetname link must be Missing" );
			Require( set.Links[1].Kind == EntityLinkKind.Path && set.Links[1].Validity == EntityLinkValidity.Missing,
				"missing path next must be a Missing Path segment" );
			Require( set.Links[1].PathOrder == 0 && set.Links[1].ChainHead == "node_01", "missing path edge keeps its order/head" );
			Require( set.MissingTargets.Count == 2, "both missing names must be listed" );
		} );

		Check( "engine special targets (!self, !activator) are never missing or ambiguous", () =>
		{
			var io = new EntityIoList();
			Require( io.Add( new EntityIoOutput( "button_01", "OnPressed", "!self", "Toggle" ) ) is null, "valid output was rejected" );
			Require( io.Add( new EntityIoOutput( "button_01", "OnPressed", "!activator", "Open" ) ) is null, "valid output was rejected" );
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { Button() }, io ) );
			Require( set.Count == 2, $"segment count {set.Count}, expected 2" );
			Require( set.Links.All( l => l.Validity == EntityLinkValidity.Valid ), "special targets must be Valid" );
			Require( set.Links.All( l => l.IsDegenerate ), "special targets have no fixed endpoint, so segments are degenerate" );
			Require( set.Links[0].TargetId == "button_01", "!self must resolve to the source entity" );
			Require( set.Links[1].TargetId == "", "!activator has no entity id" );
			Require( set.IsClean, "special targets must not count as problems" );
		} );

		Check( "a disabled output yields no segment, and an output with no source entity is reported, not drawn", () =>
		{
			var io = ButtonDoorIo( enabled: false );
			Require( io.Add( new EntityIoOutput( "ghost_button", "OnPressed", "door_01", "Open" ) ) is null, "valid output was rejected" );
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { Button(), Door() }, io ) );
			Require( set.Count == 0, $"segment count {set.Count}, expected 0 (disabled rows never fire; sourceless rows have no origin)" );
			Require( set.UnresolvedSources.Count == 1 && set.UnresolvedSources[0].SourceEntity == "ghost_button",
				"the sourceless output must be reported by name" );
			Require( set.HasUnresolvedSources, "HasUnresolvedSources must be true" );
		} );

		Check( "LinksFor / LinksFrom / LinksTo query one entity by targetname or by id", () =>
		{
			var trigger = new EntityLinkEntity( "trigger_01", "trigger_multiple", new Vec3( 64, 64, 64 ), "trigger_01",
				new Dictionary<string, string> { ["target"] = "door_01" } );
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { Button(), Door(), trigger }, ButtonDoorIo() ) );
			Require( set.Count == 2, $"segment count {set.Count}, expected 2 (output + targetname)" );
			Require( set.LinksFor( "door_01" ).Count == 2, "LinksFor(door_01) must find both links" );
			Require( set.LinksFor( "door_01" ).Count == set.LinksFor( "DOOR_01" ).Count, "LinksFor must be case-insensitive" );
			Require( set.LinksFor( "trigger_01" ).Count == 1, "LinksFor(trigger_01) must find the targetname link only" );
			Require( set.LinksFrom( "button_01" ).Count == 1 && set.LinksFrom( "button_01" )[0].Kind == EntityLinkKind.Output,
				"LinksFrom(button_01) must find the output only" );
			Require( set.LinksTo( "door_01" ).Count == 2, "LinksTo(door_01) must find both links" );
			Require( set.LinksFor( "no_such_entity" ).Count == 0, "LinksFor of an unknown name must be empty" );
			Require( set.LinksFor( "" ).Count == 0, "LinksFor of an empty name must be empty" );
		} );

		Check( "an unnamed source entity is resolved by its stable id (the landed \"#\" + id key)", () =>
		{
			var unnamed = new EntityLinkEntity( "ent_7", "func_button", new Vec3( 0, 0, 64 ) );
			var io = new EntityIoList();
			Require( io.Add( new EntityIoOutput( "#ent_7", "OnPressed", "door_01", "Open" ) ) is null, "valid output was rejected" );
			var set = EntityLinks.BuildLinks( new EntityLinkDocument( new[] { unnamed, Door() }, io ) );
			Require( set.Count == 1, $"segment count {set.Count}, expected 1" );
			Require( set.Links[0].SourceId == "ent_7" && set.Links[0].SourceName == "ent_7",
				"an unnamed source falls back to its id" );
			Require( set.UnresolvedSources.Count == 0, "an id-resolved source must not be reported unresolved" );
		} );

		Check( "the button-door fixture builds the same segment as the code-built document", () =>
		{
			var fromFixture = EntityLinkDocument.FromJson( File.ReadAllText( Path.Combine( FixtureDir(), "button-door-links.json" ) ) );
			var set = EntityLinks.BuildLinks( fromFixture );
			Require( set.Count == 1, $"fixture segment count {set.Count}, expected 1" );
			var link = set.Links[0];
			Require( link.Kind == EntityLinkKind.Output && link.Validity == EntityLinkValidity.Valid, "fixture link kind/validity wrong" );
			RequireOrigin( link.Start, new Vec3( 0, 0, 64 ), "fixture start" );
			RequireOrigin( link.End, new Vec3( 256, 0, 64 ), "fixture end" );
			Require( link.DelaySeconds == 0.5, "fixture delay must be 0.5" );
			// The same document round-trips through ToJson/FromJson unchanged.
			var back = EntityLinkDocument.FromJson( fromFixture.ToJson() );
			var setBack = EntityLinks.BuildLinks( back );
			Require( setBack.Count == 1 && setBack.Links[0].Start.AlmostEquals( link.Start ) && setBack.Links[0].End.AlmostEquals( link.End ),
				"document JSON round trip changed the link" );
		} );

		Check( "the path-chain fixture builds ordered path segments", () =>
		{
			var doc = EntityLinkDocument.FromJson( File.ReadAllText( Path.Combine( FixtureDir(), "path-chain.json" ) ) );
			var set = EntityLinks.BuildLinks( doc );
			Require( set.Count == 2, $"fixture segment count {set.Count}, expected 2" );
			Require( set.Links[0].PathOrder == 0 && set.Links[1].PathOrder == 1, "fixture path order wrong" );
			RequireOrigin( set.Links[1].End, new Vec3( 256, 128, 64 ), "fixture chain end" );
		} );

		Check( "the missing-target fixture flags door_99 missing; the ambiguous fixture flags door_01 ambiguous", () =>
		{
			var missing = EntityLinks.BuildLinks( EntityLinkDocument.FromJson(
				File.ReadAllText( Path.Combine( FixtureDir(), "missing-target.json" ) ) ) );
			Require( missing.Count == 1 && missing.Links[0].Validity == EntityLinkValidity.Missing, "missing fixture wrong" );
			Require( missing.MissingTargets.Count == 1 && missing.MissingTargets[0] == "door_99", "missing fixture name wrong" );
			var ambiguous = EntityLinks.BuildLinks( EntityLinkDocument.FromJson(
				File.ReadAllText( Path.Combine( FixtureDir(), "ambiguous-target.json" ) ) ) );
			Require( ambiguous.Count == 2 && ambiguous.Links.All( l => l.Validity == EntityLinkValidity.Ambiguous ),
				"ambiguous fixture must yield 2 Ambiguous segments" );
			Require( ambiguous.AmbiguousTargets.Count == 1 && ambiguous.AmbiguousTargets[0] == "door_01", "ambiguous fixture name wrong" );
		} );

		Check( "FromStates adapts landed MapEntityState snapshots, preserving id/class/origin/targetname", () =>
		{
			var states = new[]
			{
				new MapEntityState
				{
					Id = "button_01", ClassName = "func_button", Origin = new Vec3( 0, 0, 64 ),
					Properties = new Dictionary<string, string> { ["targetname"] = "button_01" },
				},
				new MapEntityState
				{
					Id = "door_01", ClassName = "func_door", Origin = new Vec3( 256, 0, 64 ),
					Properties = new Dictionary<string, string> { ["targetname"] = "door_01" },
				},
			};
			var set = EntityLinks.BuildLinks( EntityLinkDocument.FromStates( states, ButtonDoorIo() ) );
			Require( set.Count == 1, $"segment count {set.Count}, expected 1" );
			RequireOrigin( set.Links[0].Start, new Vec3( 0, 0, 64 ), "adapted start" );
			RequireOrigin( set.Links[0].End, new Vec3( 256, 0, 64 ), "adapted end" );
		} );

		Check( "BuildLinks rejects null input and an empty document yields zero clean links", () =>
		{
			Throws<ArgumentNullException>( () => EntityLinks.BuildLinks( (EntityLinkDocument)null! ) );
			Throws<ArgumentNullException>( () => EntityLinks.BuildLinks( (IReadOnlyList<EntityLinkEntity>)null!, new EntityIoList() ) );
			Throws<ArgumentException>( () => new EntityLinkEntity( "", "func_button", new Vec3( 0, 0, 0 ) ) );
			Throws<ArgumentException>( () => EntityLinkDocument.FromJson( "{\"version\": 2, \"entities\": []}" ) );
			var empty = EntityLinks.BuildLinks( new EntityLinkDocument( Array.Empty<EntityLinkEntity>(), new EntityIoList() ) );
			Require( empty.Count == 0 && empty.IsClean, "an empty document must yield zero clean links" );
		} );
	}
}
