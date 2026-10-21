using System;
using Effigy;
using Marionette.Desktop;

internal static class PolypaintTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "polypaint", name, test );
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

		Check( "null source throws", () => Throws<ArgumentNullException>( () => PolypaintService.Paint( null, new PolypaintService.Brush() ) ) );
		Check( "null brush throws", () => Throws<ArgumentNullException>( () => PolypaintService.Paint( new PolyMesh(), null ) ) );
		Check( "non-positive radius throws", () => Throws<ArgumentException>( () => PolypaintService.Paint( new PolyMesh(), new PolypaintService.Brush { Radius = 0 } ) ) );

		Check( "paints vertex within radius", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			var r = PolypaintService.Paint( m, new PolypaintService.Brush { Center = new Vec3( 0, 0, 0 ), Radius = 1, Color = new Vec4( 1, 0, 0, 1 ), Strength = 1 } );
			Require( r.VertexColors[ 0 ].x > 0.5f );
		} );

		Check( "vertex outside radius unchanged", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 10, 0, 0 ) );
			var r = PolypaintService.Paint( m, new PolypaintService.Brush { Center = new Vec3( 0, 0, 0 ), Radius = 1, Color = new Vec4( 1, 0, 0, 1 ), Strength = 1 } );
			Require( MathF.Abs( r.VertexColors[ 0 ].x - 1f ) < 1e-5f );
		} );

		Check( "input unchanged", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			PolypaintService.Paint( m, new PolypaintService.Brush { Center = new Vec3( 0, 0, 0 ), Radius = 1, Strength = 1 } );
			Require( m.VertexColors is null );
		} );
	}
}
