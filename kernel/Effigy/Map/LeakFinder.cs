using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Hammer Desktop P1: leak detection (flood-fill) + point-file export.
/// Board task hammer-leak-detection-pointfile.
///
/// A leak is a gap to the void: flood-fill from an entity (player start)
/// through the BSP leaves via the portal graph; if a void leaf is reachable
/// the map is LEAKED, and the point file traces the path from the entity to
/// the void. A sealed map never reaches the void. API + data only — drawing
/// the path in the viewport is not in this ticket.
///
/// DEPENDENCY STATUS (2026-11-13): the BspBuilder / PortalGraph ticket
/// (hammer-bsp-core-tree-portals-leaves) had NOT landed when this was written
/// — no kernel/Effigy/Map/BspBuilder.cs or PortalGraph.cs exists on disk yet,
/// and its desc names no concrete leaf/portal member names. So LeakFinder does
/// NOT reference BspBuilder types. It flood-fills over the minimal
/// <see cref="ILeakPortalGraph"/> interface defined here, which is exactly the
/// shape the BSP spec describes (leaves joined by portals, one void leaf
/// outside the map). When BspBuilder lands, the integration is a thin adapter
/// that presents its graph as an ILeakPortalGraph — this file's public API
/// does not change. Do not invent a second leak finder against BspBuilder
/// types; adapt this one.
///
/// Geometry is the kernel's inches and Z-up convention. Deterministic: same
/// graph + same start -&gt; same visited order, same gap, same path (portals
/// are traversed in Id order, BFS, no hash-order dependence).
/// </summary>
public static class LeakFinder
{
	/// <summary>One convex BSP leaf as the flood-fill sees it.</summary>
	public sealed class LeakLeaf
	{
		public int Id { get; }
		public Vec3 Center { get; }
		public Vec3 Min { get; }
		public Vec3 Max { get; }
		/// <summary>True for the outside/void leaf. Reaching one means LEAKED.</summary>
		public bool IsVoid { get; }
		/// <summary>True for a solid leaf (inside a brush). Never entered.</summary>
		public bool IsSolid { get; }

		public LeakLeaf( int id, Vec3 center, Vec3 min, Vec3 max, bool isVoid = false, bool isSolid = false )
		{
			Id = id;
			Center = center;
			Min = min;
			Max = max;
			IsVoid = isVoid;
			IsSolid = isSolid;
		}

		public bool Contains( Vec3 p ) =>
			p.x >= Min.x && p.x <= Max.x &&
			p.y >= Min.y && p.y <= Max.y &&
			p.z >= Min.z && p.z <= Max.z;
	}

	/// <summary>One portal joining two leaves. The gap in a leaked map is the
	/// open portal crossed into the void leaf; its centre is the gap location.</summary>
	public sealed class LeakPortal
	{
		public int Id { get; }
		public int LeafA { get; }
		public int LeafB { get; }
		public Vec3 Center { get; }
		/// <summary>False = sealed shut (e.g. a closed door / blocked gap): the
		/// flood-fill cannot cross it.</summary>
		public bool IsOpen { get; }

		public LeakPortal( int id, int leafA, int leafB, Vec3 center, bool isOpen = true )
		{
			Id = id;
			LeafA = leafA;
			LeafB = leafB;
			Center = center;
			IsOpen = isOpen;
		}

		public int Other( int leafId ) => leafId == LeafA ? LeafB : leafId == LeafB ? LeafA : -1;
	}

	/// <summary>Minimal portal-graph surface LeakFinder flood-fills over.
	/// This is the contract the BspBuilder ticket's PortalGraph must satisfy
	/// (via an adapter) once it lands: leaves, portals, and point-to-leaf lookup.</summary>
	public interface ILeakPortalGraph
	{
		IReadOnlyList<LeakLeaf> Leaves { get; }
		IReadOnlyList<LeakPortal> Portals { get; }
		/// <summary>Leaf containing the point, or null when it is in no leaf.
		/// A point inside both an interior leaf and the (larger) void leaf
		/// resolves to the interior leaf.</summary>
		LeakLeaf LeafContaining( Vec3 point );
	}

	/// <summary>In-memory portal graph. Tests and the BSP adapter build one of
	/// these; LeakFinder itself never builds BSP geometry.</summary>
	public sealed class LeakGraph : ILeakPortalGraph
	{
		readonly List<LeakLeaf> _leaves = new();
		readonly List<LeakPortal> _portals = new();

		public IReadOnlyList<LeakLeaf> Leaves => _leaves;
		public IReadOnlyList<LeakPortal> Portals => _portals;

		public LeakGraph AddLeaf( LeakLeaf leaf )
		{
			if ( leaf is null ) throw new ArgumentNullException( nameof( leaf ) );
			if ( _leaves.Any( l => l.Id == leaf.Id ) ) throw new ArgumentException( $"Duplicate leaf id {leaf.Id}." );
			_leaves.Add( leaf );
			return this;
		}

		public LeakGraph AddPortal( LeakPortal portal )
		{
			if ( portal is null ) throw new ArgumentNullException( nameof( portal ) );
			if ( _portals.Any( p => p.Id == portal.Id ) ) throw new ArgumentException( $"Duplicate portal id {portal.Id}." );
			if ( _leaves.All( l => l.Id != portal.LeafA ) || _leaves.All( l => l.Id != portal.LeafB ) )
				throw new ArgumentException( $"Portal {portal.Id} joins unknown leaf {portal.LeafA}/{portal.LeafB}." );
			_portals.Add( portal );
			return this;
		}

		public LeakLeaf LeafContaining( Vec3 point )
		{
			// Interior (non-void, non-solid) leaves win over the void leaf,
			// whose bounds are deliberately generous and overlap them.
			foreach ( var leaf in _leaves.OrderBy( l => l.Id ) )
				if ( !leaf.IsVoid && !leaf.IsSolid && leaf.Contains( point ) ) return leaf;
			foreach ( var leaf in _leaves.OrderBy( l => l.Id ) )
				if ( leaf.IsVoid && leaf.Contains( point ) ) return leaf;
			return null;
		}

		public IReadOnlyList<LeakPortal> PortalsFrom( int leafId ) =>
			_portals.Where( p => p.LeafA == leafId || p.LeafB == leafId ).OrderBy( p => p.Id ).ToList();

		// -- Documented fixture graphs (the acceptance shapes). Units: inches, Z-up.
		// Room: 128 x 128 floor, 128 high, walls 8 thick are implied by the leaf
		// bounds, not modelled here — the BSP builder supplies real leaves.

		/// <summary>One interior leaf + one void leaf, no portal between them.
		/// Player start (0, 0, 32).</summary>
		public static LeakGraph SealedRoom()
		{
			var g = new LeakGraph();
			g.AddLeaf( new LeakLeaf( 0, new Vec3( 0, 0, 64 ), new Vec3( -64, -64, 0 ), new Vec3( 64, 64, 128 ) ) );
			g.AddLeaf( new LeakLeaf( 1, new Vec3( 0, 0, 256 ), new Vec3( -1024, -1024, -1024 ), new Vec3( 1024, 1024, 1024 ), isVoid: true ) );
			return g;
		}

		/// <summary>Sealed room plus one open 1-unit gap portal in the +X wall,
		/// centred at (64, 0, 32). Player start (0, 0, 32).</summary>
		public static LeakGraph RoomWithGap() => RoomWithGap( new Vec3( 64, 0, 32 ) );

		/// <summary>Sealed room plus one open 1-unit gap portal in the +X wall,
		/// centred at <paramref name="gapCenter"/>. Player start (0, 0, 32).</summary>
		public static LeakGraph RoomWithGap( Vec3 gapCenter )
		{
			var g = SealedRoom();
			g.AddPortal( new LeakPortal( 0, 0, 1, gapCenter ) );
			return g;
		}

		/// <summary>Two rooms (A: x -128..0, B: x 0..128) joined by a doorway
		/// portal at (0, 0, 32). When <paramref name="leakInFarRoom"/> is true,
		/// room B also has a 1-unit gap portal to the void at (128, 0, 32);
		/// otherwise the map is sealed. Player start is in room A (-64, 0, 32).</summary>
		public static LeakGraph TwoRooms( bool leakInFarRoom )
		{
			var g = new LeakGraph();
			g.AddLeaf( new LeakLeaf( 0, new Vec3( -64, 0, 64 ), new Vec3( -128, -64, 0 ), new Vec3( 0, 64, 128 ) ) );
			g.AddLeaf( new LeakLeaf( 1, new Vec3( 64, 0, 64 ), new Vec3( 0, -64, 0 ), new Vec3( 128, 64, 128 ) ) );
			g.AddLeaf( new LeakLeaf( 2, new Vec3( 0, 0, 256 ), new Vec3( -1024, -1024, -1024 ), new Vec3( 1024, 1024, 1024 ), isVoid: true ) );
			g.AddPortal( new LeakPortal( 0, 0, 1, new Vec3( 0, 0, 32 ) ) );
			if ( leakInFarRoom ) g.AddPortal( new LeakPortal( 1, 1, 2, new Vec3( 128, 0, 32 ) ) );
			return g;
		}
	}

	/// <summary>Result of one leak check. When <see cref="IsLeaked"/> is false
	/// the map is sealed: <see cref="Path"/> is empty and the gap ids are -1.</summary>
	public sealed class LeakResult
	{
		public bool IsLeaked { get; }
		public Vec3 Start { get; }
		public int StartLeafId { get; }
		/// <summary>Nearest interior leaf to the gap (the leaf the flood-fill
		/// was in when it crossed into the void), or -1 when sealed.</summary>
		public int GapLeafId { get; }
		/// <summary>Portal crossed into the void (the gap), or -1 when sealed.</summary>
		public int GapPortalId { get; }
		/// <summary>Gap location: centre of the gap portal, or Zero when sealed.</summary>
		public Vec3 GapLocation { get; }
		/// <summary>First point outside the map, past the gap, or Zero when sealed.</summary>
		public Vec3 ExitPoint { get; }
		/// <summary>Leak path: player start, then each portal centre and
		/// intermediate leaf centre crossed, ending past the gap. Empty when sealed.</summary>
		public IReadOnlyList<Vec3> Path { get; }
		public IReadOnlyList<int> VisitedLeafIds { get; }

		public LeakResult( bool isLeaked, Vec3 start, int startLeafId, int gapLeafId, int gapPortalId,
			Vec3 gapLocation, Vec3 exitPoint, IReadOnlyList<Vec3> path, IReadOnlyList<int> visitedLeafIds )
		{
			IsLeaked = isLeaked;
			Start = start;
			StartLeafId = startLeafId;
			GapLeafId = gapLeafId;
			GapPortalId = gapPortalId;
			GapLocation = gapLocation;
			ExitPoint = exitPoint;
			Path = path;
			VisitedLeafIds = visitedLeafIds;
		}

		/// <summary>Point-file text for this result's path. Empty string when sealed
		/// (a sealed map has no leak path to export).</summary>
		public string ToPointFileText() => LeakFinder.ToPointFileText( Path );
	}

	/// <summary>
	/// Flood-fill from <paramref name="playerStart"/> through open portals.
	/// Reaching a void leaf means LEAKED; exhausting the reachable interior
	/// leaves means sealed.
	/// </summary>
	/// <exception cref="ArgumentNullException">Graph is null.</exception>
	/// <exception cref="ArgumentException">Graph has no leaves, or the player
	/// start is inside no leaf (including inside a solid leaf).</exception>
	public static LeakResult FindLeak( ILeakPortalGraph graph, Vec3 playerStart )
	{
		if ( graph is null ) throw new ArgumentNullException( nameof( graph ) );
		if ( graph.Leaves is null || graph.Leaves.Count == 0 ) throw new ArgumentException( "Leak check needs at least one leaf." );
		if ( graph.Portals is null ) throw new ArgumentException( "Leak check needs a portal list (empty is fine)." );

		var startLeaf = graph.LeafContaining( playerStart )
			?? throw new ArgumentException( "Player start is not inside any leaf." );

		var visited = new List<int>();
		var seen = new HashSet<int>();
		var parentLeaf = new Dictionary<int, int>();   // leaf id -> previous leaf id
		var parentPortal = new Dictionary<int, int>(); // leaf id -> portal crossed to reach it
		var queue = new Queue<int>();
		seen.Add( startLeaf.Id );
		visited.Add( startLeaf.Id );
		queue.Enqueue( startLeaf.Id );

		// Already outside: the start itself is the leak.
		if ( startLeaf.IsVoid )
			return new LeakResult( true, playerStart, startLeaf.Id, startLeaf.Id, -1,
				playerStart, playerStart, new[] { playerStart }, visited );

		// Portals are undirected: index both ends.
		var adjacency = new Dictionary<int, List<LeakPortal>>();
		foreach ( var portal in graph.Portals.OrderBy( p => p.Id ) )
		{
			if ( !adjacency.TryGetValue( portal.LeafA, out var a ) ) adjacency[portal.LeafA] = a = new List<LeakPortal>();
			if ( !adjacency.TryGetValue( portal.LeafB, out var b ) ) adjacency[portal.LeafB] = b = new List<LeakPortal>();
			a.Add( portal );
			b.Add( portal );
		}
		var byId = graph.Leaves.ToDictionary( l => l.Id );
		int voidLeafId = -1, gapPortalId = -1, gapLeafId = -1;

		while ( queue.Count > 0 )
		{
			int current = queue.Dequeue();
			if ( !adjacency.TryGetValue( current, out var portals ) ) continue;
			foreach ( var portal in portals )
			{
				if ( !portal.IsOpen ) continue;
				int next = portal.Other( current );
				if ( next < 0 || seen.Contains( next ) || !byId.TryGetValue( next, out var nextLeaf ) ) continue;
				if ( nextLeaf.IsSolid ) continue; // never enter a solid leaf
				seen.Add( next );
				visited.Add( next );
				parentLeaf[next] = current;
				parentPortal[next] = portal.Id;
				if ( nextLeaf.IsVoid )
				{
					voidLeafId = next;
					gapPortalId = portal.Id;
					gapLeafId = current;
					queue.Clear();
					break;
				}
				queue.Enqueue( next );
			}
		}

		if ( voidLeafId < 0 )
			return new LeakResult( false, playerStart, startLeaf.Id, -1, -1,
				Vec3.Zero, Vec3.Zero, Array.Empty<Vec3>(), visited );

		// Reconstruct the leaf chain start -> ... -> gap leaf -> void leaf.
		var chain = new List<int> { voidLeafId };
		for ( int leaf = voidLeafId; parentLeaf.TryGetValue( leaf, out var prev ); leaf = prev )
			chain.Add( prev );
		chain.Reverse(); // [start, ..., gapLeaf, voidLeaf]

		var gapPortal = graph.Portals.First( p => p.Id == gapPortalId );
		var gapLocation = gapPortal.Center;
		var lastInteriorCenter = byId[gapLeafId].Center;
		// Exit step: 16 units past the gap along the horizontal component of
		// (gap - gap-leaf centre). Wall gaps are vertical, so the horizontal
		// component is the outward-through-the-wall direction; using the raw
		// 3D vector would drag the exit down/up by the leaf centre's height
		// (the centre sits mid-room, the gap sits at player height). A
		// floor/ceiling gap has no horizontal component, so fall back to the
		// full 3D direction, then to +X.
		var outward = gapLocation - lastInteriorCenter;
		var horizontal = new Vec3( outward.x, outward.y, 0 );
		var exitDir = horizontal.LengthSquared > 1e-12f ? horizontal.Normal
			: outward.LengthSquared > 1e-12f ? outward.Normal
			: new Vec3( 1, 0, 0 );
		var exit = gapLocation + exitDir * 16f;

		var path = new List<Vec3> { playerStart };
		for ( int i = 0; i + 1 < chain.Count; i++ )
		{
			int next = chain[i + 1];
			var portal = graph.Portals.First( p => p.Id == parentPortal[next] );
			path.Add( portal.Center );
			if ( !byId[next].IsVoid ) path.Add( byId[next].Center );
		}
		path.Add( exit );

		return new LeakResult( true, playerStart, startLeaf.Id, gapLeafId, gapPortalId,
			gapLocation, exit, path, visited );
	}

	// -- Point files (Valve .lin / .pts) -------------------------------------
	// Format (both extensions, one point per line, Z-up inches, invariant
	// culture, no header — the format TrenchBroom / Valve tools read):
	//   x y z
	// A sealed map has no leak path, so it has no point file (empty text).

	/// <summary>Serialise a leak path to point-file text (Valve .lin/.pts).</summary>
	public static string ToPointFileText( IReadOnlyList<Vec3> path )
	{
		if ( path is null ) throw new ArgumentNullException( nameof( path ) );
		var lines = new List<string>( path.Count );
		foreach ( var p in path )
			lines.Add( string.Format( CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####}", p.x, p.y, p.z ) );
		return lines.Count == 0 ? string.Empty : string.Join( "\n", lines ) + "\n";
	}

	/// <summary>Parse point-file text back to points. Blank lines are skipped;
	/// any other malformed line throws <see cref="FormatException"/>.</summary>
	public static List<Vec3> ParsePointFileText( string text )
	{
		if ( text is null ) throw new ArgumentNullException( nameof( text ) );
		var points = new List<Vec3>();
		foreach ( var raw in text.Split( '\n' ) )
		{
			var line = raw.Trim();
			if ( line.Length == 0 ) continue;
			var parts = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length != 3
				|| !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
				|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
				|| !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
				throw new FormatException( $"Bad point-file line: '{line}' (expected 'x y z')." );
			points.Add( new Vec3( x, y, z ) );
		}
		return points;
	}

	/// <summary>Write a leak path as a point file (.lin or .pts — same content).</summary>
	public static void WritePointFile( string path, IReadOnlyList<Vec3> points )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "Point-file path is empty." );
		File.WriteAllText( path, ToPointFileText( points ) );
	}

	/// <summary>Write a leak result's path as a point file. Sealed results
	/// write an empty file (no path to export).</summary>
	public static void WritePointFile( string path, LeakResult result )
	{
		if ( result is null ) throw new ArgumentNullException( nameof( result ) );
		WritePointFile( path, result.Path );
	}

	/// <summary>Read a point file (.lin or .pts) back to points.</summary>
	public static List<Vec3> ReadPointFile( string path ) => ParsePointFileText( File.ReadAllText( path ) );
}
