using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Which texels a magic-wand click reaches.
/// </summary>
public enum LineworkWandMode
{
	/// <summary>
	/// Only texels reachable from the seed through matching texels, 4-connected.
	/// A matching region behind a wall of non-matching texels is not selected.
	/// </summary>
	Contiguous,

	/// <summary>Every matching texel on the canvas, wherever it is.</summary>
	Global,
}

/// <summary>
/// A raster selection on a Linework canvas: one bit per texel, row-major, row 0
/// at the top, matching <see cref="PaintCanvas.Rgba"/> and
/// <see cref="BrushAlpha.Values"/> layout.
///
/// Selections are values, not views: <see cref="Expand"/>, <see cref="Shrink"/>
/// and <see cref="Feather"/> return new selections and never mutate this one,
/// so a selection a fill tool is consuming cannot change under it.
/// <see cref="Mask"/> itself is a plain array like <see cref="PaintCanvas.Rgba"/>
/// — the contents are mutable, the reference is not swapped out from under a reader.
/// </summary>
public sealed class LineworkSelection
{
	public readonly int Width;
	public readonly int Height;

	/// <summary>Row-major selection bits; true means selected. Row 0 is the top.</summary>
	public readonly bool[] Mask;

	/// <summary>Number of selected texels.</summary>
	public int Count { get; private set; }

	/// <summary>Bounds of the selection; all -1 when nothing is selected.</summary>
	public int MinX { get; private set; } = -1;
	public int MinY { get; private set; } = -1;
	public int MaxX { get; private set; } = -1;
	public int MaxY { get; private set; } = -1;

	public LineworkSelection( int width, int height, bool[] mask )
	{
		if ( width <= 0 || height <= 0 ) throw new ArgumentException( "selection size must be positive" );
		if ( mask is null ) throw new ArgumentNullException( nameof( mask ) );
		if ( mask.Length != width * height ) throw new ArgumentException( "mask length must equal width * height" );
		Width = width;
		Height = height;
		Mask = (bool[])mask.Clone();
		Recount();
	}

	/// <summary>True when the texel is selected. Out-of-bounds texels are never selected.</summary>
	public bool Contains( int x, int y )
	{
		if ( x < 0 || y < 0 || x >= Width || y >= Height ) return false;
		return Mask[y * Width + x];
	}

	/// <summary>
	/// The selection as a <see cref="BrushAlpha"/> the paint/fill tools consume:
	/// 1 where selected, 0 elsewhere, same size and layout.
	/// </summary>
	public BrushAlpha ToBrushAlpha()
	{
		var values = new float[Width * Height];
		for ( var i = 0; i < values.Length; i++ ) values[i] = Mask[i] ? 1f : 0f;
		return new BrushAlpha { Values = values, Width = Width, Height = Height };
	}

	/// <summary>A copy of the selection bits, for mask booleans and round-tripping.</summary>
	public bool[] ToMask() => (bool[])Mask.Clone();

	/// <summary>A selection from mask bits of the given size. The mask is copied.</summary>
	public static LineworkSelection FromMask( bool[] mask, int width, int height ) =>
		new( width, height, mask );

	/// <summary>
	/// A selection from a <see cref="BrushAlpha"/>: texels at or above
	/// <paramref name="threshold"/> are selected. The inverse of
	/// <see cref="ToBrushAlpha"/> at threshold 0.5.
	/// </summary>
	public static LineworkSelection FromBrushAlpha( BrushAlpha alpha, float threshold = 0.5f )
	{
		if ( alpha is null ) throw new ArgumentNullException( nameof( alpha ) );
		if ( alpha.Values is null ) throw new ArgumentException( "brush alpha has no values" );
		if ( alpha.Width <= 0 || alpha.Height <= 0 || alpha.Values.Length != alpha.Width * alpha.Height )
			throw new ArgumentException( "brush alpha size does not match its values" );
		var mask = new bool[alpha.Values.Length];
		for ( var i = 0; i < mask.Length; i++ ) mask[i] = alpha.Values[i] >= threshold;
		return new LineworkSelection( alpha.Width, alpha.Height, mask );
	}

	/// <summary>
	/// Grow the selection by <paramref name="pixels"/> texels in every direction
	/// (4-connected dilation, one texel per step). Zero or negative grows nothing
	/// and returns an equal copy.
	/// </summary>
	public LineworkSelection Expand( int pixels )
	{
		if ( pixels <= 0 ) return new LineworkSelection( Width, Height, Mask );
		var steps = Math.Min( pixels, Math.Max( Width, Height ) );
		var cur = (bool[])Mask.Clone();
		for ( var s = 0; s < steps; s++ )
		{
			var next = (bool[])cur.Clone();
			for ( var y = 0; y < Height; y++ )
			{
				for ( var x = 0; x < Width; x++ )
				{
					if ( cur[y * Width + x] ) continue;
					if ( (x > 0 && cur[y * Width + x - 1])
						|| (x + 1 < Width && cur[y * Width + x + 1])
						|| (y > 0 && cur[(y - 1) * Width + x])
						|| (y + 1 < Height && cur[(y + 1) * Width + x]) )
						next[y * Width + x] = true;
				}
			}
			cur = next;
		}
		return new LineworkSelection( Width, Height, cur );
	}

	/// <summary>
	/// Shrink the selection by <paramref name="pixels"/> texels in every direction
	/// (4-connected erosion, one texel per step). The canvas edge counts as
	/// unselected, so a selection touching the border erodes inward from it.
	/// Zero or negative shrinks nothing and returns an equal copy.
	/// </summary>
	public LineworkSelection Shrink( int pixels )
	{
		if ( pixels <= 0 ) return new LineworkSelection( Width, Height, Mask );
		var steps = Math.Min( pixels, Math.Max( Width, Height ) );
		var cur = (bool[])Mask.Clone();
		for ( var s = 0; s < steps; s++ )
		{
			var next = new bool[cur.Length];
			for ( var y = 0; y < Height; y++ )
			{
				for ( var x = 0; x < Width; x++ )
				{
					if ( !cur[y * Width + x] ) continue;
					if ( x > 0 && x + 1 < Width && y > 0 && y + 1 < Height
						&& cur[y * Width + x - 1] && cur[y * Width + x + 1]
						&& cur[(y - 1) * Width + x] && cur[(y + 1) * Width + x] )
						next[y * Width + x] = true;
				}
			}
			cur = next;
		}
		return new LineworkSelection( Width, Height, cur );
	}

	/// <summary>
	/// The selection as a soft-edged <see cref="BrushAlpha"/>: 1 deep inside,
	/// fading to 0 over roughly <paramref name="radius"/> texels across the edge,
	/// 0 far outside. Implemented as that many passes of a separable 3-tap box
	/// blur, so the fade is symmetric — it softens outward as well as inward —
	/// and deterministic. Radius 0 or below returns the hard 0/1 mask.
	/// </summary>
	public BrushAlpha Feather( float radius )
	{
		var values = new float[Width * Height];
		for ( var i = 0; i < values.Length; i++ ) values[i] = Mask[i] ? 1f : 0f;
		if ( radius <= 0f ) return new BrushAlpha { Values = values, Width = Width, Height = Height };
		if ( !float.IsFinite( radius ) ) throw new ArgumentException( "feather radius must be finite" );
		var passes = (int)Math.Min( 64, Math.Ceiling( radius ) );
		var tmp = new float[values.Length];
		for ( var p = 0; p < passes; p++ )
		{
			for ( var y = 0; y < Height; y++ )
			{
				for ( var x = 0; x < Width; x++ )
				{
					var xm = x > 0 ? x - 1 : x;
					var xp = x + 1 < Width ? x + 1 : x;
					tmp[y * Width + x] = (values[y * Width + xm] + values[y * Width + x] + values[y * Width + xp]) / 3f;
				}
			}
			for ( var y = 0; y < Height; y++ )
			{
				var ym = y > 0 ? y - 1 : y;
				var yp = y + 1 < Height ? y + 1 : y;
				for ( var x = 0; x < Width; x++ )
					values[y * Width + x] = (tmp[ym * Width + x] + tmp[y * Width + x] + tmp[yp * Width + x]) / 3f;
			}
		}
		return new BrushAlpha { Values = values, Width = Width, Height = Height };
	}

	void Recount()
	{
		Count = 0;
		MinX = MinY = MaxX = MaxY = -1;
		for ( var y = 0; y < Height; y++ )
		{
			for ( var x = 0; x < Width; x++ )
			{
				if ( !Mask[y * Width + x] ) continue;
				Count++;
				if ( MinX < 0 ) { MinX = MaxX = x; MinY = MaxY = y; }
				else
				{
					if ( x < MinX ) MinX = x;
					if ( x > MaxX ) MaxX = x;
					if ( y < MinY ) MinY = y;
					if ( y > MaxY ) MaxY = y;
				}
			}
		}
	}
}

/// <summary>
/// A magic-wand selection tool on the Linework raster: flood-fill selection from
/// a seed point with colour tolerance and contiguity, over the canvas a
/// <see cref="LineworkRaster"/> pass already painted. This is a real flood fill,
/// not a rectangle relabelled — contiguous mode walks 4-connected matching texels
/// from the seed, global mode takes every matching texel on the canvas.
///
/// A texel matches when BOTH its colour and its alpha are within tolerance of the
/// seed: colour distance is the RGB Euclidean distance normalized so 0 is an exact
/// match and 1 is black-versus-white, alpha distance is the absolute alpha
/// difference over 255, and the match is the larger of the two. Tolerance 0
/// therefore means an exact RGBA match (a click on unpainted, transparent canvas
/// selects the connected transparent region); tolerance 1 selects everything.
/// Tolerance is clamped to 0..1; a non-finite tolerance is refused.
///
/// Connectivity is 4-way on purpose: two matching texels touching only at a
/// corner do NOT connect, so a wand click cannot leak diagonally through a
/// one-texel checkerboard gap. The seed texel itself always matches.
/// </summary>
public static class LineworkMagicWand
{
	/// <summary>
	/// Flood-fill selection from the seed texel's own colour. The seed must be on
	/// the canvas; a null canvas or an out-of-bounds seed is refused.
	/// </summary>
	public static LineworkSelection Select(
		PaintCanvas canvas, int seedX, int seedY, float tolerance,
		LineworkWandMode mode = LineworkWandMode.Contiguous )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( seedX < 0 || seedY < 0 || seedX >= canvas.Width || seedY >= canvas.Height )
			throw new ArgumentOutOfRangeException( nameof( seedX ), "seed is outside the canvas" );
		var i = (seedY * canvas.Width + seedX) * 4;
		return SelectCore( canvas,
			canvas.Rgba[i], canvas.Rgba[i + 1], canvas.Rgba[i + 2], canvas.Rgba[i + 3],
			tolerance, mode, seedX, seedY );
	}

	/// <summary>
	/// Selection by an explicit target colour instead of a seed texel. Global mode
	/// takes every matching texel; contiguous mode needs a seed, so use
	/// <see cref="Select"/> for it.
	/// </summary>
	public static LineworkSelection SelectColour(
		PaintCanvas canvas, byte r, byte g, byte b, byte a, float tolerance,
		LineworkWandMode mode = LineworkWandMode.Global )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( mode == LineworkWandMode.Contiguous )
			throw new ArgumentException( "a contiguous selection needs a seed point; use Select" );
		return SelectCore( canvas, r, g, b, a, tolerance, mode, -1, -1 );
	}

	static LineworkSelection SelectCore(
		PaintCanvas canvas, byte r, byte g, byte b, byte a, float tolerance,
		LineworkWandMode mode, int seedX, int seedY )
	{
		if ( !float.IsFinite( tolerance ) ) throw new ArgumentException( "tolerance must be finite" );
		tolerance = Math.Clamp( tolerance, 0f, 1f );
		var mask = new bool[canvas.Width * canvas.Height];
		if ( mode == LineworkWandMode.Global )
		{
			for ( var i = 0; i < mask.Length; i++ )
			{
				var o = i * 4;
				if ( Matches( canvas.Rgba[o], canvas.Rgba[o + 1], canvas.Rgba[o + 2], canvas.Rgba[o + 3],
					r, g, b, a, tolerance ) )
					mask[i] = true;
			}
			return new LineworkSelection( canvas.Width, canvas.Height, mask );
		}
		// Contiguous flood fill, 4-connected, iterative so a canvas-sized region
		// cannot overflow the call stack the way a recursive fill would.
		var stack = new Stack<int>();
		stack.Push( seedY * canvas.Width + seedX );
		while ( stack.Count > 0 )
		{
			var at = stack.Pop();
			if ( mask[at] ) continue;
			var o = at * 4;
			if ( !Matches( canvas.Rgba[o], canvas.Rgba[o + 1], canvas.Rgba[o + 2], canvas.Rgba[o + 3],
				r, g, b, a, tolerance ) )
				continue;
			mask[at] = true;
			var x = at % canvas.Width;
			var y = at / canvas.Width;
			if ( x > 0 ) stack.Push( at - 1 );
			if ( x + 1 < canvas.Width ) stack.Push( at + 1 );
			if ( y > 0 ) stack.Push( at - canvas.Width );
			if ( y + 1 < canvas.Height ) stack.Push( at + canvas.Width );
		}
		return new LineworkSelection( canvas.Width, canvas.Height, mask );
	}

	static bool Matches(
		byte r, byte g, byte b, byte a, byte tr, byte tg, byte tb, byte ta, float tolerance )
	{
		var dr = (r - tr) / 255f;
		var dg = (g - tg) / 255f;
		var db = (b - tb) / 255f;
		var colour = MathF.Sqrt( dr * dr + dg * dg + db * db ) / MathF.Sqrt( 3f );
		var alpha = MathF.Abs( (a - ta) / 255f );
		return Math.Max( colour, alpha ) <= tolerance;
	}
}
