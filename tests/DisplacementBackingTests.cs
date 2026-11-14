using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;
using MapBrushFace = Effigy.Map.MapBrushFace;

// Nodraw backing-brush generation for displacements (hammer-displacement-nodraw-backing).
//
// DEPENDENCY NOTE: Displacement, BspBuilder and MapBrush have all landed
// (2026-11-13), so these tests run against the REAL Displacement.Create(
// MapBrushFace, power), the REAL BspBuilder.Build and the REAL MapBrush.
// COLLISION: there are two MapBrushFace types — Effigy.MapBrushFace (the brush
// core's, plane + FaceMaterial) and Effigy.Map.MapBrushFace (Displacement's, 4
// corners + int MaterialId). Displacement.Create takes the latter, so this file
// aliases MapBrushFace to Effigy.Map.MapBrushFace; the ToMapBrush test asserts
// the conversion to the brush core's Effigy.MapBrush. If the final integrator
// unifies the two face types, only the alias and the ToMapBrush assertions'
// type names change — the backing behaviour does not.
internal static class DisplacementBackingTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "displacementbacking", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tolerance, string what )
		{
			if ( MathF.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{what}: expected {expected:0.####}, got {actual:0.####}" );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		string FindFixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-displacement-nodraw-backing" );
					if ( Directory.Exists( candidate ) )
						return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "fixture dir tests/fixtures/hammer/hammer-displacement-nodraw-backing not found" );
		}

		Vec3[] FloorCorners() => new[]
		{
			new Vec3( 0, 0, 0 ), new Vec3( 128, 0, 0 ), new Vec3( 128, 128, 0 ), new Vec3( 0, 128, 0 ),
		};

		// +X wall inner face, 128 x 128, normal -X (into the room). Corner order
		// is what makes Newell's method return -X: reversed from the +X order.
		Vec3[] EastWallCorners() => new[]
		{
			new Vec3( 64, 64, 0 ), new Vec3( 64, -64, 0 ), new Vec3( 64, -64, 128 ), new Vec3( 64, 64, 128 ),
		};

		Displacement FloorDisplacement( int power = 2 ) =>
			Displacement.Create( MapBrushFace.FromCorners( FloorCorners() ), power );

		Displacement EastWallDisplacement( int power = 2 ) =>
			Displacement.Create( MapBrushFace.FromCorners( EastWallCorners() ), power );

		Check( "generate: six nodraw faces, same group, footprint equals the base face", () =>
		{
			var disp = FloorDisplacement();
			var backing = DisplacementBacking.GenerateBacking( disp, groupId: 7 );
			Require( backing.Faces.Count == 6, "backing has 6 faces" );
			Require( backing.Faces.All( f => f.IsNoDraw ), "every backing face is nodraw" );
			Require( backing.GroupId == 7, "backing is in the displacement's group (7)" );
			Require( backing.SourceId == "", "no source id invented when none is given" );
			for ( var i = 0; i < 4; i++ )
				Require( backing.BaseCorners[i].AlmostEquals( disp.Face.Corners[i], 1e-6f ), $"footprint corner {i} matches the base face" );
			Require( backing.Normal.AlmostEquals( new Vec3( 0, 0, 1 ), 1e-6f ), "backing normal is the base face normal (+Z)" );
			// Top face is coplanar with the base face; bottom face is one thickness below it.
			var top = backing.Faces[0];
			Require( top.Normal.AlmostEquals( new Vec3( 0, 0, 1 ), 1e-6f ), "top face normal +Z" );
			Near( top.Distance, 0f, 1e-6f, "top face distance (base plane z=0)" );
			var bottom = backing.Faces[1];
			Require( bottom.Normal.AlmostEquals( new Vec3( 0, 0, -1 ), 1e-6f ), "bottom face normal -Z" );
			Near( bottom.Distance, 16f, 1e-6f, "bottom face distance (16 below the base plane)" );
			Require( backing.IsValid, $"backing validates clean: {string.Join( ", ", backing.Validate() )}" );
		} );

		Check( "generate: volume is footprint area times thickness; custom thickness respected", () =>
		{
			var backing = DisplacementBacking.GenerateBacking( FloorDisplacement() );
			Near( backing.FootprintArea, 128f * 128f, 1e-3f, "128x128 footprint area" );
			Near( backing.Thickness, 16f, 1e-6f, "default thickness is 16" );
			Near( backing.Volume, 128f * 128f * 16f, 1f, "default volume" );
			var thin = DisplacementBacking.GenerateBacking( FloorDisplacement(), thickness: 8f );
			Near( thin.Volume, 128f * 128f * 8f, 1f, "8-thick volume" );
			Near( thin.Faces[1].Distance, 8f, 1e-6f, "8-thick bottom face" );
		} );

		Check( "generate: excluded from the baked mesh but solid for the seal path", () =>
		{
			var backing = DisplacementBacking.GenerateBacking( FloorDisplacement(), groupId: 3 );
			Require( backing.ContributesToBakedMesh == false, "backing contributes nothing to the baked mesh" );
			Require( backing.BakedFaceIndices().Count == 0, "no backing face bakes" );
			Require( backing.IsRenderExcluded, "backing is excluded from rendering" );
			Require( backing.IsSealBrush, "backing is a seal brush for the compile path" );
			Require( backing.Faces.All( f => f.IsSolid ), "every nodraw face stays solid (NoDraw without NonSolid)" );
			Require( backing.Faces.All( f => f.Material.IsNoDraw ), "every face material is nodraw" );
		} );

		Check( "generate: null displacement, invalid face and bad thickness throw, naming the problem", () =>
		{
			Throws<ArgumentNullException>( () => DisplacementBacking.GenerateBacking( (Displacement)null ), "null displacement" );
			Throws<ArgumentNullException>( () => DisplacementBacking.SyncBacking( null, FloorDisplacement() ), "null backing on sync" );
			Throws<ArgumentOutOfRangeException>( () => DisplacementBacking.GenerateBacking( FloorDisplacement(), thickness: 0f ), "zero thickness" );
			Throws<ArgumentOutOfRangeException>( () => DisplacementBacking.GenerateBacking( FloorDisplacement(), thickness: -4f ), "negative thickness" );
			Throws<ArgumentOutOfRangeException>( () => Displacement.Create( MapBrushFace.FromCorners( FloorCorners() ), 5 ), "power 5 is out of range 2..4" );
			// A face with a zero normal is not a face: GenerateBacking names it instead of building a flat brush.
			var badFace = new MapBrushFace( FloorCorners(), Vec3.Zero, 0f, 0,
				new Vec2( 0, 0 ), new Vec2( 1, 1 ), 0f, 16f, 0,
				new[] { new Vec2( 0, 0 ), new Vec2( 0, 0 ), new Vec2( 0, 0 ), new Vec2( 0, 0 ) } );
			Throws<ArgumentException>( () => DisplacementBacking.GenerateBacking( badFace ), "zero-normal face" );
		} );

		Check( "displacement never seals on its own: SealsMap is false", () =>
		{
			Require( FloorDisplacement().SealsMap == false, "a displacement does not seal the map (validation rule, not assumption)" );
			Require( EastWallDisplacement().SealsMap == false, "wall displacement does not seal either" );
		} );

		Check( "sync: moving the base face moves the backing, group and thickness kept", () =>
		{
			var before = FloorDisplacement();
			var backing = DisplacementBacking.GenerateBacking( before, groupId: 7, thickness: 8f, sourceId: "floor-terrain" );
			// The base face moves +32 in X: Displacement.Face is immutable, so the
			// moved face is a new displacement — that IS the base-face change.
			var movedCorners = FloorCorners().Select( p => p + new Vec3( 32, 0, 0 ) ).ToArray();
			var after = Displacement.Create( MapBrushFace.FromCorners( movedCorners ), 2 );
			Require( !DisplacementBacking.IsInSync( backing, after ), "old backing is out of sync after the move" );
			var defects = DisplacementBacking.ValidateSync( backing, after );
			Require( defects.Contains( "footprint-moved" ), $"sync break is named footprint-moved, got: {string.Join( ", ", defects )}" );
			var synced = DisplacementBacking.SyncBacking( backing, after );
			Require( synced.GroupId == 7, "sync keeps the group" );
			Near( synced.Thickness, 8f, 1e-6f, "sync keeps the thickness" );
			Require( synced.SourceId == "floor-terrain", "sync keeps the source id" );
			for ( var i = 0; i < 4; i++ )
				Require( synced.BaseCorners[i].AlmostEquals( movedCorners[i], 1e-6f ), $"synced corner {i} follows the moved face" );
			Require( DisplacementBacking.IsInSync( synced, after ), "synced backing is in sync" );
			Require( DisplacementBacking.ValidateSync( synced, after ).Count == 0, "no sync defects after sync" );
			Require( !ReferenceEquals( backing, synced ), "sync returns a new brush; the old one is not mutated" );
			Require( backing.BaseCorners[0].AlmostEquals( new Vec3( 0, 0, 0 ), 1e-6f ), "the old brush still holds the old footprint" );
		} );

		Check( "sync: resizing the base face resizes the backing", () =>
		{
			var before = FloorDisplacement();
			var backing = DisplacementBacking.GenerateBacking( before, groupId: 2 );
			var wideCorners = new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 256, 0, 0 ), new Vec3( 256, 128, 0 ), new Vec3( 0, 128, 0 ),
			};
			var after = Displacement.Create( MapBrushFace.FromCorners( wideCorners ), 2 );
			var defects = DisplacementBacking.ValidateSync( backing, after );
			Require( defects.Contains( "footprint-resized" ), $"sync break is named footprint-resized, got: {string.Join( ", ", defects )}" );
			var synced = DisplacementBacking.SyncBacking( backing, after );
			Near( synced.FootprintArea, 256f * 128f, 1e-3f, "synced footprint area doubles" );
			Near( synced.Volume, 256f * 128f * 16f, 1f, "synced volume doubles" );
			Require( DisplacementBacking.IsInSync( synced, after ), "resized backing is in sync" );
		} );

		Check( "sync: a tilted base face is a named normal-changed break", () =>
		{
			var before = FloorDisplacement();
			var backing = DisplacementBacking.GenerateBacking( before );
			// Same footprint, tilted: z rises with y (plane z = y/2), still a valid planar quad.
			var tiltedCorners = new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 128, 0, 0 ), new Vec3( 128, 128, 64 ), new Vec3( 0, 128, 64 ),
			};
			var tilted = Displacement.Create( MapBrushFace.FromCorners( tiltedCorners ), 2 );
			var defects = DisplacementBacking.ValidateSync( backing, tilted );
			Require( defects.Contains( "normal-changed" ), $"sync break is named normal-changed, got: {string.Join( ", ", defects )}" );
			var synced = DisplacementBacking.SyncBacking( backing, tilted );
			Require( synced.Normal.AlmostEquals( tilted.Face.Normal, 1e-6f ), "synced backing takes the tilted normal" );
			Require( DisplacementBacking.IsInSync( synced, tilted ), "tilted backing is in sync after sync" );
		} );

		Check( "seal (leak finder): unbacked displacement terrain leaks, backed seals", () =>
		{
			var start = new Vec3( 0, 0, 32 );
			var unbacked = LeakFinder.LeakGraph.RoomWithGap();
			var leaked = LeakFinder.FindLeak( unbacked, start );
			Require( leaked.IsLeaked, "room with a gap leaks (the displacement alone does not seal it)" );
			Require( leaked.GapLocation.AlmostEquals( new Vec3( 64, 0, 32 ), 1e-6f ), "gap is at the +X wall" );

			var backing = DisplacementBacking.GenerateBacking( EastWallDisplacement(), groupId: 7, sourceId: "terrain-east" );
			var gapPortal = unbacked.Portals[0];
			Require( DisplacementBacking.SealsPortal( backing, gapPortal ), "the backing covers the gap portal centre" );
			var sealedGraph = DisplacementBacking.SealGraph( unbacked, backing );
			Require( unbacked.Portals[0].IsOpen, "SealGraph does not mutate the input graph" );
			Require( sealedGraph.Portals[0].IsOpen == false, "the sealed graph closes the gap portal" );
			var sealedResult = LeakFinder.FindLeak( sealedGraph, start );
			Require( !sealedResult.IsLeaked, "backed displacement terrain seals (leak finder passes)" );
			Require( sealedResult.Path.Count == 0, "a sealed map has no leak path" );
		} );

		Check( "seal (leak finder): a backing that misses the gap leaves the leak", () =>
		{
			var start = new Vec3( 0, 0, 32 );
			var graph = LeakFinder.LeakGraph.RoomWithGap();
			// Backing for the opposite (-X) wall: same size, wrong side of the room.
			var westCorners = new[]
			{
				new Vec3( -64, -64, 0 ), new Vec3( -64, 64, 0 ), new Vec3( -64, 64, 128 ), new Vec3( -64, -64, 128 ),
			};
			var west = Displacement.Create( MapBrushFace.FromCorners( westCorners ), 2 );
			var backing = DisplacementBacking.GenerateBacking( west, groupId: 7 );
			Require( !DisplacementBacking.SealsPortal( backing, graph.Portals[0] ), "west backing does not cover the +X gap" );
			var stillLeaking = LeakFinder.FindLeak( DisplacementBacking.SealGraph( graph, backing ), start );
			Require( stillLeaking.IsLeaked, "the leak survives a backing on the wrong wall" );
		} );

		Check( "seal (bsp): room with a displacement wall leaks; adding its backing seals", () =>
		{
			// 128 x 128 x 128 interior, walls 8 thick, floor/ceiling and side
			// walls extended to x=80 so the backing overlaps them volumetrically.
			var floor = BspBrush.Box( new Vec3( -72, -72, -8 ), new Vec3( 80, 72, 0 ), name: "floor" );
			var ceiling = BspBrush.Box( new Vec3( -72, -72, 128 ), new Vec3( 80, 72, 136 ), name: "ceiling" );
			var west = BspBrush.Box( new Vec3( -72, -72, 0 ), new Vec3( -64, 72, 128 ), name: "west" );
			var south = BspBrush.Box( new Vec3( -72, -72, 0 ), new Vec3( 80, -64, 128 ), name: "south" );
			var north = BspBrush.Box( new Vec3( -72, 64, 0 ), new Vec3( 80, 72, 128 ), name: "north" );
			var east = BspBrush.Box( new Vec3( 64, -72, 0 ), new Vec3( 72, 72, 128 ), name: "east" );

			var full = BspBuilder.Build( new[] { floor, ceiling, west, south, north, east } );
			Require( full.IsSealed, "six-wall room seals (builder sanity)" );
			Require( full.VisLeafCount == 1, "six-wall room has exactly one interior visleaf" );

			// The +X wall is a displacement, not a brush: it contributes no brush
			// to the build, so the room leaks — Displacement.SealsMap == false.
			var unbacked = BspBuilder.Build( new[] { floor, ceiling, west, south, north } );
			Require( !unbacked.IsSealed, "room with a displacement wall leaks" );
			Require( unbacked.VisLeafCount == 0, "leaked room has no interior visleaf" );

			var backing = DisplacementBacking.GenerateBacking( EastWallDisplacement(), groupId: 7, sourceId: "terrain-east" );
			var bspBrush = backing.ToBspBrush();
			Require( bspBrush.IsValid, $"backing converts to a valid BSP brush: {string.Join( "; ", bspBrush.Validate() )}" );
			Require( bspBrush.IsDetail == false, "the backing is structural, not func_detail — detail never seals" );
			Near( bspBrush.Volume(), 128f * 128f * 16f, 1f, "backing BSP volume" );
			var backed = BspBuilder.Build( new[] { floor, ceiling, west, south, north, bspBrush } );
			Require( backed.IsSealed, "room with the displacement's backing brush seals" );
			Require( backed.VisLeafCount == 1, "backed room has exactly one interior visleaf" );
		} );

		Check( "round-trip: json preserves group, thickness, source and footprint", () =>
		{
			var backing = DisplacementBacking.GenerateBacking( FloorDisplacement(), groupId: 7, thickness: 8f, sourceId: "floor-terrain" );
			var restored = BackingBrush.FromJsonString( backing.ToJsonString() );
			Require( restored.GroupId == 7, "group survives" );
			Near( restored.Thickness, 8f, 1e-6f, "thickness survives" );
			Require( restored.SourceId == "floor-terrain", "source id survives" );
			for ( var i = 0; i < 4; i++ )
				Require( restored.BaseCorners[i].AlmostEquals( backing.BaseCorners[i], 1e-6f ), $"corner {i} survives" );
			Require( restored.IsValid, "restored backing validates" );
			Throws<ArgumentException>( () => BackingBrush.FromJsonString( "{}" ), "missing corners throws" );
			Throws<ArgumentException>( () => BackingBrush.FromJsonString( "" ), "blank json throws" );
		} );

		Check( "mapbrush: backing converts to a real nodraw MapBrush", () =>
		{
			var backing = DisplacementBacking.GenerateBacking( FloorDisplacement(), groupId: 7 );
			var brush = backing.ToMapBrush();
			Require( brush.FaceCount == 6, "MapBrush has 6 faces" );
			Require( brush.Faces.All( f => f.Material.IsNoDraw ), "every MapBrush face is nodraw" );
			Require( brush.Faces.All( f => f.Material.MaterialName == ToolTexture.NoDrawName ), "every face names tools/toolsnodraw" );
			Require( brush.IsValid, $"MapBrush validates clean: {string.Join( ", ", brush.Validate().Select( d => d.Code ) )}" );
			Near( brush.Volume, 128f * 128f * 16f, 1f, "MapBrush volume is footprint area times thickness" );
			Require( brush.Centroid.AlmostEquals( new Vec3( 64, 64, -8 ), 1e-3f ), "MapBrush centroid is the box centre (64, 64, -8)" );
		} );

		Check( "vmf: backing converts to a nodraw brush in the same group", () =>
		{
			var backing = DisplacementBacking.GenerateBacking( FloorDisplacement(), groupId: 7 );
			var brush = backing.ToVmfBrush( brushId: 41 );
			Require( brush.Id == 41, "brush id carried" );
			Require( brush.GroupId == 7, "VMF brush is in the same group (7)" );
			Require( brush.Faces.Count == 6, "VMF brush has 6 faces" );
			foreach ( var face in brush.Faces )
			{
				Require( face.Material == ToolTexture.NoDrawName, $"VMF face material is {ToolTexture.NoDrawName}" );
				Require( face.Displacement is null, "a backing face carries no displacement" );
				Require( face.Normal.Length > 0.9, "VMF face plane points define a real plane" );
			}
			Require( brush.HasDisplacement == false, "the backing brush has no displacement faces" );
		} );

		Check( "fixture: backed-terrain.json generates backings that seal the gap", () =>
		{
			var dir = FindFixtureDir();
			var path = Path.Combine( dir, "backed-terrain.json" );
			Require( File.Exists( path ), "backed-terrain.json exists" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var gap = root.GetProperty( "gap" ).GetProperty( "center" );
			var gapCenter = new Vec3( gap[0].GetSingle(), gap[1].GetSingle(), gap[2].GetSingle() );
			var backings = new List<BackingBrush>();
			foreach ( var entry in root.GetProperty( "displacements" ).EnumerateArray() )
			{
				var corners = entry.GetProperty( "corners" ).EnumerateArray()
					.Select( c => new Vec3( c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle() ) ).ToArray();
				var disp = Displacement.Create( MapBrushFace.FromCorners( corners ),
					entry.GetProperty( "power" ).GetInt32() );
				var backing = DisplacementBacking.GenerateBacking( disp,
					entry.GetProperty( "groupId" ).GetInt32(),
					entry.GetProperty( "thickness" ).GetSingle(),
					entry.GetProperty( "sourceId" ).GetString() ?? "" );
				Require( backing.IsValid, $"fixture backing {backing.SourceId} validates" );
				Require( backing.Faces.All( f => f.IsNoDraw ), $"fixture backing {backing.SourceId} is all nodraw" );
				backings.Add( backing );
			}
			Require( backings.Count == 1, "fixture has one displacement" );
			var start = new Vec3( 0, 0, 32 );
			var unbacked = LeakFinder.LeakGraph.RoomWithGap( gapCenter );
			Require( LeakFinder.FindLeak( unbacked, start ).IsLeaked, "fixture gap leaks unbacked" );
			var sealedGraph = DisplacementBacking.SealGraph( unbacked, backings );
			Require( !LeakFinder.FindLeak( sealedGraph, start ).IsLeaked, "fixture backings seal the gap" );
		} );
	}
}
