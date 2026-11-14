#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// Paste Special API (see kernel/Effigy/Map/MapPaste.cs, board
/// hammer-paste-special): PasteAtCentre, PasteWithRotation,
/// PasteWithOffset, grid snap, gizmo pivot, texture lock, and
/// valid-or-rejected-with-a-named-reason.
///
/// DEPENDENCY NOTE. MapBrush (hammer-brush-core-plane-based-brushes)
/// LANDED at 16:08, after these checks were written (namespace Effigy;
/// MapBrushFace = Vec3 Normal + Distance + FaceMaterial;
/// FromPlanes/FromVertices/FromFaceVertices/Box; Validate() returning
/// MapBrushDefect codes) — but the shared tree still does not compile
/// (MapClip.cs / MapBrushPrimitives.cs reference the old `MapFace` name,
/// not the landed MapBrushFace), and no FaceUv &lt;-&gt; MapUvMapping
/// conversion exists for the texture-lock bridge. These checks therefore
/// run against MapPasteBrush, the paste-side working copy documented in
/// MapPaste.cs, built on the LANDED MapTransform
/// (SnapToGrid/SnapAngle/RotationMatrix/ApplyTextureLock) and the LANDED
/// GizmoModel (Pivot/GridSize/AngleSnapDegrees/SnapEnabled). When the
/// final gate reconciles the tree, the MapBrush adapter checks belong
/// with it; nothing here asserts an API the core spec does not name.
///
/// The acceptance numbers live in the fixture
/// tests/fixtures/hammer/hammer-paste-special/cases.json and one check
/// below recomputes them from that file; the rest build the same cases
/// synthetically so a missing fixture can never hide a geometry failure.
/// Volumes are asserted on the solved solid; on-grid means every solved
/// vertex sits on a grid multiple.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// MapPasteTests.Register(registry, settings); line.
/// </summary>
internal static class MapPasteTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mappaste", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireReason( PasteResult result, string reason )
		{
			Require( !result.Ok, $"expected the paste to be refused with '{reason}', got success" );
			Require( result.Reason == reason,
				$"expected reason '{reason}', got '{result.Reason}': {result.Message}" );
			Require( result.Brushes.Count == 0, "a refused paste must return no brushes" );
		}

		MapPasteBrush Box( Vector3 min, Vector3 max, string material = "default" ) =>
			MapPasteBrush.FromBox( min, max, material );

		MapPasteBrush BoxMats( Vector3 min, Vector3 max, string[] materials ) =>
			MapPasteBrush.FromBox( min, max, materials );

		// Source box used by most checks: 64 x 32 x 32, corners on grid 16,
		// centroid (32, 16, 16), volume 65536.
		MapPasteBrush SourceBox() => Box( new Vector3( 0, 0, 0 ), new Vector3( 64, 32, 32 ) );

		( Vector3 Min, Vector3 Max ) Aabb( MapPasteBrush brush, double tolerance = 1e-3 )
		{
			var verts = brush.Vertices( tolerance );
			Require( verts.Count > 0, "brush has no solved vertices, so it has no bounds" );
			var min = new Vector3( float.MaxValue );
			var max = new Vector3( float.MinValue );
			foreach ( var v in verts )
			{
				min = new Vector3( MathF.Min( min.X, v.X ), MathF.Min( min.Y, v.Y ), MathF.Min( min.Z, v.Z ) );
				max = new Vector3( MathF.Max( max.X, v.X ), MathF.Max( max.Y, v.Y ), MathF.Max( max.Z, v.Z ) );
			}
			return ( min, max );
		}

		void RequireAabb( MapPasteBrush brush, Vector3 expectedMin, Vector3 expectedMax, double tolerance, string message )
		{
			var (min, max) = Aabb( brush, tolerance );
			Require( ( min - expectedMin ).Length() <= tolerance,
				$"{message}: min {min} vs expected {expectedMin}" );
			Require( ( max - expectedMax ).Length() <= tolerance,
				$"{message}: max {max} vs expected {expectedMax}" );
		}

		void RequirePlanesEqual( MapPasteBrush a, MapPasteBrush b, double tolerance, string message )
		{
			Require( a.FaceCount == b.FaceCount,
				$"{message}: face count {a.FaceCount} vs {b.FaceCount}" );
			for ( int i = 0; i < a.FaceCount; i++ )
			{
				var pa = a.Planes[i];
				var pb = b.Planes[i];
				Require( ( pa.Normal - pb.Normal ).Length() <= tolerance,
					$"{message}: face {i} normal {pa.Normal} vs {pb.Normal}" );
				Require( Math.Abs( pa.D - pb.D ) <= tolerance,
					$"{message}: face {i} distance {pa.D} vs {pb.D}" );
			}
		}

		void RequireContainsVertex( MapPasteBrush brush, Vector3 expected, double radius, double solveTolerance, string message )
		{
			foreach ( var v in brush.Vertices( solveTolerance ) )
				if ( ( v - expected ).Length() <= radius ) return;
			throw new Exception( $"{message}: no solved vertex within {radius} of {expected}" );
		}

		void RequireNoVertex( MapPasteBrush brush, Vector3 unexpected, double radius, double solveTolerance, string message )
		{
			foreach ( var v in brush.Vertices( solveTolerance ) )
				if ( ( v - unexpected ).Length() <= radius )
					throw new Exception( $"{message}: unexpected solved vertex {v} within {radius} of {unexpected}" );
		}

		string? FindFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-paste-special", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		Check( "paste-at-centre lands on the original", () =>
		{
			var source = SourceBox();
			var r = MapPaste.PasteAtCentre( new[] { source } );
			Require( r.Ok, $"paste-at-centre refused: {r.Reason} {r.Message}" );
			Require( r.Brushes.Count == 1, $"expected 1 brush, got {r.Brushes.Count}" );
			var copy = r.Brushes[0];
			RequirePlanesEqual( copy, source, 1e-3, "paste-at-centre" );
			Require( ( copy.Centroid() - source.Centroid() ).Length() <= 1e-3f,
				$"paste-at-centre centroid {copy.Centroid()} vs source {source.Centroid()}" );
			Require( Math.Abs( copy.Volume() - source.Volume() ) < 1e-3,
				$"paste-at-centre volume {copy.Volume()} vs source {source.Volume()}" );
			Require( copy.IsValid(), "paste-at-centre copy is not valid: " + string.Join( "; ", copy.Validate() ) );
			Require( copy.IsOnGrid( 16 ), "paste-at-centre copy is not on grid 16" );
		} );

		Check( "paste-at-centre preserves per-face materials", () =>
		{
			var materials = new[] { "concrete/concrete", "brick/brick", "metal/metal", "glass/glass", "wood/wood", "nodraw" };
			var source = BoxMats( new Vector3( 0, 0, 0 ), new Vector3( 64, 32, 32 ), materials );
			var r = MapPaste.PasteAtCentre( new[] { source } );
			Require( r.Ok, $"paste refused: {r.Reason} {r.Message}" );
			Require( r.Brushes[0].Materials.SequenceEqual( materials ),
				"materials did not survive the paste: " + string.Join( ",", r.Brushes[0].Materials ) );
		} );

		Check( "texture lock keeps UV samples constant across translate and rotate", () =>
		{
			var source = SourceBox();
			// Translate by exactly one grid step, texture lock on (default).
			var moved = MapPaste.PasteWithOffset( new[] { source }, new Vector3( 16, 0, 0 ) );
			Require( moved.Ok, $"offset paste refused: {moved.Reason} {moved.Message}" );
			var copy = moved.Brushes[0];
			var before = source.UvMappings[0].Sample( source.FaceCentroid( 0 ) );
			var after = copy.UvMappings[0].Sample( copy.FaceCentroid( 0 ) );
			Require( ( before - after ).Length() <= 1e-3f,
				$"texture lock moved the UV sample on translate: {before} vs {after}" );

			// Rotate 90 degrees about the selection centre, face 2 (front).
			var rotated = MapPaste.PasteWithRotation( new[] { source }, 1, 90.0 );
			Require( rotated.Ok, $"rotation paste refused: {rotated.Reason} {rotated.Message}" );
			var spun = rotated.Brushes[0];
			var beforeR = source.UvMappings[2].Sample( source.FaceCentroid( 2 ) );
			var afterR = spun.UvMappings[2].Sample( spun.FaceCentroid( 2 ) );
			Require( ( beforeR - afterR ).Length() <= 1e-3f,
				$"texture lock moved the UV sample on rotate: {beforeR} vs {afterR}" );
		} );

		Check( "texture lock off carries UV mappings unchanged", () =>
		{
			var source = SourceBox();
			var options = new PasteSpecialOptions { TextureLock = false };
			var r = MapPaste.PasteWithOffset( new[] { source }, new Vector3( 16, 0, 0 ), options );
			Require( r.Ok, $"paste refused: {r.Reason} {r.Message}" );
			var copy = r.Brushes[0];
			for ( int i = 0; i < source.FaceCount; i++ )
			{
				var a = source.UvMappings[i];
				var b = copy.UvMappings[i];
				Require( a.UAxis == b.UAxis && a.VAxis == b.VAxis && a.Offset == b.Offset
						&& a.Scale == b.Scale && a.RotationDegrees == b.RotationDegrees,
					$"face {i} UV mapping changed with texture lock off" );
			}
		} );

		Check( "rotate-90 four copies returns the last copy to its start orientation", () =>
		{
			var source = SourceBox();
			var r = MapPaste.PasteWithRotation( new[] { source }, 4, 90.0 );
			Require( r.Ok, $"rotation paste refused: {r.Reason} {r.Message}" );
			Require( r.Brushes.Count == 4, $"expected 4 copies, got {r.Brushes.Count}" );
			var last = r.Brushes[3];
			RequirePlanesEqual( last, source, 1e-3, "fourth 90-degree copy" );
			RequireAabb( last, new Vector3( 0, 0, 0 ), new Vector3( 64, 32, 32 ), 1e-3,
				"fourth 90-degree copy" );
			foreach ( var copy in r.Brushes )
			{
				Require( copy.IsValid(), "rotated copy is not valid: " + string.Join( "; ", copy.Validate() ) );
				Require( Math.Abs( copy.Volume() - source.Volume() ) < 1e-3,
					$"rotated copy volume {copy.Volume()} vs source {source.Volume()}" );
				Require( copy.IsOnGrid( 16 ), "rotated copy is not on grid 16" );
			}
		} );

		Check( "rotate-90 first copy lands at the expected rotated bounds", () =>
		{
			// Box x in [0,64], y in [0,32] rotated 90 deg about Z through
			// the selection centre (32,16,16): (x,y) -> (48-y, x-16), so
			// x in [16,48], y in [-16,48], z unchanged.
			var source = SourceBox();
			var r = MapPaste.PasteWithRotation( new[] { source }, 1, 90.0 );
			Require( r.Ok, $"rotation paste refused: {r.Reason} {r.Message}" );
			RequireAabb( r.Brushes[0], new Vector3( 16, -16, 0 ), new Vector3( 48, 48, 32 ), 1e-3,
				"first 90-degree copy" );
		} );

		Check( "explicit pivot changes the rotation centre", () =>
		{
			// Same box rotated 90 deg about the ORIGIN instead of the
			// selection centre: (x,y) -> (-y, x), so x in [-32,0],
			// y in [0,64].
			var source = SourceBox();
			var options = new PasteSpecialOptions { Pivot = Vector3.Zero };
			var r = MapPaste.PasteWithRotation( new[] { source }, 1, 90.0, options );
			Require( r.Ok, $"rotation paste refused: {r.Reason} {r.Message}" );
			Require( r.PivotUsed == Vector3.Zero, $"PivotUsed was {r.PivotUsed}, expected the origin" );
			RequireAabb( r.Brushes[0], new Vector3( -32, 0, 0 ), new Vector3( 0, 64, 32 ), 1e-3,
				"origin-pivot 90-degree copy" );
		} );

		Check( "default pivot is the selection centre", () =>
		{
			var source = SourceBox();
			var centre = MapPaste.SelectionCentre( new[] { source } );
			Require( ( centre - new Vector3( 32, 16, 16 ) ).Length() <= 1e-3f,
				$"selection centre was {centre}, expected (32, 16, 16)" );
			var r = MapPaste.PasteWithRotation( new[] { source }, 1, 90.0 );
			Require( r.Ok, $"rotation paste refused: {r.Reason} {r.Message}" );
			Require( ( r.PivotUsed - centre ).Length() <= 1e-3f,
				$"PivotUsed was {r.PivotUsed}, expected the selection centre {centre}" );
		} );

		Check( "gizmo pivot and snap settings are honoured via FromGizmo", () =>
		{
			var gizmo = new GizmoModel();
			gizmo.SetPivot( Vector3.Zero );
			gizmo.SetSnap( true, 16, 15 );
			var options = PasteSpecialOptions.FromGizmo( gizmo );
			Require( options.Pivot == Vector3.Zero, "FromGizmo did not carry the gizmo pivot" );
			Require( options.GridSize == 16, "FromGizmo did not carry the gizmo grid size" );
			Require( options.AngleSnapDegrees == 15, "FromGizmo did not carry the gizmo angle snap" );

			var source = SourceBox();
			var r = MapPaste.PasteWithRotation( new[] { source }, 1, 90.0, options );
			Require( r.Ok, $"gizmo-pivot paste refused: {r.Reason} {r.Message}" );
			Require( r.PivotUsed == Vector3.Zero, $"PivotUsed was {r.PivotUsed}, expected the gizmo pivot" );
			RequireAabb( r.Brushes[0], new Vector3( -32, 0, 0 ), new Vector3( 0, 64, 32 ), 1e-3,
				"gizmo-pivot 90-degree copy" );

			// Snapping off on the gizmo means no angle snap is carried.
			var freeGizmo = new GizmoModel();
			var freeOptions = PasteSpecialOptions.FromGizmo( freeGizmo );
			Require( freeOptions.AngleSnapDegrees == 0.0,
				"FromGizmo carried an angle snap from a gizmo with snapping off" );
		} );

		Check( "angle snap 17deg with 15deg snap pastes at 15deg", () =>
		{
			// Corner (0,0,0) rotated 15 deg about (32,16,16) lands at
			// (5.23,-7.74,0), snapped to grid 1 at (5,-8,0). At 17 deg it
			// would land at (6.08,-8.66,0), snapped to (6,-9,0). The
			// 1.2 radius separates the two snapped corners (1.41 apart)
			// while allowing the re-solve to sit ~0.7 off the snapped point.
			var source = SourceBox();
			var options = new PasteSpecialOptions { GridSize = 1.0, AngleSnapDegrees = 15.0 };
			var r = MapPaste.PasteWithRotation( new[] { source }, 1, 17.0, options );
			Require( r.Ok, $"angle-snapped paste refused: {r.Reason} {r.Message}" );
			var copy = r.Brushes[0];
			RequireContainsVertex( copy, new Vector3( 5, -8, 0 ), 1.2, 1.0, "angle-snapped copy" );
			RequireNoVertex( copy, new Vector3( 6, -9, 0 ), 1.2, 1.0, "angle-snapped copy" );
		} );

		Check( "offset 10 on grid 16 snaps to 16", () =>
		{
			var source = SourceBox();
			var r = MapPaste.PasteWithOffset( new[] { source }, new Vector3( 10, 0, 0 ) );
			Require( r.Ok, $"offset paste refused: {r.Reason} {r.Message}" );
			RequireAabb( r.Brushes[0], new Vector3( 16, 0, 0 ), new Vector3( 80, 32, 32 ), 1e-3,
				"offset-10 copy" );
			Require( r.Brushes[0].IsOnGrid( 16 ), "offset copy is not on grid 16" );
		} );

		Check( "off-grid source pastes on-grid", () =>
		{
			// Source corners at 10/74/42 are off grid 16; the pasted copy
			// snaps to 16/80/48. This is the paste fix for off-grid
			// vertex drift after copy/paste.
			var source = Box( new Vector3( 10, 10, 10 ), new Vector3( 74, 42, 42 ) );
			var r = MapPaste.PasteAtCentre( new[] { source } );
			Require( r.Ok, $"paste refused: {r.Reason} {r.Message}" );
			var copy = r.Brushes[0];
			RequireAabb( copy, new Vector3( 16, 16, 16 ), new Vector3( 80, 48, 48 ), 1e-3,
				"snapped off-grid copy" );
			Require( copy.IsOnGrid( 16 ), "snapped copy is not on grid 16" );
			Require( copy.IsValid(), "snapped copy is not valid: " + string.Join( "; ", copy.Validate() ) );
			Require( Math.Abs( copy.Volume() - source.Volume() ) < 1e-3,
				$"snapped copy volume {copy.Volume()} vs source {source.Volume()}" );
		} );

		Check( "rotate-45 on a fine grid stays valid and on-grid", () =>
		{
			// A 45-degree paste is the case a strict 1e-3 re-solve would
			// reject: snapped vertices sit up to ~a grid step off their
			// best-fit planes. Judged at the paste grid size the copy is
			// a valid convex on-grid brush with its volume intact.
			var source = SourceBox();
			var options = new PasteSpecialOptions { GridSize = 1.0 };
			var r = MapPaste.PasteWithRotation( new[] { source }, 1, 45.0, options );
			Require( r.Ok, $"45-degree paste refused: {r.Reason} {r.Message}" );
			var copy = r.Brushes[0];
			Require( copy.IsValid( 1.0 ),
				"45-degree copy is not valid at its grid size: " + string.Join( "; ", copy.Validate( 1.0 ) ) );
			Require( copy.IsOnGrid( 1.0, 1.0 ), "45-degree copy is not on grid 1" );
			Require( Math.Abs( copy.Volume( 1.0 ) - source.Volume() ) / source.Volume() < 0.10,
				$"45-degree copy volume {copy.Volume( 1.0 )} moved more than 10% from source {source.Volume()}" );
		} );

		Check( "invalid source brush is rejected with a named reason", () =>
		{
			// Only 3 faces: no closed solid.
			var three = MapPasteBrush.FromPlanes( new[]
			{
				new Plane( new Vector3( 0, 0, -1 ), 0 ),
				new Plane( new Vector3( 0, 0, 1 ), -32 ),
				new Plane( new Vector3( 0, -1, 0 ), 0 ),
			} );
			RequireReason( MapPaste.PasteAtCentre( new[] { three } ), "paste-source-invalid" );

			// Flat brush: top and bottom are the same plane z = 0, so the
			// volume is zero.
			var flat = MapPasteBrush.FromPlanes( new[]
			{
				new Plane( new Vector3( 0, 0, -1 ), 0 ),
				new Plane( new Vector3( 0, 0, 1 ), 0 ),
				new Plane( new Vector3( 0, -1, 0 ), 0 ),
				new Plane( new Vector3( 0, 1, 0 ), -32 ),
				new Plane( new Vector3( -1, 0, 0 ), 0 ),
				new Plane( new Vector3( 1, 0, 0 ), -64 ),
			} );
			RequireReason( MapPaste.PasteAtCentre( new[] { flat } ), "paste-source-invalid" );
		} );

		Check( "empty selection and zero copies are rejected with named reasons", () =>
		{
			RequireReason( MapPaste.PasteSpecial( null!, null ), "paste-selection-null" );
			RequireReason( MapPaste.PasteSpecial( Array.Empty<MapPasteBrush>() ), "paste-selection-empty" );
			var withNull = new List<MapPasteBrush> { null! };
			RequireReason( MapPaste.PasteSpecial( withNull ), "paste-brush-null" );
			RequireReason(
				MapPaste.PasteSpecial( new[] { SourceBox() }, new PasteSpecialOptions { Copies = 0 } ),
				"paste-copies-not-positive" );
		} );

		Check( "bad grid, angle snap, axis and copy cap are rejected with named reasons", () =>
		{
			var source = new[] { SourceBox() };
			RequireReason(
				MapPaste.PasteSpecial( source, new PasteSpecialOptions { GridSize = 0 } ),
				"paste-grid-not-positive" );
			RequireReason(
				MapPaste.PasteSpecial( source, new PasteSpecialOptions { AngleSnapDegrees = -5 } ),
				"paste-angle-snap-not-positive" );
			RequireReason(
				MapPaste.PasteSpecial( source, new PasteSpecialOptions { RotationAxis = Vector3.Zero } ),
				"paste-rotation-axis-invalid" );
			RequireReason(
				MapPaste.PasteSpecial( source, new PasteSpecialOptions { Copies = PasteSpecialOptions.MaxCopies + 1 } ),
				"paste-too-many-copies" );
		} );

		Check( "paste does not mutate the source selection", () =>
		{
			var source = SourceBox();
			var beforePlanes = source.Planes.Select( p => ( p.Normal, p.D ) ).ToArray();
			var beforeMaterials = source.Materials.ToArray();
			var beforeVolume = source.Volume();
			var r = MapPaste.PasteWithRotation( new[] { source }, 2, 45.0,
				new PasteSpecialOptions { GridSize = 1.0, Offset = new Vector3( 10, 0, 0 ) } );
			Require( r.Ok, $"paste refused: {r.Reason} {r.Message}" );
			for ( int i = 0; i < source.FaceCount; i++ )
			{
				Require( source.Planes[i].Normal == beforePlanes[i].Normal && source.Planes[i].D == beforePlanes[i].D,
					$"source plane {i} moved" );
				Require( source.Materials[i] == beforeMaterials[i], $"source material {i} changed" );
			}
			Require( Math.Abs( source.Volume() - beforeVolume ) < 1e-6, "source volume changed" );
		} );

		Check( "multi-brush selection pastes every brush with materials preserved", () =>
		{
			var a = Box( new Vector3( 0, 0, 0 ), new Vector3( 64, 32, 32 ), "brick/brick" );
			var b = Box( new Vector3( 128, 0, 0 ), new Vector3( 192, 32, 32 ), "metal/metal" );
			var r = MapPaste.PasteAtCentre( new[] { a, b } );
			Require( r.Ok, $"paste refused: {r.Reason} {r.Message}" );
			Require( r.Brushes.Count == 2, $"expected 2 brushes, got {r.Brushes.Count}" );
			Require( r.Brushes[0].Materials.All( m => m == "brick/brick" ), "first brush lost its material" );
			Require( r.Brushes[1].Materials.All( m => m == "metal/metal" ), "second brush lost its material" );
			Require( Math.Abs( r.Brushes[0].Volume() - a.Volume() ) < 1e-3, "first brush volume changed" );
			Require( Math.Abs( r.Brushes[1].Volume() - b.Volume() ) < 1e-3, "second brush volume changed" );
			Require( r.Brushes[0].IsOnGrid( 16 ) && r.Brushes[1].IsOnGrid( 16 ), "a pasted brush is off-grid" );
		} );

		Check( "fixture cases recompute", () =>
		{
			var path = FindFixture( "cases.json" );
			Require( path != null, "fixture cases.json not found under tests/fixtures/hammer/hammer-paste-special/" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			Require( root.GetProperty( "schema" ).GetString() == "hammer-paste-special-cases/v1",
				"fixture schema mismatch" );
			var src = root.GetProperty( "source" );
			float[] V3( JsonElement e ) => e.EnumerateArray().Select( v => v.GetSingle() ).ToArray();
			var smin = V3( src.GetProperty( "min" ) );
			var smax = V3( src.GetProperty( "max" ) );
			var mats = src.GetProperty( "materials" ).EnumerateArray()
				.Select( m => m.GetString() ?? "default" ).ToArray();
			var source = BoxMats( new Vector3( smin[0], smin[1], smin[2] ), new Vector3( smax[0], smax[1], smax[2] ), mats );

			foreach ( var c in root.GetProperty( "cases" ).EnumerateArray() )
			{
				var name = c.GetProperty( "name" ).GetString();
				var mode = c.GetProperty( "mode" ).GetString();
				var copies = c.GetProperty( "copies" ).GetInt32();
				var angle = c.GetProperty( "angle" ).GetDouble();
				var off = V3( c.GetProperty( "offset" ) );
				var grid = c.GetProperty( "grid" ).GetDouble();
				var expectedCopies = c.GetProperty( "expectedCopies" ).GetInt32();
				var expectedVolume = c.GetProperty( "expectedVolume" ).GetDouble();
				var expectedCentroid = V3( c.GetProperty( "expectedCentroid" ) );
				var options = new PasteSpecialOptions { GridSize = grid };
				PasteResult r = mode switch
				{
					"at-centre" => MapPaste.PasteAtCentre( new[] { source }, options ),
					"rotate" => MapPaste.PasteWithRotation( new[] { source }, copies, angle, options ),
					"offset" => MapPaste.PasteWithOffset( new[] { source }, new Vector3( off[0], off[1], off[2] ), options ),
					_ => throw new Exception( $"fixture case '{name}' has unknown mode '{mode}'" ),
				};
				Require( r.Ok, $"fixture case '{name}' refused: {r.Reason} {r.Message}" );
				Require( r.Brushes.Count == expectedCopies,
					$"fixture case '{name}': {r.Brushes.Count} copies vs expected {expectedCopies}" );
				var last = r.Brushes[r.Brushes.Count - 1];
				Require( Math.Abs( last.Volume() - expectedVolume ) < 1e-3,
					$"fixture case '{name}': volume {last.Volume()} vs expected {expectedVolume}" );
				var centroid = last.Centroid();
				Require( ( centroid - new Vector3( expectedCentroid[0], expectedCentroid[1], expectedCentroid[2] ) ).Length() <= 1e-3f,
					$"fixture case '{name}': centroid {centroid} vs expected ({expectedCentroid[0]}, {expectedCentroid[1]}, {expectedCentroid[2]})" );
				Require( last.Materials.SequenceEqual( mats ),
					$"fixture case '{name}': materials did not survive the paste" );
			}
		} );
	}
}
