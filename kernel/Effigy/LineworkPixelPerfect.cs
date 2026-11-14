using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Aseprite-grade pixel-perfect line filtering.
/// Removes accidental double-pixel (L-shaped) corner artifacts from rapid freehand sketching,
/// creating clean, 1-pixel-wide diagonal and orthogonal strokes.
/// </summary>
public static class LineworkPixelPerfect
{
	public readonly struct PixelCoord : IEquatable<PixelCoord>
	{
		public readonly int X;
		public readonly int Y;

		public PixelCoord( int x, int y )
		{
			X = x;
			Y = y;
		}

		public bool Equals( PixelCoord other ) => X == other.X && Y == other.Y;
		public override bool Equals( object obj ) => obj is PixelCoord other && Equals( other );
		public override int GetHashCode() => HashCode.Combine( X, Y );
		public override string ToString() => $"({X}, {Y})";
	}

	/// <summary>
	/// Filters a stream of raster coordinates, removing redundant L-shaped corner pixels.
	/// </summary>
	public static List<PixelCoord> Filter( IReadOnlyList<PixelCoord> input )
	{
		if ( input == null || input.Count <= 2 )
			return input != null ? new List<PixelCoord>( input ) : new List<PixelCoord>();

		var result = new List<PixelCoord>( input.Count );

		for ( var i = 0; i < input.Count; i++ )
		{
			var current = input[i];

			// Don't add duplicate adjacent pixels
			if ( result.Count > 0 && result[^1].Equals( current ) )
				continue;

			// Check if the previous pixel forms an L-corner between result[^2] and current
			if ( result.Count >= 2 )
			{
				var p0 = result[^2];
				var p1 = result[^1];
				var p2 = current;

				var dx1 = p1.X - p0.X;
				var dy1 = p1.Y - p0.Y;
				var dx2 = p2.X - p1.X;
				var dy2 = p2.Y - p1.Y;

				// If p1 is an orthogonal neighbor to both p0 and p2, and p2 is diagonal to p0:
				// e.g. p0=(0,0), p1=(1,0), p2=(1,1) -> dx1=1, dy1=0, dx2=0, dy2=1
				// p1 is redundant because p0 and p2 are diagonally connected (step X and step Y).
				var isLCorner = (Math.Abs( dx1 ) + Math.Abs( dy1 ) == 1) &&
				                (Math.Abs( dx2 ) + Math.Abs( dy2 ) == 1) &&
				                (Math.Abs( p2.X - p0.X ) == 1) &&
				                (Math.Abs( p2.Y - p0.Y ) == 1);

				if ( isLCorner )
				{
					// Drop redundant corner p1
					result.RemoveAt( result.Count - 1 );
				}
			}

			result.Add( current );
		}

		return result;
	}
}
