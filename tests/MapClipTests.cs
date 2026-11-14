using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// 2-point Clip tool for brushes (hammer-brush-clip-two-point, group "mapclip").
///
/// Built against the landed MapBrush API (hammer-brush-core-plane-based-brushes,
/// kernel/Effigy/Map/MapBrush.cs): MapBrushFace (Normal, Distance, MaterialId, Uv),
/// FaceMaterial / FaceUv, FromPlanes, Faces, Volume (float), Centroid, IsValid,
/// Validate() returning MapBrushDefect codes. The clip cases in
/// tests/fixtures/hammer/hammer-brush-clip-two-point/clip-cases.json are the same
/// cases as data.
/// </summary>
internal static class MapClipTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "mapclip", name, test );
		void CheckMissing( string name, Action test, Func<string> missing ) => suite.Add( "mapclip", name, test, missing );
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

		// A face: outward unit normal, plane Dot(n, p) = d, distinct material + UVs per face
		// so a test can tell exactly which faces survived a clip and with what data.
		static MapBrushFace Face( Vec3 n, float d, string material, Vec2 uvOffset, Vec2 uvScale = default, float uvRotation = 0f, float lightmapScale = 16f, int smoothingGroup = 0 )
			=> new MapBrushFace( n, d, material,
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

		// Triangular prism: right triangle (0,0)-(64,0)-(0,64) in XY, extruded z 0..64.
		// Volume = (64*64/2)*64 = 131072.
		static MapBrush Wedge()
		{
			var diag = new Vec3( 1, 1, 0 ).Normal;
			return MapBrush.FromPlanes( new[]
			{
				Face( new Vec3( 0, -1, 0 ), 0, "brick/brick", new Vec2( 0, 0 ) ),
				Face( new Vec3( 0, 0, -1 ), 0, "metal/metal", new Vec2( 1, 0 ) ),
				Face( new Vec3( 0, 0, 1 ), 64, "wood/wood", new Vec2( 2, 0 ) ),
				Face( new Vec3( -1, 0, 0 ), 0, "glass/glass", new Vec2( 3, 0 ) ),
				Face( diag, Vec3.Dot( diag, new Vec3( 64, 0, 0 ) ), "concrete/concrete", new Vec2( 4, 0 ) ),
			} );
		}

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

		static int FaceIndexByMaterial( MapBrush brush, string materialId )
		{
			for ( var i = 0; i < brush.FaceCount; i++ )
				if ( brush.Faces[i].MaterialId == materialId )
					return i;
			return -1;
		}

		static HashSet<string> Materials( MapBrush brush ) => new( brush.Faces.Select( f => f.MaterialId ) );

		static string FindFixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-brush-clip-two-point" );
					if ( File.Exists( Path.Combine( candidate, "clip-cases.json" ) ) )
						return candidate;
					dir = dir.Parent;
				}
			}
			return null;
		}

		Check( "diagonal-top-cut-box-in-half", () =>
		{
			var box = Box64();
			Approx( box.Volume, 262144, 1e-9, "box64 volume" );
			var result = MapClip.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z );
			Require( result.Outcome == ClipOutcome.Cut, $"outcome was {result.Outcome}, expected Cut" );
			Require( result.Changed, "a real cut must report Changed" );
			Require( result.Reason is null, $"a real cut must have no reason, got: {result.Reason}" );
			Require( result.Kept is not null && result.Kept.IsValid, "kept brush must exist and be valid" );
			Approx( result.Kept.Volume, 131072, 1e-3, "kept volume after diagonal cut" );
			Require( result.Kept.FaceCount == 5, $"kept brush has {result.Kept.FaceCount} faces, expected 5 (4 originals + cap)" );
			var capIdx = FindCapFaceIndex( result.Kept, new Vec3( -1, 1, 0 ).Normal, 0 );
			Require( capIdx >= 0, "kept brush has no cap face on the clip plane" );
			var cap = result.Kept.Faces[capIdx];
			Require( cap.MaterialId == MapClip.NoDrawMaterial, $"cap face material was '{cap.MaterialId}', expected nodraw" );
			var mats = Materials( result.Kept );
			Require( mats.SetEquals( new[] { "metal/metal", "wood/wood", "concrete/concrete", "plaster/plaster", MapClip.NoDrawMaterial } ),
				"kept faces lost or gained a material: " + string.Join( ",", mats ) );
		} );

		Check( "split-returns-two-valid-brushes-volumes-sum", () =>
		{
			var box = Box64();
			var result = MapClip.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z,
				new MapClipOptions { Mode = ClipMode.Split } );
			Require( result.Outcome == ClipOutcome.Split, $"outcome was {result.Outcome}, expected Split" );
			Require( result.Changed, "a real split must report Changed" );
			Require( result.Kept is not null && result.Kept.IsValid, "front brush must exist and be valid" );
			Require( result.Second is not null && result.Second.IsValid, "back brush must exist and be valid" );
			Approx( result.Kept.Volume, 131072, 1e-3, "front volume after split" );
			Approx( result.Second.Volume, 131072, 1e-3, "back volume after split" );
			Approx( result.Kept.Volume + result.Second.Volume, box.Volume, 1e-3, "split volumes sum to the original" );
			Require( result.Kept.FaceCount == 5 && result.Second.FaceCount == 5,
				$"split brushes have {result.Kept.FaceCount}/{result.Second.FaceCount} faces, expected 5/5" );
			var frontCapIdx = FindCapFaceIndex( result.Kept, new Vec3( -1, 1, 0 ).Normal, 0 );
			var backCapIdx = FindCapFaceIndex( result.Second, new Vec3( 1, -1, 0 ).Normal, 0 );
			Require( frontCapIdx >= 0 && result.Kept.Faces[frontCapIdx].MaterialId == MapClip.NoDrawMaterial, "front brush has no nodraw cap face" );
			Require( backCapIdx >= 0 && result.Second.Faces[backCapIdx].MaterialId == MapClip.NoDrawMaterial, "back brush has no nodraw cap face" );
			Require( Materials( result.Kept ).SetEquals( new[] { "metal/metal", "wood/wood", "concrete/concrete", "plaster/plaster", MapClip.NoDrawMaterial } ),
				"front brush materials wrong: " + string.Join( ",", Materials( result.Kept ) ) );
			Require( Materials( result.Second ).SetEquals( new[] { "brick/brick", "glass/glass", "concrete/concrete", "plaster/plaster", MapClip.NoDrawMaterial } ),
				"back brush materials wrong: " + string.Join( ",", Materials( result.Second ) ) );
		} );

		Check( "plane-outside-is-noop-with-named-result", () =>
		{
			var box = Box64();
			var result = MapClip.Clip( box, new Vec3( 100, 0, 0 ), new Vec3( 100, 64, 0 ), ClipViewAxis.Z,
				new MapClipOptions { KeepFront = false } );
			Require( result.Outcome == ClipOutcome.NoOpPlaneOutside, $"outcome was {result.Outcome}, expected NoOpPlaneOutside" );
			Require( !result.Changed, "a no-op must not report Changed" );
			Require( result.Reason is not null && result.Reason.Contains( "clip-plane-outside-brush", StringComparison.Ordinal ),
				$"no-op reason must name the cause, got: {result.Reason}" );
			Require( result.Kept is not null, "a no-op must return the original brush as Kept" );
			Approx( result.Kept.Volume, box.Volume, 1e-9, "no-op kept volume" );
			Require( result.Kept.FaceCount == 6, "a no-op must keep all 6 faces" );
			Require( result.Second is null, "a no-op must not produce a second brush" );
		} );

		Check( "plane-outside-on-cut-side-removes-whole-brush", () =>
		{
			var box = Box64();
			var result = MapClip.Clip( box, new Vec3( 100, 0, 0 ), new Vec3( 100, 64, 0 ), ClipViewAxis.Z );
			Require( result.Outcome == ClipOutcome.WholeBrushCutAway, $"outcome was {result.Outcome}, expected WholeBrushCutAway" );
			Require( result.Changed, "cutting the whole brush away is a change" );
			Require( result.Kept is null, "a whole-brush cut-away must leave Kept null" );
			Require( result.Reason is not null && result.Reason.Contains( "whole-brush-on-cut-side", StringComparison.Ordinal ),
				$"reason must name the cause, got: {result.Reason}" );
		} );

		Check( "split-with-plane-outside-is-noop", () =>
		{
			var box = Box64();
			var result = MapClip.Clip( box, new Vec3( 100, 0, 0 ), new Vec3( 100, 64, 0 ), ClipViewAxis.Z,
				new MapClipOptions { Mode = ClipMode.Split } );
			Require( result.Outcome == ClipOutcome.NoOpPlaneOutside, $"outcome was {result.Outcome}, expected NoOpPlaneOutside" );
			Require( !result.Changed, "a no-op split must not report Changed" );
			Require( result.Kept is not null, "a no-op split must return the original brush as Kept" );
			Approx( result.Kept.Volume, box.Volume, 1e-9, "no-op split kept volume" );
			Require( result.Second is null, "a no-op split must not produce a second brush" );
		} );

		Check( "plane-coplanar-with-face-is-noop", () =>
		{
			var box = Box64();
			var result = MapClip.Clip( box, new Vec3( 64, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z );
			Require( result.Outcome == ClipOutcome.NoOpPlaneCoplanar, $"outcome was {result.Outcome}, expected NoOpPlaneCoplanar" );
			Require( !result.Changed, "a coplanar no-op must not report Changed" );
			Require( result.Reason is not null && result.Reason.Contains( "clip-plane-coincident-with-face-", StringComparison.Ordinal ),
				$"reason must name the coincident face, got: {result.Reason}" );
			Require( result.Kept is not null, "a coplanar no-op must return the original brush as Kept" );
			Approx( result.Kept.Volume, box.Volume, 1e-9, "coplanar no-op kept volume" );
		} );

		Check( "two-point-line-must-not-be-degenerate", () =>
		{
			Throws<ArgumentException>( () => MapClip.PlaneFromTwoPoints( new Vec3( 1, 2, 3 ), new Vec3( 1, 2, 3 ), ClipViewAxis.Z ), "clip-two-points-coincident" );
			Throws<ArgumentException>( () => MapClip.PlaneFromTwoPoints( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 5 ), ClipViewAxis.Z ), "clip-line-parallel-to-view-axis" );
			Throws<ArgumentException>( () => MapClip.Clip( Box64(), new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ), ClipViewAxis.Y ), "clip-two-points-coincident" );
		} );

		Check( "null-brush-throws", () =>
		{
			var plane = MapClip.PlaneFromTwoPoints( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z );
			Throws<ArgumentNullException>( () => MapClip.Clip( null, plane ) );
		} );

		Check( "cut-face-gets-current-material-when-nodraw-off", () =>
		{
			var box = Box64();
			var explicit_ = MapClip.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z,
				new MapClipOptions { CutFaceNoDraw = false, CutFaceMaterial = "plaster/plaster" } );
			Require( explicit_.Outcome == ClipOutcome.Cut, $"outcome was {explicit_.Outcome}, expected Cut" );
			var capIdx = FindCapFaceIndex( explicit_.Kept, new Vec3( -1, 1, 0 ).Normal, 0 );
			Require( capIdx >= 0, "kept brush has no cap face" );
			var cap = explicit_.Kept.Faces[capIdx];
			Require( cap.MaterialId == "plaster/plaster", $"cap face material was '{cap.MaterialId}', expected the caller's plaster/plaster" );
			Require( cap.Uv.LightmapScale == 16f, $"cap lightmap scale was {cap.Uv.LightmapScale}, expected the 16 default" );

			var inherited = MapClip.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z,
				new MapClipOptions { CutFaceNoDraw = false, CutFaceMaterial = null, CutFaceLightmapScale = 32f } );
			var cap2Idx = FindCapFaceIndex( inherited.Kept, new Vec3( -1, 1, 0 ).Normal, 0 );
			Require( cap2Idx >= 0, "kept brush has no cap face" );
			var cap2 = inherited.Kept.Faces[cap2Idx];
			Require( cap2.MaterialId == "brick/brick", $"cap face material was '{cap2.MaterialId}', expected the first face material brick/brick" );
			Require( cap2.Uv.LightmapScale == 32f, $"cap lightmap scale was {cap2.Uv.LightmapScale}, expected the caller's 32" );
		} );

		Check( "front-view-vertical-cut-keeps-front-half", () =>
		{
			var box = Box64();
			var result = MapClip.Clip( box, new Vec3( 32, 0, 0 ), new Vec3( 32, 0, 64 ), ClipViewAxis.Y );
			Require( result.Outcome == ClipOutcome.Cut, $"outcome was {result.Outcome}, expected Cut" );
			Approx( result.Kept.Volume, 131072, 1e-3, "kept volume after front-view cut" );
			Approx( result.Kept.Centroid.x, 16, 1e-3, "kept centroid x (the x<=32 half)" );
			var capIdx = FindCapFaceIndex( result.Kept, new Vec3( 1, 0, 0 ), 32 );
			Require( capIdx >= 0, "kept brush has no cap face on x=32" );
			Require( result.Kept.Faces[capIdx].MaterialId == MapClip.NoDrawMaterial, $"cap face material was '{result.Kept.Faces[capIdx].MaterialId}', expected nodraw" );
		} );

		Check( "invalid-brush-rejected-with-named-reason", () =>
		{
			var flat = MapBrush.FromPlanes( new[]
			{
				Face( new Vec3( -1, 0, 0 ), 0, "brick/brick", new Vec2( 0, 0 ) ),
				Face( new Vec3( 1, 0, 0 ), 0, "metal/metal", new Vec2( 1, 0 ) ),
				Face( new Vec3( 0, -1, 0 ), 0, "wood/wood", new Vec2( 2, 0 ) ),
				Face( new Vec3( 0, 1, 0 ), 64, "glass/glass", new Vec2( 3, 0 ) ),
				Face( new Vec3( 0, 0, -1 ), 0, "concrete/concrete", new Vec2( 4, 0 ) ),
				Face( new Vec3( 0, 0, 1 ), 64, "plaster/plaster", new Vec2( 5, 0 ) ),
			} );
			Require( !flat.IsValid, "the zero-volume slab fixture must actually be invalid, or this test proves nothing" );
			var result = MapClip.Clip( flat, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z );
			Require( result.Outcome == ClipOutcome.RejectedInvalidBrush, $"outcome was {result.Outcome}, expected RejectedInvalidBrush" );
			Require( !result.Changed, "a rejection must not report Changed" );
			Require( result.Kept is null, "a rejection must not produce a kept brush" );
			Require( result.Reason is not null && result.Reason.StartsWith( "invalid-brush:", StringComparison.Ordinal ),
				$"reason must name the defects, got: {result.Reason}" );
			Require( result.Reason.Contains( MapBrushDefect.ZeroVolume, StringComparison.Ordinal ),
				$"reason must name the ZeroVolume defect code, got: {result.Reason}" );
		} );

		Check( "clip-preserves-uv-and-face-data-on-kept-faces", () =>
		{
			var box = Box64();
			var result = MapClip.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z,
				new MapClipOptions { Mode = ClipMode.Split } );
			var glassIdx = FaceIndexByMaterial( result.Second, "glass/glass" );
			Require( glassIdx >= 0, "back brush lost the glass/glass face" );
			var glass = result.Second.Faces[glassIdx];
			Require( glass.Uv.Offset.x == 3 && glass.Uv.Offset.y == 0, $"glass UV offset was {glass.Uv.Offset}, expected (3, 0)" );
			Require( glass.Uv.Scale.x == 2 && glass.Uv.Scale.y == 2, $"glass UV scale was {glass.Uv.Scale}, expected (2, 2)" );
			Require( glass.Uv.RotationDegrees == 15f, $"glass UV rotation was {glass.Uv.RotationDegrees}, expected 15" );
			Require( glass.Uv.LightmapScale == 32f, $"glass lightmap scale was {glass.Uv.LightmapScale}, expected 32" );
			Require( glass.Uv.SmoothingGroup == 1, $"glass smoothing group was {glass.Uv.SmoothingGroup}, expected 1" );
			var metalIdx = FaceIndexByMaterial( result.Kept, "metal/metal" );
			Require( metalIdx >= 0, "front brush lost the metal/metal face" );
			var metal = result.Kept.Faces[metalIdx];
			Require( metal.Uv.Offset.x == 1 && metal.Uv.Offset.y == 0, $"metal UV offset was {metal.Uv.Offset}, expected (1, 0)" );
			Require( metal.Uv.Scale.x == 1 && metal.Uv.Scale.y == 1, $"metal UV scale was {metal.Uv.Scale}, expected (1, 1)" );
			Require( metal.Uv.SmoothingGroup == 0, $"metal smoothing group was {metal.Uv.SmoothingGroup}, expected 0" );
		} );

		Check( "wedge-cut-by-vertical-plane", () =>
		{
			var wedge = Wedge();
			Approx( wedge.Volume, 131072, 1e-3, "wedge volume" );
			var result = MapClip.Clip( wedge, new Vec3( 32, 0, 0 ), new Vec3( 32, 0, 64 ), ClipViewAxis.Y );
			Require( result.Outcome == ClipOutcome.Cut, $"outcome was {result.Outcome}, expected Cut" );
			Require( result.Kept is not null && result.Kept.IsValid, "kept wedge half must exist and be valid" );
			Approx( result.Kept.Volume, 98304, 1e-3, "kept wedge volume (x<=32 slice of the prism)" );
		} );

		CheckMissing( "fixture-json-cases-match", () =>
		{
			var dir = FindFixtureDir();
			Require( dir is not null, "fixture dir vanished between the missing-check and the run" );
			using var doc = JsonDocument.Parse( File.ReadAllText( Path.Combine( dir, "clip-cases.json" ) ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			Require( cases.GetArrayLength() == 5, $"fixture has {cases.GetArrayLength()} cases, expected 5" );
			foreach ( var c in cases.EnumerateArray() )
			{
				var name = c.GetProperty( "name" ).GetString();
				var brush = c.GetProperty( "brush" ).GetString() == "wedge" ? Wedge() : Box64();
				Vec3 V( JsonElement e ) => new( e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle() );
				var axis = Enum.Parse<ClipViewAxis>( c.GetProperty( "axis" ).GetString(), ignoreCase: true );
				var mode = c.GetProperty( "mode" ).GetString() == "split" ? ClipMode.Split : ClipMode.Cut;
				var expected = Enum.Parse<ClipOutcome>( c.GetProperty( "expectedOutcome" ).GetString(), ignoreCase: true );
				var result = MapClip.Clip( brush, V( c.GetProperty( "a" ) ), V( c.GetProperty( "b" ) ), axis,
					new MapClipOptions { Mode = mode, KeepFront = c.GetProperty( "keepFront" ).GetBoolean() } );
				Require( result.Outcome == expected, $"case {name}: outcome was {result.Outcome}, expected {expected}" );
				var kept = c.GetProperty( "expectedKeptVolume" );
				if ( kept.ValueKind == JsonValueKind.Number )
					Approx( result.Kept.Volume, kept.GetDouble(), 1e-3, $"case {name}: kept volume" );
				else
					Approx( result.Kept.Volume, brush.Volume, 1e-9, $"case {name}: no-op kept volume" );
				var second = c.GetProperty( "expectedSecondVolume" );
				if ( second.ValueKind == JsonValueKind.Number )
				{
					Require( result.Second is not null, $"case {name}: expected a second brush" );
					Approx( result.Second.Volume, second.GetDouble(), 1e-3, $"case {name}: second volume" );
				}
			}
		}, () => FindFixtureDir() is null ? "fixture clip-cases.json not found under tests/fixtures/hammer/hammer-brush-clip-two-point" : null );
	}
}
