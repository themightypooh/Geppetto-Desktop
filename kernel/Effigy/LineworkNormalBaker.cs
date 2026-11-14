using System;

namespace Effigy;

/// <summary>
/// Generates Source 2-ready tangent space normal maps and cavity maps from 2D strokes and heightmaps.
/// Provides Reoriented Normal Mapping (RNM) to blend 2D hand-drawn details cleanly over 3D sculpt base normals.
/// </summary>
public static class LineworkNormalBaker
{
	/// <summary>
	/// Converts a 2D grayscale heightmap buffer (0..255) into an RGB tangent space normal map.
	/// </summary>
	public static byte[] HeightToNormal( byte[] height, int width, int heightPx, float strength = 2.0f, bool flipY = true, bool wrap = false )
	{
		if ( height == null || width <= 0 || heightPx <= 0 || (long)width * heightPx != height.Length || !float.IsFinite( strength ) )
			throw new ArgumentException( "Invalid height buffer parameters." );

		var normalRgb = new byte[width * heightPx * 3];

		for ( var y = 0; y < heightPx; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				// 3x3 Sobel sampling
				var x0 = wrap ? (x + width - 1) % width : Math.Max( 0, x - 1 );
				var x1 = wrap ? (x + 1) % width : Math.Min( width - 1, x + 1 );
				var y0 = wrap ? (y + heightPx - 1) % heightPx : Math.Max( 0, y - 1 );
				var y1 = wrap ? (y + 1) % heightPx : Math.Min( heightPx - 1, y + 1 );

				var tl = height[y0 * width + x0];
				var tc = height[y0 * width + x];
				var tr = height[y0 * width + x1];

				var ml = height[y * width + x0];
				var mr = height[y * width + x1];

				var bl = height[y1 * width + x0];
				var bc = height[y1 * width + x];
				var br = height[y1 * width + x1];

				// Sobel gradients
				var dx = (tr + 2f * mr + br) - (tl + 2f * ml + bl);
				var dy = (bl + 2f * bc + br) - (tl + 2f * tc + tr);

				if ( flipY ) dy = -dy;

				// Scale to [-1, 1]
				var nx = -dx * strength / 255f;
				var ny = -dy * strength / 255f;
				var nz = 1.0f;

				// Normalize
				var len = MathF.Sqrt( nx * nx + ny * ny + nz * nz );
				if ( len > 0.0001f )
				{
					nx /= len;
					ny /= len;
					nz /= len;
				}

				var idx = (y * width + x) * 3;
				normalRgb[idx] = (byte)Math.Clamp( (int)((nx * 0.5f + 0.5f) * 255f), 0, 255 );
				normalRgb[idx + 1] = (byte)Math.Clamp( (int)((ny * 0.5f + 0.5f) * 255f), 0, 255 );
				normalRgb[idx + 2] = (byte)Math.Clamp( (int)((nz * 0.5f + 0.5f) * 255f), 0, 255 );
			}
		}

		return normalRgb;
	}

	/// <summary>
	/// Reoriented Normal Mapping (RNM) blending: composites detail normal map over base normal map
	/// without flattening the base surface curvature.
	/// </summary>
	public static byte[] CompositeRnm( byte[] baseNormals, byte[] detailNormals, int width, int heightPx )
	{
		if ( baseNormals == null || detailNormals == null )
			throw new ArgumentNullException();
		if ( width <= 0 || heightPx <= 0 || (long)width * heightPx * 3 != baseNormals.Length || baseNormals.Length != detailNormals.Length )
			throw new ArgumentException( "Normal buffers must contain three bytes per texel." );

		var output = new byte[width * heightPx * 3];

		for ( var i = 0; i < width * heightPx; i++ )
		{
			var idx = i * 3;

			// Unpack [0, 255] -> [-1, 1]
			var n1x = (baseNormals[idx] / 255f) * 2f - 1f;
			var n1y = (baseNormals[idx + 1] / 255f) * 2f - 1f;
			var n1z = (baseNormals[idx + 2] / 255f) * 2f - 1f;

			var n2x = (detailNormals[idx] / 255f) * 2f - 1f;
			var n2y = (detailNormals[idx + 1] / 255f) * 2f - 1f;
			var n2z = (detailNormals[idx + 2] / 255f) * 2f - 1f;

			// RNM algorithm
			var uX = n1x;
			var uY = n1y;
			var uZ = n1z + 1f;

			var vX = -n2x;
			var vY = -n2y;
			var vZ = n2z;

			var dotUV = uX * vX + uY * vY + uZ * vZ;
			var factor = uZ > 0.0001f ? dotUV / uZ : 1f;

			var rx = uX * factor - vX;
			var ry = uY * factor - vY;
			var rz = uZ * factor - vZ;

			var len = MathF.Sqrt( rx * rx + ry * ry + rz * rz );
			if ( len > 0.0001f )
			{
				rx /= len;
				ry /= len;
				rz /= len;
			}

			output[idx] = (byte)Math.Clamp( (int)((rx * 0.5f + 0.5f) * 255f), 0, 255 );
			output[idx + 1] = (byte)Math.Clamp( (int)((ry * 0.5f + 0.5f) * 255f), 0, 255 );
			output[idx + 2] = (byte)Math.Clamp( (int)((rz * 0.5f + 0.5f) * 255f), 0, 255 );
		}

		return output;
	}
}
