using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// Hammer Desktop P3: native navmesh generation + preview data + export.
/// Board task hammer-navmesh-generation-export, group "navmeshgen".
///
/// Fixtures live in tests/fixtures/hammer/hammer-navmesh-generation-export/ as JSON
/// (the QuickPlay fixture schema: {brushes:[{id,min,max,detail,material}],
/// entities:[{class,origin,yaw}], displacements:[{origin,sizeX,sizeY,columns,rows,
/// heights}]}). Units are inches, Z-up. Generation runs from the QuickPlay bake:
/// the tests below assert against the bake's own collision, never against a
/// re-implementation of it.
/// </summary>
internal static class NavMeshGenTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "navmeshgen", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float eps, string what )
		{
			if ( MathF.Abs( actual - expected ) > eps )
				throw new Exception( $"{what}: expected {expected}, got {actual}" );
		}
		void ThrowsNavCode( string code, Action action )
		{
			try { action(); }
			catch ( NavMeshException ex )
			{
				if ( ex.Code != code )
					throw new Exception( $"expected navmesh error '{code}', got '{ex.Code}: {ex.Message}'" );
				return;
			}
			throw new Exception( $"expected navmesh error '{code}', got silent success" );
		}
		void ThrowsQuickCode( string code, Action action )
		{
			try { action(); }
			catch ( QuickPlayException ex )
			{
				if ( ex.Code != code )
					throw new Exception( $"expected QuickPlay error '{code}', got '{ex.Code}: {ex.Message}'" );
				return;
			}
			throw new Exception( $"expected QuickPlay error '{code}', got silent success" );
		}

		string Fixture( string name )
		{
			string rel = Path.Combine( "fixtures", "hammer", "hammer-navmesh-generation-export", name );
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, rel ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", rel ) ),
				Path.GetFullPath( Path.Combine( "tests", rel ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return File.ReadAllText( candidate );
			throw new Exception( "navmesh fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
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

		QuickPlayMap Map( string fixture ) => LoadMap( Fixture( fixture ) );

		QuickPlayBrush Box( string id, float x0, float y0, float z0, float x1, float y1, float z1, bool detail = false ) =>
			new QuickPlayBrush( id, new Vec3( x0, y0, z0 ), new Vec3( x1, y1, z1 ), detail );

		// Same sealed room as the QuickPlay fixture: interior 256 x 256 x 128.
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

		// Reachable set of polys from a start poly, following directed links.
		HashSet<int> Reachable( NavMeshResult result, int start )
		{
			var seen = new HashSet<int> { start };
			var queue = new Queue<int>();
			queue.Enqueue( start );
			while ( queue.Count > 0 )
			{
				int cur = queue.Dequeue();
				foreach ( var link in result.Links )
					if ( link.From == cur && seen.Add( link.To ) )
						queue.Enqueue( link.To );
			}
			return seen;
		}

		Check( "flat room yields one connected navmesh: one region, one poly, no links", () =>
		{
			var result = NavMeshGen.Generate( Map( "sealed-room.json" ) );
			Require( result.RegionCount == 1, $"regions {result.RegionCount}, expected 1" );
			Require( result.Polys.Count == 1, $"polys {result.Polys.Count}, expected 1" );
			Require( result.Links.Count == 0, $"links {result.Links.Count}, expected 0" );
			Require( result.SpawnOk, "spawn must be ok: " + result.SpawnReason );
			Require( result.Sealed, "sealed room must report sealed" );
			Require( result.CellCount == 1296, $"cells {result.CellCount}, expected 36x36 = 1296" );
			Require( result.WalkableCellCount == 784, $"walkable cells {result.WalkableCellCount}, expected 28x28 = 784" );
			var poly = result.Polys[0];
			// Agent-radius erosion: the walkable floor is the interior shrunk by the
			// 16-unit radius on every side (256 - 2*16 = 224).
			Near( poly.Min.x, -112f, 1e-3f, "poly min x" );
			Near( poly.Min.y, -112f, 1e-3f, "poly min y" );
			Near( poly.Max.x, 112f, 1e-3f, "poly max x" );
			Near( poly.Max.y, 112f, 1e-3f, "poly max y" );
			Near( poly.Height, 0f, 1e-3f, "poly height" );
			Near( poly.Area, 224f * 224f, 1e-2f, "poly area" );
			Require( poly.Corners.Length == 4, "a poly has four corners" );
			Require( result.PolyAt( new Vec3( 0, 0, 0 ), 18f ) == 0, "spawn must be on the poly" );
			Require( result.Hints.Count == 0, "sealed-room fixture has no nav hints" );
		} );

		Check( "room with a 2m wall yields two disconnected regions, no links", () =>
		{
			var result = NavMeshGen.Generate( Map( "wall-room.json" ) );
			Require( result.RegionCount == 2, $"regions {result.RegionCount}, expected 2" );
			Require( result.Polys.Count == 2, $"polys {result.Polys.Count}, expected 2" );
			Require( result.Links.Count == 0, $"links {result.Links.Count}, expected 0 — the 79-unit wall is past the 18-unit step" );
			Require( result.Polys[0].RegionId != result.Polys[1].RegionId, "the two polys must be in different regions" );
			Require( result.Hints.Count == 2, $"hints {result.Hints.Count}, expected the two info_nodes" );
			foreach ( var hint in result.Hints )
				Require( hint.Covered, $"hint at {hint.Point} must be covered" );
			Require( result.Hints[0].PolyId != result.Hints[1].PolyId, "the two nodes must sit on different polys" );
			var left = result.Polys[result.Hints[0].PolyId];
			var right = result.Polys[result.Hints[1].PolyId];
			Require( left.RegionId != right.RegionId, "the two nodes must sit in different regions" );
			Near( left.Max.x, -24f, 1e-3f, "left poly max x (eroded off the wall face)" );
			Near( right.Min.x, 24f, 1e-3f, "right poly min x (eroded off the wall face)" );
		} );

		Check( "stairs link the floors: one region, every poly reachable, 9-unit step links", () =>
		{
			var result = NavMeshGen.Generate( Map( "stairs-room.json" ) );
			Require( result.RegionCount == 1, $"regions {result.RegionCount}, expected 1" );
			Require( result.SpawnOk, "spawn must be ok: " + result.SpawnReason );
			int spawnPoly = result.PolyAt( new Vec3( -100, -100, 0 ), 18f );
			int platformPoly = result.PolyAt( new Vec3( 0, 72, 54 ), 18f );
			Require( spawnPoly >= 0, "spawn must be on a poly" );
			Require( platformPoly >= 0, "platform hint must be on a poly" );
			Require( spawnPoly != platformPoly, "spawn and platform must be different polys" );
			var reached = Reachable( result, spawnPoly );
			Require( reached.Count == result.Polys.Count,
				$"reachable polys {reached.Count} of {result.Polys.Count}: every poly must be reachable from the spawn" );
			Require( reached.Contains( platformPoly ), "the platform must be reachable from the spawn" );
			bool step9 = false;
			float maxHeight = float.MinValue, minHeight = float.MaxValue;
			foreach ( var poly in result.Polys )
			{
				maxHeight = MathF.Max( maxHeight, poly.Height );
				minHeight = MathF.Min( minHeight, poly.Height );
			}
			foreach ( var link in result.Links )
				if ( link.Kind == NavMeshLinkKind.StepUp && MathF.Abs( link.HeightDelta - 9f ) < 1e-3f )
					step9 = true;
			Require( step9, "expected a StepUp link with a 9-unit rise (one tread)" );
			// The stairs link the floors WITHOUT the Drop: following only Walk /
			// StepUp / StepDown links from the spawn must reach the platform. (A
			// Drop off the platform's open edge is real too — the platform is 54
			// above the floor — but the stairs are the link this test is about.)
			var stepped = new HashSet<int> { spawnPoly };
			var stepQueue = new Queue<int>();
			stepQueue.Enqueue( spawnPoly );
			while ( stepQueue.Count > 0 )
			{
				int cur = stepQueue.Dequeue();
				foreach ( var link in result.Links )
					if ( link.From == cur && link.Kind != NavMeshLinkKind.Drop && stepped.Add( link.To ) )
						stepQueue.Enqueue( link.To );
			}
			Require( stepped.Contains( platformPoly ), "the platform must be reachable from the spawn by steps alone (no Drop)" );
			Near( minHeight, 0f, 1e-3f, "lowest poly height" );
			Near( maxHeight, 54f, 1e-3f, "highest poly height (platform)" );
			Require( result.Hints.Count == 2, $"hints {result.Hints.Count}, expected 2" );
			foreach ( var hint in result.Hints )
				Require( hint.Covered, $"hint at {hint.Point} must be covered" );
		} );

		Check( "48-wide doorway connects the rooms: one region, linked across the doorway", () =>
		{
			var result = NavMeshGen.Generate( Map( "doorway-48.json" ) );
			Require( result.RegionCount == 1, $"regions {result.RegionCount}, expected 1" );
			Require( result.Links.Count > 0, "the rooms must be linked" );
			int west = result.PolyAt( new Vec3( -192, 0, 0 ), 18f );
			int east = result.PolyAt( new Vec3( 192, 0, 0 ), 18f );
			Require( west >= 0 && east >= 0, "both rooms must have a poly at their centres" );
			Require( result.Polys[west].RegionId == result.Polys[east].RegionId, "both rooms must be one region" );
			Require( Reachable( result, west ).Contains( east ),
				"the east room must be reachable from the west room through the doorway" );
			// The doorway is 48 wide; the agent needs 32, so a 16-wide walkable
			// strip remains — a Walk link in that strip (midpoint |y| <= 8) is the
			// crossing. (The strip is its own band poly; the rooms' own polys meet
			// it at the strip's edges, not across the whole doorway.)
			bool crossing = false;
			foreach ( var link in result.Links )
				if ( link.Kind == NavMeshLinkKind.Walk && MathF.Abs( link.Midpoint.y ) <= 8f )
					crossing = true;
			Require( crossing, "expected a Walk link crossing the doorway strip (|midpoint y| <= 8)" );
		} );

		Check( "24-wide doorway does not connect: two regions, no link between them", () =>
		{
			var result = NavMeshGen.Generate( Map( "doorway-24.json" ) );
			Require( result.RegionCount == 2, $"regions {result.RegionCount}, expected 2 — 24 wide is under the 32-unit agent diameter" );
			int west = result.PolyAt( new Vec3( -192, 0, 0 ), 18f );
			int east = result.PolyAt( new Vec3( 192, 0, 0 ), 18f );
			Require( west >= 0 && east >= 0, "both rooms must have a poly at their centres" );
			Require( result.Polys[west].RegionId != result.Polys[east].RegionId, "the rooms must be different regions" );
			foreach ( var link in result.Links )
			{
				var from = result.Polys[link.From];
				var to = result.Polys[link.To];
				Require( from.RegionId == to.RegionId || link.Kind == NavMeshLinkKind.Drop,
					"only a Drop link may cross regions" );
				Require( link.Kind != NavMeshLinkKind.Drop || MathF.Abs( link.HeightDelta ) > 18f,
					"a Drop link needs a real drop, not a flat doorway" );
			}
		} );

		Check( "gentle displacement ramp links by steps and drops off its far edge", () =>
		{
			var result = NavMeshGen.Generate( Map( "ramp-room.json" ) );
			Require( result.RegionCount == 2, $"regions {result.RegionCount}, expected 2 (west+ramp / east, split by the 36-unit drop)" );
			bool drop = false;
			foreach ( var link in result.Links )
			{
				if ( link.Kind == NavMeshLinkKind.Drop )
				{
					drop = true;
					Require( result.Polys[link.From].RegionId != result.Polys[link.To].RegionId,
						"a Drop link must cross regions" );
					Require( link.HeightDelta < 0f, "a Drop link must go downhill" );
					Require( MathF.Abs( link.HeightDelta ) > 18f && MathF.Abs( link.HeightDelta ) <= 72f,
						$"drop {link.HeightDelta} must be past the step and within MaxDrop" );
				}
			}
			Require( drop, "expected a Drop link off the ramp's far edge" );
			Require( result.Hints.Count == 2, $"hints {result.Hints.Count}, expected 2" );
			foreach ( var hint in result.Hints )
				Require( hint.Covered, $"hint at {hint.Point} must be covered" );
			int rampPoly = result.PolyAt( new Vec3( 48, 0, 31.5f ), 18f );
			Require( rampPoly >= 0, "the ramp hint must be on a poly" );
			Require( result.Polys[rampPoly].Height > 18f, "the ramp hint must sit on a raised ramp poly, not the floor" );
		} );

		Check( "steep ramp respects slope limit and its crest remains a directed drop", () =>
		{
			var steep = NavMeshGen.Generate( Map( "steep-ramp-room.json" ) );
			Require( steep.RegionCount == 2, $"regions {steep.RegionCount}, expected 2 — the 56-degree ramp is past the 45-degree slope limit" );
			Require( steep.Links.Count == 0, $"links {steep.Links.Count}, expected 0 across the unwalkable band" );
			var permissive = NavMeshGen.Generate( Map( "steep-ramp-room.json" ),
				new NavMeshGenOptions { MaxSlopeDegrees = 60f } );
			// This fixture ends at height 48 over a floor at 0: the crest is a real drop,
            // even when the ramp itself is walkable. It cannot merge uphill regions.
            Require( permissive.RegionCount == 2, $"regions {permissive.RegionCount}, expected 2 across the 48-unit crest drop" );
            int onRamp=permissive.PolyAt(new Vec3(0,0,24),18);
            Require(onRamp>=0 && permissive.Polys[onRamp].Height>0,"60-degree limit did not admit raised ramp surface");
            Require(permissive.Links.Any(l=>l.Kind==NavMeshLinkKind.Drop),"crest did not retain its downhill drop link");
			Require( permissive.Links.Count > 0, "the ramp must link the floors with a 60-degree limit" );
		} );

		Check( "low ceiling and missing floor both fail with no-walkable-surface", () =>
		{
			ThrowsNavCode( NavMeshError.NoWalkableSurface, () => NavMeshGen.Generate( Map( "low-ceiling-room.json" ) ) );
			ThrowsNavCode( NavMeshError.NoWalkableSurface, () => NavMeshGen.Generate( Map( "no-floor-room.json" ) ) );
		} );

		Check( "36-unit solid ledge: two regions, one one-way Drop link bridging the erosion gap", () =>
		{
			var result = NavMeshGen.Generate( Map( "ledge-room.json" ) );
			Require( result.RegionCount == 2, $"regions {result.RegionCount}, expected 2" );
			Require( result.Polys.Count == 2, $"polys {result.Polys.Count}, expected 2" );
			Require( result.Links.Count == 1, $"links {result.Links.Count}, expected exactly the one Drop link" );
			var link = result.Links[0];
			Require( link.Kind == NavMeshLinkKind.Drop, $"link kind {link.Kind}, expected Drop" );
			int platformPoly = result.PolyAt( new Vec3( 0, 72, 36 ), 18f );
			int floorPoly = result.PolyAt( new Vec3( -100, -72, 0 ), 18f );
			Require( link.From == platformPoly, $"drop must start on the platform poly {platformPoly}, got {link.From}" );
			Require( link.To == floorPoly, $"drop must land on the floor poly {floorPoly}, got {link.To}" );
			Near( link.HeightDelta, -36f, 1e-3f, "drop height" );
			Near( link.SharedLength, 224f, 1e-3f, "drop edge length (the 224-wide ledge face)" );
			Near( link.Midpoint.x, 0f, 1f, "drop midpoint x" );
			Near( link.Midpoint.y, -8f, 1f, "drop midpoint y (between the ledge edge and the landing)" );
			Near( link.Midpoint.z, 18f, 1e-3f, "drop midpoint z (mean of the two heights)" );
			foreach ( var hint in result.Hints )
				Require( hint.Covered, $"hint at {hint.Point} must be covered" );
		} );

		Check( "ConvexHull.Build on a cube's 8 corners returns all 8 (worker-21's 6-point finding does NOT reproduce)", () =>
		{
			var corners = new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 16, 0, 0 ), new Vec3( 0, 16, 0 ), new Vec3( 16, 16, 0 ),
				new Vec3( 0, 0, 16 ), new Vec3( 16, 0, 16 ), new Vec3( 0, 16, 16 ), new Vec3( 16, 16, 16 ),
			};
			var hull = ConvexHull.Build( corners );
			Require( hull != null, "a cube must have a hull" );
			var (points, faces) = hull.Value;
			// Worker 21 reported 6 points / 8 faces on 2026-11-13 (two coplanar-cap
			// corners dropped). Re-probed here against the current tree: the hull
			// contains the box. If this ever regresses to 6 points, NavMeshGen is
			// unaffected — it never calls ConvexHull.Build (see below) — but the
			// finding would be confirmed and this test must be updated to say so.
			Require( points.Count == 8, $"hull points {points.Count}, expected 8 (worker 21 reported 6; not reproduced)" );
			Require( faces.Count == 12, $"hull faces {faces.Count}, expected 12 triangles for a cube" );
			foreach ( var c in corners )
				Require( points.Any( p => p.AlmostEquals( c ) ), $"hull is missing cube corner {c}" );
			// And the route-around itself: the bake's brush hulls keep all 8 corners.
			var bake = QuickPlay.Bake( SealedRoom() );
			foreach ( var brush in SealedRoom().Brushes )
			{
				var shape = bake.CollisionHulls.FirstOrDefault( h => h.BodyId == brush.Id );
				Require( shape != null, "missing collision hull for brush " + brush.Id );
				Require( shape.Points.Count == 8, $"hull for {brush.Id} has {shape.Points.Count} points, expected 8 exact corners" );
			}
		} );

		Check( "text .nav export round-trips through Parse; bad input is rejected, never partial", () =>
		{
			var result = NavMeshGen.Generate( Map( "sealed-room.json" ), new NavMeshGenOptions { MapName = "test-map" } );
			string text = result.ToNavText();
			Require( text.Contains( "NOT byte-compatible with Valve binary .nav" ),
				"the export must say plainly that it is not binary .nav" );
			var parsed = NavMeshTextExport.Parse( text );
			Require( parsed.MapName == "test-map", "map name " + parsed.MapName );
			Require( parsed.Areas.Count == 1, $"areas {parsed.Areas.Count}, expected 1" );
			Require( parsed.Areas[0].Id == 0 && parsed.Areas[0].RegionId == 0, "area 0 must be region 0" );
			Near( parsed.Areas[0].Min.x, -112f, 1e-3f, "area min x" );
			Near( parsed.Areas[0].Max.x, 112f, 1e-3f, "area max x" );
			Require( parsed.Links.Count == 0, "flat room has no connections" );
			Require( parsed.Hints.Count == 0, "sealed room has no hints" );
			Near( parsed.AgentRadius, 16f, 1e-3f, "agent radius" );
			Near( parsed.AgentHeight, 72f, 1e-3f, "agent height" );
			Near( parsed.StepHeight, 18f, 1e-3f, "step height" );

			var wall = NavMeshGen.Generate( Map( "wall-room.json" ) );
			var wallParsed = NavMeshTextExport.Parse( wall.ToNavText() );
			Require( wallParsed.Areas.Count == 2, $"wall areas {wallParsed.Areas.Count}, expected 2" );
			Require( wallParsed.Hints.Count == 2, $"wall hints {wallParsed.Hints.Count}, expected 2" );
			Require( wallParsed.Hints.TrueForAll( h => h.Covered ), "exported hints must both be covered" );
			Require( wallParsed.Hints[0].PolyId != wallParsed.Hints[1].PolyId, "exported hints must be on different areas" );

			void ThrowsFormat( string bad )
			{
				try { NavMeshTextExport.Parse( bad ); }
				catch ( FormatException ) { return; }
				throw new Exception( "expected FormatException for: " + bad.Substring( 0, Math.Min( bad.Length, 60 ) ) );
			}
			ThrowsFormat( "this is not a navmesh" );
			ThrowsFormat( text.Replace( "NAV 1", "NAV 2" ) );
			ThrowsFormat( text.Substring( 0, text.LastIndexOf( "hints", StringComparison.Ordinal ) ) );
			ThrowsFormat( text + "area 99 region 0 min 0 0 0 max 1 1 0 corners (0,0,0) (1,0,0) (1,1,0) (0,1,0)\n" );
			ThrowsFormat( "" );
		} );

		Check( "generation is deterministic: same Describe and same export text twice", () =>
		{
			var map = Map( "stairs-room.json" );
			var first = NavMeshGen.Generate( map );
			var second = NavMeshGen.Generate( map );
			Require( first.Describe() == second.Describe(), "Describe must be identical across runs" );
			Require( first.ToNavText() == second.ToNavText(), "export text must be identical across runs" );
			Require( first.Polys.Count == second.Polys.Count, "poly count moved" );
			for ( int i = 0; i < first.Polys.Count; i++ )
				Require( first.Polys[i].Min.Equals( second.Polys[i].Min ) && first.Polys[i].Max.Equals( second.Polys[i].Max ),
					$"poly {i} bounds moved" );
		} );

		Check( "generating from an explicit bake matches generating from the map", () =>
		{
			var map = Map( "wall-room.json" );
			var bake = QuickPlay.Bake( map );
			var fromBake = NavMeshGen.Generate( bake, map );
			var fromMap = NavMeshGen.Generate( map );
			Require( fromBake.Describe() == fromMap.Describe(), "bake-first and map-first generation must agree" );
			Require( fromBake.Sealed && fromMap.Sealed, "both must report sealed" );
		} );

		Check( "bad input fails with the named errors: QuickPlay codes pass through, nulls throw", () =>
		{
			var noStart = Map( "sealed-room.json" );
			noStart.Entities.Clear();
			ThrowsQuickCode( QuickPlayError.NoPlayerStart, () => NavMeshGen.Generate( noStart ) );

			var empty = SealedRoom();
			empty.Brushes.Clear();
			ThrowsQuickCode( QuickPlayError.EmptyMap, () => NavMeshGen.Generate( empty ) );

			var badBrush = SealedRoom();
			badBrush.Brushes.Add( new QuickPlayBrush( "flat", new Vec3( 0, 0, 0 ), new Vec3( 0, 10, 10 ) ) );
			ThrowsQuickCode( QuickPlayError.InvalidBrush, () => NavMeshGen.Generate( badBrush ) );

			try { NavMeshGen.Generate( (QuickPlayMap)null ); throw new Exception( "expected ArgumentNullException" ); }
			catch ( ArgumentNullException ) { }
			try { NavMeshGen.Generate( (QuickPlayBakeResult)null, SealedRoom() ); throw new Exception( "expected ArgumentNullException" ); }
			catch ( ArgumentNullException ) { }
			try { NavMeshGen.Generate( QuickPlay.Bake( SealedRoom() ), (QuickPlayMap)null ); throw new Exception( "expected ArgumentNullException" ); }
			catch ( ArgumentNullException ) { }
		} );

		Check( "invalid options fail fast: bad cell size / slope / grid cap", () =>
		{
			var map = Map( "sealed-room.json" );
			try { NavMeshGen.Generate( map, new NavMeshGenOptions { CellSize = 0f } ); throw new Exception( "expected ArgumentException" ); }
			catch ( ArgumentException ) { }
			try { NavMeshGen.Generate( map, new NavMeshGenOptions { MaxSlopeDegrees = 91f } ); throw new Exception( "expected ArgumentException" ); }
			catch ( ArgumentException ) { }
			try { NavMeshGen.Generate( map, new NavMeshGenOptions { AgentHeight = 0f } ); throw new Exception( "expected ArgumentException" ); }
			catch ( ArgumentException ) { }
			ThrowsNavCode( NavMeshError.TooComplex,
				() => NavMeshGen.Generate( map, new NavMeshGenOptions { MaxCells = 100 } ) );
		} );

		Check( "uncovered hints are reported, never silently dropped", () =>
		{
			var map = SealedRoom();
			// info_node inside the north wall solid: no poly covers it.
			map.Entities.Add( new QuickPlayEntity( "info_node", new Vec3( 0, 136, 64 ), 0f ) );
			// An npc_* entity needs a navmesh too (PlaytestLoop's rule) and counts.
			map.Entities.Add( new QuickPlayEntity( "npc_zombie", new Vec3( 0, 0, 0 ), 0f ) );
			var result = NavMeshGen.Generate( map, new NavMeshGenOptions
			{
				HintPoints = new List<Vec3> { new Vec3( 0, 0, 0 ), new Vec3( 10000, 0, 0 ) },
			} );
			Require( result.Hints.Count == 4, $"hints {result.Hints.Count}, expected 2 entities + 2 points" );
			var wallHint = result.Hints[0];
			Require( !wallHint.Covered, "the wall node must be uncovered" );
			Require( wallHint.PolyId == -1, "an uncovered hint must have poly -1" );
			Require( wallHint.ClassName == "info_node", "hint class " + wallHint.ClassName );
			Require( result.Hints[1].Covered, "the npc_zombie on the floor must be covered" );
			Require( result.Hints[1].ClassName == "npc_zombie", "hint class " + result.Hints[1].ClassName );
			Require( result.Hints[2].Covered, "the explicit point on the floor must be covered" );
			Require( !result.Hints[3].Covered, "the explicit point outside the map must be uncovered" );
			Require( result.Warnings.Exists( w => w.StartsWith( "uncovered-hint:info_node@", StringComparison.Ordinal ) ),
				"warnings must name the uncovered wall node: " + string.Join( ",", result.Warnings ) );
			Require( result.Warnings.Exists( w => w.StartsWith( "uncovered-hint:point@", StringComparison.Ordinal ) ),
				"warnings must name the uncovered explicit point" );
		} );

		Check( "invalid spawn is a warning, not a failure: the floor navmesh still generates", () =>
		{
			var map = SealedRoom();
			map.Brushes.Add( Box( "pillar", -16, -16, 0, 16, 16, 128 ) );
			var result = NavMeshGen.Generate( map );
			Require( !result.SpawnOk, "spawn inside the pillar must be invalid" );
			Require( result.SpawnReason == "spawn-blocked", "spawn reason " + result.SpawnReason );
			Require( result.Warnings.Contains( "spawn-invalid:spawn-blocked" ),
				"warnings must name the invalid spawn: " + string.Join( ",", result.Warnings ) );
			Require( result.RegionCount == 1, "the floor around the pillar must still be one region" );
			Require( result.Polys.Count >= 1, "the floor must still produce polys" );
		} );

		Check( "explicit hint points seed coverage without any map entity", () =>
		{
			var result = NavMeshGen.Generate( Map( "sealed-room.json" ), new NavMeshGenOptions
			{
				HintPoints = new List<Vec3> { new Vec3( -100, -100, 0 ), new Vec3( 100, 100, 0 ) },
			} );
			Require( result.Hints.Count == 2, $"hints {result.Hints.Count}, expected 2" );
			foreach ( var hint in result.Hints )
			{
				Require( hint.Covered, $"hint at {hint.Point} must be covered" );
				Require( hint.ClassName == "", "an explicit point has no class name" );
			}
			Require( result.Hints[0].PolyId == result.Hints[1].PolyId, "both points sit on the one flat-room poly" );
		} );
	}
}
