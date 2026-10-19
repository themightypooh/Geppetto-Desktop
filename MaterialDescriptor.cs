#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Effigy;

namespace Marionette.Desktop;

public enum TextureWrap
{
	Repeat,
	Clamp
}

public enum TextureFilter
{
	Linear,
	Nearest
}

public enum NormalMapConvention
{
	OpenGL,  // +Y (green channel points up in texture space)
	DirectX  // -Y (green channel points down in texture space)
}

public enum ColorSpaceInterpretation
{
	Srgb,    // Non-linear color data (albedo, emission)
	Linear   // Linear data (normals, roughness, metallic, ambient occlusion)
}

public enum MaterialChannel
{
	BaseColor,
	Normal,
	Roughness,
	Metallic,
	Emission,
	AmbientOcclusion
}

/// <summary>
/// CPU-only texture binding descriptor. Carries asset reference, UV set index, sampler modes,
/// normal map convention, and color space interpretation. Contains no GPU or OpenGL handles.
/// </summary>
public sealed record TextureBinding(
	AssetReference Reference,
	int UvSetIndex = 0,
	TextureWrap Wrap = TextureWrap.Repeat,
	TextureFilter Filter = TextureFilter.Linear,
	ColorSpaceInterpretation ColorSpace = ColorSpaceInterpretation.Linear,
	NormalMapConvention NormalConvention = NormalMapConvention.OpenGL
)
{
	public TextureWrap WrapMode => Wrap;
	public TextureFilter FilterMode => Filter;

	public static TextureBinding ForBaseColor( AssetReference reference, int uvSet = 0,
		TextureWrap wrap = TextureWrap.Repeat, TextureFilter filter = TextureFilter.Linear ) =>
		new( reference, uvSet, wrap, filter, ColorSpaceInterpretation.Srgb, NormalMapConvention.OpenGL );

	public static TextureBinding ForEmission( AssetReference reference, int uvSet = 0,
		TextureWrap wrap = TextureWrap.Repeat, TextureFilter filter = TextureFilter.Linear ) =>
		new( reference, uvSet, wrap, filter, ColorSpaceInterpretation.Srgb, NormalMapConvention.OpenGL );

	public static TextureBinding ForNormal( AssetReference reference, int uvSet = 0,
		TextureWrap wrap = TextureWrap.Repeat, TextureFilter filter = TextureFilter.Linear,
		NormalMapConvention convention = NormalMapConvention.OpenGL ) =>
		new( reference, uvSet, wrap, filter, ColorSpaceInterpretation.Linear, convention );

	public static TextureBinding ForData( AssetReference reference, int uvSet = 0,
		TextureWrap wrap = TextureWrap.Repeat, TextureFilter filter = TextureFilter.Linear ) =>
		new( reference, uvSet, wrap, filter, ColorSpaceInterpretation.Linear, NormalMapConvention.OpenGL );
}

/// <summary>
/// CPU-only material descriptor with stable identity, PBR factors, and optional channel bindings.
/// Contains strictly no OpenGL/GPU handles or platform UI dependencies.
/// </summary>
public sealed record MaterialDescriptor
{
	public string Id { get; init; }
	public string Name { get; init; }

	// Color & factor properties with documented defaults
	public Vec4 BaseColorFactor { get; init; } = new( 1f, 1f, 1f, 1f );
	public float RoughnessFactor { get; init; } = 0.5f;
	public float MetallicFactor { get; init; } = 0.0f;
	public Vec3 EmissionFactor { get; init; } = Vec3.Zero;

	// Channel texture bindings
	public TextureBinding? BaseColorMap { get; init; }
	public TextureBinding? NormalMap { get; init; }
	public TextureBinding? RoughnessMap { get; init; }
	public TextureBinding? MetallicMap { get; init; }
	public TextureBinding? EmissionMap { get; init; }
	public TextureBinding? AmbientOcclusionMap { get; init; }

	// Ergonomic property aliases
	public TextureBinding? BaseColor => BaseColorMap;
	public TextureBinding? Normal => NormalMap;
	public TextureBinding? Roughness => RoughnessMap;
	public TextureBinding? Metallic => MetallicMap;
	public TextureBinding? Emission => EmissionMap;
	public TextureBinding? AmbientOcclusion => AmbientOcclusionMap;

	public MaterialDescriptor( string id, string? name = null )
	{
		if ( string.IsNullOrWhiteSpace( id ) ) throw new ArgumentException( "Material ID must not be empty.", nameof( id ) );
		Id = id;
		Name = name ?? id;
	}

	public TextureBinding? GetChannel( MaterialChannel channel ) => channel switch
	{
		MaterialChannel.BaseColor => BaseColorMap,
		MaterialChannel.Normal => NormalMap,
		MaterialChannel.Roughness => RoughnessMap,
		MaterialChannel.Metallic => MetallicMap,
		MaterialChannel.Emission => EmissionMap,
		MaterialChannel.AmbientOcclusion => AmbientOcclusionMap,
		_ => null
	};

	public MaterialDescriptor WithChannel( MaterialChannel channel, TextureBinding? binding ) => channel switch
	{
		MaterialChannel.BaseColor => this with { BaseColorMap = binding },
		MaterialChannel.Normal => this with { NormalMap = binding },
		MaterialChannel.Roughness => this with { RoughnessMap = binding },
		MaterialChannel.Metallic => this with { MetallicMap = binding },
		MaterialChannel.Emission => this with { EmissionMap = binding },
		MaterialChannel.AmbientOcclusion => this with { AmbientOcclusionMap = binding },
		_ => throw new ArgumentOutOfRangeException( nameof( channel ), channel, null )
	};

	public static MaterialDescriptor NeutralDefault( string id = "default_neutral", string name = "Neutral Default" ) =>
		new( id, name )
		{
			BaseColorFactor = new Vec4( 1f, 1f, 1f, 1f ),
			RoughnessFactor = 1.0f,
			MetallicFactor = 0.0f,
			EmissionFactor = Vec3.Zero
		};
}

/// <summary>
/// Diagnostic message for material resolution or texture loading issues.
/// </summary>
public sealed record MaterialDiagnostic(
	DiagnosticSeverity Severity,
	string MaterialId,
	string Message,
	string? AssetPath = null
);

/// <summary>
/// CPU decoded pixel buffer in RGBA8 format (row 0 at top).
/// </summary>
public sealed record DecodedImage( int Width, int Height, byte[] Rgba )
{
	public static DecodedImage NeutralWhite { get; } = new( 1, 1, new byte[] { 255, 255, 255, 255 } );
	public static DecodedImage NeutralFlatNormal { get; } = new( 1, 1, new byte[] { 128, 128, 255, 255 } );
	public static DecodedImage NeutralRoughness { get; } = new( 1, 1, new byte[] { 255, 255, 255, 255 } );
	public static DecodedImage NeutralMetallic { get; } = new( 1, 1, new byte[] { 0, 0, 0, 255 } );
	public static DecodedImage NeutralEmission { get; } = new( 1, 1, new byte[] { 0, 0, 0, 255 } );
	public static DecodedImage NeutralAmbientOcclusion { get; } = new( 1, 1, new byte[] { 255, 255, 255, 255 } );

	public static DecodedImage NeutralForChannel( MaterialChannel channel ) => channel switch
	{
		MaterialChannel.BaseColor => NeutralWhite,
		MaterialChannel.Normal => NeutralFlatNormal,
		MaterialChannel.Roughness => NeutralRoughness,
		MaterialChannel.Metallic => NeutralMetallic,
		MaterialChannel.Emission => NeutralEmission,
		MaterialChannel.AmbientOcclusion => NeutralAmbientOcclusion,
		_ => NeutralWhite
	};
}

public sealed record ImageDecodeResult( bool Success, DecodedImage? Image, MaterialDiagnostic? Diagnostic );

/// <summary>
/// CPU-only image decoder utilizing Effigy.PngReader.
/// Rejects non-PNG, paletted, or corrupt image data with clear diagnostics.
/// </summary>
public static class CpuImageReader
{
	public static ImageDecodeResult DecodePng( byte[]? data, string sourceId = "image", string? assetPath = null )
	{
		if ( data is null || data.Length == 0 )
			return new( false, null, new( DiagnosticSeverity.Error, sourceId, "Image data is empty or null.", assetPath ) );

		if ( data.Length < 8 || data[0] != 0x89 || data[1] != (byte)'P' || data[2] != (byte)'N' || data[3] != (byte)'G' )
		{
			string format = "unknown";
			if ( data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8 ) format = "JPEG";
			else if ( data.Length >= 6 && System.Text.Encoding.ASCII.GetString( data, 0, 6 ) is "GIF87a" or "GIF89a" ) format = "GIF";
			else if ( data.Length >= 4 && System.Text.Encoding.ASCII.GetString( data, 0, 4 ) == "RIFF" ) format = "RIFF/WebP";
			else if ( data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D ) format = "BMP";

			return new( false, null, new( DiagnosticSeverity.Error, sourceId,
				$"Unsupported image format '{format}' at '{assetPath ?? sourceId}'. Only 8- and 16-bit non-paletted PNG is supported.", assetPath ) );
		}

		// Inspect IHDR chunk for paletted colour type (colour type 3)
		if ( data.Length >= 29 && data[12] == (byte)'I' && data[13] == (byte)'H' && data[14] == (byte)'D' && data[15] == (byte)'R' )
		{
			int colourType = data[25];
			if ( colourType == 3 )
			{
				return new( false, null, new( DiagnosticSeverity.Error, sourceId,
					$"Unsupported paletted PNG format (colour type 3) at '{assetPath ?? sourceId}'. Only RGB/RGBA/greyscale are supported.", assetPath ) );
			}
		}

		try
		{
			var (rgba, width, height) = Effigy.PngReader.Read( data );
			return new( true, new DecodedImage( width, height, rgba ), null );
		}
		catch ( NotSupportedException ex )
		{
			return new( false, null, new( DiagnosticSeverity.Error, sourceId,
				$"Unsupported PNG feature in '{assetPath ?? sourceId}': {ex.Message}", assetPath ) );
		}
		catch ( InvalidDataException ex )
		{
			return new( false, null, new( DiagnosticSeverity.Error, sourceId,
				$"Corrupt PNG data in '{assetPath ?? sourceId}': {ex.Message}", assetPath ) );
		}
		catch ( Exception ex )
		{
			return new( false, null, new( DiagnosticSeverity.Error, sourceId,
				$"Failed decoding PNG '{assetPath ?? sourceId}': {ex.Message}", assetPath ) );
		}
	}
}

/// <summary>
/// Source .vmat parser that extracts only declared PBR channel keys without loading engine shaders.
/// </summary>
public static class VmatParser
{
	private static readonly HashSet<string> SupportedShaders = new( StringComparer.OrdinalIgnoreCase )
	{
		"shaders/complex.shader",
		"complex.shader",
		"shaders/simple.shader",
		"simple.shader",
		"shaders/default.shader",
		"default.shader"
	};

	public static (MaterialDescriptor Descriptor, List<MaterialDiagnostic> Diagnostics) Parse(
		string content,
		string materialId,
		string? vmatPath = null )
	{
		var diagnostics = new List<MaterialDiagnostic>();
		var descriptor = new MaterialDescriptor( materialId );

		using var reader = new StringReader( content );
		string? line;
		while ( (line = reader.ReadLine()) != null )
		{
			line = line.Trim();
			if ( string.IsNullOrEmpty( line ) || line.StartsWith( "//" ) || line.StartsWith( "#" ) )
				continue;
			if ( line == "{" || line == "}" || line.StartsWith( "Layer0" ) )
				continue;

			int firstSpace = line.IndexOfAny( new[] { ' ', '\t' } );
			if ( firstSpace < 0 )
				continue;

			string key = line.Substring( 0, firstSpace ).Trim();
			string val = line.Substring( firstSpace + 1 ).Trim().Trim( '"' );

			if ( key.Equals( "shader", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !SupportedShaders.Contains( val ) )
				{
					diagnostics.Add( new( DiagnosticSeverity.Warning, materialId,
						$"Unsupported shader '{val}' in material '{materialId}'. Falling back to standard CPU PBR descriptor.", vmatPath ) );
				}
				continue;
			}

			if ( key.Equals( "TextureColor", StringComparison.OrdinalIgnoreCase ) ||
				 key.Equals( "g_tColor", StringComparison.OrdinalIgnoreCase ) ||
				 key.Equals( "TextureBaseColor", StringComparison.OrdinalIgnoreCase ) ||
				 key.Equals( "TextureAlbedo", StringComparison.OrdinalIgnoreCase ) )
			{
				var refAsset = new AssetReference( $"{materialId}_color", val );
				descriptor = descriptor with { BaseColorMap = TextureBinding.ForBaseColor( refAsset ) };
			}
			else if ( key.Equals( "TextureNormal", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "g_tNormal", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "TextureNormalMap", StringComparison.OrdinalIgnoreCase ) )
			{
				var refAsset = new AssetReference( $"{materialId}_normal", val );
				descriptor = descriptor with { NormalMap = TextureBinding.ForNormal( refAsset ) };
			}
			else if ( key.Equals( "TextureRoughness", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "g_tRoughness", StringComparison.OrdinalIgnoreCase ) )
			{
				var refAsset = new AssetReference( $"{materialId}_roughness", val );
				descriptor = descriptor with { RoughnessMap = TextureBinding.ForData( refAsset ) };
			}
			else if ( key.Equals( "TextureMetalness", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "TextureMetallic", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "g_tMetalness", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "g_tMetallic", StringComparison.OrdinalIgnoreCase ) )
			{
				var refAsset = new AssetReference( $"{materialId}_metallic", val );
				descriptor = descriptor with { MetallicMap = TextureBinding.ForData( refAsset ) };
			}
			else if ( key.Equals( "TextureEmission", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "TextureSelfIllum", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "g_tEmission", StringComparison.OrdinalIgnoreCase ) )
			{
				var refAsset = new AssetReference( $"{materialId}_emission", val );
				descriptor = descriptor with { EmissionMap = TextureBinding.ForEmission( refAsset ) };
			}
			else if ( key.Equals( "TextureAmbientOcclusion", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "TextureAO", StringComparison.OrdinalIgnoreCase ) ||
					  key.Equals( "g_tAmbientOcclusion", StringComparison.OrdinalIgnoreCase ) )
			{
				var refAsset = new AssetReference( $"{materialId}_ao", val );
				descriptor = descriptor with { AmbientOcclusionMap = TextureBinding.ForData( refAsset ) };
			}
			else
			{
				if ( key.StartsWith( "F_" ) || key.StartsWith( "g_v" ) || key.StartsWith( "g_fl" ) || key.StartsWith( "Dynamic" ) )
				{
					diagnostics.Add( new( DiagnosticSeverity.Warning, materialId,
						$"Unsupported .vmat parameter or semantic '{key}' in '{materialId}'.", vmatPath ) );
				}
			}
		}

		return (descriptor, diagnostics);
	}
}

public sealed record MaterialResolutionResult(
	int SlotIndex,
	string SlotName,
	MaterialDescriptor Descriptor,
	bool Success,
	IReadOnlyList<MaterialDiagnostic> Diagnostics
);

/// <summary>
/// Material resolution service that maps mesh material slots to CPU material descriptors.
/// Scoped to a document/project resolver instance to prevent cross-document override collisions.
/// Performs strictly read-only filesystem operations.
/// </summary>
public sealed class MaterialResolver
{
	private readonly AssetPathResolver? _pathResolver;
	private readonly string _documentId;
	private readonly Dictionary<string, MaterialDescriptor> _slotOverrides = new( StringComparer.Ordinal );
	private readonly Dictionary<int, MaterialDescriptor> _indexOverrides = new();
	private readonly Dictionary<string, AssetReference> _vmatOverrides = new( StringComparer.Ordinal );
	private readonly HashSet<string> _requiredSlots = new( StringComparer.Ordinal );
	private readonly HashSet<string> _emittedDiagnosticKeys = new( StringComparer.Ordinal );
	private readonly List<MaterialDiagnostic> _diagnostics = new();
	private readonly Dictionary<string, DecodedImage> _decodedImages = new( StringComparer.Ordinal );

	public AssetPathResolver? PathResolver => _pathResolver;
	public string DocumentId => _documentId;
	public IReadOnlyList<MaterialDiagnostic> Diagnostics => _diagnostics;

	public MaterialResolver( AssetPathResolver? pathResolver = null, string documentId = "default" )
	{
		_pathResolver = pathResolver;
		_documentId = documentId;
	}

	public MaterialResolver( string projectRoot, string documentId = "default" )
		: this( new AssetPathResolver( projectRoot ), documentId )
	{
	}

	public void SetOverride( string slotName, MaterialDescriptor descriptor, bool required = false )
	{
		ArgumentNullException.ThrowIfNull( slotName );
		ArgumentNullException.ThrowIfNull( descriptor );
		_slotOverrides[slotName] = descriptor;
		if ( required ) _requiredSlots.Add( slotName );
	}

	public void SetOverride( int slotIndex, MaterialDescriptor descriptor, bool required = false )
	{
		ArgumentNullException.ThrowIfNull( descriptor );
		_indexOverrides[slotIndex] = descriptor;
		if ( required ) _requiredSlots.Add( slotIndex.ToString() );
	}

	public void SetOverride( string slotName, AssetReference vmatReference, bool required = false )
	{
		ArgumentNullException.ThrowIfNull( slotName );
		ArgumentNullException.ThrowIfNull( vmatReference );
		_vmatOverrides[slotName] = vmatReference;
		if ( required ) _requiredSlots.Add( slotName );
	}

	public void SetRequiredSlot( string slotName )
	{
		ArgumentNullException.ThrowIfNull( slotName );
		_requiredSlots.Add( slotName );
	}

	public void SetRequiredSlot( int slotIndex )
	{
		_requiredSlots.Add( slotIndex.ToString() );
	}

	public MaterialResolutionResult ResolveSlot( string slotName ) => ResolveSlot( 0, slotName );

	public MaterialResolutionResult ResolveSlot( int slotIndex, string slotName, bool validateTextures = true )
	{
		var resultDiagnostics = new List<MaterialDiagnostic>();
		bool isRequired = _requiredSlots.Contains( slotName ) || _requiredSlots.Contains( slotIndex.ToString() );

		MaterialDescriptor? descriptor = null;
		if ( _indexOverrides.TryGetValue( slotIndex, out var idxDesc ) )
		{
			descriptor = idxDesc;
		}
		else if ( _slotOverrides.TryGetValue( slotName, out var nameDesc ) )
		{
			descriptor = nameDesc;
		}
		else if ( _vmatOverrides.TryGetValue( slotName, out var vmatRef ) )
		{
			if ( _pathResolver is null )
			{
				var diag = EmitDiagnostic( DiagnosticSeverity.Error, slotName,
					$"Cannot resolve .vmat override '{vmatRef.Path}' without an AssetPathResolver.", vmatRef.Path );
				resultDiagnostics.Add( diag );
				return new( slotIndex, slotName, MaterialDescriptor.NeutralDefault( slotName ), false, resultDiagnostics );
			}

			try
			{
				var resolvedVmat = _pathResolver.ResolveRead( vmatRef, requireExists: true );
				string content = File.ReadAllText( resolvedVmat.CanonicalPath );
				var (vmatDesc, vmatDiags) = VmatParser.Parse( content, slotName, resolvedVmat.ReferencePath );
				foreach ( var vd in vmatDiags )
				{
					var em = EmitDiagnostic( vd.Severity, vd.MaterialId, vd.Message, vd.AssetPath );
					resultDiagnostics.Add( em );
				}
				descriptor = vmatDesc;
			}
			catch ( Exception ex )
			{
				var diag = EmitDiagnostic( isRequired ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
					slotName, $"Failed reading .vmat override '{vmatRef.Path}': {ex.Message}", vmatRef.Path );
				resultDiagnostics.Add( diag );
				if ( isRequired )
				{
					return new( slotIndex, slotName, MaterialDescriptor.NeutralDefault( slotName ), false, resultDiagnostics );
				}
			}
		}

		if ( descriptor is null )
		{
			if ( isRequired )
			{
				var diag = EmitDiagnostic( DiagnosticSeverity.Error, slotName,
					$"Required override for slot '{slotName}' (index {slotIndex}) is missing.", slotName );
				resultDiagnostics.Add( diag );
				return new( slotIndex, slotName, MaterialDescriptor.NeutralDefault( slotName ), false, resultDiagnostics );
			}

			if ( _pathResolver != null && slotName.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
			{
				try
				{
					var vmatRef = new AssetReference( slotName, slotName );
					var resolvedVmat = _pathResolver.ResolveRead( vmatRef, requireExists: true );
					string content = File.ReadAllText( resolvedVmat.CanonicalPath );
					var (vmatDesc, vmatDiags) = VmatParser.Parse( content, slotName, resolvedVmat.ReferencePath );
					foreach ( var vd in vmatDiags )
					{
						var em = EmitDiagnostic( vd.Severity, vd.MaterialId, vd.Message, vd.AssetPath );
						resultDiagnostics.Add( em );
					}
					descriptor = vmatDesc;
				}
				catch ( Exception ex )
				{
					var diag = EmitDiagnostic( DiagnosticSeverity.Warning, slotName,
						$"Could not load .vmat slot '{slotName}': {ex.Message}", slotName );
					resultDiagnostics.Add( diag );
				}
			}

			if ( descriptor is null )
			{
				var paletteEntry = MaterialPalette.ForName( slotName );
				descriptor = new MaterialDescriptor( slotName )
				{
					BaseColorFactor = new Vec4( paletteEntry.R, paletteEntry.G, paletteEntry.B, 1f ),
					RoughnessFactor = paletteEntry.Roughness,
					MetallicFactor = paletteEntry.Metallic,
					EmissionFactor = new Vec3( paletteEntry.Er, paletteEntry.Eg, paletteEntry.Eb )
				};
			}
		}

		bool textureSuccess = true;
		if ( validateTextures && _pathResolver != null )
		{
			foreach ( MaterialChannel channel in Enum.GetValues<MaterialChannel>() )
			{
				var binding = descriptor.GetChannel( channel );
				if ( binding is null )
				{
					// No map authored: neutral factor applies cleanly without error or diagnostic.
					continue;
				}

				try
				{
					var resolved = _pathResolver.ResolveRead( binding.Reference, requireExists: false );
					if ( !resolved.Exists )
					{
						var diag = EmitDiagnostic( isRequired ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
							descriptor.Id,
							$"Material '{descriptor.Id}': authored {channel} map '{binding.Reference.Path}' could not be found.",
							binding.Reference.Path );
						resultDiagnostics.Add( diag );
						if ( isRequired ) textureSuccess = false;
					}
					else
					{
						var decodeResult = DecodeOrGetCached( resolved.CanonicalPath, descriptor.Id );
						if ( !decodeResult.Success )
						{
							var diag = EmitDiagnostic( isRequired ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
								descriptor.Id,
								$"Material '{descriptor.Id}': authored {channel} map '{binding.Reference.Path}' could not be decoded: {decodeResult.Diagnostic?.Message}",
								binding.Reference.Path );
							resultDiagnostics.Add( diag );
							if ( isRequired ) textureSuccess = false;
						}
					}
				}
				catch ( Exception ex )
				{
					var diag = EmitDiagnostic( isRequired ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
						descriptor.Id,
						$"Material '{descriptor.Id}': authored {channel} map '{binding.Reference.Path}' resolution error: {ex.Message}",
						binding.Reference.Path );
					resultDiagnostics.Add( diag );
					if ( isRequired ) textureSuccess = false;
				}
			}
		}

		return new MaterialResolutionResult(
			slotIndex,
			slotName,
			descriptor,
			Success: textureSuccess,
			Diagnostics: resultDiagnostics
		);
	}

	public ImageDecodeResult LoadTexture( TextureBinding binding, string materialId = "material" )
	{
		ArgumentNullException.ThrowIfNull( binding );
		if ( _pathResolver is null )
			return new( false, null, new( DiagnosticSeverity.Error, materialId, "No AssetPathResolver configured.", binding.Reference.Path ) );

		try
		{
			var resolved = _pathResolver.ResolveRead( binding.Reference, requireExists: false );
			if ( !resolved.Exists )
			{
				var diag = EmitDiagnostic( DiagnosticSeverity.Warning, materialId,
					$"Texture '{binding.Reference.Path}' not found.", binding.Reference.Path );
				return new( false, null, diag );
			}

			return DecodeOrGetCached( resolved.CanonicalPath, materialId );
		}
		catch ( Exception ex )
		{
			var diag = EmitDiagnostic( DiagnosticSeverity.Error, materialId,
				$"Failed resolving texture path '{binding.Reference.Path}': {ex.Message}", binding.Reference.Path );
			return new( false, null, diag );
		}
	}

	public DecodedImage GetChannelImageOrFallback( MaterialDescriptor descriptor, MaterialChannel channel )
	{
		var binding = descriptor.GetChannel( channel );
		if ( binding is not null )
		{
			var result = LoadTexture( binding, descriptor.Id );
			if ( result.Success && result.Image is not null )
				return result.Image;
		}
		return DecodedImage.NeutralForChannel( channel );
	}

	private ImageDecodeResult DecodeOrGetCached( string canonicalPath, string materialId )
	{
		if ( _decodedImages.TryGetValue( canonicalPath, out var cached ) )
			return new( true, cached, null );

		try
		{
			byte[] bytes = File.ReadAllBytes( canonicalPath );
			var result = CpuImageReader.DecodePng( bytes, materialId, canonicalPath );
			if ( result.Success && result.Image != null )
			{
				_decodedImages[canonicalPath] = result.Image;
			}
			return result;
		}
		catch ( Exception ex )
		{
			return new( false, null, new( DiagnosticSeverity.Error, materialId,
				$"Failed reading texture file '{canonicalPath}': {ex.Message}", canonicalPath ) );
		}
	}

	private MaterialDiagnostic EmitDiagnostic( DiagnosticSeverity severity, string materialId, string message, string? assetPath )
	{
		string key = $"{materialId}::{message}::{assetPath}";
		var diag = new MaterialDiagnostic( severity, materialId, message, assetPath );
		if ( _emittedDiagnosticKeys.Add( key ) )
		{
			_diagnostics.Add( diag );
		}
		return diag;
	}
}
