using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

/// <summary>
/// 3-point Clip + polygon tool for brushes (hammer-clip-three-point-polygon,
/// group "clippolygon"). Built on top of MapClip (hammer-brush-clip-two-point):
/// the 3-point tests assert the plane construction and the delegation (same
/// volumes, same material/UV preservation, same named no-ops), and the polygon
/// tests assert the footprint extrusion plus the never-silently-substitute rule
/// for concave input. Cases in
/// tests/fixtures/hammer/hammer-clip-three-point-polygon/polygon-cases.json are
/// the same cases as data.
/// </summary>
internal static class ClipPolygonTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "clippolygon", name, test );
		void CheckMissing( string name, Action test, Func<string> missing ) => suite.Add( "clippolygon", name, test, missing );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string mustContain = null ) where T : Exception
		{
			try { action(); }
			catch ( T ex )
			{
				if ( mustContain is not null && !ex.Message.Contains( mustContain, StringComparison.Ordinal ) )
					throw new Exception( $"expected error naming '{mustContain}', got: {ex.Message}" );
				return;
			}
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}
		void Approx( double actual, double expected, double relTol, string what )
		{
			var tol = Math.Max( 1e-9, Math.Abs( expected ) * relTol );
			Require( Math.Abs( actual - expected ) <= tol, $"{what}: got {actual}, expected {expected} (tol {tol})" );
		}

		// The return type and constructor are spelled global::Effigy.MapBrushFace on
		// purpose: this file is in the global namespace with both `using Effigy;` and
		// `using Effigy.Map;`, and Displacement.cs also defines an
		// Effigy.Map.MapBrushFace — an unqualified name here is ambiguous, and the
		// landed brush face is the Effigy one (see ClipPolygon.cs).
		static global::Effigy.MapBrushFace Face( Vec3 n, float d, string material, Vec2 uvOffset, Vec2 uvScale = default, float uvRotation = 0f, float lightmapScale = 16f, int smoothingGroup = 0 )
			=> new global::Effigy.MapBrushFace( n, d, material,
				new FaceUv( uvOffset, uvScale.x == 0 && uvScale.y == 0 ? new Vec2( 1, 1 ) : uvScale, uvRotation, lightmapScale, smoothingGroup ) );

		static MapBrush Box( double x0, double y0, double z0, double x1, double y1, double z1 )
			=> MapBrush.FromPlanes( new[]
			{
				Face( new Vec3( -1, 0, 0 ), (float)-x0, "brick/brick", new Vec2( 0, 0 ) ),
				Face( new Vec3( 1, 0, 0 ), (float)x1, "metal/metal", new Vec2( 1, 0 ) ),
				Face( new Vec3( 0, -1, 0 ), (float)-y0, "wood/wood", new Vec2( 2, 0 ) ),
				Face( new Vec3( 0, 1, 0 ), (float)y1, "glass/glass", new Vec2( 3, 0 ), new Vec2( 2, 2 ), 15f, 32f, 1 ),
				Face( new Vec3( 0, 0, -1 ), (float)-z0, "concrete/concrete", new Vec2( 4, 0 ) ),
				Face( new Vec3( 0, 0, 1 ), (float)z1, "plaster/plaster", new Vec2( 5, 0 ) ),
			} );

		static MapBrush Box64() => Box( 0, 0, 0, 64, 64, 64 );

		static HashSet<string> Materials( MapBrush brush ) => new( brush.Faces.Select( f => f.MaterialId ) );

		static int FindCapFaceIndex( MapBrush brush, Vec3 capNormal, float capOffset )
		{
			for ( var i = 0; i < brush.FaceCount; i++ )
			{
				var f = brush.Faces[i];
				if ( Vec3.Dot( f.Normal, capNormal ) > 0.999 && Math.Abs( f.Distance - capOffset ) < 1e-3 )
					return i;
			}
			return -1;
		}

		static string FindFixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-clip-three-point-polygon" );
					if ( File.Exists( Path.Combine( candidate, "polygon-cases.json" ) ) )
						return candidate;
					dir = dir.Parent;
				}
			}
			return null;
		}

		static List<Vec2> Square64() => new()
		{
			new Vec2( 0, 0 ), new Vec2( 64, 0 ), new Vec2( 64, 64 ), new Vec2( 0, 64 ),
		};

		// ---- 3-point clip plane ----

		Check( "three-point-plane-through-diagonal-points", () =>
		{
			var plane = ClipPolygon.PlaneFromThreePoints( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), new Vec3( 0, 0, 64 ) );
			var expectedNormal = new Vec3( 1, -1, 0 ).Normal;
			Require( plane.Normal.AlmostEquals( expectedNormal, 1e-5f ), $"plane normal was {plane.Normal}, expected {expectedNormal}" );
			Approx( plane.Offset, 0, 1e-9, "plane offset" );
			foreach ( var p in new[] { new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), new Vec3( 0, 0, 64 ), new Vec3( 32, 32, 32 ) } )
				Approx( plane.Distance( p ), 0, 1e-9, $"point {p} on the plane" );
		} );

		Check( "three-point-plane-horizontal-through-three-heights", () =>
		{
			var plane = ClipPolygon.PlaneFromThreePoints( new Vec3( 0, 0, 32 ), new Vec3( 64, 0, 32 ), new Vec3( 0, 64, 32 ) );
			Require( plane.Normal.AlmostEquals( new Vec3( 0, 0, 1 ), 1e-5f ), $"plane normal was {plane.Normal}, expected +Z" );
			Approx( plane.Offset, 32, 1e-9, "plane offset is the z height" );
		} );

		Check( "three-point-degenerate-input-throws-named", () =>
		{
			Throws<ArgumentException>( () => ClipPolygon.PlaneFromThreePoints( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ) ), "clip-three-points-coincident" );
			Throws<ArgumentException>( () => ClipPolygon.PlaneFromThreePoints( new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 0, 0 ) ), "clip-three-points-coincident" );
			Throws<ArgumentException>( () => ClipPolygon.PlaneFromThreePoints( new Vec3( 0, 0, 0 ), new Vec3( 32, 32, 0 ), new Vec3( 64, 64, 0 ) ), "clip-three-points-collinear" );
			Throws<ArgumentException>( () => ClipPolygon.Clip( Box64(), new Vec3( 0, 0, 0 ), new Vec3( 32, 32, 0 ), new Vec3( 64, 64, 0 ) ), "clip-three-points-collinear" );
		} );

		// ---- 3-point clip ----

		Check( "three-point-clip-halves-box", () =>
		{
			var box = Box64();
			var result = ClipPolygon.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), new Vec3( 0, 0, 64 ) );
			Require( result.Outcome == ClipOutcome.Cut, $"outcome was {result.Outcome}, expected Cut" );
			Require( result.Changed, "a real cut must report Changed" );
			Require( result.Reason is null, $"a real cut must have no reason, got: {result.Reason}" );
			Require( result.Kept is not null && result.Kept.IsValid, "kept brush must exist and be valid" );
			Approx( result.Kept.Volume, 131072, 1e-3, "kept volume after diagonal 3-point cut" );
			Require( result.Kept.FaceCount == 5, $"kept brush has {result.Kept.FaceCount} faces, expected 5 (4 originals + cap)" );
			var capIdx = FindCapFaceIndex( result.Kept, new Vec3( -1, 1, 0 ).Normal, 0 );
			Require( capIdx >= 0, "kept brush has no cap face on the clip plane" );
			Require( result.Kept.Faces[capIdx].MaterialId == MapClip.NoDrawMaterial, $"cap face material was '{result.Kept.Faces[capIdx].MaterialId}', expected nodraw" );
			var mats = Materials( result.Kept );
			Require( mats.SetEquals( new[] { "metal/metal", "wood/wood", "concrete/concrete", "plaster/plaster", MapClip.NoDrawMaterial } ),
				"kept faces lost or gained a material: " + string.Join( ",", mats ) );
		} );

		Check( "three-point-split-volumes-sum", () =>
		{
			var box = Box64();
			var result = ClipPolygon.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), new Vec3( 0, 0, 64 ),
				new MapClipOptions { Mode = ClipMode.Split } );
			Require( result.Outcome == ClipOutcome.Split, $"outcome was {result.Outcome}, expected Split" );
			Require( result.Kept is not null && result.Kept.IsValid, "front brush must exist and be valid" );
			Require( result.Second is not null && result.Second.IsValid, "back brush must exist and be valid" );
			Approx( result.Kept.Volume, 131072, 1e-3, "front volume after 3-point split" );
			Approx( result.Second.Volume, 131072, 1e-3, "back volume after 3-point split" );
			Approx( result.Kept.Volume + result.Second.Volume, box.Volume, 1e-3, "split volumes sum to the original" );
			var frontCapIdx = FindCapFaceIndex( result.Kept, new Vec3( -1, 1, 0 ).Normal, 0 );
			var backCapIdx = FindCapFaceIndex( result.Second, new Vec3( 1, -1, 0 ).Normal, 0 );
			Require( frontCapIdx >= 0 && result.Kept.Faces[frontCapIdx].MaterialId == MapClip.NoDrawMaterial, "front brush has no nodraw cap face" );
			Require( backCapIdx >= 0 && result.Second.Faces[backCapIdx].MaterialId == MapClip.NoDrawMaterial, "back brush has no nodraw cap face" );
		} );

		Check( "three-point-horizontal-clip-keeps-bottom-half", () =>
		{
			var box = Box64();
			var result = ClipPolygon.Clip( box, new Vec3( 0, 0, 32 ), new Vec3( 64, 0, 32 ), new Vec3( 0, 64, 32 ),
				new MapClipOptions { KeepFront = false } );
			Require( result.Outcome == ClipOutcome.Cut, $"outcome was {result.Outcome}, expected Cut" );
			Approx( result.Kept.Volume, 131072, 1e-3, "kept volume after horizontal 3-point cut" );
			Approx( result.Kept.Centroid.z, 16, 1e-3, "kept centroid z (the z<=32 half)" );
		} );

		Check( "three-point-plane-outside-is-noop-with-named-result", () =>
		{
			var box = Box64();
			var result = ClipPolygon.Clip( box, new Vec3( 100, 0, 0 ), new Vec3( 100, 64, 0 ), new Vec3( 100, 0, 64 ),
				new MapClipOptions { KeepFront = false } );
			Require( result.Outcome == ClipOutcome.NoOpPlaneOutside, $"outcome was {result.Outcome}, expected NoOpPlaneOutside" );
			Require( !result.Changed, "a no-op must not report Changed" );
			Require( result.Reason is not null && result.Reason.Contains( "clip-plane-outside-brush", StringComparison.Ordinal ),
				$"no-op reason must name the cause, got: {result.Reason}" );
			Approx( result.Kept.Volume, box.Volume, 1e-9, "no-op kept volume" );
		} );

		Check( "three-point-clip-preserves-uv-on-kept-faces", () =>
		{
			var box = Box64();
			var result = ClipPolygon.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), new Vec3( 0, 0, 64 ),
				new MapClipOptions { Mode = ClipMode.Split } );
			var glassIdx = -1;
			for ( var i = 0; i < result.Second.FaceCount; i++ )
				if ( result.Second.Faces[i].MaterialId == "glass/glass" )
					glassIdx = i;
			Require( glassIdx >= 0, "back brush lost the glass/glass face" );
			var glass = result.Second.Faces[glassIdx];
			Require( glass.Uv.Offset.x == 3 && glass.Uv.Offset.y == 0, $"glass UV offset was {glass.Uv.Offset}, expected (3, 0)" );
			Require( glass.Uv.Scale.x == 2 && glass.Uv.Scale.y == 2, $"glass UV scale was {glass.Uv.Scale}, expected (2, 2)" );
			Require( glass.Uv.RotationDegrees == 15f, $"glass UV rotation was {glass.Uv.RotationDegrees}, expected 15" );
			Require( glass.Uv.LightmapScale == 32f, $"glass lightmap scale was {glass.Uv.LightmapScale}, expected 32" );
			Require( glass.Uv.SmoothingGroup == 1, $"glass smoothing group was {glass.Uv.SmoothingGroup}, expected 1" );
		} );

		Check( "three-point-preview-reports-removed-half", () =>
		{
			var box = Box64();
			var preview = ClipPolygon.Preview( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), new Vec3( 0, 0, 64 ) );
			Require( preview.Outcome == ClipOutcome.Split, $"preview outcome was {preview.Outcome}, expected Split" );
			Approx( preview.FrontVolume, 131072, 1e-3, "preview front volume" );
			Approx( preview.BackVolume, 131072, 1e-3, "preview back volume" );
			Approx( preview.RemovedVolume, 131072, 1e-3, "preview removed volume (the red highlight)" );
			Approx( preview.RemovedFraction, 0.5, 1e-3, "preview removed fraction" );
			Approx( preview.KeptVolume, 131072, 1e-3, "preview kept volume" );
			Require( preview.Points.Count == 3, "preview must carry the three picked points" );
			// Plane outside on the KEPT side (KeepFront=false keeps the x<=100 side
			// the brush is on): nothing is removed. This mirrors the clip no-op test.
			var outside = ClipPolygon.Preview( box, new Vec3( 100, 0, 0 ), new Vec3( 100, 64, 0 ), new Vec3( 100, 0, 64 ),
				new MapClipOptions { KeepFront = false } );
			Require( outside.Outcome == ClipOutcome.NoOpPlaneOutside, $"outside preview outcome was {outside.Outcome}, expected NoOpPlaneOutside" );
			Approx( outside.RemovedVolume, 0, 1e-9, "a plane outside on the kept side removes nothing" );
			Approx( outside.RemovedFraction, 0, 1e-9, "a plane outside on the kept side removes no fraction" );
			Approx( outside.KeptVolume, box.Volume, 1e-9, "a plane outside on the kept side keeps everything" );
			// Same plane, default options (KeepFront=true): the brush sits entirely
			// on the cut side, so committing the cut would remove the WHOLE brush —
			// the highlight must show the full volume, not nothing.
			var outsideCutSide = ClipPolygon.Preview( box, new Vec3( 100, 0, 0 ), new Vec3( 100, 64, 0 ), new Vec3( 100, 0, 64 ) );
			Require( outsideCutSide.Outcome == ClipOutcome.NoOpPlaneOutside, $"outside cut-side preview outcome was {outsideCutSide.Outcome}, expected NoOpPlaneOutside" );
			Approx( outsideCutSide.RemovedVolume, box.Volume, 1e-9, "a plane outside on the cut side removes the whole brush" );
			Approx( outsideCutSide.RemovedFraction, 1, 1e-9, "a plane outside on the cut side removes the whole fraction" );
			Approx( outsideCutSide.KeptVolume, 0, 1e-9, "a plane outside on the cut side keeps nothing" );
		} );

		// ---- polygon footprint ----

		Check( "polygon-square-makes-box", () =>
		{
			var result = ClipPolygon.PolygonFootprint( Square64(), 64, "brick/brick" );
			Require( result.Outcome == PolygonFootprintOutcome.Created, $"outcome was {result.Outcome}, expected Created (reason: {result.Reason})" );
			Require( result.Reason is null, "a created footprint must have no reason" );
			Require( result.Brush is not null && result.Brush.IsValid, "polygon brush must exist and be valid" );
			Approx( result.Brush.Volume, 262144, 1e-3, "square footprint volume" );
			Approx( result.FootprintArea, 4096, 1e-9, "square footprint area" );
			Require( result.Brush.FaceCount == 6, $"square brush has {result.Brush.FaceCount} faces, expected 6 (4 sides + top + bottom)" );
			Require( result.Brush.Centroid.AlmostEquals( new Vec3( 32, 32, 32 ), 1e-3f ), $"square brush centroid was {result.Brush.Centroid}, expected (32, 32, 32)" );
			Require( Materials( result.Brush ).SetEquals( new[] { "brick/brick" } ),
				"every polygon face must carry the requested material: " + string.Join( ",", Materials( result.Brush ) ) );
		} );

		Check( "polygon-triangle-prism-volume", () =>
		{
			var result = ClipPolygon.PolygonFootprint( new List<Vec2> { new( 0, 0 ), new( 64, 0 ), new( 0, 64 ) }, 64 );
			Require( result.Outcome == PolygonFootprintOutcome.Created, $"outcome was {result.Outcome}, expected Created (reason: {result.Reason})" );
			Approx( result.Brush.Volume, 131072, 1e-3, "triangle prism volume" );
			Require( result.Brush.FaceCount == 5, $"triangle brush has {result.Brush.FaceCount} faces, expected 5 (3 sides + top + bottom)" );
			Require( Materials( result.Brush ).SetEquals( new[] { MapClip.NoDrawMaterial } ),
				"a polygon with no material must be nodraw on every face" );
		} );

		Check( "polygon-clockwise-input-still-builds", () =>
		{
			var cw = new List<Vec2> { new( 0, 0 ), new( 0, 64 ), new( 64, 64 ), new( 64, 0 ) };
			var result = ClipPolygon.PolygonFootprint( cw, 64 );
			Require( result.Outcome == PolygonFootprintOutcome.Created, $"outcome was {result.Outcome}, expected Created (reason: {result.Reason})" );
			Approx( result.Brush.Volume, 262144, 1e-3, "clockwise square volume" );
			Require( result.Brush.IsValid, "clockwise square brush must be valid" );
		} );

		Check( "polygon-hexagon-volume", () =>
		{
			var points = new List<Vec2>();
			for ( var i = 0; i < 6; i++ )
			{
				var a = i / 6.0 * Math.PI * 2;
				points.Add( new Vec2( (float)( 32 * Math.Cos( a ) ), (float)( 32 * Math.Sin( a ) ) ) );
			}
			var result = ClipPolygon.PolygonFootprint( points, 16 );
			Require( result.Outcome == PolygonFootprintOutcome.Created, $"outcome was {result.Outcome}, expected Created (reason: {result.Reason})" );
			// Regular hexagon, circumradius 32: area = (3*sqrt(3)/2) * 32^2; height 16.
			Approx( result.Brush.Volume, 3 * Math.Sqrt( 3 ) / 2 * 1024 * 16, 1e-3, "hexagon prism volume" );
			Require( result.Brush.FaceCount == 8, $"hexagon brush has {result.Brush.FaceCount} faces, expected 8 (6 sides + top + bottom)" );
		} );

		Check( "polygon-3d-points-use-base-z", () =>
		{
			var result = ClipPolygon.PolygonFootprint( new List<Vec3>
			{
				new( 0, 0, 16 ), new( 64, 0, 16 ), new( 64, 64, 16 ), new( 0, 64, 16 ),
			}, 32, "metal/metal" );
			Require( result.Outcome == PolygonFootprintOutcome.Created, $"outcome was {result.Outcome}, expected Created (reason: {result.Reason})" );
			Approx( result.Brush.Volume, 131072, 1e-3, "3D square footprint volume (64x64x32)" );
			Approx( result.Brush.Centroid.z, 32, 1e-3, "3D square brush centroid z (base 16 + half of 32)" );
			Throws<ArgumentException>( () => ClipPolygon.PolygonFootprint( new List<Vec3>
			{
				new( 0, 0, 16 ), new( 64, 0, 16 ), new( 64, 64, 17 ), new( 0, 64, 16 ),
			}, 32 ), "polygon-points-not-coplanar" );
		} );

		Check( "polygon-concave-rejected-with-hull-suggestion", () =>
		{
			var dart = new List<Vec2>
			{
				new( 0, 0 ), new( 64, 0 ), new( 64, 64 ), new( 32, 32 ), new( 0, 64 ),
			};
			var result = ClipPolygon.PolygonFootprint( dart, 64 );
			Require( result.Outcome == PolygonFootprintOutcome.RejectedNonConvex, $"outcome was {result.Outcome}, expected RejectedNonConvex" );
			Require( result.Brush is null, "a rejected footprint must not produce a brush" );
			Require( result.Reason is not null && result.Reason.Contains( "polygon-non-convex", StringComparison.Ordinal ),
				$"reason must name the cause, got: {result.Reason}" );
			Require( result.Reason.Contains( "vertex 3", StringComparison.Ordinal ),
				$"reason must name the concave vertex, got: {result.Reason}" );
			Require( result.SuggestedHull.Count == 4, $"hull suggestion has {result.SuggestedHull.Count} points, expected the 4 corners" );
			var hullSet = new HashSet<(float, float)>( result.SuggestedHull.Select( p => (p.x, p.y) ) );
			Require( hullSet.SetEquals( new[] { (0f, 0f), (64f, 0f), (64f, 64f), (0f, 64f) } ),
				"hull suggestion must be the four corners: " + string.Join( ";", result.SuggestedHull ) );
			// The suggestion is usable when the caller chooses it explicitly — but it
			// was NOT substituted in: this call builds from the hull on purpose.
			var fromHull = ClipPolygon.PolygonFootprint( result.SuggestedHull, 64 );
			Require( fromHull.Outcome == PolygonFootprintOutcome.Created, "the suggested hull must itself build" );
			Approx( fromHull.Brush.Volume, 262144, 1e-3, "hull brush volume" );
		} );

		Check( "polygon-mid-edge-point-still-builds", () =>
		{
			// A redundant point on a straight edge is still a convex footprint: the
			// builder drops it, and the brush is the plain square (6 faces, not 7,
			// and no duplicate-plane faces). Without the drop this throws
			// InvalidOperationException from the MapBrush validity check.
			var withMidpoint = new List<Vec2>
			{
				new( 0, 0 ), new( 32, 0 ), new( 64, 0 ), new( 64, 64 ), new( 0, 64 ),
			};
			var result = ClipPolygon.PolygonFootprint( withMidpoint, 64 );
			Require( result.Outcome == PolygonFootprintOutcome.Created, $"outcome was {result.Outcome}, expected Created (reason: {result.Reason})" );
			Require( result.Brush is not null && result.Brush.IsValid, "mid-edge-point brush must exist and be valid" );
			Approx( result.Brush.Volume, 262144, 1e-3, "mid-edge-point square volume" );
			Require( result.Brush.FaceCount == 6, $"mid-edge-point brush has {result.Brush.FaceCount} faces, expected 6 (the redundant vertex adds no face)" );
		} );

		Check( "polygon-self-intersecting-rejected", () =>
		{
			var bowtie = new List<Vec2>
			{
				new( 0, 0 ), new( 64, 64 ), new( 64, 0 ), new( 0, 64 ),
			};
			var result = ClipPolygon.PolygonFootprint( bowtie, 64 );
			Require( result.Outcome == PolygonFootprintOutcome.RejectedSelfIntersecting, $"outcome was {result.Outcome}, expected RejectedSelfIntersecting" );
			Require( result.Brush is null, "a rejected footprint must not produce a brush" );
			Require( result.Reason is not null && result.Reason.Contains( "polygon-self-intersecting", StringComparison.Ordinal ),
				$"reason must name the cause, got: {result.Reason}" );
			Require( result.SuggestedHull.Count == 4, $"hull suggestion has {result.SuggestedHull.Count} points, expected the 4 corners" );
		} );

		Check( "polygon-degenerate-rejected", () =>
		{
			var two = ClipPolygon.PolygonFootprint( new List<Vec2> { new( 0, 0 ), new( 64, 0 ) }, 64 );
			Require( two.Outcome == PolygonFootprintOutcome.RejectedDegenerate, $"outcome was {two.Outcome}, expected RejectedDegenerate" );
			Require( two.Brush is null, "a rejected footprint must not produce a brush" );
			Require( two.Reason is not null && two.Reason.Contains( "polygon-degenerate", StringComparison.Ordinal ),
				$"reason must name the cause, got: {two.Reason}" );
			var collinear = ClipPolygon.PolygonFootprint( new List<Vec2> { new( 0, 0 ), new( 32, 0 ), new( 64, 0 ) }, 64 );
			Require( collinear.Outcome == PolygonFootprintOutcome.RejectedDegenerate, $"outcome was {collinear.Outcome}, expected RejectedDegenerate" );
			Require( collinear.Reason.Contains( "zero-area", StringComparison.Ordinal ),
				$"reason must name the zero area, got: {collinear.Reason}" );
		} );

		Check( "polygon-bad-height-throws", () =>
		{
			Throws<ArgumentOutOfRangeException>( () => ClipPolygon.PolygonFootprint( Square64(), 0 ), "polygon-height-must-be-positive" );
			Throws<ArgumentOutOfRangeException>( () => ClipPolygon.PolygonFootprint( Square64(), -5 ), "polygon-height-must-be-positive" );
			Throws<ArgumentOutOfRangeException>( () => ClipPolygon.PolygonFootprint( Square64(), float.NaN ), "polygon-height-must-be-positive" );
		} );

		Check( "polygon-convex-hull-helper", () =>
		{
			var hull = ClipPolygon.ConvexHull( new List<Vec2>
			{
				new( 0, 0 ), new( 64, 0 ), new( 64, 64 ), new( 32, 32 ), new( 0, 64 ), new( 32, 0 ),
			} );
			Require( hull.Count == 4, $"hull has {hull.Count} points, expected 4 (the collinear mid-edge point must drop out)" );
			var hullSet = new HashSet<(float, float)>( hull.Select( p => (p.x, p.y) ) );
			Require( hullSet.SetEquals( new[] { (0f, 0f), (64f, 0f), (64f, 64f), (0f, 64f) } ), "hull points wrong: " + string.Join( ";", hull ) );
			Approx( ClipPolygon.FootprintArea( Square64() ), 4096, 1e-9, "footprint area helper" );
			Approx( ClipPolygon.FootprintArea( new List<Vec2> { new( 0, 0 ), new( 64, 0 ), new( 0, 64 ) } ), 2048, 1e-9, "triangle footprint area helper" );
		} );

		CheckMissing( "fixture-json-cases-match", () =>
		{
			var dir = FindFixtureDir();
			Require( dir is not null, "fixture dir vanished between the missing-check and the run" );
			using var doc = JsonDocument.Parse( File.ReadAllText( Path.Combine( dir, "polygon-cases.json" ) ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			Require( cases.GetArrayLength() == 6, $"fixture has {cases.GetArrayLength()} cases, expected 6" );
			foreach ( var c in cases.EnumerateArray() )
			{
				var name = c.GetProperty( "name" ).GetString();
				var points = c.GetProperty( "points" ).EnumerateArray()
					.Select( p => new Vec2( p[0].GetSingle(), p[1].GetSingle() ) ).ToList();
				var height = c.GetProperty( "height" ).GetSingle();
				var expected = Enum.Parse<PolygonFootprintOutcome>( c.GetProperty( "expectedOutcome" ).GetString(), ignoreCase: true );
				var result = ClipPolygon.PolygonFootprint( points, height );
				Require( result.Outcome == expected, $"case {name}: outcome was {result.Outcome}, expected {expected}" );
				var volume = c.GetProperty( "expectedVolume" );
				if ( volume.ValueKind == JsonValueKind.Number )
				{
					Require( result.Brush is not null, $"case {name}: expected a brush" );
					Approx( result.Brush.Volume, volume.GetDouble(), 1e-3, $"case {name}: volume" );
				}
				else
				{
					Require( result.Brush is null, $"case {name}: a rejected footprint must not produce a brush" );
					Require( result.Reason is not null && result.Reason.Contains( c.GetProperty( "expectedReasonContains" ).GetString(), StringComparison.Ordinal ),
						$"case {name}: reason must contain '{c.GetProperty( "expectedReasonContains" ).GetString()}', got: {result.Reason}" );
					var hull = c.GetProperty( "expectedHullPointCount" );
					if ( hull.ValueKind == JsonValueKind.Number )
						Require( result.SuggestedHull.Count == hull.GetInt32(),
							$"case {name}: hull has {result.SuggestedHull.Count} points, expected {hull.GetInt32()}" );
				}
			}
		}, () => FindFixtureDir() is null ? "fixture polygon-cases.json not found under tests/fixtures/hammer/hammer-clip-three-point-polygon" : null );
	}
}
