using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Colour-management helpers that run on the CPU, before the view transform.
///
/// <para><b>Colour convention of the renderer (R1).</b> Colour and emission images are
/// sRGB-encoded inputs and are decoded to linear when they are sampled; roughness,
/// metallic, normal, occlusion and mask images are linear data and are sampled as-is.
/// Every lighting calculation — the key/fill/rim/bounce lights, the image-based lighting,
/// the screen-space effects, the bloom and the depth of field — happens in linear
/// Rec.709. The view transform (AgX by default) and the single sRGB display encode happen
/// exactly once, in <see cref="BloomPass.Composite"/>, so the display-referred image is
/// encoded once and only once.</para>
///
/// <para><b>White balance.</b> A camera white balance works by declaring which illuminant
/// the scene was lit by and removing that illuminant's tint, so a neutral surface stays
/// neutral. The same convention is used here, matching Blender's Color Management
/// "White Balance" controls: <see cref="ReferenceKelvin"/> (D65) is neutral, a lower
/// temperature means a warmer illuminant and therefore a cooler correction, and a higher
/// temperature means a bluer illuminant and therefore a warmer correction. The gain is
/// normalised so that it never changes the exposure: white balance only moves colour.</para>
///
/// <para>The illuminant's chromaticity comes from the Planckian locus (Kim et al.'s cubic
/// approximation of the CIE blackbody curve), converted to linear Rec.709. This is a
/// chromatic adaptation in Rec.709 primaries, not a full CIE chromatic adaptation
/// transform, so it is a close but not exact match for Blender's result at extreme
/// temperatures.</para></summary>
public static class WhiteBalance
{
	/// <summary>D65, the reference white point of the view transform. Neutral by definition.</summary>
	public const float ReferenceKelvin = 6500f;
	/// <summary>Lowest supported illuminant temperature (Kim et al.'s fit is valid from 1667 K).</summary>
	public const float MinKelvin = 1667f;
	/// <summary>Highest supported illuminant temperature.</summary>
	public const float MaxKelvin = 25000f;

	/// <summary>The white-balance gain for the given illuminant temperature and tint, as a
	/// per-channel multiplier for linear Rec.709 values. Neutral (1,1,1) at
	/// <see cref="ReferenceKelvin"/> with no tint.</summary>
	public static Vector3 Gain( float kelvin, float tint )
	{
		var target = BlackbodyLinearRgb( kelvin );
		var reference = BlackbodyLinearRgb( ReferenceKelvin );
		var gain = new Vector3(
			reference.X / MathF.Max( target.X, 1e-4f ),
			reference.Y / MathF.Max( target.Y, 1e-4f ),
			reference.Z / MathF.Max( target.Z, 1e-4f ) );
		// Tint: green (<0) to magenta (>0). A full chromatic adaptation would handle this
		// exactly; here the green channel is scaled against the red/blue pair and the
		// result is renormalised, which keeps the hue shift small and the exposure fixed.
		tint = Math.Clamp( tint, -1f, 1f );
		if ( tint != 0f ) gain.Y *= 1f - tint * 0.5f;
		// White balance must not change exposure: renormalise to unit luminance.
		float luma = 0.2126f * gain.X + 0.7152f * gain.Y + 0.0722f * gain.Z;
		if ( luma > 1e-4f ) gain /= luma;
		return gain;
	}

	/// <summary>The linear Rec.709 colour of a blackbody radiator at the given temperature,
	/// normalised to unit luminance so it carries chromaticity only.</summary>
	public static Vector3 BlackbodyLinearRgb( float kelvin )
	{
		kelvin = Math.Clamp( kelvin, MinKelvin, MaxKelvin );
		// Kim et al. (2002), cubic approximation of the Planckian locus in CIE 1931 xy.
		float t = kelvin;
		float x = t <= 4000f
			? -0.2661239e9f / ( t * t * t ) - 0.2343589e6f / ( t * t ) + 0.8776956e3f / t + 0.179910f
			: -3.0258469e9f / ( t * t * t ) + 2.1070379e6f / ( t * t ) + 0.2226347e3f / t + 0.240390f;
		float y;
		if ( t <= 2222f ) y = -1.1063814f * x * x * x - 1.34811020f * x * x + 2.18555832f * x - 0.20219683f;
		else if ( t <= 4000f ) y = -0.9549476f * x * x * x - 1.37418593f * x * x + 2.09137015f * x - 0.16748867f;
		else y = 3.0817580f * x * x * x - 5.87338670f * x * x + 3.75112997f * x - 0.37001483f;
		if ( y <= 1e-5f ) return Vector3.One;
		// xyY with Y = 1, then to linear Rec.709 (D65 primaries).
		float bigX = x / y;
		float bigZ = ( 1f - x - y ) / y;
		var rgb = new Vector3(
			3.2404542f * bigX - 1.5371385f - 0.4985314f * bigZ,
			-0.9692660f * bigX + 1.8760108f + 0.0415560f * bigZ,
			0.0556434f * bigX - 0.2040259f + 1.0572252f * bigZ );
		// Extreme temperatures can push a primary slightly negative; clamp and renormalise.
		rgb = new Vector3( MathF.Max( rgb.X, 1e-3f ), MathF.Max( rgb.Y, 1e-3f ), MathF.Max( rgb.Z, 1e-3f ) );
		float luma = 0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z;
		if ( luma > 1e-5f ) rgb /= luma;
		return rgb;
	}
}
