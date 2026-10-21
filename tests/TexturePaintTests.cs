using System;
using Marionette.Desktop;

internal static class TexturePaintTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "texture-paint", name, test );
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

		Check( "null image throws", () => Throws<ArgumentNullException>( () => TexturePaintService.Paint( null, new TexturePaintService.Brush() ) ) );
		Check( "null brush throws", () => Throws<ArgumentNullException>( () => TexturePaintService.Paint( new TexturePaintService.Image( 4, 4 ), null ) ) );
		Check( "nonpositive radius throws", () => Throws<ArgumentException>( () => TexturePaintService.Paint( new TexturePaintService.Image( 4, 4 ), new TexturePaintService.Brush { Radius = 0 } ) ) );

		Check( "paints center pixel", () =>
		{
			var img = new TexturePaintService.Image( 8, 8 );
			TexturePaintService.Paint( img, new TexturePaintService.Brush { X = 4, Y = 4, Radius = 2, R = 255, G = 0, B = 0, Strength = 1 } );
			int idx = ( 4 * 8 + 4 ) * 4;
			Require( img.Rgba[ idx ] > 200 );
		} );

		Check( "out of bounds safe", () =>
		{
			var img = new TexturePaintService.Image( 4, 4 );
			TexturePaintService.Paint( img, new TexturePaintService.Brush { X = 100, Y = 100, Radius = 2 } );
			Require( true );
		} );

		Check( "default white", () =>
		{
			var img = new TexturePaintService.Image( 2, 2 );
			Require( img.Rgba[ 0 ] == 255 );
		} );
	}
}
