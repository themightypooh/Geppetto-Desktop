using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Which green convention a normal map is in.</summary>
public enum NormalMapConvention
{
	/// <summary>DirectX green: +green is down the image (Unreal, s&amp;box, this kernel).</summary>
	DirectX,

	/// <summary>OpenGL green: +green is up the image (glTF, Blender).</summary>
	OpenGL,
}

/// <summary>The Unreal preset's output: one image per map type, plus the VRAM
/// warning (null when the export fits the reported VRAM, or when no VRAM
/// figure was given).</summary>
public sealed class UnrealExport
{
	public List<PaintTextureSet.Image> Images = new();

	public string Warning;
}

/// <summary>
/// The Unreal engine export preset: one baked + painted asset out as the
/// packed maps Unreal expects, named {Asset}_{MapType}, at 4K working or 8K
/// export, DirectX normal.
///
/// Packing (Unreal's packed ORM): R = ambient occlusion, G = roughness,
/// B = metallic, A = 255. BaseColor carries the painted colour with the
/// opacity map stamped into its alpha when one is painted. Normal is the
/// kernel's combined normal (painted + height-derived) — the kernel already
/// stores DirectX green, so the preset passes it through unchanged and only
/// flips maps that arrive declaring OpenGL.
/// </summary>
public static class UnrealOrmPreset
{
	public const int WorkingSize = 4096;

	public const int ExportSize = 8192;

	/// <summary>8K export needs at least this much VRAM, in MB. The export is
	/// never silently downscaled to fit: it runs at the requested size and the
	/// warning is reported alongside it.</summary>
	public const long MinVramMbFor8K = 2500;

	public const string BaseColorType = "BaseColor";

	public const string NormalType = "Normal";

	public const string OrmType = "ORM";

	public const string EmissiveType = "Emissive";

	/// <summary>{Asset}_{MapType}.png — the template Unreal-side import
	/// rules match on.</summary>
	public static string FileName( string asset, string mapType ) => $"{asset}_{mapType}.png";

	/// <summary>The VRAM warning for an export at <paramref name="size"/>, or
	/// null when it fits or when no VRAM figure was given. Only exports above
	/// the 4K working size need the 2.5 GB.</summary>
	public static string VramWarning( int size, long? availableVramMb )
	{
		if ( size <= WorkingSize || availableVramMb is null || availableVramMb >= MinVramMbFor8K )
			return null;
		return $"8K export needs at least 2.5 GB of VRAM, but only {availableVramMb} MB was reported. The export is NOT downscaled — it runs at {size}x{size} and may be slow or fail on this GPU.";
	}

	/// <summary>Flip a normal map's green channel (DirectX &lt;-&gt; OpenGL).
	/// Takes an RGBA image; returns a new array, the input untouched.</summary>
	public static byte[] FlipGreen( byte[] normal )
	{
		if ( normal is null ) return null;
		var result = (byte[])normal.Clone();
		for ( var i = 1; i < result.Length; i += 4 )
			result[i] = (byte)(255 - result[i]);
		return result;
	}

	/// <summary>A normal map in the DirectX convention: a map already in
	/// DirectX passes through unchanged (the kernel stores DirectX green);
	/// an OpenGL map has its green flipped.</summary>
	public static byte[] ToDirectX( byte[] normal, NormalMapConvention source ) =>
		source == NormalMapConvention.OpenGL ? FlipGreen( normal ) : normal;

	/// <summary>Pack ambient occlusion, roughness and metallic into one ORM
	/// image: R = occlusion, G = roughness, B = metallic, A = 255. Each input
	/// is an RGBA image at <paramref name="width"/> x <paramref name="height"/>
	/// and only its red channel is read (the maps are grey).</summary>
	public static byte[] PackOrm( byte[] occlusion, byte[] roughness, byte[] metallic, int width, int height )
	{
		if ( occlusion is null || roughness is null || metallic is null )
			throw new ArgumentNullException( "ORM packing needs all three channels." );

		var result = new byte[width * height * 4];
		for ( var i = 0; i < width * height; i++ )
		{
			var s = i * 4;
			result[s] = occlusion[s];
			result[s + 1] = roughness[s];
			result[s + 2] = metallic[s];
			result[s + 3] = 255;
		}
		return result;
	}

	/// <summary>Build the preset's images from a painted canvas at
	/// <paramref name="size"/> (4K working, 8K export, up to 8192). The paint
	/// is resampled to the size first; nothing is downscaled to fit VRAM —
	/// <see cref="VramWarning"/> reports the shortfall instead.</summary>
	public static UnrealExport Build( PaintCanvas paint, string asset, int size = WorkingSize, long? availableVramMb = null )
	{
		if ( paint is null )
			throw new ArgumentNullException( nameof( paint ) );

		if ( string.IsNullOrWhiteSpace( asset ) )
			throw new ArgumentException( "An asset name is needed for the {Asset}_{MapType} template.", nameof( asset ) );

		if ( size < 1 || size > ExportSize )
			throw new ArgumentOutOfRangeException( nameof( size ), "The Unreal preset tops out at 8K." );

		var export = new UnrealExport { Warning = VramWarning( size, availableVramMb ) };
		var atSize = AtSize( paint, size );
		var images = PaintTextureSet.Images( atSize );

		// BaseColor: the painted colour, with the opacity map stamped into its
		// alpha when one is painted (Unreal reads base-colour alpha for
		// translucency when the material is set translucent).
		var color = Find( images, "color" );
		var colorRgba = color.Rgba;
		var opacity = FindOrNull( images, "opacity" );
		if ( opacity is not null )
			StampOpacity( colorRgba, opacity.Rgba );
		export.Images.Add( new PaintTextureSet.Image { Suffix = FileName( asset, BaseColorType ), Rgba = colorRgba, Width = size, Height = size } );

		// Normal: the kernel's combined normal (painted + height-derived), already
		// DirectX green — passed through, not flipped.
		var normal = FindOrNull( images, "normal" );
		if ( normal is not null )
			export.Images.Add( new PaintTextureSet.Image { Suffix = FileName( asset, NormalType ), Rgba = ToDirectX( normal.Rgba, NormalMapConvention.DirectX ), Width = size, Height = size } );

		// ORM: baked AO first, then the height-derived AO the export writes,
		// then flat white; roughness and metallic fall back to their channel
		// bases when unpainted (0.6 rough, 0 metal).
		var ao = atSize.AmbientOcclusionMap is not null
			? atSize.AmbientOcclusionMap.Rgba
			: FindOrNull( images, "ao" )?.Rgba ?? Flat( size, 255 );
		var rough = FindOrNull( images, "rough" )?.Rgba ?? Flat( size, (byte)Math.Round( PaintChannels.Base( PaintChannel.Roughness ) * 255f ) );
		var metal = FindOrNull( images, "metal" )?.Rgba ?? Flat( size, (byte)Math.Round( PaintChannels.Base( PaintChannel.Metalness ) * 255f ) );
		export.Images.Add( new PaintTextureSet.Image { Suffix = FileName( asset, OrmType ), Rgba = PackOrm( ao, rough, metal, size, size ), Width = size, Height = size } );

		var emit = FindOrNull( images, "emit" );
		if ( emit is not null )
			export.Images.Add( new PaintTextureSet.Image { Suffix = FileName( asset, EmissiveType ), Rgba = emit.Rgba, Width = size, Height = size } );

		return export;
	}

	/// <summary>The whole paint canvas at <paramref name="size"/> x
	/// <paramref name="size"/>: the colour and every companion map resampled
	/// (bilinear, premultiplied). Same size hands the canvas back unchanged.</summary>
	static PaintCanvas AtSize( PaintCanvas paint, int size )
	{
		if ( paint.Width == size && paint.Height == size )
			return paint;

		var result = PaintLayers.Resample( paint, size, size );
		result.RoughnessMap = paint.RoughnessMap is null ? null : PaintLayers.Resample( paint.RoughnessMap, size, size );
		result.MetalnessMap = paint.MetalnessMap is null ? null : PaintLayers.Resample( paint.MetalnessMap, size, size );
		result.HeightMap = paint.HeightMap is null ? null : PaintLayers.Resample( paint.HeightMap, size, size );
		result.EmissiveMap = paint.EmissiveMap is null ? null : PaintLayers.Resample( paint.EmissiveMap, size, size );
		result.OpacityMap = paint.OpacityMap is null ? null : PaintLayers.Resample( paint.OpacityMap, size, size );
		result.NormalMap = paint.NormalMap is null ? null : PaintLayers.Resample( paint.NormalMap, size, size );
		result.AmbientOcclusionMap = paint.AmbientOcclusionMap is null ? null : PaintLayers.Resample( paint.AmbientOcclusionMap, size, size );
		return result;
	}

	static PaintTextureSet.Image Find( List<PaintTextureSet.Image> images, string suffix )
	{
		foreach ( var image in images )
			if ( image.Suffix == suffix )
				return image;
		throw new InvalidOperationException( $"The paint export has no '{suffix}' image." );
	}

	static PaintTextureSet.Image FindOrNull( List<PaintTextureSet.Image> images, string suffix )
	{
		foreach ( var image in images )
			if ( image.Suffix == suffix )
				return image;
		return null;
	}

	/// <summary>Stamp an opacity map's grey into an albedo image's alpha, texel
	/// for texel (both already at the export size). Mirrors the viewport's
	/// DocumentSession.StampOpacity.</summary>
	static void StampOpacity( byte[] albedo, byte[] opacity )
	{
		for ( var i = 0; i < albedo.Length / 4; i++ )
			albedo[i * 4 + 3] = opacity[i * 4];
	}

	static byte[] Flat( int size, byte grey )
	{
		var result = new byte[size * size * 4];
		for ( var i = 0; i < size * size; i++ )
		{
			result[i * 4] = grey;
			result[i * 4 + 1] = grey;
			result[i * 4 + 2] = grey;
			result[i * 4 + 3] = 255;
		}
		return result;
	}
}
