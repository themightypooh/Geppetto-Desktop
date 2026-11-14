using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// Visleaf debug data (hammer-optimisation-visleaf-debug-data).
///
/// Fixtures are the source of truth for the maps: plain-text brush lists under
/// tests/fixtures/hammer/hammer-optimisation-visleaf-debug-data/ — the same
/// `brush &lt;name&gt; [detail]` / `plane nx ny nz d` format as the BSP builder's
/// fixtures (copied geometry, own directory: this ticket must not depend on the
/// dependency ticket's fixture dir). The parser below is the only reader; if a
/// fixture fails to parse, the tests fail, so a stale fixture cannot silently pass.
///
/// The acceptance bar is that the dump matches the BSP builder exactly: the
/// "dump matches the builder exactly" check below compares every leaf, portal,
/// PVS row and cluster against the tree the builder produced, field by field.
/// </summary>
internal static class VisleafDebugTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "visleafdebug", name, test );
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
			var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-optimisation-visleaf-debug-data" );
			if ( Directory.Exists( rel ) ) return rel;
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			while ( dir != null )
			{
				var candidate = Path.Combine( dir.FullName, rel );
				if ( Directory.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			throw new Exception( "visleafdebug fixture dir not found from " + Directory.GetCurrentDirectory() );
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

		Check( "two-room fixture dumps 2+ leaves with the doorway portal and correct PVS sets", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			var dump = VisleafDebug.DumpLeaves( tree );
			Require( dump.VisLeafCount >= 2, $"visleaves {dump.VisLeafCount}" );
			Require( dump.Sealed, "two-room map must dump sealed" );
			Require( dump.Leaves.Count == dump.VisLeafCount, $"dump leaves {dump.Leaves.Count} != visleaves {dump.VisLeafCount}" );
			// The doorway portal: the one vis portal crossing x = 0, 64 wide x 96 high.
			var doorway = dump.Portals.Where( p => p.IsVisPortal
				&& MathF.Abs( p.Normal.x ) > 0.999f && MathF.Abs( p.Center.x ) < 0.01f ).ToList();
			Require( doorway.Count == 1, $"doorway portals {doorway.Count}\n{tree.Describe()}" );
			var portal = doorway[0];
			Require( MathF.Abs( portal.Area - 64 * 96 ) < 61.44f, $"doorway area {portal.Area}, want 6144 (64x96)" );
			Require( MathF.Abs( portal.Center.y ) < 0.5f && MathF.Abs( portal.Center.z - 48 ) < 0.5f,
				$"doorway centre {portal.Center}, want (0, 0, 48)" );
			Require( portal.Polygon.Count >= 4, $"doorway polygon has {portal.Polygon.Count} verts, want at least a quad" );
			// Correct PVS sets: every room-A leaf sees every room-B leaf and vice versa.
			var a = dump.Leaves.Where( l => l.Center.x < 0 ).Select( l => l.VisLeafIndex ).ToList();
			var b = dump.Leaves.Where( l => l.Center.x > 0 ).Select( l => l.VisLeafIndex ).ToList();
			Require( a.Count > 0 && b.Count > 0, "both rooms must have dumped leaves" );
			foreach ( var leaf in dump.Leaves.Where( l => l.Center.x < 0 ) )
				foreach ( var j in b )
					Require( leaf.VisibleVisLeaves.Contains( j ), $"room A leaf {leaf.VisLeafIndex} PVS misses room B leaf {j}" );
			foreach ( var leaf in dump.Leaves.Where( l => l.Center.x > 0 ) )
				foreach ( var i in a )
					Require( leaf.VisibleVisLeaves.Contains( i ), $"room B leaf {leaf.VisLeafIndex} PVS misses room A leaf {i}" );
			// And the portal neighbours agree: a room-A leaf touches a room-B leaf.
			Require( dump.Leaves.Where( l => l.Center.x < 0 ).Any( l =>
				l.PortalVisLeaves.Any( p => b.Contains( p ) ) ), "no dumped portal joins the two rooms" );
		} );

		Check( "dump matches the BSP builder exactly: leaves, portals, PVS and clusters", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			var dump = VisleafDebug.DumpLeaves( tree );
			Require( dump.SchemaVersion == VisleafDebug.SchemaVersion, $"schema {dump.SchemaVersion}" );
			Require( dump.LeafCount == tree.LeafCount, $"leafCount {dump.LeafCount} != {tree.LeafCount}" );
			Require( dump.VisLeafCount == tree.VisLeafCount, $"visLeafCount {dump.VisLeafCount} != {tree.VisLeafCount}" );
			Require( dump.PortalCount == tree.PortalCount, $"portalCount {dump.PortalCount} != {tree.PortalCount}" );
			Require( dump.ClusterCount == tree.ClusterCount, $"clusterCount {dump.ClusterCount} != {tree.ClusterCount}" );
			Require( dump.Sealed == tree.IsSealed, $"sealed {dump.Sealed} != {tree.IsSealed}" );
			var exported = tree.ExportVisLeaves();
			Require( dump.Leaves.Count == exported.Count, "leaf export count mismatch" );
			for ( int i = 0; i < exported.Count; i++ )
			{
				var want = exported[i];
				var got = dump.Leaves[i];
				Require( got.LeafIndex == want.LeafIndex && got.VisLeafIndex == want.VisLeafIndex
					&& got.ClusterId == want.ClusterId, $"leaf {i} identity mismatch" );
				Require( got.Center.Equals( want.Center ) && got.Min.Equals( want.Min ) && got.Max.Equals( want.Max ),
					$"leaf {i} centre/bounds mismatch: {got.Center} vs {want.Center}" );
				Require( got.PortalVisLeaves.SequenceEqual( want.PortalVisLeaves ),
					$"leaf {i} portal neighbours mismatch" );
				Require( got.VisibleVisLeaves.SequenceEqual( want.VisibleVisLeaves ),
					$"leaf {i} PVS mismatch" );
				// PVS cross-checked against the tree's own CanSee, not just the export.
				for ( int j = 0; j < tree.VisLeafCount; j++ )
					Require( got.VisibleVisLeaves.Contains( j ) == tree.CanSee( got.VisLeafIndex, j ),
						$"leaf {i} PVS disagrees with CanSee for {j}" );
			}
			Require( dump.Portals.Count == tree.Portals.Count, "portal count mismatch" );
			for ( int i = 0; i < tree.Portals.Count; i++ )
			{
				var want = tree.Portals[i];
				var got = dump.Portals[i];
				Require( got.LeafA == want.LeafA && got.LeafB == want.LeafB, $"portal {i} leaf pair mismatch" );
				Require( got.VisLeafA == tree.Leaves[want.LeafA].VisLeafIndex
					&& got.VisLeafB == tree.Leaves[want.LeafB].VisLeafIndex, $"portal {i} visleaf pair mismatch" );
				Require( got.IsVisPortal == ( got.VisLeafA >= 0 && got.VisLeafB >= 0 ), $"portal {i} isVisPortal mismatch" );
				Require( MathF.Abs( got.Area - want.Area ) < 0.01f, $"portal {i} area mismatch" );
				Require( got.Center.Equals( want.Center ), $"portal {i} centre mismatch" );
				Require( got.Normal.Equals( want.Plane.Normal ) && got.Distance == want.Plane.Distance,
					$"portal {i} plane mismatch" );
				Require( got.Polygon.Count == want.Polygon.Count
					&& got.Polygon.Zip( want.Polygon, ( g, w ) => g.Equals( w ) ).All( eq => eq ),
					$"portal {i} polygon mismatch" );
			}
			// Clusters: same partition as the leaves' ClusterId, in id order.
			Require( dump.Clusters.Count == tree.ClusterCount, "cluster list count mismatch" );
			for ( int c = 0; c < tree.ClusterCount; c++ )
			{
				var want = exported.Where( d => d.ClusterId == c ).Select( d => d.VisLeafIndex ).OrderBy( x => x ).ToList();
				Require( dump.Clusters[c].SequenceEqual( want ), $"cluster {c} membership mismatch" );
			}
		} );

		Check( "leaf boundaries are closed convex polyhedra matching the builder's volumes", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			var dump = VisleafDebug.DumpLeaves( tree );
			foreach ( var leaf in dump.Leaves )
			{
				Require( leaf.Kind == "empty", $"dumped leaf kind {leaf.Kind}, want empty" );
				Require( leaf.Volume > 0, $"leaf {leaf.VisLeafIndex} volume {leaf.Volume} must be positive" );
				var volume = BspPolyhedron.Volume( tree.Leaves[leaf.LeafIndex].Planes, out _ );
				Require( MathF.Abs( leaf.Volume - volume ) < 0.01f,
					$"leaf {leaf.VisLeafIndex} volume {leaf.Volume} != builder volume {volume}" );
				Require( leaf.Faces.Count >= 4, $"leaf {leaf.VisLeafIndex} has {leaf.Faces.Count} faces, want at least a tetrahedron" );
				var planes = tree.Leaves[leaf.LeafIndex].Planes;
				foreach ( var face in leaf.Faces )
				{
					Require( face.Vertices.Count >= 3, $"leaf {leaf.VisLeafIndex} face has {face.Vertices.Count} verts" );
					Require( BspPolyhedron.PolygonArea( face.Vertices ) > 0.01f,
						$"leaf {leaf.VisLeafIndex} face has zero area" );
					foreach ( var v in face.Vertices )
					{
						var onFace = new BspPlane( face.Normal, face.Distance ).SignedDistance( v );
						Require( MathF.Abs( onFace ) <= 0.01f,
							$"leaf {leaf.VisLeafIndex} face vertex {v} is {onFace} off its face plane" );
						foreach ( var plane in planes )
							Require( plane.SignedDistance( v ) <= 0.01f,
								$"leaf {leaf.VisLeafIndex} face vertex {v} is outside the leaf ({plane.SignedDistance( v )} past {plane})" );
					}
				}
			}
		} );

		Check( "sealed room dumps one leaf, no vis portals, self-only PVS, one cluster", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "sealed-room.txt" ) );
			var dump = VisleafDebug.DumpLeaves( tree );
			Require( dump.Sealed, "sealed room must dump sealed" );
			Require( dump.VisLeafCount == 1 && dump.Leaves.Count == 1, $"visleaves {dump.VisLeafCount}" );
			Require( dump.ClusterCount == 1 && dump.Clusters.Count == 1
				&& dump.Clusters[0].SequenceEqual( new[] { 0 } ), "sealed room must dump one cluster [0]" );
			Require( dump.Portals.All( p => !p.IsVisPortal ), "a sealed room's visleaf must have no vis portals" );
			Require( dump.Leaves[0].PortalVisLeaves.Count == 0, "sealed room leaf must have no portal neighbours" );
			Require( dump.Leaves[0].VisibleVisLeaves.SequenceEqual( new[] { 0 } ), "sealed room PVS must be self-only" );
			Require( MathF.Abs( dump.Leaves[0].Center.x ) < 0.01f && MathF.Abs( dump.Leaves[0].Center.y ) < 0.01f
				&& MathF.Abs( dump.Leaves[0].Center.z - 64 ) < 0.01f, $"room centre {dump.Leaves[0].Center}, want (0, 0, 64)" );
		} );

		Check( "two sealed rooms dump disjoint PVS rows and two clusters", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-sealed.txt" ) );
			var dump = VisleafDebug.DumpLeaves( tree );
			Require( dump.Sealed, "sealed two-room map must dump sealed" );
			Require( dump.ClusterCount == 2, $"clusters {dump.ClusterCount}, want 2" );
			var a = dump.Leaves.Where( l => l.Center.x < 0 ).Select( l => l.VisLeafIndex ).ToList();
			var b = dump.Leaves.Where( l => l.Center.x > 0 ).Select( l => l.VisLeafIndex ).ToList();
			Require( a.Count > 0 && b.Count > 0, "both rooms must have dumped leaves" );
			foreach ( var leaf in dump.Leaves.Where( l => l.Center.x < 0 ) )
				foreach ( var j in b )
					Require( !leaf.VisibleVisLeaves.Contains( j ), $"room A leaf {leaf.VisLeafIndex} PVS sees room B leaf {j}" );
			foreach ( var leaf in dump.Leaves.Where( l => l.Center.x > 0 ) )
				foreach ( var i in a )
					Require( !leaf.VisibleVisLeaves.Contains( i ), $"room B leaf {leaf.VisLeafIndex} PVS sees room A leaf {i}" );
			foreach ( var portal in dump.Portals.Where( p => p.IsVisPortal ) )
			{
				var la = dump.Leaves[portal.VisLeafA];
				var lb = dump.Leaves[portal.VisLeafB];
				Require( ( la.Center.x < 0 ) == ( lb.Center.x < 0 ),
					$"vis portal {portal.LeafA}<->{portal.LeafB} joins the two rooms" );
			}
		} );

		Check( "leaked room dumps zero visleaves and sealed false", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "leak-room.txt" ) );
			var dump = VisleafDebug.DumpLeaves( tree );
			Require( dump.VisLeafCount == 0 && dump.Leaves.Count == 0, $"visleaves {dump.VisLeafCount}" );
			Require( !dump.Sealed, "leaked room must dump unsealed" );
			Require( dump.ClusterCount == 0 && dump.Clusters.Count == 0, "leaked room must dump zero clusters" );
			Require( dump.Portals.All( p => !p.IsVisPortal ), "a leaked map has no vis portals" );
			var json = dump.ToJson();
			using var doc = JsonDocument.Parse( json );
			Require( doc.RootElement.GetProperty( "visLeafCount" ).GetInt32() == 0, "JSON must carry visLeafCount 0" );
			Require( doc.RootElement.GetProperty( "leaves" ).GetArrayLength() == 0, "JSON leaves must be empty" );
		} );

		Check( "dump JSON is deterministic: same tree, same bytes; same map built twice, same bytes", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			var first = VisleafDebug.DumpLeaves( tree ).ToJson();
			var second = VisleafDebug.DumpLeaves( tree ).ToJson();
			Require( first == second, "two dumps of the same tree differ" );
			var rebuilt = VisleafDebug.DumpLeaves( BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) ) ).ToJson();
			Require( first == rebuilt, "two builds of the same map dump different JSON" );
			Require( !first.Contains( "-0,", StringComparison.Ordinal ) && !first.Contains( "-0]", StringComparison.Ordinal ),
				"JSON must not contain -0 (same value, two spellings)" );
		} );

		Check( "dump JSON round-trips the counts, doorway portal and PVS sets", () =>
		{
			var tree = BspBuilder.Build( LoadFixture( "two-room-doorway.txt" ) );
			var json = VisleafDebug.DumpLeaves( tree ).ToJson();
			using var doc = JsonDocument.Parse( json );
			var root = doc.RootElement;
			Require( root.GetProperty( "schemaVersion" ).GetInt32() == VisleafDebug.SchemaVersion, "JSON schemaVersion mismatch" );
			Require( root.GetProperty( "format" ).GetString() == "geppetto-visleaf-debug/1", "JSON format tag mismatch" );
			Require( root.GetProperty( "sealed" ).GetBoolean(), "JSON sealed must be true" );
			Require( root.GetProperty( "visLeafCount" ).GetInt32() == tree.VisLeafCount, "JSON visLeafCount mismatch" );
			Require( root.GetProperty( "portalCount" ).GetInt32() == tree.PortalCount, "JSON portalCount mismatch" );
			Require( root.GetProperty( "clusterCount" ).GetInt32() == tree.ClusterCount, "JSON clusterCount mismatch" );
			var leaves = root.GetProperty( "leaves" );
			Require( leaves.GetArrayLength() == tree.VisLeafCount, "JSON leaves length mismatch" );
			foreach ( var leaf in leaves.EnumerateArray() )
			{
				var idx = leaf.GetProperty( "visLeafIndex" ).GetInt32();
				var pvs = leaf.GetProperty( "visibleVisLeaves" ).EnumerateArray().Select( e => e.GetInt32() ).ToList();
				for ( int j = 0; j < tree.VisLeafCount; j++ )
					Require( pvs.Contains( j ) == tree.CanSee( idx, j ), $"JSON PVS for leaf {idx} disagrees with CanSee for {j}" );
				Require( leaf.GetProperty( "faces" ).GetArrayLength() >= 4, $"JSON leaf {idx} has too few boundary faces" );
				Require( leaf.GetProperty( "volume" ).GetSingle() > 0, $"JSON leaf {idx} volume must be positive" );
			}
			var portals = root.GetProperty( "portals" );
			var doorway = portals.EnumerateArray().Where( p =>
				p.GetProperty( "isVisPortal" ).GetBoolean()
				&& MathF.Abs( p.GetProperty( "normal" )[0].GetSingle() ) > 0.999f
				&& MathF.Abs( p.GetProperty( "center" )[0].GetSingle() ) < 0.01f ).ToList();
			Require( doorway.Count == 1, $"JSON doorway portals {doorway.Count}" );
			Require( MathF.Abs( doorway[0].GetProperty( "area" ).GetSingle() - 6144 ) < 61.44f,
				$"JSON doorway area {doorway[0].GetProperty( "area" ).GetSingle()}, want 6144" );
		} );

		Check( "brush-list overload dumps the same JSON as build-then-dump", () =>
		{
			var brushes = LoadFixture( "two-room-doorway.txt" );
			var viaOverload = VisleafDebug.DumpLeaves( brushes ).ToJson();
			var viaBuild = VisleafDebug.DumpLeaves( BspBuilder.Build( brushes ) ).ToJson();
			Require( viaOverload == viaBuild, "brush-list overload must dump exactly what build-then-dump dumps" );
		} );

		Check( "null tree and null dump are rejected with a named reason", () =>
		{
			ThrowsNamed( () => VisleafDebug.DumpLeaves( (BspTree)null! ), "tree" );
			ThrowsNamed( () => VisleafDebug.ToJson( (VisleafDebugDump)null! ), "dump" );
		} );
	}
}
