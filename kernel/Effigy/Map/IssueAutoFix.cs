using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Effigy;

namespace Effigy.Map;

// IssueAutoFix — Issue auto-fix actions (one-click fixes per issue type).
// Board: hammer-issue-autofix-actions (Geppetto Desktop, Hammer P2).
//
// WHAT IT DOES. Fix(document, issue) -> FixResult: the fix for one issue
// reported by the Issue engine, applied as a single undoable MapUndo command,
// with a before/after summary and the changed objects. Fixes that apply a
// document change:
//   InvalidBrush    — concave solid split into valid convex pieces (JACK-style
//                     triangulation by plane splitting; never silently deletes
//                     the brush) or inverted AABB bounds normalised. Same volume.
//   MissingTarget   — create the target entity stub (info_target, targetname =
//                     the missing name, flagged mapcheck_stub=1) at the referring
//                     entity's origin, or retarget to an existing match.
//   MissingMaterial — assign tools/toolsnodraw and flag it (nodraw).
// Report-only (a result is returned, the document is NOT changed):
//   UnsealedMap / Leak — report the gap location; auto-seal is refused, it
//                     would invent geometry. LimitExceeded — list the biggest
//                     contributors (the engine's message already names them).
//   EntityNoClass / DuplicateTargetName — no safe automatic fix; assign a
//                     class / pick the canonical holder by hand.
//
// WHY Fix TAKES THE DOCUMENT. The engine's Issue carries kind, code,
// severity, location, message and fix hint — it does NOT carry the payload a
// fix needs (the missing target's name lives in the referring entity's
// keyvalues, the brush's faces live in IssueBrush, the limit counts live in
// the limit message). Parsing the message for those would be fragile, so Fix
// reads them from the IssueDocument the issue was checked against. The
// document is the checked snapshot; the IMapUndoTarget given to the
// constructor is the mutable document the fix writes to.
//
// ID MAPPING. IssueBrush / IssueEntity ids are ints; MapBrushState /
// MapEntityState ids are strings. The default mapping is the invariant
// decimal form (brush #12 <-> target id "12"); pass brushIdMap / entityIdMap
// to the constructor when the adapter that projected the document onto
// IssueDocument numbers objects differently. A fix whose object is missing
// on either side refuses with a reason that names the mapping, instead of
// touching the wrong object.
//
// DEPENDENCY STATUS, 2026-11-13 ~16:20 EST. The Issue engine HAS landed
// (kernel/Effigy/Map/IssueEngine.cs: Issue / IssueKind / IssueSeverity /
// IssueDocument / IssueBrush / IssueEntity / IssueEngine.Run / ReCheck), and
// MapUndo HAS landed (MapUndo.cs: IMapUndoTarget / MapCommand / MapUndo) —
// this file builds directly on both, and defines NO issue record of its own
// (the earlier draft's MapIssue is gone: the engine's Issue is the record).
// MapDocument HAS landed (kernel/Effigy/Map/MapDocument.cs: MapDocument :
// IMapUndoTarget, with ToIssueDocument() projecting real face polygons), so
// the fix drives the IMapUndoTarget contract and the InMemoryMapUndoTarget is
// the target the tests drive. NOTE for the integrator: MapDocument.NewId()
// produces 8-hex-char Guid strings and ToIssueDocument() numbers brushes /
// entities by INDEX, so the default id mapping (int #12 <-> string "12") does
// NOT hold for a real MapDocument — pass brushIdMap / entityIdMap to the
// constructor when wiring a real MapDocument. MapBrush HAS
// landed (plane-based, namespace Effigy), but the undo target still stores
// brushes as axis-aligned boxes, so pieces must be boxes (see limits).
// MapCheck HAS landed (namespace Effigy: shallow fixes on IMapCheckDocument,
// no undo): its FixInvalidBrush normalises bounds and DEFERS triangulation
// to this file, and its stub flag mapcheck_stub=1 is the landed convention —
// this file uses the same flag, so Entity Report finds every stub whichever
// fix created it. MapCheck's FixResult (Effigy.FixResult) is a different,
// smaller type from this file's FixResult (Effigy.Map.FixResult, which also
// carries pieces, volumes, flags and changed objects); the integrator maps
// between them with the FixName table below.
//
// LIMITS, stated so nobody mistakes them for bugs. (1) The map target stores
// brushes as axis-aligned boxes (MapBrushState Min/Max/Material), so a concave
// brush whose convex pieces are not axis-aligned boxes is REFUSED, not
// rewritten — the pieces are listed in the result's reason. The common Hammer
// case (axis-aligned concave brush, e.g. an L-prism) splits into boxes exactly.
// (2) Defects that do not change the solid's shape — a non-planar face, a
// duplicate face, coplanar-split faces — cannot be repaired in the AABB target
// model; the fix names the faces and refuses, rather than pretending a
// bounds change fixed them. (3) The stub class info_target is the real Source
// point entity for a named target position, but it is NOT in this repo's core
// registry yet (registry/core-source1.json), so a stub may itself be flagged
// by entity validation until the registry gains it; the mapcheck_stub=1 flag
// is what lets Entity Report find and replace every stub. (4) A brush that
// carries displacements keeps them: the fix removes and re-adds them inside
// the same composite command, so undo restores them exactly. (5) Fixes are
// atomic: every check and the whole decomposition run BEFORE any command
// executes, so a refused fix changes nothing and pushes no undo step.
//
// NO UI. Backend / data-model / API only. No Hammer app, no shelling out.

/// <summary>How a missing-target fix resolves the reference: create a stub
/// entity with the missing targetname (the default), or retarget the reference
/// to an existing entity whose targetname matches a candidate.</summary>
public enum MissingTargetFix
{
	CreateStub,
	Retarget
}

/// <summary>Options for one <see cref="IssueAutoFix.Fix"/> call. Only the
/// missing-target retarget mode reads these; every other fix ignores them.</summary>
public sealed class AutoFixOptions
{
	public MissingTargetFix TargetFix = MissingTargetFix.CreateStub;
	public IReadOnlyList<string> CandidateTargetNames = Array.Empty<string>();
}

/// <summary>The result of one Fix call: whether the document changed, the
/// named fix, a before/after summary in plain words, the changed object ids,
/// any flags left behind (stub marker, nodraw marker), the convex pieces
/// produced, and the undo description when a command was pushed. Applied ==
/// false is a normal outcome for a report-only kind or a refused fix — the
/// Reason says why, and the document is untouched.</summary>
public sealed class FixResult
{
	public bool Applied;
	public string IssueId = "";
	public IssueKind Kind;
	public string FixName = "";
	public string Summary = "";
	public string Reason = "";
	public IReadOnlyList<string> ChangedObjects = Array.Empty<string>();
	public IReadOnlyList<string> Flags = Array.Empty<string>();
	public IReadOnlyList<PolyMesh> Pieces = Array.Empty<PolyMesh>();
	public float BeforeVolume;
	public float AfterVolume;
	public string UndoDescription = "";
}

/// <summary>Auto-fix actions for map issues. Constructed over an
/// <see cref="IMapUndoTarget"/> and its <see cref="MapUndo"/> stack; every fix
/// that changes the document executes exactly one MapCommand (a composite when
/// the fix touches several objects), so every auto-fix is a single undo step.
/// The undo stack may be null — then the command is applied directly and the
/// result says so; the command itself is still undoable, the caller just did
/// not record the step. A fix never mutates the issue or the document passed in.</summary>
public sealed class IssueAutoFix
{
	/// <summary>Class of the stub entity a missing-target fix creates: the
	/// Source point entity for a named target position.</summary>
	public const string StubClassName = "info_target";

	/// <summary>Named flag on every stub a missing-target fix creates, so the
	/// stubs can be found (Entity Report) and replaced with real entities.
	/// Same flag MapCheck's AddStubEntity sets (landed convention).</summary>
	public const string StubFlagKey = "mapcheck_stub";

	public const string StubFlagValue = "1";

	/// <summary>Material a missing-material fix assigns: the canonical nodraw
	/// tool texture (ToolTexture.NoDrawName). Same convention as MapCheck.</summary>
	public const string NoDrawMaterial = ToolTexture.NoDrawName;

	const string StrokeReason =
		"A stroke is active; finish or cancel the stroke before auto-fixing, so the fix lands as its own undo step.";

	const int MaxDecomposeDepth = 12;
	const int MaxPieces = 32;

	readonly IMapUndoTarget _target;
	readonly MapUndo _undo;
	readonly Func<int, string> _brushId;
	readonly Func<int, string> _entityId;

	public IMapUndoTarget Target => _target;
	public MapUndo Undo => _undo;

	/// <summary>The kinds Fix actually changes the document for. Leak,
	/// UnsealedMap, LimitExceeded, EntityNoClass and DuplicateTargetName are
	/// report-only by design.</summary>
	public static IReadOnlyList<IssueKind> FixableKinds => new[]
	{
		IssueKind.InvalidBrush,
		IssueKind.MissingTarget,
		IssueKind.MissingMaterial
	};

	public static bool CanFix( IssueKind kind ) =>
		kind == IssueKind.InvalidBrush ||
		kind == IssueKind.MissingTarget ||
		kind == IssueKind.MissingMaterial;

	public static bool CanFix( Issue issue ) => issue != null && CanFix( issue.Kind );

	public IssueAutoFix( IMapUndoTarget target, MapUndo undo = null,
		Func<int, string> brushIdMap = null, Func<int, string> entityIdMap = null )
	{
		_target = target ?? throw new ArgumentNullException( nameof( target ) );
		_undo = undo;
		_brushId = brushIdMap ?? ( id => id.ToString( CultureInfo.InvariantCulture ) );
		_entityId = entityIdMap ?? ( id => id.ToString( CultureInfo.InvariantCulture ) );
	}

	/// <summary>Apply the fix for one issue from a checked document.
	/// Report-only kinds and refused fixes return Applied == false with a
	/// Reason and change nothing.</summary>
	public FixResult Fix( IssueDocument document, Issue issue, AutoFixOptions options = null )
	{
		if ( document == null ) throw new ArgumentNullException( nameof( document ) );
		if ( issue == null ) throw new ArgumentNullException( nameof( issue ) );
		switch ( issue.Kind )
		{
			case IssueKind.InvalidBrush: return FixInvalidBrush( document, issue );
			case IssueKind.MissingTarget: return FixMissingTarget( document, issue, options );
			case IssueKind.MissingMaterial: return FixMissingMaterial( document, issue );
			case IssueKind.Leak:
				return ReportOnly( issue, "Go to gap — leaks are not auto-sealed",
					"Leak: gap at " + FormatLocation( issue ) +
					" — NOT auto-sealed. Sealing would invent geometry; close the gap by hand and re-run the check. " + issue.Message,
					"Report-only: auto-seal is refused because sealing would invent geometry." );
			case IssueKind.UnsealedMap:
				return ReportOnly( issue, "Go to gap — leaks are not auto-sealed",
					"Unsealed map: gap at " + FormatLocation( issue ) +
					" — NOT auto-sealed. Sealing would invent geometry; close the gap by hand and re-run the check. " + issue.Message,
					"Report-only: auto-seal is refused because sealing would invent geometry." );
			case IssueKind.LimitExceeded:
				return ReportOnly( issue, "Over-limit report",
					"Over limit: " + issue.Message +
					". No automatic fix — remove, func_detail or split the largest contributors, then re-run the check.",
					"Report-only: an over-limit map has no safe automatic fix." );
			case IssueKind.EntityNoClass:
				return ReportOnly( issue, "Assign a class by hand",
					"Entity #" + issue.EntityId + " has no class — no class can be inferred; assign one by hand. " + issue.Message,
					"Report-only: no class can be inferred for an entity with none." );
			case IssueKind.DuplicateTargetName:
				return ReportOnly( issue, "Resolve duplicate targetname",
					"Duplicate targetname: " + issue.Message +
					". Renaming targetnames would break references; choose the canonical holder by hand.",
					"Report-only: renaming targetnames is not an automatic fix." );
			default:
				return NotApplied( issue, "No auto-fix", "No auto-fix is registered for issue kind " + issue.Kind + "." );
		}
	}

	// ---- Invalid brush ----

	FixResult FixInvalidBrush( IssueDocument document, Issue issue )
	{
		string targetBrushId = _brushId( issue.BrushId );
		var brush = _target.FindBrush( targetBrushId );
		if ( brush == null )
			return NotApplied( issue, "Repair brush bounds",
				"Brush #" + issue.BrushId + " is not in the undo target (looked for id '" + targetBrushId +
				"'); the id mapping between the checked document and the undo target disagrees — check the IssueAutoFix id maps." );
		if ( BoundsInverted( brush.Min, brush.Max ) ) return FixInvalidBrushBounds( issue, brush );
		if ( AabbVolume( brush.Min, brush.Max ) <= 1e-6f )
			return NotApplied( issue, "Triangulate into convex pieces",
				"Brush #" + issue.BrushId + " has zero volume (bounds " + brush.Min + " / " + brush.Max +
				"); it cannot be triangulated into same-volume pieces. Rebuild it by hand — auto-fix never silently deletes a brush." );
		var docBrush = FindIssueBrush( document, issue.BrushId );
		if ( docBrush == null )
			return NotApplied( issue, "Triangulate into convex pieces",
				"Brush #" + issue.BrushId + " is not in the checked document; re-run the check on the current document." );
		var mesh = MeshFromIssueBrush( docBrush );
		if ( mesh.FaceCount == 0 )
			return NotApplied( issue, "Triangulate into convex pieces",
				"Brush #" + issue.BrushId + " has no usable faces in the checked document; it cannot be triangulated." );
		return FixInvalidBrushMesh( issue, brush, mesh );
	}

	static bool BoundsInverted( Vec3 min, Vec3 max ) =>
		min.x > max.x || min.y > max.y || min.z > max.z;

	FixResult FixInvalidBrushBounds( Issue issue, MapBrushState brush )
	{
		var beforeMin = brush.Min;
		var beforeMax = brush.Max;
		var afterMin = new Vec3(
			MathF.Min( beforeMin.x, beforeMax.x ),
			MathF.Min( beforeMin.y, beforeMax.y ),
			MathF.Min( beforeMin.z, beforeMax.z ) );
		var afterMax = new Vec3(
			MathF.Max( beforeMin.x, beforeMax.x ),
			MathF.Max( beforeMin.y, beforeMax.y ),
			MathF.Max( beforeMin.z, beforeMax.z ) );
		float beforeVolume = AabbVolume( beforeMin, beforeMax );
		var command = new MoveBrushCommand( brush.Id, beforeMin, beforeMax, afterMin, afterMax );
		if ( command.IsNoOp )
			return NotApplied( issue, "Repair brush bounds", "Brush #" + issue.BrushId + " bounds are already valid; nothing to change." );
		if ( _undo != null && _undo.StrokeActive ) return NotApplied( issue, "Repair brush bounds", StrokeReason );
		Execute( command );
		float afterVolume = AabbVolume( afterMin, afterMax );
		return Applied( issue, "Repair brush bounds",
			"Auto-fix invalid brush #" + issue.BrushId + " (target id '" + brush.Id + "'): bounds normalised " + beforeMin + " / " + beforeMax +
			" -> " + afterMin + " / " + afterMax + "; volume " + afterVolume.ToString( "0.###" ) + " preserved." + UndoNote(),
			new[] { brush.Id }, null, null, beforeVolume, afterVolume );
	}

	FixResult FixInvalidBrushMesh( Issue issue, MapBrushState brush, PolyMesh mesh )
	{
		var validation = MeshValidator.Validate( mesh );
		if ( !validation.IsClosed )
			return NotApplied( issue, "Triangulate into convex pieces",
				"Brush #" + issue.BrushId + " is not a closed solid (" + validation.Errors.Count +
				" validation errors, " + validation.BoundaryEdges + " boundary edges); auto-fix cannot triangulate an open mesh. Repair the faces by hand." );
		float beforeVolume = MathF.Abs( mesh.SignedVolume() );
		if ( beforeVolume <= 1e-6f )
			return NotApplied( issue, "Triangulate into convex pieces",
				"Brush #" + issue.BrushId + " has (near-)zero volume; it cannot be triangulated into same-volume pieces. Rebuild it by hand — auto-fix never silently deletes a brush." );
		var defect = FindShapeDefect( mesh );
		if ( defect != null )
			return NotApplied( issue, "Triangulate into convex pieces", defect );
		if ( MapCsg.IsConvex( mesh ) )
			return NotApplied( issue, "Triangulate into convex pieces",
				"Brush #" + issue.BrushId + " is already a valid convex solid; nothing to change." );
		var pieces = Decompose( mesh );
		if ( pieces == null )
			return NotApplied( issue, "Triangulate into convex pieces",
				"Brush #" + issue.BrushId + "' is concave but no face plane splits it into convex pieces; auto-fix refuses rather than emit invalid pieces. Split it by hand (Clip tool) or rebuild it." );
		float afterVolume = 0f;
		var states = new List<MapBrushState>();
		for ( int i = 0; i < pieces.Count; i++ )
		{
			var piece = pieces[i];
			if ( !MeshValidator.Validate( piece ).IsClosed || !MapCsg.IsConvex( piece ) )
				return NotApplied( issue, "Triangulate into convex pieces",
					"Decomposition produced a piece that is not a closed convex solid; refusing the fix rather than write an invalid brush." );
			Vec3 pieceMin, pieceMax;
			if ( !TryBoxBounds( piece, out pieceMin, out pieceMax ) )
				return NotApplied( issue, "Triangulate into convex pieces",
					"Decomposition produced a convex piece that is not an axis-aligned box; the map target stores boxes only, so the fix cannot be written back. Split the brush by hand into boxes." );
			afterVolume += MathF.Abs( piece.SignedVolume() );
			states.Add( new MapBrushState
			{
				Id = NewPieceId( brush.Id, i + 1 ),
				Min = pieceMin,
				Max = pieceMax,
				Material = brush.Material
			} );
		}
		if ( MathF.Abs( afterVolume - beforeVolume ) > 1e-3f * MathF.Max( 1f, beforeVolume ) )
			return NotApplied( issue, "Triangulate into convex pieces",
				"Decomposition changed the volume (" + beforeVolume.ToString( "0.###" ) + " -> " + afterVolume.ToString( "0.###" ) +
				"); refusing the fix rather than lose or invent volume." );
		if ( _undo != null && _undo.StrokeActive ) return NotApplied( issue, "Triangulate into convex pieces", StrokeReason );
		var children = new List<MapCommand>();
		foreach ( var displacement in _target.Displacements.Where( d => d.BrushId == brush.Id ).ToList() )
			children.Add( new RemoveDisplacementCommand( displacement.Clone() ) );
		children.Add( new RemoveBrushCommand( brush.Clone() ) );
		foreach ( var state in states )
			children.Add( new AddBrushCommand( state ) );
		var command = children.Count == 1
			? children[0]
			: new CompositeMapCommand(
				"Auto-fix invalid brush #" + issue.BrushId + ": split into " + states.Count + " convex pieces",
				children );
		Execute( command );
		var changed = new List<string> { brush.Id };
		changed.AddRange( states.Select( s => s.Id ) );
		var volumes = string.Join( ", ", states.Select( s => AabbVolume( s.Min, s.Max ).ToString( "0.###" ) ) );
		return Applied( issue, "Triangulate into convex pieces",
			"Auto-fix invalid brush #" + issue.BrushId + " (target id '" + brush.Id + "'): concave solid, volume " + beforeVolume.ToString( "0.###" ) +
			" -> " + states.Count + " convex pieces (volumes " + volumes + ", total " + afterVolume.ToString( "0.###" ) +
			"); original brush removed, pieces added as " + string.Join( ", ", states.Select( s => "'" + s.Id + "'" ) ) + "." + UndoNote(),
			changed, null, pieces, beforeVolume, afterVolume );
	}

	/// <summary>Shape defects that do not change the solid's shape, so the
	/// AABB target model cannot repair them: a non-planar face, a duplicate
	/// face, coplanar-split faces. Returns null when the mesh is clean.
	/// The engine flags these too (MapBrushDefect); the fix names the faces
	/// and refuses instead of pretending a bounds change fixed them.</summary>
	static string FindShapeDefect( PolyMesh mesh )
	{
		const float tolerance = 1e-3f;
		for ( int i = 0; i < mesh.Faces.Count; i++ )
		{
			var face = mesh.Faces[i];
			if ( face.Count < 4 ) continue;
			var normal = mesh.FaceNormal( face );
			float d = Vec3.Dot( normal, mesh.Positions[face.Indices[0]] );
			for ( int k = 1; k < face.Count; k++ )
			{
				float off = MathF.Abs( Vec3.Dot( normal, mesh.Positions[face.Indices[k]] ) - d );
				if ( off > tolerance )
					return "Brush face " + i + " is non-planar (a corner is " + off.ToString( "0.###" ) +
						" off its plane); the AABB target model cannot re-planarise it — fix the face in the face editor.";
			}
		}
		for ( int i = 0; i < mesh.Faces.Count; i++ )
		for ( int j = i + 1; j < mesh.Faces.Count; j++ )
		{
			var a = mesh.Faces[i];
			var b = mesh.Faces[j];
			var na = mesh.FaceNormal( a );
			var nb = mesh.FaceNormal( b );
			float dot = Vec3.Dot( na, nb );
			if ( MathF.Abs( dot ) < 0.9999f ) continue;
			float da = Vec3.Dot( na, mesh.Positions[a.Indices[0]] );
			float db = Vec3.Dot( nb, mesh.Positions[b.Indices[0]] );
			float gap = dot > 0 ? MathF.Abs( da - db ) : MathF.Abs( da + db );
			if ( gap > tolerance ) continue;
			var ca = mesh.FaceCentroid( a );
			var cb = mesh.FaceCentroid( b );
			if ( ( ca - cb ).Length <= tolerance )
				return "Brush faces " + i + " and " + j + " are duplicates; remove one by hand.";
			return "Brush faces " + i + " and " + j + " are coplanar splits of one plane; merge them in the face editor.";
		}
		return null;
	}

	/// <summary>Build the brush solid from the engine's face polygons
	/// (outward-wound, per the IssueEngine header). Vertices are welded by
	/// position; degenerate faces are dropped. The result is the solid the
	/// engine checked — no plane solving, no guessing.</summary>
	static PolyMesh MeshFromIssueBrush( IssueBrush brush )
	{
		var mesh = new PolyMesh();
		var weld = new Dictionary<(long, long, long), int>();
		foreach ( var face in brush.Faces )
		{
			if ( face == null || face.Vertices == null || face.Vertices.Count < 3 ) continue;
			var idx = new int[face.Vertices.Count];
			for ( int i = 0; i < face.Vertices.Count; i++ )
			{
				var p = face.Vertices[i];
				var key = ( (long)MathF.Round( p.x * 10000f ), (long)MathF.Round( p.y * 10000f ), (long)MathF.Round( p.z * 10000f ) );
				if ( !weld.TryGetValue( key, out int at ) )
				{
					at = mesh.AddVertex( p );
					weld[key] = at;
				}
				idx[i] = at;
			}
			if ( idx.Distinct().Count() < 3 ) continue;
			mesh.AddFace( idx, null, 0 );
		}
		return mesh;
	}

	static IssueBrush FindIssueBrush( IssueDocument document, int id )
	{
		if ( document.Brushes == null ) return null;
		foreach ( var brush in document.Brushes )
			if ( brush != null && brush.Id == id ) return brush;
		return null;
	}

	static IssueEntity FindIssueEntity( IssueDocument document, int id )
	{
		if ( document.Entities == null ) return null;
		foreach ( var entity in document.Entities )
			if ( entity != null && entity.Id == id ) return entity;
		return null;
	}

	// ---- Missing target ----

	FixResult FixMissingTarget( IssueDocument document, Issue issue, AutoFixOptions options )
	{
		var source = FindIssueEntity( document, issue.EntityId );
		if ( source == null )
			return NotApplied( issue, "Create target stub (or retarget)",
				"Entity #" + issue.EntityId + " is not in the checked document; re-run the check on the current document." );
		var dangling = DanglingTargets( document, source );
		var specials = SpecialTargets( source );
		if ( dangling.Count == 0 )
		{
			if ( specials.Count > 0 )
				return NotApplied( issue, "Create target stub (or retarget)",
					"Target '" + specials[0] + "' is an engine special target (!self, !activator, ...); it always resolves and is never missing." );
			return NotApplied( issue, "Create target stub (or retarget)",
				"Entity #" + issue.EntityId + " no longer has a dangling target; the issue is stale (an earlier fix already resolved it)." );
		}
		if ( options != null && options.TargetFix == MissingTargetFix.Retarget )
			return FixMissingTargetRetarget( document, issue, source, dangling, options );
		if ( _undo != null && _undo.StrokeActive ) return NotApplied( issue, "Create target stub (or retarget)", StrokeReason );
		var commands = new List<MapCommand>();
		var stubIds = new List<string>();
		var flags = new List<string>();
		var parts = new List<string>();
		foreach ( var pair in dangling )
		{
			string stubId = NewStubId( pair.Name );
			var stub = new MapEntityState
			{
				Id = stubId,
				ClassName = StubClassName,
				Origin = source.Origin,
				Properties = new Dictionary<string, string>
				{
					{ "targetname", pair.Name },
					{ StubFlagKey, StubFlagValue }
				}
			};
			commands.Add( new AddEntityCommand( stub ) );
			stubIds.Add( stubId );
			flags.Add( StubFlagKey + ":" + stubId );
			parts.Add( "'" + pair.Name + "' (key '" + pair.Key + "') -> stub '" + stubId + "' (" + StubClassName + ", " + StubFlagKey + "=" + StubFlagValue + ") at " + source.Origin );
		}
		MapCommand command = commands.Count == 1
			? commands[0]
			: new CompositeMapCommand( "Auto-fix missing targets for entity #" + issue.EntityId, commands );
		Execute( command );
		return Applied( issue, "Create target stub (or retarget)",
			"Auto-fix missing target for entity #" + issue.EntityId + " ('" + source.ClassName + "'): created stub(s) " +
			string.Join( "; ", parts ) +
			". Each stub is a placeholder — replace it with the real entity." + UndoNote(),
			stubIds, flags, null, 0f, 0f );
	}

	FixResult FixMissingTargetRetarget( IssueDocument document, Issue issue, IssueEntity source,
		List<(string Key, string Name)> dangling, AutoFixOptions options )
	{
		string targetEntityId = _entityId( issue.EntityId );
		var referrer = _target.FindEntity( targetEntityId );
		if ( referrer == null )
			return NotApplied( issue, "Create target stub (or retarget)",
				"Entity #" + issue.EntityId + " is not in the undo target (looked for id '" + targetEntityId +
				"'); the id mapping between the checked document and the undo target disagrees — check the IssueAutoFix id maps." );
		var candidates = options != null && options.CandidateTargetNames != null
			? options.CandidateTargetNames
			: Array.Empty<string>();
		var commands = new List<MapCommand>();
		var parts = new List<string>();
		foreach ( var pair in dangling )
		{
			string match = null;
			string matchId = null;
			foreach ( var candidate in candidates )
			{
				if ( string.IsNullOrWhiteSpace( candidate ) ) continue;
				var holder = FindTargetByName( candidate );
				if ( holder != null ) { match = candidate; matchId = holder.Id; break; }
			}
			if ( match == null )
				return NotApplied( issue, "Create target stub (or retarget)",
					"No candidate target exists in the document for '" + pair.Name + "', so retarget is impossible; create a stub instead." );
			string oldValue;
			bool had = TryGetTargetProperty( referrer, pair.Key, out oldValue );
			var command = new SetEntityPropertyCommand( targetEntityId, pair.Key, had ? oldValue : "", match, had );
			if ( command.IsNoOp ) continue;
			commands.Add( command );
			parts.Add( "'" + pair.Key + "' '" + pair.Name + "' -> '" + match + "' (entity '" + matchId + "')" );
		}
		if ( commands.Count == 0 )
			return NotApplied( issue, "Create target stub (or retarget)",
				"Entity #" + issue.EntityId + " already targets the candidate(s); nothing to change." );
		if ( _undo != null && _undo.StrokeActive ) return NotApplied( issue, "Create target stub (or retarget)", StrokeReason );
		MapCommand composite = commands.Count == 1
			? commands[0]
			: new CompositeMapCommand( "Auto-fix missing targets for entity #" + issue.EntityId + ": retarget", commands );
		Execute( composite );
		return Applied( issue, "Create target stub (or retarget)",
			"Auto-fix missing target for entity #" + issue.EntityId + " ('" + source.ClassName + "'): retargeted " +
			string.Join( "; ", parts ) + "." + UndoNote(),
			new[] { targetEntityId }, null, null, 0f, 0f );
	}

	/// <summary>Every (key, name) on the entity that dangles: a target /
	/// killtarget value that is non-empty, not a "!" special target, and held
	/// by no entity in the checked document NOR in the undo target (a stub an
	/// earlier fix created counts as held, so Fix is idempotent across a
	/// FixAll run). Same key set as MissingTargetRule.TargetKeys.</summary>
	List<(string Key, string Name)> DanglingTargets( IssueDocument document, IssueEntity source )
	{
		var dangling = new List<(string Key, string Name)>();
		if ( source.KeyValues == null ) return dangling;
		foreach ( var key in MissingTargetRule.TargetKeys )
		{
			var name = source.GetKeyValue( key );
			if ( string.IsNullOrWhiteSpace( name ) ) continue;
			if ( name.StartsWith( "!", StringComparison.Ordinal ) ) continue;
			if ( HasTargetName( document, name ) ) continue;
			if ( FindTargetByName( name ) != null ) continue;
			dangling.Add( ( key, name ) );
		}
		return dangling;
	}

	List<string> SpecialTargets( IssueEntity source )
	{
		var specials = new List<string>();
		if ( source.KeyValues == null ) return specials;
		foreach ( var key in MissingTargetRule.TargetKeys )
		{
			var name = source.GetKeyValue( key );
			if ( !string.IsNullOrWhiteSpace( name ) && name.StartsWith( "!", StringComparison.Ordinal ) )
				specials.Add( name );
		}
		return specials;
	}

	static bool HasTargetName( IssueDocument document, string name )
	{
		if ( document.Entities == null ) return false;
		foreach ( var entity in document.Entities )
			if ( entity != null && string.Equals( entity.TargetName, name, StringComparison.OrdinalIgnoreCase ) )
				return true;
		return false;
	}

	MapEntityState FindTargetByName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		foreach ( var entity in _target.Entities )
		{
			string targetName;
			if ( entity.Properties != null &&
				entity.Properties.TryGetValue( "targetname", out targetName ) &&
				string.Equals( targetName, name, StringComparison.OrdinalIgnoreCase ) )
				return entity;
		}
		return null;
	}

	static bool TryGetTargetProperty( MapEntityState entity, string key, out string value )
	{
		value = "";
		if ( entity.Properties == null ) return false;
		if ( entity.Properties.TryGetValue( key, out value ) ) return true;
		foreach ( var kv in entity.Properties )
			if ( string.Equals( kv.Key, key, StringComparison.OrdinalIgnoreCase ) ) { value = kv.Value; return true; }
		value = "";
		return false;
	}

	string NewStubId( string targetName )
	{
		var chars = targetName.ToCharArray();
		for ( int i = 0; i < chars.Length; i++ )
		{
			char c = chars[i];
			if ( !( char.IsLetterOrDigit( c ) || c == '_' || c == '-' ) ) chars[i] = '-';
		}
		string stem = new string( chars );
		if ( string.IsNullOrWhiteSpace( stem ) ) stem = "stub";
		string candidate = "autofix-" + stem;
		int suffix = 2;
		while ( _target.FindEntity( candidate ) != null )
		{
			candidate = "autofix-" + stem + "-" + suffix;
			suffix++;
		}
		return candidate;
	}

	// ---- Missing material ----

	FixResult FixMissingMaterial( IssueDocument document, Issue issue )
	{
		string targetBrushId = _brushId( issue.BrushId );
		var brush = _target.FindBrush( targetBrushId );
		if ( brush == null )
			return NotApplied( issue, "Assign tools/toolsnodraw",
				"Brush #" + issue.BrushId + " is not in the undo target (looked for id '" + targetBrushId +
				"'); the id mapping between the checked document and the undo target disagrees — check the IssueAutoFix id maps." );
		var docBrush = FindIssueBrush( document, issue.BrushId );
		if ( docBrush == null )
			return NotApplied( issue, "Assign tools/toolsnodraw",
				"Brush #" + issue.BrushId + " is not in the checked document; re-run the check on the current document." );
		var badFaces = new List<int>();
		for ( int i = 0; i < docBrush.Faces.Count; i++ )
		{
			var face = docBrush.Faces[i];
			if ( face != null && string.IsNullOrWhiteSpace( face.Material ) ) badFaces.Add( i );
		}
		if ( badFaces.Count == 0 )
			return NotApplied( issue, "Assign tools/toolsnodraw",
				"Brush #" + issue.BrushId + " no longer has a face without a material; the issue is stale." );
		if ( ToolTexture.IsNoDraw( brush.Material ) )
			return NotApplied( issue, "Assign tools/toolsnodraw",
				"Brush #" + issue.BrushId + " is already nodraw; nothing to change." );
		if ( _undo != null && _undo.StrokeActive ) return NotApplied( issue, "Assign tools/toolsnodraw", StrokeReason );
		string before = brush.Material;
		var command = new SetBrushMaterialCommand( targetBrushId, before, NoDrawMaterial );
		if ( command.IsNoOp )
			return NotApplied( issue, "Assign tools/toolsnodraw", "Brush #" + issue.BrushId + " already uses " + NoDrawMaterial + "." );
		Execute( command );
		return Applied( issue, "Assign tools/toolsnodraw",
			"Auto-fix missing material on brush #" + issue.BrushId + " (target id '" + targetBrushId + "'): face(s) " +
			string.Join( ", ", badFaces ) + " have no material; '" +
			( string.IsNullOrEmpty( before ) ? "(none assigned)" : before ) + "' -> " + NoDrawMaterial +
			" for the whole brush (the undo target carries one material per brush; assign per-face materials when the face model lands). Flagged nodraw; assign a real material to clear the flag." + UndoNote(),
			new[] { targetBrushId }, new[] { "material-nodraw:" + targetBrushId }, null, 0f, 0f );
	}

	// ---- Execution + results ----

	void Execute( MapCommand command )
	{
		if ( _undo != null ) _undo.Execute( command );
		else command.Do( _target );
	}

	string UndoNote() =>
		_undo != null
			? " One undo step (" + _undo.LastDescription + ")."
			: " (No undo stack attached: the fix was applied directly. Execute the same fix through a MapUndo to record the step.)";

	static string FormatLocation( Issue issue ) =>
		issue.HasLocation ? issue.Location.ToString() : "(no location recorded)";

	static FixResult NotApplied( Issue issue, string fixName, string reason ) => new FixResult
	{
		Applied = false,
		IssueId = issue.Code,
		Kind = issue.Kind,
		FixName = fixName,
		Summary = "Auto-fix did not apply: " + reason,
		Reason = reason
	};

	static FixResult ReportOnly( Issue issue, string fixName, string summary, string reason ) => new FixResult
	{
		Applied = false,
		IssueId = issue.Code,
		Kind = issue.Kind,
		FixName = fixName,
		Summary = summary,
		Reason = reason
	};

	FixResult Applied( Issue issue, string fixName, string summary,
		IReadOnlyList<string> changed, IReadOnlyList<string> flags, IReadOnlyList<PolyMesh> pieces,
		float beforeVolume, float afterVolume ) => new FixResult
	{
		Applied = true,
		IssueId = issue.Code,
		Kind = issue.Kind,
		FixName = fixName,
		Summary = summary,
		ChangedObjects = changed ?? Array.Empty<string>(),
		Flags = flags ?? Array.Empty<string>(),
		Pieces = pieces ?? Array.Empty<PolyMesh>(),
		BeforeVolume = beforeVolume,
		AfterVolume = afterVolume,
		UndoDescription = _undo != null ? _undo.LastDescription : ""
	};

	// ---- Convex decomposition ----

	/// <summary>Recursive plane-split convex decomposition (JACK-style
	/// triangulation into convex pieces). A piece that is already convex is
	/// kept; otherwise each face plane is tried as a split — only a plane that
	/// cuts real volume off both sides is accepted — and both halves recurse.
	/// Returns null when no split sequence reaches all-convex pieces.</summary>
	static List<PolyMesh> Decompose( PolyMesh mesh )
	{
		var pieces = new List<PolyMesh>();
		return TryDecompose( mesh, 0, pieces ) ? pieces : null;
	}

	static bool TryDecompose( PolyMesh mesh, int depth, List<PolyMesh> pieces )
	{
		if ( pieces.Count >= MaxPieces ) return false;
		if ( MapCsg.IsConvex( mesh ) ) { pieces.Add( mesh ); return true; }
		if ( depth >= MaxDecomposeDepth ) return false;
		float total = MathF.Abs( mesh.SignedVolume() );
		float eps = 1e-3f * MathF.Max( 1f, total );
		foreach ( var plane in MapCsg.BrushPlanes( mesh ) )
		{
			var front = SplitSolid( mesh, plane.Normal, plane.Distance );
			var back = SplitSolid( mesh, -plane.Normal, -plane.Distance );
			float frontVolume = MathF.Abs( front.SignedVolume() );
			float backVolume = MathF.Abs( back.SignedVolume() );
			if ( frontVolume <= eps || backVolume <= eps ) continue;
			if ( frontVolume >= total - eps || backVolume >= total - eps ) continue;
			if ( MathF.Abs( frontVolume + backVolume - total ) > 1e-3f * MathF.Max( 1f, total ) ) continue;
			if ( !MeshValidator.Validate( front ).IsClosed || !MeshValidator.Validate( back ).IsClosed ) continue;
			int mark = pieces.Count;
			if ( TryDecompose( front, depth + 1, pieces ) && TryDecompose( back, depth + 1, pieces ) ) return true;
			pieces.RemoveRange( mark, pieces.Count - mark );
		}
		return false;
	}

	/// <summary>Split a closed solid by a plane into the kept half
	/// (plane.Distance &gt;= 0), capped so the result is a solid again.
	/// Built on <see cref="MeshClip.Section"/> rather than
	/// <see cref="MeshClip.TrimPlane"/> on purpose: TrimPlane caps the whole
	/// section loop of the ORIGINAL solid, which is wrong when the cutting plane
	/// coincides with one of the solid's own faces (the reflex face of an
	/// L-prism) — the cap then covers area that belongs to the other half, and
	/// the kept face is duplicated as a flap, so the piece's convex hull is
	/// inflated and it never tests convex. SplitSolid instead drops in-plane
	/// faces whose outward normal points INTO the kept half (a flap: the face
	/// bounds the other half, not this one), spike-cleans the face polygons,
	/// and re-caps from the kept half's own boundary loops. Skin/paint are
	/// dropped: brush solids carry neither.</summary>
	static PolyMesh SplitSolid( PolyMesh mesh, Vec3 normal, float offset )
	{
		var plane = new ClipPlane( normal, offset );
		var clipped = MeshClip.Section( mesh, plane );
		var keptNormal = plane.Normal;
		var result = new PolyMesh();
		var weld = new Dictionary<(long, long, long), int>();
		foreach ( var face in clipped.Faces )
		{
			bool inPlane = true;
			foreach ( int vi in face.Indices )
				if ( MathF.Abs( plane.Distance( clipped.Positions[vi] ) ) > 1e-4f ) { inPlane = false; break; }
			if ( inPlane && face.Count >= 3 )
			{
				var faceNormal = clipped.FaceNormal( face );
				if ( Vec3.Dot( faceNormal, keptNormal ) > 0.5f ) continue;
			}
			var cleaned = CleanPolygon( face.Indices, clipped.Positions );
			if ( cleaned.Count < 3 ) continue;
			var mapped = new List<int>();
			foreach ( int vi in cleaned )
			{
				var p = clipped.Positions[vi];
				var key = ( (long)MathF.Round( p.x * 10000f ), (long)MathF.Round( p.y * 10000f ), (long)MathF.Round( p.z * 10000f ) );
				if ( !weld.TryGetValue( key, out int at ) )
				{
					at = result.AddVertex( p );
					weld[key] = at;
				}
				if ( mapped.Count == 0 || mapped[mapped.Count - 1] != at ) mapped.Add( at );
			}
			if ( mapped.Count > 1 && mapped[0] == mapped[mapped.Count - 1] ) mapped.RemoveAt( mapped.Count - 1 );
			if ( mapped.Count < 3 ) continue;
			result.AddFace( mapped.ToArray(), null, face.Material );
		}
		CapBoundaryLoops( result, -keptNormal );
		return Compact( result );
	}

	/// <summary>Remove consecutive duplicate vertices and backtracking spikes
	/// (A, B, C collinear with the walk reversing at B — a zero-area excursion,
	/// e.g. where a cut passes through an original vertex lying in the plane).
	/// A genuine reflex corner is perpendicular, not a reversal, so it is kept.</summary>
	static List<int> CleanPolygon( IReadOnlyList<int> indices, IReadOnlyList<Vec3> positions )
	{
		var pts = new List<int>( indices );
		bool changed = true;
		while ( changed && pts.Count > 0 )
		{
			changed = false;
			for ( int i = 0; i < pts.Count; i++ )
			{
				int j = ( i + 1 ) % pts.Count;
				if ( i == j ) break;
				if ( pts[i] == pts[j] || ( positions[pts[i]] - positions[pts[j]] ).LengthSquared < 1e-8f )
				{
					pts.RemoveAt( j );
					changed = true;
					break;
				}
			}
		}
		changed = true;
		while ( changed && pts.Count > 3 )
		{
			changed = false;
			for ( int i = 0; i < pts.Count; i++ )
			{
				int a = pts[( i + pts.Count - 1 ) % pts.Count];
				int b = pts[i];
				int c = pts[( i + 1 ) % pts.Count];
				var ab = positions[b] - positions[a];
				var bc = positions[c] - positions[b];
				if ( ab.LengthSquared < 1e-12f || bc.LengthSquared < 1e-12f ) continue;
				if ( Vec3.Dot( ab.Normal, bc.Normal ) < -0.9999f )
				{
					pts.RemoveAt( i );
					changed = true;
					break;
				}
			}
		}
		return pts;
	}

	/// <summary>Close the cut with one n-gon cap per boundary loop, oriented so
	/// its Newell normal matches <paramref name="capNormal"/> (the kept half's
	/// outward normal). Loops are chained undirected — a cap edge may run either
	/// way round the loop — so a loop shared by two boundary paths still chains.</summary>
	static void CapBoundaryLoops( PolyMesh mesh, Vec3 capNormal )
	{
		var use = new Dictionary<(int, int), int>();
		for ( int fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			for ( int k = 0; k < f.Count; k++ )
			{
				int a = f.Indices[k], b = f.Indices[( k + 1 ) % f.Count];
				var key = a < b ? ( a, b ) : ( b, a );
				use.TryGetValue( key, out int n );
				use[key] = n + 1;
			}
		}
		var neighbors = new Dictionary<int, List<int>>();
		foreach ( var kv in use )
		{
			if ( kv.Value != 1 ) continue;
			int a = kv.Key.Item1, b = kv.Key.Item2;
			if ( !neighbors.TryGetValue( a, out var la ) ) neighbors[a] = la = new List<int>();
			if ( !neighbors.TryGetValue( b, out var lb ) ) neighbors[b] = lb = new List<int>();
			la.Add( b );
			lb.Add( a );
		}
		var visited = new HashSet<int>();
		foreach ( int start in neighbors.Keys.ToList() )
		{
			if ( visited.Contains( start ) ) continue;
			var loop = new List<int>();
			int prev = -1, cur = start;
			bool closed = false;
			while ( true )
			{
				visited.Add( cur );
				loop.Add( cur );
				int next = -1;
				foreach ( int nb in neighbors[cur] )
					if ( nb != prev ) { next = nb; break; }
				if ( next < 0 ) break;
				if ( next == start ) { closed = true; break; }
				if ( visited.Contains( next ) ) break;
				prev = cur;
				cur = next;
				if ( loop.Count > mesh.Positions.Count + 1 ) break;
			}
			if ( !closed || loop.Count < 3 ) continue;
			var cleaned = CleanPolygon( loop, mesh.Positions );
			if ( cleaned.Count < 3 ) continue;
			var idx = cleaned.ToArray();
			if ( Vec3.Dot( NewellNormal( mesh.Positions, idx ), capNormal ) < 0f ) Array.Reverse( idx );
			mesh.AddFace( idx, null, 0 );
		}
	}

	/// <summary>Newell's method, as in <see cref="PolyMesh.FaceNormal"/> — but
	/// over an index list, so it also orients cap loops that are not faces yet.</summary>
	static Vec3 NewellNormal( IReadOnlyList<Vec3> positions, IReadOnlyList<int> indices )
	{
		var n = Vec3.Zero;
		for ( int i = 0; i < indices.Count; i++ )
		{
			var a = positions[indices[i]];
			var b = positions[indices[( i + 1 ) % indices.Count]];
			n += new Vec3(
				( a.y - b.y ) * ( a.z + b.z ),
				( a.z - b.z ) * ( a.x + b.x ),
				( a.x - b.x ) * ( a.y + b.y ) );
		}
		return n.Normal;
	}

	/// <summary>Drop vertices no face references, so a stray cut-point cannot
	/// inflate a later convex-hull test.</summary>
	static PolyMesh Compact( PolyMesh mesh )
	{
		var used = new HashSet<int>();
		foreach ( var f in mesh.Faces )
			foreach ( int i in f.Indices ) used.Add( i );
		if ( used.Count == mesh.Positions.Count ) return mesh;
		var remap = new Dictionary<int, int>();
		var result = new PolyMesh();
		foreach ( var f in mesh.Faces )
		{
			var idx = new int[f.Count];
			for ( int k = 0; k < f.Count; k++ )
			{
				int old = f.Indices[k];
				if ( !remap.TryGetValue( old, out int at ) )
				{
					at = result.AddVertex( mesh.Positions[old] );
					remap[old] = at;
				}
				idx[k] = at;
			}
			result.AddFace( idx, null, f.Material );
		}
		return result;
	}

	static float AabbVolume( Vec3 min, Vec3 max ) =>
		MathF.Abs( ( max.x - min.x ) * ( max.y - min.y ) * ( max.z - min.z ) );

	/// <summary>True when the piece is exactly its own axis-aligned bounds
	/// (a piece is always inside its bounds, so equal volumes mean equal
	/// solids), with the bounds returned. That is the only shape the map
	/// target can store as a brush.</summary>
	static bool TryBoxBounds( PolyMesh piece, out Vec3 min, out Vec3 max )
	{
		min = Vec3.Zero;
		max = Vec3.Zero;
		if ( piece == null || piece.Positions.Count == 0 ) return false;
		min = piece.Positions[0];
		max = piece.Positions[0];
		foreach ( var p in piece.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		float aabbVolume = AabbVolume( min, max );
		float volume = MathF.Abs( piece.SignedVolume() );
		if ( aabbVolume <= 1e-6f ) return false;
		return MathF.Abs( aabbVolume - volume ) <= 1e-3f * MathF.Max( 1f, volume );
	}

	string NewPieceId( string brushId, int index )
	{
		string candidate = brushId + "-fix-" + index;
		int suffix = index;
		while ( _target.FindBrush( candidate ) != null )
		{
			suffix++;
			candidate = brushId + "-fix-" + suffix;
		}
		return candidate;
	}
}
