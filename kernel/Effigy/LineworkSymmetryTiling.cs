using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Lane E: radial symmetry and wrap-around tiling for 2D strokes.
///
/// This is deliberately NOT <see cref="BrushSymmetry"/>: that class is the 3D sculpt/stroke
/// symmetry (orthogonal Vec3 mirrors plus rotation about an axis through the origin). What it
/// does not do is kaleidoscope symmetry — mirror a stroke across the boundary between two
/// radial segments so the pattern joins seamlessly at every boundary — and it knows nothing
/// about tiles. This file is the 2D-only counterpart: it works on Vec2 polylines (and on the
/// sampled points of a <see cref="PressureSpline"/>) around a 2D centre, and on strokes drawn
/// on a rectangular tile that must wrap from one edge to the opposite one.
///
/// Radial rule, in one sentence: every input point is first folded into wedge 0 (the wedge
/// between angle 0 and 2 * PI / segments), mirroring across a boundary when it came from an
/// odd-numbered wedge, and then copied into every segment — even segments by rotation, odd
/// segments by reflection — so a stroke that crosses a segment boundary cannot tear at it:
/// the point just below the boundary and the point just above it fold to the same wedge-0
/// point and therefore expand to the same set of copies.
///
/// Tiling rule, in one sentence: a stroke is split where it crosses a tile edge, the exit
/// point is kept on the edge it left by, and the continuation restarts from the opposite
/// edge, so a rasterizer drawing each piece sees the stroke continue seamlessly across the
/// seam instead of a jump line drawn across the whole tile.
/// </summary>
public static class LineworkSymmetryTiling
{
	const float Eps = 1e-6f;

	/// <summary>Segments below 1 mean no symmetry: a single copy, the stroke as drawn.</summary>
	static int NormalSegments( int segments ) => Math.Max( 1, segments );

	static float AngleOf( Vec2 d )
	{
		var a = MathF.Atan2( d.y, d.x );
		return a < 0f ? a + 2f * MathF.PI : a;
	}

	static Vec2 Polar( Vec2 centre, float radius, float angle ) =>
		centre + new Vec2( MathF.Cos( angle ) * radius, MathF.Sin( angle ) * radius );

	/// <summary>
	/// The input point folded into wedge 0. With <paramref name="mirror"/> on, a point from
	/// an odd wedge is reflected across the wedge boundary it crossed (local angle becomes
	/// wedge - local angle); with it off, the point is only rotated into wedge 0. The centre
	/// itself has no angle and folds to itself.
	/// </summary>
	public static Vec2 FoldToWedge( Vec2 point, Vec2 centre, int segments, bool mirror = true )
	{
		segments = NormalSegments( segments );
		if ( segments == 1 )
			return point;

		var d = point - centre;
		var r = d.Length;
		if ( r < 1e-12f )
			return centre;

		var wedge = 2f * MathF.PI / segments;
		var angle = AngleOf( d );
		var index = (int)(angle / wedge);
		if ( index >= segments )
			index = segments - 1;
		var local = angle - index * wedge;
		if ( mirror && (index & 1) == 1 )
			local = wedge - local;
		return Polar( centre, r, local );
	}

	static Vec2 PlaceInSegment( Vec2 folded, Vec2 centre, int segments, int k, bool mirror )
	{
		var d = folded - centre;
		var r = d.Length;
		if ( r < 1e-12f )
			return centre;

		var wedge = 2f * MathF.PI / segments;
		var local = AngleOf( d );
		var angle = k * wedge + (mirror && (k & 1) == 1 ? wedge - local : local);
		return Polar( centre, r, angle );
	}

	/// <summary>
	/// One polyline per radial copy, copy 0 first (the folded stroke as drawn in wedge 0).
	/// Copy k is the folded stroke placed in segment k; odd copies are reflections when
	/// <paramref name="mirror"/> is on, plain rotations when it is off. Every copy keeps the
	/// input point count and order, so a copy is a drop-in polyline for the rasterizer.
	/// </summary>
	public static List<List<Vec2>> ExpandRadial(
		IReadOnlyList<Vec2> points, Vec2 centre, int segments, bool mirror = true )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );
		segments = NormalSegments( segments );

		var folded = new List<Vec2>( points.Count );
		foreach ( var p in points )
			folded.Add( FoldToWedge( p, centre, segments, mirror ) );

		var copies = new List<List<Vec2>>( segments );
		for ( var k = 0; k < segments; k++ )
		{
			var copy = new List<Vec2>( folded.Count );
			foreach ( var q in folded )
				copy.Add( PlaceInSegment( q, centre, segments, k, mirror ) );
			copies.Add( copy );
		}
		return copies;
	}

	/// <summary>
	/// The spline form of <see cref="ExpandRadial"/>: each copy transforms the spline's
	/// sampled <see cref="PressureSpline.Points"/> (the drawn path, so this is exact) and
	/// applies the same fold-and-place to its <see cref="PressureSpline.ControlPoints"/>.
	/// Pressures, <see cref="PressureSpline.BaseWidth"/> and layer carry over unchanged;
	/// copy k is flagged <see cref="PressureSpline.Mirrored"/> exactly when it is a
	/// reflection (odd k, mirror on), and its id is the original suffixed "#radial-k".
	/// </summary>
	public static List<PressureSpline> ExpandRadialSpline(
		PressureSpline spline, Vec2 centre, int segments, bool mirror = true )
	{
		if ( spline is null )
			throw new ArgumentNullException( nameof( spline ) );
		segments = NormalSegments( segments );

		var copies = new List<PressureSpline>( segments );
		for ( var k = 0; k < segments; k++ )
		{
			List<Vec2> Map( List<Vec2> pts )
			{
				var m = new List<Vec2>( pts.Count );
				foreach ( var p in pts )
					m.Add( PlaceInSegment( FoldToWedge( p, centre, segments, mirror ), centre, segments, k, mirror ) );
				return m;
			}

			copies.Add( new PressureSpline
			{
				StrokeId = spline.StrokeId + "#radial-" + k,
				Layer = spline.Layer,
				BaseWidth = spline.BaseWidth,
				ControlPoints = Map( spline.ControlPoints ),
				Points = Map( spline.Points ),
				Pressures = new List<float>( spline.Pressures ),
				Mirrored = mirror && (k & 1) == 1,
			} );
		}
		return copies;
	}

	/// <summary>
	/// A point wrapped into the tile [0, tileWidth) x [0, tileHeight): a point exactly on
	/// the right/top edge wraps to the left/bottom edge, and a negative point wraps from
	/// the opposite side. Tile sizes must be positive.
	/// </summary>
	public static Vec2 WrapPoint( Vec2 point, float tileWidth, float tileHeight )
	{
		if ( tileWidth <= 0f )
			throw new ArgumentOutOfRangeException( nameof( tileWidth ), "Tile width must be positive." );
		if ( tileHeight <= 0f )
			throw new ArgumentOutOfRangeException( nameof( tileHeight ), "Tile height must be positive." );
		return new Vec2(
			point.x - MathF.Floor( point.x / tileWidth ) * tileWidth,
			point.y - MathF.Floor( point.y / tileHeight ) * tileHeight );
	}

	static bool Same( Vec2 a, Vec2 b ) => (a - b).LengthSquared < 1e-10f;

	/// <summary>
	/// A polyline split into pieces that each lie inside the tile, in drawing order. A
	/// piece that runs off an edge ends on that edge and the next piece starts on the
	/// opposite edge at the same other coordinate, so the seam is invisible when the tile
	/// repeats. A stroke drawn entirely inside the tile comes back as one unchanged piece
	/// (up to the wrapping of a start point that was already outside the tile). A segment
	/// longer than the tile is split once per edge it crosses.
	/// </summary>
	public static List<List<Vec2>> WrapStroke(
		IReadOnlyList<Vec2> points, float tileWidth, float tileHeight )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );
		if ( tileWidth <= 0f )
			throw new ArgumentOutOfRangeException( nameof( tileWidth ), "Tile width must be positive." );
		if ( tileHeight <= 0f )
			throw new ArgumentOutOfRangeException( nameof( tileHeight ), "Tile height must be positive." );

		var pieces = new List<List<Vec2>>();
		if ( points.Count == 0 )
			return pieces;
		if ( points.Count == 1 )
		{
			pieces.Add( new List<Vec2> { WrapPoint( points[0], tileWidth, tileHeight ) } );
			return pieces;
		}

		List<Vec2> current = null;
		void Add( Vec2 p )
		{
			if ( current is null )
			{
				current = new List<Vec2> { p };
				pieces.Add( current );
			}
			else if ( !Same( current[^1], p ) )
			{
				current.Add( p );
			}
		}

		for ( var i = 0; i + 1 < points.Count; i++ )
		{
			// Shift the segment so it starts inside the tile, keeping its shape: the
			// wrapped start plus the original segment vector.
			var cur = WrapPoint( points[i], tileWidth, tileHeight );
			var target = cur + (points[i + 1] - points[i]);

			// A start sitting exactly on an edge and heading out of the tile is really
			// the continuation from the opposite edge — wrap it before drawing anything.
			if ( cur.x <= Eps && target.x < cur.x ) { cur = new Vec2( cur.x + tileWidth, cur.y ); target = new Vec2( target.x + tileWidth, target.y ); }
			if ( cur.x >= tileWidth - Eps && target.x > cur.x ) { cur = new Vec2( cur.x - tileWidth, cur.y ); target = new Vec2( target.x - tileWidth, target.y ); }
			if ( cur.y <= Eps && target.y < cur.y ) { cur = new Vec2( cur.x, cur.y + tileHeight ); target = new Vec2( target.x, target.y + tileHeight ); }
			if ( cur.y >= tileHeight - Eps && target.y > cur.y ) { cur = new Vec2( cur.x, cur.y - tileHeight ); target = new Vec2( target.x, target.y - tileHeight ); }

			// A new segment continues the open piece only when it starts where that
			// piece ended; after an edge split the wrapped start already matches the
			// open piece, and a jump (e.g. a polyline resuming on the far edge) starts
			// a fresh piece.
			if ( current is not null && !Same( current[^1], cur ) )
				current = null;
			Add( cur );

			for ( var guard = 0; guard < 1024; guard++ )
			{
				var d = target - cur;
				if ( d.LengthSquared < 1e-12f )
					break;

				// Parametric exit times for each edge; infinity when not heading there.
				float Tx()
				{
					if ( d.x > Eps ) return (tileWidth - cur.x) / d.x;
					if ( d.x < -Eps ) return (0f - cur.x) / d.x;
					return float.PositiveInfinity;
				}
				float Ty()
				{
					if ( d.y > Eps ) return (tileHeight - cur.y) / d.y;
					if ( d.y < -Eps ) return (0f - cur.y) / d.y;
					return float.PositiveInfinity;
				}
				var tx = Tx();
				var ty = Ty();
				var t = MathF.Min( tx, ty );

				// No exit before the end (t >= 1, or no edge ahead): the rest is inside.
				if ( float.IsInfinity( t ) || t > 1f - 1e-6f )
				{
					Add( target );
					break;
				}
				if ( t <= Eps )
				{
					// On a boundary heading out (only reachable for a segment that
					// started on an edge the pre-wrap above missed, e.g. a corner):
					// wrap immediately rather than emitting a zero-length piece.
					if ( tx <= Eps )
					{
						var shift = d.x > 0f ? -tileWidth : tileWidth;
						cur = new Vec2( cur.x + shift, cur.y );
						target = new Vec2( target.x + shift, target.y );
					}
					if ( ty <= Eps )
					{
						var shift = d.y > 0f ? -tileHeight : tileHeight;
						cur = new Vec2( cur.x, cur.y + shift );
						target = new Vec2( target.x, target.y + shift );
					}
					current = null;
					Add( cur );
					continue;
				}

				var exit = cur + d * t;
				Add( exit );
				// Close this piece on the edge, restart from the opposite edge.
				var wrapped = new Vec2(
					MathF.Abs( tx - t ) < 1e-6f ? (d.x > 0f ? 0f : tileWidth) : exit.x,
					MathF.Abs( ty - t ) < 1e-6f ? (d.y > 0f ? 0f : tileHeight) : exit.y );
				target += wrapped - exit;
				cur = wrapped;
				current = null;
				Add( cur );
			}
		}
		return pieces;
	}
}
