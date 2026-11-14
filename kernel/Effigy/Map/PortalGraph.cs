using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Effigy;

/// <summary>
/// A portal: the shared face between two empty leaves. The polygon lies on
/// <see cref="Plane"/> (whose outward normal points from <see cref="LeafA"/> toward
/// <see cref="LeafB"/>), and its area is the open cross-section between the rooms —
/// the doorway, in the two-room fixture. Portals are the edges of the visibility
/// graph: a leaf sees another leaf exactly when a portal path joins them.
/// </summary>
public sealed class BspPortal
{
	public int LeafA { get; }
	public int LeafB { get; }
	public BspPlane Plane { get; }
	public IReadOnlyList<Vec3> Polygon { get; }
	public float Area { get; }
	public Vec3 Center { get; }

	internal BspPortal( int leafA, int leafB, BspPlane plane, IReadOnlyList<Vec3> polygon, float area, Vec3 center )
	{
		LeafA = leafA;
		LeafB = leafB;
		Plane = plane;
		Polygon = polygon;
		Area = area;
		Center = center;
	}
}

/// <summary>
/// The portal graph of a BSP tree (hammer-bsp-core-tree-portals-leaves): portals
/// between empty leaves, the outside flood, the parallel PVS and vis clusters.
///
/// A portal exists between two empty leaves exactly when they share a face with
/// positive area — detected by opposing planes (one leaf's face plane is the other's
/// negated plane) and computed as the first leaf's cross-section on that plane,
/// clipped by the second leaf's half-spaces. Solid leaves never get portals: a face
/// against solid is a wall, not a way through.
/// </summary>
public sealed class PortalGraph
{
	/// <summary>Portals smaller than this (model units²) are degenerate contact, not
	/// an opening — an edge or a point touch, never a doorway.</summary>
	public const float MinPortalArea = 0.01f;

	public IReadOnlyList<BspPortal> Portals { get; }

	readonly Dictionary<int, List<int>> _neighbors = new();
	readonly Dictionary<int, List<BspPortal>> _byLeaf = new();
	readonly IReadOnlyList<BspLeaf> _leaves;

	PortalGraph( List<BspPortal> portals, IReadOnlyList<BspLeaf> leaves )
	{
		Portals = portals;
		_leaves = leaves;
		foreach ( var portal in portals )
		{
			AddNeighbor( portal.LeafA, portal.LeafB );
			AddNeighbor( portal.LeafB, portal.LeafA );
			AddPortal( portal.LeafA, portal );
			AddPortal( portal.LeafB, portal );
		}
	}

	void AddNeighbor( int from, int to )
	{
		if ( !_neighbors.TryGetValue( from, out var list ) ) _neighbors[from] = list = new List<int>();
		if ( !list.Contains( to ) ) list.Add( to );
	}

	void AddPortal( int leaf, BspPortal portal )
	{
		if ( !_byLeaf.TryGetValue( leaf, out var list ) ) _byLeaf[leaf] = list = new List<BspPortal>();
		list.Add( portal );
	}

	/// <summary>Leaf indices adjacent to the leaf through a portal (empty leaves only).</summary>
	public IReadOnlyList<int> Neighbors( int leafIndex ) =>
		_neighbors.TryGetValue( leafIndex, out var list ) ? list : Array.Empty<int>();

	/// <summary>Portals touching the leaf.</summary>
	public IReadOnlyList<BspPortal> PortalsOf( int leafIndex ) =>
		_byLeaf.TryGetValue( leafIndex, out var list ) ? list : Array.Empty<BspPortal>();

	/// <summary>Build the portal graph over the tree's empty leaves. (At build time the
	/// outside leaves are still marked Empty — the flood in <see cref="FloodOutside"/>
	/// sorts them out afterwards, and it needs these portals to do it.)</summary>
	public static PortalGraph Build( IReadOnlyList<BspLeaf> leaves )
	{
		var portals = new List<BspPortal>();
		var donePairs = new HashSet<(int, int)>();
		var byIndex = new Dictionary<int, BspLeaf>();
		foreach ( var leaf in leaves ) byIndex[leaf.Index] = leaf;

		// Index every empty leaf's planes by canonical key, so the opposite plane of a
		// face finds the leaf on the other side without an O(leaves²) scan.
		var byPlane = new Dictionary<(int, int, int, int), List<int>>();
		foreach ( var leaf in leaves )
		{
			if ( leaf.Kind != BspLeafKind.Empty ) continue;
			foreach ( var plane in leaf.Planes )
			{
				var key = BspPolyhedron.CanonicalKey( plane );
				if ( !byPlane.TryGetValue( key, out var list ) ) byPlane[key] = list = new List<int>();
				if ( !list.Contains( leaf.Index ) ) list.Add( leaf.Index );
			}
		}

		foreach ( var leaf in leaves )
		{
			if ( leaf.Kind != BspLeafKind.Empty ) continue;
			foreach ( var plane in leaf.Planes )
			{
				if ( !byPlane.TryGetValue( BspPolyhedron.CanonicalKeyNegated( plane ), out var others ) ) continue;
				foreach ( var otherIndex in others )
				{
					if ( otherIndex <= leaf.Index ) continue;
					if ( !donePairs.Add( (leaf.Index, otherIndex) ) ) continue;
					var other = byIndex[otherIndex];
					var polygon = BspPolyhedron.FacePolygon( leaf.Planes, plane );
					foreach ( var otherPlane in other.Planes )
					{
						polygon = BspPolyhedron.ClipPolygon( polygon, otherPlane );
						if ( polygon.Count < 3 ) break;
					}
					if ( polygon.Count < 3 ) continue;
					var area = BspPolyhedron.PolygonArea( polygon );
					if ( area <= MinPortalArea ) continue;
					var center = Vec3.Zero;
					foreach ( var v in polygon ) center += v;
					center /= polygon.Count;
					portals.Add( new BspPortal( leaf.Index, otherIndex, plane, polygon, area, center ) );
				}
			}
		}

		portals.Sort( ( a, b ) =>
		{
			var c = a.LeafA.CompareTo( b.LeafA );
			return c != 0 ? c : a.LeafB.CompareTo( b.LeafB );
		} );
		return new PortalGraph( portals, leaves );
	}

	/// <summary>
	/// Outside flood: the leaves reachable from the world bounds without crossing
	/// solid, walking the portal graph. Seeds are the empty leaves that touch a root
	/// (world-bound) plane; the flood then spreads through portals. Returns one flag
	/// per leaf index. A sealed room's interior is never reached; a leaked room's
	/// interior is, through the gap — which is exactly the leak test.
	/// </summary>
	public bool[] FloodOutside( IReadOnlyList<BspPlane> rootPlanes )
	{
		var outside = new bool[_leaves.Count];
		var queue = new Queue<int>();

		// Seeds: empty leaves with a real face on a world-bound plane. A leaf merely
		// INHERITING a root plane (every leaf descends from the root region, so every
		// leaf's plane list contains the root planes as redundant constraints) does
		// NOT touch the boundary — only a positive-area cross-section on the plane
		// counts. Seeding on plane membership alone floods the interior too.
		foreach ( var leaf in _leaves )
		{
			if ( leaf.Kind != BspLeafKind.Empty ) continue;
			var touches = false;
			foreach ( var plane in rootPlanes )
			{
				var face = BspPolyhedron.FacePolygon( leaf.Planes, plane );
				if ( face.Count >= 3 && BspPolyhedron.PolygonArea( face ) > MinPortalArea )
				{
					touches = true;
					break;
				}
			}
			if ( !touches ) continue;
			outside[leaf.Index] = true;
			queue.Enqueue( leaf.Index );
		}
		while ( queue.Count > 0 )
		{
			var cur = queue.Dequeue();
			foreach ( var nb in Neighbors( cur ) )
			{
				if ( outside[nb] ) continue;
				outside[nb] = true;
				queue.Enqueue( nb );
			}
		}
		return outside;
	}

	/// <summary>
	/// Parallel PVS: one flood fill per visleaf over the portal graph (BFS through
	/// portals, visleaves only). Each row is written by exactly one iteration, so the
	/// result is identical for any degree of parallelism. A leaf always sees itself.
	/// </summary>
	public static bool[][] ComputePvs( IReadOnlyList<BspLeaf> visLeaves, PortalGraph graph, BspBuildOptions options )
	{
		var n = visLeaves.Count;
		var pvs = new bool[n][];
		var visIndexByLeaf = new Dictionary<int, int>( n );
		for ( int i = 0; i < n; i++ ) visIndexByLeaf[visLeaves[i].Index] = i;
		var parallel = new ParallelOptions
		{
			MaxDegreeOfParallelism = options is null ? -1 : options.MaxDegreeOfParallelism,
		};
		Parallel.For( 0, n, parallel, i =>
		{
			var seen = new bool[n];
			var queue = new Queue<int>();
			seen[i] = true;
			queue.Enqueue( i );
			while ( queue.Count > 0 )
			{
				var cur = queue.Dequeue();
				foreach ( var nbLeaf in graph.Neighbors( visLeaves[cur].Index ) )
				{
					if ( !visIndexByLeaf.TryGetValue( nbLeaf, out var nb ) ) continue;
					if ( seen[nb] ) continue;
					seen[nb] = true;
					queue.Enqueue( nb );
				}
			}
			pvs[i] = seen;
		} );
		return pvs;
	}

	/// <summary>
	/// Vis clusters (Quake vvis convention): leaves whose PVS row is identical share
	/// one cluster, numbered in first-occurrence order (lowest visleaf index first).
	/// Returns one cluster id per visleaf index.
	/// </summary>
	public static int[] ComputeClusters( bool[][] pvs )
	{
		var clusters = new int[pvs.Length];
		var representatives = new List<bool[]>();
		for ( int i = 0; i < pvs.Length; i++ )
		{
			var found = -1;
			for ( int c = 0; c < representatives.Count; c++ )
				if ( RowsEqual( pvs[i], representatives[c] ) )
				{
					found = c;
					break;
				}
			if ( found < 0 )
			{
				found = representatives.Count;
				representatives.Add( pvs[i] );
			}
			clusters[i] = found;
		}
		return clusters;
	}

	static bool RowsEqual( bool[] a, bool[] b )
	{
		if ( a.Length != b.Length ) return false;
		for ( int i = 0; i < a.Length; i++ )
			if ( a[i] != b[i] ) return false;
		return true;
	}
}
