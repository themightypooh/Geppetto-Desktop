using System;
using System.Collections.Generic;
using System.IO;

namespace Effigy;

public sealed class LineworkMaterialBake
{
	public int Width, Height;
	public byte[] Color, Normal, HeightMap, Roughness, Metalness, Cavity;

	public byte[] NormalRgb => Normal;
	public byte[] Albedo => Color;
	public byte[] Ao => Cavity;

	public byte[] ViewRgba( LineworkMaterialView view )
	{
		if ( view == LineworkMaterialView.Albedo ) return (byte[])Color.Clone();
		var rgba = new byte[Width * Height * 4];
		for ( var i = 0; i < Width * Height; i++ )
		{
			var p = i * 4;
			if ( view == LineworkMaterialView.Normal )
			{
				for ( var c = 0; c < 3; c++ ) rgba[p + c] = Normal[i * 3 + c];
			}
			else if ( view == LineworkMaterialView.LitHeight )
			{
				var n = i * 3;
				var light = Math.Clamp( 0.25f + 0.75f * ((Normal[n] / 127.5f - 1) * -0.4f + (Normal[n + 1] / 127.5f - 1) * -0.5f + (Normal[n + 2] / 127.5f - 1) * 0.768f), 0.08f, 1f );
				for ( var c = 0; c < 3; c++ ) rgba[p + c] = (byte)(Color[p + c] * light * Cavity[i] / 255f);
			}
			else
			{
				var v = view switch
				{
					LineworkMaterialView.Height => HeightMap[i],
					LineworkMaterialView.Roughness => Roughness[i],
					LineworkMaterialView.Metalness => Metalness[i],
					LineworkMaterialView.Cavity => Cavity[i],
					_ => Color[p + 3]
				};
				rgba[p] = rgba[p + 1] = rgba[p + 2] = v;
			}
			rgba[p + 3] = view == LineworkMaterialView.LitHeight ? Color[p + 3] : (byte)255;
		}
		return rgba;
	}

	public void SaveToDirectory( string dir, string baseName = "material", string vmatDirRelative = null )
	{
		Directory.CreateDirectory( dir );
		var colorPath = Path.Combine( dir, $"{baseName}_color.png" );
		var normalPath = Path.Combine( dir, $"{baseName}_normal.png" );
		var roughPath = Path.Combine( dir, $"{baseName}_rough.png" );
		var metalPath = Path.Combine( dir, $"{baseName}_metal.png" );
		var aoPath = Path.Combine( dir, $"{baseName}_ao.png" );
		var vmatPath = Path.Combine( dir, $"{baseName}.vmat" );

		PngWriter.WriteFileRgba( colorPath, Color, Width, Height );
		PngWriter.WriteFile( normalPath, Normal, Width, Height );

		static byte[] ToRgb( byte[] mono, int count )
		{
			var rgb = new byte[count * 3];
			for ( var i = 0; i < count; i++ )
			{
				var v = mono[i];
				rgb[i * 3] = v;
				rgb[i * 3 + 1] = v;
				rgb[i * 3 + 2] = v;
			}
			return rgb;
		}

		var count = Width * Height;
		PngWriter.WriteFile( roughPath, ToRgb( Roughness, count ), Width, Height );
		PngWriter.WriteFile( metalPath, ToRgb( Metalness, count ), Width, Height );
		PngWriter.WriteFile( aoPath, ToRgb( Cavity, count ), Width, Height );

		var vmatContent = LineworkMaterialBaker.GenerateVmatSource( vmatDirRelative ?? dir, baseName );
		File.WriteAllText( vmatPath, vmatContent );
	}
}

/// <summary>One coverage raster per stroke feeds every enabled PBR channel. No colour-luminance height guesses.</summary>
public static class LineworkMaterialBaker
{
	static float Finite( float v, float fallback ) => float.IsFinite( v ) ? v : fallback;
	static byte Unit( float v ) => (byte)MathF.Round( Math.Clamp( Finite( v, 0 ), 0, 1 ) * 255 );
	static void GreyBlend( PaintCanvas canvas, int x, int y, byte value, float alpha, bool erase )
	{
		if ( erase ) canvas.Erase( x, y, alpha ); else canvas.Blend( x, y, value, value, value, alpha );
	}

	public static LineworkMaterialBake Bake( LineworkDocument doc, LineworkMaterialBakeOptions options )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		if ( options is not null )
		{
			doc.Material ??= new LineworkMaterialSettings();
			doc.Material.Width = options.Width;
			doc.Material.Height = options.Height;
			doc.Material.NormalStrength = options.NormalStrength;
			doc.Material.FlipNormalY = options.FlipNormalY;
			doc.Material.WrapTile = options.WrapTile;
			doc.Material.Workflow = options.Workflow;
			doc.Material.BaseColor = options.BaseColor;
			doc.Material.BaseRoughness = options.BaseRoughness;
			doc.Material.BaseMetalness = options.BaseMetalness;
		}
		return Bake( doc, options?.BaseNormalRgb );
	}

	public static LineworkMaterialBake Bake( LineworkDocument doc, byte[] baseNormalRgb = null )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var s = doc.Material ?? throw new ArgumentException( "Missing material settings." );
		var w = s.Width; var h = s.Height;
		if ( w < 1 || h < 1 || w > 4096 || h > 4096 ) throw new ArgumentException( "Material dimensions must be 1..4096." );
		if ( baseNormalRgb is not null && baseNormalRgb.Length != (long)w * h * 3 ) throw new ArgumentException( "Base normal must match the document resolution." );
		var count = w * h;
		var color = new PaintCanvas( w, h );
		var height = new PaintCanvas( w, h ); var rough = new PaintCanvas( w, h ); var metal = new PaintCanvas( w, h );
		var (br, bg, bb) = LineworkRaster.ParseColorOrDefault( s.BaseColor );
		for ( var i = 0; i < count; i++ )
		{
			var p = i * 4;
			color.Rgba[p] = br; color.Rgba[p + 1] = bg; color.Rgba[p + 2] = bb;
			color.Rgba[p + 3] = s.Workflow == LineworkMaterialWorkflow.Decal ? (byte)0 : (byte)255;
			height.Rgba[p] = height.Rgba[p + 1] = height.Rgba[p + 2] = 128;
			rough.Rgba[p] = rough.Rgba[p + 1] = rough.Rgba[p + 2] = Unit( s.BaseRoughness );
			metal.Rgba[p] = metal.Rgba[p + 1] = metal.Rgba[p + 2] = Unit( s.BaseMetalness );
			height.Rgba[p + 3] = rough.Rgba[p + 3] = metal.Rgba[p + 3] = 255;
		}

		// Detect coordinate domain: if negative points exist, assume centered on origin (-w/2, -h/2).
		// If all points are non-negative, assume origin is (0, 0).
		var hasNegativePoints = false;
		foreach ( var layer in doc.Layers )
		{
			foreach ( var stroke in layer.Strokes )
			{
				foreach ( var p in stroke.Points )
				{
					if ( p.x < -0.001f || p.y < -0.001f ) { hasNegativePoints = true; break; }
				}
				if ( hasNegativePoints ) break;
			}
			if ( hasNegativePoints ) break;
		}

		var map = hasNegativePoints
			? new RasterMap { OriginX = -w / 2f, OriginY = -h / 2f }
			: new RasterMap { OriginX = 0f, OriginY = 0f };

		foreach ( var layer in doc.Layers )
		{
			if ( !layer.Visible || layer.Ref ) continue;
			var lc = new PaintCanvas( w, h ); var lh = new PaintCanvas( w, h );
			var lr = new PaintCanvas( w, h ); var lm = new PaintCanvas( w, h );
			foreach ( var stroke in layer.Strokes )
			{
				var coverage = new float[count];
				foreach ( var copy in StrokeCopies( doc, stroke, hasNegativePoints ) )
				{
					var one = new LineworkLayer { Strokes = new() { copy } };
					var alpha = LineworkRaster.ToBrushAlpha( one, w, h, map );
					for ( var i = 0; i < count; i++ ) coverage[i] = Math.Max( coverage[i], alpha.Values[i] );
				}
				var (r, g, b) = LineworkRaster.ParseColorOrDefault( stroke.Color );

				// Tool defaults if not explicitly set
				var defaultToolHeight = 0f;
				var toolLower = stroke.Tool?.ToLowerInvariant() ?? "";
				if ( toolLower == "stitch" || toolLower == "raised" ) defaultToolHeight = 0.65f;
				else if ( toolLower == "groove" || toolLower == "seam" || toolLower == "carve" ) defaultToolHeight = -0.65f;
				else if ( toolLower == "rivet" || toolLower == "stud" ) defaultToolHeight = 0.80f;

				var rawHeight = stroke.Height ?? (layer.Height != 0f ? layer.Height : defaultToolHeight);
				var hv = Unit( (Math.Clamp( Finite( rawHeight, 0 ), -1, 1 ) + 1) / 2 );
				var rv = Unit( stroke.Roughness ?? layer.Roughness );
				var mv = Unit( stroke.Metalness ?? layer.Metalness );
				var erase = string.Equals( stroke.Tool, "eraser", StringComparison.OrdinalIgnoreCase );

				for ( var i = 0; i < count; i++ )
				{
					var a = coverage[i]; if ( a <= 0 ) continue;
					var x = i % w; var y = i / w;
					if ( (stroke.Channels & LineworkMaterialChannels.Color) != 0 )
					{ if ( erase ) lc.Erase( x, y, a ); else lc.Blend( x, y, r, g, b, a ); }
					if ( (stroke.Channels & LineworkMaterialChannels.Height) != 0 ) GreyBlend( lh, x, y, hv, a, erase );
					if ( (stroke.Channels & LineworkMaterialChannels.Roughness) != 0 ) GreyBlend( lr, x, y, rv, a, erase );
					if ( (stroke.Channels & LineworkMaterialChannels.Metalness) != 0 ) GreyBlend( lm, x, y, mv, a, erase );
				}
			}
			Composite( color, lc, layer.Opacity ); Composite( height, lh, layer.Opacity );
			Composite( rough, lr, layer.Opacity ); Composite( metal, lm, layer.Opacity );
		}
		var result = new LineworkMaterialBake
		{
			Width = w,
			Height = h,
			Color = color.Rgba,
			HeightMap = Scalar( height ),
			Roughness = Scalar( rough ),
			Metalness = Scalar( metal )
		};

		var wrap = s.WrapTile && s.Workflow == LineworkMaterialWorkflow.Tileable;
		result.Normal = LineworkNormalBaker.HeightToNormal( result.HeightMap, w, h, Math.Clamp( Finite( s.NormalStrength, 2 ), 0, 32 ), s.FlipNormalY, wrap );
		if ( baseNormalRgb is not null ) result.Normal = LineworkNormalBaker.CompositeRnm( baseNormalRgb, result.Normal, w, h );

		result.Cavity = new byte[count];
		Array.Fill( result.Cavity, (byte)255 );
		ComputeHeightAo( result.HeightMap, result.Cavity, w, h, radius: 8, intensity: 0.8f, wrapTile: wrap );

		return result;
	}

	public static void ComputeHeightAo( byte[] height, byte[] ao, int width, int heightPx, int radius = 8, float intensity = 0.8f, bool wrapTile = false )
	{
		if ( height == null || ao == null || width <= 0 || heightPx <= 0 ) return;
		var r = Math.Clamp( radius, 1, 32 );
		var radSq = r * r;

		for ( var y = 0; y < heightPx; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var h0 = height[y * width + x];
				float occlusionSum = 0f;
				float weightSum = 0f;

				for ( var dy = -r; dy <= r; dy++ )
				{
					for ( var dx = -r; dx <= r; dx++ )
					{
						var d2 = dx * dx + dy * dy;
						if ( d2 == 0 || d2 > radSq ) continue;

						var dist = MathF.Sqrt( d2 );
						var wSample = 1f / dist;
						weightSum += wSample;

						var xx = wrapTile ? (x + dx + width * 4) % width : Math.Clamp( x + dx, 0, width - 1 );
						var yy = wrapTile ? (y + dy + heightPx * 4) % heightPx : Math.Clamp( y + dy, 0, heightPx - 1 );

						var hn = height[yy * width + xx];
						if ( hn > h0 )
						{
							var diff = (hn - h0) / 255f;
							occlusionSum += diff * wSample;
						}
					}
				}

				if ( weightSum > 0.0001f )
				{
					var occ = (occlusionSum / weightSum) * intensity * 2.5f;
					ao[y * width + x] = (byte)Math.Clamp( (int)((1f - occ) * 255f), 0, 255 );
				}
				else
				{
					ao[y * width + x] = 255;
				}
			}
		}
	}

	public static void ApplyCavityOcclusion( byte[] albedoRgba, byte[] ao, int width, int heightPx, float weight = 0.4f )
	{
		if ( albedoRgba == null || ao == null || width <= 0 || heightPx <= 0 ) return;
		var count = width * heightPx;
		var w = Math.Clamp( weight, 0f, 1f );

		for ( var i = 0; i < count; i++ )
		{
			var aoVal = ao[i];
			if ( aoVal == 255 ) continue;

			var factor = 1f - (1f - aoVal / 255f) * w;
			var p = i * 4;
			albedoRgba[p] = (byte)Math.Clamp( (int)(albedoRgba[p] * factor), 0, 255 );
			albedoRgba[p + 1] = (byte)Math.Clamp( (int)(albedoRgba[p + 1] * factor), 0, 255 );
			albedoRgba[p + 2] = (byte)Math.Clamp( (int)(albedoRgba[p + 2] * factor), 0, 255 );
		}
	}

	public static string GenerateVmatSource( string materialDir, string materialName )
	{
		var slash = string.IsNullOrEmpty( materialDir ) ? "" : materialDir.TrimEnd( '/' ) + "/";
		return $@"// THIS FILE IS AUTO-GENERATED

Layer0
{{
	shader ""shaders/complex.shader_c""

	//---- Color ----
	F_SPECULAR 1
	TextureColor ""{slash}{materialName}_color.png""

	//---- Normal ----
	TextureNormal ""{slash}{materialName}_normal.png""

	//---- Roughness ----
	TextureRoughness ""{slash}{materialName}_rough.png""

	//---- Metalness ----
	F_METALNESS_TEXTURE 1
	TextureMetalness ""{slash}{materialName}_metal.png""

	//---- Ambient Occlusion ----
	TextureAmbientOcclusion ""{slash}{materialName}_ao.png""
}}
";
	}

	static byte[] Scalar( PaintCanvas c )
	{
		var b = new byte[c.Width * c.Height];
		for ( var i = 0; i < b.Length; i++ ) b[i] = c.Rgba[i * 4];
		return b;
	}

	static void Composite( PaintCanvas target, PaintCanvas source, float opacity )
	{
		opacity = Math.Clamp( Finite( opacity, 1 ), 0, 1 );
		for ( var i = 0; i < target.Width * target.Height; i++ )
		{
			var p = i * 4;
			target.Blend( i % target.Width, i / target.Width, source.Rgba[p], source.Rgba[p + 1], source.Rgba[p + 2], source.Rgba[p + 3] / 255f * opacity );
		}
	}

	public static LineworkStroke TransformStroke( LineworkStroke source, Func<Vec2, Vec2> transform )
	{
		var copy = new LineworkStroke
		{
			Id = source.Id,
			Tool = source.Tool,
			Color = source.Color,
			Size = source.Size,
			Opacity = source.Opacity,
			Height = source.Height,
			Roughness = source.Roughness,
			Metalness = source.Metalness,
			Channels = source.Channels,
			Closed = source.Closed,
			Pressures = new( source.Pressures )
		};
		foreach ( var p in source.Points ) copy.Points.Add( transform( p ) );
		foreach ( var ring in source.Holes )
		{
			var hole = new List<Vec2>();
			foreach ( var p in ring ) hole.Add( transform( p ) );
			copy.Holes.Add( hole );
		}
		return copy;
	}

	public static IEnumerable<LineworkStroke> StrokeCopies( LineworkDocument doc, LineworkStroke stroke, bool centeredOrigin = false )
	{
		var s = doc.Material;
		var originals = new List<LineworkStroke> { stroke };
		if ( doc.SymOn ) originals.Add( TransformStroke( stroke, p => new Vec2( 2 * doc.SymX - p.x, p.y ) ) );
		var segments = Math.Clamp( s.RadialSegments, 1, 32 );

		foreach ( var original in originals )
		{
			for ( var k = 0; k < segments; k++ )
			{
				Vec2 Radial( Vec2 p )
				{
					if ( segments == 1 ) return p;
					var folded = LineworkSymmetryTiling.FoldToWedge( p, new Vec2(), segments, s.Kaleidoscope );
					var a = MathF.Atan2( folded.y, folded.x );
					var wedge = MathF.PI * 2 / segments;
					a = k * wedge + (s.Kaleidoscope && (k & 1) != 0 ? wedge - a : a);
					return new Vec2( MathF.Cos( a ), MathF.Sin( a ) ) * folded.Length;
				}

				var radial = TransformStroke( original, Radial );
				if ( !s.WrapTile || s.Workflow != LineworkMaterialWorkflow.Tileable || radial.Points.Count == 0 )
				{
					yield return radial;
					continue;
				}

				var minX = float.MaxValue; var maxX = float.MinValue;
				var minY = float.MaxValue; var maxY = float.MinValue;
				foreach ( var p in radial.Points )
				{
					minX = Math.Min( minX, p.x ); maxX = Math.Max( maxX, p.x );
					minY = Math.Min( minY, p.y ); maxY = Math.Max( maxY, p.y );
				}
				var radius = Math.Max( 1, radial.Size ) / 2f + 2f;

				var minDomainX = centeredOrigin ? -s.Width / 2f : 0f;
				var maxDomainX = centeredOrigin ? s.Width / 2f : s.Width;
				var minDomainY = centeredOrigin ? -s.Height / 2f : 0f;
				var maxDomainY = centeredOrigin ? s.Height / 2f : s.Height;

				var x0 = (int)MathF.Ceiling( (minDomainX - maxX - radius) / s.Width );
				var x1 = (int)MathF.Floor( (maxDomainX - minX + radius) / s.Width );
				var y0 = (int)MathF.Ceiling( (minDomainY - maxY - radius) / s.Height );
				var y1 = (int)MathF.Floor( (maxDomainY - minY + radius) / s.Height );

				x0 = Math.Clamp( x0, -1, 0 );
				x1 = Math.Clamp( x1, 0, 1 );
				y0 = Math.Clamp( y0, -1, 0 );
				y1 = Math.Clamp( y1, 0, 1 );

				for ( var y = y0; y <= y1; y++ )
				{
					for ( var x = x0; x <= x1; x++ )
					{
						yield return TransformStroke( radial, p => p + new Vec2( x * s.Width, y * s.Height ) );
					}
				}
			}
		}
	}
}
