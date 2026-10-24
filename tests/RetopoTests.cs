using System;
using Effigy;
using Marionette.Desktop;

internal static class RetopoTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "retopo", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof(T).Name}, got silent success" );
		}

		Check( "null input throws", () => Throws<ArgumentNullException>( () => RetopoService.Retopo( null ) ) );

		Check( "empty mesh stays empty", () =>
		{
			var m = new PolyMesh();
			var r = RetopoService.Retopo( m );
			Require( r.InputTriangles == 0 );
			Require( r.OutputQuads == 0 );
			Require( r.OutputTriangles == 0 );
			Require( r.Mesh.Faces.Count == 0 );
		} );

		Check( "single triangle stays triangle", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var r = RetopoService.Retopo( m );
			Require( r.InputTriangles == 1 );
			Require( r.OutputQuads == 0 );
			Require( r.OutputTriangles == 1 );
		} );

		Check( "2x2 grid pairs into quads", () =>
		{
			// 2 triangles forming one quad
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			m.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			var r = RetopoService.Retopo( m );
			Require( r.InputTriangles == 2 );
			Require( r.OutputQuads == 1 );
			Require( r.OutputTriangles == 0 );
		} );

		Check( "uv seam prevents pairing", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			var f0 = new Face( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ) } );
			var f1 = new Face( new[] { 0, 2, 3 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) } );
			// break seam: change one UV on shared edge
			f1.UVs[ 0 ] = new Vec2( 0.5f, 0.5f );
			m.Faces.Add( f0 );
			m.Faces.Add( f1 );
			var r = RetopoService.Retopo( m );
			Require( r.OutputQuads == 0, "seam should prevent pairing" );
			Require( r.OutputTriangles == 2 );
		} );

		Check( "input unchanged", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			m.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			int before = m.Faces.Count;
			RetopoService.Retopo( m );
			Require( m.Faces.Count == before );
		} );

		Check( "materials respected", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			var a = new Face( new[] { 0, 1, 2 } ); a.Material = 0;
			var b = new Face( new[] { 0, 2, 3 } ); b.Material = 1;
			m.Faces.Add( a ); m.Faces.Add( b );
			var r = RetopoService.Retopo( m );
			Require( r.OutputQuads == 0, "different materials should not pair" );
		} );

		Check( "deterministic repeat", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			m.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			var r1 = RetopoService.Retopo( m );
			var r2 = RetopoService.Retopo( m );
			Require( r1.OutputQuads == r2.OutputQuads );
		} );

		Check( "metrics: paired quad is manifold with no boundary", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			m.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			var r = RetopoService.Retopo( m );
			Require( r.IsManifold );
			Require( r.BoundaryEdges == 4 );
			Require( r.QuadRatio == 1.0 );
		} );

		Check( "metrics: single triangle has boundary edges", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var r = RetopoService.Retopo( m );
			Require( r.BoundaryEdges == 3 );
			Require( r.IsManifold );
			Require( r.QuadRatio == 0.0 );
		} );

		Check( "n-gon input is triangulated not thrown", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2, 3 } ) );
			var r = RetopoService.Retopo( m );
			Require( r.InputTriangles == 2 );
		} );
		Check( "lod targets parse sorted descending deduped", () =>
		{
			var t = RetopoService.ParseLodTargets( "500,4000,500" );
			Require( t.Length == 2 && t[ 0 ] == 4000 && t[ 1 ] == 500 );
		} );

		Check( "lod chain descends and preservation summaries report targets", () =>
		{
			var m = new PolyMesh();
			for ( int y = 0; y < 5; y++ ) for ( int x = 0; x < 5; x++ ) m.Positions.Add( new Vec3( x, y, 0 ) );
			for ( int y = 0; y < 4; y++ ) for ( int x = 0; x < 4; x++ )
			{
				int a = y * 5 + x, b = a + 1, c = a + 5, d = c + 1;
				m.Faces.Add( new Face( new[] { a, b, d } ) );
				m.Faces.Add( new Face( new[] { a, d, c } ) );
			}
			var chain = RetopoService.RetopoLodChain( m, "16,8" );
			Require( chain.Count == 2 );
			Require( chain[ 1 ].Mesh.Faces.Count <= chain[ 0 ].Mesh.Faces.Count, "smaller LOD target should not have more faces" );
			Require( chain[ 0 ].Mesh.Faces.Count < m.Faces.Count, "LOD should decimate below the input" );
			var p0 = RetopoService.CheckLodPreservation( m, 16, chain[ 0 ] );
			var p1 = RetopoService.CheckLodPreservation( m, 8, chain[ 1 ] );
			Require( p0.Summary().Contains( "target 16" ) && p1.Summary().Contains( "target 8" ) );
			Require( p0.MaterialsPreserved && p1.MaterialsPreserved );
		} );

	}
}
