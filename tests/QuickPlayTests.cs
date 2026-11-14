using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Effigy;

/// <summary>
/// Hammer Desktop P1: Quick Play — BSP-only bake + walkable collision + player-start
/// spawn (headless). Board task hammer-quick-play-bsp-only-bake, group "quickplay".
///
/// Fixtures live in tests/fixtures/hammer/hammer-quick-play-bsp-only-bake/ as JSON
/// ({brushes:[{id,min,max,detail,material}], entities:[{class,origin,yaw}],
/// displacements:[]}); the in-code maps below mirror the same numbers so a fixture
/// failure can be told apart from a code failure. Units are inches, Z-up, yaw 0 = +X.
/// </summary>
internal static class QuickPlayTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "quickplay", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float eps, string what )
		{
			if ( MathF.Abs( actual - expected ) > eps )
				throw new Exception( $"{what}: expected {expected}, got {actual}" );
		}
		void ThrowsCode( string code, Action action )
		{
			try { action(); }
			catch ( QuickPlayException ex )
			{
				if ( ex.Code != code )
					throw new Exception( $"expected error '{code}', got '{ex.Code}: {ex.Message}'" );
				return;
			}
			throw new Exception( $"expected QuickPlay error '{code}', got silent success" );
		}

		string Fixture( string name )
		{
			string rel = Path.Combine( "fixtures", "hammer", "hammer-quick-play-bsp-only-bake", name );
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, rel ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", rel ) ),
				Path.GetFullPath( Path.Combine( "tests", rel ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return File.ReadAllText( candidate );
			throw new Exception( "quickplay fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
		}

		Vec3 V( JsonElement e ) => new Vec3( e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle() );

		QuickPlayMap LoadMap( string json )
		{
			var map = new QuickPlayMap();
			using var doc = JsonDocument.Parse( json );
			var root = doc.RootElement;
			foreach ( var b in root.GetProperty( "brushes" ).EnumerateArray() )
			{
				bool detail = b.TryGetProperty( "detail", out var d ) && d.GetBoolean();
				string material = b.TryGetProperty( "material", out var m ) ? m.GetString() : null;
				map.Brushes.Add( new QuickPlayBrush(
					b.GetProperty( "id" ).GetString(), V( b.GetProperty( "min" ) ), V( b.GetProperty( "max" ) ),
					detail, material ) );
			}
			foreach ( var e in root.GetProperty( "entities" ).EnumerateArray() )
			{
				float yaw = e.TryGetProperty( "yaw", out var y ) ? y.GetSingle() : 0f;
				map.Entities.Add( new QuickPlayEntity(
					e.GetProperty( "class" ).GetString(), V( e.GetProperty( "origin" ) ), yaw ) );
			}
			if ( root.TryGetProperty( "displacements", out var disps ) )
			{
				foreach ( var d in disps.EnumerateArray() )
				{
					int cols = d.GetProperty( "columns" ).GetInt32();
					int rows = d.GetProperty( "rows" ).GetInt32();
					var heights = new float[rows, cols];
					int r = 0;
					foreach ( var row in d.GetProperty( "heights" ).EnumerateArray() )
					{
						int c = 0;
						foreach ( var h in row.EnumerateArray() )
							heights[r, c++] = h.GetSingle();
						r++;
					}
					map.Displacements.Add( new QuickPlayDisplacement(
						V( d.GetProperty( "origin" ) ),
						d.GetProperty( "sizeX" ).GetSingle(), d.GetProperty( "sizeY" ).GetSingle(),
						cols, rows, heights ) );
				}
			}
			return map;
		}

		QuickPlayBrush Box( string id, float x0, float y0, float z0, float x1, float y1, float z1, bool detail = false ) =>
			new QuickPlayBrush( id, new Vec3( x0, y0, z0 ), new Vec3( x1, y1, z1 ), detail );

		// Sealed room, interior 256 x 256 x 128, walls/floor/ceiling 16 thick.
		// Same numbers as sealed-room.json.
		QuickPlayMap SealedRoom()
		{
			var map = new QuickPlayMap();
			map.Brushes.Add( Box( "floor", -144, -144, -16, 144, 144, 0 ) );
			map.Brushes.Add( Box( "ceiling", -144, -144, 128, 144, 144, 144 ) );
			map.Brushes.Add( Box( "wall-west", -144, -144, 0, -128, 144, 128 ) );
			map.Brushes.Add( Box( "wall-east", 128, -144, 0, 144, 144, 128 ) );
			map.Brushes.Add( Box( "wall-south", -128, -144, 0, 128, -128, 128 ) );
			map.Brushes.Add( Box( "wall-north", -128, 128, 0, 128, 144, 128 ) );
			map.Entities.Add( new QuickPlayEntity( "info_player_start", new Vec3( 0, 0, 0 ), 0f ) );
			return map;
		}

		// Two 248-wide rooms joined by a 48-wide, 96-high doorway in the shared wall.
		QuickPlayMap TwoRooms()
		{
			var map = new QuickPlayMap();
			map.Brushes.Add( Box( "floor", -272, -144, -16, 272, 144, 0 ) );
			map.Brushes.Add( Box( "ceiling", -272, -144, 128, 272, 144, 144 ) );
			map.Brushes.Add( Box( "wall-west", -272, -144, 0, -256, 144, 128 ) );
			map.Brushes.Add( Box( "wall-east", 256, -144, 0, 272, 144, 128 ) );
			map.Brushes.Add( Box( "wall-south", -256, -144, 0, 256, -128, 128 ) );
			map.Brushes.Add( Box( "wall-north", -256, 128, 0, 256, 144, 128 ) );
			map.Brushes.Add( Box( "shared-south", -8, -128, 0, 8, -24, 128 ) );
			map.Brushes.Add( Box( "shared-north", -8, 24, 0, 8, 128, 128 ) );
			map.Brushes.Add( Box( "shared-lintel", -8, -24, 96, 8, 24, 128 ) );
			map.Entities.Add( new QuickPlayEntity( "info_player_start", new Vec3( -192, 0, 0 ), 0f ) );
			return map;
		}

		Check( "sealed-room fixture bakes BSP-only: one empty leaf, no portals, sealed", () =>
		{
			var map = LoadMap( Fixture( "sealed-room.json" ) );
			var result = QuickPlay.Bake( map );
			Require( result.EmptyLeafCount == 1, $"empty leaves {result.EmptyLeafCount}, expected 1" );
			Require( result.PortalCount == 0, $"portals {result.PortalCount}, expected 0" );
			Require( result.Sealed, "sealed room must report sealed" );
			Require( result.LeakPortal is null, "sealed room must have no leak portal" );
			Require( result.LeakPath.Count == 0, "sealed room must have an empty leak path" );
			Require( result.WorldBrushCount == 6, $"world brushes {result.WorldBrushCount}" );
			Require( result.CollisionHullCount == 6, $"collision hulls {result.CollisionHullCount}, expected one per brush" );
			// Every brush hull must contain the brush: all 8 corners present. (A hull
			// that drops a corner lets the player clip into it — ConvexHull.Build does
			// exactly that on a cube, which is why brush hulls are exact box corners.)
			foreach ( var brush in map.Brushes )
			{
				var hull = result.CollisionHulls.FirstOrDefault( h => h.BodyId == brush.Id );
				Require( hull != null, "missing collision hull for brush " + brush.Id );
				Require( hull.Points.Count == 8, $"hull for {brush.Id} has {hull.Points.Count} points, expected 8" );
				foreach ( var corner in brush.Corners() )
					Require( hull.Points.Any( p => p.AlmostEquals( corner ) ),
						$"hull for {brush.Id} is missing corner {corner}" );
			}
		} );

		Check( "sealed-room fixture: spawn ok on the floor and the walk passes", () =>
		{
			var result = QuickPlay.Bake( LoadMap( Fixture( "sealed-room.json" ) ) );
			Require( result.Spawn.Ok, "spawn must be ok: " + result.Spawn.Reason );
			Require( result.Spawn.HasFloor, "spawn must have a floor" );
			Near( result.Spawn.FloorHeight, 0f, 1e-4f, "spawn floor height" );
			Require( !result.Walk.Blocked, "walk must pass: " + result.Walk.BlockReason );
			Require( result.Walk.BlockReason is null, "unblocked walk must have no reason" );
			Near( result.Walk.End.x, 64f, 1e-4f, "walk end x" );
			Near( result.Walk.End.y, 0f, 1e-4f, "walk end y" );
			Near( result.Walk.DistanceWalked, 64f, 1e-4f, "walk distance" );
			Require( result.Walk.StepsTaken == 8, $"walk steps {result.Walk.StepsTaken}, expected 8" );
			Require( result.Walk.InsideAtEnd, "walk must end inside the map" );
			Near( result.Walk.FloorHeightAtEnd, 0f, 1e-4f, "walk end floor height" );
		} );

		Check( "no-player-start fixture fails with the named no-player-start error", () =>
		{
			var map = LoadMap( Fixture( "no-player-start.json" ) );
			ThrowsCode( QuickPlayError.NoPlayerStart, () => QuickPlay.Bake( map ) );
		} );

		Check( "blocked-spawn fixture: spawn ok, walk stops at the blocking wall", () =>
		{
			var result = QuickPlay.Bake( LoadMap( Fixture( "blocked-spawn.json" ) ) );
			Require( result.Spawn.Ok, "spawn must be ok: " + result.Spawn.Reason );
			Require( result.Walk.Blocked, "walk must be blocked by the wall" );
			Require( result.Walk.BlockReason == "walk-blocked", "reason " + result.Walk.BlockReason );
			Require( result.Walk.BlockedBy == "blocker", "blocked by " + result.Walk.BlockedBy );
			Near( result.Walk.DistanceWalked, 16f, 1e-4f, "walked distance before the wall" );
			Require( result.Walk.StepsTaken == 2, $"walk steps {result.Walk.StepsTaken}, expected 2" );
			Near( result.Walk.End.x, 16f, 1e-4f, "walk end x" );
			Require( !result.Walk.InsideAtEnd, "blocked walk must not claim inside-at-end" );
		} );

		Check( "gap-room fixture: unsealed with a leak portal at the gap, walk still passes", () =>
		{
			var result = QuickPlay.Bake( LoadMap( Fixture( "gap-room.json" ) ) );
			Require( !result.Sealed, "gap room must report unsealed" );
			Require( result.LeakPortal != null, "gap room must name a leak portal" );
			// The gap is the 16-wide hole through the 16-thick north wall (y 128..144):
			// the leak path crosses the wall band at y 128, and the leak portal itself
			// is the exit into the void at the wall's outer face, y 144.
			Require( MathF.Abs( result.LeakPortal.Centre.x ) <= 8f, "leak portal x " + result.LeakPortal.Centre.x );
			Require( result.LeakPortal.Centre.y >= 136f && result.LeakPortal.Centre.y <= 152f,
				"leak portal y " + result.LeakPortal.Centre.y );
			Require( result.LeakPath.Count >= 2, "leak path must start at the spawn and exit the gap" );
			Near( result.LeakPath[0].x, 0f, 1e-4f, "leak path start x" );
			bool throughGap = false;
			foreach ( var p in result.LeakPath )
				if ( MathF.Abs( p.x ) <= 8f && p.y >= 120f && p.y <= 136f )
					throughGap = true;
			Require( throughGap, "leak path must pass through the gap in the north wall" );
			Require( result.EmptyLeafCount >= 2, $"empty leaves {result.EmptyLeafCount}, expected the gap to split the interior" );
			Require( !result.Walk.Blocked, "walk must still pass: " + result.Walk.BlockReason );
		} );

		Check( "spawn inside a solid brush is spawn-blocked and the walk never starts", () =>
		{
			var map = SealedRoom();
			map.Brushes.Add( Box( "pillar", -16, -16, 0, 16, 16, 128 ) );
			var result = QuickPlay.Bake( map );
			Require( !result.Spawn.Ok, "spawn inside the pillar must fail" );
			Require( result.Spawn.Reason == "spawn-blocked", "reason " + result.Spawn.Reason );
			Require( result.Walk.Blocked, "walk must not start from a blocked spawn" );
			Require( result.Walk.BlockReason == "spawn-blocked", "walk reason " + result.Walk.BlockReason );
			Near( result.Walk.DistanceWalked, 0f, 1e-4f, "walked distance" );
		} );

		Check( "spawn with no floor under it is spawn-no-floor", () =>
		{
			var map = SealedRoom();
			map.Brushes.RemoveAll( b => b.Id == "floor" );
			var result = QuickPlay.Bake( map );
			Require( !result.Spawn.Ok, "floating spawn must fail" );
			Require( result.Spawn.Reason == "spawn-no-floor", "reason " + result.Spawn.Reason );
			Require( !result.Spawn.HasFloor, "floating spawn must have no floor" );
		} );

		Check( "spawn outside the map is spawn-outside-map", () =>
		{
			var map = SealedRoom();
			map.Entities[0] = new QuickPlayEntity( "info_player_start", new Vec3( 10000, 0, 0 ), 0f );
			var result = QuickPlay.Bake( map );
			Require( !result.Spawn.Ok, "outside spawn must fail" );
			Require( result.Spawn.Reason == "spawn-outside-map", "reason " + result.Spawn.Reason );
			// Sealed-ness is a map property: it still flood-fills from the interior.
			Require( result.Sealed, "the room itself is still sealed" );
		} );

		Check( "two rooms joined by a doorway: leaves on both sides, a portal in the doorway, walk passes through", () =>
		{
			var result = QuickPlay.Bake( TwoRooms(), new QuickPlayOptions { WalkDistance = 256f } );
			Require( result.Sealed, "two sealed rooms must report sealed" );
			Require( result.EmptyLeafCount >= 2, $"empty leaves {result.EmptyLeafCount}, expected at least one per room" );
			Require( result.PortalCount >= 1, "expected at least one portal" );
			bool doorwayPortal = false;
			foreach ( var p in result.Portals )
			{
				var c = p.Centre;
				if ( MathF.Abs( c.x ) <= 8f && MathF.Abs( c.y ) <= 24f && c.z >= 0f && c.z <= 96f )
					doorwayPortal = true;
			}
			Require( doorwayPortal, "expected a portal centred in the doorway" );
			Require( !result.Walk.Blocked, "walk through the doorway must pass: " + result.Walk.BlockReason );
			Near( result.Walk.End.x, 64f, 1e-4f, "walk end x" );
			Require( result.Walk.StepsTaken == 32, $"walk steps {result.Walk.StepsTaken}, expected 32" );
			Require( result.Walk.InsideAtEnd, "walk must end inside the second room" );
		} );

		Check( "detail brush is excluded from the BSP but still collides", () =>
		{
			var map = SealedRoom();
			map.Brushes.Add( Box( "pillar-detail", -80, 64, 0, -64, 80, 72, detail: true ) );
			var result = QuickPlay.Bake( map );
			Require( result.EmptyLeafCount == 1, $"empty leaves {result.EmptyLeafCount}: a detail brush must not split the BSP" );
			Require( result.CollisionHullCount == 7, $"collision hulls {result.CollisionHullCount}, expected 6 world + 1 detail" );
			Require( !result.Walk.Blocked, "walk must pass clear of the off-path detail pillar: " + result.Walk.BlockReason );
		} );

		Check( "displacement mound raises the floor under the walk and still passes", () =>
		{
			var map = SealedRoom();
			map.Entities[0] = new QuickPlayEntity( "info_player_start", new Vec3( -96, 0, 0 ), 0f );
			var heights = new float[5, 5];
			for ( int r = 0; r < 5; r++ )
				for ( int c = 0; c < 5; c++ )
				{
					float x = -64 + c * 32, y = -64 + r * 32;
					heights[r, c] = 8f * MathF.Max( 0f, 1f - ( x * x + y * y ) / 8192f );
				}
			map.Displacements.Add( new QuickPlayDisplacement( new Vec3( -64, -64, 0 ), 128f, 128f, 5, 5, heights ) );
			var result = QuickPlay.Bake( map );
			Require( result.EmptyLeafCount == 1, "a displacement must not split the BSP (it does not seal)" );
			Require( result.DisplacementHullCount >= 1, "mound must produce at least one collision hull" );
			Require( !result.Walk.Blocked, "walk over the mound must pass: " + result.Walk.BlockReason );
			Near( result.Walk.FloorHeightAtEnd, 7f, 0.01f, "floor height on the mound" );
		} );

		Check( "displacement cliff steeper than the step height blocks the walk", () =>
		{
			var map = SealedRoom();
			map.Entities[0] = new QuickPlayEntity( "info_player_start", new Vec3( -96, 0, 0 ), 0f );
			var heights = new float[5, 9];
			for ( int r = 0; r < 5; r++ )
				for ( int c = 0; c < 9; c++ )
					heights[r, c] = c >= 4 ? 48f : 0f;
			map.Displacements.Add( new QuickPlayDisplacement( new Vec3( -64, -64, 0 ), 128f, 128f, 9, 5, heights ) );
			var result = QuickPlay.Bake( map, new QuickPlayOptions { WalkDistance = 128f } );
			Require( result.Walk.Blocked, "walk up a 48-unit cliff must block" );
			Require( result.Walk.BlockReason == "walk-step-too-high", "reason " + result.Walk.BlockReason );
			Near( result.Walk.DistanceWalked, 80f, 1e-4f, "walked distance before the cliff" );
			Require( result.Walk.StepsTaken == 10, $"walk steps {result.Walk.StepsTaken}, expected 10" );
		} );

		Check( "bake is deterministic: same leaves, portals and walk end twice", () =>
		{
			var first = QuickPlay.Bake( SealedRoom() );
			var second = QuickPlay.Bake( SealedRoom() );
			Require( first.EmptyLeafCount == second.EmptyLeafCount, "empty leaf count moved" );
			Require( first.SolidLeafCount == second.SolidLeafCount, "solid leaf count moved" );
			Require( first.PortalCount == second.PortalCount, "portal count moved" );
			Require( first.Walk.End.Equals( second.Walk.End ), "walk end moved" );
			Near( first.Spawn.FloorHeight, second.Spawn.FloorHeight, 0f, "spawn floor moved" );
			Require( first.Leaves.Count == second.Leaves.Count, "leaf list moved" );
			for ( int i = 0; i < first.Leaves.Count; i++ )
				Require( first.Leaves[i].Min.Equals( second.Leaves[i].Min ) && first.Leaves[i].Max.Equals( second.Leaves[i].Max ),
					$"leaf {i} bounds moved" );
		} );

		Check( "invalid brush, empty map and null map fail with named errors; multiple player starts warn", () =>
		{
			var badBrush = SealedRoom();
			badBrush.Brushes.Add( new QuickPlayBrush( "flat", new Vec3( 0, 0, 0 ), new Vec3( 0, 10, 10 ) ) );
			ThrowsCode( QuickPlayError.InvalidBrush, () => QuickPlay.Bake( badBrush ) );

			var empty = SealedRoom();
			empty.Brushes.Clear();
			ThrowsCode( QuickPlayError.EmptyMap, () => QuickPlay.Bake( empty ) );

			try { QuickPlay.Bake( null ); throw new Exception( "expected ArgumentNullException" ); }
			catch ( ArgumentNullException ) { }

			var multi = SealedRoom();
			multi.Entities.Add( new QuickPlayEntity( "info_player_start", new Vec3( 64, 64, 0 ), 0f ) );
			var result = QuickPlay.Bake( multi );
			Require( result.Spawn.Ok, "first player start must still spawn" );
			Near( result.Spawn.Origin.x, 0f, 1e-4f, "spawn must use the first player start" );
			Require( result.Warnings.Contains( "multiple-player-starts:2" ),
				"warnings must name the duplicate starts: " + string.Join( ",", result.Warnings ) );
		} );
	}
}
