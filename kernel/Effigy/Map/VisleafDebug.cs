using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Effigy;

namespace Effigy.Map;

/// <summary>
/// Visleaf debug data (hammer-optimisation-visleaf-debug-data). API/data only —
/// no viewport UI, no viewer: the dump is the stable text/JSON form a headless
/// caller or a future viewer consumes to show leaf boundaries and PVS sets
/// (Report §5.2: mappers NEED to see leaves to optimise).
///
/// The dump is a READ of a built <see cref="BspTree"/>, never a rebuild: every
/// leaf, portal and PVS row in it comes straight from the tree the BSP builder
/// produced (hammer-bsp-core-tree-portals-leaves), so the dump matches the
/// builder's leaves/portals/PVS exactly by construction. A leaf boundary is its
/// convex polyhedron as one polygon per bounding plane
/// (<see cref="BspPolyhedron.FacePolygon"/> — planes that only bound the region
/// redundantly produce no face and are skipped, they are not geometry).
///
/// There is deliberately NO MapDocument overload: the builder takes brush lists,
/// and the MapDocument -&gt; BspBrush adapter (<c>BspBrush.FromMapBrush</c>) is the
/// final integrator's follow-up (see BspBuilder.cs header). The brush-list
/// overload below is the honest entry point until that adapter lands; inventing
/// a document type here would be a second model, not a bridge.
///
/// Units are the kernel's inches, Z-up (CONTRACTS.md). Deterministic: same tree
/// dumps byte-identical JSON (leaves in visleaf order, portals in the tree's
/// sorted order, clusters in id order with sorted members, fixed float format).
/// </summary>
public static class VisleafDebug
{
	/// <summary>Schema version carried in every dump.</summary>
	public const int SchemaVersion = 1;

	/// <summary>Dump the visleaves of a built tree: leaf boundary polyhedra,
	/// the portal list and the per-leaf PVS sets.</summary>
	public static VisleafDebugDump DumpLeaves( BspTree tree )
	{
		if ( tree is null ) throw new ArgumentNullException( nameof( tree ) );
		return new VisleafDebugDump( tree );
	}

	/// <summary>Build a tree from brushes, then dump it. Identical to
	/// <c>DumpLeaves(BspBuilder.Build(brushes, options))</c>; the builder's own
	/// named rejections (empty list, invalid brush, detail-only) surface as-is.</summary>
	public static VisleafDebugDump DumpLeaves( IReadOnlyList<BspBrush> brushes, BspBuildOptions options = null ) =>
		DumpLeaves( BspBuilder.Build( brushes, options ) );

	/// <summary>Serialise a dump to its stable JSON form. Deterministic: the
	/// same dump always produces the same bytes (fixed key order, fixed float
	/// format, no timestamps, no absolute paths).</summary>
	public static string ToJson( VisleafDebugDump dump )
	{
		if ( dump is null ) throw new ArgumentNullException( nameof( dump ) );
		var sb = new StringBuilder( 8192 );
		sb.Append( "{\n" );
		AppendLine( sb, 1, $"\"schemaVersion\": {dump.SchemaVersion}," );
		AppendLine( sb, 1, "\"format\": \"geppetto-visleaf-debug/1\"," );
		AppendLine( sb, 1, $"\"sealed\": {Bool( dump.Sealed )}," );
		AppendLine( sb, 1, $"\"leafCount\": {dump.LeafCount}," );
		AppendLine( sb, 1, $"\"visLeafCount\": {dump.VisLeafCount}," );
		AppendLine( sb, 1, $"\"portalCount\": {dump.PortalCount}," );
		AppendLine( sb, 1, $"\"clusterCount\": {dump.ClusterCount}," );
		AppendArray( sb, 1, "\"leaves\"", dump.Leaves, ( leaf, last ) =>
		{
			sb.Append( Indent( 2 ) ).Append( "{\n" );
			AppendLine( sb, 3, $"\"leafIndex\": {leaf.LeafIndex}," );
			AppendLine( sb, 3, $"\"visLeafIndex\": {leaf.VisLeafIndex}," );
			AppendLine( sb, 3, $"\"clusterId\": {leaf.ClusterId}," );
			AppendLine( sb, 3, $"\"kind\": \"{leaf.Kind}\"," );
			AppendLine( sb, 3, $"\"center\": {Vec( leaf.Center )}," );
			AppendLine( sb, 3, $"\"min\": {Vec( leaf.Min )}," );
			AppendLine( sb, 3, $"\"max\": {Vec( leaf.Max )}," );
			AppendLine( sb, 3, $"\"volume\": {Num( leaf.Volume )}," );
			AppendArray( sb, 3, "\"faces\"", leaf.Faces, ( face, faceLast ) =>
			{
				sb.Append( Indent( 4 ) ).Append( "{\n" );
				AppendLine( sb, 5, $"\"normal\": {Vec( face.Normal )}," );
				AppendLine( sb, 5, $"\"distance\": {Num( face.Distance )}," );
				AppendArray( sb, 5, "\"vertices\"", face.Vertices, ( v, vLast ) =>
					AppendLine( sb, 6, Vec( v ) + ( vLast ? "" : "," ) ), last: true );
				sb.Append( Indent( 4 ) ).Append( '}' ).Append( faceLast ? "" : "," ).Append( '\n' );
			} );
			AppendLine( sb, 3, $"\"portalVisLeaves\": {Ints( leaf.PortalVisLeaves )}," );
			AppendLine( sb, 3, $"\"visibleVisLeaves\": {Ints( leaf.VisibleVisLeaves )}" );
			sb.Append( Indent( 2 ) ).Append( '}' ).Append( last ? "" : "," ).Append( '\n' );
		} );
		AppendArray( sb, 1, "\"portals\"", dump.Portals, ( portal, last ) =>
		{
			sb.Append( Indent( 2 ) ).Append( "{\n" );
			AppendLine( sb, 3, $"\"leafA\": {portal.LeafA}," );
			AppendLine( sb, 3, $"\"leafB\": {portal.LeafB}," );
			AppendLine( sb, 3, $"\"visLeafA\": {portal.VisLeafA}," );
			AppendLine( sb, 3, $"\"visLeafB\": {portal.VisLeafB}," );
			AppendLine( sb, 3, $"\"isVisPortal\": {Bool( portal.IsVisPortal )}," );
			AppendLine( sb, 3, $"\"area\": {Num( portal.Area )}," );
			AppendLine( sb, 3, $"\"center\": {Vec( portal.Center )}," );
			AppendLine( sb, 3, $"\"normal\": {Vec( portal.Normal )}," );
			AppendLine( sb, 3, $"\"distance\": {Num( portal.Distance )}," );
			AppendArray( sb, 3, "\"polygon\"", portal.Polygon, ( v, vLast ) =>
				AppendLine( sb, 4, Vec( v ) + ( vLast ? "" : "," ) ), last: true );
			sb.Append( Indent( 2 ) ).Append( '}' ).Append( last ? "" : "," ).Append( '\n' );
		} );
		AppendArray( sb, 1, "\"clusters\"", dump.Clusters, ( cluster, last ) =>
			AppendLine( sb, 2, Ints( cluster ) + ( last ? "" : "," ) ), last: true );
		sb.Append( "}\n" );
		return sb.ToString();
	}

	static string Indent( int level ) => new( '\t', level );
	static void AppendLine( StringBuilder sb, int level, string text ) => sb.Append( Indent( level ) ).Append( text ).Append( '\n' );
	static string Bool( bool value ) => value ? "true" : "false";

	/// <summary>Fixed 3-decimal float format (invariant culture); -0 prints as 0,
	/// so the same value can never serialise two ways.</summary>
	static string Num( float value )
	{
		if ( MathF.Abs( value ) < 0.0005f ) value = 0f;
		return value.ToString( "0.###", CultureInfo.InvariantCulture );
	}

	static string Vec( Vec3 v ) => $"[{Num( v.x )}, {Num( v.y )}, {Num( v.z )}]";
	static string Ints( IReadOnlyList<int> values ) => "[" + string.Join( ", ", values ) + "]";

	static void AppendArray<T>( StringBuilder sb, int level, string name, IReadOnlyList<T> items, Action<T, bool> writeItem, bool last = false )
	{
		sb.Append( Indent( level ) ).Append( name ).Append( ": [" );
		if ( items.Count == 0 ) { sb.Append( ']' ).Append( last ? "\n" : ",\n" ); return; }
		sb.Append( '\n' );
		for ( int i = 0; i < items.Count; i++ ) writeItem( items[i], i == items.Count - 1 );
		sb.Append( Indent( level ) ).Append( ']' ).Append( last ? "\n" : ",\n" );
	}
}

/// <summary>One face of a dumped leaf boundary: the bounding plane plus the
/// polygon of the leaf on that plane (counter-clockwise seen from outside).</summary>
public sealed class VisleafDebugFace
{
	public Vec3 Normal { get; }
	public float Distance { get; }
	public IReadOnlyList<Vec3> Vertices { get; }

	internal VisleafDebugFace( Vec3 normal, float distance, IReadOnlyList<Vec3> vertices )
	{
		Normal = normal;
		Distance = distance;
		Vertices = vertices;
	}
}

/// <summary>One dumped visleaf: where it is, its boundary polyhedron, which
/// visleaves it touches through a portal, and which visleaves it can see.</summary>
public sealed class VisleafDebugLeaf
{
	public int LeafIndex { get; }
	public int VisLeafIndex { get; }
	public int ClusterId { get; }
	public string Kind { get; }
	public Vec3 Center { get; }
	public Vec3 Min { get; }
	public Vec3 Max { get; }
	public float Volume { get; }
	public IReadOnlyList<VisleafDebugFace> Faces { get; }
	public IReadOnlyList<int> PortalVisLeaves { get; }
	public IReadOnlyList<int> VisibleVisLeaves { get; }

	internal VisleafDebugLeaf( int leafIndex, int visLeafIndex, int clusterId, string kind,
		Vec3 center, Vec3 min, Vec3 max, float volume, IReadOnlyList<VisleafDebugFace> faces,
		IReadOnlyList<int> portalVisLeaves, IReadOnlyList<int> visibleVisLeaves )
	{
		LeafIndex = leafIndex;
		VisLeafIndex = visLeafIndex;
		ClusterId = clusterId;
		Kind = kind;
		Center = center;
		Min = min;
		Max = max;
		Volume = volume;
		Faces = faces;
		PortalVisLeaves = portalVisLeaves;
		VisibleVisLeaves = visibleVisLeaves;
	}
}

/// <summary>One dumped portal: the shared face between two leaves. The leaf
/// indices are the tree's leaf indices (matching <see cref="BspPortal"/>); the
/// visleaf indices are -1 on an end that is not a visleaf (e.g. the outside
/// void), and <see cref="IsVisPortal"/> is true only when both ends are
/// visleaves — the doorway, in the two-room fixture.</summary>
public sealed class VisleafDebugPortal
{
	public int LeafA { get; }
	public int LeafB { get; }
	public int VisLeafA { get; }
	public int VisLeafB { get; }
	public bool IsVisPortal { get; }
	public float Area { get; }
	public Vec3 Center { get; }
	public Vec3 Normal { get; }
	public float Distance { get; }
	public IReadOnlyList<Vec3> Polygon { get; }

	internal VisleafDebugPortal( int leafA, int leafB, int visLeafA, int visLeafB,
		float area, Vec3 center, Vec3 normal, float distance, IReadOnlyList<Vec3> polygon )
	{
		LeafA = leafA;
		LeafB = leafB;
		VisLeafA = visLeafA;
		VisLeafB = visLeafB;
		IsVisPortal = visLeafA >= 0 && visLeafB >= 0;
		Area = area;
		Center = center;
		Normal = normal;
		Distance = distance;
		Polygon = polygon;
	}
}

/// <summary>The visleaf debug dump of one BSP tree: leaf boundary polyhedra,
/// the portal list and the per-leaf PVS sets, plus the cluster partition.
/// Built only by <see cref="VisleafDebug.DumpLeaves(BspTree)"/> reading the tree.</summary>
public sealed class VisleafDebugDump
{
	public int SchemaVersion { get; } = VisleafDebug.SchemaVersion;
	public bool Sealed { get; }
	public int LeafCount { get; }
	public int VisLeafCount { get; }
	public int PortalCount { get; }
	public int ClusterCount { get; }
	public IReadOnlyList<VisleafDebugLeaf> Leaves { get; }
	public IReadOnlyList<VisleafDebugPortal> Portals { get; }
	public IReadOnlyList<IReadOnlyList<int>> Clusters { get; }

	internal VisleafDebugDump( BspTree tree )
	{
		Sealed = tree.IsSealed;
		LeafCount = tree.LeafCount;
		VisLeafCount = tree.VisLeafCount;
		PortalCount = tree.PortalCount;
		ClusterCount = tree.ClusterCount;

		var exported = tree.ExportVisLeaves();
		var leaves = new List<VisleafDebugLeaf>( exported.Count );
		foreach ( var data in exported )
		{
			var leaf = tree.Leaves[data.LeafIndex];
			var faces = new List<VisleafDebugFace>();
			foreach ( var plane in leaf.Planes )
			{
				var poly = BspPolyhedron.FacePolygon( leaf.Planes, plane );
				if ( poly.Count < 3 ) continue;
				faces.Add( new VisleafDebugFace( plane.Normal, plane.Distance, poly ) );
			}
			leaves.Add( new VisleafDebugLeaf( data.LeafIndex, data.VisLeafIndex, data.ClusterId,
				leaf.Kind.ToString().ToLowerInvariant(), data.Center, data.Min, data.Max,
				BspPolyhedron.Volume( leaf.Planes, out _ ), faces,
				data.PortalVisLeaves, data.VisibleVisLeaves ) );
		}
		Leaves = leaves;

		var portals = new List<VisleafDebugPortal>( tree.Portals.Count );
		foreach ( var portal in tree.Portals )
		{
			var a = tree.Leaves[portal.LeafA];
			var b = tree.Leaves[portal.LeafB];
			portals.Add( new VisleafDebugPortal( portal.LeafA, portal.LeafB,
				a.VisLeafIndex, b.VisLeafIndex, portal.Area, portal.Center,
				portal.Plane.Normal, portal.Plane.Distance, portal.Polygon ) );
		}
		Portals = portals;

		var clusters = new List<IReadOnlyList<int>>( ClusterCount );
		for ( int c = 0; c < ClusterCount; c++ )
		{
			var members = new List<int>();
			foreach ( var data in exported )
				if ( data.ClusterId == c ) members.Add( data.VisLeafIndex );
			members.Sort();
			clusters.Add( members );
		}
		Clusters = clusters;
	}

	/// <summary>Stable JSON form of this dump (see <see cref="VisleafDebug.ToJson"/>).</summary>
	public string ToJson() => VisleafDebug.ToJson( this );
}
