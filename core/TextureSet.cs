#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Texture Sets + UDIM + the OpenPBR material model (godlike-texture-sets-udim-export-presets,
/// GODLIKE WAVE 3 P2, roadmap §5 Layers-Materials).
///
/// A Texture Set is one material ID's whole output: its own resolution, its own UDIM tiles, its
/// own OpenPBR material, and the paint channels it carries. Sets export independently — a game
/// asset with 3 material IDs exports 3 per-engine preset bundles, and a film hero exports face
/// at 8K and body at 4K, per tile. This is the layer the per-engine presets in
/// <see cref="TextureSetExport"/> plan on top of.
///
/// Reuse, not duplication: the painted channels come from <see cref="PaintTextureSet"/> (the
/// per-body paint export) and the material factors from <see cref="MaterialDescriptor"/> /
/// <see cref="MaterialLibrary"/>; nothing here re-implements paint compositing, the unified
/// Layers panel (godlike-unified-layers-panel-sculpt-paint, in-progress) or the GPU bake suite
/// (godlike-gpu-bake-suite-smart-materials). Desktop-only: no kernel file, no Mobile parity
/// surface, no UI, no viewer launch.
/// </summary>
public static class TextureSetRules
{
	/// <summary>Smallest / largest set resolution in texels. Power-of-two only, see
	/// <see cref="IsValidResolution"/>.</summary>
	public const int MinResolution = 64, MaxResolution = 16384;

	public static bool IsPowerOfTwo( int v ) => v > 0 && (v & (v - 1)) == 0;

	public static bool IsValidResolution( int v ) =>
		v >= MinResolution && v <= MaxResolution && IsPowerOfTwo( v );

	public static int RequireResolution( int v, string name )
	{
		if ( !IsValidResolution( v ) )
			throw new ArgumentOutOfRangeException( name,
				$"Resolution must be a power of two between {MinResolution} and {MaxResolution}, got {v}." );
		return v;
	}
}

/// <summary>One UDIM tile of a set, with that tile's own resolution (the hero case: face tile
/// at 8192, body tiles at 4096). The tile number is a UDIM id (1001 + u + 10·v); the numbering
/// itself lives in <see cref="UdimTileSet"/> (kernel, landed by the UDIM sibling) and is reused
/// here, not re-implemented — <see cref="UdimTileSet.TileCoords"/> validates the number.</summary>
public sealed class UdimTileEntry
{
	public int TileNumber { get; }
	public int Resolution { get; }

	public UdimTileEntry( int tileNumber, int resolution )
	{
		_ = UdimTileSet.TileCoords( tileNumber );
		TileNumber = tileNumber;
		Resolution = TextureSetRules.RequireResolution( resolution, nameof( resolution ) );
	}
}

/// <summary>
/// One material ID's texture set: its own resolution, its own UDIM tiles, its OpenPBR material
/// and the paint channels it carries. Colour is always carried — a set with no albedo is not a
/// set, it is a packing error, so a channel list without <see cref="PaintChannel.Color"/> is
/// refused at construction.
///
/// Not <see cref="TexturePaintService.TextureSet"/>: that nested type is the images one canvas
/// uploads as (colour/normal/rough/metal for a single paint). This is the per-material-ID
/// export set (resolution, UDIM tiles, material, channels) the presets plan on.
/// </summary>
public sealed class TextureSet
{
	public string MaterialId { get; }
	public string Name { get; }
	/// <summary>Base resolution; a tile's entry overrides it (see <see cref="ResolutionForTile"/>).</summary>
	public int Resolution { get; }
	public OpenPbrMaterial Material { get; }
	public IReadOnlyList<UdimTileEntry> Tiles { get; }
	public IReadOnlySet<PaintChannel> Channels { get; }

	public TextureSet( string materialId, string? name, int resolution, OpenPbrMaterial material,
		IEnumerable<UdimTileEntry>? tiles = null, IEnumerable<PaintChannel>? channels = null )
	{
		if ( string.IsNullOrWhiteSpace( materialId ) )
			throw new ArgumentException( "A texture set needs a material ID.", nameof( materialId ) );
		MaterialId = materialId;
		Name = string.IsNullOrWhiteSpace( name ) ? materialId : name!;
		Resolution = TextureSetRules.RequireResolution( resolution, nameof( resolution ) );
		Material = material ?? throw new ArgumentNullException( nameof( material ) );

		var tileList = tiles is null ? new List<UdimTileEntry>() : new List<UdimTileEntry>( tiles );
		if ( tileList.Count == 0 )
			tileList.Add( new UdimTileEntry( 1001, Resolution ) );
		var seen = new HashSet<int>();
		foreach ( var entry in tileList )
			if ( !seen.Add( entry.TileNumber ) )
				throw new ArgumentException( $"Texture set has UDIM tile {entry.TileNumber} twice.", nameof( tiles ) );
		Tiles = tileList;

		var channelSet = channels is null
			? new HashSet<PaintChannel>( PaintChannels.Surface )
			: new HashSet<PaintChannel>( channels );
		if ( !channelSet.Contains( PaintChannel.Color ) )
			throw new ArgumentException( "A texture set must carry the Colour channel.", nameof( channels ) );
		Channels = channelSet;
	}

	public bool HasChannel( PaintChannel channel ) => Channels.Contains( channel );

	/// <summary>The tile a UV point belongs to, or an honest refusal when the point is outside
	/// every tile this set owns.</summary>
	public UdimTileEntry TileForUv( float u, float v )
	{
		var tile = UdimTileSet.TileForUv( u, v );
		foreach ( var entry in Tiles )
			if ( entry.TileNumber == tile )
				return entry;
		throw new ArgumentException( $"Texture set '{MaterialId}' has no UDIM tile {tile}." );
	}

	public int ResolutionForTile( int tileNumber )
	{
		foreach ( var entry in Tiles )
			if ( entry.TileNumber == tileNumber )
				return entry.Resolution;
		throw new ArgumentException( $"Texture set '{MaterialId}' has no UDIM tile {tileNumber}." );
	}
}

/// <summary>
/// The OpenPBR material model (Academy Software Foundation OpenPBR Surface) on top of
/// <see cref="MaterialDescriptor"/>: the descriptor carries the base PBR factors and channel
/// bindings, this carries the OpenPBR extensions the descriptor does not have — subsurface,
/// clearcoat, sheen, anisotropy — plus the triplanar projection settings. The descriptor is
/// never mutated: <see cref="ToJson"/> / <see cref="FromJson"/> round-trip the descriptor's
/// core factors plus every extension, so an OpenPBR material survives a save/load cycle
/// unchanged. (The descriptor's Survey ps1_* overlays are intentionally not carried: they are
/// a viewer effect, not part of OpenPBR.)
/// </summary>
public sealed class OpenPbrMaterial
{
	public MaterialDescriptor Descriptor { get; }

	/// <summary>Subsurface colour (linear) and weight 0..1. Skin lives here.</summary>
	public Vec3 SubsurfaceColor { get; init; } = Vec3.Zero;
	public float SubsurfaceWeight { get; init; } = 0f;
	/// <summary>Clearcoat factor 0..1 and its roughness.</summary>
	public float ClearcoatFactor { get; init; } = 0f;
	public float ClearcoatRoughness { get; init; } = 0.25f;
	/// <summary>Sheen (fabric fuzz) colour, weight 0..1 and roughness.</summary>
	public Vec3 SheenColor { get; init; } = Vec3.One;
	public float SheenWeight { get; init; } = 0f;
	public float SheenRoughness { get; init; } = 0.5f;
	/// <summary>Anisotropy -1..1 and its rotation in radians.</summary>
	public float Anisotropy { get; init; } = 0f;
	public float AnisotropyRotation { get; init; } = 0f;

	/// <summary>Triplanar projection: on, the scale in world units per UV tile, and the blend
	/// sharpness (see <see cref="TriplanarBlendWeights"/>).</summary>
	public bool TriplanarEnabled { get; init; } = false;
	public float TriplanarScale { get; init; } = 1f;
	public float TriplanarBlendSharpness { get; init; } = 4f;

	public OpenPbrMaterial( MaterialDescriptor descriptor )
	{
		Descriptor = descriptor ?? throw new ArgumentNullException( nameof( descriptor ) );
	}

	/// <summary>The underlying descriptor, unchanged — the OpenPBR extensions ride beside it,
	/// they do not rewrite it.</summary>
	public MaterialDescriptor ToDescriptor() => Descriptor;

	public static OpenPbrMaterial FromDescriptor( MaterialDescriptor descriptor ) =>
		new( descriptor );

	/// <summary>
	/// Triplanar UVs for a surface point: the dominant normal axis decides the projection
	/// plane (X → the YZ plane, Y → the XZ plane, Z → the XY plane); ties resolve X, then Y,
	/// then Z, so the choice is deterministic. Returns (u, v, axis) with axis 0/1/2 for X/Y/Z.
	/// </summary>
	public static (float U, float V, int Axis) TriplanarUv( Vec3 position, Vec3 normal, float scale = 1f )
	{
		var ax = MathF.Abs( normal.x );
		var ay = MathF.Abs( normal.y );
		var az = MathF.Abs( normal.z );
		if ( ax >= ay && ax >= az ) return (position.y * scale, position.z * scale, 0);
		if ( ay >= az ) return (position.x * scale, position.z * scale, 1);
		return (position.x * scale, position.y * scale, 2);
	}

	/// <summary>
	/// Triplanar blend weights, one per axis: |normal| raised to <paramref name="sharpness"/>,
	/// normalised to sum 1. A sharpness of 1 blends smoothly; 4–8 concentrates on the dominant
	/// plane. A zero normal blends all three planes equally instead of dividing by zero.
	/// </summary>
	public static Vec3 TriplanarBlendWeights( Vec3 normal, float sharpness )
	{
		if ( sharpness <= 0f )
			throw new ArgumentOutOfRangeException( nameof( sharpness ), "Triplanar blend sharpness must be positive." );
		var wx = MathF.Pow( MathF.Abs( normal.x ), sharpness );
		var wy = MathF.Pow( MathF.Abs( normal.y ), sharpness );
		var wz = MathF.Pow( MathF.Abs( normal.z ), sharpness );
		var sum = wx + wy + wz;
		if ( sum <= 1e-12f ) return new Vec3( 1f / 3f, 1f / 3f, 1f / 3f );
		return new Vec3( wx / sum, wy / sum, wz / sum );
	}

	/// <summary>JSON shape for <see cref="ToJson"/> / <see cref="FromJson"/>. Public so the
	/// JSON serializer can construct it; it is not part of the material API.</summary>
	public sealed class Dto
	{
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public float[] BaseColor { get; set; } = { 1f, 1f, 1f, 1f };
		public float Roughness { get; set; } = 0.5f;
		public float Metallic { get; set; } = 0f;
		public float[] Emission { get; set; } = { 0f, 0f, 0f };
		public string AlphaMode { get; set; } = "Opaque";
		public float[] SubsurfaceColor { get; set; } = { 0f, 0f, 0f };
		public float SubsurfaceWeight { get; set; } = 0f;
		public float ClearcoatFactor { get; set; } = 0f;
		public float ClearcoatRoughness { get; set; } = 0.25f;
		public float[] SheenColor { get; set; } = { 1f, 1f, 1f };
		public float SheenWeight { get; set; } = 0f;
		public float SheenRoughness { get; set; } = 0.5f;
		public float Anisotropy { get; set; } = 0f;
		public float AnisotropyRotation { get; set; } = 0f;
		public bool TriplanarEnabled { get; set; } = false;
		public float TriplanarScale { get; set; } = 1f;
		public float TriplanarBlendSharpness { get; set; } = 4f;
	}

	public string ToJson()
	{
		var d = Descriptor;
		return JsonSerializer.Serialize( new Dto
		{
			Id = d.Id, Name = d.Name,
			BaseColor = new[] { d.BaseColorFactor.x, d.BaseColorFactor.y, d.BaseColorFactor.z, d.BaseColorFactor.w },
			Roughness = d.RoughnessFactor, Metallic = d.MetallicFactor,
			Emission = new[] { d.EmissionFactor.x, d.EmissionFactor.y, d.EmissionFactor.z },
			AlphaMode = d.AlphaMode.ToString(),
			SubsurfaceColor = new[] { SubsurfaceColor.x, SubsurfaceColor.y, SubsurfaceColor.z },
			SubsurfaceWeight = SubsurfaceWeight,
			ClearcoatFactor = ClearcoatFactor, ClearcoatRoughness = ClearcoatRoughness,
			SheenColor = new[] { SheenColor.x, SheenColor.y, SheenColor.z },
			SheenWeight = SheenWeight, SheenRoughness = SheenRoughness,
			Anisotropy = Anisotropy, AnisotropyRotation = AnisotropyRotation,
			TriplanarEnabled = TriplanarEnabled, TriplanarScale = TriplanarScale,
			TriplanarBlendSharpness = TriplanarBlendSharpness,
		} );
	}

	public static OpenPbrMaterial FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "An OpenPBR material needs JSON to parse.", nameof( json ) );
		var dto = JsonSerializer.Deserialize<Dto>( json )
			?? throw new ArgumentException( "Could not parse an OpenPBR material from JSON.", nameof( json ) );
		if ( dto.BaseColor.Length != 4 || dto.Emission.Length != 3
			|| dto.SubsurfaceColor.Length != 3 || dto.SheenColor.Length != 3 )
			throw new ArgumentException( "OpenPBR JSON has a colour with the wrong component count.", nameof( json ) );
		var descriptor = new MaterialDescriptor( dto.Id, dto.Name )
		{
			BaseColorFactor = new Vec4( dto.BaseColor[0], dto.BaseColor[1], dto.BaseColor[2], dto.BaseColor[3] ),
			RoughnessFactor = dto.Roughness,
			MetallicFactor = dto.Metallic,
			EmissionFactor = new Vec3( dto.Emission[0], dto.Emission[1], dto.Emission[2] ),
			AlphaMode = Enum.TryParse<AlphaMode>( dto.AlphaMode, out var mode ) ? mode : AlphaMode.Opaque,
		};
		return new OpenPbrMaterial( descriptor )
		{
			SubsurfaceColor = new Vec3( dto.SubsurfaceColor[0], dto.SubsurfaceColor[1], dto.SubsurfaceColor[2] ),
			SubsurfaceWeight = dto.SubsurfaceWeight,
			ClearcoatFactor = dto.ClearcoatFactor, ClearcoatRoughness = dto.ClearcoatRoughness,
			SheenColor = new Vec3( dto.SheenColor[0], dto.SheenColor[1], dto.SheenColor[2] ),
			SheenWeight = dto.SheenWeight, SheenRoughness = dto.SheenRoughness,
			Anisotropy = dto.Anisotropy, AnisotropyRotation = dto.AnisotropyRotation,
			TriplanarEnabled = dto.TriplanarEnabled, TriplanarScale = dto.TriplanarScale,
			TriplanarBlendSharpness = dto.TriplanarBlendSharpness,
		};
	}
}

/// <summary>
/// Scanned-quality starting points for the OpenPBR model, wrapping <see cref="MaterialLibrary"/>
/// presets (never editing them) with the extension values the preset implies. These are library
/// defaults, not measurements: skin gets subsurface, silk gets sheen, glass gets clearcoat, and
/// everything else wraps its preset unchanged. An unknown name is refused, not guessed.
/// </summary>
public static class OpenPbrLibrary
{
	public static OpenPbrMaterial Get( string name )
	{
		MaterialDescriptor descriptor;
		try { descriptor = MaterialLibrary.Get( name ); }
		catch ( KeyNotFoundException )
		{
			throw new ArgumentException( $"Unknown OpenPBR material preset: {name}", nameof( name ) );
		}

		return name.ToLowerInvariant() switch
		{
			"skin" or "skin-light" or "skin-dark" => new OpenPbrMaterial( descriptor )
			{
				SubsurfaceColor = new Vec3( 0.9f, 0.55f, 0.45f ), SubsurfaceWeight = 0.5f,
			},
			"fabric-silk" => new OpenPbrMaterial( descriptor )
			{
				SheenColor = Vec3.One, SheenWeight = 0.6f, SheenRoughness = 0.3f,
			},
			"fabric-cotton" or "fabric-denim" => new OpenPbrMaterial( descriptor )
			{
				SheenColor = Vec3.One, SheenWeight = 0.3f, SheenRoughness = 0.6f,
			},
			"glass-clear" or "glass-tinted" => new OpenPbrMaterial( descriptor )
			{
				ClearcoatFactor = 1f, ClearcoatRoughness = 0.05f,
			},
			"plastic-glossy" => new OpenPbrMaterial( descriptor )
			{
				ClearcoatFactor = 0.4f, ClearcoatRoughness = 0.2f,
			},
			_ => new OpenPbrMaterial( descriptor ),
		};
	}

	public static IEnumerable<string> Names() => MaterialLibrary.Names();
}
