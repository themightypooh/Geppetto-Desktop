using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One paint layer's own content, before it meets the layers below: a straight-alpha canvas per
/// surface channel it touches (null where it touches nothing), and a per-texel mask.
///
/// This is what a <see cref="PaintFeature"/> builds from its strokes and its fill. It is not what
/// the body wears — <see cref="PaintLayers.CompositeSet"/> lays it over the paint already there.
/// </summary>
public sealed class PaintLayerMaps
{
	public PaintCanvas Color, Roughness, Metalness, Height, Emissive, Opacity, Normal;

	/// <summary>The painted layer mask (black hides, white shows; coverage in alpha), or null when
	/// nobody painted on the mask.</summary>
	public PaintCanvas MaskPaint;

	/// <summary>Texels across (layers are square, like the canvas they replay onto).</summary>
	public int Size;

	public PaintCanvas Get( PaintChannel channel ) => channel switch
	{
		PaintChannel.Color => Color,
		PaintChannel.Roughness => Roughness,
		PaintChannel.Metalness => Metalness,
		PaintChannel.Height => Height,
		PaintChannel.Emissive => Emissive,
		PaintChannel.Opacity => Opacity,
		PaintChannel.Normal => Normal,
		PaintChannel.Mask => MaskPaint,
		_ => null,
	};

	public void Set( PaintChannel channel, PaintCanvas canvas )
	{
		switch ( channel )
		{
			case PaintChannel.Color: Color = canvas; break;
			case PaintChannel.Roughness: Roughness = canvas; break;
			case PaintChannel.Metalness: Metalness = canvas; break;
			case PaintChannel.Height: Height = canvas; break;
			case PaintChannel.Emissive: Emissive = canvas; break;
			case PaintChannel.Opacity: Opacity = canvas; break;
			case PaintChannel.Normal: Normal = canvas; break;
			case PaintChannel.Mask: MaskPaint = canvas; break;
		}
	}

	/// <summary>Whether any surface channel carries content. A layer with only a mask adds nothing.</summary>
	public bool HasSurface => Color is not null || Roughness is not null || Metalness is not null || Height is not null
		|| Emissive is not null || Opacity is not null || Normal is not null;
}

/// <summary>
/// Layer compositing for paint: how one layer lands on the paint below it.
///
/// EVERY CANVAS HERE IS STRAIGHT ALPHA, like <see cref="PaintCanvas"/> itself, so a blend mode has to
/// say what happens where the backdrop is partly transparent. The answer is the W3C one (Compositing
/// and Blending Level 1, §5.8 "general formula for compositing and blending"), which every layer-based
/// painter implements:
///
///   αo = αs + αb·(1 − αs)
///   Co = [ αs·(1 − αb)·Cs + αs·αb·B(Cb, Cs) + (1 − αs)·αb·Cb ] / αo
///
/// Where nothing is below (αb = 0) the layer shows as itself whatever its mode — a Multiply layer on a
/// bare surface is its own colour, not black — and where the backdrop is opaque the blend function B
/// decides. Normal is B = Cs, which reduces exactly to <see cref="PaintCanvas.SourceOver"/>, and Normal
/// is routed through that very function so a one-layer document composites bit-for-bit as before.
///
/// αs is the layer's own coverage times its opacity times its mask, per texel: opacity and mask are
/// one more factor on how much of the layer arrives, which is all either of them means.
/// </summary>
public static class PaintLayers
{
	/// <summary>The blend function B(Cb, Cs) for one channel, everything in 0..1.</summary>
	public static float Blend( float cb, float cs, PaintBlendMode mode ) => mode switch
	{
		PaintBlendMode.Multiply => cb * cs,
		PaintBlendMode.Screen => cb + cs - cb * cs,
		// Overlay is HardLight with the layers swapped: the BACKDROP decides multiply or screen.
		PaintBlendMode.Overlay => cb <= 0.5f ? cs * 2f * cb : Screen( cs, 2f * cb - 1f ),
		PaintBlendMode.Add => MathF.Min( 1f, cb + cs ),
		PaintBlendMode.Darken => MathF.Min( cb, cs ),
		PaintBlendMode.Lighten => MathF.Max( cb, cs ),
		PaintBlendMode.Difference => MathF.Abs( cb - cs ),
		PaintBlendMode.Exclusion => cb + cs - 2f * cb * cs,
		PaintBlendMode.HardLight => cs <= 0.5f ? cb * 2f * cs : Screen( cb, 2f * cs - 1f ),
		PaintBlendMode.SoftLight => SoftLight( cb, cs ),
		PaintBlendMode.ColorDodge => cb <= 0f ? 0f : cs >= 1f ? 1f : MathF.Min( 1f, cb / (1f - cs) ),
		PaintBlendMode.ColorBurn => cb >= 1f ? 1f : cs <= 0f ? 0f : 1f - MathF.Min( 1f, (1f - cb) / cs ),
		_ => cs,
	};

	// W3C Compositing Level 1 §10 soft-light.
	static float SoftLight( float cb, float cs )
	{
		if ( cs <= 0.5f ) return cb - (1f - 2f * cs) * cb * (1f - cb);
		var d = cb <= 0.25f ? ((16f * cb - 12f) * cb + 4f) * cb : MathF.Sqrt( cb );
		return cb + (2f * cs - 1f) * (d - cb);
	}

	static float Screen( float a, float b ) => a + b - a * b;

	/// <summary>
	/// One texel of a layer over one texel of backdrop, straight RGBA in bytes, with the layer's
	/// effective alpha <paramref name="sa"/> (its coverage × opacity × mask) given as a float.
	/// </summary>
	public static (byte R, byte G, byte B, byte A) Over(
		byte dr, byte dg, byte db, byte da, byte sr, byte sg, byte sb, float sa, PaintBlendMode mode )
	{
		if ( sa <= 0f )
			return (dr, dg, db, da);

		if ( mode == PaintBlendMode.Normal )
			return PaintCanvas.SourceOver( dr, dg, db, da, sr, sg, sb, sa );

		sa = MathF.Min( sa, 1f );
		var ab = da / 255f;
		var ao = sa + ab * (1f - sa);

		float Channel( byte b, byte s )
		{
			var cb = b / 255f;
			var cs = s / 255f;
			var premul = sa * (1f - ab) * cs + sa * ab * Blend( cb, cs, mode ) + (1f - sa) * ab * cb;
			return premul / ao;
		}

		return (ToByte( Channel( dr, sr ) ), ToByte( Channel( dg, sg ) ), ToByte( Channel( db, sb ) ), ToByte( ao ));
	}

	static byte ToByte( float v ) => (byte)Math.Clamp( MathF.Round( v * 255f ), 0f, 255f );

	/// <summary>
	/// <paramref name="layer"/> over <paramref name="below"/>, as a NEW canvas. Neither input is
	/// touched: the backdrop is the layer below's cached result, shared with the rebuild that made it,
	/// and writing into it would composite this layer into that one the next time it is reused.
	///
	/// A null layer hands the backdrop back unchanged (the same object — nothing was composited). A
	/// null backdrop is a transparent one. When the two differ in size the backdrop is resampled to
	/// the layer's, so two layers painted at different resolutions still stack.
	/// </summary>
	public static PaintCanvas Composite( PaintCanvas below, PaintCanvas layer, PaintBlendMode mode,
		float opacity, float[] mask )
	{
		if ( layer is null )
			return below;

		var w = layer.Width;
		var h = layer.Height;
		var result = below is null ? new PaintCanvas( w, h ) : Resample( below, w, h );
		var dst = result.Rgba;
		var src = layer.Rgba;
		opacity = Math.Clamp( opacity, 0f, 1f );

		for ( var i = 0; i < w * h; i++ )
		{
			var o = i * 4;
			var sa = src[o + 3] / 255f * opacity * (mask is null ? 1f : mask[i]);

			if ( sa <= 0f )
				continue;

			var (r, g, b, a) = Over( dst[o], dst[o + 1], dst[o + 2], dst[o + 3], src[o], src[o + 1], src[o + 2], sa, mode );
			dst[o] = r;
			dst[o + 1] = g;
			dst[o + 2] = b;
			dst[o + 3] = a;
		}

		result.Invalidate();
		return result;
	}

	/// <summary>
	/// A whole layer over a body's paint: every surface channel composited on its own, the result's
	/// companions set from the per-channel results. A channel the layer does not touch is carried
	/// through as it was below. Returns the backdrop itself when the layer has no surface content.
	/// </summary>
	public static PaintCanvas CompositeSet( PaintCanvas below, PaintLayerMaps layer, PaintBlendMode mode,
		float opacity, float[] mask )
	{
		if ( layer is null || !layer.HasSurface )
			return below;

		// The colour canvas carries the companions, so it always exists on the result — an
		// all-transparent colour where only roughness was painted reads as "no colour paint here".
		var colour = layer.Color is not null
			? Composite( below, layer.Color, mode, opacity, mask )
			: below is null
				? new PaintCanvas( layer.Size, layer.Size )
				: Strip( below );

		colour.RoughnessMap = Composite( below?.RoughnessMap, layer.Roughness, mode, opacity, mask );
		colour.MetalnessMap = Composite( below?.MetalnessMap, layer.Metalness, mode, opacity, mask );
		colour.HeightMap = Composite( below?.HeightMap, layer.Height, mode, opacity, mask );
		colour.EmissiveMap = Composite( below?.EmissiveMap, layer.Emissive, mode, opacity, mask );
		colour.OpacityMap = Composite( below?.OpacityMap, layer.Opacity, mode, opacity, mask );
		colour.NormalMap = Composite( below?.NormalMap, layer.Normal, mode, opacity, mask );
		colour.AmbientOcclusionMap = below?.AmbientOcclusionMap;

		return colour;
	}

	/// <summary>
	/// A whole <see cref="PaintLayer"/> over a body's paint, honouring its per-channel
	/// opacity and blend overrides and its group children — the stack's one compositing
	/// path. A group composites its children bottom to top first, applies the group's
	/// own blend, opacity and mask to that result, then its own maps (if any) over it.
	/// Returns the backdrop itself when the layer (and its children) have no surface
	/// content. The flat per-channel <see cref="CompositeSet"/> is unchanged for the
	/// feature tree, which carries a single blend and opacity per layer.
	/// </summary>
	public static PaintCanvas CompositeLayer( PaintCanvas below, PaintLayer layer )
	{
		if ( layer is null || !layer.Enabled )
			return below;

		var result = below;

		if ( layer.Children is not null )
		{
			PaintCanvas children = null;
			foreach ( var child in layer.Children )
				children = CompositeLayer( children, child );

			if ( children is not null )
				result = CompositeMaps( result, ToMaps( children ), layer );
		}

		if ( layer.Maps is null || !layer.Maps.HasSurface )
			return result;

		return CompositeMaps( result, layer.Maps, layer );
	}

	/// <summary>A layer's maps over a backdrop, with that layer's per-channel opacity
	/// and blend — the one compositing path for a layer's own maps and for a group's
	/// children result (wrapped by <see cref="ToMaps"/>).</summary>
	static PaintCanvas CompositeMaps( PaintCanvas below, PaintLayerMaps maps, PaintLayer layer )
	{
		var colour = maps.Color is not null
			? Composite( below, maps.Color, layer.BlendFor( PaintChannel.Color ), layer.OpacityFor( PaintChannel.Color ), layer.Mask )
			: below is null
				? new PaintCanvas( maps.Size, maps.Size )
				: Strip( below );

		colour.RoughnessMap = Composite( below?.RoughnessMap, maps.Roughness, layer.BlendFor( PaintChannel.Roughness ), layer.OpacityFor( PaintChannel.Roughness ), layer.Mask );
		colour.MetalnessMap = Composite( below?.MetalnessMap, maps.Metalness, layer.BlendFor( PaintChannel.Metalness ), layer.OpacityFor( PaintChannel.Metalness ), layer.Mask );
		colour.HeightMap = Composite( below?.HeightMap, maps.Height, layer.BlendFor( PaintChannel.Height ), layer.OpacityFor( PaintChannel.Height ), layer.Mask );
		colour.EmissiveMap = Composite( below?.EmissiveMap, maps.Emissive, layer.BlendFor( PaintChannel.Emissive ), layer.OpacityFor( PaintChannel.Emissive ), layer.Mask );
		colour.OpacityMap = Composite( below?.OpacityMap, maps.Opacity, layer.BlendFor( PaintChannel.Opacity ), layer.OpacityFor( PaintChannel.Opacity ), layer.Mask );
		colour.NormalMap = Composite( below?.NormalMap, maps.Normal, layer.BlendFor( PaintChannel.Normal ), layer.OpacityFor( PaintChannel.Normal ), layer.Mask );
		colour.AmbientOcclusionMap = below?.AmbientOcclusionMap;

		return colour;
	}

	/// <summary>A composited canvas back as layer maps, so a group's children result can be
	/// composited over what is below the group under the group's own blend, opacity and mask.
	/// The baked AO is composite-level, not a layer channel, so it rides on the colour canvas.</summary>
	static PaintLayerMaps ToMaps( PaintCanvas canvas ) => new()
	{
		Size = canvas.Width,
		Color = canvas,
		Roughness = canvas.RoughnessMap,
		Metalness = canvas.MetalnessMap,
		Height = canvas.HeightMap,
		Emissive = canvas.EmissiveMap,
		Opacity = canvas.OpacityMap,
		Normal = canvas.NormalMap,
	};

	/// <summary>A copy of a colour canvas without its companions, so the result's companions can be
	/// set without reaching back into the backdrop's.</summary>
	static PaintCanvas Strip( PaintCanvas canvas )
	{
		var copy = new PaintCanvas( canvas.Width, canvas.Height );
		Array.Copy( canvas.Rgba, copy.Rgba, canvas.Rgba.Length );
		return copy;
	}

	/// <summary>
	/// A canvas at another size, bilinear in premultiplied space (so a half-covered texel does not drag
	/// its transparent neighbour's black into the colour). Same size hands back a plain copy.
	/// </summary>
	public static PaintCanvas Resample( PaintCanvas source, int width, int height )
	{
		var result = new PaintCanvas( width, height );

		if ( source.Width == width && source.Height == height )
		{
			Array.Copy( source.Rgba, result.Rgba, source.Rgba.Length );
			return result;
		}

		var s = source.Rgba;
		var d = result.Rgba;

		for ( var y = 0; y < height; y++ )
		{
			var fy = (y + 0.5f) * source.Height / height - 0.5f;
			var y0 = Math.Clamp( (int)MathF.Floor( fy ), 0, source.Height - 1 );
			var y1 = Math.Min( y0 + 1, source.Height - 1 );
			var ty = Math.Clamp( fy - y0, 0f, 1f );

			for ( var x = 0; x < width; x++ )
			{
				var fx = (x + 0.5f) * source.Width / width - 0.5f;
				var x0 = Math.Clamp( (int)MathF.Floor( fx ), 0, source.Width - 1 );
				var x1 = Math.Min( x0 + 1, source.Width - 1 );
				var tx = Math.Clamp( fx - x0, 0f, 1f );

				float r = 0, g = 0, b = 0, a = 0;

				void Tap( int xx, int yy, float wgt )
				{
					var o = (yy * source.Width + xx) * 4;
					var al = s[o + 3] / 255f * wgt;
					r += s[o] * al;
					g += s[o + 1] * al;
					b += s[o + 2] * al;
					a += al;
				}

				Tap( x0, y0, (1 - tx) * (1 - ty) );
				Tap( x1, y0, tx * (1 - ty) );
				Tap( x0, y1, (1 - tx) * ty );
				Tap( x1, y1, tx * ty );

				var od = (y * width + x) * 4;

				if ( a > 0f )
				{
					d[od] = (byte)Math.Clamp( MathF.Round( r / a ), 0f, 255f );
					d[od + 1] = (byte)Math.Clamp( MathF.Round( g / a ), 0f, 255f );
					d[od + 2] = (byte)Math.Clamp( MathF.Round( b / a ), 0f, 255f );
					d[od + 3] = ToByte( a );
				}
			}
		}

		return result;
	}

	/// <summary>A float mask at another size, bilinear. Null stays null.</summary>
	public static float[] Resample( float[] mask, int sw, int sh, int width, int height )
	{
		if ( mask is null || (sw == width && sh == height) )
			return mask;

		var result = new float[width * height];

		for ( var y = 0; y < height; y++ )
		{
			var fy = (y + 0.5f) * sh / height - 0.5f;
			var y0 = Math.Clamp( (int)MathF.Floor( fy ), 0, sh - 1 );
			var y1 = Math.Min( y0 + 1, sh - 1 );
			var ty = Math.Clamp( fy - y0, 0f, 1f );

			for ( var x = 0; x < width; x++ )
			{
				var fx = (x + 0.5f) * sw / width - 0.5f;
				var x0 = Math.Clamp( (int)MathF.Floor( fx ), 0, sw - 1 );
				var x1 = Math.Min( x0 + 1, sw - 1 );
				var tx = Math.Clamp( fx - x0, 0f, 1f );

				result[y * width + x] =
					mask[y0 * sw + x0] * (1 - tx) * (1 - ty) + mask[y0 * sw + x1] * tx * (1 - ty)
					+ mask[y1 * sw + x0] * (1 - tx) * ty + mask[y1 * sw + x1] * tx * ty;
			}
		}

		return result;
	}

	/// <summary>
	/// Replay a layer's stroke log into one canvas per channel. Strokes on different channels never
	/// meet, so each channel replays only its own strokes, in log order, through the same
	/// <see cref="PaintReplay"/> the single-colour path always used. A channel nobody painted stays null.
	/// </summary>
	public static PaintLayerMaps Replay( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution )
	{
		var maps = new PaintLayerMaps { Size = resolution };

		if ( strokes is not { Count: > 0 } )
			return maps;

		var byChannel = new Dictionary<PaintChannel, List<PaintStroke>>();

		foreach ( var stroke in strokes )
		{
			if ( !byChannel.TryGetValue( stroke.Channel, out var list ) )
				byChannel[stroke.Channel] = list = new List<PaintStroke>();

			list.Add( stroke );
		}

		var bvh = MeshBVH.Build( mesh );

		foreach ( var (channel, list) in byChannel )
			maps.Set( channel, PaintReplay.Replay( mesh, list, resolution, bvh ) );

		return maps;
	}

	/// <summary>
	/// The tri-planar replay: <see cref="Replay"/> with each channel's strokes modulated by a
	/// world-space pattern (see <see cref="TriPlanar"/>). Channel grouping is identical, so a
	/// patterned colour stroke and a plain height stroke on one layer never meet.
	/// </summary>
	public static PaintLayerMaps ReplayTriPlanar( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution,
		TriPlanarPattern pattern, float scale, float sharpness )
	{
		var maps = new PaintLayerMaps { Size = resolution };

		if ( strokes is not { Count: > 0 } )
			return maps;

		var byChannel = new Dictionary<PaintChannel, List<PaintStroke>>();

		foreach ( var stroke in strokes )
		{
			if ( !byChannel.TryGetValue( stroke.Channel, out var list ) )
				byChannel[stroke.Channel] = list = new List<PaintStroke>();

			list.Add( stroke );
		}

		var bvh = MeshBVH.Build( mesh );

		foreach ( var (channel, list) in byChannel )
			maps.Set( channel, PaintReplay.ReplayTriPlanar( mesh, list, resolution, pattern, scale, sharpness, bvh ) );

		return maps;
	}

	/// <summary>A canvas fully covered with one straight colour — a fill layer's content for one channel.</summary>
	public static PaintCanvas Solid( int width, int height, byte r, byte g, byte b )
	{
		var canvas = new PaintCanvas( width, height );
		var d = canvas.Rgba;

		for ( var i = 0; i < d.Length; i += 4 )
		{
			d[i] = r;
			d[i + 1] = g;
			d[i + 2] = b;
			d[i + 3] = 255;
		}

		return canvas;
	}

	/// <summary>
	/// The layer's final mask: the generator's mask (or white when there is none), with the painted
	/// mask laid over it — a painted texel pulls the mask toward its own grey by its coverage, so black
	/// paint hides and white paint shows, and erasing the mask paint lets the generator back through.
	/// Inverted last, when asked. Null means "show everywhere" (no generator, no paint, no invert).
	/// </summary>
	public static float[] FinalMask( float[] generator, PaintCanvas painted, bool invert, int width, int height )
	{
		if ( generator is null && painted is null && !invert )
			return null;

		var n = width * height;
		var mask = new float[n];

		var p = painted is not null && (painted.Width != width || painted.Height != height)
			? Resample( painted, width, height )
			: painted;

		for ( var i = 0; i < n; i++ )
		{
			var m = generator is null ? 1f : generator[i];

			if ( p is not null )
			{
				var a = p.Rgba[i * 4 + 3] / 255f;
				m = m * (1f - a) + p.Rgba[i * 4] / 255f * a;
			}

			mask[i] = invert ? 1f - m : m;
		}

		return mask;
	}

	/// <summary>
	/// Shape a raw generator value into a mask: 0 below <paramref name="level"/>, 1 above it, with a
	/// smooth ramp <paramref name="softness"/> wide centred on the level. Softness 0 is a hard edge.
	/// Done in place and returned.
	/// </summary>
	public static float[] Shape( float[] values, float level, float softness )
	{
		if ( values is null )
			return null;

		level = Math.Clamp( level, 0f, 1f );
		var half = Math.Clamp( softness, 0f, 1f ) * 0.5f;

		for ( var i = 0; i < values.Length; i++ )
		{
			var v = values[i];

			if ( half < 1e-4f )
			{
				values[i] = v >= level ? 1f : 0f;
				continue;
			}

			var t = Math.Clamp( (v - (level - half)) / (2f * half), 0f, 1f );
			values[i] = t * t * (3f - 2f * t);
		}

		return values;
	}

	/// <summary>
	/// One channel of the body's paint as an opaque buffer over that channel's base value — the image
	/// that exports for it. Colour bakes over <paramref name="colourBase"/>; a scalar channel over its
	/// own base (<see cref="PaintChannels.Base"/>). Null when the channel was never painted.
	/// </summary>
	public static byte[] Opaque( PaintCanvas paint, PaintChannel channel )
	{
		var canvas = paint?.Channel( channel );

		if ( canvas is null )
			return null;

		var b = channel == PaintChannel.Color
			? PaintMaterial.DefaultBaseR
			: ToByte( PaintChannels.Base( channel ) );

		var opaque = new byte[canvas.Width * canvas.Height * 4];

		if ( channel == PaintChannel.Color )
			canvas.BakeOpaque( opaque, PaintMaterial.DefaultBaseR, PaintMaterial.DefaultBaseG, PaintMaterial.DefaultBaseB );
		else if ( channel == PaintChannel.Normal )
			canvas.BakeOpaque( opaque, PaintChannels.FlatNormalR, PaintChannels.FlatNormalG, PaintChannels.FlatNormalB );
		else
			canvas.BakeOpaque( opaque, b, b, b );

		return opaque;
	}
}
/// <summary>
/// One entry in a <see cref="PaintLayerStack"/>: a layer's own content, how it combines, how
/// much of it shows, and the mask that confines it.
///
/// This is the layer a <see cref="PaintFeature"/> already is, as data: <see cref="PaintFeature"/>
/// composites one <see cref="PaintLayerMaps"/> over the paint below it with a blend mode, an
/// opacity and a final mask. A stack is just that operation repeated, in order, so paint built
/// outside the feature tree — a scripted stack, a generated stack, a test — composites exactly
/// like paint built inside it. <see cref="Maps"/> is the layer's own content (fill and strokes),
/// NOT the composite below it; <see cref="Mask"/> is the final mask (generator, mask paint,
/// invert already applied), or null for "show everywhere".
/// </summary>
public sealed class PaintLayer
{
	public string Name = "";
	public PaintLayerMaps Maps;
	public PaintBlendMode Blend = PaintBlendMode.Normal;
	public float Opacity = 1f;
	public float[] Mask;
	public bool Enabled = true;

	public PaintLayer( string name, PaintLayerMaps maps, PaintBlendMode blend = PaintBlendMode.Normal,
		float opacity = 1f, float[] mask = null )
	{
		Name = name; Maps = maps; Blend = blend; Opacity = opacity; Mask = mask;
	}

	/// <summary>Per-channel opacity, as a multiplier on the layer's master <see cref="Opacity"/>
	/// (default 1): a layer at 0.5 with a colour-channel opacity of 0.5 paints colour at 0.25.
	/// Null or absent means the master opacity rules that channel.</summary>
	public Dictionary<PaintChannel, float> ChannelOpacity;

	/// <summary>Per-channel blend mode, replacing the layer's <see cref="Blend"/> for that
	/// channel only. Null or absent means the layer blend rules that channel.</summary>
	public Dictionary<PaintChannel, PaintBlendMode> ChannelBlend;

	/// <summary>Child layers, for a group: null is a plain layer. A group composites its
	/// children bottom to top first, then its own maps (if any) over the result, all under
	/// the group's blend, opacity and mask.</summary>
	public List<PaintLayer> Children;

	/// <summary>True when this layer is a group (has children), whether or not it also
	/// carries its own maps.</summary>
	public bool IsGroup => Children is not null;

	/// <summary>The opacity this channel paints at: the master opacity times the
	/// channel's multiplier, clamped to 0..1.</summary>
	public float OpacityFor( PaintChannel channel ) =>
		ChannelOpacity is not null && ChannelOpacity.TryGetValue( channel, out var o )
			? Math.Clamp( Opacity * o, 0f, 1f ) : Opacity;

	/// <summary>The blend mode this channel paints with: the channel's override, or
	/// the layer's blend when there is none.</summary>
	public PaintBlendMode BlendFor( PaintChannel channel ) =>
		ChannelBlend is not null && ChannelBlend.TryGetValue( channel, out var m ) ? m : Blend;

	/// <summary>Set one channel's opacity multiplier (0..1, clamped).</summary>
	public void SetChannelOpacity( PaintChannel channel, float opacity )
	{
		ChannelOpacity ??= new Dictionary<PaintChannel, float>();
		ChannelOpacity[channel] = Math.Clamp( opacity, 0f, 1f );
	}

	/// <summary>Set one channel's blend mode.</summary>
	public void SetChannelBlend( PaintChannel channel, PaintBlendMode blend )
	{
		ChannelBlend ??= new Dictionary<PaintChannel, PaintBlendMode>();
		ChannelBlend[channel] = blend;
	}

	/// <summary>A group layer: children composite bottom to top under the group's own
	/// blend, opacity and mask.</summary>
	public static PaintLayer Group( string name, List<PaintLayer> children,
		PaintBlendMode blend = PaintBlendMode.Normal, float opacity = 1f, float[] mask = null ) =>
		new( name, null, blend, opacity, mask ) { Children = children };

	/// <summary>A solid-colour layer: every texel covered with one colour, for stacking tests and
	/// for generated fills. Scalar channels can be added afterwards with <see cref="SetChannel"/>.</summary>
	public static PaintLayer Solid( string name, int size, byte r, byte g, byte b, float[] mask = null,
		PaintBlendMode blend = PaintBlendMode.Normal, float opacity = 1f ) =>
		new( name, new PaintLayerMaps { Size = size, Color = PaintLayers.Solid( size, size, r, g, b ) }, blend, opacity, mask );

	/// <summary>Set one channel's content on this layer, creating its maps if needed.</summary>
	public void SetChannel( PaintChannel channel, PaintCanvas canvas )
	{
		Maps ??= new PaintLayerMaps();
		Maps.Set( channel, canvas );
		if ( canvas is not null ) Maps.Size = canvas.Width;
	}
}

/// <summary>
/// An ordered stack of paint layers, bottom to top: the first layer added is the bottom, each
/// later layer composites over everything below it, exactly as PaintFeatures stack in tree order.
///
/// <c>Composite(below)</c> walks the enabled layers in order through
/// <see cref="PaintLayers.CompositeLayer"/>, so a stack and a feature tree with the same layers,
/// blends, opacities and masks produce the same canvas. Disabled layers and layers with no
/// surface content are skipped (CompositeLayer already hands a content-free layer's backdrop back).
/// Groups composite their children first (see <see cref="PaintLayer.Children"/>); each channel
/// can carry its own opacity and blend (see <see cref="PaintLayer.OpacityFor"/>).
/// The stack never mutates its layers' maps: CompositeLayer returns a new canvas per layer.
/// </summary>
public sealed class PaintLayerStack
{
	public List<PaintLayer> Layers = new();

	public int Count => Layers.Count;

	public PaintLayer this[int index] => Layers[index];

	/// <summary>Structure revision: bumped by every Add/Insert/RemoveAt and every Move that
	/// actually reorders, so a cached composite can tell a reorder happened without
	/// hashing the stack. Layer content edits (a stroke, a per-channel override) do
	/// not move it — those are the layer's own revision.</summary>
	public int Revision { get; private set; }

	public void Add( PaintLayer layer ) { Layers.Add( layer ); Revision++; }

	public void Insert( int index, PaintLayer layer ) { Layers.Insert( Math.Clamp( index, 0, Layers.Count ), layer ); Revision++; }

	public void RemoveAt( int index ) { Layers.RemoveAt( index ); Revision++; }

	/// <summary>Move the layer at <paramref name="from"/> to <paramref name="to"/> (both clamped),
	/// so reordering a stack is one call, not a remove-and-reinsert that can drop the layer.</summary>
	public void Move( int from, int to )
	{
		if ( Layers.Count == 0 ) return;
		from = Math.Clamp( from, 0, Layers.Count - 1 );
		to = Math.Clamp( to, 0, Layers.Count - 1 );
		if ( from == to ) return;
		var layer = Layers[from];
		Layers.RemoveAt( from );
		Layers.Insert( to, layer );
		Revision++;
	}

	/// <summary>Composite the whole stack over <paramref name="below"/> (null for a bare surface),
	/// bottom to top. Returns <paramref name="below"/> itself when nothing enabled has content.</summary>
	public PaintCanvas Composite( PaintCanvas below )
	{
		var result = below;
		foreach ( var layer in Layers )
			result = PaintLayers.CompositeLayer( result, layer );
		return result;
	}

	/// <summary>Composite the whole stack over a bare surface.</summary>
	public PaintCanvas Composite() => Composite( null );
}

/// <summary>
/// A layer's colour adjustment: hue / saturation / brightness, then levels (input black and white
/// points with a midtone gamma). Applied to the layer's own colour before it meets the layers
/// below, so it is non-destructive: the strokes stay as painted and the adjustment can be undone
/// or changed at any time. Alpha (coverage) is never touched.
/// </summary>
public readonly record struct PaintAdjust( float Hue = 0f, float Saturation = 1f, float Brightness = 1f,
	float Black = 0f, float White = 1f, float Gamma = 1f )
{
	public static readonly PaintAdjust Identity = new( 0f );

	public bool IsIdentity => MathF.Abs( Hue ) < 1e-4f && MathF.Abs( Saturation - 1f ) < 1e-4f && MathF.Abs( Brightness - 1f ) < 1e-4f
		&& Black <= 1e-4f && White >= 1f - 1e-4f && MathF.Abs( Gamma - 1f ) < 1e-4f;

	/// <summary>One straight sRGB texel through the adjustment.</summary>
	public (byte R, byte G, byte B) Apply( byte r, byte g, byte b )
	{
		if ( IsIdentity )
			return (r, g, b);

		float rf = r / 255f, gf = g / 255f, bf = b / 255f;
		var max = MathF.Max( rf, MathF.Max( gf, bf ) );
		var min = MathF.Min( rf, MathF.Min( gf, bf ) );
		var v = max;
		var d = max - min;
		var s = max <= 0f ? 0f : d / max;
		var h = 0f;

		if ( d > 0f )
		{
			if ( max == rf ) h = (gf - bf) / d;
			else if ( max == gf ) h = 2f + (bf - rf) / d;
			else h = 4f + (rf - gf) / d;
			h *= 60f;
		}

		h = ((h + Hue) % 360f + 360f) % 360f;
		s = Math.Clamp( s * Saturation, 0f, 1f );
		v = Math.Clamp( v * Brightness, 0f, 1f );

		// HSV back to RGB
		var c = v * s;
		var x = c * (1f - MathF.Abs( (h / 60f) % 2f - 1f ));
		var m = v - c;
		(rf, gf, bf) = (int)(h / 60f) switch
		{
			0 => (c, x, 0f),
			1 => (x, c, 0f),
			2 => (0f, c, x),
			3 => (0f, x, c),
			4 => (x, 0f, c),
			_ => (c, 0f, x),
		};

		return (Level( rf + m ), Level( gf + m ), Level( bf + m ));
	}

	byte Level( float v )
	{
		var span = MathF.Max( 1e-4f, White - Black );
		var t = Math.Clamp( (v - Black) / span, 0f, 1f );
		t = MathF.Pow( t, 1f / MathF.Max( 0.01f, Gamma ) );
		return (byte)Math.Clamp( MathF.Round( t * 255f ), 0f, 255f );
	}

	/// <summary>A new canvas with every texel's colour adjusted (alpha copied). Identity or null hands the input back.</summary>
	public PaintCanvas Apply( PaintCanvas canvas )
	{
		if ( canvas is null || IsIdentity )
			return canvas;

		var result = new PaintCanvas( canvas.Width, canvas.Height );
		var s = canvas.Rgba;
		var d = result.Rgba;

		for ( var i = 0; i < s.Length; i += 4 )
		{
			d[i + 3] = s[i + 3];
			if ( s[i + 3] == 0 ) continue;
			(d[i], d[i + 1], d[i + 2]) = Apply( s[i], s[i + 1], s[i + 2] );
		}

		return result;
	}

	/// <summary>The layer's maps with only the colour adjusted; the scalar channels and mask are shared.</summary>
	public PaintLayerMaps Apply( PaintLayerMaps maps )
	{
		if ( maps?.Color is null || IsIdentity )
			return maps;

		return new PaintLayerMaps
		{
			Color = Apply( maps.Color ), Roughness = maps.Roughness, Metalness = maps.Metalness, Height = maps.Height,
			Emissive = maps.Emissive, Opacity = maps.Opacity, Normal = maps.Normal,
			MaskPaint = maps.MaskPaint, Size = maps.Size,
		};
	}
}
