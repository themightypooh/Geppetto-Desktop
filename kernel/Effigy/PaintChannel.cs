using System;

namespace Effigy;

/// <summary>
/// Which map a paint stroke or a fill writes.
///
/// Colour is the albedo the paint path always had. Roughness, Metalness and Height are the surface
/// channels a texture set carries beside it; Height is painted as a grey and turned into a normal
/// map on export (see <see cref="PaintTextureSet.HeightToNormal"/>). Mask is not a surface channel:
/// it is the layer's own mask, painted black to hide and white to show, and it never leaves the layer.
///
/// THE ORDER IS THE FILE FORMAT'S. A stroke saves its channel by name, but code that indexes by the
/// numeric value would break if these were reordered — append, never insert.
/// </summary>
public enum PaintChannel
{
	Color,
	Roughness,
	Metalness,
	Height,
	Mask,
	/// <summary>Self-illumination mask (grey): 0 is no glow, 1 glows at the full albedo. s&amp;box complex.vfx
	/// TextureSelfIllumMask; glTF emissive.</summary>
	Emissive,
	/// <summary>Opacity (grey): 1 is solid, 0 is see-through. s&amp;box TextureTranslucency; glTF base colour alpha.</summary>
	Opacity,
	/// <summary>A painted tangent-space normal map (RGB, s&amp;box DirectX green): (128,128,255) is flat. Painted like
	/// colour, combined with the normal derived from Height on export (PaintTextureSet.Images).</summary>
	Normal,
}

/// <summary>
/// How a paint layer combines with the layers below it.
///
/// The five separable modes every layer-based painter shares, with the W3C compositing definitions
/// (Compositing and Blending Level 1, §5.8 and §10): Normal is plain source-over, Multiply darkens,
/// Screen lightens, Overlay multiplies the darks and screens the lights of what is underneath, Add
/// sums and clips. The same first five <see cref="FleshBlend"/> uses, so a layer behaves the same whether
/// it was painted or generated. After those, the rest of the W3C separable set: Darken, Lighten,
/// Difference, Exclusion, Soft Light, Hard Light, Color Dodge, Color Burn. New modes are appended so
/// a saved layer's mode index never changes meaning.
/// </summary>
public enum PaintBlendMode
{
	Normal,
	Multiply,
	Screen,
	Overlay,
	Add,
	Darken,
	Lighten,
	Difference,
	Exclusion,
	SoftLight,
	HardLight,
	ColorDodge,
	ColorBurn,
}

/// <summary>What each channel is called and what an unpainted texel of it means.
///
/// Colour spaces: Colour and Emissive are sRGB; Roughness, Metalness, Height, Opacity and Normal
/// are linear data (Normal is tangent-space, DirectX green). The baked ambient occlusion a
/// composite carries is linear data too; it is not a channel and never exports from here.</summary>
public static class PaintChannels
{
	/// <summary>The surface channels a body's paint carries, in the order they export.</summary>
	public static readonly PaintChannel[] Surface =
		{ PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Metalness, PaintChannel.Height, PaintChannel.Emissive, PaintChannel.Opacity, PaintChannel.Normal };

	/// <summary>True for the channels a stroke paints in full RGB (colour and the normal map); the rest are greys.</summary>
	public static bool IsRgb( PaintChannel channel ) => channel is PaintChannel.Color or PaintChannel.Normal;

	/// <summary>The flat tangent-space normal, as bytes: an unpainted Normal texel.</summary>
	public const byte FlatNormalR = 128, FlatNormalG = 128, FlatNormalB = 255;

	/// <summary>The label the editor shows, and the name a saved stroke carries.</summary>
	public static string Label( PaintChannel channel ) => channel switch
	{
		PaintChannel.Color => "Base Color",
		PaintChannel.Roughness => "Roughness",
		PaintChannel.Metalness => "Metallic",
		PaintChannel.Height => "Height",
		PaintChannel.Mask => "Layer Mask",
		PaintChannel.Emissive => "Emissive",
		PaintChannel.Opacity => "Opacity",
		PaintChannel.Normal => "Normal",
		_ => channel.ToString(),
	};

	/// <summary>The short tag a stroke line is prefixed with in a saved document (`@rough`), or null
	/// for colour — which keeps every document written before channels existed byte-identical.</summary>
	public static string Tag( PaintChannel channel ) => channel switch
	{
		PaintChannel.Roughness => "rough",
		PaintChannel.Metalness => "metal",
		PaintChannel.Height => "height",
		PaintChannel.Mask => "mask",
		PaintChannel.Emissive => "emit",
		PaintChannel.Opacity => "opacity",
		PaintChannel.Normal => "normal",
		_ => null,
	};

	/// <summary>The channel a saved tag names, or null when the tag is unknown.</summary>
	public static PaintChannel? FromTag( string tag )
	{
		foreach ( var channel in Enum.GetValues<PaintChannel>() )
		{
			if ( string.Equals( Tag( channel ), tag, StringComparison.OrdinalIgnoreCase ) )
				return channel;
		}

		return null;
	}

	/// <summary>
	/// What an unpainted texel of a scalar channel exports as, 0..1.
	///
	/// Roughness 0.6 is a satin non-metal, the middle of what a creature's skin, cloth and leather sit
	/// at; metalness 0 is "not metal", the only right default for a part nobody painted as metal;
	/// height 0.5 is "on the surface", so painting brighter raises and darker sinks, and an unpainted
	/// texel is flat in the normal map.
	/// </summary>
	public static float Base( PaintChannel channel ) => channel switch
	{
		PaintChannel.Roughness => 0.6f,
		PaintChannel.Metalness => 0f,
		PaintChannel.Height => 0.5f,
		PaintChannel.Mask => 1f,
		PaintChannel.Emissive => 0f,
		PaintChannel.Opacity => 1f,
		PaintChannel.Normal => 0.5f,
		_ => PaintMaterial.DefaultBaseR / 255f,
	};
}
