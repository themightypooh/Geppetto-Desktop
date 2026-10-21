using System;

namespace Marionette.Desktop;

public static class TexturePaintService
{
	public sealed class Image { public int W, H; public byte[] Rgba; public Image( int w, int h ){ W=w; H=h; Rgba=new byte[w*h*4]; for(int i=0;i<Rgba.Length;i+=4){Rgba[i]=255;Rgba[i+1]=255;Rgba[i+2]=255;Rgba[i+3]=255;} } }

	public sealed class Brush { public float X, Y, Radius=10f, Strength=1f; public byte R=255, G=0, B=0, A=255; }

	public static void Paint( Image img, Brush b )
	{
		if ( img is null ) throw new ArgumentNullException( nameof( img ) );
		if ( b is null ) throw new ArgumentNullException( nameof( b ) );
		if ( b.Radius <= 0 ) throw new ArgumentException( "radius must be positive" );
		int r = ( int ) MathF.Ceiling( b.Radius );
		for ( int y = -r; y <= r; y++ )
		for ( int x = -r; x <= r; x++ )
		{
			int px = ( int ) b.X + x;
			int py = ( int ) b.Y + y;
			if ( px < 0 || py < 0 || px >= img.W || py >= img.H ) continue;
			float d = MathF.Sqrt( x * x + y * y );
			if ( d > b.Radius ) continue;
			float t = 1f - d / b.Radius;
			float w = t * b.Strength;
			if ( w > 1f ) w = 1f;
			int idx = ( py * img.W + px ) * 4;
			img.Rgba[ idx ] = ( byte )( img.Rgba[ idx ] * ( 1 - w ) + b.R * w );
			img.Rgba[ idx + 1 ] = ( byte )( img.Rgba[ idx + 1 ] * ( 1 - w ) + b.G * w );
			img.Rgba[ idx + 2 ] = ( byte )( img.Rgba[ idx + 2 ] * ( 1 - w ) + b.B * w );
			img.Rgba[ idx + 3 ] = ( byte )( img.Rgba[ idx + 3 ] * ( 1 - w ) + b.A * w );
		}
	}
}
