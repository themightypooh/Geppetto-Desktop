#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Effigy;

namespace Marionette.Desktop;

/// <summary>The target engine a preset packs for. One preset per engine: the packing, the normal
/// convention and the file names are the engine's, not Geppetto's.</summary>
public enum ExportEngine
{
	Unreal,
	UnityUrp,
	UnityHdrp,
	Godot,
	Gltf,
}

/// <summary>
/// One map a preset exports: its key (what the plan calls it), its file suffix, its colour
/// space, and the packing rule that produced it. Colour spaces follow the existing rule:
/// colour and emissive are sRGB, roughness / metalness / normal / occlusion are linear data
/// (see <see cref="PaintTextureSet"/>).
/// </summary>
public sealed record ExportMap(
	string Key,
	string Suffix,
	ColorSpaceInterpretation ColorSpace,
	string Packing );

/// <summary>
/// A per-engine export preset. The packing is the engine's documented convention, stated in
/// the map's <see cref="ExportMap.Packing"/> text so a wrong pack is visible in the plan, not
/// hidden in a byte:
///
/// Unreal ORM — R = occlusion, G = roughness, B = metallic; normal is DirectX green.
/// Unity URP — metallic in R, smoothness (1 − roughness) in the alpha of the same map;
///   occlusion is its own map; normal is OpenGL green.
/// Unity HDRP — mask map R = metallic, G = occlusion, B = detail (0 here), A = smoothness;
///   normal is OpenGL green.
/// Godot — ORM like Unreal (R = occlusion, G = roughness, B = metallic); normal is OpenGL
///   green, so the green channel flips from the paint source's DirectX green.
/// glTF 2.0 — metallic-roughness map G = roughness, B = metallic, R unused (written 1.0);
///   occlusion is its own map; normal is OpenGL green.
///
/// Paint normals are DirectX green (<see cref="PaintTextureSet.HeightToNormal"/>), so a
/// preset whose <see cref="NormalConvention"/> is OpenGL flips green on export
/// (<see cref="ChannelPacker.FlipGreen"/>); Unreal does not.
/// </summary>
public sealed class ExportPreset
{
	public ExportEngine Engine { get; }
	public string Name { get; }
	public NormalMapConvention NormalConvention { get; }
	/// <summary>True when the preset's normal convention differs from the paint source's
	/// DirectX green, i.e. the green channel must flip on export.</summary>
	public bool FlipGreenFromDirectX => NormalConvention == NormalMapConvention.OpenGL;
	public IReadOnlyList<ExportMap> Maps { get; }

	ExportPreset( ExportEngine engine, string name, NormalMapConvention convention, ExportMap[] maps )
	{
		Engine = engine;
		Name = name;
		NormalConvention = convention;
		Maps = maps;
	}

	public static ExportPreset For( ExportEngine engine ) => engine switch
	{
		ExportEngine.Unreal => new ExportPreset( engine, "Unreal Engine", NormalMapConvention.DirectX, new[]
		{
			new ExportMap( "BaseColor", "BaseColor", ColorSpaceInterpretation.Srgb, "albedo, sRGB" ),
			new ExportMap( "Normal", "Normal", ColorSpaceInterpretation.Linear, "tangent-space normal, DirectX green" ),
			new ExportMap( "ORM", "ORM", ColorSpaceInterpretation.Linear, "R occlusion, G roughness, B metallic" ),
			new ExportMap( "Emissive", "Emissive", ColorSpaceInterpretation.Srgb, "emissive colour, sRGB" ),
		} ),
		ExportEngine.UnityUrp => new ExportPreset( engine, "Unity URP", NormalMapConvention.OpenGL, new[]
		{
			new ExportMap( "BaseColor", "BaseColor", ColorSpaceInterpretation.Srgb, "albedo, sRGB" ),
			new ExportMap( "Normal", "Normal", ColorSpaceInterpretation.Linear, "tangent-space normal, OpenGL green" ),
			new ExportMap( "MetallicSmoothness", "MetallicSmoothness", ColorSpaceInterpretation.Linear, "R metallic, A smoothness (1 - roughness)" ),
			new ExportMap( "Occlusion", "Occlusion", ColorSpaceInterpretation.Linear, "ambient occlusion, linear" ),
			new ExportMap( "Emissive", "Emissive", ColorSpaceInterpretation.Srgb, "emissive colour, sRGB" ),
		} ),
		ExportEngine.UnityHdrp => new ExportPreset( engine, "Unity HDRP", NormalMapConvention.OpenGL, new[]
		{
			new ExportMap( "BaseColor", "BaseColor", ColorSpaceInterpretation.Srgb, "albedo, sRGB" ),
			new ExportMap( "Normal", "Normal", ColorSpaceInterpretation.Linear, "tangent-space normal, OpenGL green" ),
			new ExportMap( "MaskMap", "MaskMap", ColorSpaceInterpretation.Linear, "R metallic, G occlusion, B detail (0), A smoothness (1 - roughness)" ),
			new ExportMap( "Emissive", "Emissive", ColorSpaceInterpretation.Srgb, "emissive colour, sRGB" ),
		} ),
		ExportEngine.Godot => new ExportPreset( engine, "Godot", NormalMapConvention.OpenGL, new[]
		{
			new ExportMap( "BaseColor", "BaseColor", ColorSpaceInterpretation.Srgb, "albedo, sRGB" ),
			new ExportMap( "Normal", "Normal", ColorSpaceInterpretation.Linear, "tangent-space normal, OpenGL green" ),
			new ExportMap( "ORM", "ORM", ColorSpaceInterpretation.Linear, "R occlusion, G roughness, B metallic" ),
			new ExportMap( "Emissive", "Emissive", ColorSpaceInterpretation.Srgb, "emissive colour, sRGB" ),
		} ),
		ExportEngine.Gltf => new ExportPreset( engine, "glTF 2.0", NormalMapConvention.OpenGL, new[]
		{
			new ExportMap( "BaseColor", "BaseColor", ColorSpaceInterpretation.Srgb, "albedo, sRGB" ),
			new ExportMap( "Normal", "Normal", ColorSpaceInterpretation.Linear, "tangent-space normal, OpenGL green" ),
			new ExportMap( "MetallicRoughness", "MetallicRoughness", ColorSpaceInterpretation.Linear, "G roughness, B metallic, R unused (1.0)" ),
			new ExportMap( "Occlusion", "Occlusion", ColorSpaceInterpretation.Linear, "ambient occlusion, linear" ),
			new ExportMap( "Emissive", "Emissive", ColorSpaceInterpretation.Srgb, "emissive colour, sRGB" ),
		} ),
		_ => throw new ArgumentException( $"No export preset for engine {engine}.", nameof( engine ) ),
	};
}

/// <summary>
/// Channel packing over RGBA byte images in <see cref="PaintTextureSet.Image"/> form
/// (R = G = B = the grey value, A = 255). Every pack validates its inputs: null where a channel
/// is required, a length that is not width × height × 4, or mismatched sizes between channels
/// are refused, not silently resampled — a pack that invents texels is a pack that lies about
/// the asset. Missing occlusion defaults to white (no occlusion), because an unpainted AO is
/// "no darkening", the same default the paint export already uses.
/// </summary>
public static class ChannelPacker
{
	public static byte[] SolidGrey( byte value, int width, int height )
	{
		if ( width <= 0 ) throw new ArgumentOutOfRangeException( nameof( width ) );
		if ( height <= 0 ) throw new ArgumentOutOfRangeException( nameof( height ) );
		var rgba = new byte[width * height * 4];
		for ( var i = 0; i < width * height; i++ )
		{
			rgba[i * 4] = value; rgba[i * 4 + 1] = value; rgba[i * 4 + 2] = value; rgba[i * 4 + 3] = 255;
		}
		return rgba;
	}

	/// <summary>The grey value of one pixel of a packed/grey image (its R channel).</summary>
	public static byte GreyValue( byte[] rgba, int pixel ) => rgba[pixel * 4];

	static void RequireImage( byte[]? rgba, int width, int height, string name )
	{
		if ( rgba is null ) throw new ArgumentNullException( name );
		if ( width <= 0 ) throw new ArgumentOutOfRangeException( nameof( width ) );
		if ( height <= 0 ) throw new ArgumentOutOfRangeException( nameof( height ) );
		if ( rgba.Length != width * height * 4 )
			throw new ArgumentException( $"{name} is {rgba.Length} bytes, expected {width * height * 4} for {width}x{height}.", name );
	}

	static byte[] Pack( byte[]? r, byte[]? g, byte[]? b, byte[]? a, int width, int height )
	{
		var o = new byte[width * height * 4];
		for ( var i = 0; i < width * height; i++ )
		{
			o[i * 4] = r is null ? (byte)255 : r[i * 4];
			o[i * 4 + 1] = g is null ? (byte)255 : g[i * 4];
			o[i * 4 + 2] = b is null ? (byte)255 : b[i * 4];
			o[i * 4 + 3] = a is null ? (byte)255 : a[i * 4];
		}
		return o;
	}

	/// <summary>Unreal / Godot ORM: R occlusion, G roughness, B metallic. Occlusion may be
	/// null (white); roughness and metallic are required.</summary>
	public static byte[] PackOrm( byte[]? occlusion, byte[] rough, byte[] metal, int width, int height )
	{
		RequireImage( rough, width, height, nameof( rough ) );
		RequireImage( metal, width, height, nameof( metal ) );
		if ( occlusion is not null ) RequireImage( occlusion, width, height, nameof( occlusion ) );
		return Pack( occlusion, rough, metal, null, width, height );
	}

	/// <summary>glTF metallic-roughness: G roughness, B metallic, R unused (1.0).</summary>
	public static byte[] PackMetallicRoughness( byte[] rough, byte[] metal, int width, int height )
	{
		RequireImage( rough, width, height, nameof( rough ) );
		RequireImage( metal, width, height, nameof( metal ) );
		return Pack( null, rough, metal, null, width, height );
	}

	/// <summary>Unity URP metallic-smoothness: R metallic, A smoothness (1 − roughness).</summary>
	public static byte[] PackMetallicSmoothness( byte[] metal, byte[] rough, int width, int height )
	{
		RequireImage( metal, width, height, nameof( metal ) );
		RequireImage( rough, width, height, nameof( rough ) );
		var o = new byte[width * height * 4];
		for ( var i = 0; i < width * height; i++ )
		{
			o[i * 4] = metal[i * 4];
			o[i * 4 + 1] = 0; o[i * 4 + 2] = 0;
			o[i * 4 + 3] = (byte)(255 - rough[i * 4]);
		}
		return o;
	}

	/// <summary>Unity HDRP mask map: R metallic, G occlusion, B detail (0), A smoothness
	/// (1 − roughness). Occlusion may be null (white).</summary>
	public static byte[] PackMaskMap( byte[] metal, byte[]? occlusion, byte[] rough, int width, int height )
	{
		RequireImage( metal, width, height, nameof( metal ) );
		RequireImage( rough, width, height, nameof( rough ) );
		if ( occlusion is not null ) RequireImage( occlusion, width, height, nameof( occlusion ) );
		var o = new byte[width * height * 4];
		for ( var i = 0; i < width * height; i++ )
		{
			o[i * 4] = metal[i * 4];
			o[i * 4 + 1] = occlusion is null ? (byte)255 : occlusion[i * 4];
			o[i * 4 + 2] = 0;
			o[i * 4 + 3] = (byte)(255 - rough[i * 4]);
		}
		return o;
	}

	/// <summary>Flip a normal map's green channel (DirectX green ↔ OpenGL green). Applied
	/// twice it is the identity, so a double flip is a no-op, not a corruption.</summary>
	public static byte[] FlipGreen( byte[] normal, int width, int height )
	{
		RequireImage( normal, width, height, nameof( normal ) );
		var o = (byte[])normal.Clone();
		for ( var i = 0; i < width * height; i++ )
			o[i * 4 + 1] = (byte)(255 - normal[i * 4 + 1]);
		return o;
	}
}

/// <summary>One file an export plan writes: which set and UDIM tile it belongs to, which map,
/// its file name, and its size and colour space.</summary>
public sealed record PlannedFile(
	string SetId,
	int TileNumber,
	string MapKey,
	string FileName,
	int Width,
	int Height,
	ColorSpaceInterpretation ColorSpace );

/// <summary>
/// Per-engine export planning for texture sets. This plans — it does not write files: the
/// caller writes the packed images (see <see cref="ChannelPacker"/>) under the planned names,
/// the same way <see cref="PaintTextureSet.Write"/> writes the paint set. A plan is per set
/// and per tile, so one set's export never pulls in another set's files, and a hero UDIM
/// export names every tile at that tile's own resolution.
/// </summary>
public static class TextureSetExporter
{
	/// <summary>
	/// The file name for one map: <c>{asset}_{set}[_{tile}]_{map}.png</c>. The tile number
	/// is omitted for a single-tile set (so a one-tile export keeps the plain
	/// <c>{asset}_{set}.png</c> form the paint export already uses), and the BaseColor map
	/// takes no suffix. Names are sanitised to [A-Za-z0-9_-]; anything else becomes '_'.
	/// </summary>
	public static string FileName( string assetName, string setName, int tileNumber, int tileCount, string mapSuffix )
	{
		var asset = Sanitize( assetName );
		var set = Sanitize( setName );
		if ( asset.Length == 0 ) throw new ArgumentException( "An export needs an asset name.", nameof( assetName ) );
		if ( set.Length == 0 ) throw new ArgumentException( "An export needs a set name.", nameof( setName ) );
		var sb = new StringBuilder();
		sb.Append( asset ).Append( '_' ).Append( set );
		if ( tileCount > 1 ) sb.Append( '_' ).Append( tileNumber );
		if ( mapSuffix != "BaseColor" ) sb.Append( '_' ).Append( Sanitize( mapSuffix ) );
		sb.Append( ".png" );
		return sb.ToString();
	}

	static string Sanitize( string name )
	{
		if ( name is null ) return "";
		var sb = new StringBuilder( name.Length );
		foreach ( var c in name )
			sb.Append( char.IsLetterOrDigit( c ) || c == '_' || c == '-' ? c : '_' );
		return sb.ToString();
	}

	static bool IncludeMap( TextureSet set, string mapKey ) => mapKey switch
	{
		"BaseColor" => true,
		"Normal" => set.HasChannel( PaintChannel.Normal ) || set.HasChannel( PaintChannel.Height ),
		"ORM" or "MetallicRoughness" or "MetallicSmoothness" or "MaskMap" =>
			set.HasChannel( PaintChannel.Roughness ) || set.HasChannel( PaintChannel.Metalness ),
		"Occlusion" => set.HasChannel( PaintChannel.Height ),
		"Emissive" => set.HasChannel( PaintChannel.Emissive ),
		_ => false,
	};

	public static List<PlannedFile> PlanForSet( string assetName, TextureSet set, ExportEngine engine )
	{
		if ( string.IsNullOrWhiteSpace( assetName ) )
			throw new ArgumentException( "An export needs an asset name.", nameof( assetName ) );
		if ( set is null ) throw new ArgumentNullException( nameof( set ) );
		var preset = ExportPreset.For( engine );
		var plan = new List<PlannedFile>();
		foreach ( var tile in set.Tiles )
		{
			foreach ( var map in preset.Maps )
			{
				if ( !IncludeMap( set, map.Key ) ) continue;
				plan.Add( new PlannedFile(
					set.MaterialId, tile.TileNumber, map.Key,
					FileName( assetName, set.Name, tile.TileNumber, set.Tiles.Count, map.Suffix ),
					tile.Resolution, tile.Resolution, map.ColorSpace ) );
			}
		}
		return plan;
	}

	/// <summary>The whole asset's plan: every set, every tile, every map the preset wants for
	/// that set. Sets stay independent — each file names exactly one set.</summary>
	public static List<PlannedFile> Plan( string assetName, IEnumerable<TextureSet> sets, ExportEngine engine )
	{
		if ( sets is null ) throw new ArgumentNullException( nameof( sets ) );
		var plan = new List<PlannedFile>();
		foreach ( var set in sets )
			plan.AddRange( PlanForSet( assetName, set, engine ) );
		return plan;
	}
}
