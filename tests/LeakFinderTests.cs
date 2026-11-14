using System;
using System.IO;
using System.Linq;
using Effigy;

internal static class LeakFinderTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "leakfinder", name, test );
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
		bool Near( Vec3 a, Vec3 b ) => a.AlmostEquals( b, 1e-3f );

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-leak-detection-pointfile" );
					if ( File.Exists( Path.Combine( candidate, "gap-room.lin" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "leakfinder fixture dir not found (gap-room.lin missing above cwd / BaseDirectory)" );
		}

		Check( "sealed room passes with no leak path and no gap", () =>
		{
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.SealedRoom(), new Vec3( 0, 0, 32 ) );
			Require( !result.IsLeaked, "sealed room reported a leak" );
			Require( result.Path.Count == 0, $"sealed room has a path of {result.Path.Count} points" );
			Require( result.GapLeafId == -1 && result.GapPortalId == -1, $"sealed gap ids {result.GapLeafId}/{result.GapPortalId}" );
			Require( result.ToPointFileText() == string.Empty, "sealed room exported a point file" );
			Require( result.VisitedLeafIds.SequenceEqual( new[] { 0 } ), "flood-fill visited " + string.Join( ",", result.VisitedLeafIds ) );
		} );

		Check( "room with a 1-unit gap fails with a point file from the player start out through the gap", () =>
		{
			var start = new Vec3( 0, 0, 32 );
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.RoomWithGap(), start );
			Require( result.IsLeaked, "gap room reported sealed" );
			Require( result.Path.Count == 3, $"path has {result.Path.Count} points, expected 3" );
			Require( Near( result.Path[0], start ), $"path starts at {result.Path[0]}, not the player start" );
			Require( Near( result.Path[1], new Vec3( 64, 0, 32 ) ), $"path does not go through the gap: {result.Path[1]}" );
			Require( Near( result.Path[2], new Vec3( 80, 0, 32 ) ), $"path does not exit past the gap: {result.Path[2]}" );
			Require( Near( result.ExitPoint, result.Path[2] ), "exit point is not the last path point" );
			Require( result.Path[2].x > 64, "last path point is still inside the room" );
		} );

		Check( "gap location reports the nearest leaf and the gap portal", () =>
		{
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.RoomWithGap(), new Vec3( 0, 0, 32 ) );
			Require( result.GapLeafId == 0, $"gap leaf {result.GapLeafId}, expected interior leaf 0" );
			Require( result.GapPortalId == 0, $"gap portal {result.GapPortalId}, expected portal 0" );
			Require( Near( result.GapLocation, new Vec3( 64, 0, 32 ) ), $"gap location {result.GapLocation}" );
			Require( result.StartLeafId == 0, $"start leaf {result.StartLeafId}" );
		} );

		Check( "point-file text is one 'x y z' line per point and round-trips", () =>
		{
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.RoomWithGap(), new Vec3( 0, 0, 32 ) );
			Require( result.ToPointFileText() == "0 0 32\n64 0 32\n80 0 32\n",
				"point-file text was: " + result.ToPointFileText().Replace( "\n", "|" ) );
			var parsed = LeakFinder.ParsePointFileText( result.ToPointFileText() );
			Require( parsed.Count == result.Path.Count, "round-trip point count moved" );
			for ( int i = 0; i < parsed.Count; i++ )
				Require( Near( parsed[i], result.Path[i] ), $"round-trip point {i} moved" );
			var withBlanks = LeakFinder.ParsePointFileText( "\n0 0 32\n\n64 0 32\n" );
			Require( withBlanks.Count == 2, "blank lines were not skipped" );
			Throws<FormatException>( () => LeakFinder.ParsePointFileText( "0 0\n" ), "short line" );
			Throws<FormatException>( () => LeakFinder.ParsePointFileText( "a b c\n" ), "non-numeric line" );
		} );

		Check( "point file writes to .lin and .pts and reads back identical", () =>
		{
			Directory.CreateDirectory( settings.WorkDirectory );
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.RoomWithGap(), new Vec3( 0, 0, 32 ) );
			var lin = Path.Combine( settings.WorkDirectory, "leak.lin" );
			var pts = Path.Combine( settings.WorkDirectory, "leak.pts" );
			LeakFinder.WritePointFile( lin, result );
			LeakFinder.WritePointFile( pts, result.Path );
			Require( File.ReadAllText( lin ) == File.ReadAllText( pts ), ".lin and .pts differ" );
			var back = LeakFinder.ReadPointFile( lin );
			Require( back.Count == result.Path.Count, "read-back point count moved" );
			for ( int i = 0; i < back.Count; i++ )
				Require( Near( back[i], result.Path[i] ), $"read-back point {i} moved" );
			Throws<ArgumentException>( () => LeakFinder.WritePointFile( "  ", result.Path ), "blank point-file path" );
		} );

		Check( "two-room map finds a leak in the far room through the doorway", () =>
		{
			var start = new Vec3( -64, 0, 32 );
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.TwoRooms( leakInFarRoom: true ), start );
			Require( result.IsLeaked, "far-room leak not found" );
			Require( Near( result.Path[0], start ), "path does not start at the player start" );
			Require( result.Path.Any( p => Near( p, new Vec3( 0, 0, 32 ) ) ), "path never crosses the doorway portal" );
			Require( result.Path.Any( p => Near( p, new Vec3( 64, 0, 64 ) ) ), "path never enters the far room" );
			Require( Near( result.Path[^1], new Vec3( 144, 0, 32 ) ), $"path does not exit past the far gap: {result.Path[^1]}" );
			Require( result.GapLeafId == 1 && result.GapPortalId == 1, $"gap {result.GapLeafId}/{result.GapPortalId}, expected leaf 1 / portal 1" );
			Require( result.VisitedLeafIds.SequenceEqual( new[] { 0, 1, 2 } ), "visited " + string.Join( ",", result.VisitedLeafIds ) );
		} );

		Check( "two-room map with no gap passes", () =>
		{
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.TwoRooms( leakInFarRoom: false ), new Vec3( -64, 0, 32 ) );
			Require( !result.IsLeaked, "sealed two-room map reported a leak" );
			Require( result.VisitedLeafIds.SequenceEqual( new[] { 0, 1 } ), "flood-fill should reach both rooms: " + string.Join( ",", result.VisitedLeafIds ) );
		} );

		Check( "a closed portal blocks the leak", () =>
		{
			var graph = LeakFinder.LeakGraph.SealedRoom();
			graph.AddPortal( new LeakFinder.LeakPortal( 0, 0, 1, new Vec3( 64, 0, 32 ), isOpen: false ) );
			var result = LeakFinder.FindLeak( graph, new Vec3( 0, 0, 32 ) );
			Require( !result.IsLeaked, "closed gap portal still leaked" );
		} );

		Check( "a solid leaf is never entered, so a leak behind it is not found", () =>
		{
			var graph = new LeakFinder.LeakGraph();
			graph.AddLeaf( new LeakFinder.LeakLeaf( 0, new Vec3( 0, 0, 64 ), new Vec3( -64, -64, 0 ), new Vec3( 64, 64, 128 ) ) );
			graph.AddLeaf( new LeakFinder.LeakLeaf( 1, new Vec3( 96, 0, 64 ), new Vec3( 64, -64, 0 ), new Vec3( 128, 64, 128 ), isSolid: true ) );
			graph.AddLeaf( new LeakFinder.LeakLeaf( 2, new Vec3( 0, 0, 256 ), new Vec3( -1024, -1024, -1024 ), new Vec3( 1024, 1024, 1024 ), isVoid: true ) );
			graph.AddPortal( new LeakFinder.LeakPortal( 0, 0, 1, new Vec3( 64, 0, 32 ) ) );
			graph.AddPortal( new LeakFinder.LeakPortal( 1, 1, 2, new Vec3( 128, 0, 32 ) ) );
			var result = LeakFinder.FindLeak( graph, new Vec3( 0, 0, 32 ) );
			Require( !result.IsLeaked, "flood-fill crossed a solid leaf" );
			Require( result.VisitedLeafIds.SequenceEqual( new[] { 0 } ), "visited " + string.Join( ",", result.VisitedLeafIds ) );
		} );

		Check( "invalid input fails loudly: null graph, empty graph, start in no leaf", () =>
		{
			Throws<ArgumentNullException>( () => LeakFinder.FindLeak( null, new Vec3( 0, 0, 32 ) ), "null graph" );
			Throws<ArgumentException>( () => LeakFinder.FindLeak( new LeakFinder.LeakGraph(), new Vec3( 0, 0, 32 ) ), "empty graph" );
			Throws<ArgumentException>( () => LeakFinder.FindLeak( LeakFinder.LeakGraph.SealedRoom(), new Vec3( 5000, 5000, 5000 ) ), "start in no leaf" );
			Throws<ArgumentNullException>( () => LeakFinder.ToPointFileText( null ), "null path" );
		} );

		Check( "same graph and start give the identical path and gap every run", () =>
		{
			var graph = LeakFinder.LeakGraph.TwoRooms( leakInFarRoom: true );
			var a = LeakFinder.FindLeak( graph, new Vec3( -64, 0, 32 ) );
			var b = LeakFinder.FindLeak( graph, new Vec3( -64, 0, 32 ) );
			Require( a.ToPointFileText() == b.ToPointFileText(), "point-file text differed between runs" );
			Require( a.GapLeafId == b.GapLeafId && a.GapPortalId == b.GapPortalId, "gap moved between runs" );
			Require( a.VisitedLeafIds.SequenceEqual( b.VisitedLeafIds ), "visited order differed between runs" );
		} );

		Check( "a player start already in the void is leaked immediately", () =>
		{
			var start = new Vec3( 0, 0, 512 );
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.SealedRoom(), start );
			Require( result.IsLeaked, "start in the void reported sealed" );
			Require( result.Path.Count == 1 && Near( result.Path[0], start ), "void-start path is not just the start" );
			Require( result.GapPortalId == -1 && result.StartLeafId == 1, $"void-start gap/leaf {result.GapPortalId}/{result.StartLeafId}" );
		} );

		Check( "fixture gap-room.lin matches the computed point file exactly", () =>
		{
			var dir = FixtureDir();
			var expected = File.ReadAllText( Path.Combine( dir, "gap-room.lin" ) );
			var result = LeakFinder.FindLeak( LeakFinder.LeakGraph.RoomWithGap(), new Vec3( 0, 0, 32 ) );
			Require( result.ToPointFileText() == expected, "computed point file differs from fixture gap-room.lin" );
			var parsed = LeakFinder.ParsePointFileText( expected );
			Require( parsed.Count == 3, $"fixture has {parsed.Count} points" );
			Require( Near( parsed[0], new Vec3( 0, 0, 32 ) ), "fixture does not start at the player start" );
			Require( Near( parsed[^1], new Vec3( 80, 0, 32 ) ), "fixture does not end outside the gap" );
			var pts = File.ReadAllText( Path.Combine( dir, "gap-room.pts" ) );
			Require( pts == expected, ".pts fixture differs from .lin fixture" );
		} );
	}
}
