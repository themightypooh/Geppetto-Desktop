using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Bridge utilities between 2D Linework canvas and 3D Effigy models.
/// Includes UV seam dilation (edge padding) to eliminate dark mipmap seam lines on 3D models.
/// </summary>
public static class LineworkUvBridge
{
	/// <summary>
	/// Dilates / expands border texels outward into transparent space by bleedPixels.
	/// Guarantees that when the 2D texture is mipmapped and bilinearly sampled across UV seams,
	/// adjacent texels carry the island's edge color instead of zero/black.
	/// </summary>
	public static void DilateSeams( byte[] rgba, int width, int height, int bleedPixels = 4 )
	{
		if ( rgba == null || width <= 0 || height <= 0 || bleedPixels <= 0 ) return;

		var current = (byte[])rgba.Clone();
		var next = new byte[rgba.Length];

		for ( var step = 0; step < bleedPixels; step++ )
		{
			Array.Copy( current, next, current.Length );
			var changed = false;

			for ( var y = 0; y < height; y++ )
			{
				for ( var x = 0; x < width; x++ )
				{
					var idx = (y * width + x) * 4;
					// If already opaque, keep it
					if ( current[idx + 3] > 0 ) continue;

					// Collect filled neighbors
					int sumR = 0, sumG = 0, sumB = 0, count = 0;

					// 8-way neighborhood
					for ( var dy = -1; dy <= 1; dy++ )
					{
						var ny = y + dy;
						if ( ny < 0 || ny >= height ) continue;

						for ( var dx = -1; dx <= 1; dx++ )
						{
							if ( dx == 0 && dy == 0 ) continue;
							var nx = x + dx;
							if ( nx < 0 || nx >= width ) continue;

							var nIdx = (ny * width + nx) * 4;
							if ( current[nIdx + 3] > 0 )
							{
								sumR += current[nIdx];
								sumG += current[nIdx + 1];
								sumB += current[nIdx + 2];
								count++;
							}
						}
					}

					if ( count > 0 )
					{
						next[idx] = (byte)(sumR / count);
						next[idx + 1] = (byte)(sumG / count);
						next[idx + 2] = (byte)(sumB / count);
						next[idx + 3] = 255;
						changed = true;
					}
				}
			}

			if ( !changed ) break;
			Array.Copy( next, current, current.Length );
		}

		Array.Copy( current, rgba, rgba.Length );
	}
}
