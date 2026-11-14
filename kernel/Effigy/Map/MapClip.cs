using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// 2-point Clip tool for plane-based convex brushes (Hammer Desktop P1: hammer-brush-clip-two-point).
///
/// Hammer's Clip tool: the mapper draws a line across the selected brushes in a 2D view; the
/// white side stays, the red side is cut; Enter cuts, or splits the brush into two. This is the
/// backend for that: given a clip plane built from 2 points + a view axis and a <see cref="MapBrush"/>,
/// return the kept brush(es) — cut (one side) or split (both sides as two brushes).
///
/// MECHANISM (plane classification, the idea reused from <see cref="ExactMeshBoolean"/> — not its
/// internals, and not its mesh BSP: a plane-based convex brush needs no BSP). A convex brush IS
/// the intersection of its faces' half-spaces, so clipping by one more half-space is exact and
/// cheap: classify every brush vertex by its double-precision signed distance to the clip plane
/// (same epsilon discipline as ExactMeshBoolean's SplitPolygon: on-plane vertices belong to both
/// sides, straddling edges interpolate the crossing once), drop faces whose kept area is
/// degenerate, keep every surviving face on its ORIGINAL plane (so its material and UVs survive
/// untouched), and add one cap face on the clip plane. No polygon ever needs re-triangulating,
/// and no face is ever re-fit: the planes are the brush. Face polygons and vertices come from
/// the brush itself (<see cref="MapBrush.FaceVertices(int)"/> / <see cref="MapBrush.Vertices"/> —
/// the plane-intersection vertex solve the core already does), not from a second solve here.
///
/// CONVENTIONS. Geometry is kernel inches, Z-up. A face plane is Dot(Normal, p) = Distance with
/// an outward unit normal; the solid is Normal.p &lt;= Distance. The clip plane reuses the
/// kernel's <see cref="ClipPlane"/> (normal + offset, keep side = Distance &gt;= 0).
/// <see cref="MapClip.PlaneFromTwoPoints"/> builds it from the 2 drawn points and the view axis:
/// the plane contains the drawn line and runs parallel to the view axis, with normal =
/// normalize(cross(line, axis)); the kept ("white") side is the side the normal points to, and
/// <see cref="MapClipOptions.KeepFront"/> flips it. The new cut face gets "tools/toolsnodraw"
/// by default, or the caller's material when <see cref="MapClipOptions.CutFaceNoDraw"/> is false
/// (see <see cref="MapClipOptions"/>).
///
/// API (landed). Built against <see cref="MapBrush"/> as landed by hammer-brush-core
/// (kernel/Effigy/Map/MapBrush.cs): namespace Effigy; faces are <see cref="MapBrushFace"/>
/// (Normal, Distance, Material as <see cref="FaceMaterial"/>, MaterialId string,
/// Uv as <see cref="FaceUv"/>); MapBrush.FromPlanes(IEnumerable&lt;MapBrushFace&gt;),
/// Faces / FaceCount / Vertices / FaceVertices(i), Volume (float), Centroid, IsValid,
/// Validate() returning IReadOnlyList&lt;MapBrushDefect&gt; (Code/Message/FaceIndex).
/// NOTE for the integrator: kernel/Effigy/Map/MapBrushPrimitives.cs still assumes a different
/// face surface (MapFace/MapPlane, int material slot) that does not exist in the landed API;
/// that file is its owner's ticket to reconcile, not this one.
///
/// NO UI. Backend / data-model / API only; the Clip tool's panels and viewport handles are
/// deferred to a UI ticket.
/// </summary>
public enum ClipViewAxis
{
	/// <summary>The 2D view looks along +X (the YZ / side view).</summary>
	X,
	/// <summary>The 2D view looks along +Y (the XZ / front view).</summary>
	Y,
	/// <summary>The 2D view looks along +Z (the XY / top view).</summary>
	Z,
}

/// <summary>Cut keeps one side of the clip plane; Split keeps both sides as two brushes.</summary>
public enum ClipMode
{
	Cut,
	Split,
}

/// <summary>
/// Named outcome of a clip. The plane-outside cases are named results, not silent no-ops:
/// a caller (and the UI, later) can tell "nothing was cut because the plane missed the brush"
/// from "the whole brush was on the red side".
/// </summary>
public enum ClipOutcome
{
	/// <summary>One side kept; the other side was cut away.</summary>
	Cut,
	/// <summary>Both sides kept as two brushes (<see cref="MapClipResult.Kept"/> = front, <see cref="MapClipResult.Second"/> = back).</summary>
	Split,
	/// <summary>The clip plane misses the brush (or, in Split mode, the brush lies entirely on one side): nothing changed.</summary>
	NoOpPlaneOutside,
	/// <summary>The clip plane coincides with an existing face plane: nothing changed.</summary>
	NoOpPlaneCoplanar,
	/// <summary>Cut mode, and the whole brush lies on the cut-away side: the brush is gone (<see cref="MapClipResult.Kept"/> is null).</summary>
	WholeBrushCutAway,
	/// <summary>The input brush failed <see cref="MapBrush.Validate"/>; <see cref="MapClipResult.Reason"/> names the defect codes.</summary>
	RejectedInvalidBrush,
}

/// <summary>Caller choices for a clip. Defaults are the Hammer-safe ones: Cut, keep the white (front) side, nodraw cut face.</summary>
public sealed class MapClipOptions
{
	public ClipMode Mode { get; set; } = ClipMode.Cut;
	/// <summary>True = keep the side the clip plane's normal points to (the white side); false = keep the other side.</summary>
	public bool KeepFront { get; set; } = true;
	/// <summary>True = the new cut face is "tools/toolsnodraw". False = it takes <see cref="CutFaceMaterial"/>, or the brush's first face material when that is null.</summary>
	public bool CutFaceNoDraw { get; set; } = true;
	/// <summary>Material name for the new cut face when <see cref="CutFaceNoDraw"/> is false; null = the brush's first face material.</summary>
	public string CutFaceMaterial { get; set; }
	/// <summary>Lightmap scale for the new cut face (Source default 16).</summary>
	public float CutFaceLightmapScale { get; set; } = MapClip.DefaultLightmapScale;
}

/// <summary>Result of <see cref="MapClip.Clip"/>: the outcome, the kept brush(es), and a named reason for no-ops and rejections.</summary>
public sealed class MapClipResult
{
	public ClipOutcome Outcome { get; }
	/// <summary>Cut mode: the kept side. Split mode: the front side. Null when the whole brush was cut away.</summary>
	public MapBrush Kept { get; }
	/// <summary>Split mode only: the back side. Null otherwise.</summary>
	public MapBrush Second { get; }
	/// <summary>Named reason for <see cref="ClipOutcome.NoOpPlaneOutside"/>, <see cref="ClipOutcome.NoOpPlaneCoplanar"/>, <see cref="ClipOutcome.WholeBrushCutAway"/> and <see cref="ClipOutcome.RejectedInvalidBrush"/>; null on a real cut/split.</summary>
	public string Reason { get; }
	/// <summary>True when the brush set changed (a real cut/split, or a whole-brush cut-away).</summary>
	public bool Changed { get; }
	internal MapClipResult( ClipOutcome outcome, MapBrush kept, MapBrush second, string reason, bool changed )
	{
		Outcome = outcome;
		Kept = kept;
		Second = second;
		Reason = reason;
		Changed = changed;
	}
}

/// <summary>2-point Clip tool for <see cref="MapBrush"/>: cut or split a convex brush by a plane drawn as 2 points + a view axis.</summary>
public static class MapClip
{
	/// <summary>Material id for a fresh cut face when the caller asks for nodraw: the tree's canonical nodraw name (ToolTexture.NoDrawName).</summary>
	public const string NoDrawMaterial = "tools/toolsnodraw";
	/// <summary>Source default lightmap scale, used for a fresh cut face unless the caller overrides it.</summary>
	public const float DefaultLightmapScale = 16f;

	/// <summary>Plane-classification epsilon, in model units (inches). A vertex within this of the plane counts as on it.</summary>
	const double Eps = 1e-4;
	/// <summary>Minimum kept-face / cap area, in square inches. A face clipped down to less than this is degenerate and is dropped.</summary>
	const double AreaEps = 1e-4;

	/// <summary>
	/// Build the clip plane from the 2 points the mapper drew plus the view axis the line was
	/// drawn in. The plane contains the drawn line and runs parallel to the view axis; its
	/// normal is normalize(cross(line, axis)) and the kept ("white") side is the side the
	/// normal points to. Throws <see cref="ArgumentException"/> when the points coincide or
	/// the line runs parallel to the view axis (both named in the message).
	/// </summary>
	public static ClipPlane PlaneFromTwoPoints( Vec3 a, Vec3 b, ClipViewAxis viewAxis )
	{
		var line = b - a;
		if ( line.LengthSquared < 1e-12f )
			throw new ArgumentException( "clip-two-points-coincident: the two clip points must differ.", nameof( a ) );
		var axis = viewAxis switch
		{
			ClipViewAxis.X => new Vec3( 1, 0, 0 ),
			ClipViewAxis.Y => new Vec3( 0, 1, 0 ),
			_ => new Vec3( 0, 0, 1 ),
		};
		var normal = Vec3.Cross( line, axis );
		if ( normal.LengthSquared < 1e-12f )
			throw new ArgumentException( "clip-line-parallel-to-view-axis: the clip line must not run parallel to the view axis.", nameof( b ) );
		normal = normal.Normal;
		return new ClipPlane( normal, Vec3.Dot( normal, a ) );
	}

	/// <summary>Clip by a 2-point line drawn in a 2D view: builds the plane, then cuts/splits. See <see cref="Clip(MapBrush, ClipPlane, MapClipOptions)"/>.</summary>
	public static MapClipResult Clip( MapBrush brush, Vec3 a, Vec3 b, ClipViewAxis viewAxis, MapClipOptions options = null )
		=> Clip( brush, PlaneFromTwoPoints( a, b, viewAxis ), options );

	/// <summary>
	/// Clip <paramref name="brush"/> by <paramref name="plane"/>. Cut mode keeps one side
	/// (<see cref="MapClipOptions.KeepFront"/> picks which); Split mode returns both sides.
	/// Kept faces stay on their original planes, so material and UVs survive untouched; the
	/// new cut face gets nodraw or the caller's material per <paramref name="options"/>.
	/// A plane that misses the brush is a no-op with a named <see cref="MapClipResult.Reason"/> —
	/// never a silent success, never a silently emptied brush. An invalid input brush is
	/// rejected with its Validate() defect codes named. Throws only for a null brush; a kept brush
	/// that fails validation after a real cut is an internal error and throws
	/// <see cref="InvalidOperationException"/>.
	/// </summary>
	public static MapClipResult Clip( MapBrush brush, ClipPlane plane, MapClipOptions options = null )
	{
		if ( brush is null )
			throw new ArgumentNullException( nameof( brush ) );
		var opt = options ?? new MapClipOptions();

		if ( !brush.IsValid )
		{
			var defects = brush.Validate();
			var reason = defects is null || defects.Count == 0 ? "invalid-brush" : string.Join( "; ", defects.Select( d => d.Code ) );
			return new MapClipResult( ClipOutcome.RejectedInvalidBrush, null, null, "invalid-brush: " + reason, false );
		}

		var faces = brush.Faces;
		var polygons = FacePolygons( brush );
		if ( polygons is null )
			return new MapClipResult( ClipOutcome.RejectedInvalidBrush, null, null, "invalid-brush: face-without-polygon", false );

		// Coincident with an existing face plane: a convex brush lies entirely on one side
		// of its own face plane, so the cut would remove nothing.
		for ( var i = 0; i < faces.Count; i++ )
		{
			var dot = Vec3.Dot( faces[i].Normal, plane.Normal );
			if ( dot > 1 - 1e-6 && Math.Abs( faces[i].Distance - plane.Offset ) <= Eps )
				return new MapClipResult( ClipOutcome.NoOpPlaneCoplanar, brush, null, "clip-plane-coincident-with-face-" + i, false );
			if ( dot < -( 1 - 1e-6 ) && Math.Abs( faces[i].Distance + plane.Offset ) <= Eps )
				return new MapClipResult( ClipOutcome.NoOpPlaneCoplanar, brush, null, "clip-plane-coincident-with-face-" + i, false );
		}

		if ( opt.Mode == ClipMode.Split )
		{
			var (min, max) = MinMaxDistance( brush.Vertices, plane );
			if ( min >= -Eps || max <= Eps )
				return new MapClipResult( ClipOutcome.NoOpPlaneOutside, brush, null, "clip-plane-outside-brush: brush-entirely-on-one-side-of-plane", false );
			var front = BuildSide( brush, polygons, plane.Normal, plane.Offset, plane, opt );
			var back = BuildSide( brush, polygons, -plane.Normal, -plane.Offset, plane, opt );
			if ( front is null || back is null )
				return new MapClipResult( ClipOutcome.NoOpPlaneOutside, brush, null, "clip-plane-grazes-brush: no-cut-face-area", false );
			return new MapClipResult( ClipOutcome.Split, front, back, null, true );
		}

		var keepNormal = opt.KeepFront ? plane.Normal : -plane.Normal;
		var keepOffset = opt.KeepFront ? plane.Offset : -plane.Offset;
		var (sMin, sMax) = MinMaxDistance( brush.Vertices, keepNormal, keepOffset );
		if ( sMin >= -Eps )
			return new MapClipResult( ClipOutcome.NoOpPlaneOutside, brush, null, "clip-plane-outside-brush: brush-entirely-on-kept-side", false );
		if ( sMax <= Eps )
			return new MapClipResult( ClipOutcome.WholeBrushCutAway, null, null, "clip-plane-outside-brush: whole-brush-on-cut-side", true );
		var kept = BuildSide( brush, polygons, keepNormal, keepOffset, plane, opt );
		if ( kept is null )
			return new MapClipResult( ClipOutcome.NoOpPlaneOutside, brush, null, "clip-plane-grazes-brush: no-cut-face-area", false );
		return new MapClipResult( ClipOutcome.Cut, kept, null, null, true );
	}

	/// <summary>Material name for the new cut face: nodraw, or the caller's material / first face material.</summary>
	static string CapMaterialName( MapBrush brush, MapClipOptions opt )
	{
		if ( opt.CutFaceNoDraw )
			return NoDrawMaterial;
		if ( !string.IsNullOrEmpty( opt.CutFaceMaterial ) )
			return opt.CutFaceMaterial;
		return brush.Faces.Count > 0 ? brush.Faces[0].MaterialId : NoDrawMaterial;
	}

	/// <summary>
	/// Build one side of a clip: the half-space Dot(keepNormal, p) &gt;= keepOffset. Faces whose
	/// kept area is degenerate are dropped; every surviving face is the ORIGINAL face struct,
	/// so its material and UVs survive untouched; one cap face on the clip plane closes the
	/// solid. Returns null when the plane produces no cut-face area (a graze).
	/// </summary>
	static MapBrush BuildSide( MapBrush brush, List<IReadOnlyList<Vec3>> polygons, Vec3 keepNormal, float keepOffset, ClipPlane plane, MapClipOptions opt )
	{
		var faces = brush.Faces;
		var kept = new List<MapBrushFace>();
		for ( var i = 0; i < faces.Count; i++ )
		{
			var clipped = DedupeNear( ClipPolygonToHalfSpace( polygons[i], keepNormal, keepOffset ) );
			if ( clipped.Count < 3 )
				continue;
			if ( PolygonArea( clipped, faces[i].Normal ) <= AreaEps )
				continue;
			kept.Add( faces[i] );
		}

		var crossings = DedupeNear( CapCrossings( polygons, plane ) );
		if ( crossings.Count < 3 )
			return null;
		var capNormal = -keepNormal;
		var ordered = OrderPolygon( crossings, capNormal );
		if ( PolygonArea( ordered, capNormal ) <= AreaEps )
			return null;

		kept.Add( new MapBrushFace( capNormal, -keepOffset, CapMaterialName( brush, opt ),
			new FaceUv( Vec2.Zero, new Vec2( 1, 1 ), 0f, opt.CutFaceLightmapScale, 0 ) ) );
		var result = MapBrush.FromPlanes( kept );
		if ( !result.IsValid || result.Volume <= 0 )
			throw new InvalidOperationException( $"MapClip built an invalid kept brush (valid={result.IsValid}, volume={result.Volume}); the clip plane and brush were accepted, so this is an internal error." );
		return result;
	}

	/// <summary>Double-precision signed distance to a plane: positive on the kept side.</summary>
	static double SignedDistance( ClipPlane plane, Vec3 p )
		=> (double)plane.Normal.x * p.x + (double)plane.Normal.y * p.y + (double)plane.Normal.z * p.z - plane.Offset;

	/// <summary>Double-precision signed distance to the half-space Dot(n, p) &gt;= w.</summary>
	static double SignedDistance( Vec3 n, float w, Vec3 p )
		=> (double)n.x * p.x + (double)n.y * p.y + (double)n.z * p.z - w;

	static (double Min, double Max) MinMaxDistance( IReadOnlyList<Vec3> points, ClipPlane plane )
	{
		var min = double.MaxValue;
		var max = double.MinValue;
		foreach ( var p in points )
		{
			var d = SignedDistance( plane, p );
			if ( d < min ) min = d;
			if ( d > max ) max = d;
		}
		return ( min, max );
	}

	static (double Min, double Max) MinMaxDistance( IReadOnlyList<Vec3> points, Vec3 normal, float offset )
	{
		var min = double.MaxValue;
		var max = double.MinValue;
		foreach ( var p in points )
		{
			var d = SignedDistance( normal, offset, p );
			if ( d < min ) min = d;
			if ( d > max ) max = d;
		}
		return ( min, max );
	}

	/// <summary>
	/// Every face's polygon from the brush itself (MapBrush solves its vertices from the
	/// planes, so there is exactly one vertex solve and no drift). Returns null when any
	/// face yields fewer than 3 vertices — a degenerate brush its Validate should already
	/// have rejected, so this is a defensive guard, not a normal path.
	/// </summary>
	static List<IReadOnlyList<Vec3>> FacePolygons( MapBrush brush )
	{
		var result = new List<IReadOnlyList<Vec3>>( brush.FaceCount );
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			var poly = brush.FaceVertices( i );
			if ( poly is null || poly.Count < 3 )
				return null;
			result.Add( poly );
		}
		return result;
	}

	static bool ContainsNear( List<Vec3> points, Vec3 p )
	{
		foreach ( var q in points )
			if ( ( p - q ).LengthSquared < 1e-6f )
				return true;
		return false;
	}

	static List<Vec3> DedupeNear( List<Vec3> points )
	{
		var result = new List<Vec3>();
		foreach ( var p in points )
			if ( !ContainsNear( result, p ) )
				result.Add( p );
		return result;
	}

	/// <summary>Order points counter-clockwise seen from the tip of <paramref name="normal"/> (from outside the face).</summary>
	static List<Vec3> OrderPolygon( List<Vec3> points, Vec3 normal )
	{
		var c = Vec3.Zero;
		foreach ( var p in points )
			c += p;
		c /= points.Count;
		var axis = Math.Abs( normal.z ) < 0.9 ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 );
		var u = Vec3.Cross( normal, axis ).Normal;
		var v = Vec3.Cross( normal, u );
		var ordered = new List<Vec3>( points );
		ordered.Sort( ( p, q ) =>
		{
			var ap = Math.Atan2( Vec3.Dot( p - c, v ), Vec3.Dot( p - c, u ) );
			var aq = Math.Atan2( Vec3.Dot( q - c, v ), Vec3.Dot( q - c, u ) );
			return ap.CompareTo( aq );
		} );
		return ordered;
	}

	/// <summary>
	/// Sutherland–Hodgman clip of one face polygon against the half-space Dot(n, p) &gt;= w.
	/// Same classification idea as ExactMeshBoolean's SplitPolygon, written for this file: a
	/// vertex within Eps of the plane counts as on it and belongs to the kept side; an edge
	/// that straddles emits one interpolated crossing. On-plane vertices are never dropped,
	/// so a seam vertex shared by two faces survives in both.
	/// </summary>
	static List<Vec3> ClipPolygonToHalfSpace( IReadOnlyList<Vec3> poly, Vec3 normal, float offset )
	{
		var result = new List<Vec3>();
		var count = poly.Count;
		for ( var i = 0; i < count; i++ )
		{
			var j = ( i + 1 ) % count;
			var vi = poly[i];
			var vj = poly[j];
			var si = SignedDistance( normal, offset, vi );
			var sj = SignedDistance( normal, offset, vj );
			var inI = si >= -Eps;
			var inJ = sj >= -Eps;
			if ( inI )
				result.Add( vi );
			if ( inI != inJ )
			{
				var denom = si - sj;
				var t = Math.Abs( denom ) < 1e-12 ? 0.0 : si / denom;
				if ( t >= 0.0 && t <= 1.0 )
					result.Add( Vec3.Lerp( vi, vj, (float)t ) );
			}
		}
		return result;
	}

	/// <summary>Polygon area by Newell's method, projected on the face normal. Always non-negative.</summary>
	static double PolygonArea( IReadOnlyList<Vec3> poly, Vec3 normal )
	{
		var sx = 0.0;
		var sy = 0.0;
		var sz = 0.0;
		for ( var i = 0; i < poly.Count; i++ )
		{
			var a = poly[i];
			var b = poly[( i + 1 ) % poly.Count];
			var cr = Vec3.Cross( a, b );
			sx += cr.x;
			sy += cr.y;
			sz += cr.z;
		}
		return Math.Abs( 0.5 * ( sx * normal.x + sy * normal.y + sz * normal.z ) );
	}

	/// <summary>
	/// The cap polygon's vertices: every point where the brush meets the clip plane — each
	/// strict edge crossing, plus each brush vertex lying on the plane. The on-plane vertices
	/// matter: a clip plane through brush corners (a diagonal through a box) meets the brush
	/// ONLY at its vertices, and strict edge crossings alone would find nothing. Each point
	/// is found several times (once per face sharing the edge/vertex); DedupeNear collapses
	/// them, and the convex ordering in BuildSide turns the set into the cap polygon.
	/// </summary>
	static List<Vec3> CapCrossings( List<IReadOnlyList<Vec3>> polygons, ClipPlane plane )
	{
		var points = new List<Vec3>();
		foreach ( var poly in polygons )
		{
			for ( var i = 0; i < poly.Count; i++ )
			{
				var a = poly[i];
				var b = poly[( i + 1 ) % poly.Count];
				var da = SignedDistance( plane, a );
				var db = SignedDistance( plane, b );
				if ( Math.Abs( da ) <= Eps )
					points.Add( a );
				if ( ( da < -Eps && db > Eps ) || ( da > Eps && db < -Eps ) )
				{
					var t = da / ( da - db );
					points.Add( Vec3.Lerp( a, b, (float)t ) );
				}
			}
		}
		return points;
	}
}
