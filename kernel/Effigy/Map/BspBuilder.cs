using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// BSP tree builder + portals + visleaves + parallel PVS (hammer-bsp-core-tree-portals-leaves).
///
/// Quake-style BSP, native and engine-free: space is partitioned by the planes of the
/// world's structural brushes into convex leaves; leaves that share a face are joined by
/// a portal (the shared polygon on their common plane); a leaf's PVS is the set of leaves
/// reachable from it through portals (flood fill, computed in parallel); leaves with an
/// identical PVS row are one vis cluster (the Quake vvis convention: a cluster is a PVS
/// equivalence class, so vis data is stored once per cluster, not once per leaf).
///
/// DEPENDENCY NOTE (2026-11-13, muse-3-desktop-hammer-18): the core task
/// (hammer-brush-core-plane-based-brushes, worker muse-3-desktop-hammer-01) had NOT
/// landed when this file was first written — there was no
/// kernel/Effigy/Map/MapBrush.cs on disk, so this file could not take a
/// <c>MapBrush</c> and must not invent a competing MapBrush type. It was therefore
/// written against the core spec exactly as filed (see below). MapBrush HAS since
/// landed (2026-11-13 ~16:08, hammer-01), and the filed spec matches the landed
/// API exactly, so the bridge below stands as written:
///   * a MapBrush is faces-as-planes (normal + distance) + per-face material id +
///     UV offset/scale/rotation + lightmap scale + smoothing group — landed as
///     <c>MapBrushFace</c> (<c>Normal</c>, <c>Distance</c>, <c>Material</c>) with
///     the SAME plane convention as <see cref="BspPlane"/> (outward unit normal,
///     solid is Normal.p &lt;= Distance);
///   * <c>FromPlanes</c> / <c>FromVertices</c> constructors, plane-intersection
///     vertex solve, volume + centroid, <c>IsValid</c> + <c>Validate()</c>
///     returning a list of named defects; invalid = named reason, never a
///     silent fix — all present on the landed <c>MapBrush</c>
///     (<c>Faces</c> / <c>FromPlanes(IEnumerable&lt;MapBrushFace&gt;)</c> /
///     <c>FromVertices</c> / <c>Validate()</c> returning
///     <c>MapBrushDefect</c> codes).
/// The bridge, for the final integrator, is <see cref="BspBrush"/>: it is the
/// BSP-side PLANE view of a MapBrush — the same plane set, the same
/// <c>FromPlanes</c> shape, plus the one flag the BSP needs (<c>IsDetail</c>:
/// func_detail brushes never split space, per the ticket). It carries no
/// material, no UVs, no persistence, so it is NOT a second brush type. The
/// integrator adds the one-line adapter
/// <c>BspBrush.FromMapBrush(MapBrush)</c> mapping each face
/// (<c>f.Normal</c>, <c>f.Distance</c>) to a <c>BspPlane</c>; func_detail lives
/// at the document/entity level (MapBrush itself has no detail flag), so the
/// caller passes it as the adapter's <c>isDetail</c> argument. The adapter was
/// NOT added here because the shared kernel tree does not compile yet (sibling
/// MapBrushPrimitives.cs references a <c>MapFace</c> type that does not exist —
/// the landed face type is <c>MapBrushFace</c>), so it could not be verified
/// in-tree; it is a follow-up, not a gap in this file.
///
/// DETERMINISM. Split planes are canonically sorted and ties break on the canonical
/// order, so the same brush list always builds the same tree (same leaves, same
/// portals, same PVS) regardless of input order. PVS rows are computed with
/// <c>Parallel.For</c>, one row per leaf, so parallelism cannot change the result.
///
/// PVS HONESTY. The PVS here is flood-fill reachability through portals: a leaf sees
/// every leaf reachable from it without crossing solid. That is a CONSERVATIVE PVS —
/// it can include a leaf with no straight sightline (e.g. around an L-bend). Exact
/// portal-flow sightline culling is the native compile pipeline ticket's job, not
/// this one; the debug-data ticket consumes <see cref="BspTree.ExportVisLeaves"/>.
///
/// Units are the kernel's inches, Z-up (CONTRACTS.md). No UI, no Hammer app, no
/// shell-out: backend/data-model only.
/// </summary>
public readonly struct BspPlane : IEquatable<BspPlane>, IComparable<BspPlane>
{
	/// <summary>Unit outward normal. The solid / inside side is Dot(Normal, p) &lt;= Distance.</summary>
	public readonly Vec3 Normal;

	/// <summary>Plane offset: the plane is Dot(Normal, p) == Distance.</summary>
	public readonly float Distance;

	public BspPlane( Vec3 normal, float distance )
	{
		Normal = normal;
		Distance = distance;
	}

	/// <summary>Signed distance from the plane; positive is the front (outside) side.</summary>
	public float SignedDistance( Vec3 p ) => Vec3.Dot( Normal, p ) - Distance;

	/// <summary>Same plane, opposite orientation (inside/outside swapped).</summary>
	public BspPlane Negated => new( -Normal, -Distance );

	/// <summary>Plane through three points. The normal follows the right-hand rule
	/// (b-a) x (c-a); the caller is responsible for the outward direction.</summary>
	public static BspPlane FromPoints( Vec3 a, Vec3 b, Vec3 c )
	{
		var n = Vec3.Cross( b - a, c - a ).Normal;
		return new BspPlane( n, Vec3.Dot( n, a ) );
	}

	/// <summary>Canonical order (normal components, then distance) — the tie-break that
	/// makes split-plane choice deterministic.</summary>
	public int CompareTo( BspPlane other )
	{
		var c = Normal.x.CompareTo( other.Normal.x );
		if ( c != 0 ) return c;
		c = Normal.y.CompareTo( other.Normal.y );
		if ( c != 0 ) return c;
		c = Normal.z.CompareTo( other.Normal.z );
		if ( c != 0 ) return c;
		return Distance.CompareTo( other.Distance );
	}

	public bool Equals( BspPlane other ) => Normal.Equals( other.Normal ) && Distance == other.Distance;
	public override bool Equals( object obj ) => obj is BspPlane p && Equals( p );
	public override int GetHashCode() => HashCode.Combine( Normal, Distance );
	public override string ToString() => $"n{Normal} d{Distance:0.###}";
}

/// <summary>Which side of a <see cref="BspPlane"/> a point (or region) falls on.</summary>
public enum BspSide
{
	Front,
	Back,
	On,
}

/// <summary>
/// The BSP-side plane view of a map brush: a convex solid as an intersection of
/// half-spaces, plus the one BSP-relevant flag. See the file header for the bridge to
/// the real MapBrush once hammer-brush-core-plane-based-brushes lands.
/// </summary>
public sealed class BspBrush
{
	/// <summary>Plane-classification epsilon, in model units.</summary>
	public const float Epsilon = 1e-4f;

	public IReadOnlyList<BspPlane> Planes { get; }
	public bool IsDetail { get; }
	public string Name { get; }

	public BspBrush( IReadOnlyList<BspPlane> planes, bool isDetail = false, string name = "" )
	{
		if ( planes is null ) throw new ArgumentNullException( nameof( planes ) );
		// Normalise normals (representation only — the shape is unchanged). A
		// zero-length normal cannot be normalised and is left for Validate() to flag.
		var normed = new List<BspPlane>( planes.Count );
		foreach ( var p in planes )
		{
			var len = p.Normal.Length;
			normed.Add( len > 1e-9f ? new BspPlane( p.Normal / len, p.Distance / len ) : p );
		}
		Planes = normed;
		IsDetail = isDetail;
		Name = name ?? "";
	}

	/// <summary>Brush from a plane set. Mirrors the filed <c>MapBrush.FromPlanes</c>.</summary>
	public static BspBrush FromPlanes( IEnumerable<BspPlane> planes, bool isDetail = false, string name = "" ) =>
		new( planes is null ? throw new ArgumentNullException( nameof( planes ) ) : planes.ToList(), isDetail, name );

	/// <summary>Axis-aligned box brush, min (inclusive) to max (inclusive).</summary>
	public static BspBrush Box( Vec3 min, Vec3 max, bool isDetail = false, string name = "" ) =>
		FromPlanes( new[]
		{
			new BspPlane( new Vec3( -1, 0, 0 ), -min.x ),
			new BspPlane( new Vec3( 1, 0, 0 ), max.x ),
			new BspPlane( new Vec3( 0, -1, 0 ), -min.y ),
			new BspPlane( new Vec3( 0, 1, 0 ), max.y ),
			new BspPlane( new Vec3( 0, 0, -1 ), -min.z ),
			new BspPlane( new Vec3( 0, 0, 1 ), max.z ),
		}, isDetail, name );

	public bool IsValid => Validate().Count == 0;

	/// <summary>Named defects, MapBrush.Validate() shape. An invalid brush is reported,
	/// never silently fixed: the caller decides what to do with it.</summary>
	public IReadOnlyList<string> Validate()
	{
		var defects = new List<string>();
		if ( Planes.Count < 4 )
			defects.Add( $"brush '{Name}': fewer than 4 planes ({Planes.Count}) — a convex brush needs at least 4 faces" );
		for ( int i = 0; i < Planes.Count; i++ )
			if ( Planes[i].Normal.Length < 1e-6f )
				defects.Add( $"brush '{Name}': face {i}: zero-length normal" );
		var seen = new Dictionary<(int, int, int, int), int>();
		for ( int i = 0; i < Planes.Count; i++ )
		{
			var key = BspPolyhedron.CanonicalKey( Planes[i] );
			if ( seen.TryGetValue( key, out var first ) )
				defects.Add( $"brush '{Name}': faces {first} and {i}: duplicate plane" );
			else
				seen[key] = i;
		}
		var vertices = Vertices();
		if ( vertices.Count < 4 )
			defects.Add( $"brush '{Name}': zero volume (only {vertices.Count} vertices: the planes do not enclose a volume)" );
		else if ( Volume() <= 1e-6f )
			defects.Add( $"brush '{Name}': zero volume (volume {Volume():0.###})" );
		return defects;
	}

	/// <summary>Vertices by plane-intersection solve (the filed MapBrush behaviour):
	/// every triple of planes is solved, and a solution is kept only if it lies inside
	/// every half-space.</summary>
	public IReadOnlyList<Vec3> Vertices() => BspPolyhedron.Vertices( Planes );

	/// <summary>Exact volume of the convex solid (divergence theorem over the faces).</summary>
	public float Volume() => BspPolyhedron.Volume( Planes, out _ );

	/// <summary>Exact centroid of the convex solid.</summary>
	public Vec3 Centroid()
	{
		BspPolyhedron.Volume( Planes, out var centroid );
		return centroid;
	}

	/// <summary>True when the point is inside (or on) every face plane.</summary>
	public bool Contains( Vec3 p )
	{
		foreach ( var plane in Planes )
			if ( plane.SignedDistance( p ) > Epsilon )
				return false;
		return true;
	}
}

/// <summary>What a BSP leaf is: solid brush interior, empty void, or outside (the void
/// beyond the map shell, reached by flood fill from the world bounds).</summary>
public enum BspLeafKind
{
	Solid,
	Empty,
	Outside,
}

/// <summary>A convex leaf: the region it occupies (half-spaces), its centre and bounds,
/// and its vis identity (visleaf index / cluster, -1 when it is not a visleaf).</summary>
public sealed class BspLeaf
{
	public int Index { get; }
	public BspLeafKind Kind { get; internal set; }
	public IReadOnlyList<BspPlane> Planes { get; }
	public Vec3 Center { get; }
	public Vec3 Min { get; }
	public Vec3 Max { get; }

	/// <summary>Index among visleaves (empty, non-outside leaves), or -1.</summary>
	public int VisLeafIndex { get; internal set; } = -1;

	/// <summary>Vis cluster (leaves with an identical PVS row share one), or -1.</summary>
	public int ClusterId { get; internal set; } = -1;

	public bool IsVisLeaf => Kind == BspLeafKind.Empty && VisLeafIndex >= 0;

	internal BspLeaf( int index, BspLeafKind kind, IReadOnlyList<BspPlane> planes, Vec3 center, Vec3 min, Vec3 max )
	{
		Index = index;
		Kind = kind;
		Planes = planes;
		Center = center;
		Min = min;
		Max = max;
	}

	/// <summary>True when the point is inside this leaf's region.</summary>
	public bool Contains( Vec3 p )
	{
		foreach ( var plane in Planes )
			if ( plane.SignedDistance( p ) > BspBrush.Epsilon )
				return false;
		return true;
	}
}

/// <summary>A node of the BSP tree: either an internal node splitting on a plane
/// (front child = the side the plane normal points to) or a leaf.</summary>
public sealed class BspNode
{
	public BspPlane SplitPlane { get; set; }
	public BspNode Front { get; set; }
	public BspNode Back { get; set; }

	/// <summary>Leaf index when this node is a leaf, -1 for an internal node.</summary>
	public int LeafIndex { get; set; } = -1;

	public bool IsLeaf => LeafIndex >= 0;
}

/// <summary>Options for <see cref="BspBuilder.Build"/>.</summary>
public sealed class BspBuildOptions
{
	/// <summary>How far past the outermost brush the world bounds extend (model units).
	/// The band between the map shell and the bounds is the outside region.</summary>
	public float OutsideMargin { get; set; } = 64f;

	/// <summary>Maximum tree depth; a region that deep becomes a leaf as-is.</summary>
	public int MaxDepth { get; set; } = 64;

	/// <summary>Plane-classification epsilon, in model units.</summary>
	public float Epsilon { get; set; } = 1e-4f;

	/// <summary>Parallelism for the PVS pass. -1 (default) lets the runtime decide;
	/// 1 forces the single-threaded path. The result is identical either way.</summary>
	public int MaxDegreeOfParallelism { get; set; } = -1;
}

/// <summary>One visleaf's exported data, for the debug-data ticket: where it is, which
/// visleaves it touches through a portal, and which visleaves it can see (its PVS).</summary>
public sealed class VisLeafData
{
	public int LeafIndex { get; }
	public int VisLeafIndex { get; }
	public int ClusterId { get; }
	public Vec3 Center { get; }
	public Vec3 Min { get; }
	public Vec3 Max { get; }
	public IReadOnlyList<int> PortalVisLeaves { get; }
	public IReadOnlyList<int> VisibleVisLeaves { get; }

	internal VisLeafData( int leafIndex, int visLeafIndex, int clusterId, Vec3 center, Vec3 min, Vec3 max,
		IReadOnlyList<int> portalVisLeaves, IReadOnlyList<int> visibleVisLeaves )
	{
		LeafIndex = leafIndex;
		VisLeafIndex = visLeafIndex;
		ClusterId = clusterId;
		Center = center;
		Min = min;
		Max = max;
		PortalVisLeaves = portalVisLeaves;
		VisibleVisLeaves = visibleVisLeaves;
	}
}

/// <summary>The built BSP: tree, leaves, portals, PVS and clusters.</summary>
public sealed class BspTree
{
	public BspNode Root { get; }
	public IReadOnlyList<BspNode> Nodes { get; }
	public IReadOnlyList<BspLeaf> Leaves { get; }
	public IReadOnlyList<BspLeaf> VisLeaves { get; }
	public IReadOnlyList<BspPortal> Portals { get; }
	public int ClusterCount { get; }

	/// <summary>True when the map has interior void the outside flood did not reach.
	/// A map whose void leaks to the outside has no visleaves and reports false; a
	/// map with no interior void at all (e.g. one lone solid box) also reports false.
	/// This is map-level: per-room leak reporting belongs to the Issue engine.</summary>
	public bool IsSealed { get; }

	internal bool[][] Pvs { get; }
	internal PortalGraph Graph { get; }

	public int LeafCount => Leaves.Count;
	public int VisLeafCount => VisLeaves.Count;
	public int PortalCount => Portals.Count;

	internal BspTree( BspNode root, IReadOnlyList<BspNode> nodes, IReadOnlyList<BspLeaf> leaves,
		IReadOnlyList<BspLeaf> visLeaves, IReadOnlyList<BspPortal> portals, bool[][] pvs,
		int clusterCount, bool isSealed, PortalGraph graph )
	{
		Root = root;
		Nodes = nodes;
		Leaves = leaves;
		VisLeaves = visLeaves;
		Portals = portals;
		Pvs = pvs;
		ClusterCount = clusterCount;
		IsSealed = isSealed;
		Graph = graph;
	}

	/// <summary>Can the visleaf <paramref name="fromVisLeaf"/> see visleaf
	/// <paramref name="toVisLeaf"/>? (Indices are visleaf indices, not leaf indices.)</summary>
	public bool CanSee( int fromVisLeaf, int toVisLeaf )
	{
		if ( fromVisLeaf < 0 || fromVisLeaf >= VisLeafCount ) throw new ArgumentOutOfRangeException( nameof( fromVisLeaf ) );
		if ( toVisLeaf < 0 || toVisLeaf >= VisLeafCount ) throw new ArgumentOutOfRangeException( nameof( toVisLeaf ) );
		return Pvs[fromVisLeaf][toVisLeaf];
	}

	/// <summary>Visleaf index of the visleaf containing the point, or -1 when the point
	/// is inside solid, outside the map, or in no leaf at all.</summary>
	public int VisLeafAt( Vec3 point )
	{
		foreach ( var leaf in VisLeaves )
			if ( leaf.Contains( point ) )
				return leaf.VisLeafIndex;
		return -1;
	}

	/// <summary>Visleaf data for the debug-data ticket.</summary>
	public IReadOnlyList<VisLeafData> ExportVisLeaves()
	{
		var result = new List<VisLeafData>( VisLeafCount );
		foreach ( var leaf in VisLeaves )
		{
			var portals = new List<int>();
			foreach ( var nb in Graph.Neighbors( leaf.Index ) )
			{
				var other = Leaves[nb];
				if ( other.IsVisLeaf ) portals.Add( other.VisLeafIndex );
			}
			portals.Sort();
			var visible = new List<int>();
			for ( int i = 0; i < VisLeafCount; i++ )
				if ( Pvs[leaf.VisLeafIndex][i] ) visible.Add( i );
			result.Add( new VisLeafData( leaf.Index, leaf.VisLeafIndex, leaf.ClusterId,
				leaf.Center, leaf.Min, leaf.Max, portals, visible ) );
		}
		return result;
	}

	/// <summary>Deterministic one-line-per-leaf summary (leaf kinds/centres, portals,
	/// clusters, PVS rows). Used by the determinism test; not a file format.</summary>
	public string Describe()
	{
		var lines = new List<string>();
		lines.Add( $"leaves {LeafCount} visleaves {VisLeafCount} portals {PortalCount} clusters {ClusterCount} sealed {IsSealed}" );
		foreach ( var leaf in Leaves )
			lines.Add( $"leaf {leaf.Index} {leaf.Kind} vis {leaf.VisLeafIndex} cluster {leaf.ClusterId} center {leaf.Center.x:0.###},{leaf.Center.y:0.###},{leaf.Center.z:0.###}" );
		foreach ( var portal in Portals )
			lines.Add( $"portal {portal.LeafA}<->{portal.LeafB} area {portal.Area:0.###} center {portal.Center.x:0.###},{portal.Center.y:0.###},{portal.Center.z:0.###}" );
		for ( int i = 0; i < VisLeafCount; i++ )
		{
			var seen = new List<int>();
			for ( int j = 0; j < VisLeafCount; j++ )
				if ( Pvs[i][j] ) seen.Add( j );
			lines.Add( $"pvs {i}: {string.Join( ",", seen )}" );
		}
		return string.Join( "\n", lines );
	}
}

/// <summary>
/// Builds a BSP tree from world brushes. func_detail brushes are excluded — they never
/// split space and never count as solid — per the ticket.
/// </summary>
public static class BspBuilder
{
	/// <summary>Vertex-keep tolerance for the plane-intersection solve (model units).</summary>
	const float VertexKeepEps = 1e-3f;

	/// <summary>Minimum volume for a split child to count as a real region.</summary>
	const float MinRegionVolume = 1e-6f;

	public static BspTree Build( IReadOnlyList<BspBrush> brushes, BspBuildOptions options = null )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		if ( brushes.Count == 0 )
			throw new ArgumentException( "A BSP build needs at least one brush; an empty map has no space to partition.", nameof( brushes ) );
		options ??= new BspBuildOptions();

		var solids = new List<BspBrush>();
		foreach ( var brush in brushes )
		{
			if ( brush is null ) throw new ArgumentException( "Brush list contains a null brush.", nameof( brushes ) );
			var defects = brush.Validate();
			if ( defects.Count > 0 )
				throw new ArgumentException( $"Brush '{brush.Name}' is invalid: {string.Join( "; ", defects )}", nameof( brushes ) );
			if ( !brush.IsDetail ) solids.Add( brush );
		}
		if ( solids.Count == 0 )
			throw new ArgumentException( "A BSP build needs at least one non-detail (structural) brush; func_detail brushes never split space.", nameof( brushes ) );

		// Split planes: every structural face plane, canonically deduplicated and sorted,
		// so the build does not depend on brush order.
		var splitPlanes = new List<BspPlane>();
		var seenPlanes = new HashSet<(int, int, int, int)>();
		foreach ( var brush in solids )
			foreach ( var plane in brush.Planes )
				if ( seenPlanes.Add( BspPolyhedron.CanonicalKey( plane ) ) )
					splitPlanes.Add( plane );
		splitPlanes.Sort();

		// Root region: the AABB of all structural brush vertices, expanded by the margin.
		var allVerts = new List<Vec3>();
		foreach ( var brush in solids ) allVerts.AddRange( brush.Vertices() );
		BspPolyhedron.Bounds( allVerts, out var min, out var max );
		var margin = options.OutsideMargin;
		min = new Vec3( min.x - margin, min.y - margin, min.z - margin );
		max = new Vec3( max.x + margin, max.y + margin, max.z + margin );
		var rootPlanes = AabbPlanes( min, max );

		var builder = new Builder( solids, splitPlanes, options );
		var root = builder.BuildNode( rootPlanes, 0 );
		var leaves = builder.Leaves.OrderBy( l => l.Index ).ToList();

		// Portals between empty leaves, then the outside flood through them.
		var graph = PortalGraph.Build( leaves );
		var outside = graph.FloodOutside( rootPlanes );
		foreach ( var leaf in leaves )
			if ( outside[leaf.Index] )
				leaf.Kind = BspLeafKind.Outside;

		var visLeaves = new List<BspLeaf>();
		foreach ( var leaf in leaves )
			if ( leaf.Kind == BspLeafKind.Empty )
			{
				leaf.VisLeafIndex = visLeaves.Count;
				visLeaves.Add( leaf );
			}

		var pvs = PortalGraph.ComputePvs( visLeaves, graph, options );
		var clusters = PortalGraph.ComputeClusters( pvs );
		for ( int i = 0; i < visLeaves.Count; i++ ) visLeaves[i].ClusterId = clusters[i];
		var clusterCount = visLeaves.Count == 0 ? 0 : clusters.Max() + 1;

		return new BspTree( root, builder.Nodes, leaves, visLeaves, graph.Portals, pvs,
			clusterCount, visLeaves.Count > 0, graph );
	}

	static List<BspPlane> AabbPlanes( Vec3 min, Vec3 max ) => new()
	{
		new BspPlane( new Vec3( -1, 0, 0 ), -min.x ),
		new BspPlane( new Vec3( 1, 0, 0 ), max.x ),
		new BspPlane( new Vec3( 0, -1, 0 ), -min.y ),
		new BspPlane( new Vec3( 0, 1, 0 ), max.y ),
		new BspPlane( new Vec3( 0, 0, -1 ), -min.z ),
		new BspPlane( new Vec3( 0, 0, 1 ), max.z ),
	};

	sealed class Builder
	{
		readonly List<BspBrush> _solids;
		readonly List<BspPlane> _splitPlanes;
		readonly BspBuildOptions _options;
		readonly List<BspNode> _nodes = new();
		readonly List<BspLeaf> _leaves = new();
		int _leafCounter;

		public IReadOnlyList<BspNode> Nodes => _nodes;
		public IReadOnlyList<BspLeaf> Leaves => _leaves;

		public Builder( List<BspBrush> solids, List<BspPlane> splitPlanes, BspBuildOptions options )
		{
			_solids = solids;
			_splitPlanes = splitPlanes;
			_options = options;
		}

		public BspNode BuildNode( List<BspPlane> region, int depth )
		{
			var node = new BspNode();
			_nodes.Add( node );
			var verts = BspPolyhedron.Vertices( region );
			if ( verts.Count == 0 )
				throw new InvalidOperationException( "BSP region collapsed to no vertices; the split planes do not enclose a volume." );

			var split = ChooseSplit( region, verts, depth );
			if ( split is null )
			{
				node.LeafIndex = _leafCounter++;
				_leaves.Add( MakeLeaf( node.LeafIndex, region, verts ) );
				return node;
			}

			node.SplitPlane = split.Value;
			var front = new List<BspPlane>( region ) { split.Value.Negated };
			var back = new List<BspPlane>( region ) { split.Value };
			node.Front = BuildNode( front, depth + 1 );
			node.Back = BuildNode( back, depth + 1 );
			return node;
		}

		BspLeaf MakeLeaf( int index, List<BspPlane> region, List<Vec3> verts )
		{
			var volume = BspPolyhedron.Volume( region, out var center );
			if ( volume <= MinRegionVolume )
			{
				// No volume (should not happen — splits are checked — but never hand
				// a caller a NaN centre): fall back to the vertex average.
				center = Vec3.Zero;
				foreach ( var v in verts ) center += v;
				center /= verts.Count;
			}
			BspPolyhedron.Bounds( verts, out var min, out var max );
			var kind = BspLeafKind.Empty;
			foreach ( var brush in _solids )
				if ( brush.Contains( center ) )
				{
					kind = BspLeafKind.Solid;
					break;
				}
			return new BspLeaf( index, kind, region, center, min, max );
		}

		/// <summary>Best split plane for the region: the plane that straddles it most
		/// evenly (fewest vertices on the lopsided side), ties broken by canonical
		/// plane order. A plane whose split would collapse a child to zero volume is
		/// skipped and the next-best plane is tried.</summary>
		BspPlane? ChooseSplit( List<BspPlane> region, List<Vec3> verts, int depth )
		{
			if ( depth >= _options.MaxDepth ) return null;
			var excluded = new HashSet<BspPlane>();
			while ( true )
			{
				BspPlane? best = null;
				var bestScore = int.MaxValue;
				foreach ( var plane in _splitPlanes )
				{
					if ( excluded.Contains( plane ) ) continue;
					int front = 0, back = 0;
					foreach ( var v in verts )
					{
						var d = plane.SignedDistance( v );
						if ( d > _options.Epsilon ) front++;
						else if ( d < -_options.Epsilon ) back++;
					}
					if ( front == 0 || back == 0 ) continue;
					var score = Math.Abs( front - back );
					if ( score >= bestScore ) continue;
					best = plane;
					bestScore = score;
					if ( bestScore == 0 ) break;
				}
				if ( best is null ) return null;
				var frontRegion = new List<BspPlane>( region ) { best.Value.Negated };
				var backRegion = new List<BspPlane>( region ) { best.Value };
				if ( HasVolume( frontRegion ) && HasVolume( backRegion ) ) return best;
				excluded.Add( best.Value );
			}
		}

		static bool HasVolume( List<BspPlane> region )
		{
			var verts = BspPolyhedron.Vertices( region );
			if ( verts.Count < 4 ) return false;
			return BspPolyhedron.Volume( region, out _ ) > MinRegionVolume;
		}
	}
}

/// <summary>
/// Convex-polyhedron helpers shared by the brush and the tree: plane-intersection
/// vertex solve, face polygons by clipping, exact volume + centroid, bounds.
/// A polyhedron is an intersection of half-spaces (inside is Dot(N, p) &lt;= D);
/// face polygons wind counter-clockwise seen from outside the solid.
/// </summary>
public static class BspPolyhedron
{
	const float KeepEps = 1e-3f;
	const float WeldEps = 1e-4f;

	/// <summary>Quantised plane key for canonical dedupe / opposite-plane lookup:
	/// normals to 1e-4, distance to 1e-3.</summary>
	public static (int, int, int, int) CanonicalKey( BspPlane plane ) => (
		(int)MathF.Round( plane.Normal.x * 10000f ),
		(int)MathF.Round( plane.Normal.y * 10000f ),
		(int)MathF.Round( plane.Normal.z * 10000f ),
		(int)MathF.Round( plane.Distance * 1000f ) );

	public static (int, int, int, int) CanonicalKeyNegated( BspPlane plane ) => (
		(int)MathF.Round( -plane.Normal.x * 10000f ),
		(int)MathF.Round( -plane.Normal.y * 10000f ),
		(int)MathF.Round( -plane.Normal.z * 10000f ),
		(int)MathF.Round( -plane.Distance * 1000f ) );

	/// <summary>Vertices: solve every triple of planes, keep the solutions that lie
	/// inside every half-space, weld duplicates.</summary>
	public static List<Vec3> Vertices( IReadOnlyList<BspPlane> planes )
	{
		var verts = new List<Vec3>();
		for ( int i = 0; i < planes.Count; i++ )
		for ( int j = i + 1; j < planes.Count; j++ )
		for ( int k = j + 1; k < planes.Count; k++ )
		{
			var a = planes[i];
			var b = planes[j];
			var c = planes[k];
			var det = Vec3.Dot( a.Normal, Vec3.Cross( b.Normal, c.Normal ) );
			if ( MathF.Abs( det ) < 1e-9f ) continue;
			var p = (a.Distance * Vec3.Cross( b.Normal, c.Normal )
				+ b.Distance * Vec3.Cross( c.Normal, a.Normal )
				+ c.Distance * Vec3.Cross( a.Normal, b.Normal )) / det;
			var inside = true;
			foreach ( var plane in planes )
				if ( plane.SignedDistance( p ) > KeepEps )
				{
					inside = false;
					break;
				}
			if ( !inside ) continue;
			var dup = false;
			foreach ( var v in verts )
				if ( v.AlmostEquals( p, WeldEps ) )
				{
					dup = true;
					break;
				}
			if ( !dup ) verts.Add( p );
		}
		return verts;
	}

	/// <summary>Bounds of a vertex set.</summary>
	public static void Bounds( IReadOnlyList<Vec3> verts, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var v in verts )
		{
			min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
			max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
		}
	}

	/// <summary>Cross-section polygon of the polyhedron on a face plane: the region
	/// vertices that lie on the plane, ordered counter-clockwise seen from outside
	/// (the face normal side). Every face vertex is a triple-plane intersection, so
	/// it is already in <see cref="Vertices"/> — no clipping quad is needed, and
	/// none of the quad-centre/extent failure modes apply. Empty when the plane
	/// misses the solid (or only touches it in an edge/point).</summary>
	public static List<Vec3> FacePolygon( IReadOnlyList<BspPlane> planes, BspPlane face )
	{
		var verts = Vertices( planes );
		var onPlane = new List<Vec3>();
		foreach ( var v in verts )
			if ( MathF.Abs( face.SignedDistance( v ) ) <= KeepEps )
				onPlane.Add( v );
		if ( onPlane.Count < 3 ) return new List<Vec3>();
		var center = Vec3.Zero;
		foreach ( var v in onPlane ) center += v;
		center /= onPlane.Count;
		// Basis (u, v) with u x v == face normal, so increasing polar angle winds
		// the polygon counter-clockwise seen from outside.
		var u = MathF.Abs( face.Normal.z ) < 0.9f
			? Vec3.Cross( face.Normal, new Vec3( 0, 0, 1 ) ).Normal
			: Vec3.Cross( face.Normal, new Vec3( 0, 1, 0 ) ).Normal;
		var w = Vec3.Cross( face.Normal, u );
		float Angle( Vec3 p ) => MathF.Atan2( Vec3.Dot( p - center, w ), Vec3.Dot( p - center, u ) );
		onPlane.Sort( ( a, b ) => Angle( a ).CompareTo( Angle( b ) ) );
		return onPlane;
	}

	/// <summary>Sutherland–Hodgman clip: keep the inside of the half-space.</summary>
	public static List<Vec3> ClipPolygon( List<Vec3> poly, BspPlane plane, float eps = BspBrush.Epsilon )
	{
		var result = new List<Vec3>();
		if ( poly.Count == 0 ) return result;
		for ( int i = 0; i < poly.Count; i++ )
		{
			var curr = poly[i];
			var next = poly[(i + 1) % poly.Count];
			var dCurr = plane.SignedDistance( curr );
			var dNext = plane.SignedDistance( next );
			var currIn = dCurr <= eps;
			var nextIn = dNext <= eps;
			if ( currIn ) result.Add( curr );
			if ( currIn == nextIn ) continue;
			var t = dCurr / (dCurr - dNext);
			result.Add( Vec3.Lerp( curr, next, t ) );
		}
		return result;
	}

	/// <summary>Newell area vector; |v| / 2 is the polygon area.</summary>
	public static Vec3 PolygonAreaVector( IReadOnlyList<Vec3> poly )
	{
		var area = Vec3.Zero;
		for ( int i = 0; i < poly.Count; i++ )
		{
			var a = poly[i];
			var b = poly[(i + 1) % poly.Count];
			area += Vec3.Cross( a, b );
		}
		return area * 0.5f;
	}

	public static float PolygonArea( IReadOnlyList<Vec3> poly ) => PolygonAreaVector( poly ).Length;

	/// <summary>Exact volume (divergence theorem over the face polygons) and exact
	/// centroid. Returns 0 with the vertex-average centroid when the planes enclose
	/// no volume.</summary>
	public static float Volume( IReadOnlyList<BspPlane> planes, out Vec3 centroid )
	{
		var verts = Vertices( planes );
		if ( verts.Count < 4 )
		{
			centroid = Average( verts );
			return 0f;
		}
		var volume = 0f;
		var numerator = Vec3.Zero;
		foreach ( var face in planes )
		{
			var poly = FacePolygon( planes, face );
			if ( poly.Count < 3 ) continue;
			for ( int i = 1; i < poly.Count - 1; i++ )
			{
				var a = poly[0];
				var b = poly[i];
				var c = poly[i + 1];
				var d = Vec3.Dot( a, Vec3.Cross( b, c ) );
				volume += d / 6f;
				numerator += (a + b + c) * d;
			}
		}
		if ( volume < 0 )
		{
			volume = -volume;
			numerator = -numerator;
		}
		if ( volume <= 1e-9f )
		{
			centroid = Average( verts );
			return 0f;
		}
		centroid = numerator / (24f * volume);
		return volume;
	}

	static Vec3 Average( IReadOnlyList<Vec3> verts )
	{
		if ( verts.Count == 0 ) return Vec3.Zero;
		var sum = Vec3.Zero;
		foreach ( var v in verts ) sum += v;
		return sum / verts.Count;
	}
}
