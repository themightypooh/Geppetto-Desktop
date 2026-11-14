#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// One-call sealed room + doorway/window cutout helpers (see
/// kernel/Effigy/Map/RoomHelpers.cs, board hammer-room-doorway-helpers).
///
/// Acceptance numbers live in
/// tests/fixtures/hammer/hammer-room-doorway-helpers/*.json and the checks
/// below recompute them from those files; the same cases are also built
/// synthetically, so a missing fixture can never hide a geometry failure.
/// Volumes are asserted on SignedVolume (positive = outward winding) and
/// every brush must be a closed solid. Sealed-ness is asserted through the
/// leak finder, not by construction alone: a room passes only when
/// LeakFinder.FindLeak over the graph RoomHelpers builds from the room's
/// own geometry finds no path to the void.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// RoomHelpersTests.Register(registry, settings); line.
/// </summary>
internal static class RoomHelpersTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "roomhelpers", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: wrong exception {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		bool Near( float a, float b, float tol = 1e-3f ) => Math.Abs( a - b ) <= tol * Math.Max( 1f, Math.Abs( b ) );
		bool NearVec( Vec3 a, Vec3 b ) => a.AlmostEquals( b, 1e-3f );

		static bool Closed( PolyMesh? m ) => m != null && m.FaceCount > 0 && MeshValidator.Validate( m ).IsClosed;

		void RequireVolume( PolyMesh mesh, float expected, string message )
		{
			float got = mesh.SignedVolume();
			Require( got > 0, $"{message}: signed volume {got} is not positive, so the winding is inward" );
			Require( Near( got, expected ), $"{message}: volume {got:0.###} vs expected {expected:0.###}" );
		}

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-room-doorway-helpers" );
					if ( File.Exists( Path.Combine( candidate, "room-128.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "roomhelpers fixture dir not found (room-128.json missing above cwd / BaseDirectory)" );
		}

		JsonDocument Fixture( string name ) => JsonDocument.Parse( File.ReadAllText( Path.Combine( FixtureDir(), name ) ) );

		RoomResult Room128() => RoomHelpers.CreateRoom( 128f, 128f, 128f, 8f );

		Check( "create-room-six-closed-brushes-exact-volumes", () =>
		{
			using var doc = Fixture( "room-128.json" );
			var exp = doc.RootElement.GetProperty( "expected" );
			var room = Room128();
			Require( room.Ok, $"room creation failed: {room.Reason} {room.Message}" );
			Require( room.Brushes.Count == exp.GetProperty( "brushCount" ).GetInt32(),
				$"room has {room.Brushes.Count} brushes, expected {exp.GetProperty( "brushCount" ).GetInt32()}" );
			float total = 0f;
			foreach ( var b in room.Brushes )
			{
				Require( Closed( b.Mesh ), $"brush {b.Name} is not a closed solid" );
				Require( b.Mesh.FaceCount == 6, $"brush {b.Name} has {b.Mesh.FaceCount} faces, expected 6 (a box)" );
				Require( b.FaceMaterials.Count == b.Mesh.FaceCount, $"brush {b.Name} face/material counts differ" );
				total += b.Mesh.SignedVolume();
			}
			RequireVolume( room.Brushes.First( b => b.Name == "Floor" ).Mesh, exp.GetProperty( "floorVolume" ).GetSingle(), "floor" );
			RequireVolume( room.Brushes.First( b => b.Name == "Ceiling" ).Mesh, exp.GetProperty( "ceilingVolume" ).GetSingle(), "ceiling" );
			RequireVolume( room.FindBrush( RoomWall.North )!.Mesh, exp.GetProperty( "northWallVolume" ).GetSingle(), "north wall" );
			RequireVolume( room.FindBrush( RoomWall.South )!.Mesh, exp.GetProperty( "southWallVolume" ).GetSingle(), "south wall" );
			RequireVolume( room.FindBrush( RoomWall.East )!.Mesh, exp.GetProperty( "eastWallVolume" ).GetSingle(), "east wall" );
			RequireVolume( room.FindBrush( RoomWall.West )!.Mesh, exp.GetProperty( "westWallVolume" ).GetSingle(), "west wall" );
			Require( Near( total, exp.GetProperty( "totalVolume" ).GetSingle() ),
				$"total solid volume {total:0.###} vs expected {exp.GetProperty( "totalVolume" ).GetSingle():0.###}" );
			Require( NearVec( room.PlayerStart, new Vec3( 0, 0, 32 ) ), $"player start {room.PlayerStart}, expected (0, 0, 32)" );
			Require( NearVec( room.InteriorMin, new Vec3( -64, -64, 0 ) ), $"interior min {room.InteriorMin}" );
			Require( NearVec( room.InteriorMax, new Vec3( 64, 64, 128 ) ), $"interior max {room.InteriorMax}" );
		} );

		Check( "created-room-passes-leak-finder", () =>
		{
			var room = Room128();
			Require( room.IsSealed, $"created room is not sealed: {room.Reason} {room.Message}" );
			Require( !room.LeakCheck.IsLeaked, "leak finder reported a leak in a fresh room" );
			Require( room.LeakCheck.Path.Count == 0, $"sealed room has a leak path of {room.LeakCheck.Path.Count} points" );
			Require( room.LeakCheck.GapLeafId == -1 && room.LeakCheck.GapPortalId == -1,
				$"sealed gap ids {room.LeakCheck.GapLeafId}/{room.LeakCheck.GapPortalId}" );
			Require( room.LeakCheck.ToPointFileText() == string.Empty, "sealed room exported a point file" );
			Require( room.LeakCheck.VisitedLeafIds.SequenceEqual( new[] { 0 } ),
				"flood-fill visited " + string.Join( ",", room.LeakCheck.VisitedLeafIds ) );
			Require( room.Openings.Count == 0, $"fresh room has {room.Openings.Count} openings" );
		} );

		Check( "unseen-faces-are-nodraw-visible-faces-are-dev", () =>
		{
			using var doc = Fixture( "room-128.json" );
			var exp = doc.RootElement.GetProperty( "expected" );
			var room = Room128();
			var visible = 0;
			var nodraw = 0;
			foreach ( var b in room.Brushes )
			{
				var seenOnThisBrush = 0;
				for ( var i = 0; i < b.FaceMaterials.Count; i++ )
				{
					var name = b.FaceMaterials[i];
					if ( ToolTexture.IsNoDraw( name ) ) nodraw++;
					else
					{
						visible++;
						seenOnThisBrush++;
						Require( name == RoomHelpers.DefaultDevMaterial,
							$"brush {b.Name} visible face uses {name}, expected dev {RoomHelpers.DefaultDevMaterial}" );
					}
					Require( room.Materials[b.Mesh.Faces[i].Material] == name,
						$"brush {b.Name} face {i} slot does not match its material name" );
				}
				Require( seenOnThisBrush == 1, $"brush {b.Name} has {seenOnThisBrush} visible faces, expected exactly 1" );
			}
			Require( visible == exp.GetProperty( "visibleFaces" ).GetInt32(), $"visible faces {visible}" );
			Require( nodraw == exp.GetProperty( "noDrawFaces" ).GetInt32(), $"nodraw faces {nodraw}" );
			Require( room.Materials.Contains( ToolTexture.NoDrawName ), "material table has no nodraw entry" );
			Require( room.Materials.Contains( RoomHelpers.DefaultDevMaterial ), "material table has no dev entry" );
		} );

		Check( "dev-textures-off-uses-per-part-materials", () =>
		{
			var options = new RoomOptions
			{
				DevTextures = false,
				WallMaterial = "brick/brickwall01",
				FloorMaterial = "concrete/concretefloor01",
				CeilingMaterial = "plaster/plasterceiling01",
			};
			var room = RoomHelpers.CreateRoom( 128f, 128f, 128f, 8f, options );
			Require( room.IsSealed, $"room with custom materials is not sealed: {room.Reason}" );
			var wall = room.FindBrush( RoomWall.North )!;
			Require( wall.FaceMaterials.Contains( "brick/brickwall01" ), "north wall has no brick visible face" );
			Require( room.Brushes.First( b => b.Name == "Floor" ).FaceMaterials.Contains( "concrete/concretefloor01" ),
				"floor has no concrete visible face" );
			Require( room.Brushes.First( b => b.Name == "Ceiling" ).FaceMaterials.Contains( "plaster/plasterceiling01" ),
				"ceiling has no plaster visible face" );
			var nodraw = room.Brushes.Sum( b => b.FaceMaterials.Count( ToolTexture.IsNoDraw ) );
			Require( nodraw == 30, $"nodraw faces {nodraw}, expected 30 (unseen faces stay nodraw with custom materials)" );
			Require( !room.Materials.Contains( RoomHelpers.DefaultDevMaterial ), "dev material leaked into a non-dev room" );
		} );

		Check( "doorway-cut-exact-size-keeps-room-sealed", () =>
		{
			using var doc = Fixture( "doorway-128.json" );
			var exp = doc.RootElement.GetProperty( "expected" );
			var room = RoomHelpers.CutDoorway( Room128(), RoomWall.North, 48f, 96f );
			Require( room.Ok, $"doorway cut refused: {room.Reason} {room.Message}" );
			Require( room.IsSealed, $"room with a doorway is not sealed: {room.Reason} {room.Message}" );
			Require( room.Openings.Count == 1, $"openings {room.Openings.Count}, expected 1" );
			var opening = room.Openings[0];
			Require( opening.Kind == RoomOpeningKind.Doorway, $"opening kind {opening.Kind}" );
			Require( opening.Wall == RoomWall.North, $"opening wall {opening.Wall}" );
			Require( Near( opening.U0, exp.GetProperty( "openingU0" ).GetSingle() ), $"opening U0 {opening.U0}" );
			Require( Near( opening.U1, exp.GetProperty( "openingU1" ).GetSingle() ), $"opening U1 {opening.U1}" );
			Require( Near( opening.V0, exp.GetProperty( "openingV0" ).GetSingle() ), $"opening V0 {opening.V0}" );
			Require( Near( opening.V1, exp.GetProperty( "openingV1" ).GetSingle() ), $"opening V1 {opening.V1}" );
			var wall = room.FindBrush( RoomWall.North )!;
			Require( Closed( wall.Mesh ), "cut north wall is not a closed solid" );
			RequireVolume( wall.Mesh, exp.GetProperty( "northWallVolumeAfterCut" ).GetSingle(), "cut north wall" );
			var center = new Vec3(
				exp.GetProperty( "openingCenter" )[0].GetSingle(),
				exp.GetProperty( "openingCenter" )[1].GetSingle(),
				exp.GetProperty( "openingCenter" )[2].GetSingle() );
			Require( NearVec( RoomHelpers.OpeningCenter( room, opening ), center ),
				$"opening centre {RoomHelpers.OpeningCenter( room, opening )}, expected {center}" );
			Require( !RoomHelpers.PointInSolid( center, wall.Mesh ), "opening centre is still solid — the doorway did not cut through" );
			Require( RoomHelpers.PointInSolid( new Vec3( 48, 68, 48 ), wall.Mesh ), "right jamb is missing (point 48,68,48 is not solid)" );
			Require( RoomHelpers.PointInSolid( new Vec3( -48, 68, 48 ), wall.Mesh ), "left jamb is missing" );
			Require( RoomHelpers.PointInSolid( new Vec3( 0, 68, 112 ), wall.Mesh ), "lintel is missing (point 0,68,112 is not solid)" );
		} );

		Check( "doorway-opening-edges-measured-by-scan", () =>
		{
			var room = RoomHelpers.CutDoorway( Room128(), RoomWall.North, 48f, 96f );
			Require( room.Ok, $"doorway cut refused: {room.Reason}" );
			var wall = room.FindBrush( RoomWall.North )!.Mesh;
			// Half-unit either side of each expected edge: outside the opening
			// the wall is solid, inside it is void. The edges are exact to
			// within 1 unit (the sample spacing), not just "about right".
			Require( !RoomHelpers.PointInSolid( new Vec3( 23.5f, 68, 48 ), wall ), "point just inside the right edge is solid" );
			Require( RoomHelpers.PointInSolid( new Vec3( 24.5f, 68, 48 ), wall ), "point just outside the right edge is void" );
			Require( !RoomHelpers.PointInSolid( new Vec3( -23.5f, 68, 48 ), wall ), "point just inside the left edge is solid" );
			Require( RoomHelpers.PointInSolid( new Vec3( -24.5f, 68, 48 ), wall ), "point just outside the left edge is void" );
			Require( !RoomHelpers.PointInSolid( new Vec3( 0, 68, 95.5f ), wall ), "point just under the lintel is solid" );
			Require( RoomHelpers.PointInSolid( new Vec3( 0, 68, 96.5f ), wall ), "point just over the lintel is void" );
		} );

		Check( "window-cut-preserves-sill", () =>
		{
			using var doc = Fixture( "window-128.json" );
			var exp = doc.RootElement.GetProperty( "expected" );
			var room = RoomHelpers.CutWindow( Room128(), RoomWall.East, 64f, 48f, 32f );
			Require( room.Ok, $"window cut refused: {room.Reason} {room.Message}" );
			Require( room.IsSealed, $"room with a window is not sealed: {room.Reason} {room.Message}" );
			var opening = room.Openings[0];
			Require( opening.Kind == RoomOpeningKind.Window, $"opening kind {opening.Kind}" );
			Require( Near( opening.U0, exp.GetProperty( "openingU0" ).GetSingle() ), $"opening U0 {opening.U0}" );
			Require( Near( opening.U1, exp.GetProperty( "openingU1" ).GetSingle() ), $"opening U1 {opening.U1}" );
			Require( Near( opening.V0, exp.GetProperty( "openingV0" ).GetSingle() ), $"opening V0 {opening.V0}" );
			Require( Near( opening.V1, exp.GetProperty( "openingV1" ).GetSingle() ), $"opening V1 {opening.V1}" );
			var wall = room.FindBrush( RoomWall.East )!;
			Require( Closed( wall.Mesh ), "cut east wall is not a closed solid" );
			RequireVolume( wall.Mesh, exp.GetProperty( "eastWallVolumeAfterCut" ).GetSingle(), "cut east wall" );
			var center = new Vec3(
				exp.GetProperty( "openingCenter" )[0].GetSingle(),
				exp.GetProperty( "openingCenter" )[1].GetSingle(),
				exp.GetProperty( "openingCenter" )[2].GetSingle() );
			Require( !RoomHelpers.PointInSolid( center, wall.Mesh ), "window centre is still solid — the window did not cut through" );
			Require( RoomHelpers.PointInSolid( new Vec3( 68, 0, 16 ), wall.Mesh ), "sill is missing (point 68,0,16 is not solid)" );
			Require( RoomHelpers.PointInSolid( new Vec3( 68, 0, 104 ), wall.Mesh ), "lintel is missing (point 68,0,104 is not solid)" );
			Require( RoomHelpers.PointInSolid( new Vec3( 68, 40, 56 ), wall.Mesh ), "jamb is missing (point 68,40,56 is not solid)" );
		} );

		Check( "window-with-one-unit-sill-and-lintel-cuts-and-stays-sealed", () =>
		{
			var room = RoomHelpers.CutWindow( Room128(), RoomWall.East, 64f, 126f, 1f );
			Require( room.Ok, $"1-unit sill/lintel window refused: {room.Reason} {room.Message}" );
			Require( room.IsSealed, $"room with a 1-unit sill/lintel window is not sealed: {room.Reason}" );
			var wall = room.FindBrush( RoomWall.East )!;
			RequireVolume( wall.Mesh, 131072f - 64f * 126f * 8f, "cut east wall" );
			Require( RoomHelpers.PointInSolid( new Vec3( 68, 0, 0.5f ), wall.Mesh ), "1-unit sill is missing" );
			Require( RoomHelpers.PointInSolid( new Vec3( 68, 0, 127.5f ), wall.Mesh ), "1-unit lintel is missing" );
			Require( !RoomHelpers.PointInSolid( new Vec3( 68, 0, 64 ), wall.Mesh ), "window centre is still solid" );
		} );

		Check( "doorway-then-window-different-walls-stays-sealed", () =>
		{
			var room = RoomHelpers.CutDoorway( Room128(), RoomWall.North, 48f, 96f );
			Require( room.Ok, $"doorway refused: {room.Reason}" );
			room = RoomHelpers.CutWindow( room, RoomWall.East, 64f, 48f, 32f );
			Require( room.Ok, $"window refused: {room.Reason} {room.Message}" );
			Require( room.IsSealed, $"two-opening room is not sealed: {room.Reason} {room.Message}" );
			Require( room.Openings.Count == 2, $"openings {room.Openings.Count}, expected 2" );
			RequireVolume( room.FindBrush( RoomWall.North )!.Mesh, 147456f - 48f * 96f * 8f, "north wall after doorway" );
			RequireVolume( room.FindBrush( RoomWall.East )!.Mesh, 131072f - 64f * 48f * 8f, "east wall after window" );
			RequireVolume( room.FindBrush( RoomWall.South )!.Mesh, 147456f, "south wall untouched" );
			Require( Closed( room.FindBrush( RoomWall.North )!.Mesh ), "north wall not closed after two cuts" );
			Require( Closed( room.FindBrush( RoomWall.East )!.Mesh ), "east wall not closed after two cuts" );
		} );

		Check( "doorway-too-wide-rejected-with-named-reason", () =>
		{
			var before = Room128();
			var after = RoomHelpers.CutDoorway( before, RoomWall.North, 144f, 96f );
			Require( !after.Ok, "a 144-wide doorway in a 144-long wall was accepted" );
			Require( after.Reason == "room-opening-too-wide", $"reason {after.Reason}, expected room-opening-too-wide" );
			Require( after.IsSealed, "a refused cut left the room unsealed" );
			RequireVolume( after.FindBrush( RoomWall.North )!.Mesh, 147456f, "north wall after refused cut" );
			Require( after.Openings.Count == 0, "a refused cut recorded an opening" );
			Require( ReferenceEquals( after.Brushes, before.Brushes ) || after.Brushes.Count == before.Brushes.Count,
				"a refused cut changed the brush set" );
		} );

		Check( "doorway-without-lintel-rejected", () =>
		{
			var after = RoomHelpers.CutDoorway( Room128(), RoomWall.North, 48f, 128f );
			Require( !after.Ok, "a floor-to-ceiling doorway was accepted" );
			Require( after.Reason == "room-opening-no-lintel", $"reason {after.Reason}, expected room-opening-no-lintel" );
			Require( after.IsSealed, "a refused cut left the room unsealed" );
		} );

		Check( "window-without-sill-rejected", () =>
		{
			var after = RoomHelpers.CutWindow( Room128(), RoomWall.East, 64f, 48f, 0f );
			Require( !after.Ok, "a window with no sill was accepted (that is a doorway)" );
			Require( after.Reason == "room-window-no-sill", $"reason {after.Reason}, expected room-window-no-sill" );
		} );

		Check( "window-without-lintel-rejected", () =>
		{
			var after = RoomHelpers.CutWindow( Room128(), RoomWall.East, 64f, 96f, 32f );
			Require( !after.Ok, "a window reaching the ceiling was accepted" );
			Require( after.Reason == "room-opening-no-lintel", $"reason {after.Reason}, expected room-opening-no-lintel" );
		} );

		Check( "unknown-wall-rejected", () =>
		{
			var after = RoomHelpers.CutDoorway( Room128(), (RoomWall)99, 48f, 96f );
			Require( !after.Ok, "a doorway in wall 99 was accepted" );
			Require( after.Reason == "room-unknown-wall", $"reason {after.Reason}, expected room-unknown-wall" );
		} );

		Check( "opening-bad-size-rejected", () =>
		{
			var room = Room128();
			Require( RoomHelpers.CutDoorway( room, RoomWall.North, 0f, 96f ).Reason == "room-opening-bad-size", "zero-width doorway accepted" );
			Require( RoomHelpers.CutDoorway( room, RoomWall.North, 48f, -1f ).Reason == "room-opening-bad-size", "negative-height doorway accepted" );
			Require( RoomHelpers.CutWindow( room, RoomWall.East, 64f, 48f, float.NaN ).Reason == "room-opening-bad-size", "NaN-sill window accepted" );
		} );

		Check( "overlapping-openings-rejected", () =>
		{
			var room = RoomHelpers.CutDoorway( Room128(), RoomWall.North, 48f, 96f );
			Require( room.Ok, $"first doorway refused: {room.Reason}" );
			var second = RoomHelpers.CutDoorway( room, RoomWall.North, 48f, 96f, 40f );
			Require( !second.Ok, "a doorway overlapping the first was accepted" );
			Require( second.Reason == "room-opening-overlaps-existing", $"reason {second.Reason}, expected room-opening-overlaps-existing" );
			Require( second.IsSealed, "a refused cut left the room unsealed" );
			RequireVolume( second.FindBrush( RoomWall.North )!.Mesh, 147456f - 48f * 96f * 8f, "north wall after refused second cut" );
		} );

		Check( "leak-finder-catches-deleted-face-gap", () =>
		{
			var room = Room128();
			var wall = room.FindBrush( RoomWall.North )!;
			// Face 0 of the north wall box is its +X end face (x = 72 plane).
			// Deleting it opens a real gap in the shell; the leak finder must
			// find it, name its location on that wall, and export a point
			// file from the player start out through the gap.
			wall.Mesh.Faces.RemoveAt( 0 );
			Require( !MeshValidator.Validate( wall.Mesh ).IsClosed, "wall with a deleted face still validates closed" );
			var leak = RoomHelpers.ValidateSealed( room );
			Require( leak.IsLeaked, "leak finder called a room with a missing wall face sealed" );
			Require( Math.Abs( leak.GapLocation.x - 72f ) < 0.6f, $"gap x {leak.GapLocation.x}, expected the 72 face" );
			Require( leak.GapLocation.y >= 64f && leak.GapLocation.y <= 72f, $"gap y {leak.GapLocation.y}, expected the north wall band" );
			Require( leak.GapLocation.z >= 0f && leak.GapLocation.z <= 128f, $"gap z {leak.GapLocation.z}" );
			Require( leak.Path.Count == 3, $"leak path has {leak.Path.Count} points, expected 3 (start, gap, exit)" );
			Require( NearVec( leak.Path[0], room.PlayerStart ), $"leak path starts at {leak.Path[0]}, not the player start {room.PlayerStart}" );
			Require( NearVec( leak.Path[1], leak.GapLocation ), "leak path does not go through the gap" );
			Require( leak.Path[2].x > 72f, $"leak path does not exit past the wall: {leak.Path[2]}" );
			var text = leak.ToPointFileText();
			Require( text.Length > 0, "leaked room exported an empty point file" );
			var parsed = LeakFinder.ParsePointFileText( text );
			Require( parsed.Count == leak.Path.Count, "point-file round-trip lost points" );
			for ( var i = 0; i < parsed.Count; i++ )
				Require( NearVec( parsed[i], leak.Path[i] ), $"point-file point {i} moved" );
		} );

		Check( "no-boolean-provider-cut-refused-with-named-reason", () =>
		{
			var saved = MeshBoolean.Provider;
			MeshBoolean.Provider = null;
			try
			{
				var before = Room128();
				var after = RoomHelpers.CutDoorway( before, RoomWall.North, 48f, 96f );
				Require( !after.Ok, "a cut with no boolean provider was accepted" );
				Require( after.Reason == "csg-no-boolean-provider",
					$"reason {after.Reason}, expected csg-no-boolean-provider (never a fake uncut success)" );
				Require( after.IsSealed, "a refused cut left the room unsealed" );
				RequireVolume( after.FindBrush( RoomWall.North )!.Mesh, 147456f, "north wall after refused cut" );
			}
			finally { MeshBoolean.Provider = saved; }
		} );

		Check( "no-carve-operation-exists", () =>
		{
			foreach ( var type in new[] { typeof( RoomHelpers ), typeof( RoomResult ), typeof( RoomBrush ), typeof( RoomOpening ), typeof( RoomOptions ) } )
			{
				foreach ( var m in type.GetMethods( BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly ) )
					Require( !m.Name.Contains( "Carve", StringComparison.OrdinalIgnoreCase ),
						$"{type.Name}.{m.Name} is a Carve operation (forbidden: carve creates slivers)" );
				foreach ( var f in type.GetFields( BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly ) )
					Require( !f.Name.Contains( "Carve", StringComparison.OrdinalIgnoreCase ), $"{type.Name}.{f.Name} mentions Carve" );
			}
		} );

		Check( "create-room-rejects-bad-dimensions", () =>
		{
			Throws<ArgumentOutOfRangeException>( () => RoomHelpers.CreateRoom( 0f, 128f, 128f, 8f ), "zero length" );
			Throws<ArgumentOutOfRangeException>( () => RoomHelpers.CreateRoom( 128f, -1f, 128f, 8f ), "negative width" );
			Throws<ArgumentOutOfRangeException>( () => RoomHelpers.CreateRoom( 128f, 128f, 128f, 0f ), "zero wall thickness" );
			Throws<ArgumentOutOfRangeException>( () => RoomHelpers.CreateRoom( 128f, 128f, 128f, -8f ), "negative wall thickness" );
			Throws<ArgumentOutOfRangeException>( () => RoomHelpers.CreateRoom( float.NaN, 128f, 128f, 8f ), "NaN length" );
		} );
	}
}
