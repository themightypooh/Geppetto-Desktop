// Check-for-Problems API: on-demand full-map check with actionable
// Go-to / Fix / Fix-all-of-type, plus go-to-coordinates from a compile log.
//
// Hammer Desktop P2: hammer-check-problems-fix-actions (worker muse-3-desktop-hammer-26).
// Backend / data-model / API only — no Desktop UI, no viewer, no Hammer app,
// no shelling out to Hammer/Hammer++/VBSP/VVIS/VRAD.
//
// Source: Round-2 report §7 #11 (MISSED): Check for Problems is the fix loop —
// Go to error, Fix, Fix all (of type) — plus 'go to coordinates' from the
// compile log. Pass 1 has the live Issue engine (detection + fix hints) and
// leak pointfiles, but no on-demand full-map check with actionable operations.
//
// DEPENDENCY STATUS, 2026-11-13 ~16:05 EST — reconcile before assuming the
// sibling APIs exist:
//   * IssueEngine (hammer-validation-issue-engine, worker 20) has NOT landed:
//     kernel/Effigy/Map/IssueEngine.cs does not exist on disk yet, and its desc
//     names no concrete Issue type or member names (only "rule registry,
//     Run(document) -> Issue list, incremental re-check", severity + location +
//     fix hint per issue). So MapCheck does NOT reference IssueEngine types.
//     Instead it defines the seam exactly as the spec describes it:
//     <see cref="IIssueSource"/> (Run(document) -> issue list) with the
//     built-in <see cref="DefaultIssueSource"/> implementing the spec'd rules
//     (invalid brush, missing target, missing material, bad keyvalue, leak)
//     as a named rule registry. When IssueEngine lands, the final integrator
//     writes a ~20-line adapter (IssueEngineSource : IIssueSource) that maps
//     its Issue to <see cref="MapIssue"/> and passes it to
//     RunFullCheck(document, source) — no MapCheck API changes. Do not invent
//     a second check runner against IssueEngine types; adapt this one.
//   * LeakFinder (hammer-leak-detection-pointfile, worker 19) HAS landed and
//     is reused for real: the Leak rule runs LeakFinder.FindLeak over the
//     document's <see cref="LeakFinder.ILeakPortalGraph"/> and the leak issue
//     carries the gap location + point-file text.
//   * MapDocument (hammer-map-document-model, worker 12) has NOT landed, so
//     MapCheck does not reference it either. The contract is
//     <see cref="IMapCheckDocument"/> (same pattern as MapUndo's
//     IMapUndoTarget): the real MapDocument (or a thin adapter written by the
//     final integrator) implements it, and
//     <see cref="InMemoryMapCheckDocument"/> is the reference implementation
//     the tests drive. If MapDocument lands with different member names, only
//     the adapter moves — the check, the fixes and the tests do not.
//   * IssueAutoFix (hammer-issue-autofix-actions, worker 27) owns the deep
//     one-click fix mechanics (triangulate an invalid brush into valid convex
//     pieces, undoable commands). MapCheck owns the check loop: the named fix
//     per issue, FixAllOfType, and the go-to operations. Where the deep fix is
//     the right answer and MapCheck cannot do it honestly (a zero-volume or
//     too-few-faces brush cannot be repaired by normalising bounds),
//     MapCheck returns CannotFix and names IssueAutoFix instead of deleting
//     the brush: fixes are explicit and logged, never silent, and never a
//     silent delete.
//
// Fix policy per issue type (same conventions as the sibling descs):
//   InvalidBrush   -> "Repair": normalise inverted bounds (same volume). A
//                     zero-volume / too-few-faces brush cannot be repaired
//                     this way -> CannotFix (triangulation is IssueAutoFix).
//   MissingTarget  -> "CreateStubOrRetarget": retarget to the existing entity
//                     when one matches case-insensitively, else create an
//                     info_target stub flagged mapcheck_stub=1 at the source
//                     entity's origin.
//   MissingMaterial-> "AssignNodraw": tools/toolsnodraw, old material kept
//                     in the result.
//   BadKeyValue    -> "RemoveKey": drop the key, old value kept in the result.
//   Leak           -> no automatic fix: leaks are NOT auto-sealed. Fix
//                     returns CannotFix with the gap location + point file,
//                     so the caller can Go-to the gap and seal it by hand.
//
// Geometry is the kernel's inches and Z-up convention. Deterministic: same
// document -> same issue order (rules run InvalidBrush, MissingTarget,
// MissingMaterial, BadKeyValue, Leak; document order inside a rule), same
// ids (CHK-0001...), same fix results. No timestamps anywhere.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Effigy;

/// <summary>Which kind of problem a <see cref="MapIssue"/> reports. The first
/// five are the types this ticket's fix handlers cover; the last four are
/// reported by the pre-compile gate's IssueEngine adapter
/// (hammer-compile-safety-net-gate) and have no automatic fix — Fix returns
/// CannotFix with guidance, exactly like Leak.</summary>
public enum MapIssueType
{
	InvalidBrush,
	MissingTarget,
	MissingMaterial,
	BadKeyValue,
	Leak,
	/// <summary>An entity with no classname (IssueEngine EntityNoClass).</summary>
	EntityNoClass,
	/// <summary>Two or more entities share one targetname (IssueEngine DuplicateTargetName).</summary>
	DuplicateTargetName,
	/// <summary>A compiler limit is exceeded (IssueEngine LimitExceeded).</summary>
	LimitExceeded,
	/// <summary>A 2D skybox box vastly larger than the map bounds (IssueEngine GiantSkybox).</summary>
	GiantSkybox,
	/// <summary>Two or more brushes or entities share one id in a compile input (gate guard, hammer-compile-safety-net-gate).</summary>
	DuplicateId
}

/// <summary>How bad the issue is. Errors break a compile or the map logic;
/// warnings are suspicious but survivable.</summary>
public enum MapIssueSeverity
{
	Warning,
	Error
}

/// <summary>Outcome of one <see cref="MapCheck.Fix"/> application.</summary>
public enum MapFixStatus
{
	/// <summary>The named fix was applied; the issue clears on re-check.</summary>
	Fixed,
	/// <summary>The fix was attempted but could not be applied (target object
	/// gone, mutation refused). The document is unchanged by the attempt.</summary>
	Failed,
	/// <summary>No automatic fix exists for this issue (a leak: leaks are not
	/// auto-sealed). The result's Detail says what to do instead.</summary>
	CannotFix
}

/// <summary>What a go-to target points at: a brush, an entity, or a bare
/// coordinate (a leak gap, or a compile-log coordinate with no object).</summary>
public enum GoToKind
{
	Brush,
	Entity,
	Coordinate
}

/// <summary>One brush as the check sees it: an id, an axis-aligned bounds box
/// (kernel inches, Z-up), a material name, and a face count. A convex box
/// brush has 6 faces; fewer than 4 faces cannot enclose a volume.</summary>
public sealed class CheckBrush
{
	public string Id = "";
	public Vec3 Min;
	public Vec3 Max;
	public string Material = "";
	public int FaceCount = 6;

	public Vec3 Center => ( Min + Max ) * 0.5f;
	public Vec3 Size => Max - Min;
	public float Volume => Size.x * Size.y * Size.z;

	public bool BoundsInverted => Min.x > Max.x || Min.y > Max.y || Min.z > Max.z;
	public bool IsZeroVolume => Size.x <= 0 || Size.y <= 0 || Size.z <= 0;

	/// <summary>A brush is valid when its bounds are ordered, it has volume,
	/// and it has at least 4 faces. Face-level defects (non-planar, inverted,
	/// duplicate faces) belong to MapVertexEdit's MapVertexValidity, not here.</summary>
	public bool IsValid => !BoundsInverted && !IsZeroVolume && FaceCount >= 4;

	/// <summary>First named validity defect, or "" when valid. Same shape as
	/// MapBrush.Validate()-style reasons.</summary>
	public string ValidityReason
	{
		get
		{
			if ( BoundsInverted ) return "brush bounds are inverted";
			if ( IsZeroVolume ) return "brush has zero volume";
			if ( FaceCount < 4 ) return $"brush has {FaceCount} faces, fewer than 4 cannot enclose a volume";
			return "";
		}
	}

	public bool Contains( Vec3 p ) =>
		p.x >= Min.x && p.x <= Max.x &&
		p.y >= Min.y && p.y <= Max.y &&
		p.z >= Min.z && p.z <= Max.z;

	public CheckBrush Clone() => new CheckBrush
	{
		Id = Id, Min = Min, Max = Max, Material = Material, FaceCount = FaceCount
	};
}

/// <summary>One entity as the check sees it: a classname, an origin, and
/// string key/value properties (case-insensitive keys, VMF convention).
/// The dictionary is copied on Clone, so a check snapshot can never be
/// mutated by a later fix.</summary>
public sealed class CheckEntity
{
	public string Id = "";
	public string ClassName = "";
	public Vec3 Origin;
	public Dictionary<string, string> Properties =
		new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

	/// <summary>The name other entities target, from the "targetname" key,
	/// or "" when the entity is unnamed.</summary>
	public string TargetName =>
		Properties.TryGetValue( "targetname", out var v ) ? v : "";

	public CheckEntity Clone() => new CheckEntity
	{
		Id = Id,
		ClassName = ClassName,
		Origin = Origin,
		Properties = new Dictionary<string, string>( Properties, StringComparer.OrdinalIgnoreCase )
	};
}

/// <summary>Minimal map-document surface MapCheck runs against. The real
/// MapDocument (or a thin adapter) implements it; tests use
/// <see cref="InMemoryMapCheckDocument"/>. Mutation methods return false
/// instead of throwing when the named object is not in the document, so a
/// fix attempt can report Failed instead of crashing the check loop.</summary>
public interface IMapCheckDocument
{
	string Name { get; }
	IReadOnlyList<CheckBrush> Brushes { get; }
	IReadOnlyList<CheckEntity> Entities { get; }
	/// <summary>Materials the map may reference. Empty means "unknown": only
	/// an empty material is flagged, an unlisted one is not judged.</summary>
	IReadOnlyList<string> KnownMaterials { get; }
	/// <summary>Portal graph for the Leak rule, or null when the map has no
	/// BSP graph yet — the Leak rule is then skipped, not faked.</summary>
	LeakFinder.ILeakPortalGraph LeakGraph { get; }
	bool HasPlayerStart { get; }
	Vec3 PlayerStart { get; }

	CheckBrush FindBrush( string brushId );
	CheckEntity FindEntity( string entityId );
	/// <summary>First entity whose targetname matches, case-insensitive
	/// (Source targetnames are), or null. Entities with no targetname never match.</summary>
	CheckEntity FindEntityByTargetName( string targetName );

	bool RemoveBrush( string brushId );
	bool SetBrushMaterial( string brushId, string material );
	/// <summary>Swap any inverted bounds axes. Returns false (and leaves the
	/// brush untouched) when the brush would still be invalid afterwards
	/// (zero volume or fewer than 4 faces).</summary>
	bool NormalizeBrushBounds( string brushId );
	bool RemoveKeyValue( string entityId, string key );
	bool SetKeyValue( string entityId, string key, string value );
	/// <summary>Add an entity; assigns a fresh unique id when Id is empty.
	/// Throws <see cref="ArgumentException"/> on a duplicate id.</summary>
	CheckEntity AddEntity( CheckEntity entity );
	/// <summary>Create a target stub: classname info_target, the given
	/// targetname, mapcheck_stub=1, at the given origin. Returns the new
	/// entity's id.</summary>
	string AddStubEntity( string targetName, Vec3 origin );
}

/// <summary>In-memory reference implementation of
/// <see cref="IMapCheckDocument"/>. What the tests drive, and what
/// <see cref="MapCheckDocumentJson"/> loads fixtures into.</summary>
public sealed class InMemoryMapCheckDocument : IMapCheckDocument
{
	readonly List<CheckBrush> _brushes = new List<CheckBrush>();
	readonly List<CheckEntity> _entities = new List<CheckEntity>();
	readonly List<string> _knownMaterials = new List<string>();

	public string Name { get; set; } = "untitled";
	public IReadOnlyList<CheckBrush> Brushes => _brushes;
	public IReadOnlyList<CheckEntity> Entities => _entities;
	public IReadOnlyList<string> KnownMaterials => _knownMaterials;
	public LeakFinder.ILeakPortalGraph LeakGraph { get; set; }
	public bool HasPlayerStart { get; set; }
	public Vec3 PlayerStart { get; set; }

	public InMemoryMapCheckDocument AddBrush( CheckBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( _brushes.Any( b => b.Id == brush.Id ) )
			throw new ArgumentException( $"Duplicate brush id '{brush.Id}'." );
		_brushes.Add( brush );
		return this;
	}

	public InMemoryMapCheckDocument AddKnownMaterial( string material )
	{
		if ( !string.IsNullOrWhiteSpace( material ) && !_knownMaterials.Contains( material ) )
			_knownMaterials.Add( material );
		return this;
	}

	public CheckBrush FindBrush( string brushId ) =>
		_brushes.FirstOrDefault( b => b.Id == brushId );

	public CheckEntity FindEntity( string entityId ) =>
		_entities.FirstOrDefault( e => e.Id == entityId );

	public CheckEntity FindEntityByTargetName( string targetName )
	{
		if ( string.IsNullOrWhiteSpace( targetName ) ) return null;
		return _entities.FirstOrDefault( e =>
			!string.IsNullOrWhiteSpace( e.TargetName ) &&
			string.Equals( e.TargetName, targetName, StringComparison.OrdinalIgnoreCase ) );
	}

	public bool RemoveBrush( string brushId )
	{
		var brush = FindBrush( brushId );
		if ( brush is null ) return false;
		return _brushes.Remove( brush );
	}

	public bool SetBrushMaterial( string brushId, string material )
	{
		var brush = FindBrush( brushId );
		if ( brush is null ) return false;
		brush.Material = material ?? "";
		return true;
	}

	public bool NormalizeBrushBounds( string brushId )
	{
		var brush = FindBrush( brushId );
		if ( brush is null ) return false;
		var min = brush.Min;
		var max = brush.Max;
		var normMin = new Vec3( MathF.Min( min.x, max.x ), MathF.Min( min.y, max.y ), MathF.Min( min.z, max.z ) );
		var normMax = new Vec3( MathF.Max( min.x, max.x ), MathF.Max( min.y, max.y ), MathF.Max( min.z, max.z ) );
		// Still invalid after normalising (zero volume / too few faces)?
		// Leave the brush untouched: that repair belongs to IssueAutoFix.
		var probe = new CheckBrush { Min = normMin, Max = normMax, FaceCount = brush.FaceCount };
		if ( !probe.IsValid ) return false;
		brush.Min = normMin;
		brush.Max = normMax;
		return true;
	}

	public bool RemoveKeyValue( string entityId, string key )
	{
		var entity = FindEntity( entityId );
		if ( entity is null ) return false;
		return entity.Properties.Remove( key );
	}

	public bool SetKeyValue( string entityId, string key, string value )
	{
		var entity = FindEntity( entityId );
		if ( entity is null || string.IsNullOrWhiteSpace( key ) ) return false;
		entity.Properties[key] = value ?? "";
		return true;
	}

	public CheckEntity AddEntity( CheckEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( string.IsNullOrWhiteSpace( entity.Id ) )
		{
			int n = _entities.Count + 1;
			while ( _entities.Any( e => e.Id == "ent_" + n ) ) n++;
			entity.Id = "ent_" + n;
		}
		else if ( _entities.Any( e => e.Id == entity.Id ) )
			throw new ArgumentException( $"Duplicate entity id '{entity.Id}'." );
		_entities.Add( entity );
		return entity;
	}

	public string AddStubEntity( string targetName, Vec3 origin )
	{
		if ( string.IsNullOrWhiteSpace( targetName ) )
			throw new ArgumentException( "Stub target name is empty." );
		var stub = new CheckEntity
		{
			ClassName = MapCheck.StubClassName,
			Origin = origin
		};
		stub.Properties["targetname"] = targetName;
		stub.Properties["mapcheck_stub"] = "1";
		AddEntity( stub );
		return stub.Id;
	}
}

/// <summary>Where a caller should jump to for an issue: a brush, an entity,
/// or a bare coordinate (a leak gap, or a compile-log coordinate).</summary>
public sealed class GoToTarget
{
	public GoToKind Kind;
	public string BrushId = "";
	public string EntityId = "";
	public Vec3 Position;
	public string Label = "";

	public override string ToString() => Label;
}

/// <summary>One problem found by a full check: its type, severity, message,
/// the named fix that addresses it, and where to go to see it. Location is
/// carried as brush id / entity id / position so a caller can jump to it
/// without re-running the check. <see cref="Detail"/> holds the offending
/// value: the target name (MissingTarget), the material (MissingMaterial),
/// the key's value (BadKeyValue), the validity reason (InvalidBrush), or the
/// gap description (Leak). <see cref="Key"/> holds the referencing key
/// (MissingTarget) or the offending key (BadKeyValue).</summary>
public sealed class MapIssue
{
	public string Id = "";
	public MapIssueType Type;
	public MapIssueSeverity Severity;
	public string Message = "";
	/// <summary>Named fix, e.g. "Repair brush bounds". Shown to the user;
	/// <see cref="MapCheck.Fix"/> applies exactly this fix.</summary>
	public string FixHint = "";
	public string BrushId = "";
	public string EntityId = "";
	public string Key = "";
	public string Detail = "";
	public Vec3 Position;
	/// <summary>Leak only: point-file text for the leak path ("" when sealed).</summary>
	public string GapPointFile = "";
	/// <summary>Leak only: nearest interior leaf to the gap, or -1.</summary>
	public int GapLeafId = -1;
	/// <summary>Leak only: portal crossed into the void (the gap), or -1.</summary>
	public int GapPortalId = -1;

	/// <summary>The go-to target for this issue: the brush (InvalidBrush,
	/// MissingMaterial), the source entity (MissingTarget, BadKeyValue), or
	/// the gap coordinate (Leak).</summary>
	public GoToTarget GoTo()
	{
		if ( !string.IsNullOrEmpty( BrushId ) )
			return new GoToTarget
			{
				Kind = GoToKind.Brush, BrushId = BrushId, Position = Position,
				Label = $"brush {BrushId} at {Position}"
			};
		if ( !string.IsNullOrEmpty( EntityId ) )
			return new GoToTarget
			{
				Kind = GoToKind.Entity, EntityId = EntityId, Position = Position,
				Label = $"entity {EntityId} at {Position}"
			};
		return new GoToTarget
		{
			Kind = GoToKind.Coordinate, Position = Position,
			Label = $"coordinate {Position}"
		};
	}
}

/// <summary>Result of applying one fix: which issue, which named fix, whether
/// it landed, and a before/after summary (or the reason it could not be
/// applied). Every fix produces one of these — fixes are explicit and
/// logged, never silent.</summary>
public sealed class FixResult
{
	public string IssueId = "";
	public MapIssueType Type;
	public string FixName = "";
	public MapFixStatus Status;
	public bool Applied => Status == MapFixStatus.Fixed;
	/// <summary>Before/after summary when fixed ("material 'x' ->
	/// tools/toolsnodraw"), or the reason when not fixed.</summary>
	public string Detail = "";
}

/// <summary>Result of one <see cref="MapCheck.RunFullCheck"/>: the issues in
/// deterministic order, counts by type, and any rules that were skipped
/// (the Leak rule is skipped when the document has no portal graph or no
/// player start — skipped, never faked).</summary>
public sealed class MapCheckReport
{
	public string DocumentName = "";
	public IReadOnlyList<MapIssue> Issues = Array.Empty<MapIssue>();
	public IReadOnlyList<string> SkippedRules = Array.Empty<string>();

	public int IssueCount => Issues.Count;
	public bool HasErrors => Issues.Any( i => i.Severity == MapIssueSeverity.Error );

	public int CountOf( MapIssueType type ) => Issues.Count( i => i.Type == type );

	public IReadOnlyDictionary<MapIssueType, int> CountsByType
	{
		get
		{
			var counts = new Dictionary<MapIssueType, int>();
			foreach ( var issue in Issues )
				counts[issue.Type] = counts.TryGetValue( issue.Type, out var n ) ? n + 1 : 1;
			return counts;
		}
	}

	public IReadOnlyList<MapIssue> IssuesOfType( MapIssueType type ) =>
		Issues.Where( i => i.Type == type ).ToList();
}

/// <summary>Result of resolving a coordinate to the nearest map object:
/// the point, what it resolved to, that object's id, and the distance.
/// Distance is 0 when the point is inside a brush; -1 when the document
/// has no brush or entity at all (Kind is then Coordinate).</summary>
public sealed class CoordinateResolution
{
	public Vec3 Position;
	public GoToKind Kind;
	public string BrushId = "";
	public string EntityId = "";
	public float Distance;
	public string Label = "";
}

/// <summary>An issue source: Run(document) -> issue list, exactly the shape
/// the IssueEngine desc specifies (rule registry, Run(document), severity +
/// location + fix hint per issue). The final integrator adapts the real
/// IssueEngine to this interface when it lands; MapCheck never calls the
/// engine's concrete types directly.</summary>
public interface IIssueSource
{
	IReadOnlyList<MapIssue> Run( IMapCheckDocument document );
	/// <summary>Rules skipped by the last Run (e.g. "Leak" when the document
	/// has no portal graph). Empty when nothing was skipped.</summary>
	IReadOnlyList<string> SkippedRules { get; }
}

/// <summary>Built-in issue source: the five rules from the IssueEngine spec,
/// as a named rule registry. Rules run in registry order (InvalidBrush,
/// MissingTarget, MissingMaterial, BadKeyValue, Leak); document order inside
/// a rule. Each rule is individually invokable via
/// <see cref="RunRule"/> for incremental re-checks of changed content.</summary>
public sealed class DefaultIssueSource : IIssueSource
{
	public static readonly IReadOnlyList<string> RuleNames = new[]
	{
		"InvalidBrush", "MissingTarget", "MissingMaterial", "BadKeyValue", "Leak"
	};

	/// <summary>Keys whose values name another entity (a missing value is a
	/// MissingTarget issue). "target" is the common one; "killtarget" and
	/// "master" reference entities too; "target1".."target4" on logic
	/// entities match the "target" prefix rule.</summary>
	public static readonly IReadOnlyList<string> TargetReferenceKeys = new[]
	{
		"target", "killtarget", "master"
	};

	/// <summary>Keys whose values must be three floats ("origin", "angles").</summary>
	public static readonly IReadOnlyList<string> VectorKeys = new[] { "origin", "angles" };

	/// <summary>Keys whose values must be a single float.</summary>
	public static readonly IReadOnlyList<string> NumericKeys = new[]
	{
		"speed", "health", "wait", "delay", "dmg", "scale", "distance", "radius"
	};

	readonly List<string> _skippedRules = new List<string>();
	public IReadOnlyList<string> SkippedRules => _skippedRules;

	public IReadOnlyList<MapIssue> Run( IMapCheckDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		_skippedRules.Clear();
		var issues = new List<MapIssue>();
		foreach ( var rule in RuleNames )
			issues.AddRange( RunRule( document, rule ) );
		// Stable ids in detection order: CHK-0001, CHK-0002, ...
		for ( int i = 0; i < issues.Count; i++ )
			issues[i].Id = "CHK-" + ( i + 1 ).ToString( "D4", CultureInfo.InvariantCulture );
		return issues;
	}

	/// <summary>Run one named rule (<see cref="RuleNames"/>). Throws
	/// <see cref="ArgumentException"/> for an unknown rule name. Issue ids
	/// are NOT assigned here — <see cref="Run"/> assigns them after all
	/// rules run, so a single-rule re-check has unnumbered issues.</summary>
	public IReadOnlyList<MapIssue> RunRule( IMapCheckDocument document, string ruleName )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		switch ( ruleName )
		{
			case "InvalidBrush": return InvalidBrushRule( document );
			case "MissingTarget": return MissingTargetRule( document );
			case "MissingMaterial": return MissingMaterialRule( document );
			case "BadKeyValue": return BadKeyValueRule( document );
			case "Leak": return LeakRule( document );
			default: throw new ArgumentException( $"Unknown check rule '{ruleName}'." );
		}
	}

	static List<MapIssue> InvalidBrushRule( IMapCheckDocument document )
	{
		var issues = new List<MapIssue>();
		foreach ( var brush in document.Brushes )
		{
			if ( brush is null || brush.IsValid ) continue;
			issues.Add( new MapIssue
			{
				Type = MapIssueType.InvalidBrush,
				Severity = MapIssueSeverity.Error,
				Message = $"Brush {brush.Id} is invalid: {brush.ValidityReason}.",
				FixHint = "Repair brush bounds",
				BrushId = brush.Id,
				Detail = brush.ValidityReason,
				Position = brush.Center
			} );
		}
		return issues;
	}

	static List<MapIssue> MissingTargetRule( IMapCheckDocument document )
	{
		var issues = new List<MapIssue>();
		foreach ( var entity in document.Entities )
		{
			if ( entity is null ) continue;
			foreach ( var pair in entity.Properties )
			{
				if ( !IsTargetReferenceKey( pair.Key ) ) continue;
				var target = pair.Value ?? "";
				if ( string.IsNullOrWhiteSpace( target ) ) continue; // unset, not missing
				if ( target.StartsWith( "!", StringComparison.Ordinal ) ) continue; // engine special target: always resolved
				if ( document.FindEntityByTargetName( target ) is not null ) continue;
				issues.Add( new MapIssue
				{
					Type = MapIssueType.MissingTarget,
					Severity = MapIssueSeverity.Error,
					Message = $"Entity {entity.Id} ({entity.ClassName}) references missing target '{target}' via '{pair.Key}'.",
					FixHint = "Create target stub (or retarget)",
					EntityId = entity.Id,
					Key = pair.Key,
					Detail = target,
					Position = entity.Origin
				} );
			}
		}
		return issues;
	}

	static bool IsTargetReferenceKey( string key )
	{
		// "targetname" DEFINES a name; it never references one.
		if ( string.Equals( key, "targetname", StringComparison.OrdinalIgnoreCase ) ) return false;
		if ( TargetReferenceKeys.Any( k => string.Equals( k, key, StringComparison.OrdinalIgnoreCase ) ) )
			return true;
		return key.StartsWith( "target", StringComparison.OrdinalIgnoreCase );
	}

	static List<MapIssue> MissingMaterialRule( IMapCheckDocument document )
	{
		var issues = new List<MapIssue>();
		bool judgeUnknown = document.KnownMaterials.Count > 0;
		foreach ( var brush in document.Brushes )
		{
			if ( brush is null ) continue;
			if ( string.IsNullOrWhiteSpace( brush.Material ) )
			{
				issues.Add( new MapIssue
				{
					Type = MapIssueType.MissingMaterial,
					Severity = MapIssueSeverity.Error,
					Message = $"Brush {brush.Id} has no material.",
					FixHint = "Assign tools/toolsnodraw",
					BrushId = brush.Id,
					Detail = "",
					Position = brush.Center
				} );
			}
			else if ( judgeUnknown && !document.KnownMaterials.Contains( brush.Material ) )
			{
				issues.Add( new MapIssue
				{
					Type = MapIssueType.MissingMaterial,
					Severity = MapIssueSeverity.Error,
					Message = $"Brush {brush.Id} uses unknown material '{brush.Material}'.",
					FixHint = "Assign tools/toolsnodraw",
					BrushId = brush.Id,
					Detail = brush.Material,
					Position = brush.Center
				} );
			}
		}
		return issues;
	}

	static List<MapIssue> BadKeyValueRule( IMapCheckDocument document )
	{
		var issues = new List<MapIssue>();
		foreach ( var entity in document.Entities )
		{
			if ( entity is null ) continue;
			foreach ( var pair in entity.Properties )
			{
				string reason = BadKeyValueReason( pair.Key, pair.Value );
				if ( reason is null ) continue;
				issues.Add( new MapIssue
				{
					Type = MapIssueType.BadKeyValue,
					Severity = MapIssueSeverity.Warning,
					Message = $"Entity {entity.Id} ({entity.ClassName}) has a bad keyvalue: {reason}.",
					FixHint = "Remove bad key",
					EntityId = entity.Id,
					Key = pair.Key,
					Detail = pair.Value ?? "",
					Position = entity.Origin
				} );
			}
		}
		return issues;
	}

	/// <summary>Named reason the key/value is bad, or null when it is fine.
	/// Rules: empty key; key with whitespace or a quote (breaks VMF); empty
	/// value; a vector key (origin/angles) that is not three floats; a
	/// numeric key that is not one float.</summary>
	static string BadKeyValueReason( string key, string value )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) return "key is empty";
		if ( key.IndexOfAny( new[] { ' ', '\t', '"' } ) >= 0 )
			return $"key '{key}' contains whitespace or a quote";
		if ( string.IsNullOrWhiteSpace( value ) ) return $"key '{key}' has an empty value";
		if ( VectorKeys.Any( k => string.Equals( k, key, StringComparison.OrdinalIgnoreCase ) ) )
		{
			var parts = value.Split( new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length != 3
				|| !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _ )
				|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _ )
				|| !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _ ) )
				return $"key '{key}' must be three numbers, got '{value}'";
		}
		else if ( NumericKeys.Any( k => string.Equals( k, key, StringComparison.OrdinalIgnoreCase ) ) )
		{
			if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out _ ) )
				return $"key '{key}' must be a number, got '{value}'";
		}
		return null;
	}

	List<MapIssue> LeakRule( IMapCheckDocument document )
	{
		var issues = new List<MapIssue>();
		if ( document.LeakGraph is null )
		{
			_skippedRules.Add( "Leak" );
			return issues;
		}
		if ( !document.HasPlayerStart )
		{
			_skippedRules.Add( "Leak" );
			return issues;
		}
		var leak = LeakFinder.FindLeak( document.LeakGraph, document.PlayerStart );
		if ( !leak.IsLeaked ) return issues;
		issues.Add( new MapIssue
		{
			Type = MapIssueType.Leak,
			Severity = MapIssueSeverity.Error,
			Message = $"Map is leaked: gap at {leak.GapLocation} (leaf {leak.GapLeafId}, portal {leak.GapPortalId}).",
			FixHint = "Go to gap — leaks are not auto-sealed",
			Detail = $"gap at {leak.GapLocation} (leaf {leak.GapLeafId}, portal {leak.GapPortalId})",
			Position = leak.GapLocation,
			GapPointFile = leak.ToPointFileText(),
			GapLeafId = leak.GapLeafId,
			GapPortalId = leak.GapPortalId
		} );
		return issues;
	}
}

/// <summary>Check-for-Problems: the on-demand full-map check plus the fix
/// loop. Static API; every method is deterministic and takes the document
/// explicitly — there is no global document and no hidden state. Fixes are
/// explicit and logged: pass a log list (or null) and every fix appends one
/// line per issue, and every fix returns a <see cref="FixResult"/> with a
/// before/after summary or the reason it could not be applied.</summary>
public static class MapCheck
{
	/// <summary>Material the MissingMaterial fix assigns (sibling
	/// IssueAutoFix convention: nodraw + flag; the flag here is the log
	/// entry and the FixResult detail).</summary>
	public const string NoDrawMaterial = "tools/toolsnodraw";

	/// <summary>Classname of the stub entity the MissingTarget fix creates.</summary>
	public const string StubClassName = "info_target";

	/// <summary>Run the full check with the built-in rules.</summary>
	public static MapCheckReport RunFullCheck( IMapCheckDocument document ) =>
		RunFullCheck( document, new DefaultIssueSource() );

	/// <summary>Run the full check with the given issue source (the built-in
	/// rules, or the IssueEngine adapter when it lands). Issues are numbered
	/// CHK-0001... in detection order.</summary>
	public static MapCheckReport RunFullCheck( IMapCheckDocument document, IIssueSource source )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		var issues = source.Run( document );
		return new MapCheckReport
		{
			DocumentName = document.Name,
			Issues = issues,
			SkippedRules = source.SkippedRules.ToList()
		};
	}

	/// <summary>Go-to target for an issue: the brush, the source entity, or
	/// the gap coordinate. This is what the (deferred) UI jumps to.</summary>
	public static GoToTarget GoTo( MapIssue issue )
	{
		if ( issue is null ) throw new ArgumentNullException( nameof( issue ) );
		return issue.GoTo();
	}

	/// <summary>Apply the single named fix for one issue and log it. The
	/// issue's FixHint names the fix; this applies exactly that fix.
	/// A leak returns CannotFix (leaks are not auto-sealed) with the gap
	/// location + point file in the Detail. An issue whose target object is
	/// no longer in the document returns Failed. The document is unchanged
	/// on Failed/CannotFix.</summary>
	public static FixResult Fix( IMapCheckDocument document, MapIssue issue, IList<string> log = null )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( issue is null ) throw new ArgumentNullException( nameof( issue ) );
		FixResult result;
		switch ( issue.Type )
		{
			case MapIssueType.InvalidBrush: result = FixInvalidBrush( document, issue ); break;
			case MapIssueType.MissingTarget: result = FixMissingTarget( document, issue ); break;
			case MapIssueType.MissingMaterial: result = FixMissingMaterial( document, issue ); break;
			case MapIssueType.BadKeyValue: result = FixBadKeyValue( document, issue ); break;
			case MapIssueType.Leak: result = FixLeak( issue ); break;
			// Gate-reported types (hammer-compile-safety-net-gate): no
			// automatic fix exists, exactly like a leak — the fix reports
			// what to do by hand instead of silently changing the map.
			case MapIssueType.EntityNoClass: result = CannotFix( "Set a classname by hand",
				$"entity {issue.EntityId} has no classname; set a classname or delete the entity by hand" ); break;
			case MapIssueType.DuplicateTargetName: result = CannotFix( "Rename a duplicate by hand",
				$"{issue.Message}; rename all but one of the holders by hand so each targetname is unique" ); break;
			case MapIssueType.LimitExceeded: result = CannotFix( "Remove or merge content by hand",
				$"{issue.Message}; remove or merge brushes/entities to get under the limit" ); break;
			case MapIssueType.GiantSkybox: result = CannotFix( "Shrink the skybox box by hand",
				$"{issue.Message}; shrink the skybox box to just enclose the playable map, or use a 3D skybox section (sky_camera)" ); break;
			case MapIssueType.DuplicateId: result = CannotFix( "Give each brush and entity a unique id by hand",
				$"{issue.Message}; renumber the duplicates so every brush and entity id is unique" ); break;
			default: throw new ArgumentException( $"Unknown issue type {issue.Type}." );
		}
		result.IssueId = issue.Id;
		result.Type = issue.Type;
		if ( log is not null ) log.Add( FormatLogLine( result ) );
		return result;
	}

	/// <summary>Apply the named fix to every issue of one type in a fresh
	/// full check, in issue order, and return one result per issue — the
	/// per-issue result list. A fix that fails does not stop the rest.
	/// Returns an empty list when the check finds no issue of that type.
	/// Re-run <see cref="RunFullCheck"/> afterwards to confirm the issues
	/// cleared.</summary>
	public static IReadOnlyList<FixResult> FixAllOfType(
		IMapCheckDocument document, MapIssueType type, IList<string> log = null )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var report = RunFullCheck( document );
		var results = new List<FixResult>();
		foreach ( var issue in report.IssuesOfType( type ) )
			results.Add( Fix( document, issue, log ) );
		return results;
	}

	static FixResult FixInvalidBrush( IMapCheckDocument document, MapIssue issue )
	{
		var brush = document.FindBrush( issue.BrushId );
		if ( brush is null )
			return Failed( "Repair brush bounds",
				$"brush {issue.BrushId} is no longer in the document" );
		var before = $"bounds ({brush.Min})-({brush.Max}), {brush.FaceCount} faces";
		if ( !brush.IsValid && brush.BoundsInverted && document.NormalizeBrushBounds( brush.Id ) )
		{
			var after = document.FindBrush( brush.Id );
			return Fixed( "Repair brush bounds",
				$"brush {brush.Id}: {before} -> bounds ({after.Min})-({after.Max}), volume {after.Volume:0.####}" );
		}
		// Zero volume or too few faces: normalising cannot repair it, and
		// deleting it would be a silent loss. Triangulation into valid
		// convex pieces is IssueAutoFix's fix (hammer-issue-autofix-actions).
		return CannotFix( "Repair brush bounds",
			$"brush {brush.Id} cannot be repaired by normalising bounds ({brush.ValidityReason}); " +
			"triangulate into valid convex pieces via IssueAutoFix, or delete it by hand" );
	}

	static FixResult FixMissingTarget( IMapCheckDocument document, MapIssue issue )
	{
		var source = document.FindEntity( issue.EntityId );
		if ( source is null )
			return Failed( "Create target stub (or retarget)",
				$"entity {issue.EntityId} is no longer in the document" );
		var target = issue.Detail;
		var existing = document.FindEntityByTargetName( target );
		if ( existing is not null )
		{
			// Already there: either this issue is stale (an earlier fix in a
			// FixAllOfType run created the stub), or it only differs by case.
			if ( string.Equals( existing.TargetName, target, StringComparison.Ordinal ) )
				return Fixed( "Create target stub (or retarget)",
					$"target '{target}' already exists (entity {existing.Id}); nothing to create" );
			document.SetKeyValue( source.Id, issue.Key, existing.TargetName );
			return Fixed( "Create target stub (or retarget)",
				$"entity {source.Id}: '{issue.Key}' '{target}' -> '{existing.TargetName}' (entity {existing.Id})" );
		}
		var stubId = document.AddStubEntity( target, source.Origin );
		return Fixed( "Create target stub (or retarget)",
			$"created stub {StubClassName} {stubId} targetname '{target}' at {source.Origin} (flagged mapcheck_stub=1)" );
	}

	static FixResult FixMissingMaterial( IMapCheckDocument document, MapIssue issue )
	{
		var brush = document.FindBrush( issue.BrushId );
		if ( brush is null )
			return Failed( "Assign tools/toolsnodraw",
				$"brush {issue.BrushId} is no longer in the document" );
		var before = string.IsNullOrWhiteSpace( brush.Material ) ? "(none)" : "'" + brush.Material + "'";
		document.SetBrushMaterial( brush.Id, NoDrawMaterial );
		return Fixed( "Assign tools/toolsnodraw",
			$"brush {brush.Id}: material {before} -> {NoDrawMaterial} (flagged for review)" );
	}

	static FixResult FixBadKeyValue( IMapCheckDocument document, MapIssue issue )
	{
		var entity = document.FindEntity( issue.EntityId );
		if ( entity is null )
			return Failed( "Remove bad key",
				$"entity {issue.EntityId} is no longer in the document" );
		if ( !entity.Properties.TryGetValue( issue.Key, out var oldValue ) )
			return Fixed( "Remove bad key",
				$"key '{issue.Key}' already gone from entity {entity.Id}; nothing to remove" );
		document.RemoveKeyValue( entity.Id, issue.Key );
		return Fixed( "Remove bad key",
			$"entity {entity.Id}: removed key '{issue.Key}' (was '{oldValue}')" );
	}

	static FixResult FixLeak( MapIssue issue )
	{
		// Leaks are NOT auto-sealed (same policy as IssueAutoFix): the fix
		// reports the gap and hands the caller the point file to go to it.
		return CannotFix( "Go to gap — leaks are not auto-sealed",
			$"{issue.Detail}; point file:\n{issue.GapPointFile}" );
	}

	static FixResult Fixed( string fixName, string detail ) => new FixResult
	{
		FixName = fixName, Status = MapFixStatus.Fixed, Detail = detail
	};

	static FixResult Failed( string fixName, string detail ) => new FixResult
	{
		FixName = fixName, Status = MapFixStatus.Failed, Detail = detail
	};

	static FixResult CannotFix( string fixName, string detail ) => new FixResult
	{
		FixName = fixName, Status = MapFixStatus.CannotFix, Detail = detail
	};

	/// <summary>One log line per fix: "[mapcheck] CHK-0001 MissingTarget
	/// Create target stub (or retarget): Fixed — ...". Newlines in the
	/// detail are folded so one fix is one log line.</summary>
	public static string FormatLogLine( FixResult result )
	{
		if ( result is null ) throw new ArgumentNullException( nameof( result ) );
		var detail = ( result.Detail ?? "" ).Replace( "\r", "" ).Replace( "\n", "; " );
		return $"[mapcheck] {result.IssueId} {result.Type} {result.FixName}: {result.Status} — {detail}";
	}

	// -- Go-to-coordinates ----------------------------------------------------
	// Compile logs name coordinates the way Valve tools do: "(x y z)" inside
	// a line, e.g. "Entity info_player_start (0 0 32) leaked!". The resolver
	// takes the point and finds the nearest brush/entity to jump to.

	/// <summary>Parse the first "(x y z)" triple out of a log line. Numbers
	/// are invariant-culture floats, separated by spaces, tabs or commas.
	/// False when the line holds no parenthesised triple.</summary>
	public static bool TryParseCoordinates( string logEntry, out Vec3 point )
	{
		point = Vec3.Zero;
		if ( string.IsNullOrEmpty( logEntry ) ) return false;
		int searchFrom = 0;
		while ( true )
		{
			int open = logEntry.IndexOf( '(', searchFrom );
			if ( open < 0 ) return false;
			int close = logEntry.IndexOf( ')', open + 1 );
			if ( close < 0 ) return false;
			var inner = logEntry.Substring( open + 1, close - open - 1 );
			var parts = inner.Split( new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length == 3
				&& float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
				&& float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
				&& float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
			{
				point = new Vec3( x, y, z );
				return true;
			}
			searchFrom = close + 1;
		}
	}

	/// <summary>Resolve a compile-log entry to the nearest brush/entity:
	/// parse its "(x y z)" coordinate, then resolve as
	/// <see cref="GoToCoordinates(IMapCheckDocument, Vec3)"/>. Null when the
	/// entry holds no coordinate.</summary>
	public static CoordinateResolution GoToCoordinates( IMapCheckDocument document, string logEntry )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( !TryParseCoordinates( logEntry, out var point ) ) return null;
		return GoToCoordinates( document, point );
	}

	/// <summary>Resolve a coordinate to the nearest brush/entity. A point
	/// inside a brush resolves to that brush (distance 0, first in document
	/// order). Otherwise the nearest brush centre or entity origin wins;
	/// ties go to the brush. An empty document resolves to Kind Coordinate
	/// with Distance -1.</summary>
	public static CoordinateResolution GoToCoordinates( IMapCheckDocument document, Vec3 point )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		foreach ( var brush in document.Brushes )
		{
			if ( brush is not null && brush.Contains( point ) )
				return new CoordinateResolution
				{
					Position = point, Kind = GoToKind.Brush, BrushId = brush.Id,
					Distance = 0,
					Label = $"brush {brush.Id} at {point} (point is inside the brush)"
				};
		}
		float best = float.MaxValue;
		GoToKind bestKind = GoToKind.Coordinate;
		string bestBrush = "", bestEntity = "";
		foreach ( var brush in document.Brushes )
		{
			if ( brush is null ) continue;
			float d = ( brush.Center - point ).Length;
			if ( d < best - 1e-6f || ( MathF.Abs( d - best ) <= 1e-6f && bestKind == GoToKind.Entity ) )
			{
				best = d; bestKind = GoToKind.Brush; bestBrush = brush.Id; bestEntity = "";
			}
		}
		foreach ( var entity in document.Entities )
		{
			if ( entity is null ) continue;
			float d = ( entity.Origin - point ).Length;
			if ( d < best - 1e-6f )
			{
				best = d; bestKind = GoToKind.Entity; bestEntity = entity.Id; bestBrush = "";
			}
		}
		if ( bestKind == GoToKind.Coordinate )
			return new CoordinateResolution
			{
				Position = point, Kind = GoToKind.Coordinate, Distance = -1,
				Label = $"coordinate {point} (no brush or entity in the document)"
			};
		if ( bestKind == GoToKind.Brush )
			return new CoordinateResolution
			{
				Position = point, Kind = GoToKind.Brush, BrushId = bestBrush,
				Distance = best,
				Label = $"brush {bestBrush} at {point} (distance {best:0.####})"
			};
		return new CoordinateResolution
		{
			Position = point, Kind = GoToKind.Entity, EntityId = bestEntity,
			Distance = best,
			Label = $"entity {bestEntity} at {point} (distance {best:0.####})"
		};
	}
}

/// <summary>Fixture format for MapCheck documents (JSON). The tests load
/// tests/fixtures/hammer/hammer-check-problems-fix-actions/*.json through
/// this; the final integrator can reuse it for real MapDocument fixtures.
/// Schema (all coordinates inches, Z-up; [x, y, z] arrays):
///   { "name": "...", "knownMaterials": ["..."],
///     "brushes": [{ "id", "min": [...], "max": [...], "material", "faces" }],
///     "entities": [{ "id", "classname", "origin": [...], "properties": {...} }],
///     "playerStart": [...] | null,
///     "leakGraph": { "leaves": [{ "id", "center", "min", "max", "void", "solid" }],
///                    "portals": [{ "id", "a", "b", "center", "open" }] } | null }
/// Missing sections default to empty; "faces" defaults to 6; "open"
/// defaults to true; "void"/"solid" default to false.</summary>
public static class MapCheckDocumentJson
{
	public static InMemoryMapCheckDocument Load( string json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var doc = new InMemoryMapCheckDocument();
		using var parsed = JsonDocument.Parse( json );
		var root = parsed.RootElement;
		if ( root.ValueKind != JsonValueKind.Object )
			throw new JsonException( "MapCheck fixture root must be a JSON object." );
		if ( root.TryGetProperty( "name", out var name ) && name.ValueKind == JsonValueKind.String )
			doc.Name = name.GetString();
		if ( root.TryGetProperty( "knownMaterials", out var mats ) && mats.ValueKind == JsonValueKind.Array )
			foreach ( var m in mats.EnumerateArray() )
				if ( m.ValueKind == JsonValueKind.String ) doc.AddKnownMaterial( m.GetString() );
		if ( root.TryGetProperty( "brushes", out var brushes ) && brushes.ValueKind == JsonValueKind.Array )
			foreach ( var b in brushes.EnumerateArray() )
				doc.AddBrush( new CheckBrush
				{
					Id = RequiredString( b, "id" ),
					Min = RequiredVec3( b, "min" ),
					Max = RequiredVec3( b, "max" ),
					Material = OptionalString( b, "material", "" ),
					FaceCount = OptionalInt( b, "faces", 6 )
				} );
		if ( root.TryGetProperty( "entities", out var entities ) && entities.ValueKind == JsonValueKind.Array )
			foreach ( var e in entities.EnumerateArray() )
			{
				var entity = new CheckEntity
				{
					Id = RequiredString( e, "id" ),
					ClassName = OptionalString( e, "classname", "" ),
					Origin = OptionalVec3( e, "origin", Vec3.Zero )
				};
				if ( e.TryGetProperty( "properties", out var props ) && props.ValueKind == JsonValueKind.Object )
					foreach ( var p in props.EnumerateObject() )
						entity.Properties[p.Name] = p.Value.ValueKind == JsonValueKind.String
							? p.Value.GetString()
							: p.Value.ToString();
				doc.AddEntity( entity );
			}
		if ( root.TryGetProperty( "playerStart", out var start ) && start.ValueKind == JsonValueKind.Array )
		{
			doc.PlayerStart = ReadVec3( start );
			doc.HasPlayerStart = true;
		}
		if ( root.TryGetProperty( "leakGraph", out var graph ) && graph.ValueKind == JsonValueKind.Object )
		{
			var leakGraph = new LeakFinder.LeakGraph();
			if ( graph.TryGetProperty( "leaves", out var leaves ) && leaves.ValueKind == JsonValueKind.Array )
				foreach ( var l in leaves.EnumerateArray() )
					leakGraph.AddLeaf( new LeakFinder.LeakLeaf(
						RequiredInt( l, "id" ),
						RequiredVec3( l, "center" ),
						RequiredVec3( l, "min" ),
						RequiredVec3( l, "max" ),
						OptionalBool( l, "void", false ),
						OptionalBool( l, "solid", false ) ) );
			if ( graph.TryGetProperty( "portals", out var portals ) && portals.ValueKind == JsonValueKind.Array )
				foreach ( var p in portals.EnumerateArray() )
					leakGraph.AddPortal( new LeakFinder.LeakPortal(
						RequiredInt( p, "id" ),
						RequiredInt( p, "a" ),
						RequiredInt( p, "b" ),
						RequiredVec3( p, "center" ),
						OptionalBool( p, "open", true ) ) );
			doc.LeakGraph = leakGraph;
		}
		return doc;
	}

	public static InMemoryMapCheckDocument LoadFromFile( string path ) =>
		Load( System.IO.File.ReadAllText( path ) );

	static string RequiredString( JsonElement obj, string property )
	{
		if ( !obj.TryGetProperty( property, out var v ) || v.ValueKind != JsonValueKind.String )
			throw new JsonException( $"Fixture element is missing string '{property}'." );
		return v.GetString();
	}

	static int RequiredInt( JsonElement obj, string property )
	{
		if ( !obj.TryGetProperty( property, out var v ) || !v.TryGetInt32( out var n ) )
			throw new JsonException( $"Fixture element is missing integer '{property}'." );
		return n;
	}

	static string OptionalString( JsonElement obj, string property, string fallback ) =>
		obj.TryGetProperty( property, out var v ) && v.ValueKind == JsonValueKind.String
			? v.GetString() : fallback;

	static int OptionalInt( JsonElement obj, string property, int fallback ) =>
		obj.TryGetProperty( property, out var v ) && v.TryGetInt32( out var n ) ? n : fallback;

	static bool OptionalBool( JsonElement obj, string property, bool fallback ) =>
		obj.TryGetProperty( property, out var v ) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
			? v.GetBoolean() : fallback;

	static Vec3 RequiredVec3( JsonElement obj, string property )
	{
		if ( !obj.TryGetProperty( property, out var v ) )
			throw new JsonException( $"Fixture element is missing vector '{property}'." );
		return ReadVec3( v );
	}

	static Vec3 OptionalVec3( JsonElement obj, string property, Vec3 fallback ) =>
		obj.TryGetProperty( property, out var v ) ? ReadVec3( v ) : fallback;

	static Vec3 ReadVec3( JsonElement value )
	{
		if ( value.ValueKind != JsonValueKind.Array )
			throw new JsonException( "Vector must be a [x, y, z] array." );
		var nums = new List<float>();
		foreach ( var n in value.EnumerateArray() )
		{
			if ( !n.TryGetSingle( out var f ) )
				throw new JsonException( "Vector element is not a number." );
			nums.Add( f );
		}
		if ( nums.Count != 3 )
			throw new JsonException( $"Vector must have 3 elements, got {nums.Count}." );
		return new Vec3( nums[0], nums[1], nums[2] );
	}
}
