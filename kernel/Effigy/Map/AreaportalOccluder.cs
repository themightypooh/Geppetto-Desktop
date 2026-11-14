// Areaportal + occluder helpers (hammer-optimisation-areaportal-occluder, worker
// muse-3-desktop-hammer-51). Native Geppetto map data + visibility rules, not Hammer
// entities: no Hammer app, no shell-out to Hammer/Hammer++/VBSP/VVIS/VRAD, no UI.
//
// SOURCE (Report §1.5): func_areaportal — a doorway/window that, when closed, hides
// everything behind it; func_occluder — a curtain, props fully behind it are not
// rendered (few, large occluders only).
//
// DEPENDENCIES, as landed on 2026-11-13 (read, not assumed):
//   * PortalGraph / BspBuilder (hammer-bsp-core-tree-portals-leaves, DONE):
//     kernel/Effigy/Map/BspBuilder.cs + PortalGraph.cs, namespace Effigy. A portal
//     is the shared face between two empty leaves; a leaf's PVS is flood-fill
//     reachability through portals; BspTree.CanSee / VisLeafAt / ExportVisLeaves
//     are the public read API. This file does NOT edit those files (read-only):
//     it recomputes a PVS over the same public data with closed areaportals
//     removed from the adjacency, so a closed portal is never traversed.
//   * MapEntities (hammer-entity-place-edit-validate, DONE):
//     kernel/Effigy/Map/MapEntities.cs, namespace Effigy. The core registry
//     (registry/core-source1.json, 14 classes) has NO func_areaportal and NO
//     func_occluder class, so areaportals/occluders are NOT placed entities:
//     they are native data in this file, keyed to a BspPortal (leaf pair) for an
//     areaportal and to a polygon for an occluder. Validation resolves the
//     areaportal's control targetname against a real MapEntities collection
//     (a func_door / logic_* / trigger_* that opens/closes it).
//
// An areaportal is identified by the leaf pair of the portal it gates
// (order-insensitive), so it survives a rebuild: the same doorway builds the
// same leaf pair. An occluder is one convex planar polygon (usually a quad);
// a prop (point, or sphere with a radius) is culled when the whole prop lies
// behind the polygon as seen from the viewer — the segment viewer→prop must
// cross the polygon, the crossing must sit in front of the prop's near
// surface, and for a radius the projected disc must fit inside the polygon
// (conservative: the disc radius is the exact tangent-cone cross-section at
// the occluder plane, widened by 1/|cos| for the plane's tilt to the view
// ray, so a prop that peeks past an edge is never culled).
//
// Units are the kernel's inches, Z-up (CONTRACTS.md).

using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Map;

/// <summary>Named error from the areaportal/occluder layer. <see cref="Code"/> is the
/// stable machine-readable name; construction rejects geometrically invalid
/// occluders, validation findings are returned as issues instead.</summary>
public sealed class AreaportalOccluderException : Exception
{
	public const string CodeInvalidOccluderPolygon = "InvalidOccluderPolygon";
	public const string CodeInvalidAreaportal = "InvalidAreaportal";

	public string Code = "";

	public AreaportalOccluderException( string code, string message ) : base( message ) { Code = code; }
}

/// <summary>Areaportal state: open lets visibility through, closed blocks it.</summary>
public enum AreaportalState
{
	Open,
	Closed,
}

/// <summary>
/// One areaportal: the gate on one <see cref="BspPortal"/> of a built tree,
/// identified by the portal's leaf pair (order-insensitive), with the state a
/// door/logic control drives and the targetname of that control.
/// <see cref="ControlTarget"/> is the targetname of the entity that opens/closes
/// it (a func_door, func_button, logic_* or trigger_* in <see cref="MapEntities"/>);
/// an areaportal with no control can never change state, so validation flags it.
/// </summary>
public sealed class Areaportal
{
	public string Name { get; }
	/// <summary>Leaf index on one side of the gated portal.</summary>
	public int LeafA { get; }
	/// <summary>Leaf index on the other side of the gated portal.</summary>
	public int LeafB { get; }
	/// <summary>Current state. Mutating it and recomputing the PVS flips visibility.</summary>
	public AreaportalState State { get; set; }
	/// <summary>Targetname of the controlling entity (func_door / logic_* / ...), or "".</summary>
	public string ControlTarget { get; }

	public bool IsClosed => State == AreaportalState.Closed;

	public Areaportal( string name, int leafA, int leafB, AreaportalState state = AreaportalState.Open, string controlTarget = "" )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidAreaportal,
				"An areaportal needs a name." );
		if ( leafA < 0 || leafB < 0 )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidAreaportal,
				$"Areaportal '{name}': leaf indices must be >= 0, got {leafA}/{leafB}." );
		if ( leafA == leafB )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidAreaportal,
				$"Areaportal '{name}': both sides are leaf {leafA} — a portal joins two different leaves." );
		Name = name;
		LeafA = leafA;
		LeafB = leafB;
		State = state;
		ControlTarget = controlTarget ?? "";
	}

	/// <summary>Areaportal gating this portal, open unless told otherwise.</summary>
	public static Areaportal ForPortal( BspPortal portal, string name, AreaportalState state = AreaportalState.Open, string controlTarget = "" )
	{
		if ( portal is null ) throw new ArgumentNullException( nameof( portal ) );
		return new Areaportal( name, portal.LeafA, portal.LeafB, state, controlTarget );
	}

	/// <summary>Order-insensitive leaf-pair key, so (a, b) and (b, a) are one portal.</summary>
	public static (int, int) PortalKey( int leafA, int leafB ) =>
		leafA < leafB ? (leafA, leafB) : (leafB, leafA);

	public (int, int) Key => PortalKey( LeafA, LeafB );

	/// <summary>True when this areaportal gates this portal (same leaf pair, either order).</summary>
	public bool Matches( BspPortal portal ) =>
		portal is not null && PortalKey( portal.LeafA, portal.LeafB ) == Key;

	public override string ToString() => $"{Name} ({LeafA}<->{LeafB}, {State}, control '{ControlTarget}')";
}

/// <summary>
/// Visibility through a <see cref="BspTree"/> with areaportals applied: the same
/// flood-fill reachability <see cref="PortalGraph.ComputePvs"/> computes, except a
/// portal gated by a CLOSED areaportal is never traversed. Open areaportals change
/// nothing; an areaportal whose leaf pair matches no portal in the tree is ignored
/// here (validation flags it — see <see cref="AreaportalOccluderValidator"/>).
/// Single-threaded and deterministic: the same tree + areaportals always give the
/// same rows, and the result equals <see cref="BspTree.CanSee"/> when every
/// areaportal is open.
/// </summary>
public static class AreaportalVisibility
{
	/// <summary>PVS rows (visleaf index → visible visleaf indices) with closed
	/// areaportals removed. Same shape as <see cref="PortalGraph.ComputePvs"/>.</summary>
	public static bool[][] ComputePvs( BspTree tree, IReadOnlyList<Areaportal>? areaportals )
	{
		if ( tree is null ) throw new ArgumentNullException( nameof( tree ) );
		var closed = ClosedKeys( areaportals );
		var adjacency = BuildAdjacency( tree, closed );
		var n = tree.VisLeafCount;
		var pvs = new bool[n][];
		for ( int i = 0; i < n; i++ )
		{
			var seen = new bool[n];
			var queue = new Queue<int>();
			seen[i] = true;
			queue.Enqueue( i );
			while ( queue.Count > 0 )
			{
				var cur = queue.Dequeue();
				foreach ( var nb in adjacency[cur] )
				{
					if ( seen[nb] ) continue;
					seen[nb] = true;
					queue.Enqueue( nb );
				}
			}
			pvs[i] = seen;
		}
		return pvs;
	}

	/// <summary>Can visleaf <paramref name="fromVisLeaf"/> see visleaf
	/// <paramref name="toVisLeaf"/> with these areaportals applied? (Indices are
	/// visleaf indices, not leaf indices — same convention as
	/// <see cref="BspTree.CanSee"/>.)</summary>
	public static bool CanSee( BspTree tree, IReadOnlyList<Areaportal>? areaportals, int fromVisLeaf, int toVisLeaf )
	{
		if ( tree is null ) throw new ArgumentNullException( nameof( tree ) );
		if ( fromVisLeaf < 0 || fromVisLeaf >= tree.VisLeafCount ) throw new ArgumentOutOfRangeException( nameof( fromVisLeaf ) );
		if ( toVisLeaf < 0 || toVisLeaf >= tree.VisLeafCount ) throw new ArgumentOutOfRangeException( nameof( toVisLeaf ) );
		return ComputePvs( tree, areaportals )[fromVisLeaf][toVisLeaf];
	}

	/// <summary>Visleaf indices visible from <paramref name="fromVisLeaf"/> with these
	/// areaportals applied, ascending, including the leaf itself.</summary>
	public static IReadOnlyList<int> VisibleVisLeaves( BspTree tree, IReadOnlyList<Areaportal>? areaportals, int fromVisLeaf )
	{
		if ( tree is null ) throw new ArgumentNullException( nameof( tree ) );
		if ( fromVisLeaf < 0 || fromVisLeaf >= tree.VisLeafCount ) throw new ArgumentOutOfRangeException( nameof( fromVisLeaf ) );
		var row = ComputePvs( tree, areaportals )[fromVisLeaf];
		var visible = new List<int>();
		for ( int i = 0; i < row.Length; i++ )
			if ( row[i] ) visible.Add( i );
		return visible;
	}

	/// <summary>Leaf-pair keys of the closed areaportals (empty when none are closed).</summary>
	public static HashSet<(int, int)> ClosedKeys( IReadOnlyList<Areaportal>? areaportals )
	{
		var closed = new HashSet<(int, int)>();
		if ( areaportals is null ) return closed;
		foreach ( var ap in areaportals )
			if ( ap is not null && ap.IsClosed )
				closed.Add( ap.Key );
		return closed;
	}

	/// <summary>True when this portal is gated by a closed areaportal.</summary>
	public static bool IsPortalClosed( BspPortal portal, IReadOnlyList<Areaportal>? areaportals )
	{
		if ( portal is null ) throw new ArgumentNullException( nameof( portal ) );
		return ClosedKeys( areaportals ).Contains( Areaportal.PortalKey( portal.LeafA, portal.LeafB ) );
	}

	static List<int>[] BuildAdjacency( BspTree tree, HashSet<(int, int)> closed )
	{
		var n = tree.VisLeafCount;
		var adjacency = new List<int>[n];
		for ( int i = 0; i < n; i++ ) adjacency[i] = new List<int>();
		var visIndexByLeaf = new Dictionary<int, int>( n );
		for ( int i = 0; i < n; i++ ) visIndexByLeaf[tree.VisLeaves[i].Index] = i;
		foreach ( var portal in tree.Portals )
		{
			if ( closed.Contains( Areaportal.PortalKey( portal.LeafA, portal.LeafB ) ) ) continue;
			if ( !visIndexByLeaf.TryGetValue( portal.LeafA, out var a ) ) continue;
			if ( !visIndexByLeaf.TryGetValue( portal.LeafB, out var b ) ) continue;
			if ( !adjacency[a].Contains( b ) ) adjacency[a].Add( b );
			if ( !adjacency[b].Contains( a ) ) adjacency[b].Add( a );
		}
		return adjacency;
	}
}

/// <summary>
/// One occluder: a convex planar polygon (usually a quad) that hides props fully
/// behind it, as seen from a viewer. Source guidance is "few, large occluders
/// only" — a small curtain culls nothing, so validation flags one below
/// <see cref="MinOccluderArea"/>; construction only rejects geometry that cannot
/// be an occluder at all (fewer than 3 points, non-planar, zero area).
/// </summary>
public sealed class Occluder
{
	/// <summary>Minimum useful occluder area (model units²): 64×64. Below this the
	/// curtain is smaller than a doorway and culls nothing worth the cost.</summary>
	public const float MinOccluderArea = 4096f;

	/// <summary>Planarity tolerance for the polygon (model units off the face plane).</summary>
	public const float PlanarityEps = 1e-3f;

	public string Name { get; }
	public IReadOnlyList<Vec3> Polygon { get; }
	/// <summary>Face plane (normal from the first three non-collinear points).</summary>
	public BspPlane Plane { get; }
	public float Area { get; }
	public Vec3 Center { get; }

	Occluder( string name, IReadOnlyList<Vec3> polygon, BspPlane plane, float area, Vec3 center )
	{
		Name = name;
		Polygon = polygon;
		Plane = plane;
		Area = area;
		Center = center;
	}

	/// <summary>Occluder from a convex planar polygon (3+ points). The winding may be
	/// either way; the interior test derives the side from the signed area.</summary>
	public static Occluder FromPolygon( string name, IReadOnlyList<Vec3> polygon )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidOccluderPolygon,
				"An occluder needs a name." );
		if ( polygon is null ) throw new ArgumentNullException( nameof( polygon ) );
		if ( polygon.Count < 3 )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidOccluderPolygon,
				$"Occluder '{name}': {polygon.Count} points cannot form a polygon (need at least 3)." );
		// Plane from the first non-collinear triple; a polygon with no such triple
		// is a line/point, not a face.
		BspPlane? plane = null;
		for ( int i = 0; i < polygon.Count - 2 && plane is null; i++ )
			for ( int j = i + 1; j < polygon.Count - 1 && plane is null; j++ )
				for ( int k = j + 1; k < polygon.Count && plane is null; k++ )
				{
					var candidate = BspPlane.FromPoints( polygon[i], polygon[j], polygon[k] );
					if ( candidate.Normal.LengthSquared > 1e-12f ) plane = candidate;
				}
		if ( plane is null )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidOccluderPolygon,
				$"Occluder '{name}': all points are collinear — the polygon has no face." );
		foreach ( var p in polygon )
			if ( MathF.Abs( plane.Value.SignedDistance( p ) ) > PlanarityEps )
				throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidOccluderPolygon,
					$"Occluder '{name}': point {p} is off the face plane by more than {PlanarityEps} — the polygon is not planar." );
		var area = BspPolyhedron.PolygonArea( polygon );
		if ( area <= 1e-6f )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidOccluderPolygon,
				$"Occluder '{name}': zero area — the polygon encloses nothing." );
		var center = Vec3.Zero;
		foreach ( var p in polygon ) center += p;
		center /= polygon.Count;
		return new Occluder( name, polygon.ToArray(), plane.Value, area, center );
	}

	/// <summary>Rectangular occluder quad, centred on <paramref name="center"/>,
	/// facing along <paramref name="normal"/>, spanning <paramref name="width"/>
	/// by <paramref name="height"/> (model units). Width runs across, height up.</summary>
	public static Occluder Quad( string name, Vec3 center, Vec3 normal, float width, float height )
	{
		if ( width <= 0 || height <= 0 )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidOccluderPolygon,
				$"Occluder '{name}': quad size must be positive, got {width}x{height}." );
		var n = normal.Normal;
		if ( n.LengthSquared < 1e-12f )
			throw new AreaportalOccluderException( AreaportalOccluderException.CodeInvalidOccluderPolygon,
				$"Occluder '{name}': quad normal is zero — it has no facing." );
		var u = MathF.Abs( n.z ) < 0.9f
			? Vec3.Cross( n, new Vec3( 0, 0, 1 ) ).Normal
			: Vec3.Cross( n, new Vec3( 0, 1, 0 ) ).Normal;
		var w = Vec3.Cross( n, u );
		var hw = width * 0.5f;
		var hh = height * 0.5f;
		return FromPolygon( name, new[]
		{
			center - u * hw - w * hh,
			center + u * hw - w * hh,
			center + u * hw + w * hh,
			center - u * hw + w * hh,
		} );
	}

	/// <summary>
	/// True when this occluder hides a prop from <paramref name="viewer"/>: the
	/// prop is a sphere of <paramref name="propRadius"/> (0 = a point) centred on
	/// <paramref name="propCenter"/>, and the WHOLE prop lies behind the polygon
	/// as seen from the viewer. The ray viewer→prop must cross the polygon, the
	/// crossing must sit in front of the prop's near surface, and the prop's
	/// projected disc (tangent-cone cross-section at the occluder plane, widened
	/// for the plane's tilt to the ray) must fit inside the polygon — a prop that
	/// peeks past an edge is not culled.
	/// </summary>
	public bool Hides( Vec3 viewer, Vec3 propCenter, float propRadius = 0f )
	{
		if ( propRadius < 0 )
			throw new ArgumentOutOfRangeException( nameof( propRadius ), "A prop radius cannot be negative." );
		var toProp = propCenter - viewer;
		var distToCenter = toProp.Length;
		if ( distToCenter <= 1e-6f ) return false; // viewer is at the prop: nothing hides it
		var dir = toProp / distToCenter;
		var denom = Vec3.Dot( Plane.Normal, dir );
		if ( MathF.Abs( denom ) < 1e-9f ) return false; // ray parallel to the occluder plane: never crosses it
		var t = (Plane.Distance - Vec3.Dot( Plane.Normal, viewer )) / denom;
		// t is the distance along the ray to the plane. The crossing must be strictly
		// between the viewer and the prop's near surface.
		if ( t <= 1e-4f || t >= distToCenter - propRadius ) return false;
		var hit = viewer + dir * t;
		// Projected disc radius at the occluder plane: the tangent cone from the
		// viewer around the sphere has cross-section radius r * t / sqrt(d² - r²)
		// at distance t along the axis; the plane's tilt stretches it by 1/|cos|.
		var projectedRadius = 0f;
		if ( propRadius > 0f )
		{
			var axial = MathF.Sqrt( MathF.Max( distToCenter * distToCenter - propRadius * propRadius, 1e-9f ) );
			projectedRadius = propRadius * t / axial / MathF.Abs( denom );
		}
		return DiscInsidePolygon( hit, projectedRadius );
	}

	// 2D basis on the face plane: (u, v) with u x v == plane normal.
	void Basis( out Vec3 u, out Vec3 v )
	{
		u = MathF.Abs( Plane.Normal.z ) < 0.9f
			? Vec3.Cross( Plane.Normal, new Vec3( 0, 0, 1 ) ).Normal
			: Vec3.Cross( Plane.Normal, new Vec3( 0, 1, 0 ) ).Normal;
		v = Vec3.Cross( Plane.Normal, u );
	}

	/// <summary>True when the disc (centre on the plane, radius in plane units) lies
	/// fully inside the polygon: every edge's interior half-plane must contain it
	/// with room for the radius. Radius 0 is the point-in-polygon test.</summary>
	bool DiscInsidePolygon( Vec3 point, float radius )
	{
		Basis( out var u, out var v );
		(float, float) To2D( Vec3 p ) => (Vec3.Dot( p, u ), Vec3.Dot( p, v ));
		var poly2 = new (float, float)[Polygon.Count];
		for ( int i = 0; i < Polygon.Count; i++ ) poly2[i] = To2D( Polygon[i] );
		var (px, py) = To2D( point );
		// Winding sign from the shoelace area: +1 means interior is left of each edge.
		var area2 = 0f;
		for ( int i = 0; i < poly2.Length; i++ )
		{
			var a = poly2[i];
			var b = poly2[(i + 1) % poly2.Length];
			area2 += a.Item1 * b.Item2 - b.Item1 * a.Item2;
		}
		var sign = area2 >= 0 ? 1f : -1f;
		for ( int i = 0; i < poly2.Length; i++ )
		{
			var a = poly2[i];
			var b = poly2[(i + 1) % poly2.Length];
			var ex = b.Item1 - a.Item1;
			var ey = b.Item2 - a.Item2;
			var edgeLen = MathF.Sqrt( ex * ex + ey * ey );
			if ( edgeLen < 1e-9f ) continue;
			var cross = ex * (py - a.Item2) - ey * (px - a.Item1);
			// Signed distance from the point to the edge line, positive inside.
			var distance = cross * sign / edgeLen;
			if ( distance < radius - 1e-4f ) return false;
		}
		return true;
	}

	public override string ToString() => $"{Name} quad-ish area {Area:0.##} centre {Center}";
}

/// <summary>A prop for the culling pass: a placed prop_static / prop_dynamic /
/// prop_physics (or any map prop) as a position + bounding radius.</summary>
public sealed class PropInstance
{
	public string Name { get; }
	public Vec3 Position { get; }
	/// <summary>Bounding radius (model units); 0 = a point prop.</summary>
	public float Radius { get; }

	public PropInstance( string name, Vec3 position, float radius = 0f )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "A prop needs a name.", nameof( name ) );
		if ( radius < 0 ) throw new ArgumentOutOfRangeException( nameof( radius ), "A prop radius cannot be negative." );
		if ( !float.IsFinite( position.x ) || !float.IsFinite( position.y ) || !float.IsFinite( position.z ) )
			throw new ArgumentException( "A prop position must be finite.", nameof( position ) );
		Name = name;
		Position = position;
		Radius = radius;
	}

	public override string ToString() => $"{Name} @ {Position} r{Radius:0.##}";
}

/// <summary>Result of the occluder prop pass: which props stay visible, which an
/// occluder hid.</summary>
public sealed class OccluderCullResult
{
	public IReadOnlyList<PropInstance> Visible { get; }
	public IReadOnlyList<PropInstance> Culled { get; }

	public OccluderCullResult( IReadOnlyList<PropInstance> visible, IReadOnlyList<PropInstance> culled )
	{
		Visible = visible;
		Culled = culled;
	}
}

/// <summary>
/// The PVS/prop pass: occluder culling, and the composed visibility query (closed
/// areaportals + occluders) a renderer consumes. Pure geometry + the built tree's
/// public data; no rendering, no Hammer.
/// </summary>
public static class OccluderCull
{
	/// <summary>Split props into visible / culled by occluders only (no PVS, no
	/// areaportals): a prop is culled when ANY occluder hides it from the viewer.</summary>
	public static OccluderCullResult CullProps( Vec3 viewer, IReadOnlyList<PropInstance> props, IReadOnlyList<Occluder>? occluders )
	{
		if ( props is null ) throw new ArgumentNullException( nameof( props ) );
		var visible = new List<PropInstance>();
		var culled = new List<PropInstance>();
		foreach ( var prop in props )
		{
			var hidden = false;
			if ( occluders is not null )
				foreach ( var occluder in occluders )
				{
					if ( occluder is not null && occluder.Hides( viewer, prop.Position, prop.Radius ) )
					{
						hidden = true;
						break;
					}
				}
			( hidden ? culled : visible ).Add( prop );
		}
		return new OccluderCullResult( visible, culled );
	}

	/// <summary>
	/// Props visible from <paramref name="viewerPoint"/>: the prop must stand in a
	/// visleaf the viewer's visleaf can see (areaportal-adjusted PVS — a closed
	/// areaportal hides the whole room behind it) AND no occluder may hide it.
	/// A prop inside solid, outside the map, or in no visleaf is never visible,
	/// and neither is a prop seen from a point that is itself in no visleaf.
	/// </summary>
	public static IReadOnlyList<PropInstance> VisibleProps( Vec3 viewerPoint, BspTree tree,
		IReadOnlyList<Areaportal>? areaportals, IReadOnlyList<PropInstance> props, IReadOnlyList<Occluder>? occluders )
	{
		if ( tree is null ) throw new ArgumentNullException( nameof( tree ) );
		if ( props is null ) throw new ArgumentNullException( nameof( props ) );
		var viewerVis = tree.VisLeafAt( viewerPoint );
		if ( viewerVis < 0 ) return Array.Empty<PropInstance>();
		var pvs = AreaportalVisibility.ComputePvs( tree, areaportals );
		var visible = new List<PropInstance>();
		foreach ( var prop in props )
		{
			var propVis = tree.VisLeafAt( prop.Position );
			if ( propVis < 0 ) continue;
			if ( !pvs[viewerVis][propVis] ) continue;
			var hidden = false;
			if ( occluders is not null )
				foreach ( var occluder in occluders )
				{
					if ( occluder is not null && occluder.Hides( viewerPoint, prop.Position, prop.Radius ) )
					{
						hidden = true;
						break;
					}
				}
			if ( !hidden ) visible.Add( prop );
		}
		return visible;
	}
}

/// <summary>Severity of one <see cref="AreaportalOccluderIssue"/>.</summary>
public enum AreaportalOccluderIssueSeverity
{
	Error,
	Warning,
}

/// <summary>One validation finding: a named code, a message, and the areaportal /
/// occluder it is about (by name).</summary>
public sealed class AreaportalOccluderIssue
{
	public const string CodeAreaportalNoControl = "AreaportalNoControl";
	public const string CodeAreaportalControlMissing = "AreaportalControlMissing";
	public const string CodeAreaportalControlNotOpener = "AreaportalControlNotOpener";
	public const string CodeAreaportalNoPortal = "AreaportalNoPortal";
	public const string CodeAreaportalDuplicatePortal = "AreaportalDuplicatePortal";
	public const string CodeAreaportalNotVisPortal = "AreaportalNotVisPortal";
	public const string CodeOccluderTooSmall = "OccluderTooSmall";
	public const string CodeOccluderOutsideMap = "OccluderOutsideMap";

	public AreaportalOccluderIssueSeverity Severity;
	public string Code = "";
	public string Message = "";
	public string Name = "";

	public AreaportalOccluderIssue() { }
	public AreaportalOccluderIssue( AreaportalOccluderIssueSeverity severity, string code, string message, string name = "" )
	{
		Severity = severity; Code = code; Message = message; Name = name;
	}

	public override string ToString() => $"[{Severity} {Code}] {Message}";
}

/// <summary>
/// Validation for areaportals + occluders (the ticket's "misconfigured ones are
/// validation issues"):
///   * every areaportal must be tied to something that opens/closes it — a named
///     control entity that exists in <see cref="MapEntities"/> and whose class can
///     actually open/close things (func_door / func_button / logic_* / trigger_*);
///   * every areaportal must gate a real portal of the built tree, exactly once,
///     and that portal should join two visleaves (an outside-region portal gates
///     nothing the player sees);
///   * every occluder must be large enough to matter (Source: few, large
///     occluders only) and must sit inside the map (its centre in a visleaf).
/// An areaportal with no control is an Error even without an entity collection:
/// it can never change state. Control-existence/class checks need the collection.
/// </summary>
public static class AreaportalOccluderValidator
{
	/// <summary>Registry classes (core-source1.json) that can open/close an
	/// areaportal: doors, buttons, logic entities and triggers. Anything else
	/// (a light, a prop, a spawn point) never fires an open/close.</summary>
	public static readonly IReadOnlyList<string> OpenerClasses = new[]
	{
		"func_door", "func_button",
		"logic_relay", "logic_timer", "logic_auto",
		"trigger_multiple", "trigger_once", "trigger_hurt",
	};

	/// <summary>Validate areaportals + occluders against a built tree and, when
	/// supplied, the map's entities. Pass null for <paramref name="entities"/>
	/// to skip the control-existence/class checks (the no-control error still fires).</summary>
	public static IReadOnlyList<AreaportalOccluderIssue> Validate( BspTree tree, MapEntities? entities,
		IReadOnlyList<Areaportal>? areaportals, IReadOnlyList<Occluder>? occluders )
	{
		if ( tree is null ) throw new ArgumentNullException( nameof( tree ) );
		var issues = new List<AreaportalOccluderIssue>();
		var portalByKey = new Dictionary<(int, int), BspPortal>();
		foreach ( var portal in tree.Portals )
			portalByKey[Areaportal.PortalKey( portal.LeafA, portal.LeafB )] = portal;

		if ( areaportals is not null )
		{
			var seenKeys = new HashSet<(int, int)>();
			foreach ( var ap in areaportals )
			{
				if ( ap is null ) continue;
				if ( string.IsNullOrWhiteSpace( ap.ControlTarget ) )
				{
					issues.Add( new AreaportalOccluderIssue( AreaportalOccluderIssueSeverity.Error,
						AreaportalOccluderIssue.CodeAreaportalNoControl,
						$"Areaportal '{ap.Name}': no control target — nothing opens or closes it, so it can never change state.",
						ap.Name ) );
				}
				else if ( entities is not null )
				{
					var controls = entities.FindByTargetName( ap.ControlTarget );
					if ( controls.Count == 0 )
					{
						issues.Add( new AreaportalOccluderIssue( AreaportalOccluderIssueSeverity.Error,
							AreaportalOccluderIssue.CodeAreaportalControlMissing,
							$"Areaportal '{ap.Name}': control '{ap.ControlTarget}' names no entity in the map.",
							ap.Name ) );
					}
					else if ( !OpenerClasses.Contains( controls[0].ClassName, StringComparer.OrdinalIgnoreCase ) )
					{
						issues.Add( new AreaportalOccluderIssue( AreaportalOccluderIssueSeverity.Warning,
							AreaportalOccluderIssue.CodeAreaportalControlNotOpener,
							$"Areaportal '{ap.Name}': control '{ap.ControlTarget}' is a '{controls[0].ClassName}', which never opens or closes anything.",
							ap.Name ) );
					}
				}
				if ( !seenKeys.Add( ap.Key ) )
				{
					issues.Add( new AreaportalOccluderIssue( AreaportalOccluderIssueSeverity.Error,
						AreaportalOccluderIssue.CodeAreaportalDuplicatePortal,
						$"Areaportal '{ap.Name}': another areaportal already gates portal {ap.LeafA}<->{ap.LeafB}.",
						ap.Name ) );
				}
				if ( !portalByKey.TryGetValue( ap.Key, out var portal ) )
				{
					issues.Add( new AreaportalOccluderIssue( AreaportalOccluderIssueSeverity.Error,
						AreaportalOccluderIssue.CodeAreaportalNoPortal,
						$"Areaportal '{ap.Name}': no portal joins leaves {ap.LeafA} and {ap.LeafB} in this tree.",
						ap.Name ) );
				}
				else
				{
					var la = tree.Leaves[portal.LeafA];
					var lb = tree.Leaves[portal.LeafB];
					if ( !la.IsVisLeaf || !lb.IsVisLeaf )
						issues.Add( new AreaportalOccluderIssue( AreaportalOccluderIssueSeverity.Warning,
							AreaportalOccluderIssue.CodeAreaportalNotVisPortal,
							$"Areaportal '{ap.Name}': portal {ap.LeafA}<->{ap.LeafB} does not join two visleaves, so it gates no visible room.",
							ap.Name ) );
				}
			}
		}

		if ( occluders is not null )
			foreach ( var occluder in occluders )
			{
				if ( occluder is null ) continue;
				if ( occluder.Area < Occluder.MinOccluderArea )
					issues.Add( new AreaportalOccluderIssue( AreaportalOccluderIssueSeverity.Error,
						AreaportalOccluderIssue.CodeOccluderTooSmall,
						$"Occluder '{occluder.Name}': area {occluder.Area:0.##} is below the {Occluder.MinOccluderArea:0.##} minimum — too small to cull anything (few, large occluders only).",
						occluder.Name ) );
				if ( tree.VisLeafAt( occluder.Center ) < 0 )
					issues.Add( new AreaportalOccluderIssue( AreaportalOccluderIssueSeverity.Error,
						AreaportalOccluderIssue.CodeOccluderOutsideMap,
						$"Occluder '{occluder.Name}': centre {occluder.Center} is in no visleaf — it sits outside the map or inside solid.",
						occluder.Name ) );
			}
		return issues;
	}
}
