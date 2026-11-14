using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;

/// <summary>
/// BSP tree builder + portals + visleaves + parallel PVS (hammer-bsp-core-tree-portals-leaves).
///
/// Fixtures are the source of truth for the maps: plain-text brush lists under
/// tests/fixtures/hammer/hammer-bsp-core-tree-portals-leaves/ — one `brush &lt;name&gt;
/// [detail]` line, then one `plane nx ny nz d` line per face (outward unit normal,
/// plane Dot(n, p) == d, solid side Dot(n, p) &lt;= d). The parser below is the only
/// reader; if a fixture fails to parse, the tests fail, so a stale fixture cannot
/// silently pass.
///
/// MapBrush landed 2026-11-13 ~16:08 (hammer-01) while this ticket was in
/// flight, with the filed API exactly as specified (MapBrushFace: Normal +
/// Distance + Material; MapBrush.Faces / FromPlanes / FromVertices / Validate).
/// The fixtures are still built with BspBrush.FromPlanes / BspBrush.Box, which
/// mirror the filed MapBrush constructors 1:1; the integrator's FromMapBrush
/// adapter (see BspBuilder.cs header) consumes the same planes.
/// </summary>
internal static class BspBuilderTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "bspbuilder", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void ThrowsNamed( Action action, string mustContain )
		{
			try { action(); }
			catch ( Exception ex )
			{
				Require( ex.Message.Contains( mustContain, StringComparison.Ordinal ),
					$"error '{ex.Message}' did not name '{mustContain}'" );
				return;
			}
			throw new Exception( $"expected a rejection naming '{mustContain}', got silent success" );
		}

		static string FixtureDir()
		{
			var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-bsp-core-tree-portals-leaves" );
			if ( Directory.Exists( rel ) ) return rel;
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			while ( dir != null )
			{
				var candidate = Path.Combine( dir.FullName, rel );
				if ( Directory.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			throw new Exception( "bsp fixture dir not found from " + Directory.GetCurrentDirectory() );
		}

		static List<BspBrush> LoadFixture( string name )
		{
			var path = Path.Combine( FixtureDir(), name );
			var brushes = new List<BspBrush>();
			var planes = new List<BspPlane>();
			var brushName = "";
			var isDetail = false;
			void Flush()
			{
				if ( planes.Count > 0 )
					brushes.Add( BspBrush.FromPlanes( planes, isDetail, brushName ) );
				planes = new List<BspPlane>();
			}
			foreach ( var raw in File.ReadLines( path ) )
			{
				var line = raw.Trim();
				if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
				var parts = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
				if ( parts[0] == "brush" )
				{
					Flush();
					if ( parts.Length < 2 ) throw new Exception( $"{name}: brush line needs a name: '{line}'" );
					brushName = parts[1];
					isDetail = parts.Length > 2 && parts[2] == "detail";
				}
				else if ( parts[0] == "plane" )
				{
					if ( parts.Length != 5 ) throw new Exception( $"{name}: plane line needs nx ny nz d: '{line}'" );
					float Num( int i ) => float.Parse( parts[i], CultureInfo.InvariantCulture );
					planes.Add( new BspPlane( new Vec3( Num( 1 ), Num( 2 ), Num( 3 ) ), Num( 4 ) ) );
				}
				else throw new Exception( $"{name}: unknown line: '{line}'" );
			}
			Flush();
			if ( brushes.Count == 0 ) throw new Exception( $"{name}: no brushes parsed" );
			return brushes;
		}

		Check( "sealed room is one visleaf, sealed, no portals, sees only itself", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "sealed-room.txt" ) );
			Require( tree.VisLeafCount == 1, $"visleaves {tree.VisLeafCount}\n{tree.Describe()}" );
			Require( tree.IsSealed, "sealed room must report sealed" );
			// Portals includes the outside region's own adjacencies (the void outside
			// the shell is split into leaves too); what must be zero is portals
			// touching a visleaf.
			Require( tree.Portals.All( p => !tree.Leaves[p.LeafA].IsVisLeaf && !tree.Leaves[p.LeafB].IsVisLeaf ),
				"a sealed room's visleaf must have no portals" );
			Require( tree.ClusterCount == 1, $"clusters {tree.ClusterCount}" );
			Require( tree.CanSee( 0, 0 ), "a leaf must see itself" );
			var data = tree.ExportVisLeaves();
			Require( data.Count == 1 && data[0].ClusterId == 0, "export must carry the one visleaf in cluster 0" );
			Require( data[0].PortalVisLeaves.Count == 0 && data[0].VisibleVisLeaves.SequenceEqual( new[] { 0 } ),
				"export PVS must be self-only" );
			Require( MathF.Abs( data[0].Center.x ) < 0.01f && MathF.Abs( data[0].Center.y ) < 0.01f
				&& MathF.Abs( data[0].Center.z - 64 ) < 0.01f, $"room centre {data[0].Center}, want (0, 0, 64)" );
		} );

		Check( "two rooms with a doorway have exactly one doorway portal of the right size and place", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			Require( tree.VisLeafCount >= 2, $"visleaves {tree.VisLeafCount}" );
			Require( tree.IsSealed, "two-room map must be sealed" );
			// The x = 0 plane also splits the outside void, so portals at x = 0 exist
			// outside the building too; the doorway portal is the one between visleaves.
			var doorway = tree.Portals.Where( p =>
				tree.Leaves[p.LeafA].IsVisLeaf && tree.Leaves[p.LeafB].IsVisLeaf
				&& MathF.Abs( p.Plane.Normal.x ) > 0.999f && MathF.Abs( p.Center.x ) < 0.01f ).ToList();
			Require( doorway.Count == 1, $"doorway portals {doorway.Count}\n{tree.Describe()}" );
			var portal = doorway[0];
			Require( MathF.Abs( portal.Area - 64 * 96 ) < 61.44f, $"doorway area {portal.Area}, want 6144 (64x96)" );
			Require( MathF.Abs( portal.Center.y ) < 0.5f && MathF.Abs( portal.Center.z - 48 ) < 0.5f,
				$"doorway centre {portal.Center}, want (0, 0, 48)" );
			Require( portal.Polygon.Count >= 4, $"doorway polygon has {portal.Polygon.Count} verts, want at least a quad" );
		} );

		Check( "two rooms with a doorway see each other through it (PVS)", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			var a = tree.VisLeaves.Where( l => l.Center.x < 0 ).Select( l => l.VisLeafIndex ).ToList();
			var b = tree.VisLeaves.Where( l => l.Center.x > 0 ).Select( l => l.VisLeafIndex ).ToList();
			Require( a.Count > 0 && b.Count > 0, "both rooms must have visleaves" );
			foreach ( var i in a )
				foreach ( var j in b )
					Require( tree.CanSee( i, j ), $"room A leaf {i} cannot see room B leaf {j}" );
			foreach ( var j in b )
				foreach ( var i in a )
					Require( tree.CanSee( j, i ), $"room B leaf {j} cannot see room A leaf {i}" );
		} );

		Check( "two sealed rooms see only themselves and form two clusters", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-sealed.txt" ) );
			Require( tree.IsSealed, "sealed two-room map must be sealed" );
			var a = tree.VisLeaves.Where( l => l.Center.x < 0 ).Select( l => l.VisLeafIndex ).ToList();
			var b = tree.VisLeaves.Where( l => l.Center.x > 0 ).Select( l => l.VisLeafIndex ).ToList();
			Require( a.Count > 0 && b.Count > 0, "both rooms must have visleaves" );
			foreach ( var i in a )
				foreach ( var j in b )
					Require( !tree.CanSee( i, j ), $"room A leaf {i} sees room B leaf {j} through a solid wall" );
			foreach ( var i in a )
				foreach ( var j in a )
					Require( tree.CanSee( i, j ), $"room A leaf {i} cannot see room A leaf {j}" );
			foreach ( var i in b )
				foreach ( var j in b )
					Require( tree.CanSee( i, j ), $"room B leaf {i} cannot see room B leaf {j}" );
			Require( tree.ClusterCount == 2, $"clusters {tree.ClusterCount}, want 2 (one PVS row per room)" );
			// No VIS portal joins the rooms (outside-region portals may cross x = 0
			// above/beside the building; they are not vis portals).
			foreach ( var portal in tree.Portals )
			{
				var la = tree.Leaves[portal.LeafA];
				var lb = tree.Leaves[portal.LeafB];
				if ( !la.IsVisLeaf || !lb.IsVisLeaf ) continue;
				Require( (la.Center.x < 0) == (lb.Center.x < 0 ),
					$"portal {portal.LeafA}<->{portal.LeafB} joins the two rooms" );
			}
		} );

		Check( "a room with a missing wall leaks: no visleaves, not sealed", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "leak-room.txt" ) );
			Require( tree.VisLeafCount == 0, $"visleaves {tree.VisLeafCount}\n{tree.Describe()}" );
			Require( !tree.IsSealed, "leaked room must report unsealed" );
		} );

		Check( "detail brushes do not split leaves", () =>
		{
			var brushes = LoadFixture( "sealed-room.txt" );
			brushes.Add( BspBrush.Box( new Vec3( -16, -16, 0 ), new Vec3( 16, 16, 64 ), isDetail: true, name: "pillar-detail" ) );
			var tree = BspBuilder.Build( brushes );
			Require( tree.VisLeafCount == 1, $"visleaves {tree.VisLeafCount} with a detail pillar in the room" );
			Require( tree.IsSealed, "room with a detail pillar must stay sealed" );
		} );

		Check( "empty brush list is rejected with a named reason", () =>
			ThrowsNamed( () => BspBuilder.Build( Array.Empty<BspBrush>() ), "at least one brush" ) );

		Check( "a brush with fewer than four planes is rejected with a named reason", () =>
		{
			var bad = BspBrush.FromPlanes( new[]
			{
				new BspPlane( new Vec3( -1, 0, 0 ), 0 ),
				new BspPlane( new Vec3( 0, -1, 0 ), 0 ),
				new BspPlane( new Vec3( 0, 0, -1 ), 0 ),
			}, name: "tri" );
			ThrowsNamed( () => BspBuilder.Build( new[] { bad } ), "fewer than 4 planes" );
		} );

		Check( "build is deterministic: same input, same leaves, portals and PVS", () =>
		{
			var first = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) ).Describe();
			var second = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) ).Describe();
			Require( first == second, "same input must build the same tree" );
		} );

		Check( "parallel and single-threaded PVS agree", () =>
		{
			var brushes = LoadFixture( "two-room-doorway.txt" );
			var parallel = BspBuilder.Build( brushes ).Describe();
			var single = BspBuilder.Build( brushes, new BspBuildOptions { MaxDegreeOfParallelism = 1 } ).Describe();
			Require( parallel == single, "PVS must not depend on thread count" );
		} );

		Check( "a lone solid box has no interior void", () =>
		{
			var tree = BspBuilder.Build( new[]
			{
				BspBrush.Box( new Vec3( -64, -64, 0 ), new Vec3( 64, 64, 64 ), name: "lone" ),
			} );
			Require( tree.VisLeafCount == 0, $"visleaves {tree.VisLeafCount}" );
			Require( !tree.IsSealed, "a lone box has no interior void to seal" );
		} );

		Check( "box brush volume and centroid match the analytic values", () =>
		{
			var box = BspBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 128, 128, 64 ), name: "box" );
			Require( box.IsValid, string.Join( "; ", box.Validate() ) );
			Require( MathF.Abs( box.Volume() - 128 * 128 * 64 ) < 1f, $"volume {box.Volume()}, want 1048576" );
			var c = box.Centroid();
			Require( MathF.Abs( c.x - 64 ) < 0.01f && MathF.Abs( c.y - 64 ) < 0.01f && MathF.Abs( c.z - 32 ) < 0.01f,
				$"centroid {c}, want (64, 64, 32)" );
		} );

		Check( "zero-volume and duplicate-plane brushes fail Validate with distinct named defects", () =>
		{
			var flat = BspBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 0, 128, 64 ), name: "flat" );
			var flatDefects = flat.Validate();
			Require( flatDefects.Any( d => d.Contains( "zero volume", StringComparison.Ordinal ) ),
				$"flat brush defects: {string.Join( "; ", flatDefects )}" );
			ThrowsNamed( () => BspBuilder.Build( new[] { flat } ), "zero volume" );

			var box = BspBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 128, 128, 64 ), name: "box" );
			var dupPlanes = box.Planes.ToList();
			dupPlanes.Add( box.Planes[0] );
			var dup = BspBrush.FromPlanes( dupPlanes, name: "dup" );
			var dupDefects = dup.Validate();
			Require( dupDefects.Any( d => d.Contains( "duplicate plane", StringComparison.Ordinal ) ),
				$"dup brush defects: {string.Join( "; ", dupDefects )}" );
			Require( !dupDefects.Any( d => d.Contains( "zero volume", StringComparison.Ordinal ) ),
				"a duplicated face must not be reported as zero volume" );
			ThrowsNamed( () => BspBuilder.Build( new[] { dup } ), "duplicate plane" );
		} );

		Check( "VisLeafAt finds the room a point stands in and rejects points inside walls", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			var inA = tree.VisLeafAt( new Vec3( -128, 0, 32 ) );
			Require( inA >= 0, "point in room A must be in a visleaf" );
			Require( tree.VisLeaves[inA].Center.x < 0, "room A point must land in a room A leaf" );
			var inB = tree.VisLeafAt( new Vec3( 128, 0, 32 ) );
			Require( inB >= 0 && tree.VisLeaves[inB].Center.x > 0, "point in room B must land in a room B leaf" );
			var inWall = tree.VisLeafAt( new Vec3( 0, -100, 32 ) );
			Require( inWall == -1, $"point inside the shared wall must be in no visleaf, got {inWall}" );
			var above = tree.VisLeafAt( new Vec3( 0, 0, 500 ) );
			Require( above == -1, $"point above the map must be in no visleaf, got {above}" );
		} );

		Check( "ExportVisLeaves lists every visleaf with its portals and PVS", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			var data = tree.ExportVisLeaves();
			Require( data.Count == tree.VisLeafCount, $"export has {data.Count} entries for {tree.VisLeafCount} visleaves" );
			foreach ( var d in data )
			{
				Require( d.VisibleVisLeaves.Contains( d.VisLeafIndex ), "a leaf must see itself in the export" );
				foreach ( var p in d.PortalVisLeaves )
					Require( p >= 0 && p < tree.VisLeafCount, $"portal neighbour {p} is not a visleaf" );
				Require( d.ClusterId >= 0 && d.ClusterId < tree.ClusterCount, $"cluster {d.ClusterId} out of range" );
			}
			var aLeaves = data.Where( d => d.Center.x < 0 ).ToList();
			var bLeaves = data.Where( d => d.Center.x > 0 ).ToList();
			Require( aLeaves.Any( d => d.PortalVisLeaves.Any( p => bLeaves.Any( b => b.VisLeafIndex == p ) ) ),
				"no exported portal joins the two rooms" );
		} );
	}
}
