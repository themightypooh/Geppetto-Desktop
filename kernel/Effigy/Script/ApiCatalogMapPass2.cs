// ApiCatalogMapPass2.cs — Hammer Desktop AI pass 2: ApiCatalog surface for the
// pass-2 map verbs (board task hammer-ai-pass2-tool-verbs).
//
// The pass-1 ApiCatalog (ApiCatalog.cs, NOT partial) owns MapVerbNames() and
// its backing table; the integrator merges these pass-2 names + rows into
// that table (see tests/fixtures/hammer/hammer-ai-pass2-tool-verbs/SEAM.md).
// This class is the pass-2 side of that merge: it exposes the names, the
// backing types, one-line descriptions, a markdown section for the catalog
// docs, and a backing-type check that runs ApiCatalog's own check 5
// (BackingTypeExists) over every row — no phantom entries.
//
// IMPLEMENTATION DONE, NOT RUNTIME VERIFIED (PO workflow override 2026-11-13).

using System;
using System.Collections.Generic;
using System.Text;

namespace Effigy;

public static class ApiCatalogMapPass2
{
	/// <summary>Pass-2 map verb names (same list as BuildScript.MapPass2VerbNames).</summary>
	public static IReadOnlyList<string> MapPass2VerbNames() => BuildScript.MapPass2VerbNames;

	/// <summary>Pass-2 verb -&gt; backing kernel type (fully qualified).</summary>
	public static IReadOnlyDictionary<string, string> MapPass2VerbBacking() => BuildScript.MapPass2VerbBacking();

	/// <summary>One-line description per pass-2 verb (catalog docs).</summary>
	public static IReadOnlyDictionary<string, string> MapPass2VerbDescriptions() => new Dictionary<string, string>( StringComparer.Ordinal )
	{
		["map_live_lighting"] = "Evaluate live lighting over the map's faces (LiveLighting.Evaluate). Read-only.",
		["map_skybox_preview"] = "Build the skybox preview scene from the map (SkyboxPreview.BuildPreview). Read-only.",
		["map_effects_preview"] = "Evaluate preview effects — fog from the map's env_fog_controller (PreviewEffects.EvaluateFog). Read-only.",
		["map_compile_preset"] = "Run a compile preset (quickplay/preview/final) over the map (CompileRunner.Run). Destructive when it writes output.",
		["map_compile_only_entities"] = "Rebuild the entity lump only, reusing a prior full build's artefacts (CompileRunner.RunOnlyEntities).",
		["map_check_problems"] = "Run the full map check over the map (MapCheck.RunFullCheck). Read-only.",
		["map_issue_autofix"] = "Apply a MapCheck fix to one issue and report IssueEngine fixability (MapCheck.Fix + IssueAutoFix.CanFix). Destructive.",
		["map_layer"] = "Layer ops on the VMF visgroup subset (define/list/rename/remove/setcolour). Full MapLayer semantics live in the MapDocument session.",
		["map_visgroup_ops"] = "Visgroup ops on the VMF visgroup subset (define/list/rename/remove/setcolour/assign). Pass-1 visgroup tool covers define/setVisible/assign/list.",
		["map_lock"] = "Evaluate texture locks on one face (FaceMaterial.WithLocks + MapTransform.ApplyTextureLock). Read-only evaluation; session application is the MCP tool.",
		["map_paste_special"] = "Paste brushes with copies/rotation/offset/pivot (MapPaste.PasteSpecial). Destructive.",
		["map_gizmo"] = "Evaluate a gizmo state: health check + snap math (GizmoModel.GizmoHealthCheck). Read-only.",
		["map_direct_edit"] = "Direct-edit one brush: extrude/drag/shear/scale (DirectEdit3D). Returns the edited geometry + validity.",
		["map_linked_group"] = "Linked-group ops: define/link/edit/edit-via-instance/unlink/list (LinkedGroupSet). Unlink is destructive.",
		["map_prop_place"] = "Seat a prop on the map surface: raycast + floor pose (PropPlacement.MoveToFloor).",
		["map_propper"] = "Convert brushes to a model + prop entity (BrushToModel.Convert). Destructive.",
		["map_hotspot"] = "Match/apply hotspot texture rects on one face (HotspotTexturing).",
		["map_browser"] = "Search the model/particle/sound asset registries (AssetRegistries). Texture has no registry — named capability error.",
		["map_hotload"] = "Watch/poll/reload watched assets (AssetHotload). Reload is destructive.",
		["map_playtest"] = "Playtest loop: bake / pre-launch validation / re-run command (PlaytestLoop). Pure/headless; launches nothing.",
		["map_navmesh_generate"] = "Generate the navmesh from the map's axis-aligned brushes (NavMeshGen.Generate). Read-only.",
	};

	/// <summary>Markdown table section for the catalog docs (same shape as the
	/// pass-1 ApiCatalog emitters: one row per verb, never hand-written in the
	/// docs themselves).</summary>
	public static string GenerateMarkdownSection()
	{
		var descriptions = MapPass2VerbDescriptions();
		var backing = MapPass2VerbBacking();
		var sb = new StringBuilder();
		sb.AppendLine( "## Pass-2 map verbs (hammer-ai-pass2-tool-verbs)" );
		sb.AppendLine();
		sb.AppendLine( "| Verb | Backing type | Description |" );
		sb.AppendLine( "| --- | --- | --- |" );
		foreach ( var verb in MapPass2VerbNames() )
			sb.AppendLine( $"| `{verb}` | `{backing[verb]}` | {descriptions[verb]} |" );
		return sb.ToString();
	}

	/// <summary>Run ApiCatalog's own backing-type check over every pass-2 row.
	/// Returns (verb, backing, exists) per row; Exists is false for a phantom
	/// entry. The integrator (or a test) fails the merge when any row is false.</summary>
	public static IReadOnlyList<(string Verb, string Backing, bool Exists)> VerifyBacking()
	{
		var rows = new List<(string, string, bool)>();
		foreach ( var pair in MapPass2VerbBacking() )
			rows.Add( (pair.Key, pair.Value, ApiCatalog.BackingTypeExists( pair.Value )) );
		return rows;
	}
}
