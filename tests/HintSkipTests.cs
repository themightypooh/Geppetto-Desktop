using MapBrushFace = Effigy.MapBrushFace;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

/// <summary>
/// Hint/skip brushes as a tool (hammer-optimisation-hint-skip-brushes).
///
/// Fixtures are the source of truth for the hint brushes: plain-text brush lists
/// under tests/fixtures/hammer/hammer-optimisation-hint-skip-brushes/ — one
/// `brush &lt;name&gt;` line, then one `face nx ny nz d &lt;material&gt;` line per face
/// (outward unit normal, plane Dot(n, p) == d, solid side Dot(n, p) &lt;= d, material
/// is a full material name such as tools/toolshint). The parser below is the only
/// reader; if a fixture fails to parse, the tests fail, so a stale fixture cannot
/// silently pass.
///
/// The rooms come from the BSP ticket's fixtures (read-only,
/// hammer-bsp-core-tree-portals-leaves): sealed-room.txt is one sealed room
/// (1 visleaf), two-room-doorway.txt is two rooms joined by a doorway gap
/// (y in [-32, 32], z in [0, 96]) through the shared wall at x in [-8, 8].
/// </summary>
internal static class HintSkipTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "hintskip", name, test );
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

		static string FixtureDir( string leaf )
		{
			var rel = Path.Combine( "tests", "fixtures", "hammer", leaf );
			if ( Directory.Exists( rel ) ) return rel;
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			while ( dir != null )
			{
				var candidate = Path.Combine( dir.FullName, rel );
				if ( Directory.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			throw new Exception( "hintskip fixture dir not found from " + Directory.GetCurrentDirectory() );
		}

		static string HintDir() => FixtureDir( "hammer-optimisation-hint-skip-brushes" );
		static string BspDir() => FixtureDir( "hammer-bsp-core-tree-portals-leaves" );

		static List<BspBrush> LoadRoom( string name )
		{
			var path = Path.Combine( BspDir(), name );
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

		static HintBrush LoadHint( string name )
		{
			var path = Path.Combine( HintDir(), name );
			var faces = new List<MapBrushFace>();
			var brushName = "";
			foreach ( var raw in File.ReadLines( path ) )
			{
				var line = raw.Trim();
				if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
				var parts = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
				if ( parts[0] == "brush" )
				{
					if ( parts.Length < 2 ) throw new Exception( $"{name}: brush line needs a name: '{line}'" );
					brushName = parts[1];
				}
				else if ( parts[0] == "face" )
				{
					if ( parts.Length != 6 ) throw new Exception( $"{name}: face line needs nx ny nz d material: '{line}'" );
					float Num( int i ) => float.Parse( parts[i], CultureInfo.InvariantCulture );
					faces.Add( new MapBrushFace( new Vec3( Num( 1 ), Num( 2 ), Num( 3 ) ), Num( 4 ), parts[5] ) );
				}
				else throw new Exception( $"{name}: unknown line: '{line}'" );
			}
			if ( faces.Count == 0 ) throw new Exception( $"{name}: no faces parsed" );
			return HintBrush.FromMapBrush( MapBrush.FromPlanes( faces ), brushName );
		}

		Check( "fixture hint slab classifies one hint face and five skip faces", () =>
		{
			var hint = LoadHint( "doorway-hint.txt" );
			Require( hint.FaceCount == 6, $"faces {hint.FaceCount}, want 6" );
			Require( hint.HintFaceCount == 1, $"hint faces {hint.HintFaceCount}, want 1" );
			Require( hint.SkipFaceCount == 5, $"skip faces {hint.SkipFaceCount}, want 5" );
			Require( hint.OrdinaryFaceIndices.Count == 0, "a hint slab has no ordinary faces" );
			Require( hint.HintFaceIndices.SequenceEqual( new[] { 1 } ),
				$"hint faces at {string.Join( ",", hint.HintFaceIndices )}, want face 1 (+X)" );
			Require( hint.HasHintFace && !hint.IsAllSkip, "a hint brush has a hint face and is not all-skip" );
			Require( hint.HintPlanes.Count == 1, $"hint planes {hint.HintPlanes.Count}, want 1" );
			Require( hint.HintPlanes[0].Equals( new BspPlane( new Vec3( 1, 0, 0 ), 2 ) ),
				$"hint plane {hint.HintPlanes[0]}, want n(1,0,0) d2 (the doorway plane x = 2)" );
		} );

		Check( "a hint brush with a hint face validates clean", () =>
		{
			var hint = LoadHint( "doorway-hint.txt" );
			Require( hint.IsValid, string.Join( "; ", hint.Validate().Select( d => d.ToString() ) ) );
			Require( hint.Validate().Count == 0, "a hint brush with a hint face must have no defects" );
		} );

		Check( "an all-skip brush fails validation with NoHintFace and nothing else", () =>
		{
			var hint = LoadHint( "doorway-hint-allskip.txt" );
			Require( hint.IsAllSkip, "the all-skip fixture must be all skip faces" );
			Require( hint.HintFaceCount == 0 && hint.HintPlanes.Count == 0,
				"an all-skip brush contributes no hint planes" );
			Require( !hint.IsValid, "an all-skip brush is not a valid hint brush" );
			var defects = hint.Validate();
			Require( defects.Count == 1, $"defects: {string.Join( "; ", defects.Select( d => d.ToString() ) )}" );
			Require( defects[0].Code == HintBrushDefect.NoHintFace,
				$"defect code {defects[0].Code}, want NoHintFace" );
		} );

		Check( "a brush with no tool faces fails validation with NoHintFace", () =>
		{
			var brick = HintBrush.FromMapBrush(
				MapBrush.Box( new Vec3( -2, -32, 0 ), new Vec3( 2, 32, 96 ), "brick/brickwall001" ), "brick-slab" );
			Require( brick.HintFaceCount == 0 && brick.SkipFaceCount == 0,
				"a brick brush has neither hint nor skip faces" );
			Require( brick.OrdinaryFaceIndices.Count == 6, "all six faces are ordinary" );
			Require( brick.Validate().Any( d => d.Code == HintBrushDefect.NoHintFace ),
				$"defects: {string.Join( "; ", brick.Validate().Select( d => d.ToString() ) )}" );
		} );

		Check( "HintBrush.Box marks the chosen faces hint and the rest skip", () =>
		{
			var hint = HintBrush.Box( new Vec3( -2, -32, 0 ), new Vec3( 2, 32, 96 ), new[] { 1 }, "box-hint" );
			Require( hint.HintFaceIndices.SequenceEqual( new[] { 1 } ),
				$"hint faces at {string.Join( ",", hint.HintFaceIndices )}, want face 1 (+X)" );
			Require( hint.SkipFaceCount == 5, $"skip faces {hint.SkipFaceCount}, want 5" );
			Require( hint.IsValid, string.Join( "; ", hint.Validate().Select( d => d.ToString() ) ) );
			Require( hint.HintPlanes[0].Equals( new BspPlane( new Vec3( 1, 0, 0 ), 2 ) ),
				$"hint plane {hint.HintPlanes[0]}, want n(1,0,0) d2" );
			var allSkip = HintBrush.Box( new Vec3( -2, -32, 0 ), new Vec3( 2, 32, 96 ), Array.Empty<int>(), "box-skip" );
			Require( allSkip.IsAllSkip && !allSkip.IsValid, "a box with no hint faces is all-skip and invalid as a hint brush" );
			ThrowsNamed( () => HintBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 1, 1, 1 ), new[] { 6 }, "bad" ), "out of range" );
		} );

		Check( "hint material lookup is case-insensitive and tolerates a materials prefix", () =>
		{
			var faces = new[]
			{
				new MapBrushFace( new Vec3( -1, 0, 0 ), 2, "Tools/ToolsHint" ),
				new MapBrushFace( new Vec3( 1, 0, 0 ), 2, "materials/tools/toolsskip" ),
				new MapBrushFace( new Vec3( 0, -1, 0 ), 32, "tools/toolsskip" ),
				new MapBrushFace( new Vec3( 0, 1, 0 ), 32, "tools/toolsskip" ),
				new MapBrushFace( new Vec3( 0, 0, -1 ), 0, "tools/toolsskip" ),
				new MapBrushFace( new Vec3( 0, 0, 1 ), 96, "tools/toolsskip" ),
			};
			var hint = HintBrush.FromMapBrush( MapBrush.FromPlanes( faces ), "case-hint" );
			Require( hint.HintFaceCount == 1 && hint.HintFaceIndices[0] == 0,
				$"hint faces at {string.Join( ",", hint.HintFaceIndices )}, want face 0" );
			Require( hint.IsValid, "case-insensitive hint classification must validate" );
			Require( HintBrush.Classify( "tools/toolshint" ) == HintFaceRole.Hint, "toolshint classifies Hint" );
			Require( HintBrush.Classify( "tools/toolsskip" ) == HintFaceRole.Skip, "toolsskip classifies Skip" );
			Require( HintBrush.Classify( "brick/brickwall001" ) == HintFaceRole.Ordinary, "brick classifies Ordinary" );
			Require( HintBrush.Classify( "tools/toolsnodraw" ) == HintFaceRole.Ordinary, "nodraw is not a hint or skip face" );
		} );

		Check( "a hint brush across a doorway splits the leaf along its plane", () =>
		{
			var room = LoadRoom( "two-room-doorway.txt" );
			var baseline = BspBuilder.Build( room );
			var hint = LoadHint( "doorway-hint.txt" );
			var tree = HintSkip.Build( room, new[] { hint } );
			Require( tree.IsSealed, "the hinted map must stay sealed" );
			Require( tree.VisLeafCount > baseline.VisLeafCount,
				$"visleaves {tree.VisLeafCount} with the hint, want more than baseline {baseline.VisLeafCount}" );
			// The doorway throat (x in [0, 8], y in [-32, 32], z in [0, 96]) is split
			// at x = 2: the two probe points sit either side of the hint plane.
			var west = tree.VisLeafAt( new Vec3( 1, 0, 48 ) );
			var east = tree.VisLeafAt( new Vec3( 4, 0, 48 ) );
			Require( west >= 0 && east >= 0, $"throat points must be in visleaves, got {west} and {east}" );
			Require( west != east, "the two sides of the hint plane must be different visleaves" );
			Require( baseline.VisLeafAt( new Vec3( 1, 0, 48 ) ) == baseline.VisLeafAt( new Vec3( 4, 0, 48 ) ),
				"without the hint the two throat points share one leaf" );
			// The split is a real portal: the two new leaves share a 64x96 face on x = 2.
			var split = tree.Portals.Where( p =>
				tree.Leaves[p.LeafA].IsVisLeaf && tree.Leaves[p.LeafB].IsVisLeaf
				&& MathF.Abs( p.Plane.Normal.x ) > 0.999f && MathF.Abs( p.Center.x - 2 ) < 0.01f ).ToList();
			Require( split.Count == 1, $"x = 2 split portals {split.Count}, want 1\n{tree.Describe()}" );
			Require( MathF.Abs( split[0].Area - 64 * 96 ) < 61.44f, $"split portal area {split[0].Area}, want 6144 (64x96)" );
			Require( MathF.Abs( split[0].Center.y ) < 0.5f && MathF.Abs( split[0].Center.z - 48 ) < 0.5f,
				$"split portal centre {split[0].Center}, want (2, 0, 48)" );
			// The hint brush is not solid: both sides of its volume stay visible void.
			Require( tree.VisLeafAt( new Vec3( -128, 0, 32 ) ) >= 0, "room A must stay a visleaf" );
			Require( tree.VisLeafAt( new Vec3( 128, 0, 32 ) ) >= 0, "room B must stay a visleaf" );
		} );

		Check( "an all-skip brush across a doorway changes nothing", () =>
		{
			var room = LoadRoom( "two-room-doorway.txt" );
			var baseline = BspBuilder.Build( room ).Describe();
			var hint = LoadHint( "doorway-hint-allskip.txt" );
			var tree = HintSkip.Build( room, new[] { hint } );
			Require( tree.Describe() == baseline, "an all-skip brush must build the identical tree" );
		} );

		Check( "a hint brush splits a sealed room into exactly two visleaves", () =>
		{
			var room = LoadRoom( "sealed-room.txt" );
			var baseline = BspBuilder.Build( room );
			Require( baseline.VisLeafCount == 1, $"baseline visleaves {baseline.VisLeafCount}, want 1" );
			var hint = LoadHint( "room-hint.txt" );
			var tree = HintSkip.Build( room, new[] { hint } );
			Require( tree.VisLeafCount == 2, $"visleaves {tree.VisLeafCount} with the hint, want 2\n{tree.Describe()}" );
			Require( tree.IsSealed, "the hinted room must stay sealed" );
			var west = tree.VisLeafAt( new Vec3( -64, 0, 64 ) );
			var east = tree.VisLeafAt( new Vec3( 64, 0, 64 ) );
			Require( west >= 0 && east >= 0 && west != east,
				$"room points must be in different visleaves, got {west} and {east}" );
		} );

		Check( "skip faces cut nothing: no split across the skip planes", () =>
		{
			var room = LoadRoom( "sealed-room.txt" );
			var hint = LoadHint( "room-hint.txt" );
			var tree = HintSkip.Build( room, new[] { hint } );
			Require( tree.VisLeafCount == 2, $"visleaves {tree.VisLeafCount}, want exactly 2 (one hint cut, no skip cuts)" );
			// The slab's skip faces sit on y = -32 and y = 32: points either side of
			// each must share a leaf, or the skip face cut.
			var south = tree.VisLeafAt( new Vec3( 64, -64, 64 ) );
			var middle = tree.VisLeafAt( new Vec3( 64, 0, 64 ) );
			var north = tree.VisLeafAt( new Vec3( 64, 64, 64 ) );
			Require( south >= 0 && middle >= 0 && north >= 0, $"room points must be in visleaves, got {south}/{middle}/{north}" );
			Require( south == middle && middle == north,
				$"points across the y = +/-32 skip planes must share one leaf, got {south}/{middle}/{north}" );
			// The hint slab's own volume is not solid: a point inside it is void.
			Require( tree.VisLeafAt( new Vec3( 0, 0, 64 ) ) >= 0, "a point inside the hint brush volume must stay void" );
		} );

		Check( "an all-skip brush in a sealed room changes nothing", () =>
		{
			var room = LoadRoom( "sealed-room.txt" );
			var baseline = BspBuilder.Build( room ).Describe();
			var hint = LoadHint( "room-hint-allskip.txt" );
			var tree = HintSkip.Build( room, new[] { hint } );
			Require( tree.Describe() == baseline, "an all-skip brush must build the identical tree" );
		} );

		Check( "a hint face coincident with a structural plane adds no split", () =>
		{
			var room = LoadRoom( "sealed-room.txt" );
			var baseline = BspBuilder.Build( room ).Describe();
			// The slab spans the whole room in Y; its hint face is the +Y face on
			// y = 128, the room wall's own plane — already a split plane.
			var hint = LoadHint( "room-hint-on-wall.txt" );
			Require( hint.HintPlanes[0].Equals( new BspPlane( new Vec3( 0, 1, 0 ), 128 ) ),
				$"hint plane {hint.HintPlanes[0]}, want n(0,1,0) d128" );
			var tree = HintSkip.Build( room, new[] { hint } );
			Require( tree.Describe() == baseline,
				"a hint on an existing structural plane must build the identical tree" );
		} );

		Check( "hint build with no hints matches BspBuilder exactly", () =>
		{
			foreach ( var fixture in new[] { "sealed-room.txt", "two-room-doorway.txt" } )
			{
				var room = LoadRoom( fixture );
				var expected = BspBuilder.Build( room ).Describe();
				var actual = HintSkip.Build( room, Array.Empty<HintBrush>() ).Describe();
				Require( actual == expected, $"{fixture}: no-hint build differs from BspBuilder.Build" );
				var nullHints = HintSkip.Build( room, null ).Describe();
				Require( nullHints == expected, $"{fixture}: null hint list differs from BspBuilder.Build" );
			}
		} );

		Check( "two hint brushes on the same plane split once", () =>
		{
			var room = LoadRoom( "two-room-doorway.txt" );
			var hint = LoadHint( "doorway-hint.txt" );
			var once = HintSkip.Build( room, new[] { hint } );
			var twice = HintSkip.Build( room, new[] { hint, hint } );
			Require( twice.VisLeafCount == once.VisLeafCount,
				$"visleaves {twice.VisLeafCount} with the hint twice, want {once.VisLeafCount}" );
			Require( twice.Describe() == once.Describe(), "a duplicated hint plane must build the identical tree" );
			var planes = HintSkip.CollectHintPlanes( new[] { hint, hint } );
			Require( planes.Count == 1, $"collected hint planes {planes.Count}, want 1" );
		} );

		Check( "a hint brush from a degenerate brush is rejected with a named reason", () =>
		{
			var room = LoadRoom( "sealed-room.txt" );
			var flat = HintBrush.FromMapBrush(
				MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 0, 128, 64 ), "tools/toolshint" ), "flat-hint" );
			Require( flat.HintFaceCount == 6, "the flat box's faces are all hint" );
			ThrowsNamed( () => HintSkip.Build( room, new[] { flat } ), "ZeroVolume" );
			ThrowsNamed( () => HintSkip.CollectHintPlanes( new[] { flat } ), "ZeroVolume" );
			ThrowsNamed( () => HintSkip.Build( room, new HintBrush[] { null } ), "null hint brush" );
		} );

		Check( "hint build is deterministic", () =>
		{
			var room = LoadRoom( "two-room-doorway.txt" );
			var hint = LoadHint( "doorway-hint.txt" );
			var first = HintSkip.Build( room, new[] { hint } ).Describe();
			var second = HintSkip.Build( room, new[] { hint } ).Describe();
			Require( first == second, "same input must build the same tree" );
		} );
	}
}
