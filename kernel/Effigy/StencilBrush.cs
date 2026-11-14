using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Stencil and clone brushes, at texel level: the 2D half of texture painting.
///
/// A stencil is a mask canvas the same size as the target: a texel is painted only where the
/// mask's alpha is above zero, and the mask's alpha scales the stroke's weight there — a soft
/// stencil edge feathers the stroke. A clone brush copies texels from a source canvas at a
/// fixed offset, so a decal, a logo, or a patch of skin can be stamped across the texture.
///
/// Both walk a <see cref="TexelStroke"/> exactly the way <see cref="TexelReplay"/> does — dab
/// at every path point, fill the gaps between consecutive points at Spacing intervals, a
/// falloff-weighted disc at each dab — so a stencil/clone stroke lands on the same texels a
/// plain stroke would. The one deliberate difference is the per-texel gate (mask) or colour
/// (clone source) applied inside the disc.</summary>
public static class StencilBrush
{
	/// <summary>Apply <paramref name="stroke"/> to <paramref name="target"/>, gated by
	/// <paramref name="mask"/>: a texel takes paint only where the mask's alpha is above
	/// zero, at weight × mask coverage. The mask must be the same size as the target; a
	/// mismatched mask is refused instead of silently resampled. Returns texels painted.</summary>
	public static int Apply( PaintCanvas target, TexelStroke stroke, PaintCanvas mask )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		if ( stroke is null )
			throw new ArgumentNullException( nameof( stroke ) );
		if ( mask is null )
			throw new ArgumentNullException( nameof( mask ) );
		if ( mask.Width != target.Width || mask.Height != target.Height )
			throw new ArgumentException( "A stencil mask must be the same size as the canvas it masks." );

		var (r, g, b) = ColourOf( stroke );
		var painted = 0;
		foreach ( var (x, y, w) in Dabs( stroke, target.Width, target.Height ) )
		{
			var coverage = mask.Rgba[(y * mask.Width + x) * 4 + 3] / 255f;
			if ( coverage <= 0f )
				continue;
			var weight = w * coverage;
			if ( stroke.Erase )
				target.Erase( x, y, weight );
			else
				target.Blend( x, y, r, g, b, weight );
			painted++;
		}
		return painted;
	}

	/// <summary>Clone <paramref name="source"/> onto <paramref name="target"/> along
	/// <paramref name="stroke"/>: each dab texel takes the source texel at
	/// (x + offsetX, y + offsetY), bilinearly sampled. The source is snapshotted first, so
	/// cloning a canvas onto itself reads the pre-stroke texels, not the ones just written.
	/// An erase stroke erases at the dab (the source is irrelevant there). The offset is in
	/// texels; a source sample outside the source canvas clamps to its edge. Returns texels painted.</summary>
	public static int Clone( PaintCanvas target, PaintCanvas source, TexelStroke stroke, int offsetX, int offsetY )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		if ( source is null )
			throw new ArgumentNullException( nameof( source ) );
		if ( stroke is null )
			throw new ArgumentNullException( nameof( stroke ) );

		var snapshot = source.Clone();
		var painted = 0;
		foreach ( var (x, y, w) in Dabs( stroke, target.Width, target.Height ) )
		{
			if ( stroke.Erase )
			{
				target.Erase( x, y, w );
				painted++;
				continue;
			}
			var (r, g, b, a) = ProjectionPaint.SampleBilinear( snapshot, (x + offsetX + 0.5f) / snapshot.Width, (y + offsetY + 0.5f) / snapshot.Height );
			// The sampled alpha scales the weight: a semi-transparent source texel clones
			// semi-transparent. Blend itself carries no per-texel alpha, so the weight is
			// where the sampled coverage has to go.
			target.Blend( x, y, r, g, b, w * (a / 255f) );
			painted++;
		}
		return painted;
	}

	/// <summary>Coverage of the stencil mask at a texel, 0..1. A painted mask texel (alpha 255)
	/// lets the stroke through at full weight; an empty one blocks it entirely.</summary>
	public static float CoverageAt( PaintCanvas mask, int x, int y )
	{
		if ( mask is null )
			throw new ArgumentNullException( nameof( mask ) );
		if ( x < 0 || y < 0 || x >= mask.Width || y >= mask.Height )
			return 0f;
		return mask.Rgba[(y * mask.Width + x) * 4 + 3] / 255f;
	}

	static (byte R, byte G, byte B) ColourOf( TexelStroke stroke )
	{
		// Same rule as the texel replay: a scalar channel reads R as its value and writes it
		// as grey; colour reads the full RGB.
		var r = ToByte( stroke.R );
		return stroke.Channel == PaintChannel.Color ? (r, ToByte( stroke.G ), ToByte( stroke.B )) : (r, r, r);
	}

	static byte ToByte( float v ) => (byte)Math.Clamp( MathF.Round( v * 255f ), 0f, 255f );

	/// <summary>Every (texel, weight) a stroke's dabs touch, in path order. This is the dab
	/// walk of <see cref="TexelReplay.Apply"/> — same path stepping, same falloff disc — so a
	/// stencil or clone stroke and a plain stroke agree on which texels they touch.</summary>
	static IEnumerable<(int X, int Y, float W)> Dabs( TexelStroke stroke, int width, int height )
	{
		if ( stroke.Path.Count == 0 )
			yield break;
		var strength = Math.Clamp( stroke.Strength, 0f, 1f );
		if ( strength <= 0f || stroke.Radius <= 0f )
			yield break;
		for ( var i = 0; i < stroke.Path.Count; i++ )
		{
			var p = stroke.Path[i];
			foreach ( var dab in Disc( p.X, p.Y, stroke, strength, width, height ) )
				yield return dab;
			if ( i + 1 >= stroke.Path.Count )
				break;
			var q = stroke.Path[i + 1];
			var dx = q.X - p.X;
			var dy = q.Y - p.Y;
			var dist = MathF.Sqrt( dx * dx + dy * dy );
			var step = Math.Max( 1f, stroke.Radius * Math.Max( 0.05f, stroke.Spacing ) );
			var steps = Math.Max( 1, (int)MathF.Ceiling( dist / step ) );
			for ( var s = 1; s < steps; s++ )
			{
				var t = (float)s / steps;
				foreach ( var dab in Disc( p.X + dx * t, p.Y + dy * t, stroke, strength, width, height ) )
					yield return dab;
			}
		}
	}

	static IEnumerable<(int X, int Y, float W)> Disc( float x, float y, TexelStroke stroke, float strength, int width, int height )
	{
		var rad = (int)MathF.Ceiling( stroke.Radius );
		var cx = (int)MathF.Round( x );
		var cy = (int)MathF.Round( y );
		for ( var oy = -rad; oy <= rad; oy++ )
		for ( var ox = -rad; ox <= rad; ox++ )
		{
			var px = cx + ox;
			var py = cy + oy;
			if ( px < 0 || py < 0 || px >= width || py >= height )
				continue;
			var d = MathF.Sqrt( ox * ox + oy * oy );
			if ( d > stroke.Radius )
				continue;
			var w = Brush.Falloff( d / stroke.Radius, stroke.Falloff ) * strength;
			if ( w <= 0f )
				continue;
			yield return (px, py, w);
		}
	}
}
