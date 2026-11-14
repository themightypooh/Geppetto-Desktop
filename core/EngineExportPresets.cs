#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Engine export presets + per-engine PBR channel packing.
///
/// Godlike File-Finish-Bake P2 (board task godlike-engine-export-presets-file-safety).
/// The portable GLB baseline (board task add-portable-glb-import-and-static-textured-export,
/// DONE) already embeds base colour / metallic-roughness / normal / occlusion / emissive
/// into the GLB via ExportService. These presets add the per-engine side of that story:
/// which packed maps each engine expects, how source channels pack into them, locked
/// resolution / bit-depth / colour-space, and the {Asset}_{MapType} naming template.
///
/// Honest limits, recorded here rather than papered over:
/// - Packing is 8-bit RGBA in this ticket. The roadmap's 16-bit non-colour data maps need
///   a ushort packer (PngWriter.Write16 exists); that is a follow-on, not claimed here.
/// - USD/USDZ has no writer on Desktop (the USD stage is godlike-usd-stage-alembic-materialx,
///   Wave 1). The USD preset is a definition only and reports unsupported.
/// - STL/3MF has no writer on Desktop. The 3D-print preset is a definition only; the
///   watertight check for it lives in ShipChecklist, ready for a future writer.
/// - Draco compression has no encoder on Desktop. GLB presets export uncompressed and
///   report Draco as unavailable; they do not claim a compressed file.
/// - Normal maps are packed from the source normal map. Unreal/DirectX presets flip the
///   green channel when the source is OpenGL; OpenGL presets pass it through.
/// </summary>
public enum EngineTarget
{
	Unreal5,
	UnityUrp,
	UnityHdrp,
	Godot4,
	Gltf,
	Usd,
	Sketchfab,
	Print3d
}

public enum PresetContainer
{
	None,
	Glb,
	Fbx,
	Obj,
	Usd,
	Stl
}

/// <summary>One channel a source texture set can supply. Scalar maps carry their value in R.</summary>
public enum SourceChannel
{
	Albedo,
	Normal,
	Roughness,
	Metallic,
	AmbientOcclusion,
	Emissive,
	Opacity,
	Height
}

/// <summary>
/// One output channel of a packed map: a source channel byte, optionally inverted
/// (smoothness is inverted roughness), or a constant fill when Constant is set.
/// </summary>
public sealed record PackedChannel( SourceChannel Source, int ByteIndex = 0, bool Invert = false, byte? Constant = null );

/// <summary>
/// One packed output map: the four output channels, its file suffix and colour space.
/// Packed maps are linear data, never sRGB.
/// </summary>
public sealed record PackedMapSpec(
	string Name,
	string Suffix,
	PackedChannel Red,
	PackedChannel Green,
	PackedChannel Blue,
	PackedChannel Alpha,
	ColorSpaceInterpretation ColorSpace,
	string Description );

/// <summary>
/// The source maps for one texture set. All present maps must share Width x Height and
/// be 8-bit RGBA (Width * Height * 4 bytes). Missing maps are null.
/// </summary>
public sealed record TextureMapSet(
	int Width,
	int Height,
	byte[]? Albedo = null,
	byte[]? Normal = null,
	byte[]? Roughness = null,
	byte[]? Metallic = null,
	byte[]? AmbientOcclusion = null,
	byte[]? Emissive = null,
	byte[]? Opacity = null,
	byte[]? HeightMap = null )
{
	public byte[]? Get( SourceChannel channel ) => channel switch
	{
		SourceChannel.Albedo => Albedo,
		SourceChannel.Normal => Normal,
		SourceChannel.Roughness => Roughness,
		SourceChannel.Metallic => Metallic,
		SourceChannel.AmbientOcclusion => AmbientOcclusion,
		SourceChannel.Emissive => Emissive,
		SourceChannel.Opacity => Opacity,
		SourceChannel.Height => HeightMap,
		_ => null
	};
}

public sealed record PackedMap( PackedMapSpec Spec, byte[] PngBytes, int Width, int Height );

public sealed record PackResult(
	bool Success,
	IReadOnlyList<PackedMap> PackedMaps,
	IReadOnlyList<string> Warnings,
	IReadOnlyList<string> Errors );

/// <summary>
/// One engine export preset. Supported == false means Desktop cannot write this target
/// yet; UnsupportedReason names the missing piece. Never present such a preset as
/// exportable: Pack and the batch runner both refuse it without touching disk.
/// </summary>
public sealed record EngineExportPreset(
	string Id,
	string DisplayName,
	EngineTarget Target,
	PresetContainer Container,
	NormalMapConvention NormalConvention,
	int WorkingResolution,
	int ExportResolution,
	int BitDepth,
	string NamingTemplate,
	IReadOnlyList<PackedMapSpec> PackedMaps,
	IReadOnlyList<SourceChannel> RequiredSourceMaps,
	bool Supported,
	string? UnsupportedReason,
	bool DracoSupported,
	string OutputUpAxis,
	string OutputForwardAxis,
	string? UnitsConversion,
	string Notes );

public static class EngineExportPresets
{
	static PackedChannel C( SourceChannel source, int byteIndex = 0, bool invert = false ) =>
		new( source, byteIndex, invert );
	static PackedChannel Fill( byte value ) =>
		new( SourceChannel.Albedo, 0, false, value );

	static PackedMapSpec Orm( NormalMapConvention convention ) => new(
		"ORM", "ORM",
		C( SourceChannel.AmbientOcclusion ),
		C( SourceChannel.Roughness ),
		C( SourceChannel.Metallic ),
		Fill( 255 ),
		ColorSpaceInterpretation.Linear,
		"Occlusion (R) / roughness (G) / metallic (B), alpha opaque. Unreal, Godot and glTF share this layout." );

	static PackedMapSpec Normal( NormalMapConvention convention ) => new(
		"Normal", "Normal",
		C( SourceChannel.Normal, 0 ),
		C( SourceChannel.Normal, 1 ),
		C( SourceChannel.Normal, 2 ),
		Fill( 255 ),
		ColorSpaceInterpretation.Linear,
		convention == NormalMapConvention.DirectX
			? "DirectX normal: green channel is flipped from an OpenGL source at pack time."
			: "OpenGL normal: green channel passes through unchanged." );

	static PackedMapSpec MetallicSmoothness() => new(
		"MetallicSmoothness", "MetallicSmoothness",
		C( SourceChannel.Metallic ),
		Fill( 0 ),
		Fill( 0 ),
		C( SourceChannel.Roughness, 0, invert: true ),
		ColorSpaceInterpretation.Linear,
		"Unity URP metallic-smoothness: metallic in R, smoothness (inverted roughness) in A." );

	static PackedMapSpec MaskMap() => new(
		"MaskMap", "MaskMap",
		C( SourceChannel.Metallic ),
		C( SourceChannel.AmbientOcclusion ),
		Fill( 0 ),
		C( SourceChannel.Roughness, 0, invert: true ),
		ColorSpaceInterpretation.Linear,
		"Unity HDRP mask map: metallic (R) / occlusion (G) / detail mask (B, 0: no detail source) / smoothness (A)." );

	public static readonly IReadOnlyList<EngineExportPreset> All = new[]
	{
		new EngineExportPreset(
			"unreal-5", "Unreal Engine 5 (ORM / DirectX)", EngineTarget.Unreal5, PresetContainer.Glb,
			NormalMapConvention.DirectX, 2048, 4096, 8, "{Asset}_{MapType}",
			new[] { Orm( NormalMapConvention.DirectX ), Normal( NormalMapConvention.DirectX ) },
			new[] { SourceChannel.Albedo, SourceChannel.Normal, SourceChannel.Roughness, SourceChannel.Metallic, SourceChannel.AmbientOcclusion },
			Supported: true, UnsupportedReason: null, DracoSupported: false,
			"Z", "X", "inches->centimetres x2.54",
			"ORM packed for Unreal; Z-up is kept, inches convert to centimetres." ),
		new EngineExportPreset(
			"unity-urp", "Unity URP (metallic-smoothness / OpenGL)", EngineTarget.UnityUrp, PresetContainer.Fbx,
			NormalMapConvention.OpenGL, 2048, 4096, 8, "{Asset}_{MapType}",
			new[] { MetallicSmoothness(), Normal( NormalMapConvention.OpenGL ) },
			new[] { SourceChannel.Albedo, SourceChannel.Normal, SourceChannel.Metallic, SourceChannel.Roughness },
			Supported: true, UnsupportedReason: null, DracoSupported: false,
			"Y", "Z", "inches->metres x0.0254",
			"FBX model (static mesh/rig only, no animation curves) + metallic-smoothness maps." ),
		new EngineExportPreset(
			"unity-hdrp", "Unity HDRP (mask map / OpenGL)", EngineTarget.UnityHdrp, PresetContainer.Fbx,
			NormalMapConvention.OpenGL, 2048, 4096, 8, "{Asset}_{MapType}",
			new[] { MaskMap(), Normal( NormalMapConvention.OpenGL ) },
			new[] { SourceChannel.Albedo, SourceChannel.Normal, SourceChannel.Metallic, SourceChannel.AmbientOcclusion, SourceChannel.Roughness },
			Supported: true, UnsupportedReason: null, DracoSupported: false,
			"Y", "Z", "inches->metres x0.0254",
			"FBX model (static mesh/rig only, no animation curves) + HDRP mask map." ),
		new EngineExportPreset(
			"godot-4", "Godot 4 (ORM / OpenGL)", EngineTarget.Godot4, PresetContainer.Glb,
			NormalMapConvention.OpenGL, 2048, 4096, 8, "{Asset}_{MapType}",
			new[] { Orm( NormalMapConvention.OpenGL ), Normal( NormalMapConvention.OpenGL ) },
			new[] { SourceChannel.Albedo, SourceChannel.Normal, SourceChannel.Roughness, SourceChannel.Metallic, SourceChannel.AmbientOcclusion },
			Supported: true, UnsupportedReason: null, DracoSupported: false,
			"Y", "-Z", "inches->metres x0.0254",
			"ORM packed for Godot; forward is -Z in Godot space." ),
		new EngineExportPreset(
			"gltf-2", "glTF 2.0 / GLB (embedded PBR / OpenGL)", EngineTarget.Gltf, PresetContainer.Glb,
			NormalMapConvention.OpenGL, 2048, 4096, 8, "{Asset}_{MapType}",
			new[] { Orm( NormalMapConvention.OpenGL ), Normal( NormalMapConvention.OpenGL ) },
			new[] { SourceChannel.Albedo, SourceChannel.Normal, SourceChannel.Roughness, SourceChannel.Metallic, SourceChannel.AmbientOcclusion },
			Supported: true, UnsupportedReason: null, DracoSupported: false,
			"Y", "Z", "inches->metres x0.0254",
			"Embedded PBR comes from the portable GLB baseline (ExportService embeds base colour, metallic-roughness, normal, occlusion, emissive). Draco is unavailable: GLB is written uncompressed." ),
		new EngineExportPreset(
			"usd", "USD / USDZ (export presets only)", EngineTarget.Usd, PresetContainer.Usd,
			NormalMapConvention.OpenGL, 2048, 4096, 8, "{Asset}_{MapType}",
			new[] { Orm( NormalMapConvention.OpenGL ), Normal( NormalMapConvention.OpenGL ) },
			new[] { SourceChannel.Albedo, SourceChannel.Normal, SourceChannel.Roughness, SourceChannel.Metallic, SourceChannel.AmbientOcclusion },
			Supported: false, UnsupportedReason: "No USD writer on Desktop; the USD stage is godlike-usd-stage-alembic-materialx (Wave 1). This preset is a definition only.",
			DracoSupported: false,
			"Y", "Z", "inches->metres x0.0254",
			"Definition only. Do not present as exportable." ),
		new EngineExportPreset(
			"sketchfab", "Sketchfab / web (GLB / OpenGL)", EngineTarget.Sketchfab, PresetContainer.Glb,
			NormalMapConvention.OpenGL, 1024, 2048, 8, "{Asset}_{MapType}",
			new[] { Orm( NormalMapConvention.OpenGL ), Normal( NormalMapConvention.OpenGL ) },
			new[] { SourceChannel.Albedo, SourceChannel.Normal, SourceChannel.Roughness, SourceChannel.Metallic, SourceChannel.AmbientOcclusion },
			Supported: true, UnsupportedReason: null, DracoSupported: false,
			"Y", "Z", "inches->metres x0.0254",
			"Sketchfab ingests GLB; web-friendly 2K export cap." ),
		new EngineExportPreset(
			"print-3d", "3D print (STL / 3MF, watertight)", EngineTarget.Print3d, PresetContainer.Stl,
			NormalMapConvention.OpenGL, 0, 0, 8, "{Asset}_{MapType}",
			Array.Empty<PackedMapSpec>(),
			Array.Empty<SourceChannel>(),
			Supported: false, UnsupportedReason: "No STL/3MF writer on Desktop. The watertight check for this preset lives in ShipChecklist, ready for a future writer.",
			DracoSupported: false,
			"Z", "-Y", "inches->millimetres x25.4",
			"Definition only. Do not present as exportable." ),
	};

	public static EngineExportPreset? Get( string id )
	{
		if ( string.IsNullOrWhiteSpace( id ) ) return null;
		return All.FirstOrDefault( p => string.Equals( p.Id, id, StringComparison.OrdinalIgnoreCase ) );
	}

	public static bool IsPowerOfTwo( int value ) => value > 0 && ( value & ( value - 1 ) ) == 0;

	/// <summary>
	/// Packs every packed map of a supported preset from the source maps.
	/// A missing ambient-occlusion source packs as white (no occlusion) with a warning;
	/// any other missing required source fails that map with a readable error.
	/// A missing source normal green flip is applied only when the target convention
	/// differs from the source convention. Nothing is written to disk here.
	/// </summary>
	public static PackResult Pack(
		EngineExportPreset preset,
		TextureMapSet maps,
		NormalMapConvention sourceNormalConvention )
	{
		ArgumentNullException.ThrowIfNull( preset );
		ArgumentNullException.ThrowIfNull( maps );
		var warnings = new List<string>();
		var errors = new List<string>();
		var packed = new List<PackedMap>();

		if ( !preset.Supported )
		{
			errors.Add( preset.UnsupportedReason ?? $"Preset '{preset.Id}' is not supported on Desktop." );
			return new PackResult( false, packed, warnings, errors );
		}
		if ( preset.BitDepth != 8 )
		{
			errors.Add( $"Preset '{preset.Id}' locks {preset.BitDepth}-bit packing, but this ticket packs 8-bit only." );
			return new PackResult( false, packed, warnings, errors );
		}
		if ( maps.Width <= 0 || maps.Height <= 0 )
		{
			errors.Add( "Texture map set has no size." );
			return new PackResult( false, packed, warnings, errors );
		}
		int expectedBytes = maps.Width * maps.Height * 4;
		foreach ( var channel in Enum.GetValues<SourceChannel>() )
		{
			var bytes = maps.Get( channel );
			if ( bytes != null && bytes.Length != expectedBytes )
				errors.Add( $"Map '{channel}' is {bytes.Length} bytes, expected {expectedBytes} RGBA bytes for {maps.Width}x{maps.Height}." );
		}
		if ( errors.Count > 0 )
			return new PackResult( false, packed, warnings, errors );

		foreach ( var required in preset.RequiredSourceMaps )
		{
			if ( maps.Get( required ) == null )
			{
				if ( required == SourceChannel.AmbientOcclusion )
					warnings.Add( "Ambient occlusion map missing; packed occlusion channels default to white (no occlusion)." );
				else
					errors.Add( $"Required source map '{required}' is missing for preset '{preset.Id}'." );
			}
		}

		foreach ( var spec in preset.PackedMaps )
		{
			var (rgba, mapErrors, mapWarnings) = PackOne( spec, maps, preset.NormalConvention, sourceNormalConvention );
			warnings.AddRange( mapWarnings );
			errors.AddRange( mapErrors );
			if ( rgba == null )
				continue;
			packed.Add( new PackedMap( spec, PngWriter.ToBytesRgba( rgba, maps.Width, maps.Height ), maps.Width, maps.Height ) );
		}

		return new PackResult( errors.Count == 0, packed, warnings, errors );
	}

	static (byte[]? Rgba, List<string> Errors, List<string> Warnings) PackOne(
		PackedMapSpec spec,
		TextureMapSet maps,
		NormalMapConvention targetConvention,
		NormalMapConvention sourceConvention )
	{
		var errors = new List<string>();
		var warnings = new List<string>();
		var channels = new[] { spec.Red, spec.Green, spec.Blue, spec.Alpha };
		var resolved = new (byte[]? Bytes, int ByteIndex, bool Invert, byte Constant, bool IsConstant)[4];

		for ( int c = 0; c < 4; c++ )
		{
			var channel = channels[c];
			if ( channel.Constant.HasValue )
			{
				resolved[c] = (null, 0, false, channel.Constant.Value, true);
				continue;
			}
			var bytes = maps.Get( channel.Source );
			if ( bytes == null )
			{
				if ( channel.Source == SourceChannel.AmbientOcclusion )
				{
					warnings.Add( $"Packed map '{spec.Name}': ambient occlusion missing, channel defaults to white." );
					resolved[c] = (null, 0, false, 255, true);
				}
				else
				{
					errors.Add( $"Packed map '{spec.Name}': source map '{channel.Source}' is missing." );
					return (null, errors, warnings);
				}
			}
			else
			{
				bool invert = channel.Invert;
				if ( channel.Source == SourceChannel.Normal && channel.ByteIndex == 1 && targetConvention != sourceConvention )
					invert = !invert;
				resolved[c] = (bytes, channel.ByteIndex, invert, 0, false);
			}
		}

		int pixelCount = maps.Width * maps.Height;
		var rgba = new byte[pixelCount * 4];
		for ( int i = 0; i < pixelCount; i++ )
		{
			for ( int c = 0; c < 4; c++ )
			{
				var r = resolved[c];
				byte value = r.IsConstant ? r.Constant : r.Bytes![i * 4 + r.ByteIndex];
				if ( r.Invert )
					value = (byte)( 255 - value );
				rgba[i * 4 + c] = value;
			}
		}
		return (rgba, errors, warnings);
	}

	/// <summary>File name for one packed map: the preset naming template with {Asset} and {MapType} filled.</summary>
	public static string FileNameFor( EngineExportPreset preset, string assetName, PackedMapSpec spec, string extension = ".png" )
	{
		ArgumentNullException.ThrowIfNull( preset );
		ArgumentNullException.ThrowIfNull( spec );
		return FileNameFor( preset, assetName, spec.Suffix, extension );
	}

	public static string FileNameFor( EngineExportPreset preset, string assetName, string mapSuffix, string extension = ".png" )
	{
		ArgumentNullException.ThrowIfNull( preset );
		string asset = SanitizeFileName( assetName );
		string suffix = SanitizeFileName( mapSuffix );
		string name;
		if ( preset.NamingTemplate.Contains( "{Asset}", StringComparison.Ordinal ) ||
		     preset.NamingTemplate.Contains( "{MapType}", StringComparison.Ordinal ) )
		{
			name = preset.NamingTemplate
				.Replace( "{Asset}", asset, StringComparison.Ordinal )
				.Replace( "{MapType}", suffix, StringComparison.Ordinal );
		}
		else
		{
			name = asset + "_" + suffix;
		}
		if ( string.IsNullOrWhiteSpace( extension ) )
			extension = ".png";
		if ( !extension.StartsWith( ".", StringComparison.Ordinal ) )
			extension = "." + extension;
		return name + extension;
	}

	/// <summary>Model file name for a preset: {Asset} + the container extension.</summary>
	public static string ModelFileNameFor( EngineExportPreset preset, string assetName )
	{
		ArgumentNullException.ThrowIfNull( preset );
		string extension = preset.Container switch
		{
			PresetContainer.Glb => ".glb",
			PresetContainer.Fbx => ".fbx",
			PresetContainer.Obj => ".obj",
			PresetContainer.Usd => ".usd",
			PresetContainer.Stl => ".stl",
			_ => ""
		};
		if ( extension.Length == 0 )
			return SanitizeFileName( assetName );
		return SanitizeFileName( assetName ) + extension;
	}

	public static string SanitizeFileName( string? value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return "Asset";
		var chars = value.Trim().ToCharArray();
		for ( int i = 0; i < chars.Length; i++ )
		{
			char c = chars[i];
			bool ok = ( c >= 'a' && c <= 'z' ) || ( c >= 'A' && c <= 'Z' ) || ( c >= '0' && c <= '9' ) || c == '-' || c == '_' || c == '.';
			if ( !ok )
				chars[i] = '_';
		}
		string cleaned = new string( chars ).Trim( '.' );
		return cleaned.Length == 0 ? "Asset" : cleaned;
	}

	/// <summary>Self-check for a preset definition. Empty means the preset is well formed.</summary>
	public static IReadOnlyList<string> ValidatePreset( EngineExportPreset preset )
	{
		ArgumentNullException.ThrowIfNull( preset );
		var errors = new List<string>();
		if ( string.IsNullOrWhiteSpace( preset.Id ) )
			errors.Add( "Preset id is empty." );
		if ( !preset.NamingTemplate.Contains( "{Asset}", StringComparison.Ordinal ) )
			errors.Add( $"Preset '{preset.Id}': naming template '{preset.NamingTemplate}' has no {{Asset}} placeholder." );
		if ( !preset.NamingTemplate.Contains( "{MapType}", StringComparison.Ordinal ) )
			errors.Add( $"Preset '{preset.Id}': naming template '{preset.NamingTemplate}' has no {{MapType}} placeholder." );
		if ( preset.DracoSupported )
			errors.Add( $"Preset '{preset.Id}': Draco is marked supported, but Desktop has no Draco encoder." );
		if ( preset.Supported )
		{
			if ( preset.Container == PresetContainer.None )
				errors.Add( $"Preset '{preset.Id}': supported but has no container format." );
			if ( preset.PackedMaps.Count == 0 )
				errors.Add( $"Preset '{preset.Id}': supported but defines no packed maps." );
			if ( preset.RequiredSourceMaps.Count == 0 )
				errors.Add( $"Preset '{preset.Id}': supported but requires no source maps." );
			if ( !IsPowerOfTwo( preset.WorkingResolution ) )
				errors.Add( $"Preset '{preset.Id}': working resolution {preset.WorkingResolution} is not a power of two." );
			if ( !IsPowerOfTwo( preset.ExportResolution ) )
				errors.Add( $"Preset '{preset.Id}': export resolution {preset.ExportResolution} is not a power of two." );
			if ( preset.ExportResolution < preset.WorkingResolution )
				errors.Add( $"Preset '{preset.Id}': export resolution is below the working resolution." );
			if ( preset.BitDepth != 8 )
				errors.Add( $"Preset '{preset.Id}': bit depth {preset.BitDepth} is not the 8-bit packing this ticket implements." );
			if ( string.IsNullOrWhiteSpace( preset.UnitsConversion ) )
				errors.Add( $"Preset '{preset.Id}': supported but declares no units conversion from Desktop inches." );
			foreach ( var spec in preset.PackedMaps )
			{
				if ( string.IsNullOrWhiteSpace( spec.Suffix ) )
					errors.Add( $"Preset '{preset.Id}': packed map '{spec.Name}' has no file suffix." );
				if ( spec.ColorSpace != ColorSpaceInterpretation.Linear )
					errors.Add( $"Preset '{preset.Id}': packed map '{spec.Name}' is not linear data." );
				foreach ( var channel in new[] { spec.Red, spec.Green, spec.Blue, spec.Alpha } )
				{
					if ( !channel.Constant.HasValue && ( channel.ByteIndex < 0 || channel.ByteIndex > 3 ) )
						errors.Add( $"Preset '{preset.Id}': packed map '{spec.Name}' has an out-of-range channel byte index." );
					if ( !Enum.IsDefined( channel.Source ) )
						errors.Add( $"Preset '{preset.Id}': packed map '{spec.Name}' names an unknown source channel." );
				}
			}
		}
		else if ( string.IsNullOrWhiteSpace( preset.UnsupportedReason ) )
		{
			errors.Add( $"Preset '{preset.Id}': unsupported but names no reason." );
		}
		return errors;
	}
}
