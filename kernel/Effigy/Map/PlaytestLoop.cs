using System;
using System.Collections.Generic;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P2: in-editor playtest / hot-reload loop
/// (hammer-playtest-hotreload-loop). Headless protocol, no game UI:
/// incremental re-bake + pre-launch validation + re-run signalling that
/// an AI or script drives while the game stays running.
///
/// The loop, in order: <see cref="PlaytestLoop.PreLaunchValidation"/>
/// must pass, then <see cref="PlaytestLoop.IncrementalRebake"/> re-bakes
/// only what changed since the last bake, then
/// <see cref="PlaytestLoop.RequestReRun"/> issues the reload signal
/// (changelevel when a session is already running, map for a fresh
/// launch) with the map path. No game process is launched by this
/// ticket itself: <see cref="ReRunCommand"/> is the command definition
/// (signal + map path); the ops-CLI ticket owns the process/CLI side.
///
/// Dependency status at write time (2026-11-13, verified against the
/// tree): MapDocument (hammer-map-document-model), MapBrush
/// (hammer-brush-core-plane-based-brushes), BspBuilder
/// (hammer-bsp-core-tree-portals-leaves), QuickPlay
/// (hammer-quick-play-bsp-only-bake), CompileRunner
/// (hammer-compile-native-pipeline), LightmapBake
/// (hammer-lightmap-progressive-bake) and the map ops commands
/// (hammer-ai-ops-cli-map-commands) have NOT landed — the files do not
/// exist on disk, only the claims do. Per the ticket ("implement against
/// the spec exactly as filed and report any mismatch"), this file
/// therefore builds on the types that HAVE landed, exactly as the
/// sibling IssueEngine did:
///   - The document is <see cref="IssueDocument"/> (Effigy.Map): brushes
///     as plane faces + polygon vertices, entities with classname /
///     origin / keyvalues. When MapDocument lands, the integrator adds
///     ONE thin adapter projecting MapDocument onto IssueDocument; the
///     loop logic here does not change. Do NOT redefine a second
///     document snapshot type with the same responsibility.
///   - Change invalidation is the LightmapBake concept from its desc
///     ("invalidate only changed leaves/faces"), implemented here as
///     <see cref="PlaytestLoop.ComputeChangeSet"/> (per-brush content
///     hash diff -&gt; dirty leaves/faces). LightmapBake reuses that
///     method/type when it lands; this file does not depend on it.
///   - Leaves are a uniform 256-inch grid over world space (see
///     <see cref="PlaytestLoop.LeafCellSize"/>), NOT BspBuilder
///     visleaves: BspBuilder has not landed, so there is no portal
///     graph to cut leaves from yet. The grid is anchored at the world
///     origin (never at the document bounds) so moving a brush never
///     shifts the leaf layout. When BspBuilder lands, the integrator
///     swaps the leaf assignment for real visleaves behind the same
///     <see cref="BakeChangeSet"/> shape; the change-set logic does
///     not change. A leaf here is therefore "the set of grid cells a
///     brush touches", honestly labelled, not a visleaf.
///   - The ops surface is the command-name constants in
///     <see cref="PlaytestOpsCommands"/> plus
///     <see cref="ReRunCommand"/>; the ops-CLI ticket wires them.
///
/// Geometry is the kernel's inches and Z-up (see handoffs/CONTRACTS.md).
/// Brush faces are outward-wound; a brush is the intersection of its
/// face half-spaces (solid = Dot(N, p) &lt;= Distance for every face).
/// Nothing here shells out to Hammer / VBSP / VVIS / VRAD, launches a
/// game, or touches UI types. Engine-free kernel code only.
/// </summary>
public enum ReRunSignal
{
	/// <summary>Signal the RUNNING session to switch maps without
	/// disconnecting (in-game console: "changelevel &lt;map&gt;").
	/// This is the hot-reload path: the game stays up.</summary>
	ChangeLevel,
	/// <summary>Fresh launch (in-game console: "map &lt;map&gt;").
	/// Used when no playtest session is running yet.</summary>
	ReloadMap,
}

/// <summary>Pre-launch finding severity. Errors block a re-run;
/// warnings do not.</summary>
public enum PreLaunchSeverity
{
	Error,
	Warning,
}

/// <summary>One pre-launch validation finding. <see cref="Code"/> is
/// the stable machine code the ops CLI and AI drivers switch on.</summary>
public sealed class PreLaunchIssue
{
	public string Code = "";
	public string Message = "";
	public PreLaunchSeverity Severity;
	/// <summary>Entity the finding is about, or -1.</summary>
	public int EntityId = -1;

	public override string ToString() =>
		"[" + Severity + " " + Code + "] " + Message;
}

/// <summary>Result of <see cref="PlaytestLoop.PreLaunchValidation"/>.
/// <see cref="Passed"/> is true only when there are no Error issues.
/// Named error codes: NO_PLAYER_START, SPAWN_INSIDE_SOLID,
/// NAVMESH_MISSING, EMPTY_MAP. Named warning code:
/// MULTIPLE_PLAYER_STARTS.</summary>
public sealed class PreLaunchResult
{
	public bool Passed;
	public List<PreLaunchIssue> Issues = new List<PreLaunchIssue>();
	/// <summary>True when the map needs a navmesh (it has NPC /
	/// navigation-node entities).</summary>
	public bool NavmeshRequired;
	public bool HasPlayerStart;
	public int PlayerStartCount;

	public bool HasError( string code )
	{
		if ( string.IsNullOrEmpty( code ) || Issues is null )
			return false;
		foreach ( var issue in Issues )
			if ( issue != null && issue.Severity == PreLaunchSeverity.Error &&
				string.Equals( issue.Code, code, StringComparison.Ordinal ) )
				return true;
		return false;
	}

	public bool HasIssue( string code )
	{
		if ( string.IsNullOrEmpty( code ) || Issues is null )
			return false;
		foreach ( var issue in Issues )
			if ( issue != null &&
				string.Equals( issue.Code, code, StringComparison.Ordinal ) )
				return true;
		return false;
	}
}

/// <summary>One leaf of the incremental-bake grid: the uniform cell at
/// grid coordinates (X, Y, Z), each cell
/// <see cref="PlaytestLoop.LeafCellSize"/> inches on a side, anchored
/// at the world origin. A brush touches every cell its axis-aligned
/// bounding box overlaps. This is a grid cell, not (yet) a BspBuilder
/// visleaf — see the file header.</summary>
public readonly struct PlaytestLeaf : IEquatable<PlaytestLeaf>
{
	public readonly int X, Y, Z;

	public PlaytestLeaf( int x, int y, int z )
	{
		X = x;
		Y = y;
		Z = z;
	}

	public bool Equals( PlaytestLeaf other ) => X == other.X && Y == other.Y && Z == other.Z;
	public override bool Equals( object obj ) => obj is PlaytestLeaf other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( X, Y, Z );
	public override string ToString() => "(" + X + ", " + Y + ", " + Z + ")";
}

/// <summary>What changed between a document and a previous bake, and
/// exactly what must be re-baked. This is the change-invalidation
/// seam: the LightmapBake ticket reuses this type (per-brush hash diff
/// -&gt; dirty leaves/faces) instead of inventing its own. DirtyLeaves is
/// the union of the old leaves of changed/removed brushes and the new
/// leaves of changed/added brushes; ReusedLeaves counts new leaves
/// that are NOT dirty. DirtyFaces counts the faces of changed + added
/// brushes (new counts) plus the faces of removed brushes (old
/// counts); ReusedFaces counts the faces of unchanged brushes.</summary>
public sealed class BakeChangeSet
{
	public bool WasFull;
	public List<int> ChangedBrushIds = new List<int>();
	public List<int> AddedBrushIds = new List<int>();
	public List<int> RemovedBrushIds = new List<int>();
	public List<PlaytestLeaf> DirtyLeaves = new List<PlaytestLeaf>();
	public int DirtyFaces;
	public int ReusedFaces;
	public int ReusedLeaves;
	public int TotalLeaves;
	public int TotalFaces;
}

/// <summary>Snapshot of one bake: per-brush content hashes, per-brush
/// leaf membership and per-brush face counts, plus the totals. The
/// next <see cref="PlaytestLoop.IncrementalRebake"/> diffs the live
/// document against this snapshot. Build one only through
/// <see cref="PlaytestLoop.Bake"/> / IncrementalRebake; a
/// default-constructed snapshot (Revision 0, no brushes) is treated
/// as "no previous bake" and forces a full re-bake.</summary>
public sealed class PlaytestBake
{
	public string DocumentName = "";
	public string MapPath = "";
	public int Revision;
	public int TotalLeaves;
	public int TotalFaces;
	/// <summary>True when this snapshot came from a full bake.</summary>
	public bool IsFull;

	readonly Dictionary<int, long> _hashes = new Dictionary<int, long>();
	readonly Dictionary<int, List<PlaytestLeaf>> _leaves = new Dictionary<int, List<PlaytestLeaf>>();
	readonly Dictionary<int, int> _faceCounts = new Dictionary<int, int>();

	public int BrushCount => _hashes.Count;

	public bool HasBrush( int brushId ) => _hashes.ContainsKey( brushId );

	public long BrushHash( int brushId ) =>
		_hashes.TryGetValue( brushId, out var hash ) ? hash : 0;

	public int BrushFaceCount( int brushId ) =>
		_faceCounts.TryGetValue( brushId, out var count ) ? count : 0;

	public IReadOnlyList<PlaytestLeaf> LeavesForBrush( int brushId ) =>
		_leaves.TryGetValue( brushId, out var leaves )
			? leaves
			: Array.Empty<PlaytestLeaf>();

	public List<int> BrushIds() => new List<int>( _hashes.Keys );

	internal void Record( int brushId, long hash, List<PlaytestLeaf> leaves, int faceCount )
	{
		_hashes[brushId] = hash;
		_leaves[brushId] = leaves ?? new List<PlaytestLeaf>();
		_faceCounts[brushId] = faceCount;
	}
}

/// <summary>Result of an incremental re-bake: the new snapshot plus
/// the change set that produced it.</summary>
public sealed class RebakeResult
{
	public PlaytestBake Bake = new PlaytestBake();
	public BakeChangeSet ChangeSet = new BakeChangeSet();
}

/// <summary>The re-run command definition: the reload signal plus the
/// map path. This is data, not a process launch — nothing here starts
/// a game, talks to a running game, or shells out. The ops-CLI ticket
/// owns delivery; <see cref="ToConsoleCommand"/> renders the in-game
/// console command the signal corresponds to.</summary>
public sealed class ReRunCommand
{
	/// <summary>Map to load, as the game addresses it
	/// (e.g. "maps/sealed_room.bsp").</summary>
	public string MapPath = "";
	public ReRunSignal Signal;
	/// <summary>Why this signal was chosen (session running or not).</summary>
	public string Reason = "";
	/// <summary>Revision of the bake this re-run reloads.</summary>
	public int BakeRevision;

	public bool IsValid => !string.IsNullOrWhiteSpace( MapPath );

	/// <summary>In-game console command: "changelevel &lt;map&gt;" for
	/// a running session, "map &lt;map&gt;" for a fresh launch.</summary>
	public string ToConsoleCommand() =>
		( Signal == ReRunSignal.ChangeLevel ? "changelevel " : "map " ) + MapPath;

	public override string ToString() => ToConsoleCommand();
}

/// <summary>Result of <see cref="PlaytestLoop.RequestReRun"/>: the
/// validation that gated it, the re-bake it ran (null when validation
/// refused the re-run), and the issued command (null when refused).
/// <see cref="Issued"/> is true only when validation passed AND a
/// command was built.</summary>
public sealed class ReRunResult
{
	public bool Issued;
	public ReRunCommand Command;
	public PreLaunchResult Validation = new PreLaunchResult();
	public RebakeResult Rebake;
}

/// <summary>Ops-CLI command surface for the loop, as the
/// hammer-ai-ops-cli-map-commands ticket wires it (that ticket has not
/// landed yet; these names are the contract it builds against).
/// Full invocations are "ops map playtest &lt;map&gt;" and
/// "ops map rerun &lt;map&gt;".</summary>
public static class PlaytestOpsCommands
{
	/// <summary>Full bake + pre-launch validation + first run:
	/// "ops map playtest &lt;map&gt;".</summary>
	public const string Playtest = "map playtest";
	/// <summary>Validate + incremental re-bake + reload signal:
	/// "ops map rerun &lt;map&gt;".</summary>
	public const string ReRun = "map rerun";
}

/// <summary>In-editor playtest / hot-reload loop (headless). All
/// methods are pure over the document snapshot except for the
/// <see cref="ReRunCommand"/> they return: nothing here launches a
/// game process, opens a window, or performs IO.</summary>
public static class PlaytestLoop
{
	/// <summary>Leaf grid cell size in inches. 256 matches the
	/// Source leaf scale closely enough for an incremental-bake
	/// stand-in; BspBuilder visleaves replace the grid when they land.</summary>
	public const float LeafCellSize = 256f;

	/// <summary>A brush whose bounding box spans more than this many
	/// cells on any axis is treated as touching only the cell holding
	/// its centroid (degenerate / room-sized brush guard: without it
	/// one pathological brush could mint hundreds of thousands of
	/// leaves). Real map brushes are far below this.</summary>
	public const int MaxLeafSpanPerAxis = 32;

	/// <summary>A player start counts as inside solid only when its
	/// origin is at least this far (inches) inside EVERY face
	/// half-space of a brush. A spawn standing exactly ON a face —
	/// feet on the floor — is legal and must not be flagged.</summary>
	public const float SpawnSolidMargin = 0.5f;

	/// <summary>Bake snapshot schema version, bumped if
	/// <see cref="PlaytestBake"/> changes shape.</summary>
	public const int BakeSchemaVersion = 1;

	/// <summary>Full bake: every brush is (re)baked, every leaf is
	/// dirty. Revision starts at 1.</summary>
	public static PlaytestBake Bake( IssueDocument document, string mapPath )
	{
		ArgumentNullException.ThrowIfNull( document );
		var bake = new PlaytestBake
		{
			DocumentName = document.Name ?? "",
			MapPath = mapPath ?? "",
			Revision = 1,
			IsFull = true,
		};
		FillSnapshot( document, bake );
		return bake;
	}

	/// <summary>Incremental re-bake: diff <paramref name="document"/>
	/// against <paramref name="lastBake"/> and re-bake only the
	/// changed brushes' leaves/faces. A null last bake, a
	/// never-baked snapshot (Revision &lt;= 0), or a document whose name
	/// differs from the last bake's forces a FULL re-bake (the guard
	/// against diffing two different maps). The returned bake keeps
	/// the last bake's map path and advances its revision (a full
	/// re-bake of a different document restarts at revision 1).</summary>
	public static RebakeResult IncrementalRebake( IssueDocument document, PlaytestBake lastBake )
	{
		ArgumentNullException.ThrowIfNull( document );
		var changeSet = ComputeChangeSet( document, lastBake );
		var bake = new PlaytestBake
		{
			DocumentName = document.Name ?? "",
			MapPath = lastBake != null ? lastBake.MapPath ?? "" : "",
			Revision = changeSet.WasFull && lastBake != null && lastBake.Revision > 0 &&
				!string.Equals( document.Name ?? "", lastBake.DocumentName ?? "", StringComparison.Ordinal )
				? 1
				: ( lastBake != null && lastBake.Revision > 0 ? lastBake.Revision + 1 : 1 ),
			IsFull = changeSet.WasFull,
		};
		FillSnapshot( document, bake );
		return new RebakeResult { Bake = bake, ChangeSet = changeSet };
	}

	/// <summary>Change invalidation (the LightmapBake seam): compare
	/// the live document against a previous bake by per-brush content
	/// hash and report exactly which brushes changed / were added /
	/// removed and which leaves/faces that dirties. A null last bake
	/// or a document-name mismatch yields a full change set
	/// (WasFull, everything dirty). Pure: it never mutates either
	/// input.</summary>
	public static BakeChangeSet ComputeChangeSet( IssueDocument document, PlaytestBake lastBake )
	{
		ArgumentNullException.ThrowIfNull( document );
		var brushes = document.Brushes ?? new List<IssueBrush>();
		var changeSet = new BakeChangeSet();

		var currentLeaves = new HashSet<PlaytestLeaf>();
		var currentLeafByBrush = new Dictionary<int, List<PlaytestLeaf>>();
		var currentHashByBrush = new Dictionary<int, long>();
		var currentFaceCountByBrush = new Dictionary<int, int>();
		var totalFaces = 0;
		foreach ( var brush in brushes )
		{
			if ( brush is null )
				continue;
			var leaves = LeavesForBrush( brush );
			currentLeafByBrush[brush.Id] = leaves;
			currentHashByBrush[brush.Id] = HashBrush( brush );
			var faceCount = brush.Faces != null ? brush.Faces.Count : 0;
			currentFaceCountByBrush[brush.Id] = faceCount;
			totalFaces += faceCount;
			foreach ( var leaf in leaves )
				currentLeaves.Add( leaf );
		}
		changeSet.TotalLeaves = currentLeaves.Count;
		changeSet.TotalFaces = totalFaces;

		if ( lastBake is null || lastBake.Revision <= 0 ||
			!string.Equals( document.Name ?? "", lastBake.DocumentName ?? "", StringComparison.Ordinal ) )
		{
			changeSet.WasFull = true;
			foreach ( var id in currentHashByBrush.Keys )
			{
				changeSet.ChangedBrushIds.Add( id );
				changeSet.AddedBrushIds.Add( id );
			}
			if ( lastBake != null )
				foreach ( var id in lastBake.BrushIds() )
					if ( !currentHashByBrush.ContainsKey( id ) )
						changeSet.RemovedBrushIds.Add( id );
			changeSet.DirtyLeaves.AddRange( currentLeaves );
			changeSet.DirtyFaces = totalFaces;
			changeSet.ReusedFaces = 0;
			changeSet.ReusedLeaves = 0;
			return changeSet;
		}

		var dirtyLeaves = new HashSet<PlaytestLeaf>();
		var dirtyFaces = 0;
		var reusedFaces = 0;
		foreach ( var id in currentHashByBrush.Keys )
		{
			if ( !lastBake.HasBrush( id ) )
			{
				changeSet.AddedBrushIds.Add( id );
				dirtyFaces += currentFaceCountByBrush[id];
				foreach ( var leaf in currentLeafByBrush[id] )
					dirtyLeaves.Add( leaf );
			}
			else if ( lastBake.BrushHash( id ) != currentHashByBrush[id] )
			{
				changeSet.ChangedBrushIds.Add( id );
				dirtyFaces += currentFaceCountByBrush[id];
				foreach ( var leaf in currentLeafByBrush[id] )
					dirtyLeaves.Add( leaf );
				foreach ( var leaf in lastBake.LeavesForBrush( id ) )
					dirtyLeaves.Add( leaf );
			}
			else
			{
				reusedFaces += currentFaceCountByBrush[id];
			}
		}
		foreach ( var id in lastBake.BrushIds() )
		{
			if ( currentHashByBrush.ContainsKey( id ) )
				continue;
			changeSet.RemovedBrushIds.Add( id );
			dirtyFaces += lastBake.BrushFaceCount( id );
			foreach ( var leaf in lastBake.LeavesForBrush( id ) )
				dirtyLeaves.Add( leaf );
		}
		changeSet.DirtyLeaves.AddRange( dirtyLeaves );
		changeSet.DirtyFaces = dirtyFaces;
		changeSet.ReusedFaces = reusedFaces;
		var dirtyInNew = 0;
		foreach ( var leaf in dirtyLeaves )
			if ( currentLeaves.Contains( leaf ) )
				dirtyInNew++;
		changeSet.ReusedLeaves = currentLeaves.Count - dirtyInNew;
		changeSet.WasFull = false;
		return changeSet;
	}

	/// <summary>Pre-launch validation — the gate that must pass
	/// before a re-run is issued. Checks, in order: the map has
	/// brushes (EMPTY_MAP); at least one info_player_start exists
	/// (NO_PLAYER_START); more than one start is a warning, not an
	/// error (MULTIPLE_PLAYER_STARTS); no start sits inside solid
	/// brush geometry (SPAWN_INSIDE_SOLID, detail brushes included —
	/// detail is solid to the player even though it does not seal);
	/// when the map needs a navmesh (any npc_* entity or info_node)
	/// one must be present (NAVMESH_MISSING). Navmesh presence is a
	/// caller input: no navmesh type has landed yet, so the loop
	/// cannot observe it itself.</summary>
	public static PreLaunchResult PreLaunchValidation( IssueDocument document, bool navmeshPresent = false )
	{
		ArgumentNullException.ThrowIfNull( document );
		var result = new PreLaunchResult();
		var brushes = document.Brushes ?? new List<IssueBrush>();
		var entities = document.Entities ?? new List<IssueEntity>();

		if ( brushes.Count == 0 )
			AddError( result, "EMPTY_MAP", "Map has no brushes: there is nothing to play in.", -1 );

		var starts = new List<IssueEntity>();
		foreach ( var entity in entities )
		{
			if ( entity != null &&
				string.Equals( entity.ClassName, "info_player_start", StringComparison.OrdinalIgnoreCase ) )
				starts.Add( entity );
		}
		result.PlayerStartCount = starts.Count;
		result.HasPlayerStart = starts.Count > 0;
		if ( starts.Count == 0 )
		{
			AddError( result, "NO_PLAYER_START",
				"Map has no info_player_start: the player has nowhere to spawn.", -1 );
		}
		else
		{
			if ( starts.Count > 1 )
				AddWarning( result, "MULTIPLE_PLAYER_STARTS",
					"Map has " + starts.Count + " info_player_start entities; the playtest spawns at the first.",
					starts[0].Id );
			foreach ( var start in starts )
			{
				if ( IsInsideSolid( start.Origin, brushes ) )
					AddError( result, "SPAWN_INSIDE_SOLID",
						"Player start " + start.Id + " at " + start.Origin +
						" is inside solid brush geometry; the player would spawn stuck.",
						start.Id );
			}
		}

		var needsNav = false;
		foreach ( var entity in entities )
		{
			if ( entity is null || string.IsNullOrEmpty( entity.ClassName ) )
				continue;
			if ( entity.ClassName.StartsWith( "npc_", StringComparison.OrdinalIgnoreCase ) ||
				string.Equals( entity.ClassName, "info_node", StringComparison.OrdinalIgnoreCase ) )
			{
				needsNav = true;
				break;
			}
		}
		result.NavmeshRequired = needsNav;
		if ( needsNav && !navmeshPresent )
			AddError( result, "NAVMESH_MISSING",
				"Map has NPC / navigation entities but no navmesh is present; AI cannot move.", -1 );

		result.Passed = true;
		foreach ( var issue in result.Issues )
			if ( issue != null && issue.Severity == PreLaunchSeverity.Error )
			{
				result.Passed = false;
				break;
			}
		return result;
	}

	/// <summary>Build the re-run command for a bake: ChangeLevel when
	/// a playtest session is already running (hot-reload, the game
	/// stays up), ReloadMap for a fresh launch. The caller owns
	/// knowing whether a session is running; this ticket launches
	/// nothing itself.</summary>
	public static ReRunCommand BuildReRun( string mapPath, PlaytestBake bake, bool sessionRunning )
	{
		if ( string.IsNullOrWhiteSpace( mapPath ) )
			throw new ArgumentException( "A re-run needs a map path.", nameof( mapPath ) );
		ArgumentNullException.ThrowIfNull( bake );
		return new ReRunCommand
		{
			MapPath = mapPath,
			Signal = sessionRunning ? ReRunSignal.ChangeLevel : ReRunSignal.ReloadMap,
			Reason = sessionRunning
				? "Session running: hot-reload the running game via changelevel."
				: "No session running: fresh launch via map.",
			BakeRevision = bake.Revision,
		};
	}

	/// <summary>The headless loop in one call, in protocol order:
	/// validate, then incremental re-bake, then issue the reload
	/// signal. When validation fails nothing is re-baked and no
	/// command is issued (Issued false, Command and Rebake null) —
	/// a failed gate never reaches the running game.</summary>
	public static ReRunResult RequestReRun( IssueDocument document, PlaytestBake lastBake,
		string mapPath, bool navmeshPresent, bool sessionRunning )
	{
		ArgumentNullException.ThrowIfNull( document );
		if ( string.IsNullOrWhiteSpace( mapPath ) )
			throw new ArgumentException( "A re-run needs a map path.", nameof( mapPath ) );
		var result = new ReRunResult
		{
			Validation = PreLaunchValidation( document, navmeshPresent ),
		};
		if ( !result.Validation.Passed )
		{
			result.Issued = false;
			return result;
		}
		result.Rebake = IncrementalRebake( document, lastBake );
		result.Command = BuildReRun( mapPath, result.Rebake.Bake, sessionRunning );
		result.Issued = true;
		return result;
	}

	static void AddError( PreLaunchResult result, string code, string message, int entityId )
	{
		result.Issues.Add( new PreLaunchIssue
		{
			Code = code,
			Message = message,
			Severity = PreLaunchSeverity.Error,
			EntityId = entityId,
		} );
	}

	static void AddWarning( PreLaunchResult result, string code, string message, int entityId )
	{
		result.Issues.Add( new PreLaunchIssue
		{
			Code = code,
			Message = message,
			Severity = PreLaunchSeverity.Warning,
			EntityId = entityId,
		} );
	}

	/// <summary>Strict point-in-solid: the point is at least
	/// <see cref="SpawnSolidMargin"/> inside EVERY face half-space
	/// of the brush. A point exactly on a face (standing on the
	/// floor) fails the margin and is not "inside".</summary>
	static bool IsInsideSolid( Vec3 point, List<IssueBrush> brushes )
	{
		foreach ( var brush in brushes )
		{
			if ( brush is null || brush.Faces is null || brush.Faces.Count == 0 )
				continue;
			var inside = true;
			foreach ( var face in brush.Faces )
			{
				if ( face is null )
				{
					inside = false;
					break;
				}
				var normal = face.Normal.Normal;
				if ( (double) Vec3.Dot( normal, point ) > face.Distance - SpawnSolidMargin )
				{
					inside = false;
					break;
				}
			}
			if ( inside )
				return true;
		}
		return false;
	}

	static void FillSnapshot( IssueDocument document, PlaytestBake bake )
	{
		var brushes = document.Brushes ?? new List<IssueBrush>();
		var leaves = new HashSet<PlaytestLeaf>();
		var totalFaces = 0;
		foreach ( var brush in brushes )
		{
			if ( brush is null )
				continue;
			var brushLeaves = LeavesForBrush( brush );
			var faceCount = brush.Faces != null ? brush.Faces.Count : 0;
			totalFaces += faceCount;
			foreach ( var leaf in brushLeaves )
				leaves.Add( leaf );
			bake.Record( brush.Id, HashBrush( brush ), brushLeaves, faceCount );
		}
		bake.TotalLeaves = leaves.Count;
		bake.TotalFaces = totalFaces;
	}

	/// <summary>Grid cells a brush touches: every cell its
	/// vertex bounding box overlaps (world-origin anchored grid).</summary>
	static List<PlaytestLeaf> LeavesForBrush( IssueBrush brush )
	{
		var leaves = new List<PlaytestLeaf>();
		if ( brush is null || brush.Faces is null )
			return leaves;
		var any = false;
		var minX = float.MaxValue;
		var minY = float.MaxValue;
		var minZ = float.MaxValue;
		var maxX = float.MinValue;
		var maxY = float.MinValue;
		var maxZ = float.MinValue;
		foreach ( var face in brush.Faces )
		{
			if ( face is null || face.Vertices is null )
				continue;
			foreach ( var v in face.Vertices )
			{
				any = true;
				if ( v.x < minX ) minX = v.x;
				if ( v.y < minY ) minY = v.y;
				if ( v.z < minZ ) minZ = v.z;
				if ( v.x > maxX ) maxX = v.x;
				if ( v.y > maxY ) maxY = v.y;
				if ( v.z > maxZ ) maxZ = v.z;
			}
		}
		if ( !any )
			return leaves;
		var minCellX = Cell( minX );
		var minCellY = Cell( minY );
		var minCellZ = Cell( minZ );
		var maxCellX = Cell( maxX );
		var maxCellY = Cell( maxY );
		var maxCellZ = Cell( maxZ );
		if ( maxCellX - minCellX > MaxLeafSpanPerAxis ||
			maxCellY - minCellY > MaxLeafSpanPerAxis ||
			maxCellZ - minCellZ > MaxLeafSpanPerAxis )
		{
			var centroid = brush.Centroid();
			leaves.Add( new PlaytestLeaf( Cell( centroid.x ), Cell( centroid.y ), Cell( centroid.z ) ) );
			return leaves;
		}
		for ( var x = minCellX; x <= maxCellX; x++ )
			for ( var y = minCellY; y <= maxCellY; y++ )
				for ( var z = minCellZ; z <= maxCellZ; z++ )
					leaves.Add( new PlaytestLeaf( x, y, z ) );
		return leaves;
	}

	static int Cell( float value ) => (int) Math.Floor( value / LeafCellSize );

	/// <summary>Deterministic FNV-1a 64-bit content hash of a brush:
	/// id, detail flag, every face plane (normal bits + distance
	/// bits), material, and every vertex. Any geometry or material
	/// edit changes the hash; reordering faces does not hide a change
	/// (faces hash in order — a reorder IS a change here, which is
	/// the safe direction for an incremental bake).</summary>
	static long HashBrush( IssueBrush brush )
	{
		unchecked
		{
			ulong hash = 14695981039346656037UL;
			MixInt( ref hash, brush.Id );
			MixInt( ref hash, brush.IsDetail ? 1 : 0 );
			if ( brush.Faces != null )
			{
				foreach ( var face in brush.Faces )
				{
					if ( face is null )
					{
						MixInt( ref hash, -1 );
						continue;
					}
					MixFloat( ref hash, face.Normal.x );
					MixFloat( ref hash, face.Normal.y );
					MixFloat( ref hash, face.Normal.z );
					MixDouble( ref hash, face.Distance );
					MixString( ref hash, face.Material );
					if ( face.Vertices != null )
						foreach ( var v in face.Vertices )
						{
							MixFloat( ref hash, v.x );
							MixFloat( ref hash, v.y );
							MixFloat( ref hash, v.z );
						}
				}
			}
			return (long) hash;
		}
	}

	static void MixInt( ref ulong hash, int value )
	{
		for ( var i = 0; i < 4; i++ )
		{
			hash ^= (ulong) ( ( value >> ( i * 8 ) ) & 0xFF );
			hash *= 1099511628211UL;
		}
	}

	static void MixFloat( ref ulong hash, float value ) =>
		MixInt( ref hash, BitConverter.SingleToInt32Bits( value ) );

	static void MixDouble( ref ulong hash, double value )
	{
		var bits = BitConverter.DoubleToInt64Bits( value );
		MixInt( ref hash, (int) ( bits & 0xFFFFFFFFL ) );
		MixInt( ref hash, (int) ( ( bits >> 32 ) & 0xFFFFFFFFL ) );
	}

	static void MixString( ref ulong hash, string value )
	{
		if ( value is null )
		{
			MixInt( ref hash, -1 );
			return;
		}
		MixInt( ref hash, value.Length );
		foreach ( var ch in value )
			MixInt( ref hash, ch );
	}
}
