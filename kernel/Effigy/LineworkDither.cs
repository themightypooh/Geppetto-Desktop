using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Dithering algorithms for retro pixel-art rendering and color quantization.
/// Ordered Bayer dithering is spatially stable across frames (zero noise crawl during 3D animation),
/// while Atkinson and Floyd-Steinberg provide high-fidelity error diffusion for single-frame bakes.
/// </summary>
public static class LineworkDither
{
	// 2x2 Bayer matrix (normalized / 4)
	public static readonly byte[] Bayer2x2 = { 0, 2, 3, 1 };

	// 4x4 Bayer matrix (normalized / 16)
	public static readonly byte[] Bayer4x4 =
	{
		0,  8,  2, 10,
		12,  4, 14,  6,
		3, 11,  1,  9,
		15,  7, 13,  5
	};

	// 8x8 Bayer matrix (normalized / 64)
	public static readonly byte[] Bayer8x8 =
	{
		0, 32,  8, 40,  2, 34, 10, 42,
		48, 16, 56, 24, 50, 18, 58, 26,
		12, 44,  4, 36, 14, 46,  6, 38,
		60, 28, 52, 20, 62, 30, 54, 22,
		3, 35, 11, 43,  1, 33,  9, 41,
		51, 19, 59, 27, 49, 17, 57, 25,
		15, 47,  7, 39, 13, 45,  5, 37,
		63, 31, 55, 23, 61, 29, 53, 21
	};

	public static readonly int[] PaletteGameBoy =
	{
		0x0f380f, // Deepest olive
		0x306230, // Dark green
		0x8bac0f, // Light green
		0x9bbc0f  // Bright yellow-green
	};

	public static readonly int[] PalettePico8 =
	{
		0x000000, 0x1d2b53, 0x7e2553, 0x008751,
		0xab5236, 0x5f574f, 0xc2c3c7, 0xfff1e8,
		0xff004d, 0xffa300, 0xffec27, 0x00e436,
		0x29adff, 0x83769c, 0xff77a8, 0xffccaa
	};

	public static readonly int[] PaletteCga1 =
	{
		0x000000, 0x55ffff, 0xff55ff, 0xffffff
	};

	public static readonly int[] PaletteViscera =
	{
		0x1a090d, 0x3d1017, 0x6e1a24, 0xa12836,
		0xd44855, 0xe87a82, 0xf4b8bc, 0xffffff,
		0x3a2c27, 0x61493c, 0x8f6d53, 0xba9575,
		0xdfc5a6, 0xecd9c6, 0x7a2243, 0xb83b68
	};

	/// <summary>
	/// NES (2C02) 64-color master palette, PPU order $00-$3F.
	/// Source: the widely published NES palette table documented via NESdev Wiki
	/// "PPU palettes" (nesdev.org/wiki/PPU_palettes: 2C02 generates NTSC composite;
	/// no single composite palette is canonical — NESdev notes TV/monitor variation —
	/// and this table is the classic 55-usable-color table reproduced in Wikipedia's
	/// "List of video game console palettes" and the NES palette references that cite it).
	/// $0D is "blacker than black" per NESdev and $0F is the canonical black; $0E/$0F
	/// and the $xD/$xE/$xF slots that are mirrors of black are stored as 0x000000.
	/// $2D is the grey 0x787878 and $3D is 0xF8D8F8, as in that table.
	/// </summary>
	public static readonly int[] PaletteNes =
	{
		0x7c7c7c, 0x0000fc, 0x0000bc, 0x4428bc, 0x940084, 0xa80020, 0xa81000, 0x881400, // $00-$07
		0x503000, 0x007800, 0x006800, 0x005800, 0x004058, 0x000000, 0x000000, 0x000000, // $08-$0F
		0xbcbcbc, 0x0078f8, 0x0058f8, 0x6844fc, 0xd800cc, 0xe40058, 0xf83800, 0xe45c10, // $10-$17
		0xac7c00, 0x00b800, 0x00a800, 0x00a844, 0x008888, 0x000000, 0x000000, 0x000000, // $18-$1F
		0xf8f8f8, 0x3cbcfc, 0x6888fc, 0x9878f8, 0xf878f8, 0xf85898, 0xf87858, 0xfca044, // $20-$27
		0xf8b800, 0xb8f818, 0x58d854, 0x58f898, 0x00e8d8, 0x787878, 0x000000, 0x000000, // $28-$2F
		0xfcfcfc, 0xa4e4fc, 0xb8b8f8, 0xd8b8f8, 0xf8b8f8, 0xf8a4c0, 0xf0d0b0, 0xfce0a8, // $30-$37
		0xf8d878, 0xd8f878, 0xb8f8b8, 0xb8f8d8, 0x00fcfc, 0xf8d8f8, 0x000000, 0x000000  // $38-$3F
	};

	/// <summary>
	/// Commodore 64 VIC-II 16-color palette, VIC-II index order 0-15.
	/// Source: Philip "Pepto" Timmermann's VIC-II color research (pepto.de/projects/colorvic),
	/// hex list as published there: 000000, FFFFFF, 68372B, 70A4B2, 6F3D86, 588D43,
	/// 352879, B8C76F, 6F4F25, 433900, 9A6759, 444444, 6C6C6C, 9AD284, 6C5EB5, 959595.
	/// Note: the "colodore" variant differs slightly on some entries; this array is
	/// the Pepto set, stated here so a later colodore swap is a deliberate change.
	/// </summary>
	public static readonly int[] PaletteC64 =
	{
		0x000000, // Black
		0xffffff, // White
		0x68372b, // Red
		0x70a4b2, // Cyan
		0x6f3d86, // Purple
		0x588d43, // Green
		0x352879, // Blue
		0xb8c76f, // Yellow
		0x6f4f25, // Orange
		0x433900, // Brown
		0x9a6759, // Light red
		0x444444, // Dark grey
		0x6c6c6c, // Grey
		0x9ad284, // Light green
		0x6c5eb5, // Light blue
		0x959595  // Light grey
	};

	/// <summary>
	/// Ordered Bayer dithering on an RGB pixel buffer.
	/// matrixSize must be 2, 4, or 8.
	/// </summary>
	public static void ApplyBayer( int[] pixels, int width, int height, int[] palette, int matrixSize = 4, float spread = 0.25f )
	{
		if ( pixels == null || palette == null || palette.Length == 0 ) return;
		var matrix = matrixSize == 2 ? Bayer2x2 : matrixSize == 8 ? Bayer8x8 : Bayer4x4;
		var denom = (float)(matrixSize * matrixSize);

		for ( var y = 0; y < height; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var idx = y * width + x;
				var c = pixels[idx];
				var r = (c >> 16) & 0xFF;
				var g = (c >> 8) & 0xFF;
				var b = c & 0xFF;

				var threshold = (matrix[(y % matrixSize) * matrixSize + (x % matrixSize)] / denom) - 0.5f;
				var offset = (int)(threshold * spread * 255f);

				var rTarget = Math.Clamp( r + offset, 0, 255 );
				var gTarget = Math.Clamp( g + offset, 0, 255 );
				var bTarget = Math.Clamp( b + offset, 0, 255 );

				pixels[idx] = FindClosestPaletteColor( rTarget, gTarget, bTarget, palette );
			}
		}
	}

	/// <summary>
	/// Atkinson error diffusion dithering (iconic Mac / 1-bit Game Boy style).
	/// Retains sharp contrast by diffusing 3/4 of the error across 6 neighbors and discarding 1/4.
	/// </summary>
	public static void ApplyAtkinson( int[] pixels, int width, int height, int[] palette )
	{
		if ( pixels == null || palette == null || palette.Length == 0 ) return;

		var rErr = new float[width * height];
		var gErr = new float[width * height];
		var bErr = new float[width * height];

		for ( var i = 0; i < pixels.Length; i++ )
		{
			rErr[i] = (pixels[i] >> 16) & 0xFF;
			gErr[i] = (pixels[i] >> 8) & 0xFF;
			bErr[i] = pixels[i] & 0xFF;
		}

		for ( var y = 0; y < height; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var idx = y * width + x;
				var r = Math.Clamp( (int)MathF.Round( rErr[idx] ), 0, 255 );
				var g = Math.Clamp( (int)MathF.Round( gErr[idx] ), 0, 255 );
				var b = Math.Clamp( (int)MathF.Round( bErr[idx] ), 0, 255 );

				var match = FindClosestPaletteColor( r, g, b, palette );
				pixels[idx] = match;

				var mr = (match >> 16) & 0xFF;
				var mg = (match >> 8) & 0xFF;
				var mb = match & 0xFF;

				var er = (r - mr) / 8f;
				var eg = (g - mg) / 8f;
				var eb = (b - mb) / 8f;

				DistributeError( rErr, gErr, bErr, width, height, x + 1, y, er, eg, eb );
				DistributeError( rErr, gErr, bErr, width, height, x + 2, y, er, eg, eb );
				DistributeError( rErr, gErr, bErr, width, height, x - 1, y + 1, er, eg, eb );
				DistributeError( rErr, gErr, bErr, width, height, x, y + 1, er, eg, eb );
				DistributeError( rErr, gErr, bErr, width, height, x + 1, y + 1, er, eg, eb );
				DistributeError( rErr, gErr, bErr, width, height, x, y + 2, er, eg, eb );
			}
		}
	}

	private static void DistributeError( float[] rArr, float[] gArr, float[] bArr, int w, int h, int x, int y, float er, float eg, float eb )
	{
		if ( x < 0 || x >= w || y < 0 || y >= h ) return;
		var idx = y * w + x;
		rArr[idx] += er;
		gArr[idx] += eg;
		bArr[idx] += eb;
	}

	public static int FindClosestPaletteColor( int r, int g, int b, int[] palette )
	{
		var bestCol = palette[0];
		var bestDist = int.MaxValue;

		for ( var i = 0; i < palette.Length; i++ )
		{
			var c = palette[i];
			var pr = (c >> 16) & 0xFF;
			var pg = (c >> 8) & 0xFF;
			var pb = c & 0xFF;

			// Perceptual Euclidean distance weights
			var dr = r - pr;
			var dg = g - pg;
			var db = b - pb;
			var dist = (dr * dr * 3) + (dg * dg * 4) + (db * db * 2);

			if ( dist < bestDist )
			{
				bestDist = dist;
				bestCol = c;
				if ( dist == 0 ) break;
			}
		}

		return bestCol;
	}
}
