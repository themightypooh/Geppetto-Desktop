// PbrBakeExport — the integration for godlike-pbr-layer-stack-baker-unreal-orm
// (GODLIKE WAVE 2 P1): a baked + painted asset out through the Unreal ORM preset.
//
// The three pieces this ticket names ALREADY EXIST, landed by siblings — this file
// duplicates NONE of them:
//   * PBR layer stack ... kernel/Effigy/UnifiedLayerStack.cs (groups, per-channel
//     blend/opacity/masks, live reorder, variants, CompositePaint)
//   * Integrated baker . kernel/Effigy/Baker.cs (BakeJob/BakeSet, all 10 map types)
//     + kernel/Effigy/BakeCagePaint.cs (paintable cage) on top of NormalBake
//   * Unreal ORM preset  kernel/Effigy/PaintExportPresets.cs (UnrealOrmPreset:
//     PackOrm, FlipGreen/ToDirectX, Build at 4K working / 8K export, VramWarning,
//     {Asset}_{MapType} naming)
//   * (Related, different input type, NOT used here: core/EngineExportPresets.cs packs
//     from a TextureMapSet for 8 engines; this pipeline packs from the composited
//     PaintCanvas, which is what the layer stack produces.)
//
// What was missing — and what this file is — is the pipeline that runs them in order:
// bake (high -> low maps) -> seed a paint canvas -> composite the PBR layer stack ->
// export through the Unreal preset. Nobody called UnrealOrmPreset.Build before this file.
//
// Two conversions the pipeline has to get right, because getting either wrong is
// invisible in a thumbnail and exactly wrong in the engine:
//   * ROW ORDER. BakedMap.Rgb is row 0 at v = 0, and v = 0 is the TOP of the image
//     (NormalBake.cs header: writing row 0 first puts +v DOWN the image);
//     PaintCanvas.Rgba is row 0 at the TOP too. So seeding copies rows STRAIGHT —
//     row 0 to row 0 — no flip. (CORRECTION: the first version of this file flipped
//     rows, reading v = 0 as the bottom; that inverts the map. The sibling PBR
//     worker's PaintBake.FromBaked and a re-read of the BakedMap header both say
//     straight copy. Fixed before handoff, test updated to match.)
//   * GREEN. NormalBake writes OpenGL green by DEFAULT (+green up, towards v = 0);
//     only BakeOptions.FlipGreen writes DirectX. The Unreal preset passes the canvas
//     normal through as DirectX, so a default bake seeded as-is would export inverted
//     green. SeedBaseCanvas therefore takes the baked normal's convention and flips to
//     DirectX when the source is OpenGL (the default).
//
// Non-destructive throughout: the seeded base, the stack and the bake result are never
// mutated — the composite runs on a clone, and the export is a fresh UnrealExport.

using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>The result of one baked + painted asset exported through the Unreal preset.</summary>
public sealed class PbrBakeExportResult
{
	/// <summary>The preset's images (BaseColor / Normal / ORM / Emissive, named
	/// {Asset}_{MapType}) plus the VRAM warning, if any.</summary>
	public UnrealExport Export;

	/// <summary>The export size the images were built at (4096 working, 8192 export).</summary>
	public int Size;

	/// <summary>Baked maps that were present in the bake set (ao / normal / height / id /
	/// curvature / cavity / thickness / bentnormal / objectnormal / position). Only ao,
	/// normal and height feed the canvas; id and curvature are carried, not composited —
	/// see <see cref="BakedIdMap"/> and <see cref="CurvatureMaskCanvas"/>.</summary>
	public List<string> BakedMapsUsed = new();

	/// <summary>Enabled paint entries with surface content, counted under the base values.
	/// Variant overrides and instances may change what the composite actually applies —
	/// the composite itself is authoritative; this is the count, not the proof.</summary>
	public int PaintLayersApplied;

	/// <summary>The bake's ray-miss rate. A bake with misses still exports; the rate rides
	/// here so the caller can warn instead of shipping holes silently.</summary>
	public double BakeMissRate;

	/// <summary>The preset's VRAM warning (null when the export fits the reported VRAM,
	/// or when no VRAM figure was given). 8K is never silently downscaled.</summary>
	public string Warning => Export is null ? null : Export.Warning;

	/// <summary>The baked ID map as a paint canvas (row-flipped, colours per id), or null
	/// when the bake had no ID map. For per-material-ID splitting, feed this to
	/// <see cref="TextureSetExporter"/> with <see cref="IdColors"/>.</summary>
	public PaintCanvas BakedIdMap;

	/// <summary>Which colour each id was given in the ID map, so a painter can pick a mask
	/// by number. Null unless the bake had an ID map.</summary>
	public Dictionary<int, (byte R, byte G, byte B)> IdColors;

	/// <summary>The baked curvature map as a paint canvas (grey, row-flipped: mid grey is
	/// flat, brighter convex, darker concave), for masking wear layers. Null when the bake
	/// has no curvature map.</summary>
	public PaintCanvas CurvatureMaskCanvas;
}

/// <summary>
/// Bake -> PBR layer stack -> Unreal ORM export, in one call. See the file header for
/// what this file is (the integration) and what it deliberately does not re-implement.
/// </summary>
public static class PbrBakeExport
{
	/// <summary>4K working size, from the preset.</summary>
	public const int WorkingSize = UnrealOrmPreset.WorkingSize;

	/// <summary>8K export size, from the preset.</summary>
	public const int ExportSize = UnrealOrmPreset.ExportSize;

	/// <summary>
	/// BakedMap (3 bytes/texel, row 0 at v = 0) -> PaintCanvas (4 bytes/texel, row 0 at
	/// top). Rows copy straight: v = 0 is the TOP of the image on both sides (writing
	/// row 0 first puts +v down the image), so row 0 lands on row 0 — no flip. Alpha
	/// is 255 everywhere. The input is untouched.
	/// </summary>
	public static PaintCanvas ToCanvas( BakedMap map )
	{
		if ( map is null )
			throw new ArgumentNullException( nameof( map ) );

		var canvas = new PaintCanvas( map.Width, map.Height );
		for ( var row = 0; row < map.Height; row++ )
		{
			var srcRow = row;
			for ( var col = 0; col < map.Width; col++ )
			{
				var s = (srcRow * map.Width + col) * 3;
				var d = (row * map.Width + col) * 4;
				canvas.Rgba[d] = map.Rgb[s];
				canvas.Rgba[d + 1] = map.Rgb[s + 1];
				canvas.Rgba[d + 2] = map.Rgb[s + 2];
				canvas.Rgba[d + 3] = 255;
			}
		}

		return canvas;
	}

	static PaintCanvas Flat( int width, int height, byte grey )
	{
		var canvas = new PaintCanvas( width, height );
		for ( var i = 0; i < width * height; i++ )
		{
			canvas.Rgba[i * 4] = grey;
			canvas.Rgba[i * 4 + 1] = grey;
			canvas.Rgba[i * 4 + 2] = grey;
			canvas.Rgba[i * 4 + 3] = 255;
		}

		return canvas;
	}

	static byte BaseGrey( PaintChannel channel ) => (byte)Math.Round( PaintChannels.Base( channel ) * 255f );

	static void FlipGreenInPlace( byte[] rgba )
	{
		for ( var i = 1; i < rgba.Length; i += 4 )
			rgba[i] = (byte)(255 - rgba[i]);
	}

	/// <summary>
	/// Seed the base paint canvas a bake produces: colour at the kernel's base grey
	/// (opaque, so an unpainted export is a valid opaque asset), roughness / metalness /
	/// height at their channel bases, then the baked maps on top of those (AO ->
	/// AmbientOcclusionMap, normal -> NormalMap, height -> HeightMap). The baked normal is
	/// converted to DirectX green unless <paramref name="bakedNormalConvention"/> says it
	/// already is (NormalBake's default is OpenGL; only BakeOptions.FlipGreen writes
	/// DirectX). Curvature and ID are NOT canvas channels: curvature is a mask (see
	/// <see cref="CurvatureMask"/>), the ID map is carried on the export result. Every
	/// baked map present must share one size, or the seed is refused — never silently
	/// resampled.
	/// </summary>
	public static PaintCanvas SeedBaseCanvas( BakeSet set, NormalMapConvention bakedNormalConvention = NormalMapConvention.OpenGL )
	{
		if ( set is null )
			throw new ArgumentNullException( nameof( set ) );

		var maps = new List<(string Name, BakedMap Map)>();
		if ( set.Normal is not null ) maps.Add( ("normal", set.Normal) );
		if ( set.AmbientOcclusion is not null ) maps.Add( ("ao", set.AmbientOcclusion) );
		if ( set.Height is not null ) maps.Add( ("height", set.Height) );
		if ( set.MaterialId is not null ) maps.Add( ("id", set.MaterialId) );
		if ( set.Curvature is not null ) maps.Add( ("curvature", set.Curvature) );
		if ( maps.Count == 0 )
			throw new ArgumentException( "Nothing to seed: the bake set has no normal, AO, height, ID or curvature map.", nameof( set ) );

		var (width, height) = (maps[0].Map.Width, maps[0].Map.Height);
		foreach ( var (name, map) in maps )
		{
			if ( map.Width != width || map.Height != height )
				throw new ArgumentException( $"Baked map '{name}' is {map.Width}x{map.Height} but the set is {width}x{height}; bake all maps at one size.", nameof( set ) );
		}

		var baseCanvas = Flat( width, height, BaseGrey( PaintChannel.Color ) );
		baseCanvas.RoughnessMap = Flat( width, height, BaseGrey( PaintChannel.Roughness ) );
		baseCanvas.MetalnessMap = Flat( width, height, BaseGrey( PaintChannel.Metalness ) );
		baseCanvas.HeightMap = set.Height is not null ? ToCanvas( set.Height ) : Flat( width, height, BaseGrey( PaintChannel.Height ) );
		baseCanvas.NormalMap = set.Normal is not null ? ToCanvas( set.Normal ) : null;
		if ( baseCanvas.NormalMap is not null && bakedNormalConvention == NormalMapConvention.OpenGL )
			FlipGreenInPlace( baseCanvas.NormalMap.Rgba );
		baseCanvas.AmbientOcclusionMap = set.AmbientOcclusion is not null ? ToCanvas( set.AmbientOcclusion ) : null;
		return baseCanvas;
	}

	/// <summary>
	/// The baked curvature map as a paint canvas (grey, row-flipped), for masking wear
	/// layers: mid grey is flat, brighter convex, darker concave. Null when the bake has
	/// no curvature map.
	/// </summary>
	public static PaintCanvas CurvatureMask( BakeSet set )
	{
		if ( set is null )
			throw new ArgumentNullException( nameof( set ) );

		return set.Curvature is null ? null : ToCanvas( set.Curvature );
	}

	static List<string> UsedMaps( BakeSet set )
	{
		var used = new List<string>();
		if ( set.AmbientOcclusion is not null ) used.Add( "ao" );
		if ( set.Normal is not null ) used.Add( "normal" );
		if ( set.Height is not null ) used.Add( "height" );
		if ( set.MaterialId is not null ) used.Add( "id" );
		if ( set.Curvature is not null ) used.Add( "curvature" );
		if ( set.Cavity is not null ) used.Add( "cavity" );
		if ( set.Thickness is not null ) used.Add( "thickness" );
		if ( set.BentNormal is not null ) used.Add( "bentnormal" );
		if ( set.ObjectNormal is not null ) used.Add( "objectnormal" );
		if ( set.Position is not null ) used.Add( "position" );
		return used;
	}

	/// <summary>
	/// Full pipeline: bake result -> seeded base -> layer-stack composite -> Unreal preset.
	/// A failed or cancelled bake is refused (no export of a bake that did not finish);
	/// a bake with ray misses still exports, and the miss rate rides on the result so the
	/// caller can warn instead of shipping holes silently.
	/// </summary>
	public static PbrBakeExportResult Export( BakeJobResult bake, UnifiedLayerStack stack, string asset,
		NormalMapConvention bakedNormalConvention = NormalMapConvention.OpenGL,
		int size = WorkingSize, long? availableVramMb = null )
	{
		if ( bake is null )
			throw new ArgumentNullException( nameof( bake ) );

		if ( !bake.Succeeded )
			throw new InvalidOperationException( $"Cannot export a bake that did not succeed: {bake.Error ?? (bake.Cancelled ? "cancelled" : "no bake set")}" );

		var result = ExportFromCanvas( SeedBaseCanvas( bake.Set, bakedNormalConvention ), stack, asset, size, availableVramMb, bake.MissRate );
		result.BakedMapsUsed = UsedMaps( bake.Set );
		result.BakedIdMap = bake.Set.MaterialId is null ? null : ToCanvas( bake.Set.MaterialId );
		result.IdColors = bake.Set.IdColors;
		result.CurvatureMaskCanvas = CurvatureMask( bake.Set );
		return result;
	}

	/// <summary>
	/// The same pipeline from an already-seeded base canvas (bake-less: paint-only assets).
	/// A null stack means no paint layers — the base exports as-is. The canvas and the
	/// stack are never mutated: the composite runs on a clone, so exporting twice, or
	/// exporting and then painting more, sees the same inputs.
	/// </summary>
	public static PbrBakeExportResult ExportFromCanvas( PaintCanvas baseCanvas, UnifiedLayerStack stack, string asset,
		int size = WorkingSize, long? availableVramMb = null, double bakeMissRate = 0 )
	{
		if ( baseCanvas is null )
			throw new ArgumentNullException( nameof( baseCanvas ) );

		if ( string.IsNullOrWhiteSpace( asset ) )
			throw new ArgumentException( "An asset name is needed for the {Asset}_{MapType} template.", nameof( asset ) );

		if ( size < 1 || size > ExportSize )
			throw new ArgumentOutOfRangeException( nameof( size ), "The Unreal preset tops out at 8K." );

		var painted = baseCanvas.Clone();
		var layersApplied = 0;
		if ( stack is not null )
		{
			foreach ( var entry in stack.Entries )
			{
				if ( entry.Kind == UnifiedLayerKind.Paint && entry.Enabled && entry.Paint?.Maps?.HasSurface == true )
					layersApplied++;
			}

			painted = stack.CompositePaint( painted );
		}

		var export = UnrealOrmPreset.Build( painted, asset, size, availableVramMb );
		return new PbrBakeExportResult
		{
			Export = export,
			Size = size,
			PaintLayersApplied = layersApplied,
			BakeMissRate = bakeMissRate,
		};
	}
}
