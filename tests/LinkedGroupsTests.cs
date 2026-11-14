#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

/// <summary>
/// Linked groups — edit one copy, all update (see
/// kernel/Effigy/Map/LinkedGroups.cs, board hammer-instances-linked-groups).
///
/// The acceptance numbers live in the fixtures
/// tests/fixtures/hammer/hammer-instances-linked-groups/linked-groups.json
/// (crate-stack: 2 brushes + 1 light, 2 linked copies, 1 unlinked snapshot)
/// and linked-groups-roundtrip.json (pillar: 1 brush + 1 light, 3 copies);
/// the rest build the same shapes synthetically so a missing fixture can
/// never hide a propagation failure.
///
/// DEPENDENCY NOTE. MapLayers and MapInstances (the two depends_on tickets)
/// had not landed when this was written, so layers/visgroups are plain
/// names on the definition/instance and the VMF seam is
/// ToInstanceRecords / FromInstanceRecords — see the header of LinkedGroups.cs.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// LinkedGroupsTests.Register(registry, settings); line.
/// </summary>
internal static class LinkedGroupsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "linkedgroups", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireReason( LinkedGroupResult result, string reason, string message )
		{
			Require( !result.Ok, $"{message}: expected failure, got success" );
			Require( result.Reason == reason, $"{message}: reason '{result.Reason}' vs expected '{reason}' (message: {result.Message})" );
		}

		static LinkedGroupMember Box( string id, string name, Vec3 min, Vec3 size, string material )
		{
			var m = new LinkedGroupMember
			{
				Id = id,
				Kind = LinkedGroupMemberKind.Brush,
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

		static LinkedGroupMember Lamp( string id, string name, Vec3 origin, string targetName )
		{
			var m = new LinkedGroupMember
			{
				Id = id,
				Kind = LinkedGroupMemberKind.Entity,
				Name = name,
				Origin = origin,
			};
			m.KeyValues["classname"] = "light";
			m.KeyValues["targetname"] = targetName;
			return m;
		}

		static LinkedGroupMember Resolved( LinkedGroupSet set, string instanceId, string memberId )
		{
			var members = set.Resolve( instanceId );
			var found = members.FirstOrDefault( m => m.Id == memberId );
			if ( found is null ) throw new Exception( $"instance '{instanceId}' resolved with no member '{memberId}'" );
			return found;
		}

		void RequireVertex( LinkedGroupMember member, Vec3 expected, string message )
		{
			Require( member.Vertices.Any( v => v.AlmostEquals( expected ) ),
				$"{message}: no vertex at {expected} among [{string.Join( ", ", member.Vertices )}]" );
		}

		void RequireNoVertex( LinkedGroupMember member, Vec3 unexpected, string message )
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
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-instances-linked-groups", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			throw new Exception( $"fixture not found: tests/fixtures/hammer/hammer-instances-linked-groups/{name}" );
		}

		static LinkedGroupSet CrateSet()
		{
			var set = new LinkedGroupSet();
			var defined = set.DefineLinkedGroup( "crate-stack", new[]
			{
				Box( "m-crate", "crate", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "dev/dev_measuregeneric01" ),
				Box( "m-lid", "lid", new Vec3( 0, 0, 64 ), new Vec3( 64, 64, 8 ), "dev/dev_measuregeneric01b" ),
				Lamp( "m-lamp", "lamp", new Vec3( 32, 32, 96 ), "crate_lamp" ),
			}, layer: "detail", visGroup: "props" );
			if ( !defined.Ok || defined.Definition is null ) throw new Exception( $"crate fixture definition failed: {defined.Reason}" );
			return set;
		}

		Check( "define stores members in order with layer and visgroup", () =>
		{
			var set = CrateSet();
			Require( set.Definitions.Count == 1, $"expected 1 definition, got {set.Definitions.Count}" );
			var d = set.Definitions[0];
			Require( d.Name == "crate-stack", $"definition name '{d.Name}'" );
			Require( d.Layer == "detail" && d.VisGroup == "props", $"definition layer '{d.Layer}' / visgroup '{d.VisGroup}'" );
			Require( d.Members.Count == 3, $"expected 3 members, got {d.Members.Count}" );
			Require( d.Members[0].Kind == LinkedGroupMemberKind.Brush && d.Members[2].Kind == LinkedGroupMemberKind.Entity,
				"member kinds out of order" );
			Require( d.Members[2].KeyValues["classname"] == "light", "entity classname lost" );
		} );

		Check( "define refuses empty member list", () =>
		{
			var set = new LinkedGroupSet();
			RequireReason( set.DefineLinkedGroup( "empty", Array.Empty<LinkedGroupMember>() ),
				"linkedgroups-empty-definition", "empty definition" );
			Require( set.Definitions.Count == 0, "refused definition was stored anyway" );
		} );

		Check( "define refuses blank name and duplicate member ids", () =>
		{
			var set = new LinkedGroupSet();
			RequireReason( set.DefineLinkedGroup( "   ", new[] { Box( "m-1", "b", Vec3.Zero, new Vec3( 8, 8, 8 ), "dev/x" ) } ),
				"linkedgroups-invalid-name", "blank name" );
			RequireReason( set.DefineLinkedGroup( "dup", new[]
			{
				Box( "m-1", "a", Vec3.Zero, new Vec3( 8, 8, 8 ), "dev/x" ),
				Box( "m-1", "b", Vec3.Zero, new Vec3( 8, 8, 8 ), "dev/x" ),
			} ), "linkedgroups-duplicate-member-id", "duplicate member ids" );
			Require( set.Definitions.Count == 0, "refused definitions were stored" );
		} );

		Check( "define deep-copies members", () =>
		{
			var member = Box( "m-1", "b", Vec3.Zero, new Vec3( 8, 8, 8 ), "dev/x" );
			var set = new LinkedGroupSet();
			var defined = set.DefineLinkedGroup( "solo", new[] { member } );
			Require( defined.Ok && defined.Definition is not null, "define failed" );
			member.Material = "dev/changed";
			member.Vertices[0] = new Vec3( 999, 999, 999 );
			Require( defined.Definition!.Members[0].Material == "dev/x", "caller mutation leaked into the definition" );
			RequireNoVertex( defined.Definition.Members[0], new Vec3( 999, 999, 999 ), "caller vertex mutation leaked" );
		} );

		Check( "link copies share the definition id and resolve with offsets", () =>
		{
			var set = CrateSet();
			var d = set.Definitions[0];
			var a = set.LinkCopy( d.Id, Vec3.Zero );
			var b = set.LinkCopy( d.Id, new Vec3( 128, 0, 0 ) );
			Require( a.Ok && a.Instance is not null && b.Ok && b.Instance is not null, "link copy failed" );
			Require( a.Instance!.DefinitionId == d.Id && b.Instance!.DefinitionId == d.Id,
				"copies do not share the definition id" );
			Require( a.Instance!.Id != b.Instance!.Id, "copies share an instance id" );
			RequireVertex( Resolved( set, a.Instance!.Id, "m-crate" ), new Vec3( 0, 0, 0 ), "copy A crate" );
			RequireVertex( Resolved( set, b.Instance!.Id, "m-crate" ), new Vec3( 128, 0, 0 ), "copy B crate" );
			Require( Resolved( set, b.Instance!.Id, "m-lamp" ).Origin.AlmostEquals( new Vec3( 160, 32, 96 ) ),
				"copy B lamp origin was not offset" );
			RequireReason( set.LinkCopy( "lg-404", Vec3.Zero ), "linkedgroups-definition-not-found", "unknown definition" );
		} );

		Check( "edit member via definition moves the brush in all copies", () =>
		{
			var set = CrateSet();
			var d = set.Definitions[0];
			var a = set.LinkCopy( d.Id, Vec3.Zero ).Instance!;
			var b = set.LinkCopy( d.Id, new Vec3( 128, 0, 0 ) ).Instance!;
			var edit = set.EditMember( d.Id, "m-crate", LinkedMemberEdit.Move( new Vec3( 0, 0, 16 ) ) );
			Require( edit.Ok, $"edit failed: {edit.Reason} {edit.Message}" );
			Require( edit.AffectedInstances == 2, $"expected 2 affected copies, got {edit.AffectedInstances}" );
			Require( edit.AffectedInstanceIds.Contains( a.Id ) && edit.AffectedInstanceIds.Contains( b.Id ),
				"affected ids do not list both copies" );
			RequireVertex( Resolved( set, a.Id, "m-crate" ), new Vec3( 0, 0, 16 ), "copy A after move" );
			RequireVertex( Resolved( set, b.Id, "m-crate" ), new Vec3( 128, 0, 16 ), "copy B after move" );
			RequireNoVertex( Resolved( set, a.Id, "m-crate" ), new Vec3( 0, 0, 0 ), "copy A kept the old vertex" );
		} );

		Check( "edit member via one copy propagates to the other copies", () =>
		{
			var set = CrateSet();
			var d = set.Definitions[0];
			var a = set.LinkCopy( d.Id, Vec3.Zero ).Instance!;
			var b = set.LinkCopy( d.Id, new Vec3( 128, 0, 0 ) ).Instance!;
			var edit = set.EditMemberViaInstance( b.Id, "m-lid", LinkedMemberEdit.SetMaterial( "metal/metalwall001a" ) );
			Require( edit.Ok, $"edit via copy B failed: {edit.Reason} {edit.Message}" );
			Require( Resolved( set, a.Id, "m-lid" ).Material == "metal/metalwall001a",
				"copy A did not pick up the material edited through copy B" );
			Require( Resolved( set, b.Id, "m-lid" ).Material == "metal/metalwall001a",
				"copy B did not pick up its own material edit" );
			Require( set.Definitions[0].FindMember( "m-lid" )!.Material == "metal/metalwall001a",
				"definition itself was not updated" );
		} );

		Check( "edit entity keyvalue propagates to all copies", () =>
		{
			var set = CrateSet();
			var d = set.Definitions[0];
			var a = set.LinkCopy( d.Id, Vec3.Zero ).Instance!;
			var b = set.LinkCopy( d.Id, new Vec3( 128, 0, 0 ) ).Instance!;
			var edit = set.EditMember( d.Id, "m-lamp", LinkedMemberEdit.SetKeyValue( "targetname", "crate_lamp_2" ) );
			Require( edit.Ok, $"keyvalue edit failed: {edit.Reason}" );
			Require( Resolved( set, a.Id, "m-lamp" ).KeyValues["targetname"] == "crate_lamp_2", "copy A keyvalue" );
			Require( Resolved( set, b.Id, "m-lamp" ).KeyValues["targetname"] == "crate_lamp_2", "copy B keyvalue" );
			Require( Resolved( set, a.Id, "m-lamp" ).KeyValues["classname"] == "light", "unrelated keyvalue was clobbered" );
		} );

		Check( "edit refuses wrong-kind edits and unknown ids without changing anything", () =>
		{
			var set = CrateSet();
			var d = set.Definitions[0];
			var a = set.LinkCopy( d.Id, Vec3.Zero ).Instance!;
			RequireReason( set.EditMember( d.Id, "m-lamp", LinkedMemberEdit.SetMaterial( "dev/x" ) ),
				"linkedgroups-edit-not-applicable", "SetMaterial on an entity" );
			RequireReason( set.EditMember( d.Id, "m-crate", LinkedMemberEdit.SetKeyValue( "targetname", "x" ) ),
				"linkedgroups-edit-not-applicable", "SetKeyValue on a brush" );
			RequireReason( set.EditMember( d.Id, "m-nope", LinkedMemberEdit.Move( Vec3.One ) ),
				"linkedgroups-member-not-found", "unknown member" );
			RequireReason( set.EditMember( "lg-404", "m-crate", LinkedMemberEdit.Move( Vec3.One ) ),
				"linkedgroups-definition-not-found", "unknown definition" );
			RequireReason( set.EditMemberViaInstance( "lgi-404", "m-crate", LinkedMemberEdit.Move( Vec3.One ) ),
				"linkedgroups-instance-not-found", "unknown instance" );
			Require( Resolved( set, a.Id, "m-lamp" ).KeyValues["targetname"] == "crate_lamp",
				"a refused edit changed the definition" );
			Require( !set.TryResolve( "lgi-404", out _, out string reason ) && reason == "linkedgroups-instance-not-found",
				"TryResolve did not report the unknown instance" );
		} );

		Check( "unlink snapshots one copy and leaves the others linked", () =>
		{
			var set = CrateSet();
			var d = set.Definitions[0];
			var a = set.LinkCopy( d.Id, Vec3.Zero ).Instance!;
			var b = set.LinkCopy( d.Id, new Vec3( 128, 0, 0 ) ).Instance!;
			var unlinked = set.Unlink( b.Id );
			Require( unlinked.Ok && unlinked.Group is not null, $"unlink failed: {unlinked.Reason}" );
			Require( unlinked.Group!.Id == b.Id, "unlinked group did not keep the instance id" );
			Require( unlinked.Group.SourceDefinitionId == d.Id, "unlinked group lost its provenance" );
			Require( unlinked.Group.Members.Count == 3, "unlinked snapshot lost members" );
			RequireVertex( unlinked.Group.Members.First( m => m.Id == "m-crate" ), new Vec3( 128, 0, 0 ),
				"unlinked snapshot did not carry the instance offset" );
			Require( set.Instances.Count == 1 && set.IndependentGroups.Count == 1, "instance/independent counts after unlink" );
			Require( set.Resolve( b.Id ).Count == 0, "unlinked copy still resolves as linked" );
			var edit = set.EditMember( d.Id, "m-crate", LinkedMemberEdit.Move( new Vec3( 0, 0, 16 ) ) );
			Require( edit.Ok && edit.AffectedInstances == 1, "edit after unlink should reach exactly one copy" );
			RequireVertex( Resolved( set, a.Id, "m-crate" ), new Vec3( 0, 0, 16 ), "remaining linked copy after unlink" );
			var snapshot = unlinked.Group.Members.First( m => m.Id == "m-crate" );
			RequireNoVertex( snapshot, new Vec3( 128, 0, 16 ), "unlinked copy moved with the later definition edit" );
			RequireReason( set.Unlink( b.Id ), "linkedgroups-instance-not-found", "double unlink" );
			RequireReason( set.EditMemberViaInstance( b.Id, "m-crate", LinkedMemberEdit.Move( Vec3.One ) ),
				"linkedgroups-instance-not-found", "edit via an unlinked copy" );
		} );

		Check( "fixture linked-groups.json loads with shared definition id and an unlinked snapshot", () =>
		{
			var set = LinkedGroupSet.FromJson( File.ReadAllText( FindFixture( "linked-groups.json" ) ) );
			Require( set.Definitions.Count == 1, $"definitions: {set.Definitions.Count}" );
			Require( set.Instances.Count == 2, $"instances: {set.Instances.Count}" );
			Require( set.IndependentGroups.Count == 1, $"independent: {set.IndependentGroups.Count}" );
			var d = set.Definitions[0];
			Require( d.Name == "crate-stack" && d.Layer == "detail" && d.VisGroup == "props", "fixture definition fields" );
			Require( set.Instances.All( i => i.DefinitionId == d.Id ), "fixture copies do not share the definition id" );
			RequireVertex( Resolved( set, "lgi-2", "m-crate" ), new Vec3( 128, 0, 0 ), "fixture copy lgi-2 crate" );
			Require( Resolved( set, "lgi-2", "m-lamp" ).Origin.AlmostEquals( new Vec3( 160, 32, 96 ) ), "fixture copy lgi-2 lamp" );
			var solo = set.FindIndependent( "lgi-3" );
			Require( solo is not null && solo.SourceDefinitionId == d.Id, "fixture independent group" );
			RequireVertex( solo!.Members.First( m => m.Id == "m-crate" ), new Vec3( 256, 0, 0 ), "fixture independent crate" );
		} );

		Check( "round-trip through the map document preserves definitions, copies and the shared id", () =>
		{
			var before = LinkedGroupSet.FromJson( File.ReadAllText( FindFixture( "linked-groups-roundtrip.json" ) ) );
			Require( before.Definitions.Count == 1 && before.Instances.Count == 3, "round-trip fixture shape" );
			string json = before.ToJson();
			var after = LinkedGroupSet.FromJson( json );
			Require( after.ToJson() == json, "second serialisation differs from the first (round-trip is not stable)" );
			Require( after.Definitions[0].Id == before.Definitions[0].Id, "definition id changed across the round-trip" );
			Require( after.Instances.All( i => i.DefinitionId == after.Definitions[0].Id ),
				"copies do not share the definition id after the round-trip" );
			foreach ( var instance in before.Instances )
			{
				var beforeMembers = before.Resolve( instance.Id );
				var afterMembers = after.Resolve( instance.Id );
				Require( afterMembers.Count == beforeMembers.Count, $"instance {instance.Id} lost members in the round-trip" );
				for ( int i = 0; i < beforeMembers.Count; i++ )
				{
					Require( afterMembers[i].Vertices.Count == beforeMembers[i].Vertices.Count
						&& afterMembers[i].Vertices.Zip( beforeMembers[i].Vertices, ( x, y ) => x.AlmostEquals( y ) ).All( same => same ),
						$"instance {instance.Id} member {beforeMembers[i].Id} vertices changed in the round-trip" );
					Require( afterMembers[i].Origin.AlmostEquals( beforeMembers[i].Origin ), $"instance {instance.Id} member origin changed" );
					Require( afterMembers[i].Material == beforeMembers[i].Material, $"instance {instance.Id} member material changed" );
				}
			}
			// Ids stay unique after a reload: the next definition is lg-2, not a reused lg-1.
			var extra = after.DefineLinkedGroup( "extra", new[] { Box( "m-1", "b", Vec3.Zero, new Vec3( 8, 8, 8 ), "dev/x" ) } );
			Require( extra.Ok && extra.Definition!.Id == "lg-2", $"next definition id after reload was '{extra.Definition?.Id}'" );
			var extraCopy = after.LinkCopy( extra.Definition!.Id, Vec3.Zero );
			Require( extraCopy.Ok && extraCopy.Instance!.Id == "lgi-4", $"next instance id after reload was '{extraCopy.Instance?.Id}'" );
		} );

		Check( "from-json rejects corrupt documents with a named field", () =>
		{
			void RequireThrows( string json, string fragment, string message )
			{
				try { LinkedGroupSet.FromJson( json ); }
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
			RequireThrows( "{\"version\":2,\"definitions\":[],\"instances\":[],\"independent\":[]}",
				"unsupported linked_groups version 2", "version 2" );
			RequireThrows( "{\"version\":1,\"definitions\":[],\"instances\":[{\"id\":\"lgi-1\",\"definition_id\":\"lg-404\",\"offset\":{\"x\":0,\"y\":0,\"z\":0},\"layer\":\"\",\"visgroup\":\"\"}],\"independent\":[]}",
				"missing definition 'lg-404'", "instance pointing at a missing definition" );
			RequireThrows( "{\"version\":1,\"definitions\":[{\"id\":\"lg-1\",\"name\":\"x\",\"layer\":\"\",\"visgroup\":\"\",\"members\":[{\"id\":\"m-1\",\"kind\":\"brush\",\"name\":\"a\",\"material\":\"\",\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"vertices\":[],\"key_values\":{}},{\"id\":\"m-1\",\"kind\":\"brush\",\"name\":\"b\",\"material\":\"\",\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"vertices\":[],\"key_values\":{}}]}],\"instances\":[],\"independent\":[]}",
				"duplicate member id 'm-1'", "duplicate member id in JSON" );
		} );

		Check( "vmf instance records share the definition id and rebuild copies", () =>
		{
			var set = CrateSet();
			var d = set.Definitions[0];
			var a = set.LinkCopy( d.Id, Vec3.Zero ).Instance!;
			var b = set.LinkCopy( d.Id, new Vec3( 128, 0, 0 ) ).Instance!;
			var records = set.ToInstanceRecords();
			Require( records.Count == 2, $"expected 2 records, got {records.Count}" );
			Require( records.All( r => r.DefinitionId == d.Id ), "records do not share the definition id" );
			Require( records.First( r => r.InstanceId == b.Id ).Offset.AlmostEquals( new Vec3( 128, 0, 0 ) ), "record offset" );
			var rebuilt = new LinkedGroupSet();
			var defined = rebuilt.DefineLinkedGroup( "crate-stack", d.Members.Select( m => m.Clone() ), layer: "detail", visGroup: "props" );
			Require( defined.Ok, "rebuilt definition failed" );
			var loaded = rebuilt.FromInstanceRecords( records.Select( r => new LinkedGroupInstanceRecord
			{
				InstanceId = r.InstanceId,
				DefinitionId = defined.Definition!.Id,
				OffsetX = r.OffsetX, OffsetY = r.OffsetY, OffsetZ = r.OffsetZ,
				Layer = r.Layer, VisGroup = r.VisGroup,
			} ).ToList() );
			Require( loaded.Ok, $"records rebuild failed: {loaded.Reason} {loaded.Message}" );
			Require( rebuilt.Instances.Count == 2, "rebuilt instance count" );
			RequireVertex( Resolved( rebuilt, b.Id, "m-crate" ), new Vec3( 128, 0, 0 ), "rebuilt copy B crate" );
			RequireReason( rebuilt.FromInstanceRecords( new[]
			{
				new LinkedGroupInstanceRecord { InstanceId = "lgi-99", DefinitionId = "lg-404" },
			} ), "linkedgroups-definition-not-found", "record pointing at a missing definition" );
			Require( rebuilt.Instances.Count == 2, "failed rebuild added instances anyway" );
		} );

		Check( "linked groups nest inside layers and visgroups and hide with them", () =>
		{
			var set = CrateSet();
			var d = set.Definitions[0];
			var a = set.LinkCopy( d.Id, Vec3.Zero ).Instance!;
			var b = set.LinkCopy( d.Id, new Vec3( 128, 0, 0 ), layer: "world", visGroup: "props" ).Instance!;
			Require( set.InstancesInLayer( "detail" ).Count == 1 && set.InstancesInLayer( "detail" )[0].Id == a.Id,
				"copy A should inherit the definition layer 'detail'" );
			Require( set.InstancesInLayer( "world" ).Count == 1 && set.InstancesInLayer( "world" )[0].Id == b.Id,
				"copy B should sit in its own layer 'world'" );
			Require( set.InstancesInVisGroup( "props" ).Count == 2, "both copies are in visgroup 'props'" );
			var visible = set.VisibleInstances( new[] { "props" } );
			Require( visible.Count == 0, "hiding visgroup 'props' should hide both copies" );
			var moved = set.SetInstanceVisGroup( b.Id, "hidden-stuff" );
			Require( moved.Ok, "moving copy B to another visgroup failed" );
			Require( set.VisibleInstances( new[] { "props" } ).Count == 1
				&& set.VisibleInstances( new[] { "props" } )[0].Id == b.Id,
				"only copy B should stay visible once it leaves 'props'" );
			RequireReason( set.SetInstanceLayer( "lgi-404", "world" ), "linkedgroups-instance-not-found", "unknown instance layer move" );
			var reloaded = LinkedGroupSet.FromJson( set.ToJson() );
			Require( reloaded.InstancesInLayer( "world" ).Count == 1, "layer nesting did not survive the round-trip" );
			Require( reloaded.VisibleInstances( new[] { "props" } ).Count == 1, "visgroup nesting did not survive the round-trip" );
		} );
	}
}
