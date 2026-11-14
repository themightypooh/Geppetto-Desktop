#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

/// <summary>
/// Instances as collaboration — shared libraries, name fix-up, IO proxy,
/// per-map compile settings (see kernel/Effigy/Map/InstancesCollab.cs,
/// board hammer-instances-collaboration-libraries).
///
/// The acceptance numbers live in the fixtures
/// tests/fixtures/hammer/hammer-instances-collaboration-libraries/
/// (library-registry.json, room-small-v1.json, room-small-v2.json,
/// compile-settings.json); the rest build the same shapes synthetically so
/// a missing fixture can never hide a fix-up or persistence failure.
///
/// DEPENDENCY NOTE. MapInstances (worker 59) and MapDocument (worker 12)
/// had not landed when this was written, so the sub-map is the
/// instance-relevant subset (entities + brushes + IO outputs) and the
/// map-document embedding is a documented seam — see the header of
/// InstancesCollab.cs. CompilePreset / VisibilityQuality ARE the landed
/// CompileRunner enums, reused by name.
///
/// CARRY-ON (daxter-hammer-instances, 2026-11-13): both siblings have
/// since landed. The parallel types are integrator-owned, NOT resolved
/// here. InstanceIoProxy is renamed LibraryIoProxy in this file's source
/// (it duplicated MapInstances' type in the same namespace), and every
/// MapCompileSettings use is fully qualified as
/// Effigy.Map.MapCompileSettings (the bare name is ambiguous with
/// MapDocument's Effigy.MapCompileSettings). room-small-v3.json covers
/// the rename-aware update check.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// InstancesCollabTests.Register(registry, settings); line.
/// </summary>
internal static class InstancesCollabTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "instancescollab", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireReason( InstancesCollabResult result, string reason, string message )
		{
			Require( !result.Ok, $"{message}: expected failure, got success" );
			Require( result.Reason == reason, $"{message}: reason '{result.Reason}' vs expected '{reason}' (message: {result.Message})" );
		}

		static LibraryEntity Ent( string id, string classname, string targetName, Vec3 origin, Dictionary<string, string>? keyValues = null )
		{
			var e = new LibraryEntity { Id = id, ClassName = classname, TargetName = targetName, Origin = origin };
			if ( keyValues is not null ) foreach ( var kv in keyValues ) e.KeyValues[kv.Key] = kv.Value;
			return e;
		}

		static LibraryBrush Box( string id, string name, Vec3 min, Vec3 size, string material )
		{
			var b = new LibraryBrush { Id = id, Name = name, Material = material };
			float x0 = min.x, y0 = min.y, z0 = min.z;
			float x1 = min.x + size.x, y1 = min.y + size.y, z1 = min.z + size.z;
			b.Vertices.AddRange( new[]
			{
				new Vec3( x0, y0, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x0, y1, z0 ),
				new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ),
			} );
			return b;
		}

		// The fixture room_small v1.0.0, built synthetically: a 256x256 room,
		// player start + light + relay (target -> room_door) + door, one IO row.
		static LibrarySubMap RoomV1()
		{
			var sub = new LibrarySubMap { Name = "room_small", Version = "1.0.0", Path = "libraries/room_kit/room_small.gmap" };
			sub.Entities.Add( Ent( "e-spawn", "info_player_start", "", new Vec3( 128, 128, 16 ) ) );
			sub.Entities.Add( Ent( "e-light", "light", "room_light", new Vec3( 128, 128, 112 ) ) );
			sub.Entities.Add( Ent( "e-relay", "logic_relay", "door_relay", new Vec3( 128, 240, 64 ),
				new Dictionary<string, string> { ["target"] = "room_door" } ) );
			sub.Entities.Add( Ent( "e-door", "func_door", "room_door", new Vec3( 128, 256, 64 ) ) );
			sub.IoOutputs.Add( new LibraryIoOutput
			{
				SourceEntity = "door_relay", Event = "OnTrigger", TargetName = "room_door", Input = "Open",
			} );
			sub.Brushes.Add( Box( "b-floor", "floor", new Vec3( 0, 0, 0 ), new Vec3( 256, 256, 8 ), "dev/dev_measuregeneric01" ) );
			sub.Brushes.Add( Box( "b-wall", "wall", new Vec3( 0, 248, 8 ), new Vec3( 256, 8, 120 ), "dev/dev_measuregeneric01b" ) );
			return sub;
		}

		// v1.1.0: everything from v1 plus a prop and a ceiling brush.
		static LibrarySubMap RoomV2()
		{
			var sub = RoomV1();
			sub.Version = "1.1.0";
			sub.Entities.Add( Ent( "e-prop", "prop_static", "room_prop", new Vec3( 32, 32, 8 ),
				new Dictionary<string, string> { ["model"] = "models/props/crate.mdl" } ) );
			sub.Brushes.Add( Box( "b-ceiling", "ceiling", new Vec3( 0, 0, 120 ), new Vec3( 256, 256, 8 ), "dev/dev_measuregeneric01" ) );
			return sub;
		}

		static InstanceLibraryManifest RoomKit( string version, string path = "libraries/room_kit" )
		{
			var manifest = new InstanceLibraryManifest { Name = "room_kit", Version = version, Path = path };
			manifest.Entries.Add( new LibraryEntry { Name = "room_small", Path = "libraries/room_kit/room_small.gmap", Version = version } );
			manifest.Entries.Add( new LibraryEntry { Name = "room_large", Path = "libraries/room_kit/room_large.gmap", Version = "1.0.0" } );
			return manifest;
		}

		static LibraryInstance PlaceRoom( string prefix, Vec3 offset, string pinnedVersion = "" )
		{
			var set = new LibraryInstanceSet();
			var added = set.Add( "room_kit", "room_small", prefix, offset, pinnedVersion );
			if ( !added.Ok ) throw new Exception( $"placing room instance failed: {added.Reason}" );
			return set.LastAdded!;
		}

		static LibraryEntity ResolvedEntity( IReadOnlyList<LibraryEntity> entities, string id )
		{
			var found = entities.FirstOrDefault( e => e.Id == id );
			if ( found is null ) throw new Exception( $"resolved entities have no '{id}'" );
			return found;
		}

		void RequireVertex( LibraryBrush brush, Vec3 expected, string message )
		{
			Require( brush.Vertices.Any( v => v.AlmostEquals( expected ) ),
				$"{message}: no vertex at {expected} among [{string.Join( ", ", brush.Vertices )}]" );
		}

		static string FindFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-instances-collaboration-libraries", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			throw new Exception( $"fixture not found: tests/fixtures/hammer/hammer-instances-collaboration-libraries/{name}" );
		}

		Check( "versions order numerically and reject bad versions", () =>
		{
			Require( InstanceLibraryVersion.Compare( "1.2.3", "1.2.2" ) > 0, "1.2.3 should beat 1.2.2" );
			Require( InstanceLibraryVersion.Compare( "1.10.0", "1.9.9" ) > 0, "1.10.0 should beat 1.9.9 (numeric, not lexicographic)" );
			Require( InstanceLibraryVersion.Compare( "2.0.0", "1.99.99" ) > 0, "2.0.0 should beat 1.99.99" );
			Require( InstanceLibraryVersion.Compare( "1.2", "1.2.0" ) == 0, "'1.2' and '1.2.0' are the same version" );
			Require( InstanceLibraryVersion.Compare( "1.0.0", "1.0.0" ) == 0, "equal versions compare 0" );
			Require( InstanceLibraryVersion.Normalise( "1.2" ) == "1.2.0", "normalise pads the patch" );
			foreach ( string bad in new[] { "", "   ", "1.2.3.4", "1.2.x", "v1.2", "-1.0", "1.-2", "1..2" } )
				Require( !InstanceLibraryVersion.IsValid( bad ), $"version '{bad}' should be invalid" );
			Require( InstanceLibraryVersion.IsValid( "1" ) && InstanceLibraryVersion.IsValid( "0.0.0" ), "single-part and zero versions are valid" );
		} );

		Check( "register stores a manifest and refuses duplicates", () =>
		{
			var registry = new InstanceLibraryRegistry();
			var registered = registry.Register( RoomKit( "1.1.0" ) );
			Require( registered.Ok && registered.Manifest is not null, $"register failed: {registered.Reason}" );
			Require( registered.PreviousVersion == "", "a fresh register has no previous version" );
			Require( registry.Libraries.Count == 1, "registry should hold one library" );
			Require( registry.Find( "room_kit" )!.Version == "1.1.0", "registered version" );
			RequireReason( registry.Register( RoomKit( "1.2.0" ) ),
				"instancescollab-duplicate-library", "re-registering the same name" );
			Require( registry.Libraries.Count == 1 && registry.Find( "room_kit" )!.Version == "1.1.0",
				"a refused register changed the registry" );
			RequireReason( registry.Register( null! ),
				"instancescollab-manifest-null", "null manifest" );
			var bad = RoomKit( "1.2.x" );
			RequireReason( registry.Register( bad ),
				"instancescollab-invalid-manifest", "manifest with a bad version" );
		} );

		Check( "update moves to a newer version and reports the previous one", () =>
		{
			var registry = new InstanceLibraryRegistry();
			registry.Register( RoomKit( "1.1.0" ) );
			var updated = registry.Update( RoomKit( "1.2.0" ) );
			Require( updated.Ok, $"update failed: {updated.Reason}" );
			Require( updated.PreviousVersion == "1.1.0", $"previous version '{updated.PreviousVersion}'" );
			Require( registry.Find( "room_kit" )!.Version == "1.2.0", "registered version after update" );
			var same = registry.Update( RoomKit( "1.2.0" ) );
			Require( same.Ok && same.PreviousVersion == "1.2.0",
				"re-registering the same version at the same path should succeed and report itself as previous" );
			Require( registry.Libraries.Count == 1, "updates must not duplicate the library" );
		} );

		Check( "update refuses a downgrade and a same-version path change", () =>
		{
			var registry = new InstanceLibraryRegistry();
			registry.Register( RoomKit( "1.1.0" ) );
			RequireReason( registry.Update( RoomKit( "1.0.0" ) ),
				"instancescollab-version-downgrade", "downgrade to 1.0.0" );
			Require( registry.Find( "room_kit" )!.Version == "1.1.0", "a refused downgrade changed the version" );
			RequireReason( registry.Update( RoomKit( "1.1.0", "libraries/room_kit_moved" ) ),
				"instancescollab-version-conflict", "same version at a different path" );
			var other = new InstanceLibraryManifest { Name = "never_registered", Version = "1.0.0", Path = "libraries/other" };
			RequireReason( registry.Update( other ),
				"instancescollab-library-not-found", "updating an unregistered library" );
			RequireReason( registry.Remove( "never_registered" ),
				"instancescollab-library-not-found", "removing an unregistered library" );
			Require( registry.Remove( "room_kit" ).Ok && registry.Libraries.Count == 0, "removing a registered library" );
		} );

		Check( "manifest validation refuses bad names, versions and paths", () =>
		{
			void RequireInvalid( InstanceLibraryManifest manifest, string message )
			{
				string? error = manifest.Validate();
				Require( error is not null, $"{message}: manifest was accepted" );
			}
			RequireInvalid( new InstanceLibraryManifest { Name = "", Version = "1.0.0", Path = "libraries/x" }, "blank name" );
			RequireInvalid( new InstanceLibraryManifest { Name = "room kit", Version = "1.0.0", Path = "libraries/x" }, "name with a space" );
			RequireInvalid( new InstanceLibraryManifest { Name = "room/kit", Version = "1.0.0", Path = "libraries/x" }, "name with a slash" );
			RequireInvalid( RoomKit( "1.2.x" ), "bad version" );
			RequireInvalid( RoomKit( "1.0.0", "/abs/path" ), "absolute path" );
			RequireInvalid( RoomKit( "1.0.0", "libraries/../etc" ), "path with a .. segment" );
			RequireInvalid( RoomKit( "1.0.0", @"libraries\room_kit" ), "path with backslashes" );
			var outside = RoomKit( "1.0.0" );
			outside.Entries[0].Path = "other/room_small.gmap";
			RequireInvalid( outside, "entry path outside the library" );
			var dup = RoomKit( "1.0.0" );
			dup.Entries.Add( new LibraryEntry { Name = "room_small", Path = "libraries/room_kit/room_small2.gmap", Version = "1.0.0" } );
			RequireInvalid( dup, "duplicate entry name" );
			var badEntry = RoomKit( "1.0.0" );
			badEntry.Entries[1].Version = "x";
			RequireInvalid( badEntry, "entry with a bad version" );
			Require( RoomKit( "1.0.0" ).Validate() is null, "the good manifest should validate" );
		} );

		Check( "registry json round-trip is stable", () =>
		{
			var registry = new InstanceLibraryRegistry();
			registry.Register( RoomKit( "1.1.0" ) );
			string json = registry.ToJson();
			var reloaded = InstanceLibraryRegistry.FromJson( json );
			Require( reloaded.ToJson() == json, "second serialisation differs from the first (round-trip is not stable)" );
			Require( reloaded.Libraries.Count == 1 && reloaded.Find( "room_kit" )!.Version == "1.1.0", "reloaded library" );
			Require( reloaded.Find( "room_kit" )!.Entries.Count == 2, "reloaded entries" );
			Require( reloaded.Find( "room_kit" )!.FindEntry( "room_small" )!.Path == "libraries/room_kit/room_small.gmap",
				"reloaded entry path" );
		} );

		Check( "fixture registry and sub-maps load", () =>
		{
			var registry = InstanceLibraryRegistry.FromJson( File.ReadAllText( FindFixture( "library-registry.json" ) ) );
			Require( registry.Libraries.Count == 1, $"registry libraries: {registry.Libraries.Count}" );
			var lib = registry.Find( "room_kit" )!;
			Require( lib.Version == "1.1.0" && lib.Path == "libraries/room_kit", "fixture library fields" );
			Require( lib.Entries.Count == 2, "fixture entries" );
			Require( lib.FindEntry( "room_small" )!.Version == "1.1.0", "fixture room_small version" );
			var v1 = LibrarySubMap.FromJson( File.ReadAllText( FindFixture( "room-small-v1.json" ) ) );
			Require( v1.Name == "room_small" && v1.Version == "1.0.0", "fixture v1 identity" );
			Require( v1.Entities.Count == 4 && v1.Brushes.Count == 2 && v1.IoOutputs.Count == 1, "fixture v1 shape" );
			Require( v1.FindByTargetName( "door_relay" )!.KeyValues["target"] == "room_door", "fixture relay target keyvalue" );
			var v2 = LibrarySubMap.FromJson( File.ReadAllText( FindFixture( "room-small-v2.json" ) ) );
			Require( v2.Version == "1.1.0" && v2.Entities.Count == 5 && v2.Brushes.Count == 3, "fixture v2 shape" );
			Require( v2.FindByTargetName( "room_prop" )!.ClassName == "prop_static", "fixture v2 prop" );
		} );

		Check( "resolve prefixes targetnames and rewrites references with the offset", () =>
		{
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", new Vec3( 512, 0, 0 ) );
			var resolved = service.ResolveInstance( instance, RoomV1(), hostTargetNames: Array.Empty<string>() );
			Require( resolved.Ok, $"resolve failed: {resolved.Reason} {resolved.Message}" );
			Require( resolved.SubMapVersion == "1.0.0", "resolved version" );
			var light = ResolvedEntity( resolved.Entities, "e-light" );
			Require( light.TargetName == "room_a_room_light", $"light targetname '{light.TargetName}'" );
			Require( light.Origin.AlmostEquals( new Vec3( 640, 128, 112 ) ), $"light origin {light.Origin}" );
			var relay = ResolvedEntity( resolved.Entities, "e-relay" );
			Require( relay.TargetName == "room_a_door_relay", $"relay targetname '{relay.TargetName}'" );
			Require( relay.KeyValues["target"] == "room_a_room_door",
				$"relay 'target' keyvalue '{relay.KeyValues["target"]}' was not rewritten to the prefixed door" );
			Require( relay.Origin.AlmostEquals( new Vec3( 640, 240, 64 ) ), "relay origin offset" );
			var door = ResolvedEntity( resolved.Entities, "e-door" );
			Require( door.TargetName == "room_a_room_door" && door.Origin.AlmostEquals( new Vec3( 640, 256, 64 ) ), "door name/origin" );
			var spawn = ResolvedEntity( resolved.Entities, "e-spawn" );
			Require( spawn.TargetName == "" && spawn.Origin.AlmostEquals( new Vec3( 640, 128, 16 ) ), "unnamed spawn stays unnamed but moves" );
			var floor = resolved.Brushes.First( b => b.Id == "b-floor" );
			RequireVertex( floor, new Vec3( 512, 0, 0 ), "floor after offset" );
			RequireVertex( floor, new Vec3( 768, 256, 8 ), "floor far corner after offset" );
			Require( resolved.IoOutputs.Count == 1, "resolved IO count" );
			var io = resolved.IoOutputs[0];
			Require( io.SourceEntity == "room_a_door_relay" && io.TargetName == "room_a_room_door" && io.Input == "Open",
				$"IO row '{io.SourceEntity}' -> '{io.TargetName}' {io.Input}" );
			Require( resolved.NameMap["room_door"] == "room_a_room_door" && resolved.NameMap.Count == 3, "name map" );
			Require( resolved.Collisions.Count == 0 && resolved.Warnings.Count == 0, "a clean resolve has no collisions or warnings" );
		} );

		Check( "resolve leaves specials and host names alone and never mutates the sub-map", () =>
		{
			var sub = RoomV1();
			sub.IoOutputs.Add( new LibraryIoOutput
			{
				SourceEntity = "door_relay", Event = "OnTrigger", TargetName = "!activator", Input = "SetHealth",
			} );
			sub.Entities.First( e => e.Id == "e-relay" ).KeyValues["parentname"] = "host_crane";
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", Vec3.Zero );
			var resolved = service.ResolveInstance( instance, sub, hostTargetNames: new[] { "host_crane" } );
			Require( resolved.Ok, $"resolve failed: {resolved.Reason}" );
			var io = resolved.IoOutputs.First( o => o.TargetName == "!activator" );
			Require( io.Input == "SetHealth", "special-target IO row lost" );
			var relay = ResolvedEntity( resolved.Entities, "e-relay" );
			Require( relay.KeyValues["parentname"] == "host_crane",
				$"host-owned 'parentname' '{relay.KeyValues["parentname"]}' must not be rewritten" );
			Require( sub.FindByTargetName( "room_door" ) is not null, "the original sub-map was mutated (door renamed)" );
			Require( sub.FindByTargetName( "door_relay" )!.KeyValues["target"] == "room_door",
				"the original sub-map was mutated (relay target rewritten)" );
			Require( sub.IoOutputs[0].TargetName == "room_door", "the original sub-map was mutated (IO target rewritten)" );
		} );

		Check( "collision gets a numeric suffix and never renames the host", () =>
		{
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", Vec3.Zero );
			var resolved = service.ResolveInstance( instance, RoomV1(),
				hostTargetNames: new[] { "room_a_room_door", "room_a_room_door_2" } );
			Require( resolved.Ok, $"resolve failed: {resolved.Reason}" );
			var door = ResolvedEntity( resolved.Entities, "e-door" );
			Require( door.TargetName == "room_a_room_door_3",
				$"door with two host collisions should be '_3', got '{door.TargetName}'" );
			Require( resolved.Collisions.Count == 1, $"expected 1 collision, got {resolved.Collisions.Count}" );
			var collision = resolved.Collisions[0];
			Require( collision.Original == "room_door" && collision.Prefixed == "room_a_room_door" && collision.Resolved == "room_a_room_door_3",
				"collision record fields" );
			var relay = ResolvedEntity( resolved.Entities, "e-relay" );
			Require( relay.KeyValues["target"] == "room_a_room_door_3",
				"references must follow the disambiguated name, not the colliding one" );
			Require( resolved.IoOutputs[0].TargetName == "room_a_room_door_3", "IO must follow the disambiguated name" );
			Require( resolved.NameMap["room_door"] == "room_a_room_door_3", "name map records the disambiguated name" );
		} );

		Check( "update re-resolves with prefixed names intact", () =>
		{
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", new Vec3( 512, 0, 0 ) );
			var first = service.UpdateInstance( instance, RoomV1() );
			Require( first.Ok && first.Resolve is not null, $"first update failed: {first.Reason}" );
			Require( first.AddedNames.Count == 3 && first.RemovedNames.Count == 0,
				"the first resolve adds every name" );
			Require( instance.ResolvedVersion == "1.0.0", "resolved version after v1" );
			// The host gains a conflicting name between resolves: the stable
			// map must still win for the old names (kept + warning, not a rename).
			var second = service.UpdateInstance( instance, RoomV2(),
				hostTargetNames: new[] { "room_a_room_door" } );
			Require( second.Ok && second.Resolve is not null, $"second update failed: {second.Reason}" );
			Require( second.KeptNames.Count == 3
				&& second.KeptNames.Contains( "room_light" )
				&& second.KeptNames.Contains( "door_relay" )
				&& second.KeptNames.Contains( "room_door" ),
				$"kept names [{string.Join( ", ", second.KeptNames )}] should be the three v1 names" );
			Require( second.AddedNames.Count == 1 && second.AddedNames[0] == "room_prop",
				$"added names [{string.Join( ", ", second.AddedNames )}] should be only room_prop" );
			Require( second.RemovedNames.Count == 0, "nothing was removed in v2" );
			Require( instance.ResolvedVersion == "1.1.0", "resolved version after v2" );
			Require( instance.NameMap["room_door"] == "room_a_room_door",
				$"door was renamed by the update: '{instance.NameMap["room_door"]}'" );
			Require( second.Resolve!.Warnings.Count == 1,
				"the host collision with the stable door name should be one warning" );
			var prop = ResolvedEntity( second.Resolve.Entities, "e-prop" );
			Require( prop.TargetName == "room_a_room_prop" && prop.Origin.AlmostEquals( new Vec3( 544, 32, 8 ) ),
				"new prop name/origin" );
			var ceiling = second.Resolve.Brushes.First( b => b.Id == "b-ceiling" );
			RequireVertex( ceiling, new Vec3( 512, 0, 120 ), "new ceiling brush after offset" );
			var door = ResolvedEntity( second.Resolve.Entities, "e-door" );
			Require( door.TargetName == "room_a_room_door", "door in the v2 resolve" );
			// A v3 that drops the light reports it as removed.
			var v3 = RoomV2();
			v3.Version = "1.2.0";
			v3.Entities.RemoveAll( e => e.Id == "e-light" );
			var third = service.UpdateInstance( instance, v3 );
			Require( third.Ok, $"third update failed: {third.Reason}" );
			Require( third.RemovedNames.Count == 1 && third.RemovedNames[0] == "room_light",
				$"removed names [{string.Join( ", ", third.RemovedNames )}] should be only room_light" );
			Require( third.KeptNames.Count == 3, "door, relay and prop stay kept in v3" );
			Require( !instance.NameMap.ContainsKey( "room_light" ), "removed name left the name map" );
		} );

		Check( "update with a rename keeps the resolved name and remaps the proxy", () =>
		{
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", new Vec3( 512, 0, 0 ) );
			var first = service.UpdateInstance( instance, RoomV1() );
			Require( first.Ok && first.Resolve is not null, $"first update failed: {first.Reason}" );
			Require( instance.NameMap["room_door"] == "room_a_room_door", "door resolved name before the rename" );

			// v3 renames room_door -> room_gate (same entity id e-door) and
			// leaves the relay keyvalue and the IO row naming the old name.
			var v3 = LibrarySubMap.FromJson( File.ReadAllText( FindFixture( "room-small-v3.json" ) ) );
			Require( v3.Version == "1.2.0", "fixture v3 identity" );
			Require( v3.FindByTargetName( "room_gate" )!.Id == "e-door", "fixture v3 renamed door" );
			Require( v3.FindByTargetName( "room_door" ) is null, "fixture v3 must not still define the old name" );

			var proxies = new LibraryIoProxySet();
			Require( proxies.Add( "room_proxy", instance.Id ).Ok, "proxy add failed" );
			var proxy = proxies.LastAdded!;
			proxy.Forward.Add( new IoProxyRoute { Event = "OnPressed", TargetName = "room_door", Input = "Open" } );
			proxy.Backward.Add( new IoProxyRoute { SourceEntity = "room_door", Event = "OnFullyClosed", TargetName = "host_alarm", Input = "Trigger" } );

			var renames = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["room_door"] = "room_gate" };
			var renamed = service.UpdateInstance( instance, v3, renames: renames );
			Require( renamed.Ok && renamed.Resolve is not null, $"rename update failed: {renamed.Reason} {renamed.Message}" );
			Require( renamed.RenamedNames.Count == 1 && renamed.RenamedNames[0] == "room_door -> room_gate",
				$"renamed names [{string.Join( ", ", renamed.RenamedNames )}]" );
			Require( renamed.KeptNames.Contains( "room_gate" ) && renamed.KeptNames.Contains( "room_light" )
				&& renamed.KeptNames.Contains( "door_relay" ),
				$"kept names [{string.Join( ", ", renamed.KeptNames )}] should include the renamed door under its new name" );
			Require( renamed.AddedNames.Count == 1 && renamed.AddedNames[0] == "room_prop",
				$"added names [{string.Join( ", ", renamed.AddedNames )}] should be only room_prop (new in v2/v3 vs the v1 resolve)" );
			Require( renamed.RemovedNames.Count == 0, $"a rename is not a removal: [{string.Join( ", ", renamed.RemovedNames )}]" );
			Require( instance.ResolvedVersion == "1.2.0", "resolved version after the rename" );
			Require( instance.NameMap["room_gate"] == "room_a_room_door",
				$"renamed door lost its resolved name: '{( instance.NameMap.TryGetValue( "room_gate", out string? gate ) ? gate : "<missing>" )}'" );
			Require( !instance.NameMap.ContainsKey( "room_door" ), "old name left the name map" );
			var door = ResolvedEntity( renamed.Resolve!.Entities, "e-door" );
			Require( door.TargetName == "room_a_room_door", "door in the v3 resolve" );
			var relay = ResolvedEntity( renamed.Resolve.Entities, "e-relay" );
			Require( relay.KeyValues["target"] == "room_a_room_door",
				$"stale relay reference did not follow the rename: '{relay.KeyValues["target"]}'" );
			Require( renamed.Resolve.IoOutputs[0].TargetName == "room_a_room_door",
				"stale IO row did not follow the rename" );

			// The proxy follows the same rename map, then validates clean.
			Require( proxy.RemapRenames( renames ) == 2, "both proxy legs (forward target + backward source) should remap" );
			Require( proxy.Forward[0].TargetName == "room_gate", "forward leg did not remap" );
			Require( proxy.Backward[0].SourceEntity == "room_gate", "backward source did not remap" );
			Require( proxy.Backward[0].TargetName == "host_alarm", "backward host target must never remap" );
			Require( proxy.RemapRenames( null ) == 0 && proxy.RemapRenames( new Dictionary<string, string>() ) == 0,
				"empty rename maps remap nothing" );
			var validation = proxy.Validate( v3 );
			Require( validation.Ok && validation.Issues.Count == 0, $"remapped proxy failed validation: {validation.Message}" );
			var rows = proxy.ResolveForward( renamed.Resolve.NameMap, out var unresolved );
			Require( unresolved.Count == 0 && rows.Count == 1 && rows[0].TargetName == "room_a_room_door",
				"forward resolve after the rename" );
			var backRows = proxy.ResolveBackward( renamed.Resolve.NameMap );
			Require( backRows.Count == 1 && backRows[0].SourceEntity == "room_a_room_door",
				"backward resolve after the rename" );
		} );

		Check( "update rename refuses unknown, stale and blank renames without changing anything", () =>
		{
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", Vec3.Zero );
			var first = service.UpdateInstance( instance, RoomV1() );
			Require( first.Ok, $"first update failed: {first.Reason}" );

			void RequireRenameRefused( IReadOnlyDictionary<string, string> renames, string reason, string message )
			{
				var refused = service.UpdateInstance( instance, RoomV2(), renames: renames );
				Require( !refused.Ok, $"{message}: expected failure, got success" );
				Require( refused.Reason == reason, $"{message}: reason '{refused.Reason}' vs expected '{reason}' ({refused.Message})" );
			}
			// New name not defined in the new version.
			RequireRenameRefused(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["room_door"] = "room_gate" },
				"instancescollab-rename-unknown-target", "new name missing from v2" );
			// Old name never resolved before (new name is defined).
			RequireRenameRefused(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["no_such"] = "room_light" },
				"instancescollab-rename-unknown-target", "old name never resolved" );
			// Old name still defined in the new version: not a rename.
			RequireRenameRefused(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["room_door"] = "room_light" },
				"instancescollab-rename-collision", "old name still defined in v2" );
			RequireRenameRefused(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["room_door"] = "" },
				"instancescollab-invalid-rename", "blank new name" );
			RequireRenameRefused(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { [""] = "room_gate" },
				"instancescollab-invalid-rename", "blank old name" );
			RequireRenameRefused(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["!activator"] = "room_gate" },
				"instancescollab-invalid-rename", "engine special as old name" );
			RequireRenameRefused(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["room_door"] = "room_door" },
				"instancescollab-invalid-rename", "rename to itself" );
			// Atomicity: every refused update left v1 resolving.
			Require( instance.ResolvedVersion == "1.0.0", "a refused rename changed the resolved version" );
			Require( instance.NameMap["room_door"] == "room_a_room_door", "a refused rename changed the name map" );
			Require( instance.NameMap.Count == 3, "a refused rename changed the name map size" );
		} );

		Check( "pinned instance refuses a different version", () =>
		{
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", Vec3.Zero, pinnedVersion: "1.0.0" );
			var first = service.UpdateInstance( instance, RoomV1() );
			Require( first.Ok, $"pinned v1 update failed: {first.Reason}" );
			Require( instance.ResolvedVersion == "1.0.0", "pinned resolved version" );
			RequireReason( service.ResolveInstance( instance, RoomV2() ),
				"instancescollab-version-pinned", "resolving a pinned instance at v2" );
			RequireReason( service.UpdateInstance( instance, RoomV2() ),
				"instancescollab-version-pinned", "updating a pinned instance to v2" );
			Require( instance.ResolvedVersion == "1.0.0", "a refused update changed the resolved version" );
			Require( instance.NameMap["room_door"] == "room_a_room_door", "a refused update changed the name map" );
		} );

		Check( "io proxy forward resolves to prefixed targets and emits vmf lines", () =>
		{
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", Vec3.Zero );
			var updated = service.UpdateInstance( instance, RoomV1() );
			Require( updated.Ok && updated.Resolve is not null, "proxy fixture resolve failed" );
			var proxies = new LibraryIoProxySet();
			var added = proxies.Add( "room_proxy", instance.Id );
			Require( added.Ok, $"proxy add failed: {added.Reason}" );
			var proxy = proxies.LastAdded!;
			proxy.Forward.Add( new IoProxyRoute { Event = "OnPressed", TargetName = "room_door", Input = "Open" } );
			proxy.Forward.Add( new IoProxyRoute { Event = "OnPressed", TargetName = "room_light", Input = "TurnOn", Enabled = false } );
			var validation = proxy.Validate( RoomV1() );
			Require( validation.Ok && validation.Issues.Count == 0, $"valid proxy failed validation: {validation.Message}" );
			var rows = proxy.ResolveForward( updated.Resolve!.NameMap, out var unresolved );
			Require( unresolved.Count == 0, $"unresolved forward routes: [{string.Join( ", ", unresolved )}]" );
			Require( rows.Count == 1, $"disabled route should resolve to nothing; got {rows.Count} rows" );
			var row = rows[0];
			Require( row.SourceEntity == "room_proxy" && row.Event == "OnPressed"
				&& row.TargetName == "room_a_room_door" && row.Input == "Open",
				$"forward row '{row.SourceEntity}' '{row.Event}' -> '{row.TargetName}' {row.Input}" );
			var lines = proxy.ToVmfLines( updated.Resolve.NameMap );
			Require( lines.Count == 1, "one VMF line for one enabled route" );
			Require( lines[0] == "\"OnPressed\" \"room_a_room_door,Open,,0,-1\"",
				$"VMF line was '{lines[0]}', expected the EntityIo connections format" );
			var unknown = new LibraryIoProxy { Name = "room_proxy", InstanceId = instance.Id };
			unknown.Forward.Add( new IoProxyRoute { Event = "OnPressed", TargetName = "no_such", Input = "Open" } );
			var unknownRows = unknown.ResolveForward( updated.Resolve.NameMap, out var unknownUnresolved );
			Require( unknownRows.Count == 0 && unknownUnresolved.Count == 1 && unknownUnresolved[0] == "no_such",
				"an unknown forward target must be listed as unresolved, never guessed" );
		} );

		Check( "io proxy validates missing and ambiguous targets", () =>
		{
			var proxy = new LibraryIoProxy { Id = "ioproxy-1", Name = "room_proxy", InstanceId = "libinst-1" };
			proxy.Forward.Add( new IoProxyRoute { Event = "OnPressed", TargetName = "no_such", Input = "Open" } );
			proxy.Forward.Add( new IoProxyRoute { Event = "OnPressed", TargetName = "room_door", Input = "Bad,Input" } );
			var result = proxy.Validate( RoomV1() );
			Require( !result.Ok, "a proxy with a missing target and a bad route should fail validation" );
			Require( result.Issues.Any( i => i.Code == IoProxyIssue.CodeTargetMissing ),
				"missing-target issue not reported" );
			Require( result.Issues.Any( i => i.Code == IoProxyIssue.CodeRouteInvalid ),
				"invalid-route issue not reported" );
			var ambiguousSub = RoomV1();
			ambiguousSub.Entities.Add( Ent( "e-door2", "func_door", "room_door", new Vec3( 0, 0, 0 ) ) );
			var ambiguous = new LibraryIoProxy { Id = "ioproxy-2", Name = "room_proxy", InstanceId = "libinst-1" };
			ambiguous.Forward.Add( new IoProxyRoute { Event = "OnPressed", TargetName = "room_door", Input = "Open" } );
			var ambiguousResult = ambiguous.Validate( ambiguousSub );
			Require( ambiguousResult.Ok && ambiguousResult.IsValid, "an ambiguous target is a warning, not an error" );
			Require( ambiguousResult.Issues.Any( i => i.Code == IoProxyIssue.CodeTargetAmbiguous ),
				"ambiguous-target warning not reported" );
			var badBackward = new LibraryIoProxy { Id = "ioproxy-3", Name = "room_proxy", InstanceId = "libinst-1" };
			badBackward.Backward.Add( new IoProxyRoute { SourceEntity = "no_such", Event = "OnClosed", TargetName = "host_alarm", Input = "Trigger" } );
			var badBackwardResult = badBackward.Validate( RoomV1() );
			Require( !badBackwardResult.Ok
				&& badBackwardResult.Issues.Any( i => i.Code == IoProxyIssue.CodeTargetMissing ),
				"a backward route firing on a missing instance entity should fail validation" );
		} );

		Check( "io proxy backward resolves the instance source to the host target", () =>
		{
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", Vec3.Zero );
			var updated = service.UpdateInstance( instance, RoomV1() );
			Require( updated.Ok && updated.Resolve is not null, "backward fixture resolve failed" );
			var proxy = new LibraryIoProxy { Id = "ioproxy-1", Name = "room_proxy", InstanceId = instance.Id };
			proxy.Backward.Add( new IoProxyRoute
			{
				SourceEntity = "room_door", Event = "OnFullyClosed", TargetName = "host_alarm", Input = "Trigger",
			} );
			proxy.Backward.Add( new IoProxyRoute
			{
				SourceEntity = "#e-light", Event = "OnTurnedOn", TargetName = "host_lamp", Input = "TurnOn",
			} );
			var rows = proxy.ResolveBackward( updated.Resolve!.NameMap );
			Require( rows.Count == 2, "backward rows" );
			Require( rows[0].SourceEntity == "room_a_room_door" && rows[0].TargetName == "host_alarm"
				&& rows[0].Event == "OnFullyClosed" && rows[0].Input == "Trigger",
				$"backward row '{rows[0].SourceEntity}' '{rows[0].Event}' -> '{rows[0].TargetName}'" );
			Require( rows[1].SourceEntity == "#e-light" && rows[1].TargetName == "host_lamp",
				"id-based backward sources pass through unchanged" );
		} );

		Check( "compile settings defaults are preview with preset stages and resolutions", () =>
		{
			var defaults = Effigy.Map.MapCompileSettings.Defaults();
			Require( defaults.Preset == CompilePreset.Preview, $"default preset '{defaults.Preset}'" );
			Require( defaults.Stages().SequenceEqual( new[] { "BspBuild", "Visibility", "LightmapBake" } ), "Preview stages" );
			Require( defaults.EffectiveVisibility() == VisibilityQuality.Fast, "Preview visibility" );
			Require( defaults.EffectiveLightmapResolution() == 4, "Preview lightmap resolution" );
			Require( defaults.MaxBrushes == 16384 && defaults.MaxPlanesPerBrush == 128, "default limits" );
			Require( defaults.Validate() is null, "defaults should validate" );
			var quick = new Effigy.Map.MapCompileSettings { Preset = CompilePreset.QuickPlay };
			Require( quick.Stages().SequenceEqual( new[] { "BspBuild" } ), "Quick Play runs BSP only" );
			Require( quick.EffectiveVisibility() == VisibilityQuality.None && quick.EffectiveLightmapResolution() == 0,
				"Quick Play has no visibility and no bake" );
			var final = new Effigy.Map.MapCompileSettings { Preset = CompilePreset.Final };
			Require( final.Stages().Count == 3, "Final stages" );
			Require( final.EffectiveVisibility() == VisibilityQuality.Full && final.EffectiveLightmapResolution() == 16,
				"Final visibility/resolution" );
			var overridden = new Effigy.Map.MapCompileSettings
			{
				Preset = CompilePreset.Preview,
				VisibilityOverride = VisibilityQuality.Full,
				LightmapResolutionOverride = 8,
			};
			Require( overridden.EffectiveVisibility() == VisibilityQuality.Full, "visibility override wins over the preset" );
			Require( overridden.EffectiveLightmapResolution() == 8, "lightmap override wins over the preset" );
			Require( overridden.Stages().Count == 3, "overrides do not change the stage list" );
		} );

		Check( "compile settings validation refuses bad limits and overrides", () =>
		{
			void RequireInvalid( Effigy.Map.MapCompileSettings settings, string fragment, string message )
			{
				string? error = settings.Validate();
				Require( error is not null && error.Contains( fragment, StringComparison.Ordinal ),
					$"{message}: validation gave '{error ?? "<null>"}', expected to name '{fragment}'" );
			}
			RequireInvalid( new Effigy.Map.MapCompileSettings { MaxBrushes = 0 }, "MaxBrushes", "zero brushes" );
			RequireInvalid( new Effigy.Map.MapCompileSettings { MaxPlanesPerBrush = 3 }, "MaxPlanesPerBrush", "three planes per brush" );
			RequireInvalid( new Effigy.Map.MapCompileSettings { LightmapResolutionOverride = 65 }, "LightmapResolutionOverride", "override 65" );
			RequireInvalid( new Effigy.Map.MapCompileSettings { LightmapResolutionOverride = -1 }, "LightmapResolutionOverride", "override -1" );
			RequireInvalid( new Effigy.Map.MapCompileSettings { Preset = (CompilePreset)99 }, "preset", "unknown preset" );
			Require( new Effigy.Map.MapCompileSettings { LightmapResolutionOverride = 64 }.Validate() is null, "override 64 is allowed" );
			Require( new Effigy.Map.MapCompileSettings { LightmapResolutionOverride = 1 }.Validate() is null, "override 1 is allowed" );
		} );

		Check( "compile settings json round-trip is stable and fixture loads", () =>
		{
			var settings = new Effigy.Map.MapCompileSettings
			{
				Preset = CompilePreset.Final,
				LightmapResolutionOverride = 8,
				MaxBrushes = 8192,
				MaxPlanesPerBrush = 64,
			};
			string json = settings.ToJson();
			var reloaded = Effigy.Map.MapCompileSettings.FromJson( json );
			Require( reloaded.ToJson() == json, "second serialisation differs from the first (round-trip is not stable)" );
			Require( reloaded.Preset == CompilePreset.Final && reloaded.LightmapResolutionOverride == 8
				&& reloaded.MaxBrushes == 8192 && reloaded.MaxPlanesPerBrush == 64, "reloaded settings fields" );
			Require( reloaded.EffectiveLightmapResolution() == 8, "override survives the round-trip" );
			var fixture = Effigy.Map.MapCompileSettings.FromJson( File.ReadAllText( FindFixture( "compile-settings.json" ) ) );
			Require( fixture.Preset == CompilePreset.Final, "fixture preset" );
			Require( fixture.MaxBrushes == 8192 && fixture.MaxPlanesPerBrush == 64, "fixture limits" );
			Require( fixture.Stages().Count == 3 && fixture.EffectiveLightmapResolution() == 16, "fixture effective values" );
			void RequireThrows( string badJson, string fragment, string message )
			{
				try { Effigy.Map.MapCompileSettings.FromJson( badJson ); }
				catch ( ArgumentException ex )
				{
					Require( ex.Message.Contains( fragment, StringComparison.Ordinal ),
						$"{message}: error '{ex.Message}' does not name '{fragment}'" );
					return;
				}
				throw new Exception( $"{message}: corrupt JSON was accepted" );
			}
			RequireThrows( "{\"version\":1,\"preset\":\"Turbo\",\"visibility_override\":null,\"lightmap_resolution_override\":0,\"max_brushes\":16384,\"max_planes_per_brush\":128}",
				"unknown preset 'Turbo'", "unknown preset" );
			RequireThrows( "{\"version\":2,\"preset\":\"Final\",\"visibility_override\":null,\"lightmap_resolution_override\":0,\"max_brushes\":16384,\"max_planes_per_brush\":128}",
				"unsupported compile_settings version 2", "version 2" );
			RequireThrows( "{\"version\":1,\"preset\":\"Final\",\"visibility_override\":null,\"lightmap_resolution_override\":0,\"max_brushes\":0,\"max_planes_per_brush\":128}",
				"MaxBrushes 0", "invalid limits on load" );
		} );

		Check( "instance set and proxy set json round-trips preserve placements", () =>
		{
			var service = new InstanceLibraryService();
			var instances = new LibraryInstanceSet();
			instances.Add( "room_kit", "room_small", "room_a", new Vec3( 512, 0, 0 ) );
			instances.Add( "room_kit", "room_large", "room_b", new Vec3( 0, 512, 0 ), pinnedVersion: "1.0.0" );
			var first = instances.Instances[0];
			var updated = service.UpdateInstance( first, RoomV1() );
			Require( updated.Ok, "round-trip fixture update failed" );
			string json = instances.ToJson();
			var reloaded = LibraryInstanceSet.FromJson( json );
			Require( reloaded.ToJson() == json, "instance set round-trip is not stable" );
			Require( reloaded.Instances.Count == 2, "reloaded instance count" );
			Require( reloaded.Instances[0].NameMap["room_door"] == "room_a_room_door",
				"the recorded name map did not survive the round-trip" );
			Require( reloaded.Instances[0].ResolvedVersion == "1.0.0", "resolved version did not survive" );
			Require( reloaded.Instances[1].PinnedVersion == "1.0.0", "pinned version did not survive" );
			reloaded.Add( "room_kit", "room_small", "room_c", Vec3.Zero );
			Require( reloaded.LastAdded!.Id == "libinst-3", $"next instance id after reload was '{reloaded.LastAdded!.Id}'" );
			RequireReason( reloaded.Remove( "libinst-404" ), "instancescollab-instance-not-found", "removing an unknown instance" );

			var proxies = new LibraryIoProxySet();
			proxies.Add( "room_proxy", "libinst-1" );
			proxies.LastAdded!.Forward.Add( new IoProxyRoute { Event = "OnPressed", TargetName = "room_door", Input = "Open" } );
			proxies.LastAdded!.Backward.Add( new IoProxyRoute { SourceEntity = "room_door", Event = "OnFullyClosed", TargetName = "host_alarm", Input = "Trigger" } );
			string proxyJson = proxies.ToJson();
			var reloadedProxies = LibraryIoProxySet.FromJson( proxyJson );
			Require( reloadedProxies.ToJson() == proxyJson, "proxy set round-trip is not stable" );
			Require( reloadedProxies.Proxies.Count == 1, "reloaded proxy count" );
			Require( reloadedProxies.Proxies[0].Forward[0].Input == "Open", "forward route did not survive" );
			Require( reloadedProxies.Proxies[0].Backward[0].SourceEntity == "room_door", "backward route source did not survive" );
			reloadedProxies.Add( "other_proxy", "libinst-2" );
			Require( reloadedProxies.LastAdded!.Id == "ioproxy-2", $"next proxy id after reload was '{reloadedProxies.LastAdded!.Id}'" );
			RequireReason( reloadedProxies.Remove( "ioproxy-404" ), "instancescollab-proxy-not-found", "removing an unknown proxy" );
		} );

		Check( "instance set refuses bad prefixes and pins, resolve refuses bad inputs", () =>
		{
			var set = new LibraryInstanceSet();
			RequireReason( set.Add( "room_kit", "room_small", "bad prefix", Vec3.Zero ),
				"instancescollab-invalid-prefix", "prefix with a space" );
			RequireReason( set.Add( "room_kit", "room_small", "!x", Vec3.Zero ),
				"instancescollab-invalid-prefix", "prefix starting with '!'" );
			RequireReason( set.Add( "room_kit", "room_small", "room_a", Vec3.Zero, pinnedVersion: "x" ),
				"instancescollab-invalid-version", "bad pinned version" );
			RequireReason( set.Add( "", "room_small", "room_a", Vec3.Zero ),
				"instancescollab-invalid-library", "blank library name" );
			Require( set.Instances.Count == 0, "refused placements were stored anyway" );
			var service = new InstanceLibraryService();
			var instance = PlaceRoom( "room_a", Vec3.Zero );
			RequireReason( service.ResolveInstance( instance, null! ),
				"instancescollab-submap-null", "resolving against a null sub-map" );
			RequireReason( service.ResolveInstance( null!, RoomV1() ),
				"instancescollab-instance-null", "resolving a null instance" );
			RequireReason( LibraryNameFixup.FixUp( RoomV1(), "bad prefix" ),
				"instancescollab-invalid-prefix", "fix-up with a bad prefix" );
			RequireReason( LibraryNameFixup.FixUp( null!, "room_a" ),
				"instancescollab-submap-null", "fix-up with a null sub-map" );
		} );
	}
}
