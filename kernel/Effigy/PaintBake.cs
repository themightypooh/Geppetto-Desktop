using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>What one integrated bake produced for the PBR layer stack.</summary>
public sealed class PbrBakeResult
{
	/// <summary>The bake as a stack layer, named "Bake": its maps carry the baked
	/// tangent-space normal (and nothing else — AO lives on the composite, not
	/// on a layer channel, and curvature/ID are generator inputs, not paint).</summary>
	public PaintLayer Layer;

	/// <summary>Baked ambient occlusion as a paint canvas: white is open to the sky.
	/// Goes on the composite's <see cref="PaintCanvas.AmbientOcclusionMap"/>; alpha
	/// is 255 where a ray landed, 0 in padding.</summary>
	public PaintCanvas AmbientOcclusion;

	/// <summary>Baked curvature, mid grey is flat, brighter convex, darker concave.
	/// Kept raw for Smart Materials and generators (a later task); not a paint channel.</summary>
	public BakedMap Curvature;

	/// <summary>Baked material ID map: one flat colour per id (material slot or high
	/// body). The colours are in <see cref="IdColors"/>.</summary>
	public BakedMap MaterialId;

	/// <summary>Which colour each id was given in <see cref="MaterialId"/>.</summary>
	public Dictionary<int, (byte R, byte G, byte B)> IdColors;

	/// <summary>Rays that found no sculpt, per bake group: (group name, samples, hits).</summary>
	public List<(string Name, int Samples, int Hits)> Misses = new();

	/// <summary>Share of the normal map a ray actually landed on (0..1).</summary>
	public float FilledFraction;
}

/// <summary>
/// The integrated baker for the PBR layer stack: one call from bake groups to a
/// stack layer plus the maps the stack itself cannot hold.
///
/// NOT a new baker. The rays, the cage and the pairing already exist —
/// <see cref="NormalBake"/> bakes the maps, <see cref="BakeCage"/> inflates the
/// cage (the paintable cage is the baker-2 task's), <see cref="BakePairs"/>
/// matches the groups. This is the wiring between them and the stack: AO,
/// curvature, normal and ID from the high-poly, in the sizes and conventions
/// the stack already uses, with the ray-miss report attached.
/// </summary>
public static class PaintBake
{
	/// <summary>The maps a stack bake asks for: normal, ambient occlusion,
	/// curvature and material ID. Cavity, height, thickness, bent normal,
	/// object normal and position stay on <see cref="NormalBake.BakeAll"/>
	/// for callers that need them.</summary>
	public static readonly BakeMaps StackMaps =
		BakeMaps.Normal | BakeMaps.AmbientOcclusion | BakeMaps.Curvature | BakeMaps.MaterialId;

	/// <summary>Bake one group into the stack at a square size.</summary>
	public static PbrBakeResult Bake( BakeGroup group, int size, BakeOptions options = null )
	{
		if ( group is null )
			throw new ArgumentNullException( nameof( group ) );
		return Bake( new[] { group }, size, size, options );
	}

	/// <summary>Bake several groups (one texture, lows must not overlap in UV)
	/// into the stack at <paramref name="width"/> x <paramref name="height"/>.</summary>
	public static PbrBakeResult Bake( IReadOnlyList<BakeGroup> groups, int width, int height, BakeOptions options = null )
	{
		if ( groups is null || groups.Count == 0 )
			throw new ArgumentException( "A bake needs at least one low and high pair.", nameof( groups ) );

		if ( width < 1 || height < 1 )
			throw new ArgumentOutOfRangeException( nameof( width ), "A map needs at least one texel." );

		var set = NormalBake.BakeGroups( groups, width, height, StackMaps, options );
		return Build( set, width, height );
	}

	/// <summary>A baked map as a paint canvas: RGB copied, alpha 255 where a ray
	/// landed, 0 in padding — so compositing skips the padding exactly as it
	/// skips unpainted texels. Row 0 is v = 0 on both sides (the paint canvas
	/// is rasterised with row = v·height), so no flip.</summary>
	public static PaintCanvas FromBaked( BakedMap map )
	{
		if ( map is null ) return null;

		var canvas = new PaintCanvas( map.Width, map.Height );
		for ( var i = 0; i < map.Width * map.Height; i++ )
		{
			var s = i * 3;
			var d = i * 4;
			canvas.Rgba[d] = map.Rgb[s];
			canvas.Rgba[d + 1] = map.Rgb[s + 1];
			canvas.Rgba[d + 2] = map.Rgb[s + 2];
			canvas.Rgba[d + 3] = map.Filled[i] ? (byte)255 : (byte)0;
		}
		return canvas;
	}

	static PbrBakeResult Build( BakeSet set, int width, int height )
	{
		var normal = FromBaked( set.Normal );
		var maps = new PaintLayerMaps { Size = width, Normal = normal };
		return new PbrBakeResult
		{
			Layer = new PaintLayer( "Bake", maps ),
			AmbientOcclusion = FromBaked( set.AmbientOcclusion ),
			Curvature = set.Curvature,
			MaterialId = set.MaterialId,
			IdColors = set.IdColors,
			Misses = set.GroupMiss ?? new List<(string, int, int)>(),
			FilledFraction = set.Normal is null ? 0f : (float)set.Normal.FilledCount / (width * height),
		};
	}
}
