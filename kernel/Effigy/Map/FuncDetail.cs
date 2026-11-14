// FuncDetail — func_detail optimisation (hammer-optimisation-func-detail).
//
// Hammer Desktop P2: func_detail = "visual only" — rendered, solid, but IGNORED
// by visibility computation: it does not cut visleaves. The single most important
// optimisation action, and a native Geppetto concept (a flag + BSP exclusion),
// not a Hammer feature to shell out to. Never for walls/floor/ceiling that seal
// the map — only world brushes seal.
//
// WHAT THIS FILE IS. The flag + workflow API for func_detail, in namespace
// Effigy.Map, built on the real APIs of the two DONE dependencies:
//   * MapEntities (hammer-entity-place-edit-validate): a func_detail brush entity
//     is a brush entity whose class is func_detail / func_detail_blocker /
//     func_detail_illusionary. MapEntities itself is READ-ONLY for this ticket, so
//     this file does not add the flag to MapEntity; it READS the flag back from
//     placed entities (FuncDetail.FromEntities) and stores the working flag in a
//     FuncDetailSet keyed by brush reference (the one-tap workflow: mark brushes
//     func_detail, build, validate).
//   * BspBuilder (hammer-bsp-core-tree-portals-leaves): BspBrush.IsDetail already
//     excludes a brush from leaf-cutting (it never splits space and never counts
//     as solid for the tree). This file is the adapter the BspBuilder.cs header
//     asks the integrator for — FuncDetailSet.ToBspBrushes maps each MapBrush
//     face (Normal, Distance) to a BspPlane and passes IsDetail for Detail /
//     Illusionary, false for Blocker / structural — plus the Build convenience
//     that runs BspBuilder over the converted brushes.
//
// WHY A SIDECAR SET, NOT A REGISTRY CLASS. The shipped core registry
// (kernel/Effigy/Map/registry/core-source1.json) has no func_detail class, so
// MapEntities.PlaceBrushEntity("func_detail", ...) today throws
// UnknownEntityClass — the flag cannot live on an entity until a registry JSON
// adds the classes. The set is the workflow store until then; FromEntities
// already understands the classes, so the day a registry gains them the
// entity path works with no code change here (a test proves it with a
// LoadFromJson registry that defines the three classes).
//
// SEMANTICS (the table; the one-tap flag is Detail):
//   Detail      — solid, rendered, collides; IGNORED by visibility (never cuts
//                 leaves). Stairs, trim, pillars, decoration.
//   Blocker     — solid, rendered, collides; STILL cuts leaves (a vis blocker).
//                 The escape hatch when a detail-marked brush must still
//                 partition space. A blocker is structural for visibility, so it
//                 is never a sealing misuse.
//   Illusionary — rendered ONLY: non-solid, no collision, never cuts leaves.
//                 Visual-only geometry (a fake wall front, a hologram).
// A brush with no flag is structural: solid, rendered, collides, cuts leaves,
// and is the only kind that can seal the map.
//
// VALIDATION. Validate() is the "sealing brush must not be func_detail" rule,
// as a leave-one-out check against the real BSP: a flagged Detail/Illusionary
// brush is sealing when the map is sealed with it treated as structural and
// unsealed without it. That is a Warning (SealingBrushFuncDetail), never
// silent, and the flagged brush still builds — the map just reports unsealed
// (BspTree.IsSealed false) because the BSP excluded it. Validate() also
// reports an all-detail map (NoStructuralBrushes, Error — BspBuilder would
// refuse to build it) and invalid brushes (InvalidBrush, Error, with the
// MapBrush defect codes). Validate() never invents a fix and never modifies
// the brushes or the set.
//
// INTEGRATION SEAM (for the final integrator; this file changes no shared file):
//   * compile path: build the BSP from FuncDetailSet.ToBspBrushes(brushes)
//     instead of from raw structural brushes;
//   * document/entity path: FuncDetail.FromEntities(entities) to recover the
//     flags from func_detail-classed entities once the registry defines them;
//   * validation path: FuncDetailSet.Validate(brushes) alongside
//     MapEntities.Validate().
// No UI, no Hammer app, no shell-out: backend / data-model only.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Map;

/// <summary>What kind of func_detail a brush is. See the file header for the table.</summary>
public enum FuncDetailKind
{
	/// <summary>Solid, rendered, collides; ignored by visibility (never cuts leaves).</summary>
	Detail,

	/// <summary>Solid, rendered, collides; still cuts leaves (a vis blocker).</summary>
	Blocker,

	/// <summary>Rendered only: non-solid, no collision, never cuts leaves.</summary>
	Illusionary,
}

/// <summary>Severity of one <see cref="FuncDetailIssue"/> from <see cref="FuncDetailSet.Validate"/>.</summary>
public enum FuncDetailIssueSeverity
{
	Error,
	Warning,
}

/// <summary>
/// One validation finding from <see cref="FuncDetailSet.Validate"/>.
/// <see cref="BrushIndex"/> is the index of the brush in the validated list, or -1
/// when the finding is about the whole list (e.g. no structural brushes at all).
/// </summary>
public sealed class FuncDetailIssue
{
	/// <summary>A flagged Detail/Illusionary brush is sealing: the map is sealed with
	/// it treated as structural and unsealed without it. Warning.</summary>
	public const string CodeSealingBrushFuncDetail = "SealingBrushFuncDetail";

	/// <summary>Every brush is flagged Detail/Illusionary (or there are no brushes):
	/// nothing is left to cut leaves or seal the map. Error.</summary>
	public const string CodeNoStructuralBrushes = "NoStructuralBrushes";

	/// <summary>A brush is invalid (its MapBrush defects are named in the message).
	/// Error; the brush is reported, never silently dropped or fixed.</summary>
	public const string CodeInvalidBrush = "InvalidBrush";

	public FuncDetailIssueSeverity Severity;
	public string Code = "";
	public string Message = "";
	public int BrushIndex = -1;

	public FuncDetailIssue() { }
	public FuncDetailIssue( FuncDetailIssueSeverity severity, string code, string message, int brushIndex = -1 )
	{
		Severity = severity; Code = code; Message = message; BrushIndex = brushIndex;
	}

	public override string ToString() => BrushIndex >= 0
		? $"[{Severity} {Code}] {Message} (brush #{BrushIndex})"
		: $"[{Severity} {Code}] {Message}";
}

/// <summary>
/// The func_detail flag table + entity-class recognition. Stateless; the per-map
/// working flag lives in <see cref="FuncDetailSet"/>.
/// </summary>
public static class FuncDetail
{
	/// <summary>Brush-entity class for plain func_detail (solid, ignored by visibility).</summary>
	public const string ClassNameDetail = "func_detail";

	/// <summary>Brush-entity class for a func_detail that still cuts leaves.</summary>
	public const string ClassNameBlocker = "func_detail_blocker";

	/// <summary>Brush-entity class for non-solid, visual-only func_detail.</summary>
	public const string ClassNameIllusionary = "func_detail_illusionary";

	/// <summary>True when the class name is one of the three func_detail classes
	/// (case-insensitive, surrounding whitespace ignored).</summary>
	public static bool IsFuncDetailClass( string className ) => TryKindOfClass( className, out _ );

	/// <summary>Kind of a func_detail class name, or false when it is not one.
	/// "func_door", null and "" are not func_detail classes.</summary>
	public static bool TryKindOfClass( string className, out FuncDetailKind kind )
	{
		kind = FuncDetailKind.Detail;
		if ( string.IsNullOrWhiteSpace( className ) ) return false;
		if ( string.Equals( className.Trim(), ClassNameDetail, StringComparison.OrdinalIgnoreCase ) ) { kind = FuncDetailKind.Detail; return true; }
		if ( string.Equals( className.Trim(), ClassNameBlocker, StringComparison.OrdinalIgnoreCase ) ) { kind = FuncDetailKind.Blocker; return true; }
		if ( string.Equals( className.Trim(), ClassNameIllusionary, StringComparison.OrdinalIgnoreCase ) ) { kind = FuncDetailKind.Illusionary; return true; }
		return false;
	}

	/// <summary>True when the kind is solid (collides). Detail and Blocker are solid;
	/// Illusionary is not. An unflagged (structural) brush is solid.</summary>
	public static bool IsSolid( FuncDetailKind kind ) => kind != FuncDetailKind.Illusionary;

	/// <summary>True when the kind cuts visleaves. Only Blocker does; Detail and
	/// Illusionary never split space. An unflagged (structural) brush cuts leaves.</summary>
	public static bool CutsLeaves( FuncDetailKind kind ) => kind == FuncDetailKind.Blocker;

	/// <summary>True when the kind renders. All three kinds render — func_detail is
	/// "visual only" for visibility, not invisible.</summary>
	public static bool Renders( FuncDetailKind kind ) => true;

	/// <summary>
	/// Recover the func_detail flags from placed entities: every brush tied to an
	/// entity whose class is a func_detail class is marked with that class's kind.
	/// Brushes tied to any other class (func_door, trigger_*, ...) are structural
	/// and are not collected. A brush tied to a func_detail entity whose payload is
	/// not a MapBrush raises MapEntityException (BrushTypeMismatch) — the mismatch
	/// is named, never silently skipped. With the shipped core registry (no
	/// func_detail classes) this returns an empty set; a registry JSON that defines
	/// the classes makes them collectable with no code change.
	/// </summary>
	public static FuncDetailSet FromEntities( MapEntities entities )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		var set = new FuncDetailSet();
		foreach ( var entity in entities.Entities )
		{
			if ( !TryKindOfClass( entity.ClassName, out var kind ) ) continue;
			foreach ( var brush in entity.GetBrushes<MapBrush>() )
				set.Mark( brush, kind );
		}
		return set;
	}
}

/// <summary>
/// The per-map func_detail flag store: which brushes are func_detail, and of which
/// kind. Keyed by brush reference (the MapBrush instance), so marking never copies
/// or modifies a brush — the flag is workflow state, not brush data. The set is
/// also the BSP bridge (<see cref="ToBspBrushes"/>, <see cref="Build"/>) and the
/// sealing validator (<see cref="Validate"/>).
/// </summary>
public sealed class FuncDetailSet
{
	readonly Dictionary<MapBrush, FuncDetailKind> _flags = new( ReferenceEqualityComparer.Instance );

	/// <summary>How many brushes are flagged (any kind).</summary>
	public int Count => _flags.Count;

	/// <summary>Flagged brushes, in mark order.</summary>
	public IReadOnlyList<MapBrush> Flagged => _flags.Keys.ToArray();

	/// <summary>
	/// Mark one brush func_detail (the one-tap flag). Re-marking the same brush
	/// replaces its kind. The brush itself is untouched — only the flag changes.
	/// </summary>
	public void Mark( MapBrush brush, FuncDetailKind kind )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		_flags[brush] = kind;
	}

	/// <summary>Mark every brush in the list with the same kind.</summary>
	public void MarkAll( IEnumerable<MapBrush> brushes, FuncDetailKind kind )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		foreach ( var brush in brushes )
			Mark( brush, kind );
	}

	/// <summary>Drop a brush's flag; it becomes structural again. Returns false when
	/// the brush was not flagged.</summary>
	public bool Unmark( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		return _flags.Remove( brush );
	}

	/// <summary>Drop every flag.</summary>
	public void Clear() => _flags.Clear();

	/// <summary>Merge another set's flags into this one (the other set wins on a
	/// brush flagged in both). Used to combine entity-derived flags with
	/// hand-marked ones.</summary>
	public void Merge( FuncDetailSet other )
	{
		if ( other is null ) throw new ArgumentNullException( nameof( other ) );
		foreach ( var pair in other._flags )
			_flags[pair.Key] = pair.Value;
	}

	/// <summary>The kind a brush is flagged with, or null when it is structural.</summary>
	public FuncDetailKind? KindOf( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		return _flags.TryGetValue( brush, out var kind ) ? kind : (FuncDetailKind?)null;
	}

	/// <summary>True when the brush is flagged with any func_detail kind.</summary>
	public bool IsFlagged( MapBrush brush ) => KindOf( brush ).HasValue;

	/// <summary>True when the brush is excluded from leaf-cutting: flagged Detail or
	/// Illusionary. A Blocker is flagged but still cuts leaves, so this is false
	/// for it — as is a structural brush.</summary>
	public bool ExcludesFromLeaves( MapBrush brush )
	{
		var kind = KindOf( brush );
		return kind.HasValue && !FuncDetail.CutsLeaves( kind.Value );
	}

	/// <summary>True when the brush is solid (collides): structural, Detail and
	/// Blocker; false only for Illusionary.</summary>
	public bool IsSolid( MapBrush brush )
	{
		var kind = KindOf( brush );
		return !kind.HasValue || FuncDetail.IsSolid( kind.Value );
	}

	/// <summary>True when the brush cuts visleaves: structural or Blocker.</summary>
	public bool CutsLeaves( MapBrush brush )
	{
		var kind = KindOf( brush );
		return !kind.HasValue || FuncDetail.CutsLeaves( kind.Value );
	}

	/// <summary>
	/// The BspBrush adapter (see the file header): each MapBrush becomes one
	/// BspBrush with the same plane set — each face (Normal, Distance) maps to a
	/// BspPlane — and IsDetail set for Detail / Illusionary, clear for Blocker /
	/// structural. Converted brushes are named "brush {index}" (the index into
	/// <paramref name="brushes"/>), so a BSP rejection names the brush it is
	/// about and the caller can map it back. The MapBrushes are not modified.
	/// A null brush in the list is rejected with its index, never skipped.
	/// </summary>
	public IReadOnlyList<BspBrush> ToBspBrushes( IReadOnlyList<MapBrush> brushes ) =>
		ToBspBrushes( brushes, treatAsStructural: null );

	/// <summary>Conversion with one brush forced structural (the leave-one-out
	/// trial in <see cref="Validate"/>): that brush is converted with IsDetail
	/// clear even though the set flags it, so the trial measures the map sealed
	/// <em>with</em> it. A null override changes nothing.</summary>
	IReadOnlyList<BspBrush> ToBspBrushes( IReadOnlyList<MapBrush> brushes, MapBrush treatAsStructural )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var result = new List<BspBrush>( brushes.Count );
		for ( int i = 0; i < brushes.Count; i++ )
		{
			var brush = brushes[i];
			if ( brush is null )
				throw new ArgumentException( $"Brush list contains a null brush at index {i}.", nameof( brushes ) );
			var planes = new List<BspPlane>( brush.Faces.Count );
			foreach ( var face in brush.Faces )
				planes.Add( new BspPlane( face.Normal, face.Distance ) );
			var isDetail = !ReferenceEquals( brush, treatAsStructural ) && ExcludesFromLeaves( brush );
			result.Add( BspBrush.FromPlanes( planes, isDetail, $"brush {i}" ) );
		}
		return result;
	}

	/// <summary>
	/// Build the BSP for a map: convert with <see cref="ToBspBrushes"/> and run
	/// <see cref="BspBuilder.Build"/>. Detail/Illusionary brushes are excluded
	/// from leaf-cutting (they still render + collide — that is downstream of
	/// the BSP, not the BSP's job); Blocker and structural brushes cut leaves.
	/// Invalid brushes and an all-detail map are rejected by the builder with
	/// named reasons (see <see cref="Validate"/> to check first).
	/// </summary>
	public BspTree Build( IReadOnlyList<MapBrush> brushes, BspBuildOptions options = null ) =>
		BspBuilder.Build( ToBspBrushes( brushes ), options );

	/// <summary>
	/// The "sealing brush must not be func_detail" rule, plus the two errors that
	/// make a build impossible. In list order: invalid brushes (Error,
	/// InvalidBrush, with the MapBrush defect codes); an all-detail map (Error,
	/// NoStructuralBrushes); then one Warning (SealingBrushFuncDetail) per flagged
	/// Detail/Illusionary brush that is sealing — the map is sealed with it
	/// treated as structural and unsealed without it (leave-one-out against the
	/// real BSP). A map with no flagged Detail/Illusionary brush reports no
	/// sealing warnings. The brushes and the set are never modified, and no
	/// finding is ever a silent fix: the caller decides what to do with it.
	/// </summary>
	public IReadOnlyList<FuncDetailIssue> Validate( IReadOnlyList<MapBrush> brushes )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var issues = new List<FuncDetailIssue>();

		for ( int i = 0; i < brushes.Count; i++ )
		{
			var brush = brushes[i];
			if ( brush is null )
			{
				issues.Add( new FuncDetailIssue( FuncDetailIssueSeverity.Error, FuncDetailIssue.CodeInvalidBrush,
					$"Brush #{i} is null.", i ) );
				continue;
			}
			var defects = brush.Validate();
			if ( defects.Count > 0 )
				issues.Add( new FuncDetailIssue( FuncDetailIssueSeverity.Error, FuncDetailIssue.CodeInvalidBrush,
					$"Brush #{i} is invalid: {string.Join( "; ", defects.Select( d => d.Code ) )}.", i ) );
		}
		if ( issues.Count > 0 ) return issues;

		// Structural for visibility: unflagged, or flagged Blocker (a blocker cuts
		// leaves, so it holds the seal like a world brush).
		var structural = new List<MapBrush>();
		var flaggedDetail = new List<(MapBrush Brush, int Index)>();
		for ( int i = 0; i < brushes.Count; i++ )
		{
			var kind = KindOf( brushes[i] );
			if ( !kind.HasValue || kind.Value == FuncDetailKind.Blocker ) structural.Add( brushes[i] );
			else flaggedDetail.Add( (brushes[i], i) );
		}
		if ( structural.Count == 0 )
		{
			issues.Add( new FuncDetailIssue( FuncDetailIssueSeverity.Error, FuncDetailIssue.CodeNoStructuralBrushes,
				brushes.Count == 0
					? "The map has no brushes: nothing cuts leaves or seals the map."
					: "Every brush is flagged func_detail: nothing is left to cut leaves or seal the map. At least one brush must stay structural (or be a func_detail_blocker).",
				-1 ) );
			return issues;
		}
		if ( flaggedDetail.Count == 0 ) return issues;

		// Leave-one-out: if the structural-only map is already sealed, no flagged
		// brush can be sealing. Otherwise, each flagged brush is tried as
		// structural: sealed-with-it but unsealed-without-it means it is sealing.
		var baseTree = BspBuilder.Build( ToBspBrushes( structural ) );
		if ( baseTree.IsSealed ) return issues;
		foreach ( var (brush, index) in flaggedDetail )
		{
			var trial = new List<MapBrush>( structural ) { brush };
			// The trial brush is forced structural for this one conversion (the
			// set still flags it): the trial measures the map sealed WITH it.
			var trialTree = BspBuilder.Build( ToBspBrushes( trial, treatAsStructural: brush ) );
			if ( trialTree.IsSealed )
				issues.Add( new FuncDetailIssue( FuncDetailIssueSeverity.Warning, FuncDetailIssue.CodeSealingBrushFuncDetail,
					$"Brush #{index} is flagged func_detail ({KindOf( brush )}) but seals the map: the map is sealed with it and leaks without it. A sealing wall/floor/ceiling must stay structural (world brush), not func_detail.",
					index ) );
		}
		return issues;
	}
}
