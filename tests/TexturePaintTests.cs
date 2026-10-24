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

		Check( "session records waypoints and converts to a committable texel stroke", () =>
		{
			var canvas = new Effigy.PaintCanvas( 64, 64 );
			var session = new TexturePaintService.Session( canvas, Effigy.PaintChannel.Color );
			Require( session.ToTexelStroke() is null, "no stroke before any dab" );
			var brush = new TexturePaintService.TextureBrush { X = 10, Y = 10, Radius = 4, Strength = 1, R = 255, G = 0, B = 0, Channel = Effigy.PaintChannel.Color };
			session.BeginStroke( brush );
			session.Dab( 10, 10 );
			session.LineTo( 20, 10 );
			session.EndStroke();
			var tex = session.ToTexelStroke();
			Require( tex is not null, "a dabbed stroke converts" );
			Require( tex.Path.Count >= 2, $"waypoints recorded, got {tex.Path.Count}" );
			Require( tex.Channel == Effigy.PaintChannel.Color && tex.R == 1f && tex.G == 0f && tex.Radius == 4f, "brush settings carried into the texel stroke" );
			Require( session.Waypoints.Count == tex.Path.Count, "waypoints and texel path agree" );
		} );

		Check( "session stroke commits and replays through the kernel", () =>
		{
			var canvas = new Effigy.PaintCanvas( 64, 64 );
			var session = new TexturePaintService.Session( canvas, Effigy.PaintChannel.Color );
			var brush = new TexturePaintService.TextureBrush { X = 32, Y = 32, Radius = 4, Strength = 1, R = 255, G = 0, B = 0, Channel = Effigy.PaintChannel.Color };
			session.BeginStroke( brush );
			session.Dab( 32, 32 );
			session.EndStroke();
			var tex = session.ToTexelStroke();
			var maps = new Effigy.PaintLayerMaps { Size = 64 };
			Effigy.TexelReplay.Apply( maps, tex );
			int idx = ( 32 * 64 + 32 ) * 4;
			Require( maps.Color is not null && maps.Color.Rgba[ idx ] == 255 && maps.Color.Rgba[ idx + 1 ] == 0, "committed texel stroke paints the named texel red" );
		} );

		Check( "begin stroke clears the previous stroke waypoints", () =>
		{
			var canvas = new Effigy.PaintCanvas( 64, 64 );
			var session = new TexturePaintService.Session( canvas, Effigy.PaintChannel.Color );
			var brush = new TexturePaintService.TextureBrush { Radius = 4, Strength = 1, Channel = Effigy.PaintChannel.Color };
			session.BeginStroke( brush );
			session.Dab( 5, 5 );
			session.EndStroke();
			Require( session.Waypoints.Count > 0 );
			session.BeginStroke( brush );
			Require( session.Waypoints.Count == 0, "a new stroke starts with no waypoints" );
		} );
	}
}
