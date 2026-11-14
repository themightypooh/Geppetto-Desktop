using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// A painted body's whole texture set: the images its paint exports as, and the .vmat that binds them.
///
/// <see cref="PaintMaterial"/> is the colour-only path and stays as it was; this is the same chain for
/// every channel. Colour bakes over the default grey, roughness and metalness bake over their own base
/// values as grey images, and a painted height becomes a tangent-space normal map — complex.vfx has no
/// height input for an ordinary surface, but it lights a normal map, and a normal map derived from the
/// painted height is how the bumps a player painted actually show.
///
/// The metalness binding is the one the engine's own material generator writes: <c>F_METALNESS_TEXTURE 1</c>
/// with <c>TextureMetalness</c> beside <c>TextureRoughness</c> (Facepunch/sbox-public commit 0a2c0f0,
/// "Create Material"). A channel nobody painted keeps the engine default, exactly as the colour-only
/// material did.
/// </summary>
public static class PaintTextureSet
{
	/// <summary>One image to write: its file suffix (`color`, `rough`, `metal`, `normal`), RGBA bytes
	/// and size.</summary>
	public sealed class Image
	{
		public string Suffix;
		public byte[] Rgba;
		public int Width, Height;
	}

	/// <summary>
	/// How steep the normal map leans per unit of painted height difference across one texel. A full
	/// black-to-white step over one texel leans the normal by about 63°; a soft brush spreads that over
	/// many texels and reads as a gentle bump. A tuning value, not a physical one — the painted height
	/// has no units, the same as any height-to-normal filter's strength.
	/// </summary>
	public const float DefaultBumpStrength = 2f;

	/// <summary>
	/// Every image the paint on <paramref name="paint"/> exports as, in channel order. Colour is
	/// always present when there is paint at all; the others only when painted.
	///
	/// Colour spaces: colour and emit are sRGB; rough, metal, normal, ao and opacity are linear
	/// data. The normal map is tangent-space in s&amp;box DirectX green (painted Normal and
	/// height-derived combined); glTF export flips its G channel to OpenGL green.
	/// </summary>
	public static List<Image> Images( PaintCanvas paint, float bumpStrength = DefaultBumpStrength )
	{
		var images = new List<Image>();

		if ( paint is null )
			return images;

		images.Add( new Image { Suffix = "color", Rgba = PaintLayers.Opaque( paint, PaintChannel.Color ), Width = paint.Width, Height = paint.Height } );

		if ( paint.RoughnessMap is { } rough )
			images.Add( new Image { Suffix = "rough", Rgba = PaintLayers.Opaque( paint, PaintChannel.Roughness ), Width = rough.Width, Height = rough.Height } );

		if ( paint.MetalnessMap is { } metal )
			images.Add( new Image { Suffix = "metal", Rgba = PaintLayers.Opaque( paint, PaintChannel.Metalness ), Width = metal.Width, Height = metal.Height } );

		// The normal map: the one derived from painted height, the painted Normal channel, or both combined
		// (whiteout blend, so painted detail rides on the height's bumps rather than replacing them).
		var fromHeight = paint.HeightMap is { } height ? HeightToNormal( height, bumpStrength ) : null;
		var painted = paint.NormalMap is { } normalMap ? PaintLayers.Opaque( paint, PaintChannel.Normal ) : null;
		if ( fromHeight is not null || painted is not null )
		{
			var src = painted is not null ? paint.NormalMap : paint.HeightMap;
			var rgba = painted is null ? fromHeight
				: fromHeight is null ? painted
				: CombineNormals( fromHeight, paint.HeightMap.Width, paint.HeightMap.Height, painted, src.Width, src.Height );
			images.Add( new Image { Suffix = "normal", Rgba = rgba, Width = src.Width, Height = src.Height } );
		}

		if ( paint.HeightMap is { } heightAo )
			images.Add( new Image { Suffix = "ao", Rgba = HeightToAo( heightAo ), Width = heightAo.Width, Height = heightAo.Height } );

		// Emissive is a grey self-illumination mask (0 dark, 1 glows at the albedo), baked over 0;
		// opacity is grey (1 solid, 0 see-through), baked over 1. Both only when painted.
		if ( paint.EmissiveMap is { } emit )
			images.Add( new Image { Suffix = "emit", Rgba = PaintLayers.Opaque( paint, PaintChannel.Emissive ), Width = emit.Width, Height = emit.Height } );

		if ( paint.OpacityMap is { } opacity )
			images.Add( new Image { Suffix = "opacity", Rgba = PaintLayers.Opaque( paint, PaintChannel.Opacity ), Width = opacity.Width, Height = opacity.Height } );

		return images;
	}

	/// <summary>
	/// Two tangent-space normal maps (RGBA bytes) as one, at <paramref name="detail"/>'s size: the whiteout blend,
	/// normalize(a.xy + b.xy, a.z * b.z), with <paramref name="baseMap"/> sampled nearest. Flat over flat stays flat.
	/// </summary>
	public static byte[] CombineNormals( byte[] baseMap, int bw, int bh, byte[] detail, int dw, int dh )
	{
		var o = new byte[dw * dh * 4];
		for ( var y = 0; y < dh; y++ )
			for ( var x = 0; x < dw; x++ )
			{
				var i = (y * dw + x) * 4;
				var j = (Math.Min( y * bh / dh, bh - 1 ) * bw + Math.Min( x * bw / dw, bw - 1 )) * 4;
				float U( byte v ) => v / 255f * 2f - 1f;
				var nx = U( baseMap[j] ) + U( detail[i] );
				var ny = U( baseMap[j + 1] ) + U( detail[i + 1] );
				var nz = U( baseMap[j + 2] ) * U( detail[i + 2] );
				var len = MathF.Sqrt( nx * nx + ny * ny + nz * nz );
				if ( len < 1e-6f ) { nx = 0; ny = 0; nz = 1; len = 1; }
				byte E( float v ) => (byte)Math.Clamp( MathF.Round( (v / len * 0.5f + 0.5f) * 255f ), 0f, 255f );
				o[i] = E( nx ); o[i + 1] = E( ny ); o[i + 2] = E( nz ); o[i + 3] = 255;
			}
		return o;
	}

	/// <summary>
	/// A painted height canvas as a tangent-space normal map (RGBA, alpha 255).
	///
	/// The height is first baked opaque over the flat base (0.5), so unpainted texels are level. The
	/// slope is a central difference: along +x of the image is +u, and down the image (+row) is +v,
	/// because the paint canvas is rasterised with row = v·height — the same walk NormalBake uses. So
	/// in the bake's frame (x along dP/du, y along dP/dv, z out) the normal is
	/// normalize(−s·∂h/∂u, −s·∂h/∂v, 1).
	///
	/// GREEN FOLLOWS NormalBake's SETTLED CONVENTION, not a fresh guess: s&amp;box applies +green along
	/// −dP/dv (see <see cref="BakeOptions.FlipGreen"/>), so y is written negated — a painted dent and
	/// a baked dent light the same way.
	/// </summary>
	public static byte[] HeightToNormal( PaintCanvas height, float strength = DefaultBumpStrength )
	{
		if ( height is null )
			throw new ArgumentNullException( nameof( height ) );

		var w = height.Width;
		var h = height.Height;
		var b = (byte)MathF.Round( PaintChannels.Base( PaintChannel.Height ) * 255f );
		var opaque = new byte[w * h * 4];
		height.BakeOpaque( opaque, b, b, b );

		float H( int x, int y ) => opaque[(Math.Clamp( y, 0, h - 1 ) * w + Math.Clamp( x, 0, w - 1 )) * 4] / 255f;

		var result = new byte[w * h * 4];

		for ( var y = 0; y < h; y++ )
		{
			for ( var x = 0; x < w; x++ )
			{
				var du = (H( x + 1, y ) - H( x - 1, y )) * 0.5f;
				var dv = (H( x, y + 1 ) - H( x, y - 1 )) * 0.5f;
				var n = new Vec3( -strength * du, -strength * dv, 1f ).Normal;
				var o = (y * w + x) * 4;

				result[o] = Encode( n.x );
				result[o + 1] = Encode( -n.y );
				result[o + 2] = Encode( n.z );
				result[o + 3] = 255;
			}
		}

		return result;
	}

	/// <summary>
	/// AO from the same painted height that yields the normal map (P3):
	/// a texel darker than its blurred neighbourhood is a dent, and dents
	/// get AO. This is the principled fix for "pore pits do NOT read
	/// under even light" — height/normal alone do not read there, AO
	/// does. Absolute low height darkens slightly too. Grey RGBA, alpha
	/// 255, linear (the slot pipeline samples AO as linear).
	/// </summary>
	public static byte[] HeightToAo( PaintCanvas height )
	{
		if ( height is null )
			throw new ArgumentNullException( nameof( height ) );

		var w = height.Width;
		var h = height.Height;
		var b = (byte)MathF.Round( PaintChannels.Base( PaintChannel.Height ) * 255f );
		var opaque = new byte[w * h * 4];
		height.BakeOpaque( opaque, b, b, b );

		float H( int x, int y ) => opaque[(Math.Clamp( y, 0, h - 1 ) * w + Math.Clamp( x, 0, w - 1 )) * 4] / 255f;

		var result = new byte[w * h * 4];
		for ( var y = 0; y < h; y++ )
			for ( var x = 0; x < w; x++ )
			{
				var sum = 0f;
				for ( var dy = -1; dy <= 1; dy++ )
					for ( var dx = -1; dx <= 1; dx++ )
						sum += H( x + dx, y + dy );
				var blur = sum / 9f;
				var h0 = H( x, y );
				var cavity = Math.Clamp( blur - h0, 0f, 1f );
				var a = Math.Clamp( 1f - 0.70f * cavity - 0.15f * (1f - h0), 0.35f, 1f );
				var g = (byte)Math.Clamp( MathF.Round( a * 255f ), 0f, 255f );
				var o = (y * w + x) * 4;
				result[o] = g; result[o + 1] = g; result[o + 2] = g; result[o + 3] = 255;
			}
		return result;
	}

	static byte Encode( float v ) => (byte)Math.Clamp( MathF.Round( (v * 0.5f + 0.5f) * 255f ), 0f, 255f );

	/// <summary>
	/// The .vmat for a painted texture set. <paramref name="images"/> maps a suffix from
	/// <see cref="Images"/> to the image path the material should name; a channel missing from it
	/// keeps the engine default. Same Layer0 form and complex.vfx as <see cref="PaintMaterial.VmatSource"/>.
	/// </summary>
	public static string VmatSource( IReadOnlyDictionary<string, string> images )
	{
		if ( images is null || !images.TryGetValue( "color", out var color ) )
			throw new ArgumentException( "A painted texture set always has a colour image.", nameof( images ) );

		var sb = new StringBuilder();
		sb.Append( "// THIS FILE IS AUTO-GENERATED\n\nLayer0\n{\n\tshader \"complex.vfx\"\n" );

		if ( images.ContainsKey( "metal" ) )
			sb.Append( "\tF_METALNESS_TEXTURE 1\n" );

		if ( images.ContainsKey( "emit" ) )
			sb.Append( "\tF_SELF_ILLUM 1\n" );

		if ( images.ContainsKey( "opacity" ) )
			sb.Append( "\tF_TRANSLUCENT 1\n" );

		sb.Append( "\n\t//---- Color ----\n" );
		sb.Append( "\tg_flModelTintAmount \"1.000\"\n" );
		sb.Append( "\tg_vColorTint \"[1.000000 1.000000 1.000000 0.000000]\"\n" );
		sb.Append( $"\tTextureColor \"{color}\"\n" );

		sb.Append( "\n\t//---- Lighting ----\n" );
		sb.Append( "\tg_flDirectionalLightmapMinZ \"0.050\"\n" );
		sb.Append( "\tg_flDirectionalLightmapStrength \"1.000\"\n" );

		sb.Append( "\n\t//---- Metalness ----\n" );

		if ( images.TryGetValue( "metal", out var metal ) )
			sb.Append( $"\tTextureMetalness \"{metal}\"\n" );
		else
			sb.Append( "\tg_flMetalness \"0.000\"\n" );

		sb.Append( "\n\t//---- Normal ----\n" );
		sb.Append( $"\tTextureNormal \"{(images.TryGetValue( "normal", out var normal ) ? normal : "materials/default/default_normal.tga")}\"\n" );

		if ( images.TryGetValue( "ao", out var ao ) )
			sb.Append( "\n\t//---- Ambient Occlusion ----\n" + $"\tTextureAmbientOcclusion \"{ao}\"\n" );

		sb.Append( "\n\t//---- Roughness ----\n" );
		sb.Append( $"\tTextureRoughness \"{(images.TryGetValue( "rough", out var rough ) ? rough : "materials/default/default_rough.tga")}\"\n" );

		if ( images.TryGetValue( "emit", out var emit ) )
		{
			sb.Append( "\n\t//---- Self Illum ----\n" );
			sb.Append( "\tg_flSelfIllumAlbedoFactor \"1.000\"\n" );
			sb.Append( "\tg_flSelfIllumBrightness \"1.000\"\n" );
			sb.Append( "\tg_flSelfIllumScale \"1.000\"\n" );
			sb.Append( "\tg_vSelfIllumTint \"[1.000000 1.000000 1.000000 0.000000]\"\n" );
			sb.Append( $"\tTextureSelfIllumMask \"{emit}\"\n" );
		}

		if ( images.TryGetValue( "opacity", out var opacity ) )
		{
			sb.Append( "\n\t//---- Translucent ----\n" );
			sb.Append( "\tg_flOpacityScale \"1.000\"\n" );
			sb.Append( $"\tTextureTranslucency \"{opacity}\"\n" );
		}

		sb.Append( "\n\t//---- Texture Coordinates ----\n" );
		sb.Append( "\tg_nScaleTexCoordUByModelScaleAxis \"0\"\n" );
		sb.Append( "\tg_nScaleTexCoordVByModelScaleAxis \"0\"\n" );
		sb.Append( "\tg_vTexCoordOffset \"[0.000 0.000]\"\n" );
		sb.Append( "\tg_vTexCoordScale \"[1.000 1.000]\"\n" );
		sb.Append( "\tg_vTexCoordScrollSpeed \"[0.000 0.000]\"\n" );
		sb.Append( "}\n" );

		return sb.ToString();
	}

	/// <summary>
	/// Write the whole set into <paramref name="folder"/>: <c>{baseName}.png</c> for colour,
	/// <c>{baseName}_rough/_metal/_normal.png</c> for the rest, and <c>{baseName}.vmat</c> naming them
	/// by <paramref name="assetPath"/> (file path → the path the material should name). Returns every
	/// file written, the .vmat last. The editor's compile and the `paintmaps` script verb both call
	/// this, so a painted body exports the same files from either.
	/// </summary>
	public static List<string> Write( PaintCanvas paint, string folder, string baseName, Func<string, string> assetPath,
		float bumpStrength = DefaultBumpStrength )
	{
		if ( paint is null )
			throw new ArgumentNullException( nameof( paint ) );

		Directory.CreateDirectory( folder );
		var written = new List<string>();
		var named = new Dictionary<string, string>();

		foreach ( var image in Images( paint, bumpStrength ) )
		{
			var path = Path.Combine( folder, image.Suffix == "color" ? $"{baseName}.png" : $"{baseName}_{image.Suffix}.png" );
			PngWriter.WriteFileRgba( path, image.Rgba, image.Width, image.Height );
			written.Add( path );
			named[image.Suffix] = assetPath( path );
		}

		var vmat = Path.Combine( folder, $"{baseName}.vmat" );
		File.WriteAllText( vmat, VmatSource( named ) );
		written.Add( vmat );
		return written;
	}

	/// <summary>
	/// A material for Effigy's own renderer showing this paint the way the exported set will: the
	/// colour, roughness and metallic images and the normal map from height, all at the colour
	/// canvas's size (a channel painted at another resolution is resampled to it).
	/// </summary>
	public static Effigy.Render.ShadeMaterial Shade( PaintCanvas paint, float bumpStrength = DefaultBumpStrength )
	{
		if ( paint is null )
			throw new ArgumentNullException( nameof( paint ) );

		var size = paint.Width;

		byte[] At( PaintCanvas canvas, PaintChannel channel )
		{
			if ( canvas is null )
				return null;

			var holder = new PaintCanvas( size, size );
			var resized = canvas.Width == size && canvas.Height == size ? canvas : PaintLayers.Resample( canvas, size, size );

			switch ( channel )
			{
				case PaintChannel.Roughness: holder.RoughnessMap = resized; break;
				case PaintChannel.Metalness: holder.MetalnessMap = resized; break;
				case PaintChannel.Emissive: holder.EmissiveMap = resized; break;
				default: return PaintLayers.Opaque( resized, PaintChannel.Color );
			}

			return PaintLayers.Opaque( holder, channel );
		}

		var height = paint.HeightMap is null ? null
			: paint.HeightMap.Width == size ? paint.HeightMap : PaintLayers.Resample( paint.HeightMap, size, size );

		// The preview glows the way complex.vfx does: emission = albedo x the self-illumination mask.
		byte[] emission = null;

		if ( paint.EmissiveMap is not null && At( paint.EmissiveMap, PaintChannel.Emissive ) is { } emitMask )
		{
			var albedo = PaintLayers.Opaque( paint, PaintChannel.Color );
			emission = new byte[albedo.Length];

			for ( var i = 0; i < albedo.Length; i += 4 )
			{
				var k = emitMask[i] / 255f;
				emission[i] = (byte)MathF.Round( albedo[i] * k );
				emission[i + 1] = (byte)MathF.Round( albedo[i + 1] * k );
				emission[i + 2] = (byte)MathF.Round( albedo[i + 2] * k );
				emission[i + 3] = 255;
			}
		}

		return new Effigy.Render.ShadeMaterial
		{
			EmissionMap = emission,
			MapSize = size,
			AlbedoMap = PaintLayers.Opaque( paint, PaintChannel.Color ),
			Roughness = PaintChannels.Base( PaintChannel.Roughness ),
			RoughnessMap = At( paint.RoughnessMap, PaintChannel.Roughness ),
			MetallicMap = At( paint.MetalnessMap, PaintChannel.Metalness ),
			NormalMap = height is null ? null : HeightToNormal( height, bumpStrength ),
		};
	}
}
