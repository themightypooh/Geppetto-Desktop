using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Quick Play — BSP-only bake + walkable collision + player-start validation, headless.
///
/// The fast path for layout/logic tests: build ONLY the BSP (no VVIS, no VRAD, no lightmap),
/// build walkable collision from the world brushes and displacements, then validate the
/// player spawn and a headless forward walk (spawn -> move forward N units -> still inside
/// the map, on the floor). No Play button UI, no rendered walk — the bake + validation an
/// AI or headless caller runs. Units are inches, Z-up, matching the kernel and Source/Hammer.
/// Yaw 0 faces +X, yaw 90 faces +Y.
///
/// DEPENDENCY STATUS (2026-11-13, at implementation time — reconcile before wiring):
/// BspBuilder / PortalGraph (hammer-bsp-core-tree-portals-leaves) and MapBrush /
/// MapDocument / MapEntities / Displacement do NOT exist on disk yet, and their board
/// descs specify file names and behaviour but no method signatures — there is no landed
/// BSP or map-document API to reuse. LeakFinder (hammer-leak-detection-pointfile) HAS
/// landed and IS reused here: the sealed check presents this bake's leaves/portals as
/// a LeakFinder.LeakGraph and calls LeakFinder.FindLeak. So this file is self-contained
/// except for that seam:
/// it carries its own deterministic axis-aligned BSP (split planes from world-brush faces;
/// a sealed room is exactly one empty leaf) and its own QuickPlay-prefixed input types
/// (QuickPlayMap / QuickPlayBrush / QuickPlayEntity / QuickPlayDisplacement), named so they
/// can never collide with the sibling Map* types when they land. The integrator seam is:
/// map MapDocument -> QuickPlayMap (axis-aligned brushes only; a non-axis-aligned MapBrush
/// is out of scope here) and swap the internal BSP for BspBuilder — the leaf/portal result
/// shape matches the BSP ticket's acceptance (sealed room = one empty leaf; rooms joined
/// by a doorway = several empty leaves with a portal in the doorway).
///
/// COLLISION: CollisionBuilder.Build takes a PartStudio — a map has no feature history, so
/// it cannot apply. QuickPlay uses the exact case CollisionBuilder itself documents: a
/// convex solid IS its own collision, so one hull per world/detail brush, emitted as the
/// box's own 8 corners. It is NOT routed through ConvexHull.Build: probed 2026-11-13,
/// Build returns 6 points / 8 faces for a cube's 8 corners (two coplanar-cap corners
/// dropped), so that hull does not contain the brush. Displacement heightfields are
/// non-convex, so they go through ConvexDecomp (top grid + a bottom ring 16 units below
/// the lowest sample, decomposed into hulls — an approximation there, and its hulls are
/// still built from real samples). LeakFinder owns point-file export; QuickPlay only
/// reports Sealed + the portal the leak exits through.
///
/// OPS COMMAND: Bake() is the headless entry point the ops-CLI ticket wraps; this ticket
/// adds no CLI surface of its own (that surface is owned by the AI ops-CLI ticket).
/// </summary>
public static class QuickPlayError
{
	public const string NoPlayerStart = "no-player-start";
	public const string EmptyMap = "empty-map";
	public const string InvalidBrush = "invalid-brush";
	public const string InvalidDisplacement = "invalid-displacement";
	public const string TooComplex = "too-complex";
}

/// <summary>A Quick Play failure with a stable machine-readable code. Missing player start,
/// empty map, invalid brush/displacement and an over-complex BSP are named errors, never
/// silent failures. Spawn/walk problems are NOT exceptions — they are reported in
/// <see cref="QuickPlayBakeResult.Spawn"/> / <see cref="QuickPlayBakeResult.Walk"/>.</summary>
public sealed class QuickPlayException : Exception
{
	public string Code { get; }

	public QuickPlayException( string code, string message ) : base( message )
	{
		Code = code;
	}
}

/// <summary>One convex world brush, as an axis-aligned box. A Source brush is the
/// intersection of half-spaces; this ticket only accepts the axis-aligned subset, because
/// the BSP, the floor search and the capsule test are all AABB math. A future MapBrush
/// adapter rejects (never silently flattens) a non-axis-aligned brush.</summary>
public sealed class QuickPlayBrush
{
	public string Id;
	public Vec3 Min;
	public Vec3 Max;

	/// <summary>func_detail: excluded from the BSP split planes (per the BSP ticket) but
	/// still solid — it contributes collision hulls, floors and walk blocking.</summary>
	public bool Detail;

	public string Material;

	public QuickPlayBrush( string id, Vec3 min, Vec3 max, bool detail = false, string material = null )
	{
		Id = id;
		Min = min;
		Max = max;
		Detail = detail;
		Material = material;
	}

	public bool IsValid => Min.x < Max.x && Min.y < Max.y && Min.z < Max.z;

	public float Volume => IsValid ? ( Max.x - Min.x ) * ( Max.y - Min.y ) * ( Max.z - Min.z ) : 0f;

	public Vec3 Centre => new Vec3( ( Min.x + Max.x ) * 0.5f, ( Min.y + Max.y ) * 0.5f, ( Min.z + Max.z ) * 0.5f );

	public Vec3[] Corners() => new[]
	{
		new Vec3( Min.x, Min.y, Min.z ), new Vec3( Max.x, Min.y, Min.z ),
		new Vec3( Min.x, Max.y, Min.z ), new Vec3( Max.x, Max.y, Min.z ),
		new Vec3( Min.x, Min.y, Max.z ), new Vec3( Max.x, Min.y, Max.z ),
		new Vec3( Min.x, Max.y, Max.z ), new Vec3( Max.x, Max.y, Max.z ),
	};

	/// <summary>Strictly inside, with a 1e-3 inset: a point resting exactly on a face
	/// (a spawn standing on the floor) is NOT inside the solid.</summary>
	public bool ContainsStrict( Vec3 p, float eps = 1e-3f ) =>
		p.x > Min.x + eps && p.x < Max.x - eps &&
		p.y > Min.y + eps && p.y < Max.y - eps &&
		p.z > Min.z + eps && p.z < Max.z - eps;

	public bool ContainsXY( float x, float y, float eps = 1e-3f ) =>
		x >= Min.x - eps && x <= Max.x + eps && y >= Min.y - eps && y <= Max.y + eps;
}

/// <summary>One point entity. Only the class, the origin and the facing yaw are needed
/// for Quick Play; keyvalues belong to MapEntities.</summary>
public sealed class QuickPlayEntity
{
	public string ClassName;
	public Vec3 Origin;

	/// <summary>Facing yaw in degrees. 0 faces +X, 90 faces +Y.</summary>
	public float Yaw;

	public QuickPlayEntity( string className, Vec3 origin, float yaw = 0f )
	{
		ClassName = className;
		Origin = origin;
		Yaw = yaw;
	}
}

/// <summary>A walkable displacement: a heightfield grid over an axis-aligned patch.
/// Heights are absolute Z. A displacement is solid/walkable but does NOT seal the map —
/// it never contributes BSP split planes, only floor height and collision hulls.</summary>
public sealed class QuickPlayDisplacement
{
	public Vec3 Origin;
	public float SizeX;
	public float SizeY;
	public int Columns;
	public int Rows;

	/// <summary>Heights[row, column], row = +Y, column = +X.</summary>
	public float[,] Heights;

	public QuickPlayDisplacement( Vec3 origin, float sizeX, float sizeY, int columns, int rows, float[,] heights )
	{
		Origin = origin;
		SizeX = sizeX;
		SizeY = sizeY;
		Columns = columns;
		Rows = rows;
		Heights = heights;
	}

	public bool IsValid =>
		SizeX > 0f && SizeY > 0f && Columns >= 2 && Rows >= 2 && Heights != null &&
		Heights.GetLength( 0 ) == Rows && Heights.GetLength( 1 ) == Columns;

	public bool ContainsXY( float x, float y, float eps = 1e-3f ) =>
		x >= Origin.x - eps && x <= Origin.x + SizeX + eps &&
		y >= Origin.y - eps && y <= Origin.y + SizeY + eps;

	/// <summary>Bilinear height at (x, y), clamped to the patch edge.</summary>
	public float HeightAt( float x, float y )
	{
		float fx = ( x - Origin.x ) / SizeX * ( Columns - 1 );
		float fy = ( y - Origin.y ) / SizeY * ( Rows - 1 );
		fx = Math.Clamp( fx, 0f, Columns - 1 );
		fy = Math.Clamp( fy, 0f, Rows - 1 );
		int x0 = Math.Min( (int)MathF.Floor( fx ), Columns - 2 );
		int y0 = Math.Min( (int)MathF.Floor( fy ), Rows - 2 );
		float tx = fx - x0, ty = fy - y0;
		float h00 = Heights[y0, x0], h10 = Heights[y0, x0 + 1];
		float h01 = Heights[y0 + 1, x0], h11 = Heights[y0 + 1, x0 + 1];
		return ( h00 * ( 1f - tx ) + h10 * tx ) * ( 1f - ty ) + ( h01 * ( 1f - tx ) + h11 * tx ) * ty;
	}

	public float MinHeight()
	{
		float min = Heights[0, 0];
		for ( int r = 0; r < Rows; r++ )
			for ( int c = 0; c < Columns; c++ )
				if ( Heights[r, c] < min ) min = Heights[r, c];
		return min;
	}
}

/// <summary>The map Quick Play bakes: world brushes + displacements + entities.
/// Displacements and detail brushes never seal the map; only world brushes do.</summary>
public sealed class QuickPlayMap
{
	public List<QuickPlayBrush> Brushes = new List<QuickPlayBrush>();
	public List<QuickPlayEntity> Entities = new List<QuickPlayEntity>();
	public List<QuickPlayDisplacement> Displacements = new List<QuickPlayDisplacement>();

	/// <summary>First info_player_start in entity order, or null. Case-insensitive.</summary>
	public QuickPlayEntity PlayerStart
	{
		get
		{
			foreach ( var e in Entities )
				if ( string.Equals( e.ClassName, "info_player_start", StringComparison.OrdinalIgnoreCase ) )
					return e;
			return null;
		}
	}

	public int PlayerStartCount
	{
		get
		{
			int n = 0;
			foreach ( var e in Entities )
				if ( string.Equals( e.ClassName, "info_player_start", StringComparison.OrdinalIgnoreCase ) )
					n++;
			return n;
		}
	}
}

/// <summary>Bake + validation knobs. Defaults are Source-scale: a 72-unit tall,
/// 16-unit radius player, an 18-unit step, a 64-unit default walk.</summary>
public sealed class QuickPlayOptions
{
	public float WalkDistance = 64f;
	public float WalkStep = 8f;
	public float PlayerRadius = 16f;
	public float PlayerHeight = 72f;
	public float StepHeight = 18f;

	/// <summary>A drop further than this ends the walk ("walk-drop-too-far").</summary>
	public float MaxDrop = 72f;

	/// <summary>BSP leaf cap; above it Bake fails with "too-complex" rather than
	/// allocating an unbounded partition.</summary>
	public int MaxLeaves = 4096;

	/// <summary>Void margin around the brush union that bounds the BSP root.</summary>
	public float VoidMargin = 64f;
}

/// <summary>One BSP leaf: a cell of the brush-plane arrangement. Solid leaves are inside
/// a world brush; empty leaves are open space; a void leaf is empty space touching the
/// root bounds (outside the sealed shell).</summary>
public sealed class QuickPlayLeaf
{
	public int Index;
	public Vec3 Min;
	public Vec3 Max;
	public bool Solid;
	public bool IsVoid;
	public Vec3 Centre => new Vec3( ( Min.x + Max.x ) * 0.5f, ( Min.y + Max.y ) * 0.5f, ( Min.z + Max.z ) * 0.5f );
}

/// <summary>One portal: the shared face between two adjacent empty leaves, i.e. the
/// opening the player (and the leak flood-fill) passes through.</summary>
public sealed class QuickPlayPortal
{
	public int LeafA;
	public int LeafB;
	public Vec3 Min;
	public Vec3 Max;
	public Vec3 Centre => new Vec3( ( Min.x + Max.x ) * 0.5f, ( Min.y + Max.y ) * 0.5f, ( Min.z + Max.z ) * 0.5f );
}

/// <summary>Spawn validation. Ok means: a player start exists, it is inside the map, its
/// capsule is clear of solid, and a floor is under its feet.</summary>
public sealed class QuickPlaySpawnReport
{
	public bool Ok;
	/// <summary>Null when ok; else "spawn-blocked", "spawn-no-headroom",
	/// "spawn-no-floor" or "spawn-outside-map".</summary>
	public string Reason;
	public Vec3 Origin;
	public float FloorHeight = float.NaN;
	public bool HasFloor;
	public int LeafIndex = -1;
}

/// <summary>Headless walk validation. Unblocked means every sample stayed inside the map,
/// clear of solid, on a floor within step/drop limits.</summary>
public sealed class QuickPlayWalkReport
{
	public bool Blocked;
	/// <summary>Null when unblocked; else "walk-blocked", "walk-left-map",
	/// "walk-no-floor", "walk-step-too-high", "walk-drop-too-far", or the spawn reason
	/// when the walk never started.</summary>
	public string BlockReason;
	/// <summary>Brush id that stopped the walk, for "walk-blocked" only.</summary>
	public string BlockedBy;
	public Vec3 Start;
	public Vec3 End;
	public float DistanceWalked;
	public int StepsTaken;
	public float FloorHeightAtEnd = float.NaN;
	public bool InsideAtEnd;
}

/// <summary>Everything a BSP-only Quick Play bake produced.</summary>
public sealed class QuickPlayBakeResult
{
	/// <summary>False when the spawn leaf flood-fills to a void leaf through the portals.</summary>
	public bool Sealed;
	/// <summary>The portal the leak exits through (non-void -> void), null when sealed.</summary>
	public QuickPlayPortal LeakPortal;
	/// <summary>Leak path from the flood source to past the gap (LeakFinder's path);
	/// empty when sealed. Point-file export of this path is LeakFinder's job.</summary>
	public IReadOnlyList<Vec3> LeakPath;
	public int EmptyLeafCount;
	public int SolidLeafCount;
	public int VoidLeafCount;
	public int PortalCount;
	public int CollisionHullCount;
	public int DisplacementHullCount;
	public int WorldBrushCount;
	public int DetailBrushCount;
	public List<string> Warnings = new List<string>();
	public IReadOnlyList<QuickPlayLeaf> Leaves;
	public IReadOnlyList<QuickPlayPortal> Portals;

	/// <summary>One convex hull per solid brush plus the displacement hulls.</summary>
	public IReadOnlyList<CollisionShape> CollisionHulls;

	public QuickPlaySpawnReport Spawn;
	public QuickPlayWalkReport Walk;
}

/// <summary>BSP-only Quick Play bake. Throws <see cref="QuickPlayException"/> with a named
/// code for fatal input (no player start, empty map, invalid brush/displacement, too
/// complex); spawn and walk problems are reported, not thrown.</summary>
public static class QuickPlay
{
	const float Eps = 1e-3f;

	public static QuickPlayBakeResult Bake( QuickPlayMap map, QuickPlayOptions options = null )
	{
		if ( map is null )
			throw new ArgumentNullException( nameof( map ) );
		options ??= new QuickPlayOptions();

		var world = new List<QuickPlayBrush>();
		var detail = new List<QuickPlayBrush>();
		foreach ( var b in map.Brushes )
		{
			if ( b is null || !b.IsValid )
				throw new QuickPlayException( QuickPlayError.InvalidBrush,
					"invalid brush '" + ( b is null ? "null" : b.Id ) + "': a brush needs Min < Max on every axis" );
			if ( b.Detail ) detail.Add( b ); else world.Add( b );
		}
		if ( world.Count == 0 )
			throw new QuickPlayException( QuickPlayError.EmptyMap, "empty-map: no world brushes to bake" );
		foreach ( var d in map.Displacements )
			if ( d is null || !d.IsValid )
				throw new QuickPlayException( QuickPlayError.InvalidDisplacement,
					"invalid displacement: needs a grid of at least 2x2 with matching heights" );

		var spawnEntity = map.PlayerStart;
		if ( spawnEntity is null )
			throw new QuickPlayException( QuickPlayError.NoPlayerStart,
				"no-player-start: the map has no info_player_start entity" );

		// ---- BSP: deterministic axis-aligned partition on world-brush face planes. ----
		var planes = SplitPlanes( world );
		Vec3 rootMin, rootMax;
		RootBounds( world, options.VoidMargin, out rootMin, out rootMax );
		var leaves = BuildLeaves( rootMin, rootMax, planes, options.MaxLeaves );
		ClassifyLeaves( leaves, world, rootMin, rootMax );
		var portals = BuildPortals( leaves );

		var result = new QuickPlayBakeResult
		{
			Leaves = leaves,
			Portals = portals,
			PortalCount = portals.Count,
			WorldBrushCount = world.Count,
			DetailBrushCount = detail.Count,
		};
		foreach ( var leaf in leaves )
		{
			if ( leaf.Solid ) result.SolidLeafCount++;
			else if ( leaf.IsVoid ) result.VoidLeafCount++;
			else result.EmptyLeafCount++;
		}

		// ---- Collision: one convex hull per solid brush, ConvexDecomp for displacements. ----
		var hulls = new List<CollisionShape>();
		foreach ( var b in map.Brushes )
		{
			// A box IS its own convex hull: emit its 8 corners directly. Do NOT route
			// this through ConvexHull.Build — probed 2026-11-13, it returns 6 points /
			// 8 faces for a cube's 8 corners (two top corners on one side dropped as
			// "coplanar-cap" vertices), so that hull does not contain the brush and
			// the player could clip into the missing corner. Exact corners are the
			// same rule CollisionBuilder documents for convex solids.
			hulls.Add( new CollisionShape
			{
				Kind = CollisionKind.Hull,
				Position = b.Centre,
				Points = new List<Vec3>( b.Corners() ),
				BodyId = b.Id,
			} );
		}
		result.CollisionHullCount = hulls.Count;
		foreach ( var d in map.Displacements )
		{
			int before = hulls.Count;
			AddDisplacementHulls( d, hulls );
			result.DisplacementHullCount += hulls.Count - before;
		}
		result.CollisionHulls = hulls;

		if ( map.PlayerStartCount > 1 )
			result.Warnings.Add( "multiple-player-starts:" + map.PlayerStartCount );

		// ---- Spawn + walk validation. ----
		result.Spawn = ValidateSpawn( map, world, detail, spawnEntity, leaves, options );
		result.Walk = ValidateWalk( map, world, detail, spawnEntity, result.Spawn, leaves, options );

		// ---- Sealed: flood-fill from the spawn (or the first interior leaf when the
		// spawn is unusable) over the portals, via LeakFinder — the leak ticket's
		// landed API. Reaching a void leaf is a leak; LeakFinder owns the point-file
		// export for the path itself. ----
		result.Sealed = true;
		result.LeakPath = Array.Empty<Vec3>();
		var leakGraph = new LeakFinder.LeakGraph();
		foreach ( var leaf in leaves )
			leakGraph.AddLeaf( new LeakFinder.LeakLeaf( leaf.Index, leaf.Centre, leaf.Min, leaf.Max, leaf.IsVoid, leaf.Solid ) );
		for ( int i = 0; i < portals.Count; i++ )
			leakGraph.AddPortal( new LeakFinder.LeakPortal( i, portals[i].LeafA, portals[i].LeafB, portals[i].Centre ) );
		bool haveFloodStart = false;
		Vec3 floodStart = Vec3.Zero;
		if ( result.Spawn.LeafIndex >= 0 && !leaves[result.Spawn.LeafIndex].Solid )
		{
			floodStart = spawnEntity.Origin;
			haveFloodStart = true;
		}
		else
		{
			var interior = leaves.Find( l => !l.Solid && !l.IsVoid );
			if ( interior != null )
			{
				floodStart = interior.Centre;
				haveFloodStart = true;
			}
		}
		if ( haveFloodStart )
		{
			var leak = LeakFinder.FindLeak( leakGraph, floodStart );
			result.Sealed = !leak.IsLeaked;
			result.LeakPath = leak.Path;
			if ( leak.IsLeaked && leak.GapPortalId >= 0 )
				result.LeakPortal = portals[leak.GapPortalId];
		}

		return result;
	}

	struct SplitPlane
	{
		public int Axis;
		public float Coord;
	}

	static List<SplitPlane> SplitPlanes( List<QuickPlayBrush> world )
	{
		var seen = new HashSet<string>();
		var planes = new List<SplitPlane>();
		foreach ( var b in world )
		{
			AddPlane( planes, seen, 0, b.Min.x );
			AddPlane( planes, seen, 0, b.Max.x );
			AddPlane( planes, seen, 1, b.Min.y );
			AddPlane( planes, seen, 1, b.Max.y );
			AddPlane( planes, seen, 2, b.Min.z );
			AddPlane( planes, seen, 2, b.Max.z );
		}
		planes.Sort( ( a, b ) => a.Axis != b.Axis ? a.Axis.CompareTo( b.Axis ) : a.Coord.CompareTo( b.Coord ) );
		return planes;
	}

	static void AddPlane( List<SplitPlane> planes, HashSet<string> seen, int axis, float coord )
	{
		string key = axis + ":" + coord.ToString( "R" );
		if ( seen.Add( key ) )
			planes.Add( new SplitPlane { Axis = axis, Coord = coord } );
	}

	static void RootBounds( List<QuickPlayBrush> world, float margin, out Vec3 min, out Vec3 max )
	{
		min = world[0].Min;
		max = world[0].Max;
		foreach ( var b in world )
		{
			min = new Vec3( MathF.Min( min.x, b.Min.x ), MathF.Min( min.y, b.Min.y ), MathF.Min( min.z, b.Min.z ) );
			max = new Vec3( MathF.Max( max.x, b.Max.x ), MathF.Max( max.y, b.Max.y ), MathF.Max( max.z, b.Max.z ) );
		}
		min = new Vec3( min.x - margin, min.y - margin, min.z - margin );
		max = new Vec3( max.x + margin, max.y + margin, max.z + margin );
	}

	static float AxisOf( Vec3 v, int axis ) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;

	static Vec3 WithAxis( Vec3 v, int axis, float value ) =>
		axis == 0 ? new Vec3( value, v.y, v.z ) : axis == 1 ? new Vec3( v.x, value, v.z ) : new Vec3( v.x, v.y, value );

	static List<QuickPlayLeaf> BuildLeaves( Vec3 rootMin, Vec3 rootMax, List<SplitPlane> planes, int maxLeaves )
	{
		var leaves = new List<QuickPlayLeaf>();
		var stack = new Stack<(Vec3, Vec3)>();
		stack.Push( (rootMin, rootMax) );
		while ( stack.Count > 0 )
		{
			var (lo, hi) = stack.Pop();
			int split = -1;
			for ( int i = 0; i < planes.Count; i++ )
			{
				float c = planes[i].Coord;
				if ( AxisOf( lo, planes[i].Axis ) + 1e-4f < c && c < AxisOf( hi, planes[i].Axis ) - 1e-4f )
				{
					split = i;
					break;
				}
			}
			if ( split < 0 )
			{
				leaves.Add( new QuickPlayLeaf { Min = lo, Max = hi } );
				continue;
			}
			var plane = planes[split];
			stack.Push( (WithAxis( lo, plane.Axis, plane.Coord ), hi) );
			stack.Push( (lo, WithAxis( hi, plane.Axis, plane.Coord )) );
			if ( leaves.Count + stack.Count > maxLeaves )
				throw new QuickPlayException( QuickPlayError.TooComplex,
					"too-complex: BSP exceeds " + maxLeaves + " leaves" );
		}
		leaves.Sort( ( a, b ) =>
		{
			int c = a.Min.x.CompareTo( b.Min.x );
			if ( c != 0 ) return c;
			c = a.Min.y.CompareTo( b.Min.y );
			if ( c != 0 ) return c;
			c = a.Min.z.CompareTo( b.Min.z );
			if ( c != 0 ) return c;
			c = a.Max.x.CompareTo( b.Max.x );
			if ( c != 0 ) return c;
			c = a.Max.y.CompareTo( b.Max.y );
			if ( c != 0 ) return c;
			return a.Max.z.CompareTo( b.Max.z );
		} );
		for ( int i = 0; i < leaves.Count; i++ )
			leaves[i].Index = i;
		return leaves;
	}

	static void ClassifyLeaves( List<QuickPlayLeaf> leaves, List<QuickPlayBrush> world, Vec3 rootMin, Vec3 rootMax )
	{
		foreach ( var leaf in leaves )
		{
			var c = leaf.Centre;
			leaf.Solid = false;
			foreach ( var b in world )
			{
				if ( b.ContainsStrict( c ) )
				{
					leaf.Solid = true;
					break;
				}
			}
			leaf.IsVoid = !leaf.Solid &&
				( leaf.Min.x <= rootMin.x + Eps || leaf.Min.y <= rootMin.y + Eps || leaf.Min.z <= rootMin.z + Eps ||
				  leaf.Max.x >= rootMax.x - Eps || leaf.Max.y >= rootMax.y - Eps || leaf.Max.z >= rootMax.z - Eps );
		}
	}

	/// <summary>Portals join empty leaves — but never two void leaves: the void is one
	/// outside, and a portal between two of its cells says nothing about the map. A
	/// sealed room therefore has zero portals; a gap still yields the portal from the
	/// interior into the void, which is the leak exit LeakFinder reports.</summary>
	static List<QuickPlayPortal> BuildPortals( List<QuickPlayLeaf> leaves )
	{
		var portals = new List<QuickPlayPortal>();
		for ( int i = 0; i < leaves.Count; i++ )
		{
			if ( leaves[i].Solid )
				continue;
			for ( int j = i + 1; j < leaves.Count; j++ )
			{
				if ( leaves[j].Solid || ( leaves[i].IsVoid && leaves[j].IsVoid ) )
					continue;
				var portal = SharedFace( leaves[i], leaves[j] );
				if ( portal != null )
					portals.Add( portal );
			}
		}
		return portals;
	}

	static QuickPlayPortal SharedFace( QuickPlayLeaf a, QuickPlayLeaf b )
	{
		for ( int axis = 0; axis < 3; axis++ )
		{
			bool touches = MathF.Abs( AxisOf( a.Max, axis ) - AxisOf( b.Min, axis ) ) < Eps ||
				MathF.Abs( AxisOf( b.Max, axis ) - AxisOf( a.Min, axis ) ) < Eps;
			if ( !touches )
				continue;
			var min = new Vec3( MathF.Max( a.Min.x, b.Min.x ), MathF.Max( a.Min.y, b.Min.y ), MathF.Max( a.Min.z, b.Min.z ) );
			var max = new Vec3( MathF.Min( a.Max.x, b.Max.x ), MathF.Min( a.Max.y, b.Max.y ), MathF.Min( a.Max.z, b.Max.z ) );
			int o1 = ( axis + 1 ) % 3, o2 = ( axis + 2 ) % 3;
			if ( AxisOf( max, o1 ) - AxisOf( min, o1 ) <= Eps || AxisOf( max, o2 ) - AxisOf( min, o2 ) <= Eps )
				continue;
			return new QuickPlayPortal { LeafA = a.Index, LeafB = b.Index, Min = min, Max = max };
		}
		return null;
	}

	static void AddDisplacementHulls( QuickPlayDisplacement d, List<CollisionShape> hulls )
	{
		var points = new List<Vec3>();
		float bottom = d.MinHeight() - 16f;
		for ( int r = 0; r < d.Rows; r++ )
		{
			for ( int c = 0; c < d.Columns; c++ )
			{
				float x = d.Origin.x + d.SizeX * c / ( d.Columns - 1 );
				float y = d.Origin.y + d.SizeY * r / ( d.Rows - 1 );
				points.Add( new Vec3( x, y, d.Heights[r, c] ) );
			}
		}
		for ( int r = 0; r < d.Rows; r++ )
		{
			for ( int c = 0; c < d.Columns; c++ )
			{
				float x = d.Origin.x + d.SizeX * c / ( d.Columns - 1 );
				float y = d.Origin.y + d.SizeY * r / ( d.Rows - 1 );
				points.Add( new Vec3( x, y, bottom ) );
			}
		}
		var decomp = ConvexDecomp.Decompose( points,
			new ConvexDecomp.Options { TargetHulls = 4, MinHulls = 1, MaxHulls = 8 } );
		if ( decomp.Hulls.Count > 0 )
		{
			foreach ( var h in decomp.Hulls )
				hulls.Add( new CollisionShape
				{
					Kind = CollisionKind.Hull,
					Position = h.Centre,
					Points = h.Points,
					BodyId = "displacement",
				} );
			return;
		}
		var fallback = ConvexHull.Build( points );
		if ( fallback != null )
			hulls.Add( new CollisionShape
			{
				Kind = CollisionKind.Hull,
				Position = d.Origin,
				Points = fallback.Value.Points,
				BodyId = "displacement",
			} );
	}

	static bool LeafContains( QuickPlayLeaf l, Vec3 p ) =>
		p.x >= l.Min.x - Eps && p.x <= l.Max.x + Eps &&
		p.y >= l.Min.y - Eps && p.y <= l.Max.y + Eps &&
		p.z >= l.Min.z - Eps && p.z <= l.Max.z + Eps;

	/// <summary>Leaf containing the point. A point resting exactly on a face (a spawn
	/// standing on the floor, z == 0) is inside both the floor's solid leaf and the
	/// interior empty leaf — the empty leaf wins, same rule as
	/// <see cref="LeakFinder.LeakGraph.LeafContaining"/>.</summary>
	static int LeafAt( List<QuickPlayLeaf> leaves, Vec3 p )
	{
		for ( int i = 0; i < leaves.Count; i++ )
			if ( !leaves[i].Solid && LeafContains( leaves[i], p ) )
				return i;
		for ( int i = 0; i < leaves.Count; i++ )
			if ( LeafContains( leaves[i], p ) )
				return i;
		return -1;
	}

	static bool PointInSolid( List<QuickPlayBrush> brushes, Vec3 p )
	{
		foreach ( var b in brushes )
			if ( b.ContainsStrict( p ) )
				return true;
		return false;
	}

	/// <summary>True when the vertical capsule at (x, y) spanning [z0, z1] touches a solid
	/// brush. A brush the capsule stands on (top == z0) does not count.</summary>
	static bool CapsuleHit( List<QuickPlayBrush> brushes, float x, float y, float z0, float z1,
		float radius, out QuickPlayBrush hit )
	{
		hit = null;
		foreach ( var b in brushes )
		{
			if ( b.Max.z <= z0 + Eps || b.Min.z >= z1 - Eps )
				continue;
			float cx = Math.Clamp( x, b.Min.x, b.Max.x );
			float cy = Math.Clamp( y, b.Min.y, b.Max.y );
			float dx = x - cx, dy = y - cy;
			if ( dx * dx + dy * dy < radius * radius )
			{
				hit = b;
				return true;
			}
		}
		return false;
	}

	/// <summary>Highest exposed solid top at (x, y) at or below <paramref name="topLimit"/>:
	/// a top counts only when the point half a unit above it is not inside another solid
	/// (so a floor under a crate reports the crate top, and a wall top is never mistaken
	/// for the floor under it). Displacements contribute their bilinear height.</summary>
	static bool ExposedTopAt( QuickPlayMap map, List<QuickPlayBrush> solids, float x, float y,
		float topLimit, out float top )
	{
		top = float.NaN;
		bool found = false;
		foreach ( var b in solids )
		{
			if ( !b.ContainsXY( x, y ) || b.Max.z > topLimit + Eps )
				continue;
			if ( PointInSolid( solids, new Vec3( x, y, b.Max.z + 0.5f ) ) )
				continue;
			if ( !found || b.Max.z > top )
			{
				top = b.Max.z;
				found = true;
			}
		}
		foreach ( var d in map.Displacements )
		{
			if ( !d.ContainsXY( x, y ) )
				continue;
			float h = d.HeightAt( x, y );
			if ( h > topLimit + Eps )
				continue;
			if ( PointInSolid( solids, new Vec3( x, y, h + 0.5f ) ) )
				continue;
			if ( !found || h > top )
			{
				top = h;
				found = true;
			}
		}
		return found;
	}

	static QuickPlaySpawnReport ValidateSpawn( QuickPlayMap map, List<QuickPlayBrush> world,
		List<QuickPlayBrush> detail, QuickPlayEntity spawn, List<QuickPlayLeaf> leaves, QuickPlayOptions options )
	{
		var report = new QuickPlaySpawnReport { Origin = spawn.Origin };
		var solids = new List<QuickPlayBrush>( world );
		solids.AddRange( detail );

		int leaf = LeafAt( leaves, spawn.Origin );
		report.LeafIndex = leaf;
		if ( leaf < 0 )
		{
			report.Reason = "spawn-outside-map";
			return report;
		}
		if ( leaves[leaf].Solid || PointInSolid( solids, spawn.Origin ) )
		{
			report.Reason = "spawn-blocked";
			return report;
		}
		if ( CapsuleHit( solids, spawn.Origin.x, spawn.Origin.y, spawn.Origin.z,
				spawn.Origin.z + options.PlayerHeight, options.PlayerRadius, out var hit ) )
		{
			report.Reason = hit.Min.z > spawn.Origin.z + 1f ? "spawn-no-headroom" : "spawn-blocked";
			return report;
		}
		if ( !ExposedTopAt( map, solids, spawn.Origin.x, spawn.Origin.y, spawn.Origin.z + 1f, out var floor ) )
		{
			report.Reason = "spawn-no-floor";
			return report;
		}
		report.FloorHeight = floor;
		report.HasFloor = true;
		report.Ok = true;
		return report;
	}

	static QuickPlayWalkReport ValidateWalk( QuickPlayMap map, List<QuickPlayBrush> world,
		List<QuickPlayBrush> detail, QuickPlayEntity spawn, QuickPlaySpawnReport spawnReport,
		List<QuickPlayLeaf> leaves, QuickPlayOptions options )
	{
		var solids = new List<QuickPlayBrush>( world );
		solids.AddRange( detail );
		var report = new QuickPlayWalkReport { Start = spawn.Origin, End = spawn.Origin };
		if ( !spawnReport.Ok )
		{
			report.Blocked = true;
			report.BlockReason = spawnReport.Reason;
			report.FloorHeightAtEnd = spawnReport.FloorHeight;
			report.InsideAtEnd = spawnReport.LeafIndex >= 0 && !leaves[spawnReport.LeafIndex].Solid &&
				!leaves[spawnReport.LeafIndex].IsVoid;
			return report;
		}

		float yaw = spawn.Yaw * MathF.PI / 180f;
		float dirX = MathF.Cos( yaw ), dirY = MathF.Sin( yaw );
		float travelled = 0f;
		float floor = spawnReport.FloorHeight;
		var pos = spawn.Origin;
		int steps = 0;
		float lastGood = 0f;
		while ( travelled < options.WalkDistance - 1e-6f )
		{
			float step = Math.Min( options.WalkStep, options.WalkDistance - travelled );
			travelled += step;
			float nx = spawn.Origin.x + dirX * travelled;
			float ny = spawn.Origin.y + dirY * travelled;

			if ( CapsuleHit( solids, nx, ny, floor, floor + options.PlayerHeight, options.PlayerRadius, out var hit ) )
			{
				report.Blocked = true;
				report.BlockReason = "walk-blocked";
				report.BlockedBy = hit.Id;
				break;
			}
			int leaf = LeafAt( leaves, new Vec3( nx, ny, floor + 1f ) );
			if ( leaf < 0 || leaves[leaf].Solid || leaves[leaf].IsVoid )
			{
				report.Blocked = true;
				report.BlockReason = "walk-left-map";
				break;
			}
			if ( !ExposedTopAt( map, solids, nx, ny, floor + options.PlayerHeight, out var raw ) )
			{
				report.Blocked = true;
				report.BlockReason = "walk-no-floor";
				break;
			}
			if ( raw > floor + options.StepHeight + Eps )
			{
				report.Blocked = true;
				report.BlockReason = "walk-step-too-high";
				break;
			}
			if ( raw < floor - options.MaxDrop - Eps )
			{
				report.Blocked = true;
				report.BlockReason = "walk-drop-too-far";
				break;
			}
			pos = new Vec3( nx, ny, raw );
			floor = raw;
			steps++;
			lastGood = travelled;
		}
		report.End = pos;
		// DistanceWalked is the last good sample, not the blocked one: the player
		// did not walk into the wall, the step, or the void.
		report.DistanceWalked = report.Blocked ? lastGood : options.WalkDistance;
		report.StepsTaken = steps;
		report.FloorHeightAtEnd = floor;
		report.InsideAtEnd = !report.Blocked;
		return report;
	}
}
