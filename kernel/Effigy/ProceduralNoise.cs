#nullable enable
using System;

namespace Effigy;

/// <summary>
/// 3D Procedural Noise and Domain Warping (Inigo Quilez; Worley 1996).
/// Provides seam-free procedural texturing for wood, marble, metal damascus, clouds, and flesh.
/// </summary>
public static class ProceduralNoise
{
	// 3D Simplex-style value noise
	public static float ValueNoise( Vec3 p )
	{
		int xi = (int)MathF.Floor( p.x );
		int yi = (int)MathF.Floor( p.y );
		int zi = (int)MathF.Floor( p.z );

		float xf = p.x - xi;
		float yf = p.y - yi;
		float zf = p.z - zi;

		// Quintic smoothstep curve: 6t^5 - 15t^4 + 10t^3
		float u = xf * xf * xf * (xf * (xf * 6 - 15) + 10);
		float v = yf * yf * yf * (yf * (yf * 6 - 15) + 10);
		float w = zf * zf * zf * (zf * (zf * 6 - 15) + 10);

		float c000 = Hash3( xi, yi, zi );
		float c100 = Hash3( xi + 1, yi, zi );
		float c010 = Hash3( xi, yi + 1, zi );
		float c110 = Hash3( xi + 1, yi + 1, zi );
		float c001 = Hash3( xi, yi, zi + 1 );
		float c101 = Hash3( xi + 1, yi, zi + 1 );
		float c011 = Hash3( xi, yi + 1, zi + 1 );
		float c111 = Hash3( xi + 1, yi + 1, zi + 1 );

		float x00 = c000 + u * (c100 - c000);
		float x10 = c010 + u * (c110 - c010);
		float x01 = c001 + u * (c101 - c001);
		float x11 = c011 + u * (c111 - c011);

		float y0 = x00 + v * (x10 - x00);
		float y1 = x01 + v * (x11 - x01);

		return y0 + w * (y1 - y0);
	}

	public static float Fbm( Vec3 p, int octaves = 5, float lacunarity = 2.0f, float gain = 0.5f )
	{
		if ( octaves <= 0 ) return 0f;

		float sum = 0f;
		float amp = 1.0f;
		float freq = 1.0f;
		float max = 0f;

		for ( int i = 0; i < octaves; i++ )
		{
			sum += ValueNoise( p * freq ) * amp;
			max += amp;
			freq *= lacunarity;
			amp *= gain;
		}

		return max > 0f ? sum / max : 0f;
	}

	/// <summary>
	/// Inigo Quilez Domain Warping: p' = p + a * fbm(p), W(p) = fbm(p' + b * fbm(p')).
	/// Produces rich natural marble, turbulent liquids, and organic biological patterns.
	/// </summary>
	public static float DomainWarp( Vec3 p, float warp1 = 2.0f, float warp2 = 4.0f )
	{
		Vec3 q = new Vec3(
			Fbm( p + new Vec3( 0.0f, 0.0f, 0.0f ) ),
			Fbm( p + new Vec3( 5.2f, 1.3f, 2.8f ) ),
			Fbm( p + new Vec3( 1.7f, 9.2f, 0.5f ) )
		);

		Vec3 r = new Vec3(
			Fbm( p + q * warp1 + new Vec3( 1.7f, 9.2f, 0.5f ) ),
			Fbm( p + q * warp1 + new Vec3( 8.3f, 2.8f, 1.1f ) ),
			Fbm( p + q * warp1 + new Vec3( 3.2f, 4.1f, 7.6f ) )
		);

		return Fbm( p + r * warp2 );
	}

	/// <summary>
	/// Worley 3D cellular noise (distance to nearest random feature point).
	/// </summary>
	public static float Worley( Vec3 p )
	{
		int xi = (int)MathF.Floor( p.x );
		int yi = (int)MathF.Floor( p.y );
		int zi = (int)MathF.Floor( p.z );

		float minDist = 10f;

		for ( int x = -1; x <= 1; x++ )
		for ( int y = -1; y <= 1; y++ )
		for ( int z = -1; z <= 1; z++ )
		{
			Vec3 cell = new Vec3( xi + x, yi + y, zi + z );
			Vec3 featurePoint = cell + new Vec3(
				Hash3( (int)cell.x, (int)cell.y, (int)cell.z ),
				Hash3( (int)cell.x + 13, (int)cell.y + 17, (int)cell.z + 19 ),
				Hash3( (int)cell.x + 23, (int)cell.y + 29, (int)cell.z + 31 )
			);

			float dist = (featurePoint - p).Length;
			if ( dist < minDist ) minDist = dist;
		}

		return MathF.Min( minDist, 1.0f );
	}

	static float Hash3( int x, int y, int z )
	{
		int h = x * 374761393 + y * 668265263 + z * 1274126177;
		h = (h ^ (h >> 13)) * 1274126177;
		return (h & 0x7FFFFFFF) / (float)0x7FFFFFFF;
	}
}
