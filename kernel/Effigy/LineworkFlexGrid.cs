using System;
using System.Collections.Generic;
using System.Text;

namespace Effigy;

/// <summary>
/// S&amp;box Linework Lane H: a visual layout engine for UI panels — flexbox
/// (row/column, wrap, justify/align, gap) and CSS-grid tracks (fr, px, auto,
/// repeat) — computing exact child rects from a layout tree.
///
/// This is deliberately NOT the solver in LineworkUiModel.cs
/// (LineworkLayoutSolver). That solver is kept untouched and it is limited:
/// no wrapping, SpaceBetween/SpaceAround are enum values only (its position
/// pass only handles Center/FlexEnd), and its Grid mode is equal columns
/// with a single column count. This engine adds those missing behaviours
/// under its own types, reusing only LineworkInsets from the UI model.
///
/// Rects are exact floats. Nothing here rounds, clips, or measures text:
/// an item wider than its container keeps its exact rect past the edge,
/// and a label's size is whatever the caller put in Width/Height. Clipping
/// (if a panel wants it) is the caller's job, not the layout engine's.
/// No Sandbox/Editor types: this file compiles in Effigy.Tests as plain C#.
/// </summary>
public enum LineworkFlexGridDirection
{
	Row,
	Column
}

public enum LineworkFlexGridWrap
{
	NoWrap,
	Wrap,
	WrapReverse
}

public enum LineworkFlexGridJustify
{
	FlexStart,
	Center,
	FlexEnd,
	SpaceBetween,
	SpaceAround,
	SpaceEvenly
}

/// <summary>SpaceBetween/Around/Evenly are align-CONTENT values only
/// (distributing whole lines). As an item's own align they fall back to
/// FlexStart — CSS has no per-item space distribution.</summary>
public enum LineworkFlexGridAlign
{
	FlexStart,
	Center,
	FlexEnd,
	Stretch,
	SpaceBetween,
	SpaceAround,
	SpaceEvenly
}

/// <summary>One computed child rect. X/Y are the content-box corner of the
/// item itself (margins already applied outside it); W/H are the item's
/// own size, never the margin box.</summary>
public struct LineworkFlexGridRect
{
	public float X;
	public float Y;
	public float W;
	public float H;

	public LineworkFlexGridRect( float x, float y, float w, float h )
	{
		X = x; Y = y; W = w; H = h;
	}

	public float Right => X + W;
	public float Bottom => Y + H;

	public bool Contains( float x, float y ) =>
		x >= X && x <= Right && y >= Y && y <= Bottom;

	public override string ToString() => $"({X}, {Y}, {W}, {H})";
}

public sealed class LineworkFlexGridPlacement
{
	public string Name { get; set; } = "";
	public LineworkFlexGridRect Rect { get; set; }
	/// <summary>Grid only: 0-based column/row the item landed in, or -1 for flex.</summary>
	public int GridColumn { get; set; } = -1;
	public int GridRow { get; set; } = -1;
}

// ---------------------------------------------------------------- flex ---

/// <summary>One flex item. Sizes follow the UI-model convention: 0 = auto.
/// On the main axis, FlexBasis &gt; 0 wins over Width/Height; on the cross
/// axis, 0 means "stretch fills the line" when the effective align is
/// Stretch, otherwise the item keeps whatever cross size it was given
/// (0 stays 0 — the engine does not invent a content size).</summary>
public sealed class LineworkFlexGridItem
{
	public string Name { get; set; } = "";
	public float Width { get; set; } = 0f;
	public float Height { get; set; } = 0f;
	public float FlexBasis { get; set; } = 0f;
	public float FlexGrow { get; set; } = 0f;
	public float FlexShrink { get; set; } = 1f;
	/// <summary>0 = no minimum / no maximum.</summary>
	public float MinMain { get; set; } = 0f;
	public float MaxMain { get; set; } = 0f;
	public float MinCross { get; set; } = 0f;
	public float MaxCross { get; set; } = 0f;
	public LineworkInsets Margin { get; set; } = new( 0f );
	/// <summary>Null = inherit the container's AlignItems.</summary>
	public LineworkFlexGridAlign? AlignSelf { get; set; }
}

public sealed class LineworkFlexGridContainer
{
	public LineworkFlexGridDirection Direction { get; set; } = LineworkFlexGridDirection.Row;
	public LineworkFlexGridWrap Wrap { get; set; } = LineworkFlexGridWrap.NoWrap;
	public LineworkFlexGridJustify Justify { get; set; } = LineworkFlexGridJustify.FlexStart;
	public LineworkFlexGridAlign AlignItems { get; set; } = LineworkFlexGridAlign.Stretch;
	public LineworkFlexGridAlign AlignContent { get; set; } = LineworkFlexGridAlign.Stretch;
	/// <summary>Main-axis gap between items in a line.</summary>
	public float Gap { get; set; } = 0f;
	/// <summary>Cross-axis gap between lines. Negative = use Gap.</summary>
	public float CrossGap { get; set; } = -1f;
	public float Width { get; set; } = 0f;
	public float Height { get; set; } = 0f;
	public LineworkInsets Padding { get; set; } = new( 0f );
	public List<LineworkFlexGridItem> Items { get; } = new();

	public float EffectiveCrossGap => CrossGap >= 0f ? CrossGap : Gap;
}

// ---------------------------------------------------------------- grid ---

public enum LineworkFlexGridTrackKind
{
	Fixed,
	Fraction,
	Auto
}

/// <summary>One grid track. Fixed = px, Fraction = fr share of what is left
/// after fixed/auto tracks and gaps, Auto = sized by the largest
/// single-span item placed in that track (0 if nothing sizes it).</summary>
public sealed class LineworkFlexGridTrack
{
	public LineworkFlexGridTrackKind Kind { get; set; }
	public float Value { get; set; }

	public static LineworkFlexGridTrack Px( float px ) =>
		new() { Kind = LineworkFlexGridTrackKind.Fixed, Value = Math.Max( 0f, px ) };

	public static LineworkFlexGridTrack Fr( float fr ) =>
		new() { Kind = LineworkFlexGridTrackKind.Fraction, Value = Math.Max( 0f, fr ) };

	public static LineworkFlexGridTrack Auto() =>
		new() { Kind = LineworkFlexGridTrackKind.Auto, Value = 0f };

	/// <summary>CSS repeat(): count copies of one track, or of a whole list.</summary>
	public static List<LineworkFlexGridTrack> Repeat( int count, LineworkFlexGridTrack track )
	{
		var list = new List<LineworkFlexGridTrack>();
		for ( var i = 0; i < Math.Max( 0, count ); i++ )
			list.Add( new LineworkFlexGridTrack { Kind = track.Kind, Value = track.Value } );
		return list;
	}

	public static List<LineworkFlexGridTrack> Repeat( int count, List<LineworkFlexGridTrack> tracks )
	{
		var list = new List<LineworkFlexGridTrack>();
		for ( var i = 0; i < Math.Max( 0, count ); i++ )
			foreach ( var t in tracks )
				list.Add( new LineworkFlexGridTrack { Kind = t.Kind, Value = t.Value } );
		return list;
	}

	/// <summary>Parse a CSS-ish track list, e.g. "100px 1fr repeat(2, 2fr) auto".
	/// A bare number means px. Unknown tokens throw: a bad track string
	/// must never silently lay out wrong.</summary>
	public static List<LineworkFlexGridTrack> Parse( string text )
	{
		var tracks = new List<LineworkFlexGridTrack>();
		if ( string.IsNullOrWhiteSpace( text ) ) return tracks;
		var i = 0;
		while ( i < text.Length )
		{
			while ( i < text.Length && char.IsWhiteSpace( text[i] ) ) i++;
			if ( i >= text.Length ) break;
			if ( text.Substring( i ).StartsWith( "repeat(", StringComparison.OrdinalIgnoreCase ) )
			{
				var open = text.IndexOf( '(', i );
				var close = text.IndexOf( ')', open );
				if ( close < 0 ) throw new ArgumentException( "Unclosed repeat() in grid track list." );
				var inner = text.Substring( open + 1, close - open - 1 );
				var comma = inner.IndexOf( ',' );
				if ( comma < 0 ) throw new ArgumentException( "repeat() needs a count and tracks." );
				if ( !int.TryParse( inner.Substring( 0, comma ).Trim(), out var count ) || count < 0 )
					throw new ArgumentException( "repeat() count must be a non-negative integer." );
				tracks.AddRange( Repeat( count, Parse( inner.Substring( comma + 1 ) ) ) );
				i = close + 1;
			}
			else
			{
				var start = i;
				while ( i < text.Length && !char.IsWhiteSpace( text[i] ) ) i++;
				tracks.Add( ParseOne( text.Substring( start, i - start ) ) );
			}
		}
		return tracks;
	}

	static LineworkFlexGridTrack ParseOne( string token )
	{
		if ( token.Equals( "auto", StringComparison.OrdinalIgnoreCase ) ) return Auto();
		if ( token.EndsWith( "fr", StringComparison.OrdinalIgnoreCase ) &&
			float.TryParse( token.Substring( 0, token.Length - 2 ), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fr ) )
			return Fr( fr );
		if ( token.EndsWith( "px", StringComparison.OrdinalIgnoreCase ) &&
			float.TryParse( token.Substring( 0, token.Length - 2 ), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px ) )
			return Px( px );
		if ( float.TryParse( token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bare ) )
			return Px( bare );
		throw new ArgumentException( $"Unknown grid track '{token}'." );
	}
}

/// <summary>One grid item. Column/Row are 0-based; -1 = auto-place in
/// row-major order. Width/Height 0 = fill the spanned tracks; an explicit
/// size wins and the item sits at the span's start corner.</summary>
public sealed class LineworkFlexGridGridItem
{
	public string Name { get; set; } = "";
	public float Width { get; set; } = 0f;
	public float Height { get; set; } = 0f;
	public int Column { get; set; } = -1;
	public int Row { get; set; } = -1;
	public int ColumnSpan { get; set; } = 1;
	public int RowSpan { get; set; } = 1;
	public LineworkInsets Margin { get; set; } = new( 0f );
}

public sealed class LineworkFlexGridGrid
{
	public List<LineworkFlexGridTrack> Columns { get; } = new();
	public List<LineworkFlexGridTrack> Rows { get; } = new();
	public float ColumnGap { get; set; } = 0f;
	public float RowGap { get; set; } = 0f;
	public float Width { get; set; } = 0f;
	public float Height { get; set; } = 0f;
	public LineworkInsets Padding { get; set; } = new( 0f );
	public List<LineworkFlexGridGridItem> Items { get; } = new();
}

// -------------------------------------------------------------- engine ---

public static class LineworkFlexGridLayout
{
	static float Clamp( float v, float lo, float hi ) => Math.Max( lo, Math.Min( hi, v ) );
	static float MainSize( LineworkFlexGridItem item, bool isRow ) => isRow ? item.Width : item.Height;
	static float CrossSize( LineworkFlexGridItem item, bool isRow ) => isRow ? item.Height : item.Width;
	static float MarginMain( LineworkFlexGridItem item, bool isRow ) =>
		isRow ? item.Margin.Left + item.Margin.Right : item.Margin.Top + item.Margin.Bottom;
	static float MarginCross( LineworkFlexGridItem item, bool isRow ) =>
		isRow ? item.Margin.Top + item.Margin.Bottom : item.Margin.Left + item.Margin.Right;
	static float MarginMainNear( LineworkFlexGridItem item, bool isRow ) => isRow ? item.Margin.Left : item.Margin.Top;
	static float MarginCrossNear( LineworkFlexGridItem item, bool isRow ) => isRow ? item.Margin.Top : item.Margin.Left;

	/// <summary>Flex layout. Returns one placement per item, in input order.
	/// No rounding, no clipping: overflow rects keep their exact size.</summary>
	public static List<LineworkFlexGridPlacement> LayoutFlex( LineworkFlexGridContainer c )
	{
		var result = new List<LineworkFlexGridPlacement>();
		if ( c == null ) return result;
		var isRow = c.Direction == LineworkFlexGridDirection.Row;
		var originX = c.Padding.Left;
		var originY = c.Padding.Top;
		var contentW = Math.Max( 0f, c.Width - c.Padding.Left - c.Padding.Right );
		var contentH = Math.Max( 0f, c.Height - c.Padding.Top - c.Padding.Bottom );
		var contentMain = isRow ? contentW : contentH;
		var contentCross = isRow ? contentH : contentW;

		// 1. Base sizes + line breaking.
		var lines = new List<List<LineworkFlexGridItem>>();
		var line = new List<LineworkFlexGridItem>();
		var lineMain = 0f;
		foreach ( var item in c.Items )
		{
			var baseMain = item.FlexBasis > 0f ? item.FlexBasis : MainSize( item, isRow );
			var outer = baseMain + MarginMain( item, isRow );
			if ( c.Wrap != LineworkFlexGridWrap.NoWrap && line.Count > 0 &&
				lineMain + c.Gap + outer > contentMain )
			{
				lines.Add( line );
				line = new List<LineworkFlexGridItem>();
				lineMain = 0f;
			}
			if ( line.Count > 0 ) lineMain += c.Gap;
			line.Add( item );
			lineMain += outer;
		}
		if ( line.Count > 0 || c.Items.Count == 0 ) lines.Add( line );

		// 2. Resolve main sizes per line (grow, then shrink, with min/max
		// clamping re-run until no clamped item changes — a single pass
		// would leave the leftover of a clamped item undistributed).
		var resolvedMain = new Dictionary<LineworkFlexGridItem, float>();
		var resolvedCross = new Dictionary<LineworkFlexGridItem, float>();
		var lineCross = new List<float>();
		foreach ( var ln in lines )
		{
			var bases = new Dictionary<LineworkFlexGridItem, float>();
			var totalOuter = 0f;
			foreach ( var item in ln )
			{
				var b = item.FlexBasis > 0f ? item.FlexBasis : MainSize( item, isRow );
				bases[item] = b;
				totalOuter += b + MarginMain( item, isRow );
			}
			var gaps = Math.Max( 0, ln.Count - 1 ) * c.Gap;
			var free = contentMain - totalOuter - gaps;
			var sizes = new Dictionary<LineworkFlexGridItem, float>( bases );
			if ( free > 0f )
			{
				var remaining = free;
				var open = new List<LineworkFlexGridItem>( ln );
				while ( open.Count > 0 && remaining > 0.0001f )
				{
					var totalGrow = 0f;
					foreach ( var item in open ) totalGrow += item.FlexGrow;
					if ( totalGrow <= 0f ) break;
					var progressed = false;
					var next = new List<LineworkFlexGridItem>();
					foreach ( var item in open )
					{
						if ( item.FlexGrow <= 0f ) continue;
						var extra = remaining * ( item.FlexGrow / totalGrow );
						var grown = sizes[item] + extra;
						if ( item.MaxMain > 0f && grown > item.MaxMain )
						{
							remaining -= Math.Max( 0f, item.MaxMain - sizes[item] );
							sizes[item] = item.MaxMain;
							progressed = true;
						}
						else
						{
							sizes[item] = grown;
							next.Add( item );
						}
					}
					// Anything not re-queued was clamped; loop only if a
					// clamp actually freed space, else the pass is done.
					if ( !progressed ) break;
					open = next;
					// Recompute remaining from the line total, since the
					// proportional pass above already spent it once.
					var spent = 0f;
					foreach ( var item in ln ) spent += sizes[item] + MarginMain( item, isRow );
					remaining = contentMain - spent - gaps;
				}
			}
			else if ( free < 0f )
			{
				var deficit = -free;
				var open = new List<LineworkFlexGridItem>( ln );
				while ( open.Count > 0 && deficit > 0.0001f )
				{
					var totalWeight = 0f;
					foreach ( var item in open ) totalWeight += item.FlexShrink * bases[item];
					if ( totalWeight <= 0f ) break;
					var progressed = false;
					var next = new List<LineworkFlexGridItem>();
					foreach ( var item in open )
					{
						if ( item.FlexShrink <= 0f || bases[item] <= 0f ) continue;
						var cut = deficit * ( item.FlexShrink * bases[item] / totalWeight );
						var shrunk = sizes[item] - cut;
						var floor = item.MinMain > 0f ? item.MinMain : 0f;
						if ( shrunk < floor )
						{
							deficit -= Math.Max( 0f, sizes[item] - floor );
							sizes[item] = floor;
							progressed = true;
						}
						else
						{
							sizes[item] = shrunk;
							next.Add( item );
						}
					}
					if ( !progressed ) break;
					open = next;
					var spent = 0f;
					foreach ( var item in ln ) spent += sizes[item] + MarginMain( item, isRow );
					deficit = spent + gaps - contentMain;
				}
			}
			foreach ( var item in ln )
			{
				var main = sizes[item];
				if ( item.MinMain > 0f ) main = Math.Max( main, item.MinMain );
				if ( item.MaxMain > 0f ) main = Math.Min( main, item.MaxMain );
				resolvedMain[item] = Math.Max( 0f, main );
				var cross = CrossSize( item, isRow );
				if ( item.MinCross > 0f ) cross = Math.Max( cross, item.MinCross );
				if ( item.MaxCross > 0f ) cross = Math.Min( cross, item.MaxCross );
				resolvedCross[item] = Math.Max( 0f, cross );
			}
			var lineCrossSize = 0f;
			foreach ( var item in ln )
				lineCrossSize = Math.Max( lineCrossSize, resolvedCross[item] + MarginCross( item, isRow ) );
			lineCross.Add( lineCrossSize );
		}

		// CSS single-line rule: a lone line's cross size IS the container's
		// content cross size (never just the tallest item) — otherwise
		// Center/FlexEnd align would centre inside a 50px line instead of
		// the 200px panel. Overflow still wins over the container size.
		if ( lines.Count == 1 )
			lineCross[0] = Math.Max( lineCross[0], contentCross );
		// Multi-line lines with no explicit cross size share the content
		// cross when AlignContent stretches, else they stay 0.
		for ( var li = 0; li < lines.Count; li++ )
		{
			if ( lines.Count == 1 || lineCross[li] > 0f ) continue;
			if ( c.AlignContent == LineworkFlexGridAlign.Stretch )
				lineCross[li] = Math.Max( 0f, ( contentCross - ( lines.Count - 1 ) * c.EffectiveCrossGap ) / lines.Count );
		}

		// 3. Cross positions of lines (align-content), then wrap-reverse.
		var totalCross = 0f;
		foreach ( var lc in lineCross ) totalCross += lc;
		totalCross += Math.Max( 0, lines.Count - 1 ) * c.EffectiveCrossGap;
		var crossFree = contentCross - totalCross;
		var lineCrossPos = new float[lines.Count];
		// Single line: align-content is ignored (CSS: it only distributes
		// between multiple lines). Cross positioning of a lone line's items
		// is AlignItems' job, now that the line spans the content cross.
		if ( lines.Count == 1 ) lineCrossPos[0] = 0f;
		else if ( crossFree > 0f )
		{
			switch ( c.AlignContent )
			{
				case LineworkFlexGridAlign.Center:
					for ( var li = 0; li < lines.Count; li++ ) lineCrossPos[li] = crossFree * 0.5f + SumBefore( lineCross, li ) + li * c.EffectiveCrossGap;
					break;
				case LineworkFlexGridAlign.FlexEnd:
					for ( var li = 0; li < lines.Count; li++ ) lineCrossPos[li] = crossFree + SumBefore( lineCross, li ) + li * c.EffectiveCrossGap;
					break;
				case LineworkFlexGridAlign.SpaceBetween:
					{
						var extra = crossFree / ( lines.Count - 1 );
						for ( var li = 0; li < lines.Count; li++ ) lineCrossPos[li] = SumBefore( lineCross, li ) + li * ( c.EffectiveCrossGap + extra );
						break;
					}
				case LineworkFlexGridAlign.SpaceAround:
					{
						var extra = crossFree / lines.Count;
						for ( var li = 0; li < lines.Count; li++ ) lineCrossPos[li] = extra * 0.5f + SumBefore( lineCross, li ) + li * ( c.EffectiveCrossGap + extra );
						break;
					}
				case LineworkFlexGridAlign.SpaceEvenly:
					{
						var extra = crossFree / ( lines.Count + 1 );
						for ( var li = 0; li < lines.Count; li++ ) lineCrossPos[li] = extra + SumBefore( lineCross, li ) + li * ( c.EffectiveCrossGap + extra );
						break;
					}
				case LineworkFlexGridAlign.Stretch:
					{
						// Stretch lines equally to fill the content cross.
						var each = ( contentCross - ( lines.Count - 1 ) * c.EffectiveCrossGap ) / lines.Count;
						for ( var li = 0; li < lines.Count; li++ )
						{
							lineCross[li] = Math.Max( lineCross[li], each );
							lineCrossPos[li] = li * ( each + c.EffectiveCrossGap );
						}
						break;
					}
				default:
					for ( var li = 0; li < lines.Count; li++ ) lineCrossPos[li] = SumBefore( lineCross, li ) + li * c.EffectiveCrossGap;
					break;
			}
		}
		else
		{
			for ( var li = 0; li < lines.Count; li++ ) lineCrossPos[li] = SumBefore( lineCross, li ) + li * c.EffectiveCrossGap;
		}
		if ( c.Wrap == LineworkFlexGridWrap.WrapReverse )
		{
			// First line goes to the far cross end; order within a line
			// and main-axis direction do not change.
			var reversed = new float[lines.Count];
			for ( var li = 0; li < lines.Count; li++ )
				reversed[li] = contentCross - lineCrossPos[li] - lineCross[li];
			lineCrossPos = reversed;
		}

		// 4. Main + cross position per item.
		var byItem = new Dictionary<LineworkFlexGridItem, LineworkFlexGridPlacement>();
		for ( var li = 0; li < lines.Count; li++ )
		{
			var ln = lines[li];
			var used = 0f;
			foreach ( var item in ln ) used += resolvedMain[item] + MarginMain( item, isRow );
			used += Math.Max( 0, ln.Count - 1 ) * c.Gap;
			var free = contentMain - used;
			var lead = 0f;
			var extraGap = 0f;
			if ( free > 0f )
			{
				switch ( c.Justify )
				{
					case LineworkFlexGridJustify.Center: lead = free * 0.5f; break;
					case LineworkFlexGridJustify.FlexEnd: lead = free; break;
					case LineworkFlexGridJustify.SpaceBetween: extraGap = ln.Count > 1 ? free / ( ln.Count - 1 ) : 0f; break;
					case LineworkFlexGridJustify.SpaceAround: extraGap = free / ln.Count; lead = extraGap * 0.5f; break;
					case LineworkFlexGridJustify.SpaceEvenly: extraGap = free / ( ln.Count + 1 ); lead = extraGap; break;
				}
			}
			var mainPos = lead;
			foreach ( var item in ln )
			{
				var align = item.AlignSelf ?? c.AlignItems;
				var cross = resolvedCross[item];
				var outerCross = cross + MarginCross( item, isRow );
				if ( align == LineworkFlexGridAlign.Stretch && CrossSize( item, isRow ) <= 0f )
					cross = Math.Max( 0f, lineCross[li] - MarginCross( item, isRow ) );
				var crossPos = align switch
				{
					LineworkFlexGridAlign.Center => ( lineCross[li] - outerCross ) * 0.5f,
					LineworkFlexGridAlign.FlexEnd => lineCross[li] - outerCross,
					_ => 0f
				};
				if ( align == LineworkFlexGridAlign.Stretch && CrossSize( item, isRow ) <= 0f )
					crossPos = 0f;
				var main = mainPos + MarginMainNear( item, isRow );
				var crossOff = lineCrossPos[li] + crossPos + MarginCrossNear( item, isRow );
				var rect = isRow
					? new LineworkFlexGridRect( originX + main, originY + crossOff, resolvedMain[item], cross )
					: new LineworkFlexGridRect( originX + crossOff, originY + main, cross, resolvedMain[item] );
				byItem[item] = new LineworkFlexGridPlacement { Name = item.Name, Rect = rect };
				mainPos += resolvedMain[item] + MarginMain( item, isRow ) + c.Gap + extraGap;
			}
		}
		foreach ( var item in c.Items ) result.Add( byItem[item] );
		return result;
	}

	static float SumBefore( List<float> values, int upto )
	{
		var s = 0f;
		for ( var i = 0; i < upto; i++ ) s += values[i];
		return s;
	}

	/// <summary>Grid layout. Returns one placement per item, in input order.
	/// Explicit placements land first; auto-placed items then fill the
	/// first free row-major block that fits their span. Implicit rows
	/// (past the defined rows) repeat the last row track; implicit
	/// columns repeat the last column track. Auto tracks are sized from
	/// single-span items only — a spanned item does not size its tracks
	/// (documented limit, same trade-off CSS makes messy).</summary>
	public static List<LineworkFlexGridPlacement> LayoutGrid( LineworkFlexGridGrid g )
	{
		var result = new List<LineworkFlexGridPlacement>();
		if ( g == null ) return result;
		var originX = g.Padding.Left;
		var originY = g.Padding.Top;
		var contentW = Math.Max( 0f, g.Width - g.Padding.Left - g.Padding.Right );
		var contentH = Math.Max( 0f, g.Height - g.Padding.Top - g.Padding.Bottom );

		var cols = new List<LineworkFlexGridTrack>();
		foreach ( var t in g.Columns ) cols.Add( new LineworkFlexGridTrack { Kind = t.Kind, Value = t.Value } );
		if ( cols.Count == 0 ) cols.Add( LineworkFlexGridTrack.Fr( 1f ) );
		var rows = new List<LineworkFlexGridTrack>();
		foreach ( var t in g.Rows ) rows.Add( new LineworkFlexGridTrack { Kind = t.Kind, Value = t.Value } );

		// Extend tracks for explicit placements past the defined grid.
		foreach ( var item in g.Items )
		{
			if ( item.Column >= 0 )
				while ( cols.Count <= item.Column ) cols.Add( CopyTrack( cols[cols.Count - 1] ) );
			if ( item.Row >= 0 )
			{
				while ( rows.Count <= item.Row )
					rows.Add( rows.Count > 0 ? CopyTrack( rows[rows.Count - 1] ) : LineworkFlexGridTrack.Auto() );
			}
		}

		// Placement: explicit first, then auto-place row-major.
		var placed = new Dictionary<LineworkFlexGridGridItem, (int col, int row)>();
		var occupied = new HashSet<(int, int)>();
		foreach ( var item in g.Items )
		{
			if ( item.Column < 0 || item.Row < 0 ) continue;
			var cs = Math.Max( 1, item.ColumnSpan );
			var rs = Math.Max( 1, item.RowSpan );
			placed[item] = (item.Column, item.Row);
			for ( var cc = item.Column; cc < item.Column + cs; cc++ )
				for ( var rr = item.Row; rr < item.Row + rs; rr++ ) occupied.Add( (cc, rr) );
		}
		foreach ( var item in g.Items )
		{
			if ( placed.ContainsKey( item ) ) continue;
			var cs = Math.Max( 1, item.ColumnSpan );
			var rs = Math.Max( 1, item.RowSpan );
			var found = false;
			for ( var r = 0; !found; r++ )
			{
				for ( var cc = 0; cc + cs <= cols.Count; cc++ )
				{
					var freeBlock = true;
					for ( var dc = 0; dc < cs && freeBlock; dc++ )
						for ( var dr = 0; dr < rs; dr++ )
							if ( occupied.Contains( (cc + dc, r + dr) ) ) { freeBlock = false; break; }
					if ( !freeBlock ) continue;
					while ( rows.Count <= r + rs - 1 )
						rows.Add( rows.Count > 0 ? CopyTrack( rows[rows.Count - 1] ) : LineworkFlexGridTrack.Auto() );
					placed[item] = (cc, r);
					for ( var dc = 0; dc < cs; dc++ )
						for ( var dr = 0; dr < rs; dr++ ) occupied.Add( (cc + dc, r + dr) );
					found = true;
					break;
				}
				// If no block fits in this row (span wider than the grid),
				// the next row is tried; a span wider than all columns
				// still lands at column 0 of the next fresh row.
				if ( !found && r > 10000 ) break;
				if ( !found && cs > cols.Count )
				{
					while ( rows.Count <= r ) rows.Add( rows.Count > 0 ? CopyTrack( rows[rows.Count - 1] ) : LineworkFlexGridTrack.Auto() );
					placed[item] = (0, r);
					for ( var dc = 0; dc < cs; dc++ )
						for ( var dr = 0; dr < rs; dr++ ) occupied.Add( (dc, r + dr) );
					break;
				}
			}
		}

		// Track sizing.
		var colW = SizeTracks( cols, contentW, g.ColumnGap, placed, g.Items, true );
		var rowH = SizeTracks( rows, contentH, g.RowGap, placed, g.Items, false );

		var colX = new List<float>();
		var acc = 0f;
		for ( var i = 0; i < cols.Count; i++ ) { colX.Add( acc ); acc += colW[i] + g.ColumnGap; }
		var rowY = new List<float>();
		acc = 0f;
		for ( var i = 0; i < rows.Count; i++ ) { rowY.Add( acc ); acc += rowH[i] + g.RowGap; }

		foreach ( var item in g.Items )
		{
			var (col, row) = placed[item];
			var cs = Math.Max( 1, item.ColumnSpan );
			var rs = Math.Max( 1, item.RowSpan );
			var spanW = 0f;
			for ( var i = col; i < Math.Min( cols.Count, col + cs ); i++ ) spanW += colW[i];
			spanW += Math.Max( 0, Math.Min( cs, cols.Count - col ) - 1 ) * g.ColumnGap;
			var spanH = 0f;
			for ( var i = row; i < Math.Min( rows.Count, row + rs ); i++ ) spanH += rowH[i];
			spanH += Math.Max( 0, Math.Min( rs, rows.Count - row ) - 1 ) * g.RowGap;
			var w = item.Width > 0f ? item.Width : Math.Max( 0f, spanW - item.Margin.Left - item.Margin.Right );
			var h = item.Height > 0f ? item.Height : Math.Max( 0f, spanH - item.Margin.Top - item.Margin.Bottom );
			result.Add( new LineworkFlexGridPlacement
			{
				Name = item.Name,
				GridColumn = col,
				GridRow = row,
				Rect = new LineworkFlexGridRect( originX + colX[col] + item.Margin.Left, originY + rowY[row] + item.Margin.Top, w, h )
			} );
		}
		return result;
	}

	static LineworkFlexGridTrack CopyTrack( LineworkFlexGridTrack t ) =>
		new() { Kind = t.Kind, Value = t.Value };

	static List<float> SizeTracks(
		List<LineworkFlexGridTrack> tracks, float content, float gap,
		Dictionary<LineworkFlexGridGridItem, (int col, int row)> placed,
		List<LineworkFlexGridGridItem> items, bool isColumnAxis )
	{
		var sizes = new float[tracks.Count];
		var totalFr = 0f;
		var used = 0f;
		for ( var i = 0; i < tracks.Count; i++ )
		{
			if ( tracks[i].Kind == LineworkFlexGridTrackKind.Fixed )
			{
				sizes[i] = tracks[i].Value;
				used += sizes[i];
			}
			else if ( tracks[i].Kind == LineworkFlexGridTrackKind.Fraction )
			{
				totalFr += tracks[i].Value;
			}
			else // Auto: largest single-span item in this track.
			{
				var best = 0f;
				foreach ( var item in items )
				{
					if ( !placed.TryGetValue( item, out var pos ) ) continue;
					var at = isColumnAxis ? pos.col : pos.row;
					var span = isColumnAxis ? Math.Max( 1, item.ColumnSpan ) : Math.Max( 1, item.RowSpan );
					if ( at != i || span != 1 ) continue;
					var s = isColumnAxis ? item.Width : item.Height;
					if ( s > best ) best = s;
				}
				sizes[i] = best;
				used += best;
			}
		}
		used += Math.Max( 0, tracks.Count - 1 ) * gap;
		var remaining = Math.Max( 0f, content - used );
		if ( totalFr > 0f )
		{
			for ( var i = 0; i < tracks.Count; i++ )
				if ( tracks[i].Kind == LineworkFlexGridTrackKind.Fraction )
					sizes[i] = remaining * ( tracks[i].Value / totalFr );
		}
		return new List<float>( sizes );
	}
}
