using System;
using Effigy;
using Marionette.Desktop;

internal static class DyntopoServiceTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "dyntopo", name, test );
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

		Check( "null throws", () => Throws<ArgumentNullException>( () => DyntopoService.Subdivide( null, new DyntopoService.Options { Center = new Vec3( 0, 0, 0 ), Radius = 1 } ) ) );
		Check( "null options throws", () => Throws<ArgumentNullException>( () => DyntopoService.Subdivide( new PolyMesh(), null ) ) );
		Check( "nonpositive radius throws", () => Throws<ArgumentException>( () => DyntopoService.Subdivide( new PolyMesh(), new DyntopoService.Options { Radius = 0, Center = new Vec3( 0, 0, 0 ) } ) ) );

		Check( "subdivides long edge within brush", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var r = DyntopoService.Subdivide( m, new DyntopoService.Options { Center = new Vec3( 0.5f, 0, 0 ), Radius = 1, MaxEdgeLength = 0.1f, MaxIterations = 1 } );
			Require( r.Positions.Count > m.Positions.Count );
		} );

		Check( "inflate moves vertices", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			var r = DyntopoService.Sculpt( m, new DyntopoService.SculptOptions { Center = new Vec3( 0, 0, 0 ), Radius = 1, Mode = DyntopoService.BrushMode.Inflate, Strength = 0.5f, Direction = new Vec3( 0, 0, 1 ) } );
			Require( r.Positions[ 0 ].z > 0 );
		} );

		Check( "flatten adjusts y", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 5, 0 ) );
			var r = DyntopoService.Sculpt( m, new DyntopoService.SculptOptions { Center = new Vec3( 0, 0, 0 ), Radius = 10, Mode = DyntopoService.BrushMode.Flatten, Strength = 1f } );
			Require( r.Positions[ 0 ].y < 5f );
		} );

		Check( "sculpt null throws", () => Throws<ArgumentNullException>( () => DyntopoService.Sculpt( null, new DyntopoService.SculptOptions { Center = new Vec3( 0, 0, 0 ), Radius = 1 } ) ) );

		Check( "input unchanged", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			int before = m.Positions.Count;
			DyntopoService.Subdivide( m, new DyntopoService.Options { Center = new Vec3( 0.5f, 0, 0 ), Radius = 1, MaxEdgeLength = 0.1f, MaxIterations = 1 } );
			Require( m.Positions.Count == before );
		} );
	}
}
