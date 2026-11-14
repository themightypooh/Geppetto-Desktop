using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Effigy;

/// <summary>
/// Smart Materials: Substance-style generator materials driven by baked maps.
///
/// A Smart Material is an ordered stack of generators. Each generator reads ONE
/// baked map (AO, curvature, cavity, height, thickness, or position-Y), runs it
/// through a mask curve, and blends its tint / roughness / metalness / height
/// into the texel. Because the masks are read from the CURRENT baked maps on
/// every render, a Smart Material adapts when the bake changes — re-bake the
/// curvature and the edge wear moves with it. Nothing here bakes anything: the
/// maps come from <see cref="NormalBake"/> via <see cref="Baker"/> / BakeJob.
/// The PBR layer stack ticket owns the paint-side layer stack; this file is the
/// generator engine + the user library + the per-ID workflow, and nothing else.
///
/// Map encodings (from BakeSet's own docs) that the mask decoders rely on:
///   AO: white is open to the sky, so occlusion = 1 - R/255.
///   Curvature: mid grey is flat, brighter is convex — the curve reads R/255.
///   Cavity: white on flats and ridges, darker below the neighbours.
///   Height: mid grey is on the cage, white is HeightRange out.
///   Position: black is PositionMin, white is PositionMax (G channel is Y).
/// </summary>
public static class SmartMaterials
{
	/// <summary>Piecewise-linear remap, 0..1 in, 0..1 out. The mask's band selector.</summary>
	public sealed class MaskCurve
	{
		public (float Input, float Output)[] Points;

		public MaskCurve( params (float Input, float Output)[] points )
		{
			if ( points is null || points.Length < 2 )
				throw new ArgumentException( "A mask curve needs at least two points." );
			for ( var i = 0; i < points.Length; i++ )
			{
				if ( points[i].Input < 0f || points[i].Input > 1f || points[i].Output < 0f || points[i].Output > 1f )
					throw new ArgumentException( "Mask curve points must be in 0..1." );
				if ( i > 0 && points[i].Input <= points[i - 1].Input )
					throw new ArgumentException( "Mask curve inputs must strictly increase." );
			}
			Points = points.ToArray();
		}

		public float Evaluate( float input )
		{
			input = Math.Clamp( input, 0f, 1f );
			if ( input <= Points[0].Input ) return Points[0].Output;
			for ( var i = 1; i < Points.Length; i++ )
			{
				if ( input > Points[i].Input ) continue;
				var (x0, y0) = Points[i - 1];
				var (x1, y1) = Points[i];
				return y0 + (input - x0) / (x1 - x0) * (y1 - y0);
			}
			return Points[^1].Output;
		}
	}

	public enum SmartSource { AmbientOcclusion, Curvature, Cavity, Height, Thickness, PositionY }

	/// <summary>One generator: a baked-map source, a mask curve, and what it blends in.</summary>
	public sealed class SmartGenerator
	{
		public SmartSource Source;
		public MaskCurve Curve;
		public Vec3 Tint = new( 0.5f, 0.5f, 0.5f );
		public float TintStrength = 1f;
		public float Roughness = 0.8f;
		public float RoughnessStrength = 1f;
		public float Metalness;
		public float MetalnessStrength = 1f;
		/// <summary>Height added at full mask, model units. Positive raises the surface.</summary>
		public float HeightAdjust;
	}

	/// <summary>One texel's material, after the generator stack.</summary>
	public sealed class SmartSample
	{
		public Vec3 Albedo;
		public float Roughness;
		public float Metalness;
		public float Height;
	}

	/// <summary>A Smart Material: a base plus an ordered generator stack.</summary>
	public sealed class SmartMaterial
	{
		public string Name;
		public string[] Tags = Array.Empty<string>();
		public Vec3 BaseAlbedo = new( 0.5f, 0.5f, 0.5f );
		public float BaseRoughness = 0.8f;
		public float BaseMetalness;
		public List<SmartGenerator> Generators = new();

		public SmartSample Evaluate( BakeSet set, int x, int y )
		{
			if ( set is null ) throw new ArgumentNullException( nameof( set ) );
			var s = new SmartSample { Albedo = BaseAlbedo, Roughness = BaseRoughness, Metalness = BaseMetalness, Height = 0f };
			foreach ( var g in Generators )
			{
				var mask = Mask( set, g, x, y );
				if ( mask <= 0f ) continue;
				s.Albedo = Lerp( s.Albedo, g.Tint, mask * Math.Clamp( g.TintStrength, 0f, 1f ) );
				s.Roughness = Lerp( s.Roughness, g.Roughness, mask * Math.Clamp( g.RoughnessStrength, 0f, 1f ) );
				s.Metalness = Lerp( s.Metalness, g.Metalness, mask * Math.Clamp( g.MetalnessStrength, 0f, 1f ) );
				s.Height += g.HeightAdjust * mask;
			}
			return s;
		}

		static float Lerp( float a, float b, float t ) => a + (b - a) * Math.Clamp( t, 0f, 1f );
		static Vec3 Lerp( Vec3 a, Vec3 b, float t )
		{
			t = Math.Clamp( t, 0f, 1f );
			return new Vec3( a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t );
		}

		static float Mask( BakeSet set, SmartGenerator g, int x, int y )
		{
			var v = SampleSource( set, g.Source, x, y );
			if ( v is null ) return 0f;
			return g.Curve.Evaluate( v.Value );
		}

		/// <summary>The source map's 0..1 value at a texel, or null when the map was not baked.</summary>
		static float? SampleSource( BakeSet set, SmartSource source, int x, int y )
		{
			switch ( source )
			{
				case SmartSource.AmbientOcclusion:
					if ( set.AmbientOcclusion is null ) return null;
					return 1f - set.AmbientOcclusion.At( x, y ).R / 255f;
				case SmartSource.Curvature:
					if ( set.Curvature is null ) return null;
					return set.Curvature.At( x, y ).R / 255f;
				case SmartSource.Cavity:
					if ( set.Cavity is null ) return null;
					return 1f - set.Cavity.At( x, y ).R / 255f;
				case SmartSource.Height:
					if ( set.Height is null ) return null;
					return set.Height.At( x, y ).R / 255f;
				case SmartSource.Thickness:
					if ( set.Thickness is null ) return null;
					return set.Thickness.At( x, y ).R / 255f;
				case SmartSource.PositionY:
				{
					if ( set.Position is null ) return null;
					var range = set.PositionMax.y - set.PositionMin.y;
					if ( range <= 1e-9f ) return 0.5f;
					// G encodes Y linearly between PositionMin and PositionMax.
					return set.Position.At( x, y ).G / 255f;
				}
				default:
					return null;
			}
		}
	}

	/// <summary>What rendering a Smart Material produces: one map per channel.</summary>
	public sealed class SmartMaterialOutput
	{
		public BakedMap Albedo;
		public BakedMap Roughness;
		public BakedMap Metalness;
		public BakedMap Height;
		public float HeightRange;
	}

	/// <summary>Render one material over a whole baked set. Missing source maps skip their generators.</summary>
	public static SmartMaterialOutput Render( SmartMaterial material, BakeSet set )
	{
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		if ( set is null ) throw new ArgumentNullException( nameof( set ) );
		var (w, h, filled) = SizeOf( set );
		return RenderInto( material, set, w, h, filled, null );
	}

	/// <summary>
	/// Material-ID workflow: bake per ID, Smart Material per ID. <paramref name="byId"/>
	/// maps a material id (the <see cref="BakeSet.IdColors"/> key) to a material name;
	/// texels whose colour is not in IdColors, or whose id has no assignment, get
	/// <paramref name="fallback"/>. The ID map is the only map read for the choice —
	/// the generators still read their own baked maps for the masks.
	/// </summary>
	public static SmartMaterialOutput ApplyPerId( BakeSet set, Dictionary<int, string> byId, SmartMaterialLibrary library, SmartMaterial fallback )
	{
		if ( set is null ) throw new ArgumentNullException( nameof( set ) );
		if ( library is null ) throw new ArgumentNullException( nameof( library ) );
		if ( fallback is null ) throw new ArgumentNullException( nameof( fallback ) );
		var (w, h, filled) = SizeOf( set );
		var byColour = new Dictionary<(byte, byte, byte), int>();
		if ( set.IdColors is not null )
			foreach ( var (id, c) in set.IdColors )
				byColour[(c.R, c.G, c.B)] = id;
		Func<int, int, SmartMaterial> pick = ( x, y ) =>
		{
			if ( set.MaterialId is not null && byId is not null )
			{
				var (r, g, b) = set.MaterialId.At( x, y );
				if ( byColour.TryGetValue( (r, g, b), out var id ) && byId.TryGetValue( id, out var name ) )
				{
					var found = library.Get( name );
					if ( found is not null ) return found;
				}
			}
			return fallback;
		};
		return RenderInto( null, set, w, h, filled, pick );
	}

	static SmartMaterialOutput RenderInto( SmartMaterial material, BakeSet set, int w, int h, bool[] filled, Func<int, int, SmartMaterial> pick )
	{
		var albedo = new BakedMap( w, h );
		var rough = new BakedMap( w, h );
		var metal = new BakedMap( w, h );
		var height = new BakedMap( w, h );
		var sixteen = set.Normal?.Rgb16 is not null || set.Height?.Rgb16 is not null;
		if ( sixteen )
		{
			albedo.EnableSixteenBit();
			rough.EnableSixteenBit();
			metal.EnableSixteenBit();
			height.EnableSixteenBit();
		}
		var heightRange = 0f;
		for ( var y = 0; y < h; y++ )
		for ( var x = 0; x < w; x++ )
		{
			var i = y * w + x;
			var m = pick is not null ? pick( x, y ) : material;
			var s = m.Evaluate( set, x, y );
			var ai = i * 3;
			albedo.Rgb[ai] = ToByte( s.Albedo.x );
			albedo.Rgb[ai + 1] = ToByte( s.Albedo.y );
			albedo.Rgb[ai + 2] = ToByte( s.Albedo.z );
			rough.Rgb[ai] = rough.Rgb[ai + 1] = rough.Rgb[ai + 2] = ToByte( s.Roughness );
			metal.Rgb[ai] = metal.Rgb[ai + 1] = metal.Rgb[ai + 2] = ToByte( s.Metalness );
			var hg = Math.Clamp( 0.5f + s.Height, 0f, 1f );
			height.Rgb[ai] = height.Rgb[ai + 1] = height.Rgb[ai + 2] = ToByte( hg );
			if ( sixteen )
			{
				albedo.Rgb16![ai] = ToUShort( s.Albedo.x );
				albedo.Rgb16[ai + 1] = ToUShort( s.Albedo.y );
				albedo.Rgb16[ai + 2] = ToUShort( s.Albedo.z );
				rough.Rgb16![ai] = rough.Rgb16[ai + 1] = rough.Rgb16[ai + 2] = ToUShort( s.Roughness );
				metal.Rgb16![ai] = metal.Rgb16[ai + 1] = metal.Rgb16[ai + 2] = ToUShort( s.Metalness );
				height.Rgb16![ai] = height.Rgb16[ai + 1] = height.Rgb16[ai + 2] = ToUShort( hg );
			}
			heightRange = Math.Max( heightRange, Math.Abs( s.Height ) );
		}
		Array.Copy( filled, albedo.Filled, filled.Length );
		Array.Copy( filled, rough.Filled, filled.Length );
		Array.Copy( filled, metal.Filled, filled.Length );
		Array.Copy( filled, height.Filled, filled.Length );
		return new SmartMaterialOutput { Albedo = albedo, Roughness = rough, Metalness = metal, Height = height, HeightRange = heightRange };
	}

	static (int Width, int Height, bool[] Filled) SizeOf( BakeSet set )
	{
		foreach ( var map in new[] { set.Normal, set.AmbientOcclusion, set.Curvature, set.Height, set.Position, set.MaterialId } )
			if ( map is not null )
				return (map.Width, map.Height, map.Filled);
		throw new ArgumentException( "Smart material needs at least one baked map to render against." );
	}

	static byte ToByte( float v ) => (byte)Math.Clamp( (int)MathF.Round( Math.Clamp( v, 0f, 1f ) * 255f ), 0, 255 );
	static ushort ToUShort( float v ) => (ushort)Math.Clamp( (int)MathF.Round( Math.Clamp( v, 0f, 1f ) * 65535f ), 0, 65535 );

	// ------------------------------------------------------------------ library

	/// <summary>Built-in Smart Materials, so the library is useful before the user adds one.</summary>
	public static SmartMaterial EdgeWearMetal() => new()
	{
		Name = "EdgeWearMetal",
		Tags = new[] { "metal", "hard-surface", "wear" },
		BaseAlbedo = new Vec3( 0.35f, 0.36f, 0.38f ),
		BaseRoughness = 0.6f,
		BaseMetalness = 0.9f,
		Generators =
		{
			new SmartGenerator
			{
				Source = SmartSource.Curvature,
				Curve = new MaskCurve( (0f, 0f), (0.6f, 0f), (0.9f, 1f), (1f, 1f) ),
				Tint = new Vec3( 0.82f, 0.84f, 0.88f ),
				Roughness = 0.3f,
				Metalness = 1f,
			},
			new SmartGenerator
			{
				Source = SmartSource.AmbientOcclusion,
				Curve = new MaskCurve( (0f, 1f), (0.4f, 0.6f), (1f, 0f) ),
				Tint = new Vec3( 0.1f, 0.08f, 0.06f ),
				TintStrength = 0.8f,
				Roughness = 1f,
				Metalness = 0f,
			},
		},
	};

	public static SmartMaterial TopDust() => new()
	{
		Name = "TopDust",
		Tags = new[] { "dust", "weathering" },
		BaseAlbedo = new Vec3( 0.4f, 0.38f, 0.35f ),
		BaseRoughness = 0.9f,
		Generators =
		{
			new SmartGenerator
			{
				Source = SmartSource.PositionY,
				Curve = new MaskCurve( (0f, 0f), (0.7f, 0f), (1f, 1f) ),
				Tint = new Vec3( 0.85f, 0.82f, 0.72f ),
				Roughness = 1f,
			},
		},
	};

	public static SmartMaterial HeightTint() => new()
	{
		Name = "HeightTint",
		Tags = new[] { "detail", "height" },
		BaseAlbedo = new Vec3( 0.5f, 0.5f, 0.5f ),
		BaseRoughness = 0.8f,
		Generators =
		{
			new SmartGenerator
			{
				Source = SmartSource.Height,
				Curve = new MaskCurve( (0f, 0f), (0.5f, 0.3f), (1f, 1f) ),
				Tint = new Vec3( 0.9f, 0.6f, 0.3f ),
				TintStrength = 0.7f,
			},
		},
	};

	/// <summary>A named, searchable Smart Material library with JSON import/export.</summary>
	public sealed class SmartMaterialLibrary
	{
		public List<SmartMaterial> Entries { get; } = new();

		public SmartMaterialLibrary( bool withBuiltins = true )
		{
			if ( withBuiltins )
			{
				Entries.Add( EdgeWearMetal() );
				Entries.Add( TopDust() );
				Entries.Add( HeightTint() );
			}
		}

		public SmartMaterial Get( string name )
		{
			if ( string.IsNullOrWhiteSpace( name ) ) return null;
			return Entries.FirstOrDefault( e => string.Equals( e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase ) );
		}

		public List<SmartMaterial> Search( string query )
		{
			if ( string.IsNullOrWhiteSpace( query ) ) return Entries.ToList();
			var q = query.Trim().ToLowerInvariant();
			return Entries
				.Where( e => e.Name.ToLowerInvariant().Contains( q, StringComparison.Ordinal )
					|| e.Tags.Any( t => t.ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ) )
				.ToList();
		}

		public void Add( SmartMaterial material )
		{
			if ( material is null ) throw new ArgumentNullException( nameof( material ) );
			if ( string.IsNullOrWhiteSpace( material.Name ) ) throw new ArgumentException( "A Smart Material needs a name." );
			if ( Get( material.Name ) is not null ) throw new ArgumentException( $"Smart Material '{material.Name}' already exists in this library." );
			Entries.Add( material );
		}

		public bool Remove( string name ) => Entries.Remove( Get( name ) );

		public string ExportJson()
		{
			var dtos = Entries.Select( ToDto ).ToList();
			return JsonSerializer.Serialize( dtos, new JsonSerializerOptions { WriteIndented = true } );
		}

		/// <summary>Import a library. Layers whose source name is unknown are skipped, one warning each.</summary>
		public static (SmartMaterialLibrary Library, List<string> Warnings) ImportJson( string json )
		{
			if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "Nothing to import." );
			var dtos = JsonSerializer.Deserialize<List<MaterialDto>>( json )
				?? throw new ArgumentException( "Smart Material library JSON did not contain a list." );
			var library = new SmartMaterialLibrary( withBuiltins: false );
			var warnings = new List<string>();
			foreach ( var dto in dtos )
			{
				var material = FromDto( dto, warnings );
				if ( material is not null ) library.Entries.Add( material );
			}
			return (library, warnings);
		}

		static MaterialDto ToDto( SmartMaterial m ) => new()
		{
			Name = m.Name,
			Tags = m.Tags,
			BaseAlbedo = new[] { m.BaseAlbedo.x, m.BaseAlbedo.y, m.BaseAlbedo.z },
			BaseRoughness = m.BaseRoughness,
			BaseMetalness = m.BaseMetalness,
			Generators = m.Generators.Select( g => new GeneratorDto
			{
				Source = g.Source.ToString(),
				Curve = g.Curve.Points.Select( p => new[] { p.Input, p.Output } ).ToArray(),
				Tint = new[] { g.Tint.x, g.Tint.y, g.Tint.z },
				TintStrength = g.TintStrength,
				Roughness = g.Roughness,
				RoughnessStrength = g.RoughnessStrength,
				Metalness = g.Metalness,
				MetalnessStrength = g.MetalnessStrength,
				HeightAdjust = g.HeightAdjust,
			} ).ToList(),
		};

		static SmartMaterial FromDto( MaterialDto dto, List<string> warnings )
		{
			if ( dto is null || string.IsNullOrWhiteSpace( dto.Name ) )
			{
				warnings.Add( "Skipped a Smart Material with no name." );
				return null;
			}
			var m = new SmartMaterial
			{
				Name = dto.Name,
				Tags = dto.Tags ?? Array.Empty<string>(),
				BaseAlbedo = dto.BaseAlbedo?.Length == 3 ? new Vec3( dto.BaseAlbedo[0], dto.BaseAlbedo[1], dto.BaseAlbedo[2] ) : new Vec3( 0.5f, 0.5f, 0.5f ),
				BaseRoughness = dto.BaseRoughness,
				BaseMetalness = dto.BaseMetalness,
			};
			foreach ( var gdto in dto.Generators ?? new List<GeneratorDto>() )
			{
				if ( !Enum.TryParse<SmartSource>( gdto.Source, out var source ) )
				{
					warnings.Add( $"Skipped a generator with unknown source '{gdto.Source}' in '{dto.Name}'." );
					continue;
				}
				var points = (gdto.Curve ?? Array.Empty<float[]>())
					.Where( p => p?.Length == 2 )
					.Select( p => (p[0], p[1]) )
					.ToArray();
				if ( points.Length < 2 )
				{
					warnings.Add( $"Skipped a generator with too few curve points in '{dto.Name}'." );
					continue;
				}
				m.Generators.Add( new SmartGenerator
				{
					Source = source,
					Curve = new MaskCurve( points ),
					Tint = gdto.Tint?.Length == 3 ? new Vec3( gdto.Tint[0], gdto.Tint[1], gdto.Tint[2] ) : new Vec3( 0.5f, 0.5f, 0.5f ),
					TintStrength = gdto.TintStrength,
					Roughness = gdto.Roughness,
					RoughnessStrength = gdto.RoughnessStrength,
					Metalness = gdto.Metalness,
					MetalnessStrength = gdto.MetalnessStrength,
					HeightAdjust = gdto.HeightAdjust,
				} );
			}
			return m;
		}

		sealed class MaterialDto
		{
			public string Name { get; set; }
			public string[] Tags { get; set; }
			public float[] BaseAlbedo { get; set; }
			public float BaseRoughness { get; set; }
			public float BaseMetalness { get; set; }
			public List<GeneratorDto> Generators { get; set; }
		}

		sealed class GeneratorDto
		{
			public string Source { get; set; }
			public float[][] Curve { get; set; }
			public float[] Tint { get; set; }
			public float TintStrength { get; set; }
			public float Roughness { get; set; }
			public float RoughnessStrength { get; set; }
			public float Metalness { get; set; }
			public float MetalnessStrength { get; set; }
			public float HeightAdjust { get; set; }
		}
	}
}
