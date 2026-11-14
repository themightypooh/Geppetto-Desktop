#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

// Hammer Desktop P3: mesh-based geometry mode (extrude/bevel/inset/bridge,
// export back to convex brushes). Board: hammer-mesh-geometry-mode.
// Group name 'meshmode'.
//
// Fixtures live in tests/fixtures/hammer/hammer-mesh-geometry-mode/, one mesh
// per file in the line format MeshMode.ParseMesh documents (see its README.md):
//   v x y z
//   f i j k ... [materialSlot]
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned
// and the final integrator adds the one MeshModeTests.Register(registry, settings);
// line — the same arrangement as MapBrushTests / VmfIoTests before it.
internal static class MeshModeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "meshmode", "box fixture loads as a valid closed mesh with volume 24", () =>
		{
			var mesh = LoadFixture( "box.mesh" );
			Assert( MeshMode.IsValid( mesh ), $"box fixture must be valid, got: {Defects( mesh )}" );
			Assert( MeshMode.IsClosed( mesh ), "box fixture must be closed" );
			AssertNear( MeshMode.Volume( mesh ), 24f, 1e-3f, "box volume" );
			Assert( mesh.VertexCount == 8, $"box must have 8 vertices, got {mesh.VertexCount}" );
			Assert( mesh.FaceCount == 6, $"box must have 6 faces, got {mesh.FaceCount}" );
		} );

		registry.Add( "meshmode", "lshape fixture loads as a valid closed mesh with volume 24", () =>
		{
			var mesh = LoadFixture( "lshape.mesh" );
			Assert( MeshMode.IsValid( mesh ), $"L fixture must be valid, got: {Defects( mesh )}" );
			Assert( MeshMode.IsClosed( mesh ), "L fixture must be closed" );
			AssertNear( MeshMode.Volume( mesh ), 24f, 1e-3f, "L volume" );
			Assert( mesh.VertexCount == 12, $"L must have 12 vertices, got {mesh.VertexCount}" );
			Assert( mesh.FaceCount == 8, $"L must have 8 faces, got {mesh.FaceCount}" );
		} );

		registry.Add( "meshmode", "extrude top face by 1 grows the box to volume 30 with 10 faces", () =>
		{
			var mesh = BoxMesh();
			var result = MeshMode.Extrude( mesh, MeshSelection.Face( 1 ), 1f );
			Assert( MeshMode.IsValid( result ), $"extruded box must be valid, got: {Defects( result )}" );
			AssertNear( MeshMode.Volume( result ), 30f, 1e-3f, "extruded volume" );
			Assert( result.FaceCount == 10, $"extruded box must have 10 faces, got {result.FaceCount}" );
		} );

		registry.Add( "meshmode", "extrude does not mutate the input mesh", () =>
		{
			var mesh = BoxMesh();
			_ = MeshMode.Extrude( mesh, MeshSelection.Face( 1 ), 1f );
			AssertNear( MeshMode.Volume( mesh ), 24f, 1e-3f, "input volume after extrude" );
			Assert( mesh.FaceCount == 6, $"input must still have 6 faces, got {mesh.FaceCount}" );
		} );

		registry.Add( "meshmode", "extrude with zero distance refuses", () =>
		{
			AssertRefuses( () => MeshMode.Extrude( BoxMesh(), MeshSelection.Face( 1 ), 0f ), "non-zero distance" );
		} );

		registry.Add( "meshmode", "extrude inward by 1 shrinks the box to volume 18", () =>
		{
			var result = MeshMode.Extrude( BoxMesh(), MeshSelection.Face( 1 ), -1f );
			Assert( MeshMode.IsValid( result ), $"inward extrude must be valid, got: {Defects( result )}" );
			AssertNear( MeshMode.Volume( result ), 18f, 1e-3f, "inward extruded volume" );
		} );

		registry.Add( "meshmode", "extrude along an explicit direction matches the face normal", () =>
		{
			var result = MeshMode.Extrude( BoxMesh(), MeshSelection.Face( 1 ), 1f, new Vec3( 0, 0, 1 ) );
			AssertNear( MeshMode.Volume( result ), 30f, 1e-3f, "direction extruded volume" );
		} );

		registry.Add( "meshmode", "bevel a box edge with width 0.5 leaves a valid closed mesh at volume 23.75", () =>
		{
			// Top-front edge (4,5), length 2: the chamfer cuts a right triangle
			// of legs 0.5 (area 0.125) along the whole edge: 24 - 0.25 = 23.75.
			var result = MeshMode.Bevel( BoxMesh(), new EdgeKey( 4, 5 ), 0.5f );
			Assert( MeshMode.IsValid( result ), $"bevelled box must be valid, got: {Defects( result )}" );
			Assert( MeshMode.IsClosed( result ), "bevelled box must be closed" );
			AssertNear( MeshMode.Volume( result ), 23.75f, 1e-3f, "bevelled volume" );
			Assert( result.FaceCount == 7, $"bevelled box must have 7 faces, got {result.FaceCount}" );
		} );

		registry.Add( "meshmode", "bevel with 2 segments rounds the edge and removes less volume", () =>
		{
			var result = MeshMode.Bevel( BoxMesh(), new EdgeKey( 4, 5 ), 0.5f, segments: 2 );
			Assert( MeshMode.IsValid( result ), $"rounded bevel must be valid, got: {Defects( result )}" );
			Assert( result.FaceCount == 8, $"rounded bevel must have 8 faces, got {result.FaceCount}" );
			var volume = MeshMode.Volume( result );
			Assert( volume > 23.75f && volume < 24f,
				$"rounded bevel must remove less than the chamfer (23.75) and something (24), got {volume:0.###}" );
		} );

		registry.Add( "meshmode", "bevel with a width past the neighbouring vertex refuses", () =>
		{
			// The neighbouring vertices along the top face are 3 away from the
			// edge endpoints, so 3.5 reaches past them. (2 does NOT: the edge
			// itself is 2 long but the bevel fits fine at that width — an
			// earlier version of this test used 2 and was wrong about the box.)
			AssertRefuses( () => MeshMode.Bevel( BoxMesh(), new EdgeKey( 4, 5 ), 3.5f ), "reaches past the neighbouring vertex" );
		} );

		registry.Add( "meshmode", "bevel a boundary edge refuses", () =>
		{
			var open = BoxMesh();
			open.Faces.RemoveAt( 1 ); // top face gone: edge (4,5) now has 1 face
			AssertRefuses( () => MeshMode.Bevel( open, new EdgeKey( 4, 5 ), 0.5f ), "exactly 2" );
		} );

		registry.Add( "meshmode", "inset top face by 0.5 keeps volume 24 and shrinks the face to area 3.13", () =>
		{
			var result = MeshMode.Inset( BoxMesh(), MeshSelection.Face( 1 ), 0.5f );
			Assert( MeshMode.IsValid( result ), $"inset box must be valid, got: {Defects( result )}" );
			AssertNear( MeshMode.Volume( result ), 24f, 1e-3f, "inset volume (frame is coplanar, volume unchanged)" );
			Assert( result.FaceCount == 10, $"inset box must have 10 faces, got {result.FaceCount}" );
			var insetArea = -1f;
			for ( var i = 0; i < result.FaceCount; i++ )
			{
				var area = MeshMode.FaceArea( result, i );
				if ( area > 3f && area < 3.3f ) insetArea = area;
			}
			Assert( insetArea > 0f, "inset box must have one face of area ~3.13 (the inset face)" );
			AssertNear( insetArea, 3.133f, 0.02f, "inset face area" );
		} );

		registry.Add( "meshmode", "inset with an amount past the face centre refuses", () =>
		{
			AssertRefuses( () => MeshMode.Inset( BoxMesh(), MeshSelection.Face( 1 ), 99f ), "reaches the centre" );
		} );

		registry.Add( "meshmode", "inset two faces sharing an edge refuses", () =>
		{
			AssertRefuses( () => MeshMode.Inset( BoxMesh(), MeshSelection.FacesOf( new[] { 1, 2 } ), 0.5f ), "share an edge" );
		} );

		registry.Add( "meshmode", "bridge the two stacked boxes into one solid of volume 60", () =>
		{
			var mesh = LoadFixture( "twobox.mesh" );
			var result = MeshMode.Bridge( mesh, 1, 6 );
			Assert( MeshMode.IsValid( result ), $"bridged boxes must be valid, got: {Defects( result )}" );
			Assert( MeshMode.IsClosed( result ), "bridged boxes must be closed" );
			AssertNear( MeshMode.Volume( result ), 60f, 1e-3f, "bridged volume (24 + 24 + tube 12)" );
			Assert( result.FaceCount == 14, $"bridged mesh must have 14 faces, got {result.FaceCount}" );
		} );

		registry.Add( "meshmode", "bridge loops with different corner counts refuses", () =>
		{
			var mesh = LoadFixture( "lshape.mesh" );
			AssertRefuses( () => MeshMode.Bridge( mesh, 1, 2 ), "same corner count" );
		} );

		registry.Add( "meshmode", "export a convex box to one exact brush", () =>
		{
			var result = MeshMode.ExportToBrushes( BoxMesh() );
			Assert( result.Report.Exact, $"box export must be exact, got: {result.Report}" );
			Assert( result.Brushes.Count == 1, $"box export must yield 1 brush, got {result.Brushes.Count}" );
			Assert( result.Brushes[0].IsValid, $"exported brush must be valid, got: {string.Join( "; ", result.Brushes[0].Validate() )}" );
			AssertNear( result.Brushes[0].Volume, 24f, 1e-3f, "exported brush volume" );
			AssertNear( result.Report.BrushVolumeTotal, 24f, 1e-3f, "report brush volume" );
		} );

		registry.Add( "meshmode", "export a concave L mesh to exact convex brushes", () =>
		{
			var result = MeshMode.ExportToBrushes( LoadFixture( "lshape.mesh" ) );
			Assert( result.Report.Exact, $"L export must be exact, got: {result.Report}" );
			Assert( result.Brushes.Count == 2, $"L export must yield 2 brushes, got {result.Brushes.Count}" );
			foreach ( var brush in result.Brushes )
				Assert( brush.IsValid, $"exported brush must be valid, got: {string.Join( "; ", brush.Validate() )}" );
			AssertNear( result.Report.BrushVolumeTotal, 24f, 1e-3f, "report brush volume" );
		} );

		registry.Add( "meshmode", "export with MaxBrushes 1 reports the L overfill honestly", () =>
		{
			// The single brush the tree can build from the L's 12 points is the
			// dented ConvexHull (28, not the true hull 32 — see the ConvexHull
			// landmine note in MeshMode.cs). The report must still be honest about
			// what it emitted: 1 brush, inexact, excess 4/24 = 1/6, named.
			var result = MeshMode.ExportToBrushes( LoadFixture( "lshape.mesh" ),
				new MeshExportOptions { MaxBrushes = 1 } );
			Assert( result.Brushes.Count == 1, $"one-brush export must yield 1 brush, got {result.Brushes.Count}" );
			Assert( !result.Report.Exact, $"one-brush L export must not be exact, got: {result.Report}" );
			AssertNear( result.Report.ExcessRatio, 1f / 6f, 0.01f, "one-brush excess ratio (dented hull 28 vs mesh 24)" );
			Assert( result.Report.Unrepresentable.Count > 0, "one-brush export must name the unrepresentable geometry" );
		} );

		registry.Add( "meshmode", "export to VMF round-trips through VmfIo", () =>
		{
			var doc = MeshMode.ExportToVmf( BoxMesh() );
			Assert( doc.WorldBrushes.Count == 1, $"VMF export must have 1 world brush, got {doc.WorldBrushes.Count}" );
			Assert( doc.WorldBrushes[0].Faces.Count == 6, $"VMF brush must have 6 faces, got {doc.WorldBrushes[0].Faces.Count}" );
			var text = VmfIo.Write( doc );
			var back = VmfIo.Parse( text );
			Assert( back.WorldBrushes.Count == 1, $"round-tripped VMF must have 1 world brush, got {back.WorldBrushes.Count}" );
			Assert( back.WorldBrushes[0].Faces.Count == 6, $"round-tripped brush must have 6 faces, got {back.WorldBrushes[0].Faces.Count}" );
			Assert( back.VerifyStoredVertices().Count == 0,
				$"round-tripped stored vertices must verify, got: {string.Join( "; ", back.VerifyStoredVertices() )}" );
			var normals = back.WorldBrushes[0].Faces.Select( f => f.Normal ).ToList();
			Assert( normals.Any( n => n.ApproxEquals( new VmfVec( 0, 0, 1 ) ) ), "round-tripped brush must have a +Z face" );
			Assert( normals.Any( n => n.ApproxEquals( new VmfVec( 0, 0, -1 ) ) ), "round-tripped brush must have a -Z face" );
		} );

		registry.Add( "meshmode", "brush to mesh round-trips volume 24", () =>
		{
			var brush = MapBrush.Box( new Vec3( -1, -1.5f, -2 ), new Vec3( 1, 1.5f, 2 ), "dev/dev_measuregeneric01" );
			var mesh = MeshMode.FromBrush( brush );
			Assert( MeshMode.IsValid( mesh ), $"brush mesh must be valid, got: {Defects( mesh )}" );
			AssertNear( MeshMode.Volume( mesh ), 24f, 1e-3f, "brush mesh volume" );
			Assert( mesh.FaceCount == 6, $"brush mesh must have 6 faces, got {mesh.FaceCount}" );
		} );

		registry.Add( "meshmode", "validate finds the open edge on a box missing a face", () =>
		{
			var mesh = BoxMesh();
			mesh.Faces.RemoveAt( 1 );
			var defects = MeshMode.Validate( mesh );
			Assert( defects.Any( d => d.Code == MeshModeDefect.BoundaryEdge ),
				$"open box must report BoundaryEdge, got: {Defects( mesh )}" );
			Assert( !MeshMode.IsClosed( mesh ), "open box must not be closed" );
		} );

		registry.Add( "meshmode", "validate finds the non-manifold edge on a duplicated face", () =>
		{
			var mesh = BoxMesh();
			var top = mesh.Faces[1];
			mesh.AddFace( (int[])top.Indices.Clone(), (Vec2[])top.UVs.Clone(), top.Material );
			var defects = MeshMode.Validate( mesh );
			Assert( defects.Any( d => d.Code == MeshModeDefect.NonManifoldEdge ),
				$"doubled-face box must report NonManifoldEdge, got: {Defects( mesh )}" );
		} );

		registry.Add( "meshmode", "mesh mode feature extrudes through a PartStudio rebuild", () =>
		{
			var studio = new PartStudio();
			var feature = studio.Add( new MeshModeFeature() );
			feature.SourceMesh = BoxMesh();
			feature.Selection = MeshSelection.Face( 1 );
			feature.Distance.Value = 1f;
			studio.Rebuild();
			Assert( feature.Error is null, $"feature must build clean, got error: {feature.Error}" );
			Assert( studio.Bodies.Count == 1, $"expected 1 body, got {studio.Bodies.Count}" );
			AssertNear( MeshMode.Volume( studio.Bodies[0].Mesh ), 30f, 1e-3f, "feature extruded volume" );
			Assert( feature.ResultMesh is not null, "feature must keep its result mesh" );
		} );

		registry.Add( "meshmode", "mesh mode feature refuses a bad edit with a named error and no body", () =>
		{
			var studio = new PartStudio();
			var feature = studio.Add( new MeshModeFeature() );
			feature.SourceMesh = BoxMesh();
			feature.Selection = MeshSelection.Face( 1 );
			feature.Op.Index = 2; // Inset
			feature.Distance.Value = 99f;
			studio.Rebuild();
			Assert( studio.Bodies.Count == 0, $"refused feature must produce no body, got {studio.Bodies.Count}" );
			Assert( feature.Error is not null, "refused feature must set the feature error" );
			Assert( feature.Error.Contains( "Inset" ), $"feature error must name the inset refusal, got: {feature.Error}" );
		} );
	}

	// ---- fixtures ---------------------------------------------------------

	static PolyMesh LoadFixture( string name ) => MeshMode.LoadMesh( Path.Combine( FixtureDir(), name ) );

	static string FixtureDir()
	{
		// Walk up from the test binary, then from the working directory: the
		// real runner starts at the repo root, an isolated harness may not.
		foreach ( var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
		{
			var dir = new DirectoryInfo( root );
			while ( dir is not null )
			{
				var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-mesh-geometry-mode" );
				if ( Directory.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
		}
		throw new InvalidOperationException( "Cannot find tests/fixtures/hammer/hammer-mesh-geometry-mode from " + AppContext.BaseDirectory );
	}

	// ---- mesh builders ----------------------------------------------------
	//
	// Box corners: 0(-,-,-) 1(+,-,-) 2(+,+,-) 3(-,+,-) 4(-,-,+) 5(+,-,+) 6(+,+,+) 7(-,+,+).
	// Faces are [bottom, top, front, back, left, right], wound outward.

	static PolyMesh BoxMesh() => BoxMesh( new Vec3( -1, -1.5f, -2 ), new Vec3( 1, 1.5f, 2 ) );

	static PolyMesh BoxMesh( Vec3 min, Vec3 max )
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( min.x, min.y, min.z ) );
		mesh.AddVertex( new Vec3( max.x, min.y, min.z ) );
		mesh.AddVertex( new Vec3( max.x, max.y, min.z ) );
		mesh.AddVertex( new Vec3( min.x, max.y, min.z ) );
		mesh.AddVertex( new Vec3( min.x, min.y, max.z ) );
		mesh.AddVertex( new Vec3( max.x, min.y, max.z ) );
		mesh.AddVertex( new Vec3( max.x, max.y, max.z ) );
		mesh.AddVertex( new Vec3( min.x, max.y, max.z ) );
		var unit = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
		mesh.AddFace( new[] { 0, 3, 2, 1 }, (Vec2[])unit.Clone(), 0 );
		mesh.AddFace( new[] { 4, 5, 6, 7 }, (Vec2[])unit.Clone(), 0 );
		mesh.AddFace( new[] { 0, 1, 5, 4 }, (Vec2[])unit.Clone(), 0 );
		mesh.AddFace( new[] { 3, 7, 6, 2 }, (Vec2[])unit.Clone(), 0 );
		mesh.AddFace( new[] { 0, 4, 7, 3 }, (Vec2[])unit.Clone(), 0 );
		mesh.AddFace( new[] { 1, 2, 6, 5 }, (Vec2[])unit.Clone(), 0 );
		return mesh;
	}

	// ---- asserts ----------------------------------------------------------

	static string Defects( PolyMesh mesh ) =>
		string.Join( "; ", MeshMode.Validate( mesh ).Select( d => d.ToString() ) );

	static void AssertRefuses( Action body, string mustContain )
	{
		try { body(); }
		catch ( InvalidOperationException ex )
		{
			Assert( ex.Message.Contains( mustContain ),
				$"refusal must name '{mustContain}', got: {ex.Message}" );
			return;
		}
		Assert( false, $"expected a refusal naming '{mustContain}', but the op succeeded" );
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new InvalidOperationException( "ASSERT FAILED: " + message );
	}

	static void AssertNear( float actual, float expected, float tolerance, string what )
	{
		if ( MathF.Abs( actual - expected ) > tolerance )
			throw new InvalidOperationException( $"ASSERT FAILED: {what}: expected {expected:0.####} ± {tolerance:0.####}, got {actual:0.####}" );
	}
}
