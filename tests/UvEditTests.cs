using System;
using Effigy;
using Marionette.Desktop;

internal static class UvEditTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "uv-edit", name, test );
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

		Check( "null throws", () => Throws<ArgumentNullException>( () => UvEditService.Relax( null ) ) );
		Check( "negative iterations throws", () => Throws<ArgumentException>( () => UvEditService.Relax( new PolyMesh(), new UvEditService.Options { Iterations = -1 } ) ) );

		Check( "pinned vertex unchanged", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			var f = new Face( new[] { 0, 1, 2, 3 } );
			f.UVs[ 0 ] = new Vec2( 0, 0 );
			f.UVs[ 1 ] = new Vec2( 10, 0 );
			f.UVs[ 2 ] = new Vec2( 10, 10 );
			f.UVs[ 3 ] = new Vec2( 0, 10 );
			m.Faces.Add( f );
			var r = UvEditService.Relax( m, new UvEditService.Options { Iterations = 3, Pinned = new[] { 1 } } );
			// corner 1 (vertex 1) is pinned -> its UV stays
			Require( MathF.Abs( r.Faces[ 0 ].UVs[ 1 ].x - 10f ) < 1e-4f );
		} );

		Check( "zero iterations no change", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			var f = new Face( new[] { 0, 1, 2 } );
			f.UVs[ 0 ] = new Vec2( 0, 0 );
			f.UVs[ 1 ] = new Vec2( 5, 0 );
			f.UVs[ 2 ] = new Vec2( 0, 5 );
			m.Faces.Add( f );
			var r = UvEditService.Relax( m, new UvEditService.Options { Iterations = 0 } );
			Require( MathF.Abs( r.Faces[ 0 ].UVs[ 1 ].x - 5f ) < 1e-6f );
		} );
	}
}
