#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

// Hammer Desktop P1: plane-based convex brush core + validity checker.
// Board: hammer-brush-core-plane-based-brushes. Group name 'mapbrush'.
//
// Fixtures live in tests/fixtures/hammer/hammer-brush-core-plane-based-brushes/,
// one brush per file in the little line format its README.md documents:
//   face nx ny nz d material uox uoy usx usy rot lightmap smoothing
//   loop x y z x y z ...
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
// final integrator adds the one MapBrushTests.Register(registry, settings); line.
internal static class MapBrushTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "mapbrush", "box from 6 planes has volume 24 and centroid at origin", () =>
		{
			var brush = BoxBrush();
			Assert( brush.IsValid, $"box must be valid, got: {Defects( brush )}" );
			AssertNear( brush.Volume, 24f, 1e-3f, "box volume" );
			AssertVec( brush.Centroid, Vec3.Zero, 1e-4f, "box centroid" );
			Assert( brush.FaceCount == 6, $"box must have 6 faces, got {brush.FaceCount}" );
		} );

		registry.Add( "mapbrush", "box from 6 planes has 8 vertices and 6 quad faces", () =>
		{
			var brush = BoxBrush();
			Assert( brush.Vertices.Count == 8, $"box must have 8 vertices, got {brush.Vertices.Count}" );
			for ( var i = 0; i < 6; i++ )
				Assert( brush.FaceVertices( i ).Count == 4, $"face {i} must be a quad, got {brush.FaceVertices( i ).Count} vertices" );
		} );

		registry.Add( "mapbrush", "wedge from 5 planes has volume 4 and centroid (0,0,2/3)", () =>
		{
			var brush = WedgeBrush();
			Assert( brush.IsValid, $"wedge must be valid, got: {Defects( brush )}" );
			AssertNear( brush.Volume, 4f, 1e-3f, "wedge volume" );
			AssertVec( brush.Centroid, new Vec3( 0f, 0f, 2f / 3f ), 1e-4f, "wedge centroid" );
			Assert( brush.FaceCount == 5, $"wedge must have 5 faces, got {brush.FaceCount}" );
			Assert( brush.Vertices.Count == 6, $"wedge must have 6 vertices, got {brush.Vertices.Count}" );
		} );

		registry.Add( "mapbrush", "box from 8 vertices via hull has volume 24", () =>
		{
			var brush = MapBrush.FromVertices( new[]
			{
				new Vec3( -1, -1.5f, -2 ), new Vec3( 1, -1.5f, -2 ),
				new Vec3( 1, 1.5f, -2 ), new Vec3( -1, 1.5f, -2 ),
				new Vec3( -1, -1.5f, 2 ), new Vec3( 1, -1.5f, 2 ),
				new Vec3( 1, 1.5f, 2 ), new Vec3( -1, 1.5f, 2 ),
			} );
			Assert( brush.IsValid, $"hull box must be valid, got: {Defects( brush )}" );
			AssertNear( brush.Volume, 24f, 1e-3f, "hull box volume" );
			Assert( brush.FaceCount == 6, $"hull box must have 6 faces, got {brush.FaceCount}" );
		} );

		registry.Add( "mapbrush", "wedge from 6 vertices via hull has volume 4", () =>
		{
			var brush = MapBrush.FromVertices( new[]
			{
				new Vec3( -1, -1, 0 ), new Vec3( 1, -1, 0 ), new Vec3( 0, -1, 2 ),
				new Vec3( -1, 1, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 2 ),
			} );
			Assert( brush.IsValid, $"hull wedge must be valid, got: {Defects( brush )}" );
			AssertNear( brush.Volume, 4f, 1e-3f, "hull wedge volume" );
			Assert( brush.FaceCount == 5, $"hull wedge must have 5 faces, got {brush.FaceCount}" );
		} );

		registry.Add( "mapbrush", "flat box is invalid with ZeroVolume", () =>
		{
			var brush = FlatBoxBrush();
			Assert( !brush.IsValid, "flat box must be invalid" );
			AssertNear( brush.Volume, 0f, 1e-6f, "flat box volume" );
			AssertHasCode( brush, MapBrushDefect.ZeroVolume );
		} );

		registry.Add( "mapbrush", "twisted face loop is invalid with NonPlanarFace, keeps volume", () =>
		{
			var brush = MapBrush.FromFaceVertices( BoxLoops( twistedTop: true ) );
			Assert( !brush.IsValid, "twisted brush must be invalid" );
			AssertHasCode( brush, MapBrushDefect.NonPlanarFace );
			AssertHasNoCode( brush, MapBrushDefect.ZeroVolume );
			AssertNear( brush.Volume, 24f, 1e-3f, "twisted brush still encloses the box volume" );
		} );

		registry.Add( "mapbrush", "concave face loop is invalid with ConcaveInput", () =>
		{
			var loops = BoxLoops();
			loops[1] = new List<Vec3>
			{
				new( -1, -1.5f, 2 ), new( 1, -1.5f, 2 ), new( 1, 1.5f, 2 ),
				new( 0, 0, 2 ), new( -1, 1.5f, 2 ),
			};
			var brush = MapBrush.FromFaceVertices( loops );
			Assert( !brush.IsValid, "concave-loop brush must be invalid" );
			AssertHasCode( brush, MapBrushDefect.ConcaveInput );
		} );

		registry.Add( "mapbrush", "duplicate plane is invalid with DuplicateFace", () =>
		{
			var faces = BoxBrush().Faces.ToList();
			faces.Add( faces[1] );
			var brush = MapBrush.FromPlanes( faces );
			Assert( !brush.IsValid, "duplicated-plane brush must be invalid" );
			AssertHasCode( brush, MapBrushDefect.DuplicateFace );
			Assert( brush.Validate().Count == 1, $"expected exactly one defect, got: {Defects( brush )}" );
			AssertNear( brush.Volume, 24f, 1e-3f, "duplicated plane must not double-count volume" );
		} );

		registry.Add( "mapbrush", "coplanar split loops are invalid with CoplanarSplitFace", () =>
		{
			var loops = BoxLoops();
			loops[1] = new List<Vec3>
			{
				new( -1, -1.5f, 2 ), new( 0, -1.5f, 2 ), new( 0, 1.5f, 2 ), new( -1, 1.5f, 2 ),
			};
			loops.Add( new List<Vec3>
			{
				new( 0, -1.5f, 2 ), new( 1, -1.5f, 2 ), new( 1, 1.5f, 2 ), new( 0, 1.5f, 2 ),
			} );
			var brush = MapBrush.FromFaceVertices( loops );
			Assert( !brush.IsValid, "split-face brush must be invalid" );
			AssertHasCode( brush, MapBrushDefect.CoplanarSplitFace );
			Assert( brush.Validate().Count == 1, $"expected exactly one defect, got: {Defects( brush )}" );
		} );

		registry.Add( "mapbrush", "too few planes is invalid with TooFewFaces", () =>
		{
			var brush = MapBrush.FromPlanes( new[]
			{
				new MapBrushFace( new Vec3( 1, 0, 0 ), 1f ),
				new MapBrushFace( new Vec3( 0, 1, 0 ), 1f ),
				new MapBrushFace( new Vec3( 0, 0, 1 ), 1f ),
			} );
			Assert( !brush.IsValid, "3-plane brush must be invalid" );
			AssertHasCode( brush, MapBrushDefect.TooFewFaces );
		} );

		registry.Add( "mapbrush", "zero normal is invalid with DegeneratePlane", () =>
		{
			var faces = BoxBrush().Faces.ToList();
			faces[1] = new MapBrushFace( Vec3.Zero, 1f, faces[1].Material );
			var brush = MapBrush.FromPlanes( faces );
			Assert( !brush.IsValid, "zero-normal brush must be invalid" );
			AssertHasCode( brush, MapBrushDefect.DegeneratePlane );
		} );

		registry.Add( "mapbrush", "bad face UVs are named defects", () =>
		{
			var faces = BoxBrush().Faces.ToList();
			faces[0] = new MapBrushFace( faces[0].Normal, faces[0].Distance,
				new FaceMaterial( "brick/brickwall", new FaceUv( Vec2.Zero, new Vec2( 0f, 1f ), 0f, 16f, 0 ) ) );
			faces[1] = new MapBrushFace( faces[1].Normal, faces[1].Distance,
				new FaceMaterial( "brick/brickwall", new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 0f, 0 ) ) );
			faces[2] = new MapBrushFace( faces[2].Normal, faces[2].Distance,
				new FaceMaterial( "brick/brickwall", new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 16f, -1 ) ) );
			var brush = MapBrush.FromPlanes( faces );
			Assert( !brush.IsValid, "bad-UV brush must be invalid" );
			AssertHasCode( brush, MapBrushDefect.DegenerateUvScale );
			AssertHasCode( brush, MapBrushDefect.DegenerateLightmapScale );
			AssertHasCode( brush, MapBrushDefect.InvalidSmoothingGroup );
		} );

		registry.Add( "mapbrush", "face UVs follow FaceUv offset scale rotation", () =>
		{
			var brush = BoxBrush();
			// +Z face (index 5): offset (0.5, 0.25), scale (2, 2), rotation 0. Polygon
			// order is the angle-ascending order from FaceUv.PlaneAxes: (-1,-1,2),
			// (1,-1,2), (1,1,2), (-1,1,2). ComputeUvs projects relative to the first
			// vertex, then FaceUv.Transform multiplies by scale and adds the offset.
			var uvs = brush.FaceUvs( 5 );
			Assert( uvs.Length == 4, $"top face must have 4 UVs, got {uvs.Length}" );
			AssertVec2( uvs[0], new Vec2( 0.5f, 0.25f ), 1e-4f, "top uv 0" );
			AssertVec2( uvs[1], new Vec2( 4.5f, 0.25f ), 1e-4f, "top uv 1" );
			AssertVec2( uvs[2], new Vec2( 4.5f, 6.25f ), 1e-4f, "top uv 2" );
			AssertVec2( uvs[3], new Vec2( 0.5f, 6.25f ), 1e-4f, "top uv 3" );

			// Same face with rotation 90, unit scale, no offset: R90(u,v) = (-v, u).
			var rotated = MapBrush.FromPlanes( new[]
			{
				new MapBrushFace( new Vec3( 0, 0, 1 ), 2f, "metal/metalwall",
					new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 90f, 16f, 0 ) ),
				new MapBrushFace( new Vec3( 0, 0, -1 ), 2f ),
				new MapBrushFace( new Vec3( -1, 0, 0 ), 1f ),
				new MapBrushFace( new Vec3( 1, 0, 0 ), 1f ),
				new MapBrushFace( new Vec3( 0, -1, 0 ), 1.5f ),
				new MapBrushFace( new Vec3( 0, 1, 0 ), 1.5f ),
			} );
			var ruvs = rotated.FaceUvs( 0 );
			AssertVec2( ruvs[0], new Vec2( 0f, 0f ), 1e-4f, "rotated uv 0" );
			AssertVec2( ruvs[1], new Vec2( 0f, 2f ), 1e-4f, "rotated uv 1" );
			AssertVec2( ruvs[2], new Vec2( -3f, 2f ), 1e-4f, "rotated uv 2" );
			AssertVec2( ruvs[3], new Vec2( -3f, 0f ), 1e-4f, "rotated uv 3" );
		} );

		registry.Add( "mapbrush", "nodraw face is kept in brush but dropped from baked mesh", () =>
		{
			var brush = BoxBrush();
			Assert( brush.IsValid, $"box must be valid, got: {Defects( brush )}" );
			Assert( brush.FaceCount == 6, "brush keeps all 6 faces" );
			var mesh = brush.ToMesh( _ => 0 );
			Assert( mesh.FaceCount == 5, $"baked mesh must drop the nodraw face, got {mesh.FaceCount} faces" );
			Assert( mesh.VertexCount == 8, "baked mesh still references all 8 vertices" );
		} );

		registry.Add( "mapbrush", "box fixture parses and validates", () =>
		{
			var (faces, _) = ParseFixture( "box-6-planes.txt" );
			var brush = MapBrush.FromPlanes( faces );
			Assert( brush.IsValid, $"box fixture must be valid, got: {Defects( brush )}" );
			AssertNear( brush.Volume, 24f, 1e-3f, "box fixture volume" );
			Assert( brush.FaceCount == 6, $"box fixture must have 6 faces, got {brush.FaceCount}" );
		} );

		registry.Add( "mapbrush", "wedge fixture parses and validates", () =>
		{
			var (faces, _) = ParseFixture( "wedge-5-planes.txt" );
			var brush = MapBrush.FromPlanes( faces );
			Assert( brush.IsValid, $"wedge fixture must be valid, got: {Defects( brush )}" );
			AssertNear( brush.Volume, 4f, 1e-3f, "wedge fixture volume" );
			AssertVec( brush.Centroid, new Vec3( 0f, 0f, 2f / 3f ), 1e-3f, "wedge fixture centroid" );
		} );

		registry.Add( "mapbrush", "zero-volume fixture is rejected with ZeroVolume", () =>
		{
			var (faces, _) = ParseFixture( "zero-volume-flat-box.txt" );
			var brush = MapBrush.FromPlanes( faces );
			Assert( !brush.IsValid, "zero-volume fixture must be invalid" );
			AssertHasCode( brush, MapBrushDefect.ZeroVolume );
		} );

		registry.Add( "mapbrush", "twisted-loop fixture is rejected with NonPlanarFace", () =>
		{
			var (_, loops) = ParseFixture( "twisted-face-loops.txt" );
			var brush = MapBrush.FromFaceVertices( loops );
			Assert( !brush.IsValid, "twisted fixture must be invalid" );
			AssertHasCode( brush, MapBrushDefect.NonPlanarFace );
			AssertHasNoCode( brush, MapBrushDefect.ZeroVolume );
		} );

		registry.Add( "mapbrush", "duplicate-plane fixture is rejected with DuplicateFace", () =>
		{
			var (faces, _) = ParseFixture( "duplicate-plane.txt" );
			var brush = MapBrush.FromPlanes( faces );
			Assert( !brush.IsValid, "duplicate fixture must be invalid" );
			AssertHasCode( brush, MapBrushDefect.DuplicateFace );
		} );

		registry.Add( "mapbrush", "feature registry finds Map Brush", () =>
		{
			FeatureRegistry.Scan();
			var found = FeatureRegistry.Find( "Map Brush" );
			Assert( found is not null, "FeatureRegistry must find 'Map Brush'" );
			Assert( found!.Type == typeof( MapBrushFeature ), $"registry type must be MapBrushFeature, got {found.Type.Name}" );
			Assert( found.Category == "Map", $"registry category must be Map, got {found.Category}" );
		} );

		registry.Add( "mapbrush", "feature round-trips through StudioDocument with identical planes and UVs", () =>
		{
			var studio = new PartStudio();
			var feature = studio.Add( new MapBrushFeature() );
			var before = BoxBrush();
			feature.SetBrush( before );

			var text = StudioDocument.Write( studio );
			var loaded = StudioDocument.Read( text );
			Assert( loaded.Features.Count == 1, $"reloaded studio must have 1 feature, got {loaded.Features.Count}" );
			var back = (MapBrushFeature)loaded.Features[0];
			var after = back.Brush;

			Assert( after.FaceCount == before.FaceCount, $"face count must survive, got {after.FaceCount}" );
			for ( var i = 0; i < before.FaceCount; i++ )
			{
				var a = before.Faces[i];
				var b = after.Faces[i];
				AssertVec( b.Normal, a.Normal, 1e-5f, $"face {i} normal" );
				AssertNear( b.Distance, a.Distance, 1e-5f, $"face {i} distance" );
				Assert( b.MaterialId == a.MaterialId, $"face {i} material must survive: '{b.MaterialId}' vs '{a.MaterialId}'" );
				AssertVec2( b.Uv.Offset, a.Uv.Offset, 1e-5f, $"face {i} uv offset" );
				AssertVec2( b.Uv.Scale, a.Uv.Scale, 1e-5f, $"face {i} uv scale" );
				AssertNear( b.Uv.RotationDegrees, a.Uv.RotationDegrees, 1e-5f, $"face {i} uv rotation" );
				AssertNear( b.Uv.LightmapScale, a.Uv.LightmapScale, 1e-5f, $"face {i} lightmap scale" );
				Assert( b.Uv.SmoothingGroup == a.Uv.SmoothingGroup, $"face {i} smoothing group must survive" );
			}
		} );

		registry.Add( "mapbrush", "feature rebuild produces one body with volume 24", () =>
		{
			var studio = new PartStudio();
			var feature = studio.Add( new MapBrushFeature() );
			feature.SetBrush( MapBrush.Box( new Vec3( -1, -1.5f, -2 ), new Vec3( 1, 1.5f, 2 ), "brick/brickwall" ) );
			studio.Rebuild();
			Assert( feature.Error is null, $"feature must build clean, got error: {feature.Error}" );
			Assert( studio.Bodies.Count == 1, $"expected 1 body, got {studio.Bodies.Count}" );
			var mesh = studio.Bodies[0].Mesh;
			AssertNear( mesh.SignedVolume(), 24f, 1e-3f, "rebuilt mesh volume" );
			Assert( mesh.FaceCount == 6, $"rebuilt mesh must have 6 faces, got {mesh.FaceCount}" );
			Assert( mesh.VertexCount == 8, $"rebuilt mesh must have 8 vertices, got {mesh.VertexCount}" );
		} );

		registry.Add( "mapbrush", "feature rebuild of invalid brush fails with named error and no body", () =>
		{
			var studio = new PartStudio();
			var feature = studio.Add( new MapBrushFeature() );
			feature.SetBrush( FlatBoxBrush() );
			studio.Rebuild();
			Assert( studio.Bodies.Count == 0, $"invalid brush must produce no body, got {studio.Bodies.Count}" );
			Assert( feature.Error is not null, "invalid brush must set the feature error" );
			Assert( feature.Diagnostic is not null, "invalid brush must set the feature diagnostic" );
			Assert( feature.Diagnostic!.Cause is not null && feature.Diagnostic.Cause.Contains( MapBrushDefect.ZeroVolume ),
				$"diagnostic cause must name ZeroVolume, got: {feature.Diagnostic.Cause}" );
		} );

		registry.Add( "mapbrush", "material slots follow first-appearance order", () =>
		{
			var feature = new MapBrushFeature();
			feature.SetBrush( BoxBrush() );
			Assert( feature.MaterialSlot( "brick/brickwall" ) == 0, "brick must be slot 0" );
			Assert( feature.MaterialSlot( "concrete/concretefloor" ) == 1, "concrete must be slot 1" );
			Assert( feature.MaterialSlot( "tools/toolsnodraw" ) == 2, "nodraw must be slot 2" );
			Assert( feature.MaterialSlot( "metal/metalwall" ) == 3, "metal must be slot 3" );
			Assert( feature.MaterialSlot( "glass/glass" ) == 4, "an unseen material must get the next slot" );
		} );
	}

	// 2 x 3 x 4 box centred on the origin, distinct material/UV per face so the
	// round-trip test has something to distinguish. Face order: -X, +X, -Y, +Y, -Z, +Z.
	// The -Z face is nodraw: the nodraw test relies on it.
	static MapBrush BoxBrush() => MapBrush.FromPlanes( new[]
	{
		new MapBrushFace( new Vec3( -1, 0, 0 ), 1f, "brick/brickwall",
			new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 16f, 0 ) ),
		new MapBrushFace( new Vec3( 1, 0, 0 ), 1f, "brick/brickwall",
			new FaceUv( new Vec2( 0.5f, 0f ), new Vec2( 2f, 1f ), 0f, 32f, 1 ) ),
		new MapBrushFace( new Vec3( 0, -1, 0 ), 1.5f, "concrete/concretefloor",
			new FaceUv( new Vec2( 0f, 0.25f ), new Vec2( 1f, 2f ), 30f, 16f, 0 ) ),
		new MapBrushFace( new Vec3( 0, 1, 0 ), 1.5f, "concrete/concretefloor",
			new FaceUv( new Vec2( 0.25f, 0.5f ), new Vec2( 1f, 1f ), 45f, 8f, 2 ) ),
		new MapBrushFace( new Vec3( 0, 0, -1 ), 2f, "tools/toolsnodraw", FaceUv.Default ),
		new MapBrushFace( new Vec3( 0, 0, 1 ), 2f, "metal/metalwall",
			new FaceUv( new Vec2( 0.5f, 0.25f ), new Vec2( 2f, 2f ), 0f, 16f, 0 ) ),
	} );

	// Triangular prism: triangle (-1,0),(1,0),(0,2) in XZ, extruded y in [-1, 1].
	// Volume = (2*2/2) * 2 = 4, centroid (0, 0, 2/3).
	static MapBrush WedgeBrush()
	{
		var s = MathF.Sqrt( 5f );
		return MapBrush.FromPlanes( new[]
		{
			new MapBrushFace( new Vec3( 0, 0, -1 ), 0f, "brick/brickwall" ),
			new MapBrushFace( new Vec3( -2f / s, 0f, 1f / s ), 2f / s, "brick/brickwall" ),
			new MapBrushFace( new Vec3( 2f / s, 0f, 1f / s ), 2f / s, "brick/brickwall" ),
			new MapBrushFace( new Vec3( 0, -1, 0 ), 1f, "brick/brickwall" ),
			new MapBrushFace( new Vec3( 0, 1, 0 ), 1f, "brick/brickwall" ),
		} );
	}

	// Same box as BoxBrush but squashed flat: top and bottom are both z = 0.
	static MapBrush FlatBoxBrush() => MapBrush.FromPlanes( new[]
	{
		new MapBrushFace( new Vec3( -1, 0, 0 ), 1f ),
		new MapBrushFace( new Vec3( 1, 0, 0 ), 1f ),
		new MapBrushFace( new Vec3( 0, -1, 0 ), 1.5f ),
		new MapBrushFace( new Vec3( 0, 1, 0 ), 1.5f ),
		new MapBrushFace( new Vec3( 0, 0, -1 ), 0f ),
		new MapBrushFace( new Vec3( 0, 0, 1 ), 0f ),
	} );

	// The 6 face loops of the 2 x 3 x 4 box, in the same order as BoxBrush's faces:
	// bottom, top, -X, +X, -Y, +Y. twistedTop lifts the top loop's last vertex 0.5
	// out of its plane.
	static List<List<Vec3>> BoxLoops( bool twistedTop = false )
	{
		var topLast = twistedTop ? new Vec3( -1, 1.5f, 2.5f ) : new Vec3( -1, 1.5f, 2f );
		return new List<List<Vec3>>
		{
			new() { new( -1, -1.5f, -2 ), new( 1, -1.5f, -2 ), new( 1, 1.5f, -2 ), new( -1, 1.5f, -2 ) },
			new() { new( -1, -1.5f, 2 ), new( 1, -1.5f, 2 ), new( 1, 1.5f, 2 ), topLast },
			new() { new( -1, -1.5f, -2 ), new( -1, 1.5f, -2 ), new( -1, 1.5f, 2 ), new( -1, -1.5f, 2 ) },
			new() { new( 1, -1.5f, -2 ), new( 1, 1.5f, -2 ), new( 1, 1.5f, 2 ), new( 1, -1.5f, 2 ) },
			new() { new( -1, -1.5f, -2 ), new( 1, -1.5f, -2 ), new( 1, -1.5f, 2 ), new( -1, -1.5f, 2 ) },
			new() { new( -1, 1.5f, -2 ), new( 1, 1.5f, -2 ), new( 1, 1.5f, 2 ), new( -1, 1.5f, 2 ) },
		};
	}

	static string FixtureDir()
	{
		var dir = new DirectoryInfo( AppContext.BaseDirectory );
		while ( dir is not null )
		{
			var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer",
				"hammer-brush-core-plane-based-brushes" );
			if ( Directory.Exists( candidate ) ) return candidate;
			dir = dir.Parent;
		}
		throw new InvalidOperationException( "Cannot find the hammer-brush-core fixture directory." );
	}

	// Fixture format (see the fixtures' README.md):
	//   face nx ny nz d material uox uoy usx usy rot lightmap smoothing
	//   loop x y z x y z ...
	static (List<MapBrushFace> Faces, List<List<Vec3>> Loops) ParseFixture( string name )
	{
		var faces = new List<MapBrushFace>();
		var loops = new List<List<Vec3>>();
		foreach ( var raw in File.ReadLines( Path.Combine( FixtureDir(), name ) ) )
		{
			var line = raw.Trim();
			if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
			var parts = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
			if ( parts[0] == "face" && parts.Length == 13 )
			{
				faces.Add( new MapBrushFace(
					new Vec3( Parse( parts[1] ), Parse( parts[2] ), Parse( parts[3] ) ),
					Parse( parts[4] ),
					parts[5],
					new FaceUv(
						new Vec2( Parse( parts[6] ), Parse( parts[7] ) ),
						new Vec2( Parse( parts[8] ), Parse( parts[9] ) ),
						Parse( parts[10] ), Parse( parts[11] ), int.Parse( parts[12] ) ) ) );
			}
			else if ( parts[0] == "loop" && parts.Length >= 10 && (parts.Length - 1) % 3 == 0 )
			{
				var loop = new List<Vec3>();
				for ( var i = 1; i + 2 < parts.Length; i += 3 )
					loop.Add( new Vec3( Parse( parts[i] ), Parse( parts[i + 1] ), Parse( parts[i + 2] ) ) );
				loops.Add( loop );
			}
			else
			{
				throw new InvalidOperationException( $"Fixture {name}: cannot parse line: {line}" );
			}
		}
		return (faces, loops);

		static float Parse( string text ) => float.Parse( text, System.Globalization.CultureInfo.InvariantCulture );
	}

	static string Defects( MapBrush brush ) =>
		string.Join( "; ", brush.Validate().Select( d => d.ToString() ) );

	static void AssertHasCode( MapBrush brush, string code )
	{
		var defects = brush.Validate();
		Assert( defects.Any( d => d.Code == code ),
			$"expected defect {code}, got: {string.Join( "; ", defects.Select( d => d.ToString() ) )}" );
	}

	static void AssertHasNoCode( MapBrush brush, string code )
	{
		var defects = brush.Validate();
		Assert( defects.All( d => d.Code != code ),
			$"did not expect defect {code}, got: {string.Join( "; ", defects.Select( d => d.ToString() ) )}" );
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new InvalidOperationException( "Assertion failed: " + message );
	}

	static void AssertNear( float actual, float expected, float tolerance, string what )
	{
		if ( MathF.Abs( actual - expected ) > tolerance )
			throw new InvalidOperationException( $"Assertion failed: {what} is {actual}, expected {expected} (+/- {tolerance})" );
	}

	static void AssertVec( Vec3 actual, Vec3 expected, float tolerance, string what )
	{
		if ( !actual.AlmostEquals( expected, tolerance ) )
			throw new InvalidOperationException( $"Assertion failed: {what} is {actual}, expected {expected} (+/- {tolerance})" );
	}

	static void AssertVec2( Vec2 actual, Vec2 expected, float tolerance, string what )
	{
		if ( MathF.Abs( actual.x - expected.x ) > tolerance || MathF.Abs( actual.y - expected.y ) > tolerance )
			throw new InvalidOperationException( $"Assertion failed: {what} is {actual}, expected {expected} (+/- {tolerance})" );
	}
}
