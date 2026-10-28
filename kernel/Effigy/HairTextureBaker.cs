using System;
using System.Collections.Generic;
using System.IO;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// Procedural hair card texture sheet baker (roadmap Lane I1.2).
///
/// A card variant is a horizontal band of the sheet; inside a band, near-vertical
/// micro-fibers run root (band top) to tip (band bottom), with per-fiber waviness,
/// thickness and brightness variation, so a flat card reads as many hairs rather
/// than one ribbon. One deterministic pass draws all five maps together, so the
/// maps agree pixel for pixel.
///
/// Maps (all Size x Size, prefix + suffix):
/// - _color.png: root colour -&gt; tip colour per fiber, with per-fiber brightness variation.
/// - _alpha.png: fiber coverage as luminance (255 = fiber, 0 = gap). Stored as
///   luminance rather than a PNG alpha channel on purpose: the kernel's Raster PNG
///   writer is RGB, and a coverage-luminance map is what a card shader samples as
///   alpha anyway. Flyaway tips fade out toward the tip.
/// - _normal.png: tangent flow encoded in RG (fiber slope), B high — a card shader
///   reads flow direction from this instead of a geometric normal.
/// - _rough.png: base roughness with per-fiber variation, rougher toward the tip.
/// - _id_ao.png: R = fiber id hash, G = ambient occlusion (dark at root, bright at
///   tip), B = root-to-tip. Gaps read (0, 255, 0): no fiber, no occlusion.
///
/// Default variants follow the roadmap's card atlas: ClumpThick, ClumpMedium,
/// SparseLayer, Flyaway, RootTransition.
/// </summary>
public static class HairTextureBaker
{
	public sealed class Variant
	{
		public string Name;
		public float Density = 1f;
		public float Thickness = 1f;
		public float TipFade = 0f;
	}

	public sealed class Options
	{
		public int Size = 1024;
		public int Seed = 42;
		public Vec3 RootColor = new( 0.16f, 0.10f, 0.06f );
		public Vec3 TipColor = new( 0.45f, 0.30f, 0.18f );
		public float RoughBase = 0.55f;
		public List<Variant> Variants = new()
		{
			new Variant { Name = "ClumpThick", Density = 1.4f, Thickness = 1.6f, TipFade = 0.1f },
			new Variant { Name = "ClumpMedium", Density = 1.0f, Thickness = 1.0f, TipFade = 0.2f },
			new Variant { Name = "SparseLayer", Density = 0.5f, Thickness = 0.8f, TipFade = 0.4f },
			new Variant { Name = "Flyaway", Density = 0.25f, Thickness = 0.6f, TipFade = 0.8f },
			new Variant { Name = "RootTransition", Density = 1.2f, Thickness = 1.2f, TipFade = 0f },
		};
	}

	/// <summary>Bake the sheet into <paramref name="folder"/> as prefix_color.png etc. Returns every file written.</summary>
	public static List<string> Bake( string folder, string prefix, Options o )
	{
		var size = Math.Clamp( o.Size, 64, 4096 );
		Directory.CreateDirectory( folder );

		var color = new Raster( size, size, Rgb( o.RootColor ) );
		var alpha = new Raster( size, size, 0x000000 );
		var normal = new Raster( size, size, Rgb( new Vec3( 0.5f, 0.5f, 1f ) ) );
		var rough = new Raster( size, size, Rgb( new Vec3( o.RoughBase, o.RoughBase, o.RoughBase ) ) );
		var idAo = new Raster( size, size, Rgb( new Vec3( 0f, 1f, 0f ) ) );

		var rng = new Random( o.Seed );
		var variants = o.Variants.Count == 0 ? new List<Variant> { new() { Name = "Hair" } } : o.Variants;
		var bandH = size / variants.Count;

		for ( var v = 0; v < variants.Count; v++ )
		{
			var variant = variants[v];
			var y0 = v * bandH;
			var y1 = v == variants.Count - 1 ? size : (v + 1) * bandH;
			var height = Math.Max( 1, y1 - y0 );
			var fibers = Math.Max( 8, (int)(variant.Density * size * 0.5f) );

			for ( var f = 0; f < fibers; f++ )
			{
				var x0 = (float)rng.NextDouble() * size;
				var sway = ((float)rng.NextDouble() * 2f - 1f) * 6f;
				var phase = (float)rng.NextDouble() * MathF.PI * 2f;
				var freq = 0.5f + (float)rng.NextDouble() * 1.5f;
				var bright = 0.75f + (float)rng.NextDouble() * 0.5f;
				var roughVar = ((float)rng.NextDouble() * 2f - 1f) * 0.15f;
				var idHash = (f * 2654435761u % 256) / 255f;
				var thick = Math.Max( 1, (int)MathF.Round( variant.Thickness * 2f ) );

				for ( var y = y0; y < y1; y++ )
				{
					var t = height == 1 ? 0f : (float)(y - y0) / (height - 1);
					var x = x0 + sway * MathF.Sin( 2f * MathF.PI * freq * t + phase );
					var slope = sway * 2f * MathF.PI * freq * MathF.Cos( 2f * MathF.PI * freq * t + phase ) / height;
					var cover = 1f - variant.TipFade * t * t;
					var col = Vec3.Lerp( o.RootColor, o.TipColor, t ) * bright;
					var roughV = Math.Clamp( o.RoughBase + roughVar + 0.2f * t, 0f, 1f );

					for ( var dx = -thick; dx <= thick; dx++ )
					{
						var px = (int)MathF.Round( x ) + dx;
						var edge = 1f - MathF.Abs( dx ) / (thick + 1f);
						var a = cover * edge;

						if ( a <= 0.01f )
							continue;

						color.Plot( px, y, Rgb( col ), a );
						alpha.Plot( px, y, 0xFFFFFF, a );
						normal.Plot( px, y, Rgb( new Vec3(
							Math.Clamp( 0.5f + slope * 0.25f, 0f, 1f ), 0.5f, 0.9f ) ), a );
						rough.Plot( px, y, Rgb( new Vec3( roughV, roughV, roughV ) ), a );
						idAo.Plot( px, y, Rgb( new Vec3( idHash, 0.35f + 0.65f * t, t ) ), a );
					}
				}
			}
		}

		var files = new List<string>();
		var names = new[] { ("_color", color), ("_alpha", alpha), ("_normal", normal), ("_rough", rough), ("_id_ao", idAo) };

		foreach ( var (suffix, raster) in names )
		{
			var path = Path.Combine( folder, prefix + suffix + ".png" );
			raster.SavePng( path );
			files.Add( path );
		}

		return files;
	}

	static int Rgb( Vec3 v ) => Raster.Rgb(
		(int)(Math.Clamp( v.x, 0f, 1f ) * 255f),
		(int)(Math.Clamp( v.y, 0f, 1f ) * 255f),
		(int)(Math.Clamp( v.z, 0f, 1f ) * 255f) );
}
