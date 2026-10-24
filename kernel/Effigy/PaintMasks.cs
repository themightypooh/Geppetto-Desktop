using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Where a paint layer's mask comes from, before any mask painting on top of it.</summary>
public enum PaintMaskGenerator
{
	/// <summary>No generator: the layer shows everywhere (unless mask paint says otherwise).</summary>
	None,

	/// <summary>Convex edges and ridges, from the curvature bake — where a surface rubs and wears.</summary>
	EdgeWear,

	/// <summary>Crevices and shut-in places, from the ambient-occlusion and cavity bakes — where grime settles.</summary>
	Dirt,

	/// <summary>Bottom to top of the body: 0 at its lowest point, 1 at its highest.</summary>
	Gradient,

	/// <summary>The faces on one material slot: 1 on them, 0 everywhere else.</summary>
	MaterialId,
}

/// <summary>
/// Procedural masks for fill layers, per texel on the body's own UV atlas.
///
/// THE CURVATURE AND OCCLUSION ARE BAKED, NOT INTERPOLATED FROM VERTICES. A low box has eight
/// vertices and a per-vertex curvature would be one number smeared across each face; the bake
/// (<see cref="NormalBake.BakeAll"/>, the body baked onto itself) samples every texel, so a box's
/// edges read as edges. The same bake the Bake tool already ships, not a second estimate of it.
///
/// Gradient and Material ID need no rays: the first is the height of the surface point a texel
/// stands for, the second whether the face that owns the texel wears the slot.
///
/// Every generator returns raw values 0..1 at the given size; <see cref="PaintLayers.Shape"/> turns
/// them into a mask with the layer's level and softness. A texel no face owns is 0.
/// </summary>
public static class PaintMasks
{
	/// <summary>
	/// The largest size a ray-traced generator bakes at; above it the bake is upsampled. A mask is
	/// a soft thing and 512 texels already resolve a bevel, while ambient occlusion at 4096² is
	/// minutes of rays for nothing a viewer could see.
	/// </summary>
	public const int MaxBakeSize = 512;

	/// <summary>
	/// The raw values for <paramref name="generator"/> at <paramref name="size"/>², or null for
	/// <see cref="PaintMaskGenerator.None"/>. <paramref name="slot"/> is the material slot Material ID
	/// selects.
	/// </summary>
	public static float[] Generate( PolyMesh mesh, int size, PaintMaskGenerator generator, int slot )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		return generator switch
		{
			PaintMaskGenerator.EdgeWear => Baked( mesh, size, BakeMaps.Curvature, set =>
				i => Math.Clamp( (set.Curvature.Rgb[i * 3] / 255f - 0.5f) * 2f, 0f, 1f ) ),

			// Dirt is wherever either says "shut in": occlusion catches the deep folds, cavity the
			// fine creases occlusion is too coarse to see.
			PaintMaskGenerator.Dirt => Baked( mesh, size, BakeMaps.AmbientOcclusion | BakeMaps.Cavity, set =>
				i => MathF.Max( 1f - set.AmbientOcclusion.Rgb[i * 3] / 255f, 1f - set.Cavity.Rgb[i * 3] / 255f ) ),

			PaintMaskGenerator.Gradient => Gradient( mesh, size ),
			PaintMaskGenerator.MaterialId => FaceValues( mesh, size, face => face.Material == slot ? 1f : 0f ),
			_ => null,
		};
	}

	/// <summary>Whether any face of <paramref name="mesh"/> carries a full set of UVs — without one,
	/// no texel belongs to any face and a generator has nothing to say.</summary>
	public static bool HasUVs( PolyMesh mesh )
	{
		foreach ( var face in mesh.Faces )
		{
			if ( face.Count >= 3 && face.UVs is not null && face.UVs.Length == face.Count )
				return true;
		}

		return false;
	}

	static float[] Baked( PolyMesh mesh, int size, BakeMaps maps, Func<BakeSet, Func<int, float>> read )
	{
		var bakeSize = Math.Min( size, MaxBakeSize );
		var set = NormalBake.BakeAll( mesh, mesh, bakeSize, bakeSize, maps, new BakeOptions { AoRays = 32 } );
		var value = read( set );
		var raw = new float[bakeSize * bakeSize];

		for ( var i = 0; i < raw.Length; i++ )
			raw[i] = value( i );

		// The bake's rows run with v (row 0 at v = 0), the same as the paint canvas's Rasterise walk,
		// so no flip: both go through NormalBake.Rasterise.
		return PaintLayers.Resample( raw, bakeSize, bakeSize, size, size );
	}

	/// <summary>Height up the body (+z), 0 at its lowest vertex and 1 at its highest, per texel.</summary>
	static float[] Gradient( PolyMesh mesh, int size )
	{
		var lo = float.MaxValue;
		var hi = float.MinValue;

		foreach ( var p in mesh.Positions )
		{
			lo = MathF.Min( lo, p.z );
			hi = MathF.Max( hi, p.z );
		}

		var span = hi - lo > 1e-6f ? hi - lo : 1f;

		return Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) =>
		{
			var z = mesh.Positions[face.Indices[a]].z * wa + mesh.Positions[face.Indices[b]].z * wb
				+ mesh.Positions[face.Indices[c]].z * wc;
			return Math.Clamp( (z - lo) / span, 0f, 1f );
		} );
	}

	/// <summary>One value per face, written to every texel the face owns.</summary>
	public static float[] FaceValues( PolyMesh mesh, int size, Func<Face, float> value ) =>
		Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) => value( face ) );

	delegate float TexelValue( Face face, int a, int b, int c, float wa, float wb, float wc );

	/// <summary>Walk every UV triangle with the same rasteriser the paint dab and the bake use, so a
	/// mask and a stroke agree about which texel a face owns, then bleed values a few texels past the
	/// islands so filtering at a seam does not reach the empty gutter.</summary>
	static float[] Rasterised( PolyMesh mesh, int size, TexelValue value )
	{
		var result = new float[size * size];
		var filled = new bool[size * size];

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var k = 0; k < face.Count; k++ )
				corners.Add( mesh.Positions[face.Indices[k]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var f = face;
				NormalBake.Rasterise( face.UVs[ia], face.UVs[ib], face.UVs[ic], size, size, ( x, y, wa, wb, wc ) =>
				{
					var i = y * size + x;
					result[i] = value( f, ia, ib, ic, wa, wb, wc );
					filled[i] = true;
				} );
			}
		}

		Bleed( result, filled, size, 4 );
		return result;
	}

	static void Bleed( float[] values, bool[] filled, int size, int passes )
	{
		for ( var pass = 0; pass < passes; pass++ )
		{
			var added = new List<(int, float)>();

			for ( var y = 0; y < size; y++ )
			{
				for ( var x = 0; x < size; x++ )
				{
					var i = y * size + x;

					if ( filled[i] )
						continue;

					float sum = 0;
					var n = 0;

					for ( var dy = -1; dy <= 1; dy++ )
					{
						for ( var dx = -1; dx <= 1; dx++ )
						{
							int nx = x + dx, ny = y + dy;

							if ( nx < 0 || ny < 0 || nx >= size || ny >= size || !filled[ny * size + nx] )
								continue;

							sum += values[ny * size + nx];
							n++;
						}
					}

					if ( n > 0 )
						added.Add( (i, sum / n) );
				}
			}

			if ( added.Count == 0 )
				return;

			foreach ( var (i, v) in added )
			{
				values[i] = v;
				filled[i] = true;
			}
		}
	}

	/// <summary>Generate <paramref name="mask"/>'s values on <paramref name="mesh"/>'s atlas.
	/// Old kinds go through <see cref="Generate"/>; new kinds are generated here, in this class,
	/// so they share <see cref="Rasterised"/> with the old ones — a mask and a stroke agree about
	/// which texel a face owns. This is the engine behind <see cref="PaintMask"/>; the object holds
	/// the parameters, this method does the work.</summary>
	public static float[] GenerateFor( PaintMask mask, PolyMesh mesh, int size )
	{
		if ( mask is null ) throw new ArgumentNullException( nameof( mask ) );
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( size < 1 ) throw new ArgumentOutOfRangeException( nameof( size ) );

		return mask.Kind switch
		{
			PaintMaskKind.None => null,
			PaintMaskKind.EdgeWear => Generate( mesh, size, PaintMaskGenerator.EdgeWear, mask.Slot ),
			PaintMaskKind.Dirt => Generate( mesh, size, PaintMaskGenerator.Dirt, mask.Slot ),
			PaintMaskKind.Gradient => Generate( mesh, size, PaintMaskGenerator.Gradient, mask.Slot ),
			PaintMaskKind.MaterialId => Generate( mesh, size, PaintMaskGenerator.MaterialId, mask.Slot ),
			PaintMaskKind.GradientAxis => GradientAxisMask( mask, mesh, size ),
			PaintMaskKind.Bands => BandsMask( mask, mesh, size ),
			PaintMaskKind.Spots => SpotsMask( mask, mesh, size ),
			PaintMaskKind.CavityRidge => CavityRidgeMask( mask, mesh, size ),
			PaintMaskKind.Scales => ScalesMask( mask, mesh, size ),
			_ => null,
		};
	}

	static Vec3 Point( PolyMesh mesh, Face face, int a, int b, int c, float wa, float wb, float wc ) =>
		mesh.Positions[face.Indices[a]] * wa + mesh.Positions[face.Indices[b]] * wb + mesh.Positions[face.Indices[c]] * wc;

	static (float Lo, float Hi) AxisExtent( PolyMesh mesh, Vec3 axis )
	{
		var lo = float.MaxValue; var hi = float.MinValue;
		foreach ( var p in mesh.Positions ) { var v = Vec3.Dot( p, axis ); lo = MathF.Min( lo, v ); hi = MathF.Max( hi, v ); }
		if ( lo == float.MaxValue ) { lo = 0f; hi = 1f; }
		return (lo, hi);
	}

	static uint Hash01Bits( int x, int y, int z, int seed, int channel )
	{
		unchecked
		{
			uint h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647 + seed * 974634211 + channel * 2654435761);
			h = (h ^ (h >> 13)) * 1274126177;
			h ^= h >> 16;
			return h;
		}
	}

	static float Hash01( int x, int y, int z, int seed, int channel ) =>
		(Hash01Bits( x, y, z, seed, channel ) & 0xFFFFFF) / (float)0xFFFFFF;

	static float[] GradientAxisMask( PaintMask mask, PolyMesh mesh, int size )
	{
		var axis = mask.Axis.Normal;
		if ( axis.LengthSquared == 0f ) axis = new Vec3( 0, 0, 1 );
		var (lo, hi) = AxisExtent( mesh, axis );
		var start = mask.Start == mask.End ? lo : mask.Start;
		var end = mask.Start == mask.End ? hi : mask.End;
		var span = end - start > 1e-6f ? end - start : 1f;
		var falloff = mask.Falloff;
		return Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) =>
		{
			var t = Math.Clamp( (Vec3.Dot( Point( mesh, face, a, b, c, wa, wb, wc ), axis ) - start) / span, 0f, 1f );
			return falloff == 0f ? t : MathF.Pow( t, MathF.Max( 0.1f, 1f + falloff ) );
		} );
	}

	static float[] BandsMask( PaintMask mask, PolyMesh mesh, int size )
	{
		var axis = mask.Axis.Normal;
		if ( axis.LengthSquared == 0f ) axis = new Vec3( 0, 1, 0 );
		var spacing = MathF.Max( mask.Spacing, 1e-3f );
		var width = mask.Width;
		var warp = mask.Warp;
		var taper = mask.TaperAxis.Normal;
		var (tLo, tHi) = taper.LengthSquared == 0f ? (0f, 1f) : AxisExtent( mesh, taper );
		var tSpan = tHi - tLo > 1e-6f ? tHi - tLo : 1f;
		var perp = Vec3.Cross( axis, MathF.Abs( axis.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 ) ).Normal;
		var limited = mask.RangeMin != mask.RangeMax;
		return Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) =>
		{
			var p = Point( mesh, face, a, b, c, wa, wb, wc );
			var s = Vec3.Dot( p, axis );
			if ( limited && (s < mask.RangeMin || s > mask.RangeMax) ) return 0f;
			var sWarp = s + warp * MathF.Sin( 2f * MathF.PI * Vec3.Dot( p, perp ) / spacing );
			var phase = (sWarp - (limited ? mask.RangeMin : 0f)) / spacing;
			var frac = phase - MathF.Floor( phase );
			var wEff = width;
			if ( taper.LengthSquared != 0f )
				wEff = width * (1f - 0.5f * Math.Clamp( (Vec3.Dot( p, taper ) - tLo) / tSpan, 0f, 1f ));
			return frac < wEff ? 1f : 0f;
		} );
	}

	static float[] SpotsMask( PaintMask mask, PolyMesh mesh, int size )
	{
		var spacing = MathF.Max( mask.Spacing, 1e-3f );
		var radius = mask.Radius;
		var jitter = mask.Jitter;
		var ring = mask.RingWidth;
		var seed = mask.Seed;
		return Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) =>
		{
			var p = Point( mesh, face, a, b, c, wa, wb, wc );
			var bx = MathF.Floor( p.x / spacing );
			var by = MathF.Floor( p.y / spacing );
			var bz = MathF.Floor( p.z / spacing );
			var best = float.MaxValue;
			for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
			for ( var dz = -1; dz <= 1; dz++ )
			{
				var cx = bx + dx; var cy = by + dy; var cz = bz + dz;
				var jx = (Hash01( (int)cx, (int)cy, (int)cz, seed, 0 ) - 0.5f) * jitter * spacing;
				var jy = (Hash01( (int)cx, (int)cy, (int)cz, seed, 1 ) - 0.5f) * jitter * spacing;
				var jz = (Hash01( (int)cx, (int)cy, (int)cz, seed, 2 ) - 0.5f) * jitter * spacing;
				var centre = new Vec3( (cx + 0.5f) * spacing + jx, (cy + 0.5f) * spacing + jy, (cz + 0.5f) * spacing + jz );
				best = MathF.Min( best, (p - centre).Length );
			}
			return ring <= 0f ? (best < radius ? 1f : 0f) : (MathF.Abs( best - radius ) < ring * 0.5f ? 1f : 0f);
		} );
	}

	static float[] CavityRidgeMask( PaintMask mask, PolyMesh mesh, int size )
	{
		var cavity = SurfaceDetail.Cavity( mesh );
		var lo = float.MaxValue; var hi = float.MinValue;
		foreach ( var v in cavity ) { lo = MathF.Min( lo, v ); hi = MathF.Max( hi, v ); }
		var span = hi - lo > 1e-6f ? hi - lo : 1f;
		return Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) =>
		{
			var v = cavity[face.Indices[a]] * wa + cavity[face.Indices[b]] * wb + cavity[face.Indices[c]] * wc;
			return Math.Clamp( (v - lo) / span, 0f, 1f );
		} );
	}

	static float[] ScalesMask( PaintMask mask, PolyMesh mesh, int size )
	{
		var su = MathF.Max( mask.ScaleU, 1e-4f );
		var sv = MathF.Max( mask.ScaleV, 1e-4f );
		return Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) =>
		{
			var uv = face.UVs[a] * wa + face.UVs[b] * wb + face.UVs[c] * wc;
			var row = MathF.Floor( uv.y / sv );
			var odd = Math.Abs( (int)row % 2 ) == 1;
			var fu = (uv.x / su + (odd ? 0.5f : 0f)) % 1f;
			if ( fu < 0f ) fu += 1f;
			var fv = (uv.y / sv) % 1f;
			if ( fv < 0f ) fv += 1f;
			var d = MathF.Abs( fu - 0.5f ) * 2f + MathF.Abs( fv - 0.5f ) * 2f;
			return d <= 1f ? 1f : 0f;
		} );
	}
}
/// <summary>Which procedural mask a <see cref="PaintMask"/> makes. The first five are the old
/// <see cref="PaintMaskGenerator"/> kinds, kept in the same order; the rest are the Lane B recipe
/// generators (GradientAxis, Bands, Spots, CavityRidge) plus Scales, as reusable objects instead
/// of inline code inside <see cref="PaintFeature"/>.</summary>
public enum PaintMaskKind
{
	None,
	EdgeWear,
	Dirt,
	Gradient,
	MaterialId,
	GradientAxis,
	Bands,
	Spots,
	CavityRidge,
	Scales,
}

/// <summary>
/// A reusable paint mask object: a named procedural mask plus every parameter it reads, in one
/// place, usable without a <see cref="PaintFeature"/>.
///
/// WHY THIS EXISTS: Lane B put GradientAxis, Bands, Spots and CavityRidge into
/// <see cref="PaintFeature"/>'s private generator, so no other code could make one — a script, a
/// layer stack, or a test had to go through a whole feature and read its <c>Mask</c> back. A
/// <see cref="PaintMask"/> is that same mask, standalone: <c>PaintMask.Bands(...).Generate(mesh, 64)</c>
/// is the mask, shaped and inverted, ready to hand to a layer. The generation itself lives in
/// <see cref="PaintMasks.GenerateFor"/> so all generators share one rasteriser.
///
/// <c>GenerateRaw</c> is the unshaped 0..1 field; <c>Generate</c> applies <see cref="PaintLayers.Shape"/>
/// with <see cref="Level"/>/<see cref="Softness"/> and then <see cref="Invert"/>, exactly the
/// shaping a PaintFeature applies to its generator mask. A null result means "show everywhere"
/// (kind None), which compositing treats as a mask of all ones — see <see cref="PaintLayers.Composite"/>.
/// </summary>
public sealed class PaintMask
{
	public PaintMaskKind Kind;

	/// <summary>Direction GradientAxis measures along and Bands measures across. Zero falls back
	/// to +Z for GradientAxis and +Y for Bands, matching PaintFeature.</summary>
	public Vec3 Axis = new( 0, 0, 1 );

	/// <summary>GradientAxis: axis coordinate mapped to 0 (equal to End means "mesh extent").</summary>
	public float Start;
	/// <summary>GradientAxis: axis coordinate mapped to 1 (equal to Start means "mesh extent").</summary>
	public float End;
	/// <summary>GradientAxis curve: raw t becomes t^(1+Falloff).</summary>
	public float Falloff;

	/// <summary>Bands/Spots grid spacing in mesh units.</summary>
	public float Spacing = 2f;
	/// <summary>Bands width as a fraction of one period, 0..1.</summary>
	public float Width = 0.35f;
	/// <summary>Bands direction along which bands narrow to half width. Zero means no taper.</summary>
	public Vec3 TaperAxis = Vec3.Zero;
	/// <summary>Bands sinusoidal warp amplitude in mesh units.</summary>
	public float Warp;
	/// <summary>Bands range limits along the axis. Equal means "no range limit".</summary>
	public float RangeMin, RangeMax;

	/// <summary>Spots radius in mesh units.</summary>
	public float Radius = 0.5f;
	/// <summary>Spots centre wander as a fraction of the spacing.</summary>
	public float Jitter = 0.35f;
	/// <summary>Spots ring width in mesh units. 0 means filled discs.</summary>
	public float RingWidth;
	/// <summary>Spots grid seed.</summary>
	public int Seed = 1;

	/// <summary>MaterialId: the material slot selected.</summary>
	public int Slot;

	/// <summary>Scales diamond size in UV units (u across, v along).</summary>
	public float ScaleU = 0.08f, ScaleV = 0.05f;

	/// <summary>Where the shaped mask switches from hidden to shown, 0..1.</summary>
	public float Level = 0.5f;
	/// <summary>How wide that switch is: 0 is a hard edge, 1 a long fade.</summary>
	public float Softness = 0.3f;
	/// <summary>Swap what the mask shows and hides, after shaping.</summary>
	public bool Invert;

	public PaintMask( PaintMaskKind kind ) { Kind = kind; }

	public static PaintMask None() => new( PaintMaskKind.None );
	public static PaintMask EdgeWear() => new( PaintMaskKind.EdgeWear );
	public static PaintMask Dirt() => new( PaintMaskKind.Dirt );
	public static PaintMask Gradient() => new( PaintMaskKind.Gradient );
	public static PaintMask MaterialId( int slot ) => new( PaintMaskKind.MaterialId ) { Slot = slot };
	public static PaintMask GradientAxis( Vec3 axis, float start = 0f, float end = 0f, float falloff = 0f ) =>
		new( PaintMaskKind.GradientAxis ) { Axis = axis, Start = start, End = end, Falloff = falloff };
	public static PaintMask Bands( Vec3 axis, float spacing, float width, Vec3 taper = default, float warp = 0f,
		float rangeMin = 0f, float rangeMax = 0f ) =>
		new( PaintMaskKind.Bands ) { Axis = axis, Spacing = spacing, Width = width, TaperAxis = taper, Warp = warp, RangeMin = rangeMin, RangeMax = rangeMax };
	public static PaintMask Spots( float radius, float spacing, float jitter = 0.35f, float ringWidth = 0f, int seed = 1 ) =>
		new( PaintMaskKind.Spots ) { Radius = radius, Spacing = spacing, Jitter = jitter, RingWidth = ringWidth, Seed = seed };
	public static PaintMask CavityRidge() => new( PaintMaskKind.CavityRidge );
	public static PaintMask Scales( float scaleU = 0.08f, float scaleV = 0.05f ) =>
		new( PaintMaskKind.Scales ) { ScaleU = scaleU, ScaleV = scaleV };

	/// <summary>Parse a display name ("Gradient Axis", "Cavity Ridge", "Edge Wear") or a compact
	/// name ("gradientaxis", "cavityridge", "edgewear") into a kind. Anything else fails loudly.</summary>
	public static PaintMaskKind KindFromName( string name )
	{
		var n = (name ?? "").Trim().ToLowerInvariant().Replace( " ", "" ).Replace( "_", "" );
		return n switch
		{
			"none" => PaintMaskKind.None,
			"edgewear" => PaintMaskKind.EdgeWear,
			"dirt" => PaintMaskKind.Dirt,
			"gradient" => PaintMaskKind.Gradient,
			"materialid" => PaintMaskKind.MaterialId,
			"gradientaxis" => PaintMaskKind.GradientAxis,
			"bands" => PaintMaskKind.Bands,
			"spots" => PaintMaskKind.Spots,
			"cavityridge" or "cavityandridge" => PaintMaskKind.CavityRidge,
			"scales" => PaintMaskKind.Scales,
			_ => throw new ArgumentException( $"Unknown mask '{name}'. Masks are: None, Edge Wear, Dirt, Gradient, Material ID, Gradient Axis, Bands, Spots, Cavity Ridge, Scales.", nameof( name ) ),
		};
	}

	public static PaintMask FromName( string name ) => new( KindFromName( name ) );

	/// <summary>Parse a mask spec: a kind name (spaced or compact) followed by key=value pairs, e.g.
	/// <c>"bands axis=0,0,1 spacing=0.5 width=0.5"</c> or <c>"gradient axis=0,1,0 falloff=0.5"</c>.
	/// <c>from</c>/<c>to</c> are accepted for both GradientAxis (Start/End) and Bands (RangeMin/RangeMax);
	/// <c>start</c>/<c>end</c> and <c>rangemin</c>/<c>rangemax</c> name them directly. Unknown keys fail
	/// loudly instead of being silently ignored.</summary>
	public static PaintMask Parse( string spec )
	{
		if ( string.IsNullOrWhiteSpace( spec ) )
			throw new ArgumentException( "A mask spec names a mask, e.g. 'bands spacing=0.5 width=0.5'.", nameof( spec ) );
		var tokens = spec.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		var kindWords = new List<string>();
		var i = 0;
		while ( i < tokens.Length && !tokens[i].Contains( '=' ) ) { kindWords.Add( tokens[i] ); i++; }
		var mask = FromName( string.Join( " ", kindWords ) );
		for ( ; i < tokens.Length; i++ )
		{
			var kv = tokens[i].Split( '=', 2 );
			if ( kv.Length != 2 )
				throw new ArgumentException( $"Mask spec token '{tokens[i]}' is not key=value.", nameof( spec ) );
			var key = kv[0].Trim().ToLowerInvariant().Replace( "_", "" );
			var val = kv[1].Trim();
			switch ( key )
			{
				case "axis": mask.Axis = ParseVec3( val ); break;
				case "start": mask.Start = ParseFloat( val ); break;
				case "end": mask.End = ParseFloat( val ); break;
				case "from": mask.Start = ParseFloat( val ); mask.RangeMin = ParseFloat( val ); break;
				case "to": mask.End = ParseFloat( val ); mask.RangeMax = ParseFloat( val ); break;
				case "falloff": mask.Falloff = ParseFloat( val ); break;
				case "spacing": mask.Spacing = ParseFloat( val ); break;
				case "width": mask.Width = ParseFloat( val ); break;
				case "taper": case "taperaxis": mask.TaperAxis = ParseVec3( val ); break;
				case "warp": mask.Warp = ParseFloat( val ); break;
				case "rangemin": mask.RangeMin = ParseFloat( val ); break;
				case "rangemax": mask.RangeMax = ParseFloat( val ); break;
				case "radius": mask.Radius = ParseFloat( val ); break;
				case "jitter": mask.Jitter = ParseFloat( val ); break;
				case "ringwidth": mask.RingWidth = ParseFloat( val ); break;
				case "seed": mask.Seed = (int)ParseFloat( val ); break;
				case "slot": mask.Slot = (int)ParseFloat( val ); break;
				case "level": mask.Level = ParseFloat( val ); break;
				case "softness": mask.Softness = ParseFloat( val ); break;
				case "invert": mask.Invert = val == "1" || val.Equals( "true", StringComparison.OrdinalIgnoreCase ) || val.Equals( "yes", StringComparison.OrdinalIgnoreCase ); break;
				case "scaleu": mask.ScaleU = ParseFloat( val ); break;
				case "scalev": mask.ScaleV = ParseFloat( val ); break;
				default: throw new ArgumentException( $"Unknown mask parameter '{kv[0]}'.", nameof( spec ) );
			}
		}
		return mask;
	}

	static float ParseFloat( string text ) =>
		float.Parse( text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture );

	static Vec3 ParseVec3( string text )
	{
		var parts = text.Split( ',' );
		if ( parts.Length != 3 )
			throw new ArgumentException( $"A vector is x,y,z, e.g. 0,1,0 — got '{text}'.", nameof( text ) );
		return new Vec3( ParseFloat( parts[0] ), ParseFloat( parts[1] ), ParseFloat( parts[2] ) );
	}

	/// <summary>The unshaped 0..1 field for this mask on <paramref name="mesh"/>'s atlas, or null
	/// for kind None. A fresh array every call; shaping and combining never mutate a cached field.</summary>
	public float[] GenerateRaw( PolyMesh mesh, int size ) => PaintMasks.GenerateFor( this, mesh, size );

	/// <summary>The mask as a layer uses it: raw field, shaped by Level/Softness, then inverted
	/// when asked. Null for kind None (show everywhere).</summary>
	public float[] Generate( PolyMesh mesh, int size )
	{
		var raw = GenerateRaw( mesh, size );
		if ( raw is null ) return null;
		var shaped = PaintLayers.Shape( raw, Level, Softness );
		if ( Invert ) for ( var i = 0; i < shaped.Length; i++ ) shaped[i] = 1f - shaped[i];
		return shaped;
	}

	/// <summary>Elementwise product of two masks. A null mask means "show everywhere" (all ones),
	/// so it leaves the other unchanged; both null is null. Lengths must match when both are set.</summary>
	public static float[] Multiply( float[] a, float[] b )
	{
		if ( a is null ) return b is null ? null : (float[])b.Clone();
		if ( b is null ) return (float[])a.Clone();
		if ( a.Length != b.Length ) throw new ArgumentException( "Masks to combine must be the same size." );
		var r = new float[a.Length];
		for ( var i = 0; i < r.Length; i++ ) r[i] = a[i] * b[i];
		return r;
	}

	/// <summary>Elementwise maximum of two masks. A null mask means "show everywhere", so the
	/// maximum with one is all ones; both null is null.</summary>
	public static float[] Max( float[] a, float[] b )
	{
		if ( a is null && b is null ) return null;
		if ( a is null ) { var r = new float[b.Length]; Array.Fill( r, 1f ); return r; }
		if ( b is null ) { var r = new float[a.Length]; Array.Fill( r, 1f ); return r; }
		if ( a.Length != b.Length ) throw new ArgumentException( "Masks to combine must be the same size." );
		var r2 = new float[a.Length];
		for ( var i = 0; i < r2.Length; i++ ) r2[i] = MathF.Max( a[i], b[i] );
		return r2;
	}
}
