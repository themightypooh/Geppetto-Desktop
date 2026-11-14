using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// COPIC OKLCH color space helpers and the 11 Linework marker families.
///
/// Port of the original Linework ColorRing (Qt/QML + C++) color math. Perceptual OKLCH
/// clamped into sRGB gamut so every shade step has uniform lightness and chroma. The kernel
/// keeps its own vector types (see Vec.cs) so this compiles without an engine; editor glue
/// converts the returned Vec4 to the engine's Color at the boundary.
/// </summary>
public static class LineworkOklch
{
	/// <summary>A marker (shade) family: name, hue angle (degrees) and base chroma.</summary>
	public readonly struct ShadeFamily
	{
		public readonly string Name;
		public readonly float Hue;
		public readonly float Chroma;

		public ShadeFamily( string name, float hue, float chroma )
		{
			Name = name;
			Hue = hue;
			Chroma = chroma;
		}
	}

	/// <summary>The 11 COPIC marker families, in wheel order.</summary>
	public static readonly ShadeFamily[] Families = new ShadeFamily[]
	{
		new ShadeFamily( "R",   25f,  0.21f ),   // Red
		new ShadeFamily( "YR",  52f,  0.19f ),   // Yellow-Red
		new ShadeFamily( "Y",   98f,  0.18f ),   // Yellow
		new ShadeFamily( "YG", 124f,  0.18f ),   // Yellow-Green
		new ShadeFamily( "G",  150f,  0.17f ),   // Green
		new ShadeFamily( "BG", 182f,  0.14f ),   // Blue-Green
		new ShadeFamily( "B",  236f,  0.16f ),   // Blue
		new ShadeFamily( "BV", 268f,  0.16f ),   // Blue-Violet
		new ShadeFamily( "V",  305f,  0.18f ),   // Violet
		new ShadeFamily( "RV", 345f,  0.20f ),   // Red-Violet
		new ShadeFamily( "E",   58f,  0.075f ),  // Earth
	};

	/// <summary>Alias for <see cref="Families"/> (guide naming).</summary>
	public static readonly ShadeFamily[] MarkerFamilies = Families;

	public const int FamilyCount = 11;

	public static readonly string[] FamilyNames = new string[]
	{
		"R", "YR", "Y", "YG", "G", "BG", "B", "BV", "V", "RV", "E"
	};

	public static readonly float[] FamilyHues = new float[]
	{
		25f, 52f, 98f, 124f, 150f, 182f, 236f, 268f, 305f, 345f, 58f
	};

	public static readonly float[] FamilyChromas = new float[]
	{
		0.21f, 0.19f, 0.18f, 0.18f, 0.17f, 0.14f, 0.16f, 0.16f, 0.18f, 0.20f, 0.075f
	};

	/// <summary>Column chroma multipliers: Vivid, Softer, Greyed.</summary>
	public static readonly float[] ChromaMultipliers = new float[] { 1.00f, 0.58f, 0.30f };

	/// <summary>Shade step counts per column.</summary>
	public static readonly int[] ShadeStepCounts = new int[] { 9, 7, 5 };

	/// <summary>Shade labels per column (marker shade numbers, low = light).</summary>
	public static readonly string[][] ShadeLabels = new string[][]
	{
		new string[] { "9", "7", "5", "4", "3", "2", "1", "0", "00" },
		new string[] { "9", "7", "5", "3", "1", "0", "00" },
		new string[] { "9", "6", "3", "1", "0" },
	};

	/// <summary>Lightness steps for the vivid (9-step) column, low index = darkest? Guide lists 9,7,5,4,3,2,1,0,00 (lightness decreasing). We store lightness L values (0..1) descending-ish: darkest last. Using L = stepIndex mapped.</summary>
	// Lightness samples (perceptual L) for shade steps, indexed by step (0 = darkest? guide lists 9,7,5,...). We keep a helper instead.

	/// <summary>Number of grey swatches in the grey ring.</summary>
	public const int GreySwatchCount = 33;

	/// <summary>
	/// Convert OKLCH to sRGB. Clamps linear RGB into [0,1] via gamma; out-of-gamut chroma is
	/// clipped to the sRGB boundary (guide: "clamped into sRGB gamut").
	/// </summary>
	public static Vec4 OklchToRgb( float L, float C, float hDeg )
	{
		float h = hDeg * MathF.PI / 180f;
		float a = C * MathF.Cos( h );
		float b = C * MathF.Sin( h );

		float l_ = L + 0.3963377774f * a + 0.2158037573f * b;
		float m_ = L - 0.1055613458f * a - 0.0638541728f * b;
		float s_ = L - 0.0894841775f * a - 1.2914855480f * b;

		float l = l_ * l_ * l_;
		float m = m_ * m_ * m_;
		float s = s_ * s_ * s_;

		float rLin = +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
		float gLin = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
		float bLin = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

		static float Gamma( float x )
		{
			x = Math.Clamp( x, 0f, 1f );
			return x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow( x, 1f / 2.4f ) - 0.055f;
		}

		return new Vec4( Gamma( rLin ), Gamma( gLin ), Gamma( bLin ), 1f );
	}

	/// <summary>
	/// Lightness value for a shade step label. Guide lists labels like 9,7,5,... where higher = lighter.
	/// Maps label to L in [0,1] approximately: L = label/9 (clamped), with 00 -> ~0.97 (near white) and 0 -> ~0.05.
	/// This is a heuristic; the wheel's exact L table is not fully specified in the guide.
	/// </summary>
	public static float ShadeLightness( string label )
	{
		if ( label == "00" ) return 0.97f;
		if ( label == "0" ) return 0.05f;
		if ( int.TryParse( label, out var n ) ) return Math.Clamp( n / 9f, 0.05f, 0.95f );
		return 0.5f;
	}

	/// <summary>Compute the sRGB color for a shade cell.</summary>
	public static Vec4 ShadeColor( int familyIndex, int column, int step )
	{
		if ( familyIndex < 0 || familyIndex >= Families.Length ) familyIndex = 0;
		if ( column < 0 || column > 2 ) column = 0;
		var fam = Families[familyIndex];
		var labels = ShadeLabels[column];
		if ( step < 0 || step >= labels.Length ) step = 0;
		float L = ShadeLightness( labels[step] );
		float C = fam.Chroma * ChromaMultipliers[column];
		return OklchToRgb( L, C, fam.Hue );
	}

	/// <summary>Grey swatch color by index (neutral/warm/cool grouped). Returns perceptual grey.</summary>
	public static Vec4 GreyColor( int index )
	{
		// 33 greys: simple linear lightness ramp with slight warm/cool tint by group.
		int i = Math.Clamp( index, 0, GreySwatchCount - 1 );
		float L = 0.05f + (i / (float)(GreySwatchCount - 1)) * 0.9f;
		// group tint by thirds
		float hue = 0f;
		float chroma = 0f;
		if ( i < 11 ) { hue = 30f; chroma = 0.012f; }      // warm
		else if ( i < 22 ) { hue = 210f; chroma = 0.012f; } // cool
		else { chroma = 0f; }                               // neutral
		return OklchToRgb( L, chroma, hue );
	}
}
