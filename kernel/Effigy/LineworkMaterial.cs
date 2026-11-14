using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Effigy;

[Flags]
public enum LineworkMaterialChannels
{
	None = 0,
	Color = 1,
	Height = 2,
	Roughness = 4,
	Metalness = 8,
	All = 15
}

public enum LineworkMaterialWorkflow
{
	Tileable,
	ModelUv,
	Decal
}

public enum LineworkMaterialView
{
	Albedo,
	Normal,
	LitHeight,
	Height,
	Roughness,
	Metalness,
	Cavity,
	Alpha
}

/// <summary>
/// Physical PBR channel parameters for Linework strokes and layers.
/// Grounded in empirical materials calibration (roughness, metalness, and saturation-preserving cavity).
/// </summary>
public readonly struct LineworkPbrValues
{
	public readonly float Height;
	public readonly float Roughness;
	public readonly float Metalness;
	public readonly string ColorHex;

	public LineworkPbrValues( float height, float roughness, float metalness, string colorHex = "#808080" )
	{
		Height = height;
		Roughness = roughness;
		Metalness = metalness;
		ColorHex = colorHex;
	}
}

/// <summary>
/// Named PBR material preset calibrated to real physical values.
/// </summary>
public sealed class LineworkPbrPreset
{
	public string Name { get; }
	public string Category { get; }
	public LineworkPbrValues Values { get; }

	public LineworkPbrPreset( string name, string category, LineworkPbrValues values )
	{
		Name = name;
		Category = category;
		Values = values;
	}
}

/// <summary>
/// Production calibrated PBR material presets directly grounded in materials.md empirical data.
/// - Wet flesh roughness: 0.05..0.15
/// - Human skin roughness: 0.64..0.80
/// - Bone/enamel roughness: 0.07..0.16
/// - Metals: metalness 1.0 (Chrome, Brass, Steel)
/// - Crevices: darken and saturate, never grey
/// </summary>
public static class LineworkMaterialPresets
{
	public static readonly IReadOnlyList<LineworkPbrPreset> All = new List<LineworkPbrPreset>
	{
		new( "Wet Flesh (Glossy)", "Gore", new LineworkPbrValues( 0.25f, 0.10f, 0.0f, "#8a1c22" ) ),
		new( "Fresh Arterial Blood", "Gore", new LineworkPbrValues( 0.15f, 0.05f, 0.0f, "#930006" ) ),
		new( "Bone / Enamel", "Anatomy", new LineworkPbrValues( 0.40f, 0.12f, 0.0f, "#e8e0cc" ) ),
		new( "Citizen Skin (Fair)", "Organic", new LineworkPbrValues( 0.00f, 0.68f, 0.0f, "#d4a389" ) ),
		new( "Citizen Skin (Deep)", "Organic", new LineworkPbrValues( 0.00f, 0.72f, 0.0f, "#54382c" ) ),
		new( "Chrome / Mirror Metal", "Hard Surface", new LineworkPbrValues( 0.00f, 0.04f, 1.0f, "#e0e0e0" ) ),
		new( "Polished Brass", "Hard Surface", new LineworkPbrValues( 0.10f, 0.18f, 1.0f, "#c9a34d" ) ),
		new( "Brushed Steel", "Hard Surface", new LineworkPbrValues( 0.05f, 0.35f, 1.0f, "#a0a5aa" ) ),
		new( "Rusted Iron", "Hard Surface", new LineworkPbrValues( 0.20f, 0.88f, 0.15f, "#5c3222" ) ),
		new( "Worn Workwear Denim", "Fabric", new LineworkPbrValues( 0.05f, 0.90f, 0.0f, "#2d3e52" ) ),
		new( "Rough Leather", "Fabric", new LineworkPbrValues( -0.10f, 0.85f, 0.0f, "#4a2e1b" ) ),
		new( "Raised Stitching", "Trim", new LineworkPbrValues( 0.65f, 0.75f, 0.0f, "#dcd1b4" ) ),
		new( "Recessed Seam / Groove", "Trim", new LineworkPbrValues( -0.65f, 0.60f, 0.0f, "#2b2b2e" ) ),
		new( "Steel Rivet / Stud", "Trim", new LineworkPbrValues( 0.80f, 0.20f, 1.0f, "#cccccc" ) ),
	};
}

/// <summary>
/// Legacy preset structure for UI dial / inspector integration.
/// </summary>
public sealed class LineworkMaterialPreset
{
	public string Name { get; }
	public string Color { get; }
	public float Height { get; }
	public float Roughness { get; }
	public float Metalness { get; }

	public LineworkMaterialPreset( string name, string color, float height, float roughness, float metalness )
	{
		Name = name;
		Color = color;
		Height = height;
		Roughness = roughness;
		Metalness = metalness;
	}

	public static readonly LineworkMaterialPreset[] Presets =
	{
		new( "Polished Brass", "#c9a34d", 0.1f, 0.18f, 1f ),
		new( "Rough Leather", "#583a29", -0.1f, 0.85f, 0f ),
		new( "Raised Stitch", "#dfc99b", 0.65f, 0.8f, 0f ),
		new( "Wet Flesh", "#a5444e", 0.25f, 0.15f, 0f ),
		new( "Engraved Groove", "#45454b", -0.65f, 0.6f, 0f )
	};
}

/// <summary>The material domain is fixed, centred on the drawing origin, never auto-fitted on export.</summary>
public sealed class LineworkMaterialSettings
{
	public static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	public int Width = 512;
	public int Height = 512;
	public LineworkMaterialWorkflow Workflow = LineworkMaterialWorkflow.Tileable;
	public bool WrapTile = true;
	public int RadialSegments = 1;
	public bool Kaleidoscope;
	public float NormalStrength = 2f;
	public bool FlipNormalY = true;
	public int SeamPadding = 8;
	public string BaseColor = "#808080";
	public float BaseRoughness = 0.5f;
	public float BaseMetalness;
	public string ModelPath = "";
	public string BaseNormalPath = "";
}

/// <summary>
/// Configuration options for baking Linework documents to multi-channel PBR textures.
/// </summary>
public sealed class LineworkMaterialBakeOptions
{
	public int Width = 512;
	public int Height = 512;
	public float NormalStrength = 2.0f;
	public bool FlipNormalY = true;
	public bool WrapTile = true;
	public int SeamBleedPixels = 8;
	public float AoRadius = 8f;
	public float AoIntensity = 0.8f;
	public string BaseColor = "#808080";
	public float BaseRoughness = 0.5f;
	public float BaseMetalness = 0.0f;
	public LineworkMaterialWorkflow Workflow = LineworkMaterialWorkflow.Tileable;
	public byte[] BaseNormalRgb = null;
}
