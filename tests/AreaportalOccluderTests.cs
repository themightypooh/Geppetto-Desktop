using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

/// <summary>
/// Tests for areaportal + occluder helpers (hammer-optimisation-areaportal-occluder):
/// a closed areaportal cuts the room behind it out of the PVS, an open one keeps
/// both rooms mutually visible, occluders cull props fully behind them (and never
/// a prop that peeks past an edge), and misconfigured areaportals/occluders are
/// named validation issues. Fixtures live under
/// tests/fixtures/hammer/hammer-optimisation-areaportal-occluder/ — the two-room
/// doorway map (same brush list as the BSP core fixture), an areaportals list
/// (resolved to portals by doorway centre), and an occluders list (quads).
///
/// Map geometry (inches, Z-up): room A x -256..0, room B x 0..256, both y -128..128,
/// z 0..128; doorway at x = 0, y -32..32, z 0..96 (64 wide x 96 high, centre
/// (0, 0, 48)). The occluder curtain-b is a 128x128 quad at x = 128 in room B,
/// spanning y -64..64, z 0..128, facing -X toward room A.
/// </summary>
internal static class AreaportalOccluderTests
{
	sealed class TestBrush
	{
		public string Name = "";
		public TestBrush( string name ) { Name = name; }
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "areaportal", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		AreaportalOccluderException ThrowsOccluder( Action action )
		{
			try { action(); }
			catch ( AreaportalOccluderException ex ) { return ex; }
			throw new Exception( "expected AreaportalOccluderException, got silent success" );
		}

		string RepoRoot()
		{
			string? dir = AppContext.BaseDirectory;
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
				dir = Directory.GetParent( dir )?.FullName;
			}
			dir = Directory.GetCurrentDirectory();
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
				dir = Directory.GetParent( dir )?.FullName;
			}
			throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
		}

		string FixtureDir()
		{
			var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-optimisation-areaportal-occluder" );
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			while ( dir != null )
			{
				var candidate = Path.Combine( dir.FullName, rel );
				if ( Directory.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
			while ( dir != null )
			{
				var candidate = Path.Combine( dir.FullName, rel );
				if ( Directory.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			throw new Exception( "areaportal fixture dir not found from " + Directory.GetCurrentDirectory() );
		}

		string Fixture( string name ) => Path.Combine( FixtureDir(), name );

		static List<BspBrush> LoadBrushes( string path )
		{
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
					if ( parts.Length < 2 ) throw new Exception( $"{path}: brush line needs a name: '{line}'" );
					brushName = parts[1];
					isDetail = parts.Length > 2 && parts[2] == "detail";
				}
				else if ( parts[0] == "plane" )
				{
					if ( parts.Length != 5 ) throw new Exception( $"{path}: plane line needs nx ny nz d: '{line}'" );
					float Num( int i ) => float.Parse( parts[i], CultureInfo.InvariantCulture );
					planes.Add( new BspPlane( new Vec3( Num( 1 ), Num( 2 ), Num( 3 ) ), Num( 4 ) ) );
				}
				else throw new Exception( $"{path}: unknown line: '{line}'" );
			}
			Flush();
			if ( brushes.Count == 0 ) throw new Exception( $"{path}: no brushes parsed" );
			return brushes;
		}

		BspTree TwoRoomTree() => BspBuilder.Build( LoadBrushes( Fixture( "two-room-doorway.txt" ) ) );

		// The doorway portal: the one vis-to-vis portal on the x = 0 plane.
		BspPortal DoorwayPortal( BspTree tree )
		{
			var doorway = tree.Portals.Where( p =>
				tree.Leaves[p.LeafA].IsVisLeaf && tree.Leaves[p.LeafB].IsVisLeaf
				&& MathF.Abs( p.Plane.Normal.x ) > 0.999f && MathF.Abs( p.Center.x ) < 0.01f ).ToList();
			if ( doorway.Count != 1 )
				throw new Exception( $"doorway portals {doorway.Count}, want 1\n{tree.Describe()}" );
			return doorway[0];
		}

		// Visleaf indices on each side of the doorway, by room centre.
		(int[] a, int[] b) RoomLeaves( BspTree tree )
		{
			var a = tree.VisLeaves.Where( l => l.Center.x < 0 ).Select( l => l.VisLeafIndex ).ToArray();
			var b = tree.VisLeaves.Where( l => l.Center.x > 0 ).Select( l => l.VisLeafIndex ).ToArray();
			if ( a.Length == 0 || b.Length == 0 ) throw new Exception( "both rooms must have visleaves" );
			return (a, b);
		}

		// Fixture areaportals: `areaportal <name> <cx cy cz> <open|closed> <control>`.
		// The centre resolves to the nearest vis-to-vis portal.
		List<Areaportal> LoadAreaportals( BspTree tree )
		{
			var list = new List<Areaportal>();
			foreach ( var raw in File.ReadLines( Fixture( "areaportals.txt" ) ) )
			{
				var line = raw.Trim();
				if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
				var parts = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
				if ( parts[0] != "areaportal" || parts.Length != 7 )
					throw new Exception( $"areaportals.txt: bad line: '{line}'" );
				float Num( int i ) => float.Parse( parts[i], CultureInfo.InvariantCulture );
				var centre = new Vec3( Num( 2 ), Num( 3 ), Num( 4 ) );
				var state = parts[5] == "closed" ? AreaportalState.Closed
					: parts[5] == "open" ? AreaportalState.Open
					: throw new Exception( $"areaportals.txt: bad state '{parts[5]}'" );
				BspPortal? best = null;
				var bestDist = float.MaxValue;
				foreach ( var portal in tree.Portals )
				{
					if ( !tree.Leaves[portal.LeafA].IsVisLeaf || !tree.Leaves[portal.LeafB].IsVisLeaf ) continue;
					var d = (portal.Center - centre).Length;
					if ( d < bestDist ) { bestDist = d; best = portal; }
				}
				if ( best is null || bestDist > 1f )
					throw new Exception( $"areaportals.txt: no doorway portal near {centre} (nearest {bestDist:0.###})" );
				list.Add( Areaportal.ForPortal( best, parts[1], state, parts[6] ) );
			}
			if ( list.Count == 0 ) throw new Exception( "areaportals.txt: no areaportals parsed" );
			return list;
		}

		// Fixture occluders: `occluder <name> <cx cy cz> <nx ny nz> <width> <height>`.
		List<Occluder> LoadOccluders()
		{
			var list = new List<Occluder>();
			foreach ( var raw in File.ReadLines( Fixture( "occluders.txt" ) ) )
			{
				var line = raw.Trim();
				if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
				var parts = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
				if ( parts[0] != "occluder" || parts.Length != 10 )
					throw new Exception( $"occluders.txt: bad line: '{line}'" );
				float Num( int i ) => float.Parse( parts[i], CultureInfo.InvariantCulture );
				list.Add( Occluder.Quad( parts[1],
					new Vec3( Num( 2 ), Num( 3 ), Num( 4 ) ),
					new Vec3( Num( 5 ), Num( 6 ), Num( 7 ) ),
					Num( 8 ), Num( 9 ) ) );
			}
			if ( list.Count == 0 ) throw new Exception( "occluders.txt: no occluders parsed" );
			return list;
		}

		MapEntities EntitiesWithDoor()
		{
			var entities = new MapEntities( EntityRegistry.Load(
				Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) );
			entities.Tie( "func_door", new[] { new TestBrush( "door-slab" ) },
				new Dictionary<string, string> { ["targetname"] = "door_01" } );
			entities.PlacePointEntity( "logic_relay", new Vec3( 0, 0, 0 ),
				new Dictionary<string, string> { ["targetname"] = "relay_01" } );
			entities.PlacePointEntity( "light", new Vec3( 0, 0, 0 ),
				new Dictionary<string, string> { ["targetname"] = "lamp_01" } );
			return entities;
		}

		Check( "closed areaportal removes the room behind it from the PVS", () =>
		{
			var tree = TwoRoomTree();
			var (a, b) = RoomLeaves( tree );
			// Baseline: the rooms see each other through the open doorway.
			foreach ( var i in a )
				foreach ( var j in b )
					Require( tree.CanSee( i, j ), $"room A leaf {i} cannot see room B leaf {j} with no areaportal" );
			var areaportals = LoadAreaportals( tree );
			Require( areaportals.Count == 1 && areaportals[0].IsClosed, "fixture areaportal must be the closed door-01" );
			var pvs = AreaportalVisibility.ComputePvs( tree, areaportals );
			foreach ( var i in a )
				foreach ( var j in b )
					Require( !pvs[i][j], $"room A leaf {i} still sees room B leaf {j} through a closed areaportal" );
			foreach ( var j in b )
				foreach ( var i in a )
					Require( !pvs[j][i], $"room B leaf {j} still sees room A leaf {i} through a closed areaportal" );
			// Each room still sees itself.
			foreach ( var i in a )
				foreach ( var j in a )
					Require( pvs[i][j], $"room A leaf {i} lost sight of room A leaf {j} — a closed door must not blind its own room" );
			foreach ( var i in b )
				foreach ( var j in b )
					Require( pvs[i][j], $"room B leaf {i} lost sight of room B leaf {j}" );
		} );

		Check( "open areaportal keeps both rooms mutually visible", () =>
		{
			var tree = TwoRoomTree();
			var (a, b) = RoomLeaves( tree );
			var doorway = DoorwayPortal( tree );
			var open = Areaportal.ForPortal( doorway, "door-01", AreaportalState.Open, "door_01" );
			var pvs = AreaportalVisibility.ComputePvs( tree, new[] { open } );
			foreach ( var i in a )
				foreach ( var j in b )
					Require( pvs[i][j], $"room A leaf {i} cannot see room B leaf {j} through an open areaportal" );
			// And the open-areaportal PVS equals the tree's own PVS exactly.
			for ( int i = 0; i < tree.VisLeafCount; i++ )
				for ( int j = 0; j < tree.VisLeafCount; j++ )
					Require( pvs[i][j] == tree.CanSee( i, j ),
						$"open areaportal changed visibility {i}->{j}: pvs {pvs[i][j]}, base {tree.CanSee( i, j )}" );
		} );

		Check( "toggling an areaportal closed then open flips the PVS", () =>
		{
			var tree = TwoRoomTree();
			var (a, b) = RoomLeaves( tree );
			var ap = Areaportal.ForPortal( DoorwayPortal( tree ), "door-01", AreaportalState.Open, "door_01" );
			var list = new List<Areaportal> { ap };
			Require( AreaportalVisibility.CanSee( tree, list, a[0], b[0] ), "open door must let room A see room B" );
			ap.State = AreaportalState.Closed;
			Require( !AreaportalVisibility.CanSee( tree, list, a[0], b[0] ), "closing the door must cut room B from the PVS" );
			Require( AreaportalVisibility.CanSee( tree, list, a[0], a[0] ), "a leaf must still see itself with the door closed" );
			ap.State = AreaportalState.Open;
			Require( AreaportalVisibility.CanSee( tree, list, a[0], b[0] ), "reopening the door must restore room B to the PVS" );
			Require( AreaportalVisibility.VisibleVisLeaves( tree, list, a[0] ).Contains( b[0] ),
				"VisibleVisLeaves must list room B once the door is open again" );
		} );

		Check( "areaportal on an unknown leaf pair is ignored by the PVS (validation flags it)", () =>
		{
			var tree = TwoRoomTree();
			var (a, b) = RoomLeaves( tree );
			var ghost = new Areaportal( "ghost", 100000, 100001, AreaportalState.Closed, "door_01" );
			var pvs = AreaportalVisibility.ComputePvs( tree, new[] { ghost } );
			for ( int i = 0; i < tree.VisLeafCount; i++ )
				for ( int j = 0; j < tree.VisLeafCount; j++ )
					Require( pvs[i][j] == tree.CanSee( i, j ),
						$"ghost areaportal changed visibility {i}->{j}" );
			Require( !AreaportalVisibility.IsPortalClosed( DoorwayPortal( tree ), new[] { ghost } ),
				"a ghost areaportal must not report the real doorway closed" );
			var issues = AreaportalOccluderValidator.Validate( tree, EntitiesWithDoor(), new[] { ghost }, null );
			Require( issues.Any( x => x.Code == AreaportalOccluderIssue.CodeAreaportalNoPortal ),
				$"ghost areaportal must be flagged NoPortal, got: {string.Join( "; ", issues )}" );
		} );

		Check( "occluder hides a prop directly behind it", () =>
		{
			var occluders = LoadOccluders();
			var curtain = occluders.First( o => o.Name == "curtain-b" );
			Require( MathF.Abs( curtain.Area - 128 * 128 ) < 1f, $"curtain area {curtain.Area}, want 16384" );
			Require( curtain.Center.AlmostEquals( new Vec3( 128, 0, 64 ) ), $"curtain centre {curtain.Center}, want (128, 0, 64)" );
			var viewer = new Vec3( -128, 0, 64 );
			Require( curtain.Hides( viewer, new Vec3( 200, 0, 64 ) ), "curtain must hide a prop dead behind it" );
			var result = OccluderCull.CullProps( viewer,
				new[] { new PropInstance( "behind", new Vec3( 200, 0, 64 ) ) }, occluders );
			Require( result.Culled.Count == 1 && result.Visible.Count == 0,
				$"prop behind the curtain must be culled, got visible {result.Visible.Count} culled {result.Culled.Count}" );
		} );

		Check( "occluder does not hide a prop in front of it or off to the side", () =>
		{
			var occluders = LoadOccluders();
			var curtain = occluders.First( o => o.Name == "curtain-b" );
			var viewer = new Vec3( -128, 0, 64 );
			Require( !curtain.Hides( viewer, new Vec3( 64, 0, 64 ) ), "curtain must not hide a prop in front of it (room B, this side)" );
			Require( !curtain.Hides( viewer, new Vec3( 200, 100, 64 ) ), "curtain must not hide a prop past its side edge (y=100, edge at y=64)" );
			Require( !curtain.Hides( new Vec3( 200, 0, 64 ), new Vec3( 200, 100, 64 ) ),
				"viewed from behind the curtain, a prop on the viewer's own side (same x) must stay visible" );
			var result = OccluderCull.CullProps( viewer,
				new[]
				{
					new PropInstance( "front", new Vec3( 64, 0, 64 ) ),
					new PropInstance( "side", new Vec3( 200, 100, 64 ) ),
					new PropInstance( "same-room", new Vec3( -200, 0, 64 ) ),
				}, occluders );
			Require( result.Visible.Count == 3 && result.Culled.Count == 0,
				$"all three props must stay visible, got visible {result.Visible.Count} culled {result.Culled.Count}" );
		} );

		Check( "a fat prop peeking past the occluder edge is not culled, a small one behind it is", () =>
		{
			var curtain = LoadOccluders().First( o => o.Name == "curtain-b" );
			var viewer = new Vec3( -128, 0, 64 );
			// Dead centre, radius 30: projected disc (~23.5) fits inside the 128x128 quad.
			Require( curtain.Hides( viewer, new Vec3( 200, 0, 64 ), 30f ), "small prop dead behind the curtain must be culled" );
			// Near the side edge (hit y ~42.9, edge at y=64): radius 30 projects to
			// ~23.5, which spills past the edge — the prop peeks out, so it stays.
			Require( !curtain.Hides( viewer, new Vec3( 200, 55, 64 ), 30f ),
				"prop near the curtain edge with radius 30 must NOT be culled (it peeks past the edge)" );
			// Same spot, tiny radius: the disc fits, so it is culled.
			Require( curtain.Hides( viewer, new Vec3( 200, 55, 64 ), 5f ),
				"tiny prop near the curtain edge must be culled (its disc fits inside)" );
			// A prop so fat the curtain sits inside its near surface is not hidden.
			Require( !curtain.Hides( viewer, new Vec3( 200, 0, 64 ), 100f ),
				"prop with radius 100 (curtain inside its near surface) must not be culled" );
		} );

		Check( "occluder construction rejects degenerate, non-planar and zero-area polygons with named codes", () =>
		{
			var two = ThrowsOccluder( () => Occluder.FromPolygon( "two", new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ) } ) );
			Require( two.Code == AreaportalOccluderException.CodeInvalidOccluderPolygon, $"two-point code is {two.Code}" );
			var collinear = ThrowsOccluder( () => Occluder.FromPolygon( "line",
				new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 2, 0, 0 ) } ) );
			Require( collinear.Code == AreaportalOccluderException.CodeInvalidOccluderPolygon, $"collinear code is {collinear.Code}" );
			var bent = ThrowsOccluder( () => Occluder.FromPolygon( "bent",
				new[] { new Vec3( 0, 0, 0 ), new Vec3( 128, 0, 0 ), new Vec3( 128, 128, 0 ), new Vec3( 0, 128, 1 ) } ) );
			Require( bent.Code == AreaportalOccluderException.CodeInvalidOccluderPolygon, $"non-planar code is {bent.Code}" );
			var flat = ThrowsOccluder( () => Occluder.Quad( "flat", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0, 128 ) );
			Require( flat.Code == AreaportalOccluderException.CodeInvalidOccluderPolygon, $"zero-width code is {flat.Code}" );
			var zeroNormal = ThrowsOccluder( () => Occluder.Quad( "nonorm", new Vec3( 0, 0, 0 ), Vec3.Zero, 128, 128 ) );
			Require( zeroNormal.Code == AreaportalOccluderException.CodeInvalidOccluderPolygon, $"zero-normal code is {zeroNormal.Code}" );
			try { Occluder.FromPolygon( "null", null! ); throw new Exception( "null polygon must throw" ); }
			catch ( ArgumentNullException ) { }
			var negRadius = false;
			try { Occluder.Quad( "q", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 128, 128 ).Hides( Vec3.Zero, new Vec3( 1, 0, 0 ), -1f ); }
			catch ( ArgumentOutOfRangeException ) { negRadius = true; }
			Require( negRadius, "negative prop radius must be rejected" );
		} );

		Check( "areaportal validation: no control is an error, missing control is an error, door control is clean, light control is a warning", () =>
		{
			var tree = TwoRoomTree();
			var doorway = DoorwayPortal( tree );
			var entities = EntitiesWithDoor();
			var noControl = Areaportal.ForPortal( doorway, "no-control", AreaportalState.Closed, "" );
			var issues = AreaportalOccluderValidator.Validate( tree, entities, new[] { noControl }, null );
			Require( issues.Any( x => x.Code == AreaportalOccluderIssue.CodeAreaportalNoControl && x.Severity == AreaportalOccluderIssueSeverity.Error ),
				$"areaportal with no control must be a NoControl error, got: {string.Join( "; ", issues )}" );
			var missing = Areaportal.ForPortal( doorway, "missing", AreaportalState.Closed, "ghost_door" );
			issues = AreaportalOccluderValidator.Validate( tree, entities, new[] { missing }, null );
			Require( issues.Any( x => x.Code == AreaportalOccluderIssue.CodeAreaportalControlMissing && x.Severity == AreaportalOccluderIssueSeverity.Error ),
				$"areaportal naming a missing control must be a ControlMissing error, got: {string.Join( "; ", issues )}" );
			var door = Areaportal.ForPortal( doorway, "door", AreaportalState.Closed, "door_01" );
			issues = AreaportalOccluderValidator.Validate( tree, entities, new[] { door }, null );
			Require( issues.Count == 0, $"door-controlled areaportal must be clean, got: {string.Join( "; ", issues )}" );
			var relay = Areaportal.ForPortal( doorway, "relay", AreaportalState.Closed, "relay_01" );
			issues = AreaportalOccluderValidator.Validate( tree, entities, new[] { relay }, null );
			Require( issues.Count == 0, $"logic_relay control must be clean, got: {string.Join( "; ", issues )}" );
			var lamp = Areaportal.ForPortal( doorway, "lamp", AreaportalState.Closed, "lamp_01" );
			issues = AreaportalOccluderValidator.Validate( tree, entities, new[] { lamp }, null );
			Require( issues.Any( x => x.Code == AreaportalOccluderIssue.CodeAreaportalControlNotOpener && x.Severity == AreaportalOccluderIssueSeverity.Warning ),
				$"light control must be a NotOpener warning, got: {string.Join( "; ", issues )}" );
			Require( !issues.Any( x => x.Severity == AreaportalOccluderIssueSeverity.Error ),
				"a light control is a warning, not an error" );
		} );

		Check( "occluder validation: too small and outside the map are errors, a good occluder is clean", () =>
		{
			var tree = TwoRoomTree();
			var occluders = LoadOccluders();
			var curtain = occluders.First( o => o.Name == "curtain-b" );
			var stamp = occluders.First( o => o.Name == "postage-stamp" );
			Require( stamp.Area < Occluder.MinOccluderArea, $"stamp area {stamp.Area} must be below the {Occluder.MinOccluderArea} minimum" );
			var issues = AreaportalOccluderValidator.Validate( tree, null, null, new[] { curtain } );
			Require( issues.Count == 0, $"good curtain must be clean, got: {string.Join( "; ", issues )}" );
			issues = AreaportalOccluderValidator.Validate( tree, null, null, new[] { stamp } );
			Require( issues.Any( x => x.Code == AreaportalOccluderIssue.CodeOccluderTooSmall && x.Severity == AreaportalOccluderIssueSeverity.Error ),
				$"16x16 stamp must be a TooSmall error, got: {string.Join( "; ", issues )}" );
			var aboveRoof = Occluder.Quad( "above-roof", new Vec3( 0, 0, 500 ), new Vec3( 0, 0, -1 ), 128, 128 );
			issues = AreaportalOccluderValidator.Validate( tree, null, null, new[] { aboveRoof } );
			Require( issues.Any( x => x.Code == AreaportalOccluderIssue.CodeOccluderOutsideMap && x.Severity == AreaportalOccluderIssueSeverity.Error ),
				$"occluder above the roof must be an OutsideMap error, got: {string.Join( "; ", issues )}" );
			var inWall = Occluder.Quad( "in-wall", new Vec3( 0, -100, 64 ), new Vec3( 1, 0, 0 ), 128, 128 );
			issues = AreaportalOccluderValidator.Validate( tree, null, null, new[] { inWall } );
			Require( issues.Any( x => x.Code == AreaportalOccluderIssue.CodeOccluderOutsideMap ),
				$"occluder centred inside the shared wall must be an OutsideMap error, got: {string.Join( "; ", issues )}" );
		} );

		Check( "areaportal validation: duplicate gate and unknown portal are errors", () =>
		{
			var tree = TwoRoomTree();
			var doorway = DoorwayPortal( tree );
			var first = Areaportal.ForPortal( doorway, "door-a", AreaportalState.Closed, "door_01" );
			var second = Areaportal.ForPortal( doorway, "door-b", AreaportalState.Closed, "door_01" );
			var issues = AreaportalOccluderValidator.Validate( tree, EntitiesWithDoor(), new[] { first, second }, null );
			Require( issues.Any( x => x.Code == AreaportalOccluderIssue.CodeAreaportalDuplicatePortal && x.Severity == AreaportalOccluderIssueSeverity.Error ),
				$"two areaportals on one portal must be a DuplicatePortal error, got: {string.Join( "; ", issues )}" );
			Require( issues.Count( x => x.Code == AreaportalOccluderIssue.CodeAreaportalDuplicatePortal ) == 1,
				"exactly one duplicate finding (on the second areaportal)" );
		} );

		Check( "VisibleProps composes closed areaportal and occluder culling", () =>
		{
			var tree = TwoRoomTree();
			var occluders = LoadOccluders();
			var viewer = new Vec3( -128, 0, 64 );
			var props = new[]
			{
				new PropInstance( "same-room", new Vec3( -200, 0, 64 ) ),
				new PropInstance( "room-b-near", new Vec3( 64, 0, 64 ) ),
				new PropInstance( "room-b-far", new Vec3( 200, 0, 64 ) ),
				new PropInstance( "room-b-side", new Vec3( 200, 100, 64 ) ),
				new PropInstance( "in-wall", new Vec3( 0, -100, 64 ) ),
			};
			// Door closed: nothing in room B is visible at all.
			var closed = LoadAreaportals( tree );
			Require( closed[0].IsClosed, "fixture areaportal must start closed" );
			var seen = OccluderCull.VisibleProps( viewer, tree, closed, props, occluders );
			Require( seen.Select( p => p.Name ).SequenceEqual( new[] { "same-room" } ),
				$"closed door must leave only same-room visible, got: {string.Join( ",", seen.Select( p => p.Name ) )}" );
			// Door open: near + side props in room B appear; far stays behind the curtain.
			closed[0].State = AreaportalState.Open;
			seen = OccluderCull.VisibleProps( viewer, tree, closed, props, occluders );
			Require( seen.Select( p => p.Name ).SequenceEqual( new[] { "same-room", "room-b-near", "room-b-side" } ),
				$"open door must show near + side but not far (curtain) or in-wall, got: {string.Join( ",", seen.Select( p => p.Name ) )}" );
			// No occluders, door open: far appears too (in-wall never does).
			seen = OccluderCull.VisibleProps( viewer, tree, closed, props, null );
			Require( seen.Select( p => p.Name ).SequenceEqual( new[] { "same-room", "room-b-near", "room-b-far", "room-b-side" } ),
				$"open door without occluders must show all four real props, got: {string.Join( ",", seen.Select( p => p.Name ) )}" );
			// Viewer in no visleaf (above the map) sees nothing.
			seen = OccluderCull.VisibleProps( new Vec3( 0, 0, 500 ), tree, closed, props, occluders );
			Require( seen.Count == 0, "a viewer outside the map must see no props" );
		} );

		Check( "areaportal PVS is deterministic and equals the base PVS when all areaportals are open", () =>
		{
			var tree = TwoRoomTree();
			var doorway = DoorwayPortal( tree );
			var open = new[] { Areaportal.ForPortal( doorway, "door-01", AreaportalState.Open, "door_01" ) };
			var first = AreaportalVisibility.ComputePvs( tree, open );
			var second = AreaportalVisibility.ComputePvs( tree, open );
			for ( int i = 0; i < tree.VisLeafCount; i++ )
				for ( int j = 0; j < tree.VisLeafCount; j++ )
				{
					Require( first[i][j] == second[i][j], $"PVS not deterministic at {i}->{j}" );
					Require( first[i][j] == tree.CanSee( i, j ), $"open areaportal changed visibility {i}->{j}" );
				}
			// Null areaportal list also equals the base PVS.
			var none = AreaportalVisibility.ComputePvs( tree, null );
			for ( int i = 0; i < tree.VisLeafCount; i++ )
				for ( int j = 0; j < tree.VisLeafCount; j++ )
					Require( none[i][j] == tree.CanSee( i, j ), $"null areaportal list changed visibility {i}->{j}" );
		} );

		Check( "invalid arguments are rejected with named exceptions", () =>
		{
			var tree = TwoRoomTree();
			var doorway = DoorwayPortal( tree );
			var ap = Areaportal.ForPortal( doorway, "door-01", AreaportalState.Open, "door_01" );
			var list = new[] { ap };
			try { AreaportalVisibility.ComputePvs( null!, list ); throw new Exception( "null tree must throw" ); }
			catch ( ArgumentNullException ) { }
			try { AreaportalVisibility.CanSee( tree, list, -1, 0 ); throw new Exception( "negative from index must throw" ); }
			catch ( ArgumentOutOfRangeException ) { }
			try { AreaportalVisibility.CanSee( tree, list, 0, tree.VisLeafCount ); throw new Exception( "out-of-range to index must throw" ); }
			catch ( ArgumentOutOfRangeException ) { }
			try { AreaportalVisibility.VisibleVisLeaves( tree, list, tree.VisLeafCount ); throw new Exception( "out-of-range leaf must throw" ); }
			catch ( ArgumentOutOfRangeException ) { }
			var sameLeaf = ThrowsOccluder( () => new Areaportal( "same", 3, 3 ) );
			Require( sameLeaf.Code == AreaportalOccluderException.CodeInvalidAreaportal, $"same-leaf code is {sameLeaf.Code}" );
			var negLeaf = ThrowsOccluder( () => new Areaportal( "neg", -1, 2 ) );
			Require( negLeaf.Code == AreaportalOccluderException.CodeInvalidAreaportal, $"negative-leaf code is {negLeaf.Code}" );
			var noName = ThrowsOccluder( () => new Areaportal( "", 1, 2 ) );
			Require( noName.Code == AreaportalOccluderException.CodeInvalidAreaportal, $"nameless code is {noName.Code}" );
			try { OccluderCull.CullProps( Vec3.Zero, null!, null ); throw new Exception( "null props must throw" ); }
			catch ( ArgumentNullException ) { }
			try { OccluderCull.VisibleProps( Vec3.Zero, null!, null, Array.Empty<PropInstance>(), null ); throw new Exception( "null tree must throw" ); }
			catch ( ArgumentNullException ) { }
			try { new PropInstance( "x", new Vec3( 0, 0, 0 ), -5f ); throw new Exception( "negative prop radius must throw" ); }
			catch ( ArgumentOutOfRangeException ) { }
		} );
	}
}
