#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

// Hammer Desktop Interop: CAD feature tree <-> map brushes.
// Board: hammer-interop-cad-feature-tree-brushes. Group name 'cadtomap'.
//
// Fixtures live in tests/fixtures/hammer/hammer-interop-cad-feature-tree-brushes/,
// documented in that directory's README.md:
//   cases.txt        — data-driven extrude cases (kind, dims, expected volume)
//   roundtrip.effigy — a StudioDocument text with a Sketch + CadToMapFeature, written
//                      by the isolated harness (--write-fixture); the round-trip test
//                      reads it back and rebuilds it.
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
// final integrator adds the one CadToMapTests.Register(registry, settings); line.
//
// What is NOT covered here, and why:
// - MapDocument round-trip of published brushes: covered at the seam level instead
//   (PublishToMap + the MapDocument's own AddBrush/FindBrush/ToMapBrush). The map
//   document's own save/load is its own ticket's tests.
// - Taper/draft extrudes, holed profiles, two-rail sweeps, smooth lofts: refused with
//   a named reason (see the refusal tests), not silently approximated.
// - The real test runner: the shared tree does not compile right now
//   (MapBrushPrimitives.cs, 4x CS0246 MapFace — a sibling's file, not this ticket's),
//   so these tests were run in an isolated harness; see the ticket handoff for the
//   exact command and counts. The gate re-runs them in the real runner.
internal static class CadToMapTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "cadtomap", name, test );

		Check( "rectangle sketch extrudes to one valid box brush", () =>
		{
			var brushes = CadToMap.ExtrudeSketch( RectSketch( 16f, 8f ), 4f, "brick/brickwall001" );
			Assert( brushes.Count == 1, $"rectangle must extrude to 1 brush, got {brushes.Count}" );
			var brush = brushes[0];
			Assert( brush.IsValid, $"box brush must be valid, got: {Defects( brush )}" );
			Assert( brush.FaceCount == 6, $"box brush must have 6 faces, got {brush.FaceCount}" );
			AssertNear( brush.Volume, 512f, 1e-3f, "box volume" );
			AssertVec( brush.Centroid, new Vec3( 8f, 4f, 2f ), 1e-4f, "box centroid" );
			Assert( brush.Faces.All( f => f.MaterialId == "brick/brickwall001" ),
				"every face must carry the requested material" );
		} );

		Check( "extrude follows the sketch plane and flip", () =>
		{
			var sketch = new Sketch { Plane = SketchPlane.XZ };
			sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 16, 8 ) );
			var down = CadToMap.ExtrudeSketch( sketch, 4f );
			Assert( down.Count == 1, "XZ extrude must be 1 brush" );
			AssertNear( down[0].Volume, 512f, 1e-3f, "XZ box volume" );
			// XZ plane normal is (0,-1,0): the solid runs y 0..-4.
			AssertVec( down[0].Centroid, new Vec3( 8f, -2f, 4f ), 1e-4f, "XZ box centroid" );
			var up = CadToMap.ExtrudeSketch( sketch, 4f, flip: true );
			AssertVec( up[0].Centroid, new Vec3( 8f, 2f, 4f ), 1e-4f, "flipped XZ box centroid" );
		} );

		Check( "concave L profile extrudes to triangle prisms with exact total volume", () =>
		{
			var sketch = new Sketch();
			sketch.AddPolygon(
				new Vec2( 0, 0 ), new Vec2( 10, 0 ), new Vec2( 10, 4 ),
				new Vec2( 4, 4 ), new Vec2( 4, 10 ), new Vec2( 0, 10 ) );
			var brushes = CadToMap.ExtrudeSketch( sketch, 2f );
			Assert( brushes.Count == 4, $"L profile must triangulate to 4 prisms, got {brushes.Count}" );
			foreach ( var brush in brushes )
				Assert( brush.IsValid, $"L prism must be valid, got: {Defects( brush )}" );
			AssertNear( TotalVolume( brushes ), 128f, 1e-3f, "L prism total volume" );
		} );

		Check( "circle sketch extrudes to a faceted prism near the analytic volume", () =>
		{
			var sketch = new Sketch();
			sketch.AddCircle( new Vec2( 0, 0 ), 4f );
			var brushes = CadToMap.ExtrudeSketch( sketch, 2f );
			Assert( brushes.Count == 1, "circle must extrude to 1 brush (its tessellation is convex)" );
			Assert( brushes[0].IsValid, $"circle prism must be valid, got: {Defects( brushes[0] )}" );
			Assert( brushes[0].FaceCount >= 8, $"circle prism must have side faces, got {brushes[0].FaceCount}" );
			AssertNear( brushes[0].Volume, MathF.PI * 16f * 2f, MathF.PI * 16f * 2f * 0.01f,
				"circle prism volume within 1% of analytic" );
		} );

		Check( "two disjoint rectangles extrude to two brushes", () =>
		{
			var sketch = new Sketch();
			sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 16, 8 ) );
			sketch.AddRectangle( new Vec2( 20, 0 ), new Vec2( 30, 5 ) );
			var brushes = CadToMap.ExtrudeSketch( sketch, 4f );
			Assert( brushes.Count == 2, $"two rects must extrude to 2 brushes, got {brushes.Count}" );
			AssertNear( TotalVolume( brushes ), 712f, 1e-3f, "two-rect total volume" );
		} );

		Check( "edit the sketch rebuilds the brush", () =>
		{
			var studio = new PartStudio();
			var sketchFeature = studio.Add( new SketchFeature() );
			sketchFeature.Curves.Value = "rect:0,0,16,8";
			var feature = studio.Add( new CadToMapFeature() );
			feature.Distance.Value = 4f;
			feature.MaterialName.Value = "brick/brickwall001";

			studio.Rebuild();
			Assert( feature.Error is null, $"first rebuild must succeed, got error: {feature.Error}" );
			Assert( studio.Bodies.Count == 1, $"first rebuild must make 1 body, got {studio.Bodies.Count}" );
			var firstId = studio.Bodies[0].Id;
			var (_, max) = Bounds( studio.Bodies[0] );
			AssertVec( max, new Vec3( 16f, 8f, 4f ), 1e-4f, "first rebuild bounds" );

			sketchFeature.Curves.Value = "rect:0,0,20,8";
			studio.MarkDirty( sketchFeature );
			studio.Rebuild();
			Assert( feature.Error is null, $"rebuild after the sketch edit must succeed, got error: {feature.Error}" );
			Assert( studio.Bodies.Count == 1, $"rebuild after the sketch edit must still make 1 body, got {studio.Bodies.Count}" );
			Assert( studio.Bodies[0].Id == firstId, "the rebuilt body must keep its feature-based id" );
			(_, max) = Bounds( studio.Bodies[0] );
			AssertVec( max, new Vec3( 20f, 8f, 4f ), 1e-4f, "bounds after the sketch edit" );
		} );

		Check( "brush feature survives a StudioDocument save and load", () =>
		{
			var studio = new PartStudio();
			var sketchFeature = studio.Add( new SketchFeature() );
			sketchFeature.Curves.Value = "rect:0,0,16,8";
			var feature = studio.Add( new CadToMapFeature() );
			feature.Distance.Value = 4f;
			feature.MaterialName.Value = "brick/brickwall001";
			CadToMap.AttachToMap( feature, "test-room" );
			studio.Rebuild();
			Assert( studio.Bodies.Count == 1, "setup rebuild must make 1 body" );

			var text = StudioDocument.Write( studio );
			Assert( text.Contains( "feature CadToMapFeature", StringComparison.Ordinal ),
				"saved text must name the CadToMapFeature type" );
			Assert( text.Contains( "test-room", StringComparison.Ordinal ),
				"saved text must carry the map id" );

			var reloaded = StudioDocument.Read( text );
			Assert( reloaded.Features.Count == 2, $"reload must keep 2 features, got {reloaded.Features.Count}" );
			var again = reloaded.Features.OfType<CadToMapFeature>().Single();
			Assert( again.Distance.Value == 4f, $"reload must keep Distance 4, got {again.Distance.Value}" );
			Assert( again.MapId.Value == "test-room", $"reload must keep MapId, got '{again.MapId.Value}'" );
			Assert( again.Conversion.Index == 0, $"reload must keep Conversion Extrude, got index {again.Conversion.Index}" );
			Assert( again.MaterialName.Value == "brick/brickwall001", "reload must keep the material name" );

			reloaded.Rebuild();
			Assert( again.Error is null, $"reloaded rebuild must succeed, got error: {again.Error}" );
			Assert( reloaded.Bodies.Count == 1, $"reloaded rebuild must make 1 body, got {reloaded.Bodies.Count}" );
			var (_, max) = Bounds( reloaded.Bodies[0] );
			AssertVec( max, new Vec3( 16f, 8f, 4f ), 1e-4f, "reloaded rebuild bounds" );

			var path = Path.Combine( settings.WorkDirectory, "cadtomap-roundtrip.effigy" );
			StudioDocument.WriteFile( studio, path );
			var fromFile = StudioDocument.ReadFile( path );
			Assert( fromFile.Features.OfType<CadToMapFeature>().Count() == 1,
				"file round-trip must keep the CadToMapFeature" );
		} );

		Check( "snapshot brushes round-trip as MapBrushFeatures", () =>
		{
			var brushes = CadToMap.ExtrudeSketch( RectSketch( 16f, 8f ), 4f, "brick/brickwall001" );
			var studio = new PartStudio();
			var recorded = CadToMap.RecordBrushes( studio, brushes, "Wall" );
			Assert( recorded.Count == 1, $"must record 1 snapshot feature, got {recorded.Count}" );
			Assert( recorded[0].Name == "Wall", $"snapshot feature must be named Wall, got '{recorded[0].Name}'" );

			var reloaded = StudioDocument.Read( StudioDocument.Write( studio ) );
			var again = reloaded.Features.OfType<MapBrushFeature>().Single();
			AssertNear( again.Brush.Volume, 512f, 1e-3f, "reloaded snapshot brush volume" );
			Assert( again.Brush.FaceCount == 6, $"reloaded snapshot brush must have 6 faces, got {again.Brush.FaceCount}" );
			Assert( again.MaterialIds[0] == "brick/brickwall001",
				$"reloaded snapshot must keep its material, got '{again.MaterialIds[0]}'" );
			Assert( CadToMap.MapBrushFeatures( reloaded ).Count == 1,
				"MapBrushFeatures must list the snapshot" );

			reloaded.Rebuild();
			Assert( reloaded.Bodies.Count == 1, $"reloaded snapshot rebuild must make 1 body, got {reloaded.Bodies.Count}" );
		} );

		Check( "revolve a rectangle about its edge makes a faceted cylinder", () =>
		{
			var loop = new List<Vec2>
			{
				new( 0, 0 ), new( 4, 0 ), new( 4, 8 ), new( 0, 8 ),
			};
			var brushes = CadToMap.RevolveLoop( loop, SketchPlane.XY,
				new Vec2( 0, 0 ), new Vec2( 1, 0 ), 360f, 24, "brick/brickwall001" );
			Assert( brushes.Count == 48, $"2 triangles x 24 segments must be 48 brushes, got {brushes.Count}" );
			foreach ( var brush in brushes )
				Assert( brush.IsValid, $"revolve segment must be valid, got: {Defects( brush )}" );
			AssertNear( TotalVolume( brushes ), MathF.PI * 64f * 4f, MathF.PI * 64f * 4f * 0.02f,
				"revolved volume within 2% of the analytic cylinder" );
		} );

		Check( "loft between two rectangles makes the exact frustum volume", () =>
		{
			var sections = new List<CadSection>
			{
				new( new List<Vec2> { new( 0, 0 ), new( 16, 0 ), new( 16, 16 ), new( 0, 16 ) }, SketchPlane.XY ),
				new( new List<Vec2> { new( 0, 0 ), new( 8, 0 ), new( 8, 8 ), new( 0, 8 ) }, SketchPlane.XY.Offset( 10f ) ),
			};
			var brushes = CadToMap.LoftSections( sections );
			Assert( brushes.Count == 2, $"2 triangles x 1 span must be 2 brushes, got {brushes.Count}" );
			foreach ( var brush in brushes )
				Assert( brush.IsValid, $"loft prismoid must be valid, got: {Defects( brush )}" );
			AssertNear( TotalVolume( brushes ), 10f / 3f * ( 256f + 64f + 128f ),
				10f / 3f * ( 256f + 64f + 128f ) * 0.005f, "loft volume within 0.5% of the frustum" );
		} );

		Check( "sweep a rectangle along a straight path makes the exact box", () =>
		{
			var loop = new List<Vec2> { new( 0, 0 ), new( 4, 0 ), new( 4, 2 ), new( 0, 2 ) };
			var path = new List<Vec3> { new( 0, 0, 0 ), new( 0, 0, 10 ) };
			var brushes = CadToMap.SweepLoop( loop, SketchPlane.XY, path );
			Assert( brushes.Count == 1, $"straight sweep must be 1 brush, got {brushes.Count}" );
			Assert( brushes[0].IsValid, $"sweep brush must be valid, got: {Defects( brushes[0] )}" );
			Assert( brushes[0].FaceCount == 6, $"sweep box must have 6 faces, got {brushes[0].FaceCount}" );
			AssertNear( brushes[0].Volume, 80f, 1e-3f, "sweep box volume" );
		} );

		Check( "loft with mismatched section point counts refuses with a named reason", () =>
		{
			var sections = new List<CadSection>
			{
				new( new List<Vec2> { new( 0, 0 ), new( 16, 0 ), new( 16, 16 ), new( 0, 16 ) }, SketchPlane.XY ),
				new( new List<Vec2> { new( 0, 0 ), new( 8, 0 ), new( 0, 8 ) }, SketchPlane.XY.Offset( 10f ) ),
			};
			ExpectRefusal( () => CadToMap.LoftSections( sections ),
				CadToMap.ReasonSectionPointCountMismatch, "mismatched loft sections" );
		} );

		Check( "loft with oppositely wound sections refuses with a named reason", () =>
		{
			var sections = new List<CadSection>
			{
				new( new List<Vec2> { new( 0, 0 ), new( 16, 0 ), new( 16, 16 ), new( 0, 16 ) }, SketchPlane.XY ),
				new( new List<Vec2> { new( 0, 0 ), new( 0, 8 ), new( 8, 8 ), new( 8, 0 ) }, SketchPlane.XY.Offset( 10f ) ),
			};
			ExpectRefusal( () => CadToMap.LoftSections( sections ),
				CadToMap.ReasonSectionWindingMismatch, "oppositely wound loft sections" );
		} );

		Check( "profile with a hole refuses with a named reason", () =>
		{
			var profile = new Profile();
			profile.Outer.AddRange( new[]
			{
				new Vec2( 0, 0 ), new Vec2( 16, 0 ), new Vec2( 16, 16 ), new Vec2( 0, 16 ),
			} );
			profile.Holes.Add( new List<Vec2>
			{
				new( 4, 4 ), new( 12, 4 ), new( 12, 12 ), new( 4, 12 ),
			} );
			ExpectRefusal( () => CadToMap.ExtrudeProfile( profile, SketchPlane.XY, 4f ),
				CadToMap.ReasonProfileHasHoles, "holed profile" );
		} );

		Check( "revolve profile crossing its axis refuses with a named reason", () =>
		{
			var loop = new List<Vec2>
			{
				new( -4, -4 ), new( 4, -4 ), new( 4, 4 ), new( -4, 4 ),
			};
			ExpectRefusal( () => CadToMap.RevolveLoop( loop, SketchPlane.XY,
					new Vec2( 0, 0 ), new Vec2( 1, 0 ) ),
				CadToMap.ReasonProfileCrossesAxis, "revolve across the axis" );
		} );

		Check( "zero extrude distance refuses with a named reason", () =>
		{
			ExpectRefusal( () => CadToMap.ExtrudeLoop(
					new List<Vec2> { new( 0, 0 ), new( 16, 0 ), new( 16, 8 ), new( 0, 8 ) },
					SketchPlane.XY, 0f ),
				CadToMap.ReasonZeroDistance, "zero distance" );
		} );

		Check( "self-intersecting loop refuses with a named reason", () =>
		{
			var loop = new List<Vec2>
			{
				new( 0, 0 ), new( 10, 2 ), new( 10, 0 ), new( 0, 10 ),
			};
			ExpectRefusal( () => CadToMap.ExtrudeLoop( loop, SketchPlane.XY, 4f ),
				CadToMap.ReasonSelfIntersectingProfile, "bowtie loop" );
		} );

		Check( "open sketch fails the conversion feature honestly", () =>
		{
			var studio = new PartStudio();
			var sketchFeature = studio.Add( new SketchFeature() );
			sketchFeature.Curves.Value = "line:0,0,10,0";
			var feature = studio.Add( new CadToMapFeature() );
			feature.Distance.Value = 4f;
			studio.Rebuild();
			Assert( feature.Error is not null, "open sketch must fail the conversion feature" );
			Assert( feature.Error!.Contains( "closed region", StringComparison.Ordinal ),
				$"error must name the missing closed region, got: {feature.Error}" );
			Assert( studio.Bodies.Count == 0, $"open sketch must make no bodies, got {studio.Bodies.Count}" );
		} );

		Check( "converted brushes publish into a MapDocument by value", () =>
		{
			var brushes = CadToMap.ExtrudeSketch( RectSketch( 16f, 8f ), 4f, "brick/brickwall001" );
			var doc = MapDocument.Create( "test-room" );
			var published = CadToMap.PublishToMap( doc, brushes, "Wall" );
			Assert( published.Count == 1, $"must publish 1 brush, got {published.Count}" );
			Assert( doc.Brushes.Count == 1, $"map must hold 1 brush, got {doc.Brushes.Count}" );
			Assert( !string.IsNullOrWhiteSpace( published[0].Id ), "published brush must have a map-side id" );
			Assert( doc.FindBrush( published[0].Id ) is not null, "published brush must be findable by id" );
			AssertNear( doc.FindBrush( published[0].Id )!.ToMapBrush().Volume, 512f, 1e-3f,
				"published brush volume round-trips through MapBrushData" );
			Assert( doc.IsDirty, "publishing must dirty the map document" );
		} );

		Check( "map id binding finds only the conversions bound to that map", () =>
		{
			var studio = new PartStudio();
			var a = CadToMap.AttachToMap( studio.Add( new CadToMapFeature() ), "room-a" );
			var b = CadToMap.AttachToMap( studio.Add( new CadToMapFeature() ), "room-b" );
			Assert( a.MapId.Value == "room-a" && b.MapId.Value == "room-b", "AttachToMap must set the map ids" );
			Assert( CadToMap.FeaturesForMap( studio, "room-a" ).Count == 1,
				"room-a must have exactly 1 conversion" );
			Assert( CadToMap.FeaturesForMap( studio, "room-b" ).Count == 1,
				"room-b must have exactly 1 conversion" );
			Assert( CadToMap.FeaturesForMap( studio, "no-such-map" ).Count == 0,
				"an unknown map id must match nothing" );
			Assert( CadToMap.FeaturesForMap( studio, "" ).Count == 0,
				"a blank map id must match nothing" );
			ExpectThrows<ArgumentException>( () => CadToMap.AttachToMap( new CadToMapFeature(), "" ),
				"blank map id" );

			// The DocumentId overload binds to a real document, not a typed string.
			var doc = MapDocument.Create( "room-a" );
			var c = CadToMap.AttachToMap( studio.Add( new CadToMapFeature() ), doc );
			Assert( c.MapId.Value == doc.DocumentId, "AttachToMap(doc) must store the DocumentId" );
			Assert( CadToMap.FeaturesForMap( studio, doc.DocumentId ).Count == 1,
				"the document's id must find exactly its conversion" );
		} );

		Check( "fixture cases extrude to their expected volumes", () =>
		{
			foreach ( var line in File.ReadLines( Path.Combine( FixtureDir(), "cases.txt" ) ) )
			{
				if ( string.IsNullOrWhiteSpace( line ) || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
				var parts = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
				var expected = float.Parse( parts[^2], CultureInfo.InvariantCulture );
				var tolerancePercent = float.Parse( parts[^1], CultureInfo.InvariantCulture );
				IReadOnlyList<MapBrush> brushes;
				if ( parts[0] == "rect" )
				{
					var w = float.Parse( parts[1], CultureInfo.InvariantCulture );
					var h = float.Parse( parts[2], CultureInfo.InvariantCulture );
					var d = float.Parse( parts[3], CultureInfo.InvariantCulture );
					brushes = CadToMap.ExtrudeSketch( RectSketch( w, h ), d );
				}
				else if ( parts[0] == "circle" )
				{
					var r = float.Parse( parts[1], CultureInfo.InvariantCulture );
					var d = float.Parse( parts[2], CultureInfo.InvariantCulture );
					var sketch = new Sketch();
					sketch.AddCircle( new Vec2( 0, 0 ), r );
					brushes = CadToMap.ExtrudeSketch( sketch, d );
				}
				else throw new InvalidOperationException( $"Fixture cases.txt: unknown kind '{parts[0]}'." );
				AssertNear( TotalVolume( brushes ), expected, expected * tolerancePercent / 100f,
					$"fixture case '{line}'" );
			}
		} );

		Check( "fixture roundtrip effigy rebuilds to the same box", () =>
		{
			var text = File.ReadAllText( Path.Combine( FixtureDir(), "roundtrip.effigy" ) );
			var studio = StudioDocument.Read( text );
			var feature = studio.Features.OfType<CadToMapFeature>().Single();
			Assert( feature.MapId.Value == "test-room",
				$"fixture must carry MapId test-room, got '{feature.MapId.Value}'" );
			studio.Rebuild();
			Assert( feature.Error is null, $"fixture rebuild must succeed, got error: {feature.Error}" );
			Assert( studio.Bodies.Count == 1, $"fixture rebuild must make 1 body, got {studio.Bodies.Count}" );
			var (_, max) = Bounds( studio.Bodies[0] );
			AssertVec( max, new Vec3( 16f, 8f, 4f ), 1e-4f, "fixture rebuild bounds" );
		} );
	}

	static Sketch RectSketch( float w, float h )
	{
		var sketch = new Sketch();
		sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( w, h ) );
		return sketch;
	}

	static float TotalVolume( IReadOnlyList<MapBrush> brushes ) => brushes.Sum( b => b.Volume );

	static (Vec3 Min, Vec3 Max) Bounds( Body body )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in body.Mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		return (min, max);
	}

	static string FixtureDir()
	{
		var leaf = Path.Combine( "tests", "fixtures", "hammer",
			"hammer-interop-cad-feature-tree-brushes" );
		foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
		{
			var dir = new DirectoryInfo( start );
			while ( dir is not null )
			{
				var candidate = Path.Combine( dir.FullName, leaf );
				if ( Directory.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
		}
		var env = Environment.GetEnvironmentVariable( "CADTOMAP_FIXTURES" );
		if ( !string.IsNullOrEmpty( env ) && Directory.Exists( env ) ) return env;
		throw new InvalidOperationException(
			"Cannot find the hammer-interop-cad-feature-tree-brushes fixture directory." );
	}

	static string Defects( MapBrush brush ) =>
		string.Join( "; ", brush.Validate().Select( d => d.ToString() ) );

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}

	static void AssertNear( float actual, float expected, float tolerance, string what )
	{
		if ( !float.IsFinite( actual ) || MathF.Abs( actual - expected ) > tolerance )
			throw new Exception( $"{what}: expected {expected} (+/- {tolerance}), got {actual}" );
	}

	static void AssertVec( Vec3 actual, Vec3 expected, float tolerance, string what )
	{
		if ( ( actual - expected ).Length > tolerance )
			throw new Exception( $"{what}: expected {expected} (+/- {tolerance}), got {actual}" );
	}

	static T ExpectThrows<T>( Action action, string what ) where T : Exception
	{
		try
		{
			action();
		}
		catch ( T ex )
		{
			return ex;
		}
		catch ( Exception ex )
		{
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" );
		}
		throw new Exception( $"{what}: expected {typeof( T ).Name}, nothing was thrown." );
	}

	static void ExpectRefusal( Action action, string reason, string what )
	{
		var ex = ExpectThrows<CadToMapException>( action, what );
		Assert( ex.Reason == reason,
			$"{what}: expected reason {reason}, got {ex.Reason} ({ex.Message})" );
	}
}
