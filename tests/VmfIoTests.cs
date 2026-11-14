using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy.Map;

// Hammer Desktop P1: VMF + Valve 220 .map import/export with plane fidelity.
// Fixtures live in tests/fixtures/hammer/hammer-vmf-map-import-export/:
//   room.vmf — sealed 544x544x256 room (6 world brushes, floor-top displacement
//     power 3) + func_door + func_button + logic_auto/logic_relay I/O +
//     info_player_start + light + 2 visgroups + camera + cordon.
//   room.map — the same room as Valve 220 .map (no displacement / I/O: the
//     format has nowhere to put them).
//   bad.vmf / bad.map — malformed input, for the named-error checks.
// The dependency types (MapBrush / Displacement / EntityIo / MapLayers /
// MapDocument) have not landed yet, so these tests exercise the transfer
// model (VmfDocument) directly; the adapter seam is documented in VmfIo.cs.
internal static class VmfIoTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "vmfio", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		string Fixture( string name )
		{
			var tried = new List<string>();
			string Here( string path ) => Path.GetFullPath( path );
			var candidates = new List<string>
			{
				Here( Path.Combine( "tests", "fixtures", "hammer", "hammer-vmf-map-import-export", name ) ),
				Here( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "fixtures", "hammer", "hammer-vmf-map-import-export", name ) ),
			};
			// Walk up from the working directory in case the runner starts elsewhere.
			var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
			for ( int i = 0; i < 5 && dir != null; i++, dir = dir.Parent )
				candidates.Add( Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-vmf-map-import-export", name ) );
			foreach ( var c in candidates )
			{
				tried.Add( c );
				if ( File.Exists( c ) ) return c;
			}
			throw new Exception( $"fixture '{name}' not found; tried: {string.Join( "; ", tried )}" );
		}

		VmfDocument RoomVmf() => VmfIo.ParseFile( Fixture( "room.vmf" ) );
		VmfDocument RoomMap() => Map220Io.ParseFile( Fixture( "room.map" ) );

		void RequireSameFaces( VmfDocument a, VmfDocument b, string what )
		{
			var fa = a.AllBrushes().SelectMany( br => br.Faces ).ToList();
			var fb = b.AllBrushes().SelectMany( br => br.Faces ).ToList();
			Require( fa.Count == fb.Count, $"{what}: face count {fa.Count} != {fb.Count}" );
			for ( int i = 0; i < fa.Count; i++ )
			{
				Require( fa[i].PlanePointsEqual( fb[i] ), $"{what}: face {i} plane points changed" );
				Require( fa[i].Material == fb[i].Material, $"{what}: face {i} material '{fa[i].Material}' != '{fb[i].Material}'" );
				Require( fa[i].UAxis.ApproxEquals( fb[i].UAxis, 1e-9 ), $"{what}: face {i} uaxis changed" );
				Require( fa[i].VAxis.ApproxEquals( fb[i].VAxis, 1e-9 ), $"{what}: face {i} vaxis changed" );
				Require( Math.Abs( fa[i].Rotation - fb[i].Rotation ) < 1e-9, $"{what}: face {i} rotation changed" );
				Require( fa[i].LightmapScale == fb[i].LightmapScale, $"{what}: face {i} lightmapscale changed" );
				Require( fa[i].SmoothingGroups == fb[i].SmoothingGroups, $"{what}: face {i} smoothing_groups changed" );
			}
		}

		Check( "vmf fixture parses: sealed room, door, button, logic, player start, light", () =>
		{
			var doc = RoomVmf();
			Require( doc.WorldBrushes.Count == 6, $"world brushes {doc.WorldBrushes.Count}" );
			Require( doc.Entities.Count == 6, $"entities {doc.Entities.Count}" );
			Require( doc.VisGroups.Count == 2, $"visgroups {doc.VisGroups.Count}" );
			Require( doc.Cameras.Count == 1, $"cameras {doc.Cameras.Count}" );
			Require( doc.HasCordon, "cordon missing" );
			Require( doc.WorldspawnGet( "skyname" ) == "sky_day01_01", "skyname missing" );
			Require( doc.Entities.Any( e => e.ClassName == "func_door" && e.TargetName == "door_01" ), "func_door door_01 missing" );
			Require( doc.Entities.Any( e => e.ClassName == "info_player_start" && e.Get( "origin" ) == "0 0 16" ), "player start missing" );
			Require( doc.Entities.Any( e => e.ClassName == "light" ), "light missing" );
			var defects = doc.Validate();
			Require( defects.Count == 0, "validation defects: " + string.Join( "; ", defects ) );
		} );

		Check( "vmf round-trip preserves planes, materials and UVs exactly", () =>
		{
			var doc = RoomVmf();
			var again = VmfIo.Parse( VmfIo.Write( doc ) );
			RequireSameFaces( doc, again, "vmf round-trip" );
			Require( again.Validate().Count == 0, "re-parsed doc failed validation" );
			Require( again.OtherBlocks.Any( b => b.Name == "viewsettings" ), "unknown block 'viewsettings' was not preserved" );
			Require( again.WorldspawnGet( "skyname" ) == "sky_day01_01", "skyname lost" );
		} );

		Check( "vmf displacement round-trips (power 3, 9x9 grids, offsets and alphas)", () =>
		{
			var doc = RoomVmf();
			var floorTop = doc.WorldBrushes[0].Faces[4];
			Require( floorTop.Displacement != null, "floor top face has no displacement" );
			var d = floorTop.Displacement;
			Require( d.Power == 3 && d.GridSize == 9, $"power/grid {d.Power}/{d.GridSize}" );
			Require( d.StartPosition.ApproxEquals( new VmfVec( -272, -272, 0 ) ), "startposition moved" );
			Require( d.Offsets[4, 4] == 12 && d.Offsets[3, 3] == 8 && d.Offsets[0, 0] == 0, "offsets wrong" );
			Require( d.Alphas[0, 0] == 255 && d.Alphas[4, 4] == 0, "alphas wrong" );
			var again = VmfIo.Parse( VmfIo.Write( doc ) );
			var d2 = again.WorldBrushes[0].Faces[4].Displacement;
			Require( d2 != null && d2.Power == 3, "displacement lost in round-trip" );
			Require( d2.Offsets[4, 4] == 12 && d2.Offsets[3, 3] == 8, "offsets lost in round-trip" );
			Require( d2.Alphas[0, 0] == 255 && d2.Alphas[8, 8] == 255, "alphas lost in round-trip" );
			Require( d2.Normals[4, 4].ApproxEquals( new VmfVec( 0, 0, 1 ) ), "normals lost in round-trip" );
		} );

		Check( "vmf entity I/O round-trips (button, logic_auto, logic_relay)", () =>
		{
			var doc = RoomVmf();
			var button = doc.Entities.Single( e => e.TargetName == "button_01" );
			Require( button.Outputs.Count == 1, $"button outputs {button.Outputs.Count}" );
			var o = button.Outputs[0];
			Require( o.Event == "OnPressed" && o.Target == "door_01" && o.Input == "Open" && o.Delay == 0 && o.FireCount == -1,
				$"button output wrong: {o.Event} {o.ToVmfString()}" );
			var auto = doc.Entities.Single( e => e.ClassName == "logic_auto" );
			Require( auto.Outputs.Count == 1 && auto.Outputs[0].Event == "OnMapSpawn" && auto.Outputs[0].Target == "door_relay", "logic_auto output wrong" );
			var relay = doc.Entities.Single( e => e.TargetName == "door_relay" );
			Require( relay.Outputs.Count == 1 && relay.Outputs[0].Delay == 0.5 && relay.Outputs[0].Input == "Open", "logic_relay output wrong" );
			var (missing, ambiguous) = doc.ResolveOutputs();
			Require( missing.Count == 0 && ambiguous.Count == 0, $"unresolved outputs: missing [{string.Join( "; ", missing )}] ambiguous [{string.Join( "; ", ambiguous )}]" );
			var again = VmfIo.Parse( VmfIo.Write( doc ) );
			var button2 = again.Entities.Single( e => e.TargetName == "button_01" );
			Require( button2.Outputs.Count == 1 && button2.Outputs[0].ToVmfString() == "door_01,Open,,0,-1", "button output lost in round-trip" );
		} );

		Check( "resolve outputs reports a missing target and an ambiguous target", () =>
		{
			var doc = new VmfDocument();
			var a = new VmfEntity { Id = 1, ClassName = "func_button", };
			a.Set( "targetname", "a" );
			a.Outputs.Add( new VmfOutput { Event = "OnPressed", Target = "ghost", Input = "Open", Delay = 0, FireCount = -1 } );
			a.Outputs.Add( new VmfOutput { Event = "OnPressed", Target = "dup", Input = "Open", Delay = 0, FireCount = -1 } );
			a.Outputs.Add( new VmfOutput { Event = "OnPressed", Target = "!player", Input = "Ignite", Delay = 0, FireCount = -1 } );
			doc.Entities.Add( a );
			var b = new VmfEntity { Id = 2, ClassName = "info_target" };
			b.Set( "targetname", "dup" );
			var c = new VmfEntity { Id = 3, ClassName = "info_target" };
			c.Set( "targetname", "dup" );
			doc.Entities.Add( b );
			doc.Entities.Add( c );
			var (missing, ambiguous) = doc.ResolveOutputs();
			Require( missing.Count == 1 && missing[0].Contains( "ghost" ), "missing target 'ghost' not reported: " + string.Join( "; ", missing ) );
			Require( ambiguous.Count == 1 && ambiguous[0].Contains( "dup" ), "ambiguous target 'dup' not reported: " + string.Join( "; ", ambiguous ) );
			Require( !missing.Any( m => m.Contains( "!player" ) ) && !ambiguous.Any( m => m.Contains( "!player" ) ), "engine keyword '!player' must not be resolved as a targetname" );
		} );

		Check( "valve 220 .map fixture imports with correct UV axes", () =>
		{
			var doc = RoomMap();
			Require( doc.WorldBrushes.Count == 6, $"world brushes {doc.WorldBrushes.Count}" );
			Require( doc.Entities.Any( e => e.ClassName == "func_door" && e.TargetName == "door_01" ), "func_door missing" );
			Require( doc.Entities.Any( e => e.ClassName == "info_player_start" && e.Get( "origin" ) == "0 0 16" ), "player start missing" );
			var floorX = doc.WorldBrushes[0].Faces[0];
			Require( floorX.UAxis.Axis.ApproxEquals( new VmfVec( 0, 1, 0 ) ) && floorX.UAxis.Offset == 0 && floorX.UAxis.Scale == 0.25,
				$"floor +X uaxis wrong: {floorX.UAxis.ToVmfString()}" );
			Require( floorX.VAxis.Axis.ApproxEquals( new VmfVec( 0, 0, -1 ) ) && floorX.VAxis.Scale == 0.25, "floor +X vaxis wrong" );
			Require( floorX.Rotation == 0, "floor +X rotation wrong" );
			Require( floorX.Material == "BRICK/BRICKWALL001A", "floor material wrong" );
			Require( floorX.Normal.ApproxEquals( new VmfVec( 1, 0, 0 ) ), $"floor +X normal wrong: {floorX.Normal}" );
		} );

		Check( "map220 round-trip preserves planes, materials and UV axes", () =>
		{
			var doc = RoomMap();
			var again = Map220Io.Parse( Map220Io.Write( doc ) );
			RequireSameFaces( doc, again, "map220 round-trip" );
		} );

		Check( "vmf to map220 preserves planes and UV axes; displacement and I/O are warned, not silently dropped", () =>
		{
			var doc = RoomVmf();
			List<string> warnings;
			var mapText = Map220Io.Write( doc, out warnings );
			Require( warnings.Any( w => w.Contains( "displacement" ) ), "displacement drop was not warned: " + string.Join( "; ", warnings ) );
			Require( warnings.Any( w => w.Contains( "I/O" ) ), "I/O drop was not warned: " + string.Join( "; ", warnings ) );
			var again = Map220Io.Parse( mapText );
			Require( again.WorldBrushes.Count == 6, "world brushes lost in vmf->map" );
			RequireSameFaces( doc, again, "vmf->map220" );
		} );

		Check( "map220 to vmf preserves planes", () =>
		{
			var doc = RoomMap();
			var again = VmfIo.Parse( VmfIo.Write( doc ) );
			RequireSameFaces( doc, again, "map220->vmf" );
			Require( again.WorldspawnGet( "skyname" ) == "sky_day01_01", "skyname lost in map220->vmf" );
		} );

		Check( "malformed vmf throws a named parse error with a line number", () =>
		{
			try { VmfIo.ParseFile( Fixture( "bad.vmf" ) ); }
			catch ( MapParseException ex )
			{
				Require( ex.Format == "vmf", $"format {ex.Format}" );
				Require( ex.Line > 0, "line number missing" );
				Require( ex.Message.Contains( "line " + ex.Line ), $"message does not name the line: {ex.Message}" );
				return;
			}
			throw new Exception( "malformed vmf parsed without an error" );
		} );

		Check( "malformed map throws a named parse error with a line number", () =>
		{
			try { Map220Io.ParseFile( Fixture( "bad.map" ) ); }
			catch ( MapParseException ex )
			{
				Require( ex.Format == "map220", $"format {ex.Format}" );
				Require( ex.Line > 0, "line number missing" );
				Require( ex.Message.Contains( "line " + ex.Line ), $"message does not name the line: {ex.Message}" );
				return;
			}
			throw new Exception( "malformed .map parsed without an error" );
		} );

		Check( "writer emits planes from the face's plane points, not from triangulated vertices", () =>
		{
			var doc = new VmfDocument();
			var brush = new VmfBrush { Id = 1 };
			// Points on the z=0 plane that are NOT the corners of any
			// triangulated mesh of this face: a writer that rebuilt the
			// plane from vertices would emit different points.
			brush.Faces.Add( new VmfFace
			{
				Id = 1,
				PlanePoints = new[] { new VmfVec( 10, 10, 0 ), new VmfVec( 110, 10, 0 ), new VmfVec( 110, 110, 0 ) },
				Material = "BRICK/BRICKWALL001A",
			} );
			doc.WorldBrushes.Add( brush );
			var text = VmfIo.Write( doc );
			Require( text.Contains( "\"plane\" \"( 10 10 0 ) ( 110 10 0 ) ( 110 110 0 )\"" ),
				"vmf writer did not emit the face's stored plane points: " + text );
			var mapText = Map220Io.Write( doc );
			Require( mapText.Contains( "( 10 10 0 ) ( 110 10 0 ) ( 110 110 0 )" ),
				"map writer did not emit the face's stored plane points: " + mapText );
			Require( brush.Faces[0].Normal.ApproxEquals( new VmfVec( 0, 0, 1 ) ), "plane normal wrong" );
			Require( Math.Abs( brush.Faces[0].Distance ) < 1e-9, "plane distance wrong" );
		} );

		Check( "displacement validation rejects power 5 and a mismatched grid", () =>
		{
			bool threw = false;
			try { VmfDisplacement.Create( 5, VmfVec.Zero ); } catch ( ArgumentException ) { threw = true; }
			Require( threw, "power 5 displacement was created without an error" );
			var d = new VmfDisplacement( 3 ) { Offsets = new double[5, 5] };
			var defects = d.Validate();
			Require( defects.Count > 0 && defects.Any( x => x.Contains( "9x9" ) ), "mismatched offsets grid not reported: " + string.Join( "; ", defects ) );
			var ok = VmfDisplacement.Create( 3, VmfVec.Zero );
			Require( ok.Validate().Count == 0, "a fresh power-3 displacement should validate" );
		} );

		Check( "visgroups and worldspawn keyvalues round-trip", () =>
		{
			var doc = RoomVmf();
			var again = VmfIo.Parse( VmfIo.Write( doc ) );
			Require( again.VisGroups.Count == 2, $"visgroups {again.VisGroups.Count}" );
			Require( again.VisGroups[0].Name == "Structure" && again.VisGroups[0].Id == 1 && again.VisGroups[0].Color == "255 0 0", "visgroup 1 wrong" );
			Require( again.VisGroups[1].Name == "Entities" && again.VisGroups[1].Id == 2, "visgroup 2 wrong" );
			Require( again.WorldBrushes[0].VisGroupId == 1, "floor brush visgroupid lost" );
			var doorBrush = again.Entities.Single( e => e.TargetName == "door_01" ).Brushes[0];
			Require( doorBrush.VisGroupId == 2, "door brush visgroupid lost" );
			Require( again.Cameras.Count == 1 && again.Cameras[0].Position.ApproxEquals( new VmfVec( 0, -400, 200 ) ), "camera lost" );
		} );

		Check( "legacy standard .map face line imports with default axes", () =>
		{
			var text = "// Game: Half-Life 2\n// entity 0\n{\n\"classname\" \"worldspawn\"\n// brush 1\n{\n( 0 0 0 ) ( 64 0 0 ) ( 64 64 0 ) BRICK/BRICKWALL001A 32 64 45 0.5 0.5\n}\n}\n";
			var doc = Map220Io.Parse( text );
			Require( doc.WorldBrushes.Count == 1 && doc.WorldBrushes[0].Faces.Count == 1, "legacy brush missing" );
			var f = doc.WorldBrushes[0].Faces[0];
			Require( f.UvFromStandardMap, "legacy face not flagged as standard UV" );
			Require( f.UAxis.Offset == 32 && f.UAxis.Scale == 0.5, $"legacy uaxis wrong: {f.UAxis.ToVmfString()}" );
			Require( f.VAxis.Offset == 64 && f.VAxis.Scale == 0.5, "legacy vaxis wrong" );
			Require( f.Rotation == 45, "legacy rotation wrong" );
			Require( Map220Io.Write( doc ).Contains( "32 64 45 0.5 0.5" ), "legacy face was not written back in legacy form" );
		} );
	}
}
