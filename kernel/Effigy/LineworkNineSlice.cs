using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// S&amp;box Linework Lane I: an interactive 9-slice border manipulator model.
///
/// A 9-slice definition cuts a source image into a 3x3 grid (left/right/top/
/// bottom cuts) and maps it onto a target rect: corners keep their source
/// size, edges stretch or tile along one axis, and the centre is filled
/// (stretched), tiled, or left empty. This file is the MODEL only — it
/// computes source/dest rects and clamps drags. It draws nothing, measures
/// no text, and holds no Sandbox/Editor types, so it compiles in
/// Effigy.Tests as plain C#.
///
/// This is deliberately separate from the 9-slice SCSS baking in
/// LineworkUiBaker (a uniform-insets border-image string). That baker is
/// untouched. This lane adds what it cannot do: per-edge cuts, tile modes,
/// exact dest geometry, and a drag manipulator whose cuts can never cross
/// or invert.
///
/// Invariants, enforced by Normalized() and by every manipulator drag:
/// every cut is &gt;= 0, Left + Right &lt;= SourceWidth, Top + Bottom &lt;=
/// SourceHeight. A target smaller than the corner sum does not invert
/// anything: corners scale down proportionally to fit and the centre
/// dest collapses to zero.
/// </summary>
public enum LineworkNineSliceEdgeMode
{
	Stretch,
	Tile
}

public enum LineworkNineSliceCentreMode
{
	/// <summary>One centre quad, stretched to the centre dest rect.</summary>
	Fill,
	/// <summary>No centre quad at all (hollow frame).</summary>
	Empty,
	/// <summary>Centre tiled in both axes with the source centre tile.</summary>
	Tile
}

public enum LineworkNineSliceCut
{
	Left,
	Right,
	Top,
	Bottom
}

public enum LineworkNineSliceCellKind
{
	Corner,
	Edge,
	Centre
}

/// <summary>One float rect. X/Y are the top-left corner; W/H are never
/// negative in any rect this lane produces.</summary>
public struct LineworkNineSliceRect
{
	public float X;
	public float Y;
	public float W;
	public float H;

	public LineworkNineSliceRect( float x, float y, float w, float h )
	{
		X = x; Y = y; W = w; H = h;
	}

	public float Right => X + W;
	public float Bottom => Y + H;

	public bool Contains( float x, float y ) =>
		x >= X && x <= Right && y >= Y && y <= Bottom;

	public override string ToString() => $"({X}, {Y}, {W}, {H})";
}

/// <summary>One drawn piece of a laid-out 9-slice: which cell it came from,
/// which tile of that cell (0 for stretched cells), and its source and
/// dest rects. Tiled cells repeat the SAME source tile; a clipped final
/// tile shows the tile's leading part, so its source origin is the cell's
/// source origin and its source size equals its dest size.</summary>
public struct LineworkNineSliceQuad
{
	public string Name;
	public int Column;
	public int Row;
	public LineworkNineSliceCellKind Kind;
	public int TileColumn;
	public int TileRow;
	public LineworkNineSliceRect Source;
	public LineworkNineSliceRect Dest;

	public bool IsCorner => Kind == LineworkNineSliceCellKind.Corner;
	public bool IsEdge => Kind == LineworkNineSliceCellKind.Edge;
	public bool IsCentre => Kind == LineworkNineSliceCellKind.Centre;
}

/// <summary>A 9-slice definition: source size, four cuts, edge modes
/// (top/bottom edges use HorizontalEdgeMode, left/right edges use
/// VerticalEdgeMode) and the centre mode.</summary>
public sealed class LineworkNineSliceDefinition
{
	public float SourceWidth;
	public float SourceHeight;
	public float Left;
	public float Right;
	public float Top;
	public float Bottom;
	public LineworkNineSliceEdgeMode HorizontalEdgeMode = LineworkNineSliceEdgeMode.Stretch;
	public LineworkNineSliceEdgeMode VerticalEdgeMode = LineworkNineSliceEdgeMode.Stretch;
	public LineworkNineSliceCentreMode CentreMode = LineworkNineSliceCentreMode.Fill;

	public float CentreSourceWidth => Math.Max( 0f, SourceWidth - Left - Right );
	public float CentreSourceHeight => Math.Max( 0f, SourceHeight - Top - Bottom );

	/// <summary>True only when every cut is &gt;= 0 and the cut sums fit
	/// the source. A raw definition fresh from a drag is normalised
	/// instead of rejected — see Normalized().</summary>
	public bool IsValid() =>
		SourceWidth > 0f && SourceHeight > 0f &&
		Left >= 0f && Right >= 0f && Top >= 0f && Bottom >= 0f &&
		Left + Right <= SourceWidth && Top + Bottom <= SourceHeight;

	/// <summary>Clamped copy: negatives to zero, then each cut capped so
	/// it never crosses the opposite cut. Order is Left, Right, Top,
	/// Bottom, so when Left + Right overflows the source, Right is the
	/// one trimmed (and likewise Bottom for Top + Bottom).</summary>
	public LineworkNineSliceDefinition Normalized()
	{
		var w = Math.Max( 0f, SourceWidth );
		var h = Math.Max( 0f, SourceHeight );
		var l = Math.Min( Math.Max( 0f, Left ), w );
		var r = Math.Min( Math.Max( 0f, Right ), Math.Max( 0f, w - l ) );
		var t = Math.Min( Math.Max( 0f, Top ), h );
		var b = Math.Min( Math.Max( 0f, Bottom ), Math.Max( 0f, h - t ) );
		return new LineworkNineSliceDefinition
		{
			SourceWidth = w,
			SourceHeight = h,
			Left = l,
			Right = r,
			Top = t,
			Bottom = b,
			HorizontalEdgeMode = HorizontalEdgeMode,
			VerticalEdgeMode = VerticalEdgeMode,
			CentreMode = CentreMode
		};
	}

	/// <summary>Layout for a target size. The definition is normalised
	/// first, so a raw over-sum definition still lays out sanely.
	/// Returns quads in row-major order (top row first, left to right,
	/// tiles in order within a cell). A tiled cell whose dest length is
	/// zero emits no quads; a stretched cell always emits exactly one,
	/// even when a source dimension is zero.</summary>
	public List<LineworkNineSliceQuad> Layout( float targetWidth, float targetHeight ) =>
		Compute( this, targetWidth, targetHeight );

	public static List<LineworkNineSliceQuad> Compute( LineworkNineSliceDefinition definition, float targetWidth, float targetHeight )
	{
		var d = definition.Normalized();
		var tw = Math.Max( 0f, targetWidth );
		var th = Math.Max( 0f, targetHeight );

		var centreW = Math.Max( 0f, d.SourceWidth - d.Left - d.Right );
		var centreH = Math.Max( 0f, d.SourceHeight - d.Top - d.Bottom );

		var srcX = new[] { 0f, d.Left, d.Left + centreW };
		var srcY = new[] { 0f, d.Top, d.Top + centreH };
		var srcW = new[] { d.Left, centreW, d.Right };
		var srcH = new[] { d.Top, centreH, d.Bottom };

		// Dest corners keep source size unless the target is smaller
		// than the corner sum, in which case they scale down
		// proportionally and the centre collapses to zero — never
		// negative, never overlapping.
		var cornerSumX = d.Left + d.Right;
		var scaleX = cornerSumX > 0f ? ( tw >= cornerSumX ? 1f : tw / cornerSumX ) : 0f;
		var destL = d.Left * scaleX;
		var destR = d.Right * scaleX;
		var destC = Math.Max( 0f, tw - destL - destR );
		var cornerSumY = d.Top + d.Bottom;
		var scaleY = cornerSumY > 0f ? ( th >= cornerSumY ? 1f : th / cornerSumY ) : 0f;
		var destT = d.Top * scaleY;
		var destB = d.Bottom * scaleY;
		var destM = Math.Max( 0f, th - destT - destB );

		var destX = new[] { 0f, destL, destL + destC };
		var destY = new[] { 0f, destT, destT + destM };
		var destW = new[] { destL, destC, destR };
		var destH = new[] { destT, destM, destB };

		var colNames = new[] { "Left", "Centre", "Right" };
		var rowNames = new[] { "Top", "Middle", "Bottom" };

		var quads = new List<LineworkNineSliceQuad>();
		for ( var row = 0; row < 3; row++ )
		{
			for ( var col = 0; col < 3; col++ )
			{
				var kind = col == 1 && row == 1
					? LineworkNineSliceCellKind.Centre
					: ( col == 1 || row == 1
						? LineworkNineSliceCellKind.Edge
						: LineworkNineSliceCellKind.Corner );
				if ( kind == LineworkNineSliceCellKind.Centre && d.CentreMode == LineworkNineSliceCentreMode.Empty )
					continue;
				var name = kind == LineworkNineSliceCellKind.Centre
					? "Centre"
					: rowNames[row] + colNames[col];

				var tileX = false;
				var tileY = false;
				if ( kind == LineworkNineSliceCellKind.Centre )
				{
					tileX = tileY = d.CentreMode == LineworkNineSliceCentreMode.Tile;
				}
				else if ( kind == LineworkNineSliceCellKind.Edge )
				{
					if ( row == 1 ) tileY = d.VerticalEdgeMode == LineworkNineSliceEdgeMode.Tile;
					else tileX = d.HorizontalEdgeMode == LineworkNineSliceEdgeMode.Tile;
				}

				void Add( int tc, int tr, LineworkNineSliceRect src, LineworkNineSliceRect dst )
				{
					quads.Add( new LineworkNineSliceQuad
					{
						Name = name, Column = col, Row = row, Kind = kind,
						TileColumn = tc, TileRow = tr, Source = src, Dest = dst
					} );
				}

				if ( !tileX && !tileY )
				{
					Add( 0, 0,
						new LineworkNineSliceRect( srcX[col], srcY[row], srcW[col], srcH[row] ),
						new LineworkNineSliceRect( destX[col], destY[row], destW[col], destH[row] ) );
				}
				else if ( tileX && !tileY )
				{
					// One axis tiles; a zero-size source tile cannot
					// tile, so it falls back to a single quad.
					if ( srcW[col] <= 0f || destW[col] <= 0f )
					{
						if ( destW[col] > 0f || srcW[col] <= 0f )
							Add( 0, 0,
								new LineworkNineSliceRect( srcX[col], srcY[row], srcW[col], srcH[row] ),
								new LineworkNineSliceRect( destX[col], destY[row], destW[col], destH[row] ) );
					}
					else
					{
						var consumed = 0f;
						var ti = 0;
						while ( consumed < destW[col] - 0.0001f )
						{
							var step = Math.Min( srcW[col], destW[col] - consumed );
							Add( ti, 0,
								new LineworkNineSliceRect( srcX[col], srcY[row], step, srcH[row] ),
								new LineworkNineSliceRect( destX[col] + consumed, destY[row], step, destH[row] ) );
							consumed += step;
							ti++;
						}
					}
				}
				else if ( !tileX && tileY )
				{
					if ( srcH[row] <= 0f || destH[row] <= 0f )
					{
						if ( destH[row] > 0f || srcH[row] <= 0f )
							Add( 0, 0,
								new LineworkNineSliceRect( srcX[col], srcY[row], srcW[col], srcH[row] ),
								new LineworkNineSliceRect( destX[col], destY[row], destW[col], destH[row] ) );
					}
					else
					{
						var consumed = 0f;
						var ti = 0;
						while ( consumed < destH[row] - 0.0001f )
						{
							var step = Math.Min( srcH[row], destH[row] - consumed );
							Add( 0, ti,
								new LineworkNineSliceRect( srcX[col], srcY[row], srcW[col], step ),
								new LineworkNineSliceRect( destX[col], destY[row] + consumed, destW[col], step ) );
							consumed += step;
							ti++;
						}
					}
				}
				else
				{
					// Centre tiling in both axes.
					if ( srcW[col] <= 0f || srcH[row] <= 0f || destW[col] <= 0f || destH[row] <= 0f )
					{
						if ( ( destW[col] > 0f && destH[row] > 0f ) || srcW[col] <= 0f || srcH[row] <= 0f )
							Add( 0, 0,
								new LineworkNineSliceRect( srcX[col], srcY[row], srcW[col], srcH[row] ),
								new LineworkNineSliceRect( destX[col], destY[row], destW[col], destH[row] ) );
					}
					else
					{
						var consumedY = 0f;
						var tj = 0;
						while ( consumedY < destH[row] - 0.0001f )
						{
							var stepY = Math.Min( srcH[row], destH[row] - consumedY );
							var consumedX = 0f;
							var ti = 0;
							while ( consumedX < destW[col] - 0.0001f )
							{
								var stepX = Math.Min( srcW[col], destW[col] - consumedX );
								Add( ti, tj,
									new LineworkNineSliceRect( srcX[col], srcY[row], stepX, stepY ),
									new LineworkNineSliceRect( destX[col] + consumedX, destY[row] + consumedY, stepX, stepY ) );
								consumedX += stepX;
								ti++;
							}
							consumedY += stepY;
							tj++;
						}
					}
				}
			}
		}
		return quads;
	}
}

/// <summary>Interactive manipulator over a 9-slice definition. The held
/// definition is always normalised: every drag clamps its cut so it can
/// never cross the opposite cut or go negative, so cuts never invert
/// no matter how far the pointer is dragged.</summary>
public sealed class LineworkNineSliceManipulator
{
	public LineworkNineSliceDefinition Definition { get; private set; }

	public float SourceWidth => Definition.SourceWidth;
	public float SourceHeight => Definition.SourceHeight;
	public float Left => Definition.Left;
	public float Right => Definition.Right;
	public float Top => Definition.Top;
	public float Bottom => Definition.Bottom;

	public LineworkNineSliceManipulator( LineworkNineSliceDefinition definition )
	{
		Definition = definition.Normalized();
	}

	public void DragLeft( float value ) =>
		Definition.Left = Math.Min( Math.Max( 0f, value ), Math.Max( 0f, Definition.SourceWidth - Definition.Right ) );

	public void DragRight( float value ) =>
		Definition.Right = Math.Min( Math.Max( 0f, value ), Math.Max( 0f, Definition.SourceWidth - Definition.Left ) );

	public void DragTop( float value ) =>
		Definition.Top = Math.Min( Math.Max( 0f, value ), Math.Max( 0f, Definition.SourceHeight - Definition.Bottom ) );

	public void DragBottom( float value ) =>
		Definition.Bottom = Math.Min( Math.Max( 0f, value ), Math.Max( 0f, Definition.SourceHeight - Definition.Top ) );

	public void DragCut( LineworkNineSliceCut cut, float value )
	{
		switch ( cut )
		{
			case LineworkNineSliceCut.Left: DragLeft( value ); break;
			case LineworkNineSliceCut.Right: DragRight( value ); break;
			case LineworkNineSliceCut.Top: DragTop( value ); break;
			case LineworkNineSliceCut.Bottom: DragBottom( value ); break;
		}
	}

	/// <summary>Which cut line, if any, the point is grabbing. Distance is
	/// measured to the cut LINE, but only within the source rect
	/// (expanded by tolerance) — a point far above or beside the image
	/// grabs nothing. Ties resolve Left, then Right, then Top, then
	/// Bottom.</summary>
	public bool TryHitCut( float x, float y, float tolerance, out LineworkNineSliceCut cut )
	{
		cut = LineworkNineSliceCut.Left;
		var tol = Math.Max( 0f, tolerance );
		var inX = x >= -tol && x <= Definition.SourceWidth + tol;
		var inY = y >= -tol && y <= Definition.SourceHeight + tol;
		if ( !inX || !inY )
			return false;
		var dLeft = Math.Abs( x - Definition.Left );
		var dRight = Math.Abs( x - ( Definition.SourceWidth - Definition.Right ) );
		var dTop = Math.Abs( y - Definition.Top );
		var dBottom = Math.Abs( y - ( Definition.SourceHeight - Definition.Bottom ) );
		var best = dLeft;
		cut = LineworkNineSliceCut.Left;
		if ( dRight < best ) { best = dRight; cut = LineworkNineSliceCut.Right; }
		if ( dTop < best ) { best = dTop; cut = LineworkNineSliceCut.Top; }
		if ( dBottom < best ) { best = dBottom; cut = LineworkNineSliceCut.Bottom; }
		return best <= tol;
	}
}
