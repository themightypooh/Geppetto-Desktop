// Pre-compile safety-net gate: the full Check-for-Problems pass, run before
// every compile preset (Hammer Desktop P2b: hammer-compile-safety-net-gate).
//
// Backend / data-model / API only — no Desktop UI, no viewer, no Hammer app,
// no shelling out to Hammer/Hammer++/VBSP/VVIS/VRAD.
//
// What it does: before any build stage runs, the gate runs the full check
// (MapCheck.RunFullCheck with the REAL IssueEngine behind it — see
// IssueEngineIssueSource, which is the ~20-line adapter MapCheck's header
// prescribes for when IssueEngine lands). Errors block the compile with the
// named issues; warnings are logged and the compile proceeds; a
// caller-supplied override (CompileOptions.SkipPreCompileGate) skips the gate
// and is logged.
//
// Wiring (CompileRunner.Run): the gate runs in the Preparing phase, first in
// every preset (QuickPlay / Preview / Final all go through Run). It is a
// pre-stage, not a build stage: CompilePresetDef.Stages() and
// CompileResult.CompletedStages still list only BSP / visibility / lightmap
// (plus the cubemap / pack stages the runner appends), and a gate block
// reports FailedStage BspBuild because no build stage has run. The existing
// runner tests pin FailedStage == BspBuild for pre-build validation
// failures (invalid brush, over-limit); a dedicated PreCompileGate stage
// member would break them, and this file does not own those tests.
//
// Leak verdict ownership: the gate SKIPS the leak / unsealed-map rules
// (SkippedRules). The real leak verdict needs the BSP portal graph and
// LeakFinder's flood-fill, which only exist after the BSP build — and the
// BSP stage's leak diagnostics (LeakFinder path + .lin point file) are the
// fix loop's core artifact. A probe-based gate block would steal that
// verdict and break the existing leak test. When the check document DOES
// carry a LeakGraph (IMapCheckDocument.LeakGraph), the gate runs the real
// LeakFinder-backed Leak rule from MapCheck's DefaultIssueSource and a leak
// DOES block. On the CompileRunner path there is never a LeakGraph, so
// leaking maps pass the gate and fail at the BSP stage exactly as before.
//
// Giant skybox: the IssueEngine's GiantSkyboxRule (a 2D skybox box —
// sky-material brushes — vastly larger than the map bounds is a warning).
// The 3D skybox section (SkyboxWorldspawn, skybox space x SectionScale) is
// NOT in the check document, so this rule never fires on legitimate distant
// scenery — only on sky-material brushes in the main map.
//
// Geometry is the kernel's inches and Z-up convention. Deterministic: same
// document -> same issue order (engine issues sorted by the engine, then
// MissingTarget, BadKeyValue, Leak from MapCheck's DefaultIssueSource),
// same CHK-#### ids.
//
// Approximation, stated honestly: on the IMapCheckDocument path the engine
// sees each brush's BOUNDS BOX (CheckBrush carries bounds, not faces), so a
// non-box convex brush is checked as its box and a per-face defect on it
// can be missed by the gate. On the CompileInput path the engine sees the
// REAL solved face polygons (PreCompileGate.Check(CompileInput)), so the
// runner path is exact. Either way the BSP stage re-validates every brush
// with CompileBrush.Validate() and is the backstop — the gate is an early
// safety net, not the only validator.
//
// MapDocument seam (deferred, final integrator): MapDocument does not
// implement IMapCheckDocument yet. The adapter is a thin projection of
// MapDocument.Brushes (MapBrushData.TryComputeVertices gives bounds) and
// MapDocument.Entities (MapEntityData has Id / ClassName / Origin /
// KeyValues / TargetName) onto InMemoryMapCheckDocument; no MapCheck or
// gate API changes when it lands. This ticket does not write it, because
// MapDocument.cs cannot compile in the isolated harness without dragging
// the shared tree (which is currently broken by sibling files), and the
// ticket's wiring target is CompileRunner / CompileInput.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Effigy.Map;

namespace Effigy;

/// <summary>Outcome of one <see cref="PreCompileGate.Check"/> run.</summary>
public sealed class PreCompileGateResult
{
	/// <summary>True when the gate ran (false when skipped by override).</summary>
	public bool Ran { get; set; } = true;
	/// <summary>True when the caller skipped the gate via CompileOptions.SkipPreCompileGate.</summary>
	public bool Overridden { get; set; }
	/// <summary>True when the gate found errors: the compile must not run.</summary>
	public bool Blocked { get; set; }
	/// <summary>Issues from the full check, in detection order (CHK-####). Empty on a clean pass.</summary>
	public IReadOnlyList<MapIssue> Issues { get; set; } = Array.Empty<MapIssue>();
	/// <summary>Rules that could not honestly run (leak / unsealed-map without a BSP portal graph).</summary>
	public IReadOnlyList<string> SkippedRules { get; set; } = Array.Empty<string>();
	public int ErrorCount { get; set; }
	public int WarningCount { get; set; }
	public bool HasErrors => ErrorCount > 0;
	public bool HasWarnings => WarningCount > 0;
	public string Summary { get; set; } = "";

	/// <summary>Result for a gate skipped by caller override: nothing ran, nothing blocked, nothing found.</summary>
	public static PreCompileGateResult Skipped() => new PreCompileGateResult
	{
		Ran = false,
		Overridden = true,
		Blocked = false,
		Issues = Array.Empty<MapIssue>(),
		SkippedRules = Array.Empty<string>(),
		Summary = "pre-compile gate skipped by caller override",
	};
}

/// <summary>
/// The pre-compile safety-net gate. <see cref="Check(IMapCheckDocument, IIssueSource)"/>
/// is the document entry point (the Check-for-Problems pass);
/// <see cref="Check(CompileInput, CompileOptions)"/> is the CompileRunner
/// entry point (exact face polygons, no bounds approximation).
/// </summary>
public static class PreCompileGate
{
	/// <summary>Run the full check on a check document. Errors block, warnings do not.</summary>
	public static PreCompileGateResult Check( IMapCheckDocument document, IIssueSource source = null )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var used = source ?? new IssueEngineIssueSource();
		var report = MapCheck.RunFullCheck( document, used );
		return FromReport( report );
	}

	/// <summary>
	/// Run the full check on a compile input, with the REAL solved face
	/// polygons (exact geometry, per-face materials) instead of the
	/// bounds-box approximation the document path uses. Limits come from
	/// <paramref name="options"/> (MaxBrushes); the remaining engine limits
	/// stay at the IssueEngine defaults (Source published limits).
	/// </summary>
	public static PreCompileGateResult Check( CompileInput input, CompileOptions options = null )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		// Duplicate brush / entity ids would throw out of the check
		// document's AddBrush / AddEntity below (the document forbids
		// them by construction) — and the runner catches that as a bare
		// exception instead of a named issue. Guard first and report
		// them as a named gate error instead. FromDocument assigns
		// sequential ids, so this only fires on directly-constructed
		// inputs.
		var duplicates = FindDuplicateIds( input );
		if ( duplicates.Count > 0 )
		{
			return new PreCompileGateResult
			{
				Ran = true,
				Overridden = false,
				Blocked = true,
				Issues = duplicates,
				SkippedRules = Array.Empty<string>(),
				ErrorCount = duplicates.Count,
				WarningCount = 0,
				Summary = string.Format( CultureInfo.InvariantCulture,
					"pre-compile gate blocked: {0} error(s), 0 warning(s)", duplicates.Count ),
			};
		}
		var checkDoc = ToCheckDocument( input, options );
		var limits = new IssueLimits { MaxBrushes = ( options ?? new CompileOptions() ).MaxBrushes };
		var source = new IssueEngineIssueSource
		{
			Limits = limits,
			// Exact engine geometry: the solved face polygons, not bounds boxes.
			ExactEngineDocument = ToIssueDocument( input, limits ),
		};
		return FromReport( MapCheck.RunFullCheck( checkDoc, source ) );
	}

	/// <summary>One named error per duplicated brush / entity id in a compile input. Empty when ids are unique.</summary>
	static List<MapIssue> FindDuplicateIds( CompileInput input )
	{
		var issues = new List<MapIssue>();
		var seenBrushes = new Dictionary<int, string>();
		if ( input.Brushes != null )
		{
			foreach ( var brush in input.Brushes )
			{
				if ( brush is null ) continue;
				if ( seenBrushes.TryGetValue( brush.Id, out var first ) )
				{
					issues.Add( new MapIssue
					{
						Type = MapIssueType.DuplicateId,
						Severity = MapIssueSeverity.Error,
						Message = $"Duplicate brush id {brush.Id} (brushes '{first}' and '{brush.Name}').",
						FixHint = "Give each brush a unique id.",
						BrushId = brush.Id.ToString( CultureInfo.InvariantCulture ),
					} );
				}
				else
				{
					seenBrushes[brush.Id] = brush.Name ?? $"brush-{brush.Id}";
				}
			}
		}
		var seenEntities = new Dictionary<int, string>();
		if ( input.Entities != null )
		{
			foreach ( var entity in input.Entities )
			{
				if ( entity is null ) continue;
				if ( seenEntities.TryGetValue( entity.Id, out var first ) )
				{
					issues.Add( new MapIssue
					{
						Type = MapIssueType.DuplicateId,
						Severity = MapIssueSeverity.Error,
						Message = $"Duplicate entity id {entity.Id} (entities '{first}' and '{entity.ClassName}').",
						FixHint = "Give each entity a unique id.",
						EntityId = entity.Id.ToString( CultureInfo.InvariantCulture ),
					} );
				}
				else
				{
					seenEntities[entity.Id] = entity.ClassName ?? $"entity-{entity.Id}";
				}
			}
		}
		return issues;
	}

	static PreCompileGateResult FromReport( MapCheckReport report ) => new PreCompileGateResult
	{
		Ran = true,
		Overridden = false,
		Blocked = report.HasErrors,
		Issues = report.Issues,
		SkippedRules = report.SkippedRules,
		ErrorCount = report.Issues.Count( i => i.Severity == MapIssueSeverity.Error ),
		WarningCount = report.Issues.Count( i => i.Severity == MapIssueSeverity.Warning ),
		Summary = report.HasErrors
			? string.Format( CultureInfo.InvariantCulture, "pre-compile gate blocked: {0} error(s), {1} warning(s)",
				report.Issues.Count( i => i.Severity == MapIssueSeverity.Error ),
				report.Issues.Count( i => i.Severity == MapIssueSeverity.Warning ) )
			: report.Issues.Count == 0
				? "pre-compile gate passed: no issues"
				: string.Format( CultureInfo.InvariantCulture, "pre-compile gate passed with {0} warning(s)",
					report.Issues.Count( i => i.Severity == MapIssueSeverity.Warning ) ),
	};

	/// <summary>
	/// Project a compile input onto the check-document surface (bounds,
	/// materials, face counts, player start, entities). The engine part of
	/// the gate does NOT use this projection — see <see cref="ToIssueDocument"/>
	/// for the exact polygons. This document is what MapCheck's
	/// DefaultIssueSource rules (MissingTarget, BadKeyValue, Leak) run on;
	/// on the runner path there is no LeakGraph, so the leak verdict stays
	/// with the BSP stage — but entities ARE projected (a compile entity's
	/// TargetName is folded into Properties as "targetname", the key the
	/// Default rules read), so missing-target / duplicate-targetname /
	/// bad-keyvalue fire on the runner path too.
	/// </summary>
	public static InMemoryMapCheckDocument ToCheckDocument( CompileInput input, CompileOptions options = null )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		var doc = new InMemoryMapCheckDocument { Name = input.Name };
		if ( input.Brushes != null )
		{
			foreach ( var brush in input.Brushes )
			{
				// A null brush entry is the BSP stage's invalid-brush error,
				// not the gate's: skip it here and let the backstop catch it.
				if ( brush is null ) continue;
				var polygons = CompileRunner.BuildBrushPolygons( brush, out _ );
				var min = Vec3.Zero;
				var max = Vec3.Zero;
				if ( polygons.Count > 0 )
				{
					min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
					max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
					foreach ( var polygon in polygons )
					{
						foreach ( var v in polygon.Vertices )
						{
							min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
							max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
						}
					}
				}
				// Degenerate brush (no polygons): Zero/Zero bounds read as
				// zero-volume, so the gate flags it instead of passing it.
				doc.AddBrush( new CheckBrush
				{
					Id = brush.Id.ToString( CultureInfo.InvariantCulture ),
					Min = min,
					Max = max,
					Material = brush.Faces.Count > 0 ? brush.Faces[0].Material ?? "" : "",
					FaceCount = brush.Faces.Count,
				} );
			}
		}
		if ( input.PlayerStart.HasValue )
		{
			doc.HasPlayerStart = true;
			doc.PlayerStart = input.PlayerStart.Value;
		}
		if ( input.Entities != null )
		{
			foreach ( var entity in input.Entities )
			{
				if ( entity is null ) continue;
				var checkEntity = new CheckEntity
				{
					Id = entity.Id.ToString( CultureInfo.InvariantCulture ),
					ClassName = entity.ClassName ?? "",
					Origin = entity.Origin,
				};
				if ( entity.Keyvalues != null )
					foreach ( var pair in entity.Keyvalues )
						checkEntity.Properties[pair.Key] = pair.Value;
				if ( !string.IsNullOrEmpty( entity.TargetName ) )
					checkEntity.Properties["targetname"] = entity.TargetName;
				doc.AddEntity( checkEntity );
			}
		}
		return doc;
	}

	/// <summary>
	/// Project a compile input onto the IssueEngine's document with the
	/// REAL solved face polygons (exact geometry + per-face materials).
	/// A brush with no solvable polygons keeps its raw faces with empty
	/// vertex lists, so the engine still sees its face count and flags it
	/// (too few faces / zero area / zero volume) instead of seeing nothing.
	/// Entities are projected too (targetname folded into KeyValues, the
	/// key the engine's TargetName reads), so the entity rules run on the
	/// runner path as well.
	/// </summary>
	public static IssueDocument ToIssueDocument( CompileInput input, IssueLimits limits = null )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		var doc = new IssueDocument { Name = input.Name, Limits = limits ?? new IssueLimits() };
		if ( input.Brushes != null )
		{
			foreach ( var brush in input.Brushes )
			{
				if ( brush is null ) continue;
				var polygons = CompileRunner.BuildBrushPolygons( brush, out _ );
				var faces = new List<IssueFace>();
				if ( polygons.Count > 0 )
				{
					foreach ( var polygon in polygons )
						faces.Add( new IssueFace( polygon.Normal, polygon.Distance, polygon.Material, polygon.Vertices ) );
				}
				else
				{
					foreach ( var face in brush.Faces )
						faces.Add( new IssueFace( face.Normal, face.Distance, face.Material ?? "", Array.Empty<Vec3>() ) );
				}
				doc.Brushes.Add( new IssueBrush( brush.Id, faces, brush.IsDetail ) );
			}
		}
		if ( input.Entities != null )
		{
			foreach ( var entity in input.Entities )
			{
				if ( entity is null ) continue;
				var issueEntity = new IssueEntity( entity.Id, entity.ClassName ?? "", entity.Origin );
				if ( entity.Keyvalues != null )
					foreach ( var pair in entity.Keyvalues )
						issueEntity.KeyValues[pair.Key] = pair.Value;
				if ( !string.IsNullOrEmpty( entity.TargetName ) )
					issueEntity.KeyValues["targetname"] = entity.TargetName;
				doc.Entities.Add( issueEntity );
			}
		}
		return doc;
	}

	/// <summary>Gate issue code for a check issue type (kebab-case, matching the CompileIssue codes).</summary>
	public static string IssueCode( MapIssueType type ) => type switch
	{
		MapIssueType.InvalidBrush => "invalid-brush",
		MapIssueType.MissingTarget => "missing-target",
		MapIssueType.MissingMaterial => "missing-material",
		MapIssueType.BadKeyValue => "bad-keyvalue",
		MapIssueType.Leak => "leak",
		MapIssueType.EntityNoClass => "entity-no-class",
		MapIssueType.DuplicateTargetName => "duplicate-target-name",
		MapIssueType.LimitExceeded => "limit-exceeded",
		MapIssueType.GiantSkybox => "giant-skybox",
		MapIssueType.DuplicateId => "duplicate-id",
		_ => type.ToString().ToLowerInvariant(),
	};

	/// <summary>
	/// Map a gate issue onto the compile's issue shape. The subject is the
	/// brush NAME (the existing runner tests pin Subject == brush name for
	/// invalid-brush); the engine only carries the numeric id, so the name
	/// is resolved from the input here.
	/// </summary>
	public static CompileIssue ToCompileIssue( CompileInput input, MapIssue issue )
	{
		if ( issue is null ) throw new ArgumentNullException( nameof( issue ) );
		var subject = "map";
		if ( !string.IsNullOrEmpty( issue.BrushId ) )
		{
			subject = issue.BrushId;
			if ( input?.Brushes != null && int.TryParse( issue.BrushId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id ) )
			{
				var brush = input.Brushes.FirstOrDefault( b => b != null && b.Id == id );
				if ( brush != null && !string.IsNullOrEmpty( brush.Name ) ) subject = brush.Name;
			}
		}
		else if ( !string.IsNullOrEmpty( issue.EntityId ) )
		{
			subject = issue.EntityId;
		}
		return new CompileIssue(
			IssueCode( issue.Type ),
			issue.Severity == MapIssueSeverity.Error ? CompileSeverity.Error : CompileSeverity.Warning,
			issue.Message,
			subject,
			issue.Position,
			issue.FixHint ?? "",
			int.TryParse( issue.BrushId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var brushId ) ? brushId : (int?)null,
			int.TryParse( issue.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var entityId ) ? entityId : (int?)null );
	}

}

/// <summary>
/// The ~20-line adapter MapCheck's header prescribes for when IssueEngine
/// lands: run the REAL IssueEngine as MapCheck's issue source.
///
/// Composition (no rule runs twice, no issue reported twice):
///   * Engine (leak / unsealed-map / missing-target unregistered):
///     InvalidBrush (deep: planarity, volume, convexity, watertight),
///     MissingMaterial (per face, deduped to one per brush — the gate's
///     MapIssue shape has no face index, so per-face duplicates would be
///     indistinguishable), DuplicateTargetName, EntityNoClass,
///     LimitExceeded, GiantSkybox.
///   * MapCheck's DefaultIssueSource, single rules only: MissingTarget
///     (its MapIssue carries the Key + target name MapCheck.Fix needs to
///     create the stub — the engine's Issue has neither), MissingMaterial
///     (unknown-material judgements only — the engine flags empty faces,
///     deduped by brush, and never judges unknown ids), BadKeyValue
///     (the engine has no such rule), Leak — ONLY when the document
///     carries a real LeakGraph and a player start (a real LeakFinder
///     verdict). Otherwise the leak rules are skipped and the BSP stage
///     owns the leak verdict (see the PreCompileGate header).
/// Issue ids are CHK-#### in detection order: engine issues first (the
/// engine sorts them), then MissingTarget, MissingMaterial, BadKeyValue, Leak.
/// </summary>
public sealed class IssueEngineIssueSource : IIssueSource
{
	/// <summary>Compiler limits the engine checks. Defaults are the IssueEngine (Source published) limits.</summary>
	public IssueLimits Limits { get; set; } = new IssueLimits();

	/// <summary>
	/// Exact engine document (real face polygons) to run the engine on
	/// instead of projecting the check document's bounds boxes. The
	/// runner path sets this; the document path leaves it null.
	/// </summary>
	public IssueDocument ExactEngineDocument { get; set; }

	readonly List<string> _skippedRules = new List<string>();
	public IReadOnlyList<string> SkippedRules => _skippedRules;

	public IReadOnlyList<MapIssue> Run( IMapCheckDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		_skippedRules.Clear();
		var issues = new List<MapIssue>();
		issues.AddRange( RunEngine( ExactEngineDocument ?? Project( document ) ) );

		var defaults = new DefaultIssueSource();
		issues.AddRange( defaults.RunRule( document, "MissingTarget" ) );
		// MissingMaterial from BOTH sides, deduped by brush: the engine
		// flags empty faces (per face); DefaultIssueSource additionally
		// flags unknown materials when the document lists known materials
		// (the engine has no registry and never judges unknown ids).
		var engineMissingMaterial = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var issue in issues )
			if ( issue.Type == MapIssueType.MissingMaterial && !string.IsNullOrEmpty( issue.BrushId ) )
				engineMissingMaterial.Add( issue.BrushId );
		foreach ( var issue in defaults.RunRule( document, "MissingMaterial" ) )
			if ( !engineMissingMaterial.Contains( issue.BrushId ) )
				issues.Add( issue );
		issues.AddRange( defaults.RunRule( document, "BadKeyValue" ) );
		if ( document.LeakGraph != null && document.HasPlayerStart )
		{
			issues.AddRange( defaults.RunRule( document, "Leak" ) );
		}
		else
		{
			_skippedRules.Add( "leak" );
		}
		// The probe-based unsealed-map rule is always skipped: LeakFinder
		// (BSP stage, or the Leak rule above when a graph exists) owns the
		// unsealed verdict, and a probe stand-in must never fake one.
		_skippedRules.Add( "unsealed-map" );
		foreach ( var skipped in defaults.SkippedRules )
			if ( !_skippedRules.Contains( skipped ) ) _skippedRules.Add( skipped );

		for ( var i = 0; i < issues.Count; i++ )
			issues[i].Id = "CHK-" + ( i + 1 ).ToString( "D4", CultureInfo.InvariantCulture );
		return issues;
	}

	/// <summary>Run the engine on an IssueDocument and map its issues onto MapIssue.</summary>
	public IReadOnlyList<MapIssue> RunEngine( IssueDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var engine = new IssueEngine();
		// The BSP stage owns leaks (LeakFinder over the BSP portal graph +
		// .lin point file); the probe stand-in must not steal that verdict.
		engine.Unregister( LeakRule.RuleId );
		engine.Unregister( UnsealedMapRule.RuleId );
		// MissingTarget comes from DefaultIssueSource below (Key + target
		// name for the fix); the engine's version carries neither.
		engine.Unregister( MissingTargetRule.RuleId );
		var found = engine.Run( document );
		var mapped = new List<MapIssue>();
		var missingMaterialSeen = new HashSet<int>();
		foreach ( var issue in found )
		{
			if ( issue.Kind == IssueKind.MissingMaterial )
			{
				// One per brush: the gate's MapIssue has no face index, so
				// per-face duplicates would be indistinguishable noise.
				if ( !missingMaterialSeen.Add( issue.BrushId ) ) continue;
			}
			mapped.Add( MapIssue( issue, document ) );
		}
		return mapped;
	}

	static MapIssue MapIssue( Issue issue, IssueDocument document )
	{
		var type = issue.Kind switch
		{
			IssueKind.InvalidBrush => MapIssueType.InvalidBrush,
			IssueKind.MissingTarget => MapIssueType.MissingTarget,
			IssueKind.MissingMaterial => MapIssueType.MissingMaterial,
			IssueKind.EntityNoClass => MapIssueType.EntityNoClass,
			IssueKind.DuplicateTargetName => MapIssueType.DuplicateTargetName,
			IssueKind.LimitExceeded => MapIssueType.LimitExceeded,
			IssueKind.GiantSkybox => MapIssueType.GiantSkybox,
			IssueKind.Leak => MapIssueType.Leak,
			IssueKind.UnsealedMap => MapIssueType.Leak,
			_ => MapIssueType.InvalidBrush,
		};
		var brushId = "";
		if ( issue.BrushId >= 0 )
		{
			brushId = issue.BrushId.ToString( CultureInfo.InvariantCulture );
			if ( document.BrushIdByInt != null && document.BrushIdByInt.TryGetValue( issue.BrushId, out var original ) )
				brushId = original;
		}
		var entityId = "";
		if ( issue.EntityId >= 0 )
		{
			entityId = issue.EntityId.ToString( CultureInfo.InvariantCulture );
			if ( document.EntityIdByInt != null && document.EntityIdByInt.TryGetValue( issue.EntityId, out var original ) )
				entityId = original;
		}
		return new MapIssue
		{
			Type = type,
			Severity = issue.Severity == IssueSeverity.Error ? MapIssueSeverity.Error : MapIssueSeverity.Warning,
			Message = issue.Message,
			FixHint = issue.FixHint ?? "",
			BrushId = brushId,
			EntityId = entityId,
			// InvalidBrush / GiantSkybox: the offending value is the defect
			// list / the size ratio, which the message already carries.
			Detail = type is MapIssueType.InvalidBrush or MapIssueType.GiantSkybox ? issue.Message : "",
			Position = issue.HasLocation ? issue.Location : Vec3.Zero,
		};
	}

	/// <summary>
	/// Project a check document onto the engine's document. Each brush
	/// becomes its bounds box (6 outward-wound faces, the
	/// IssueEngineTests Box() winding); a brush with fewer than 4 faces
	/// keeps only that many faces so the engine still flags it. Detail
	/// state is not carried by CheckBrush and reads as non-detail (the
	/// gate's rules do not depend on it: limits count detail brushes too,
	/// and leak is unregistered here). Entities keep classname / origin /
	/// keyvalues. String ids that are not integers get sequential
	/// surrogate ids, remembered for the reverse mapping.
	/// </summary>
	IssueDocument Project( IMapCheckDocument document )
	{
		var projected = new IssueDocument { Name = document.Name ?? "", Limits = Limits ?? new IssueLimits() };
		var brushIds = new Dictionary<int, string>();
		var entityIds = new Dictionary<int, string>();
		var nextBrushId = 100000;
		var nextEntityId = 100000;
		foreach ( var brush in document.Brushes ?? Array.Empty<CheckBrush>() )
		{
			if ( brush is null ) continue;
			var id = IntId( brush.Id, brushIds, ref nextBrushId );
			projected.Brushes.Add( new IssueBrush( id, BoxFaces( brush ), isDetail: false ) );
		}
		foreach ( var entity in document.Entities ?? Array.Empty<CheckEntity>() )
		{
			if ( entity is null ) continue;
			var id = IntId( entity.Id, entityIds, ref nextEntityId );
			var projectedEntity = new IssueEntity( id, entity.ClassName ?? "", entity.Origin );
			if ( entity.Properties != null )
				foreach ( var pair in entity.Properties )
					projectedEntity.KeyValues[pair.Key] = pair.Value;
			projected.Entities.Add( projectedEntity );
		}
		projected.BrushIdByInt = brushIds;
		projected.EntityIdByInt = entityIds;
		return projected;
	}

	static int IntId( string id, Dictionary<int, string> byInt, ref int next )
	{
		if ( !string.IsNullOrEmpty( id ) && int.TryParse( id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed ) && !byInt.ContainsKey( parsed ) )
		{
			byInt[parsed] = id;
			return parsed;
		}
		while ( byInt.ContainsKey( next ) ) next++;
		var surrogate = next++;
		byInt[surrogate] = id ?? "";
		return surrogate;
	}

	/// <summary>Bounds box as 6 outward-wound faces (face order -X, +X, -Y, +Y, -Z, +Z).</summary>
	static List<IssueFace> BoxFaces( CheckBrush brush )
	{
		var x0 = brush.Min.x; var y0 = brush.Min.y; var z0 = brush.Min.z;
		var x1 = brush.Max.x; var y1 = brush.Max.y; var z1 = brush.Max.z;
		var material = brush.Material ?? "";
		var all = new (Vec3 Normal, Vec3[] Verts)[]
		{
			( new Vec3( -1, 0, 0 ), new[] { new Vec3( x0, y0, z0 ), new Vec3( x0, y0, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y1, z0 ) } ),
			( new Vec3( 1, 0, 0 ), new[] { new Vec3( x1, y1, z0 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y0, z0 ) } ),
			( new Vec3( 0, -1, 0 ), new[] { new Vec3( x1, y0, z0 ), new Vec3( x1, y0, z1 ), new Vec3( x0, y0, z1 ), new Vec3( x0, y0, z0 ) } ),
			( new Vec3( 0, 1, 0 ), new[] { new Vec3( x0, y1, z0 ), new Vec3( x0, y1, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y1, z0 ) } ),
			( new Vec3( 0, 0, -1 ), new[] { new Vec3( x0, y1, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x0, y0, z0 ) } ),
			( new Vec3( 0, 0, 1 ), new[] { new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ) } ),
		};
		var count = brush.FaceCount <= 0 ? 0 : Math.Min( brush.FaceCount, all.Length );
		var faces = new List<IssueFace>();
		for ( var i = 0; i < count; i++ )
			faces.Add( new IssueFace( all[i].Normal, Vec3.Dot( all[i].Normal, all[i].Verts[0] ), material, all[i].Verts ) );
		return faces;
	}
}
