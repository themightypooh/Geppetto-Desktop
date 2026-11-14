using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Map;

/// <summary>
/// 3-point Clip + polygon tool for plane-based convex brushes
/// (Hammer Desktop P2: hammer-clip-three-point-polygon, group "clippolygon").
///
/// Built ON TOP of <see cref="MapClip"/> (hammer-brush-clip-two-point,
/// kernel/Effigy/Map/MapClip.cs, namespace Effigy) — this file does not re-implement the
/// clip, it only builds the clip plane from 3 points and delegates the cut/split to
/// MapClip.Clip, so the 2-point guarantees carry over unchanged: kept faces stay on
/// their original planes (material and UVs survive untouched), the new cut face is
/// nodraw or the caller's material, a plane outside the brush is a named no-op, and an
/// invalid brush is rejected with its Validate() defect codes.
///
/// The polygon tool draws a convex footprint as an ordered 2D point set and extrudes
/// it to a height: the result is one convex <see cref="MapBrush"/> (side faces per
/// footprint edge plus top and bottom). A non-convex footprint is REJECTED with a named
/// reason and the convex hull returned as a suggestion — never silently substituted:
/// the caller (and, later, the UI) decides whether to use the hull.
///
/// CONVENTIONS. Geometry is kernel inches, Z-up. A face plane is Dot(Normal, p) =
/// Distance with an outward unit normal; the solid is Normal.p &lt;= Distance. The clip
/// plane reuses the kernel's <see cref="global::Effigy.ClipPlane"/> (normal + offset,
/// keep side = Distance &gt;= 0). NOTE on the name: Effigy.Map has its own internal
/// ClipPlane (CompileRunner.cs, a BSP helper), so this file always spells the kernel
/// clip plane as global::Effigy.ClipPlane — an unqualified ClipPlane here would bind to
/// the wrong one.
///
/// SEAM (for the integrator / the MapClip owner). The board text says "Implement in
/// MapClip + MapBrushPrimitives"; this ticket was instead scoped to a new file because
/// MapClip.cs belongs to worker 06 (hammer-brush-clip-two-point, live on disk,
/// unclaimed at the time of writing) and must not be edited. Everything this file
/// needs from MapClip already exists on disk as public API (PlaneFromTwoPoints is not
/// needed here; Clip(MapBrush, ClipPlane, MapClipOptions), MapClipResult,
/// ClipOutcome, ClipMode, MapClipOptions, MapClip.NoDrawMaterial). No seam is missing.
/// MapBrushPrimitives.cs is NOT used here either: as landed it builds against a
/// MapFace/MapPlane API that does not exist (its Face()/Finish() assume it), so the
/// polygon tool builds its MapBrushFace planes directly against the landed MapBrush
/// API (MapBrushFace(Normal, Distance, materialName), FromPlanes, Volume, IsValid).
/// Every MapBrushFace use in this file is spelled global::Effigy.MapBrushFace on
/// purpose: Displacement.cs also defines an Effigy.Map.MapBrushFace (a copy-only
/// alias its owner still reconciles), and an unqualified name in this namespace
/// binds to that one, whose constructor takes a different shape. The gate owns
/// removing the duplicate; this file builds against the landed Effigy type.
///
/// NO UI. Backend / data-model / API only; the Clip tool's 3D handles and the polygon
/// tool's 2D drawing are deferred to a UI ticket. <see cref="ThreePointClipPreview"/>
/// is the backend for the red "what will be removed" highlight: it reports the kept
/// and removed volumes for a 3-point plane without cutting anything.
/// </summary>
public static class ClipPolygon
{
	/// <summary>Plane-classification epsilon, in model units (inches). Same order as MapClip's.</summary>
	const double Eps = 1e-4;

	/// <summary>
	/// Build the clip plane through the 3 points the mapper picked (Hammer++ 3-point
	/// clipping, C toggles). The normal is normalize(cross(b - a, c - a)); the kept
	/// ("white") side is the side the normal points to, and
	/// <see cref="MapClipOptions.KeepFront"/> flips it, exactly as in the 2-point clip.
	/// Throws <see cref="ArgumentException"/> when two points coincide
	/// ("clip-three-points-coincident") or the three are collinear
	/// ("clip-three-points-collinear"), both named in the message.
	/// </summary>
	public static global::Effigy.ClipPlane PlaneFromThreePoints( Vec3 a, Vec3 b, Vec3 c )
	{
		if ( ( b - a ).LengthSquared < 1e-12f )
			throw new ArgumentException( "clip-three-points-coincident: clip points a and b must differ.", nameof( b ) );
		if ( ( c - a ).LengthSquared < 1e-12f )
			throw new ArgumentException( "clip-three-points-coincident: clip points a and c must differ.", nameof( c ) );
		if ( ( c - b ).LengthSquared < 1e-12f )
			throw new ArgumentException( "clip-three-points-coincident: clip points b and c must differ.", nameof( c ) );
		var normal = Vec3.Cross( b - a, c - a );
		if ( normal.LengthSquared < 1e-12f )
			throw new ArgumentException( "clip-three-points-collinear: the three clip points must not lie on one line.", nameof( c ) );
		normal = normal.Normal;
		return new global::Effigy.ClipPlane( normal, Vec3.Dot( normal, a ) );
	}

	/// <summary>
	/// Clip <paramref name="brush"/> by the plane through the 3 picked points: builds
	/// the plane with <see cref="PlaneFromThreePoints"/>, then cuts/splits via
	/// <see cref="MapClip.Clip(MapBrush, global::Effigy.ClipPlane, MapClipOptions)"/>.
	/// Cut mode keeps one side; Split mode returns both sides. Same outcomes, same
	/// material/UV preservation, same named no-ops as the 2-point clip.
	/// </summary>
	public static MapClipResult Clip( MapBrush brush, Vec3 a, Vec3 b, Vec3 c, MapClipOptions options = null )
		=> MapClip.Clip( brush, PlaneFromThreePoints( a, b, c ), options );

	/// <summary>
	/// Preview of a 3-point clip: the plane plus the kept/removed volumes, without
	/// cutting anything. The backend for the Hammer++ red highlight of what will be
	/// removed. Computed by a Split clip (both sides), so no brush is modified; when
	/// the plane misses the brush the whole volume sits on one side: the removed
	/// volume is 0 when that side is the kept side, and the FULL volume when it is
	/// the cut side — committing the cut with those options would remove the whole
	/// brush (WholeBrushCutAway), and the highlight must say so, not show nothing.
	/// When the brush is invalid the preview reports
	/// <see cref="ClipOutcome.RejectedInvalidBrush"/> with the defect codes in
	/// <see cref="ThreePointClipPreview.Reason"/> and zero volumes.
	/// </summary>
	public static ThreePointClipPreview Preview( MapBrush brush, Vec3 a, Vec3 b, Vec3 c, MapClipOptions options = null )
	{
		if ( brush is null )
			throw new ArgumentNullException( nameof( brush ) );
		var plane = PlaneFromThreePoints( a, b, c );
		var points = new[] { a, b, c };
		var keepFront = options?.KeepFront ?? true;
		var split = MapClip.Clip( brush, plane, new MapClipOptions { Mode = ClipMode.Split, KeepFront = keepFront } );
		if ( split.Outcome == ClipOutcome.RejectedInvalidBrush )
			return new ThreePointClipPreview( plane, points, 0, 0, 0, 0, 0, split.Outcome, split.Reason );
		double front;
		double back;
		if ( split.Outcome == ClipOutcome.Split )
		{
			front = split.Kept.Volume;
			back = split.Second.Volume;
		}
		else
		{
			// NoOpPlaneOutside (or a graze with no cut area): the brush lies entirely on
			// one side; the centroid says which.
			if ( plane.Distance( brush.Centroid ) >= 0f )
			{
				front = brush.Volume;
				back = 0;
			}
			else
			{
				front = 0;
				back = brush.Volume;
			}
		}
		var kept = keepFront ? front : back;
		var removed = keepFront ? back : front;
		var fraction = brush.Volume > 0 ? removed / brush.Volume : 0;
		return new ThreePointClipPreview( plane, points, front, back, kept, removed, fraction, split.Outcome, split.Reason );
	}

	/// <summary>
	/// Polygon tool: extrude an ordered 2D footprint into a convex brush of the given
	/// height. The footprint is drawn in the XY plane; the brush runs from
	/// <paramref name="baseZ"/> to baseZ + height. Every face gets
	/// <paramref name="materialName"/> (nodraw when blank). The input order may be
	/// clockwise or counter-clockwise; it is normalised to counter-clockwise
	/// internally, so the side faces' outward normals are always correct. A redundant
	/// point lying exactly on a straight edge is dropped before building (the solid
	/// is unchanged); without that, two faces would share one plane and the brush
	/// would be invalid.
	/// A non-convex, self-intersecting or degenerate footprint is NOT built: the
	/// result names the reason and carries the convex hull as a suggestion the caller
	/// may use explicitly — it is never substituted in silently. A non-positive or
	/// non-finite height throws <see cref="ArgumentOutOfRangeException"/> naming
	/// "polygon-height-must-be-positive".
	/// </summary>
	public static PolygonFootprintResult PolygonFootprint( IReadOnlyList<Vec2> points, float height,
		string materialName = null, float baseZ = 0f )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );
		if ( !float.IsFinite( height ) || !( height > 0f ) )
			throw new ArgumentOutOfRangeException( nameof( height ), height,
				"polygon-height-must-be-positive: a polygon footprint needs a finite height greater than 0." );
		if ( !float.IsFinite( baseZ ) )
			throw new ArgumentException( "polygon-base-z-must-be-finite: the footprint base height must be a finite number.", nameof( baseZ ) );
		for ( var i = 0; i < points.Count; i++ )
			if ( !float.IsFinite( points[i].x ) || !float.IsFinite( points[i].y ) )
				throw new ArgumentException( $"polygon-point-not-finite: footprint point {i} has a non-finite coordinate.", nameof( points ) );

		var cleaned = CleanFootprint( points );
		var hull = ConvexHull( cleaned );
		var area = SignedArea( cleaned );

		if ( cleaned.Count < 3 )
			return new PolygonFootprintResult( PolygonFootprintOutcome.RejectedDegenerate, null,
				$"polygon-degenerate: a footprint needs at least 3 distinct points, got {cleaned.Count}.",
				hull, 0 );
		// Zero area alone does NOT mean degenerate: a bowtie (two lobes cancelling
		// in the shoelace sum) has exactly zero net area and is self-intersecting,
		// not collinear. Degenerate here means all points on one line (every turn
		// ~0); anything else with no area falls through to the self-intersection
		// check below, which is what names it.
		if ( AllTurnsZero( cleaned ) )
			return new PolygonFootprintResult( PolygonFootprintOutcome.RejectedDegenerate, null,
				"polygon-degenerate: zero-area footprint (all points collinear).",
				hull, 0 );

		var crossing = FindSelfIntersection( cleaned );
		if ( crossing.HasValue )
			return new PolygonFootprintResult( PolygonFootprintOutcome.RejectedSelfIntersecting, null,
				$"polygon-self-intersecting: edges {crossing.Value.EdgeA} and {crossing.Value.EdgeB} cross: a footprint must be a simple polygon.",
				hull, Math.Abs( area ) );

		var concaveAt = FindConcaveVertex( cleaned );
		if ( concaveAt >= 0 )
		{
			var turn = TurnSign( cleaned, concaveAt );
			var majority = MajorityTurnSign( cleaned );
			return new PolygonFootprintResult( PolygonFootprintOutcome.RejectedNonConvex, null,
				$"polygon-non-convex: vertex {concaveAt} turns {( turn > 0 ? "left" : "right" )} while the footprint turns {( majority > 0 ? "left" : "right" )}: a brush footprint must be convex.",
				hull, Math.Abs( area ) );
		}

		var ccw = area > 0 ? cleaned : cleaned.AsEnumerable().Reverse().ToList();
		// A point lying exactly on a straight edge (a redundant mid-edge vertex) would
		// emit a second face on the same plane as its neighbour, and MapBrush.Validate
		// rightly reports that as a duplicate/coplanar-split face. The footprint is
		// still convex, so drop such vertices before building — the solid is unchanged.
		var simplified = DropCollinearVertices( ccw );
		var faces = new List<global::Effigy.MapBrushFace>( simplified.Count + 2 );
		for ( var i = 0; i < simplified.Count; i++ )
		{
			var p = simplified[i];
			var q = simplified[( i + 1 ) % simplified.Count];
			var edge = q - p;
			// Counter-clockwise footprint: the interior is left of each edge, so the
			// outward normal is the right normal (edge.y, -edge.x), normalised.
			var n2 = new Vec2( edge.y, -edge.x ).Normal;
			var n = new Vec3( n2.x, n2.y, 0 );
			faces.Add( new global::Effigy.MapBrushFace( n, Vec3.Dot( n, new Vec3( p.x, p.y, 0 ) ), materialName ) );
		}
		faces.Add( new global::Effigy.MapBrushFace( new Vec3( 0, 0, -1 ), -baseZ, materialName ) );
		faces.Add( new global::Effigy.MapBrushFace( new Vec3( 0, 0, 1 ), baseZ + height, materialName ) );

		var brush = MapBrush.FromPlanes( faces );
		if ( !brush.IsValid )
			throw new InvalidOperationException(
				$"ClipPolygon built an invalid polygon brush (this is a generator bug, not a caller error): {string.Join( "; ", brush.Validate().Select( d => d.Code ) )}" );
		return new PolygonFootprintResult( PolygonFootprintOutcome.Created, brush, null, Array.Empty<Vec2>(), Math.Abs( area ) );
	}

	/// <summary>
	/// Polygon tool from 3D points: every point must lie on one horizontal plane (same
	/// Z within 1e-4), which becomes the footprint base; the brush extrudes upward by
	/// <paramref name="height"/>. A non-coplanar set throws
	/// <see cref="ArgumentException"/> naming "polygon-points-not-coplanar".
	/// </summary>
	public static PolygonFootprintResult PolygonFootprint( IReadOnlyList<Vec3> points, float height, string materialName = null )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );
		if ( points.Count == 0 )
			return PolygonFootprint( Array.Empty<Vec2>(), height, materialName, 0f );
		var baseZ = points[0].z;
		for ( var i = 1; i < points.Count; i++ )
			if ( Math.Abs( points[i].z - baseZ ) > 1e-4f )
				throw new ArgumentException(
					$"polygon-points-not-coplanar: footprint point {i} is at z={points[i].z}, but the footprint plane is z={baseZ}; draw the polygon in one 2D view.",
					nameof( points ) );
		return PolygonFootprint( points.Select( p => new Vec2( p.x, p.y ) ).ToList(), height, materialName, baseZ );
	}

	/// <summary>
	/// Convex hull of a 2D point set (Andrew's monotone chain), counter-clockwise,
	/// without a duplicated closing point. Collinear points on a hull edge are dropped,
	/// so the hull is the minimal vertex set. Fewer than 3 distinct points come back
	/// deduplicated as given. This is the suggestion a rejected footprint carries: the
	/// caller may feed it back into <see cref="PolygonFootprint"/> explicitly.
	/// </summary>
	public static IReadOnlyList<Vec2> ConvexHull( IReadOnlyList<Vec2> points )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );
		var sorted = CleanFootprint( points );
		sorted.Sort( ( a, b ) => a.x != b.x ? a.x.CompareTo( b.x ) : a.y.CompareTo( b.y ) );
		if ( sorted.Count < 3 )
			return sorted;
		var scale = 0.0;
		for ( var i = 0; i < sorted.Count; i++ )
		{
			var e = sorted[( i + 1 ) % sorted.Count] - sorted[i];
			scale = Math.Max( scale, e.LengthSquared );
		}
		var eps = 1e-9 * Math.Max( 1.0, scale );
		var lower = new List<Vec2>();
		foreach ( var p in sorted )
		{
			while ( lower.Count >= 2 && Cross( lower[lower.Count - 2], lower[lower.Count - 1], p ) <= eps )
				lower.RemoveAt( lower.Count - 1 );
			lower.Add( p );
		}
		var upper = new List<Vec2>();
		for ( var i = sorted.Count - 1; i >= 0; i-- )
		{
			var p = sorted[i];
			while ( upper.Count >= 2 && Cross( upper[upper.Count - 2], upper[upper.Count - 1], p ) <= eps )
				upper.RemoveAt( upper.Count - 1 );
			upper.Add( p );
		}
		// The last point of each chain is the first of the other; drop both.
		lower.RemoveAt( lower.Count - 1 );
		upper.RemoveAt( upper.Count - 1 );
		lower.AddRange( upper );
		return lower;
	}

	/// <summary>Footprint area of an ordered 2D point set (shoelace), always non-negative; 0 for fewer than 3 points.</summary>
	public static double FootprintArea( IReadOnlyList<Vec2> points )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );
		return Math.Abs( SignedArea( CleanFootprint( points ) ) );
	}

	/// <summary>
	/// Drop vertices that lie on the straight edge between their neighbours (the turn
	/// at them is ~0 under the same epsilon as <see cref="TurnSign"/>). A footprint
	/// with a redundant mid-edge point is still convex and still builds; without this
	/// the builder would emit two faces on one plane and MapBrush would reject the
	/// brush. Returns the input unchanged when fewer than 3 vertices would remain
	/// (cannot happen for a positive-area footprint; defensive only).
	/// </summary>
	static List<Vec2> DropCollinearVertices( IReadOnlyList<Vec2> points )
	{
		var scale = TurnScale( points );
		var eps = 1e-9 * scale;
		var result = new List<Vec2>( points.Count );
		for ( var i = 0; i < points.Count; i++ )
		{
			var prev = points[( i + points.Count - 1 ) % points.Count];
			var cur = points[i];
			var next = points[( i + 1 ) % points.Count];
			if ( Math.Abs( Cross( prev, cur, next ) ) > eps )
				result.Add( cur );
		}
		return result.Count >= 3 ? result : points.ToList();
	}

	/// <summary>Drop a duplicated closing point and consecutive duplicates (exact float equality); keep the order.</summary>
	static List<Vec2> CleanFootprint( IReadOnlyList<Vec2> points )
	{
		var result = new List<Vec2>( points.Count );
		foreach ( var p in points )
			if ( result.Count == 0 || p.x != result[result.Count - 1].x || p.y != result[result.Count - 1].y )
				result.Add( p );
		if ( result.Count > 1 && result[0].x == result[result.Count - 1].x && result[0].y == result[result.Count - 1].y )
			result.RemoveAt( result.Count - 1 );
		return result;
	}

	/// <summary>Signed shoelace area (double precision); positive for counter-clockwise order.</summary>
	static double SignedArea( IReadOnlyList<Vec2> points )
	{
		var sum = 0.0;
		for ( var i = 0; i < points.Count; i++ )
		{
			var a = points[i];
			var b = points[( i + 1 ) % points.Count];
			sum += (double)a.x * b.y - (double)b.x * a.y;
		}
		return sum * 0.5;
	}

	/// <summary>2D cross product (b - a) x (c - a): the signed turn at b. Double precision.</summary>
	static double Cross( Vec2 a, Vec2 b, Vec2 c )
		=> (double)( b.x - a.x ) * ( c.y - a.y ) - (double)( b.y - a.y ) * ( c.x - a.x );

	/// <summary>True when every turn of the footprint is ~0: all points lie on one line (a zero-area footprint).</summary>
	static bool AllTurnsZero( IReadOnlyList<Vec2> points )
	{
		for ( var i = 0; i < points.Count; i++ )
			if ( TurnSign( points, i ) != 0 )
				return false;
		return true;
	}

	/// <summary>Turn sign at vertex i (+1 left, -1 right, 0 collinear) under the turn epsilon.</summary>
	static int TurnSign( IReadOnlyList<Vec2> points, int i )
	{
		var n = points.Count;
		var cross = Cross( points[i], points[( i + 1 ) % n], points[( i + 2 ) % n] );
		var scale = TurnScale( points );
		if ( cross > 1e-9 * scale ) return 1;
		if ( cross < -1e-9 * scale ) return -1;
		return 0;
	}

	static double TurnScale( IReadOnlyList<Vec2> points )
	{
		var scale = 1.0;
		for ( var i = 0; i < points.Count; i++ )
		{
			var e = points[( i + 1 ) % points.Count] - points[i];
			scale = Math.Max( scale, e.LengthSquared );
		}
		return scale;
	}

	/// <summary>Sign of the majority turn of the footprint (the winding): +1 CCW, -1 CW, 0 when no turn clears the epsilon.</summary>
	static int MajorityTurnSign( IReadOnlyList<Vec2> points )
	{
		var positive = 0;
		var negative = 0;
		for ( var i = 0; i < points.Count; i++ )
		{
			var sign = TurnSign( points, i );
			if ( sign > 0 ) positive++;
			else if ( sign < 0 ) negative++;
		}
		return positive >= negative ? 1 : -1;
	}

	/// <summary>First vertex whose turn disagrees with the majority winding, or -1 when the footprint is convex (collinear runs are not turns).</summary>
	static int FindConcaveVertex( IReadOnlyList<Vec2> points )
	{
		var majority = MajorityTurnSign( points );
		for ( var i = 0; i < points.Count; i++ )
		{
			var sign = TurnSign( points, i );
			if ( sign != 0 && sign != majority )
				return ( i + 1 ) % points.Count;
		}
		return -1;
	}

	/// <summary>First pair of non-adjacent edges that cross or overlap, or null when the footprint is a simple polygon.</summary>
	static (int EdgeA, int EdgeB)? FindSelfIntersection( IReadOnlyList<Vec2> points )
	{
		var n = points.Count;
		for ( var i = 0; i < n; i++ )
			for ( var j = i + 1; j < n; j++ )
			{
				// Adjacent edges share a vertex by construction, including the wrap pair.
				if ( j == i + 1 || ( i == 0 && j == n - 1 ) )
					continue;
				if ( SegmentsCrossOrOverlap( points[i], points[( i + 1 ) % n], points[j], points[( j + 1 ) % n] ) )
					return (i, j);
			}
		return null;
	}

	static bool SegmentsCrossOrOverlap( Vec2 a, Vec2 b, Vec2 c, Vec2 d )
	{
		var scale = 1.0;
		foreach ( var e in new[] { b - a, d - c, c - a } )
			scale = Math.Max( scale, e.LengthSquared );
		var eps = 1e-9 * scale;
		var d1 = Cross( c, d, a );
		var d2 = Cross( c, d, b );
		var d3 = Cross( a, b, c );
		var d4 = Cross( a, b, d );
		if ( ( ( d1 > eps && d2 < -eps ) || ( d1 < -eps && d2 > eps ) )
			&& ( ( d3 > eps && d4 < -eps ) || ( d3 < -eps && d4 > eps ) ) )
			return true;
		// Collinear overlap (a spike that runs back along an earlier edge): a vertex of
		// one segment lying strictly inside the other, or the segments sharing more
		// than an endpoint.
		if ( Math.Abs( d1 ) <= eps && OnSegment( c, d, a ) ) return true;
		if ( Math.Abs( d2 ) <= eps && OnSegment( c, d, b ) ) return true;
		if ( Math.Abs( d3 ) <= eps && OnSegment( a, b, c ) ) return true;
		if ( Math.Abs( d4 ) <= eps && OnSegment( a, b, d ) ) return true;
		return false;
	}

	static bool OnSegment( Vec2 a, Vec2 b, Vec2 p )
		=> p.x >= Math.Min( a.x, b.x ) - 1e-6f && p.x <= Math.Max( a.x, b.x ) + 1e-6f
			&& p.y >= Math.Min( a.y, b.y ) - 1e-6f && p.y <= Math.Max( a.y, b.y ) + 1e-6f;
}

/// <summary>Preview of a 3-point clip: what the Hammer++ red highlight would show, without cutting anything.</summary>
public readonly struct ThreePointClipPreview
{
	/// <summary>The clip plane through the three picked points.</summary>
	public readonly global::Effigy.ClipPlane Plane;
	/// <summary>The three picked points, in the order given.</summary>
	public readonly IReadOnlyList<Vec3> Points;
	/// <summary>Volume on the side the plane normal points to.</summary>
	public readonly double FrontVolume;
	/// <summary>Volume on the opposite side.</summary>
	public readonly double BackVolume;
	/// <summary>Volume that survives the cut (front when KeepFront, else back).</summary>
	public readonly double KeptVolume;
	/// <summary>Volume that would be cut away (back when KeepFront, else front).</summary>
	public readonly double RemovedVolume;
	/// <summary>RemovedVolume / brush volume, 0 when the brush has no volume.</summary>
	public readonly double RemovedFraction;
	/// <summary>Split when the plane cuts the brush; NoOpPlaneOutside when it misses it; RejectedInvalidBrush when the brush is invalid.</summary>
	public readonly ClipOutcome Outcome;
	/// <summary>Named reason for a no-op or rejection; null on a real cut preview.</summary>
	public readonly string Reason;

	internal ThreePointClipPreview( global::Effigy.ClipPlane plane, IReadOnlyList<Vec3> points,
		double frontVolume, double backVolume, double keptVolume, double removedVolume,
		double removedFraction, ClipOutcome outcome, string reason )
	{
		Plane = plane;
		Points = points;
		FrontVolume = frontVolume;
		BackVolume = backVolume;
		KeptVolume = keptVolume;
		RemovedVolume = removedVolume;
		RemovedFraction = removedFraction;
		Outcome = outcome;
		Reason = reason;
	}
}

/// <summary>Named outcome of a polygon-footprint build.</summary>
public enum PolygonFootprintOutcome
{
	/// <summary>The footprint was convex and simple: <see cref="PolygonFootprintResult.Brush"/> holds the extruded brush.</summary>
	Created,
	/// <summary>The footprint turns both ways: rejected, with the convex hull as a suggestion.</summary>
	RejectedNonConvex,
	/// <summary>Two non-adjacent edges cross or overlap: rejected, with the convex hull as a suggestion.</summary>
	RejectedSelfIntersecting,
	/// <summary>Fewer than 3 distinct points, or zero area: rejected, with the hull (if any) as a suggestion.</summary>
	RejectedDegenerate,
}

/// <summary>
/// Result of <see cref="ClipPolygon.PolygonFootprint(IReadOnlyList{Vec2}, float, string, float)"/>:
/// the outcome, the built brush on success, and — on rejection — a named reason plus the
/// convex hull as a suggestion. The hull is NEVER substituted for the input: the caller
/// must feed it back into PolygonFootprint explicitly if that is what the mapper wants.
/// </summary>
public sealed class PolygonFootprintResult
{
	public PolygonFootprintOutcome Outcome { get; }
	/// <summary>The extruded convex brush. Null unless <see cref="Outcome"/> is Created.</summary>
	public MapBrush Brush { get; }
	/// <summary>Named reason for a rejection; null when the brush was created.</summary>
	public string Reason { get; }
	/// <summary>Convex hull of the input points (counter-clockwise), as a suggestion on rejection; empty when the brush was created.</summary>
	public IReadOnlyList<Vec2> SuggestedHull { get; }
	/// <summary>Footprint area in square inches (0 when the input has no area).</summary>
	public double FootprintArea { get; }

	internal PolygonFootprintResult( PolygonFootprintOutcome outcome, MapBrush brush, string reason,
		IReadOnlyList<Vec2> suggestedHull, double footprintArea )
	{
		Outcome = outcome;
		Brush = brush;
		Reason = reason;
		SuggestedHull = suggestedHull;
		FootprintArea = footprintArea;
	}
}
