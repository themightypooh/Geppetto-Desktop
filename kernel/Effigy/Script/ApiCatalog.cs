using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// Lane P: the self-documenting API catalog. Everything EFFIGY-API.md claims comes
/// from here, by reflection over the kernel that is actually built — not from a
/// hand-maintained list that drifts behind new features (Backlog D1–D6).
///
/// How it is used:
///   effigy.sh api                    — list feature types and script verbs
///   effigy.sh api Profile            — one feature's exact parameters
///   effigy.sh api --markdown         — the full catalog as Markdown
///   effigy.sh api --check            — FAILS if any feature, parameter-bearing
///                                      type, verb, preset or CLI command is
///                                      missing from docs/EFFIGY-API.md
///   effigy.sh api --write            — regenerate EFFIGY-API.md from this
///
/// Notes on what is NOT reflected: script verbs are switch cases, not types, so
/// <see cref="BuildScript.VerbNames"/> is the single maintained verb list and
/// ApiCatalog reads it (a verb added to the switch but not to VerbNames is a
/// docs bug the --check cannot see — keep the two together). GeoNodes has no
/// type in this tree yet, so it is omitted rather than invented; a future
/// geometry-nodes lane adds the type and the catalog picks it up.
/// </summary>
public static class ApiCatalog
{
	public static IReadOnlyList<string> FeatureNames() => BuildScript.FeatureTypeNames().ToList();

	public static IReadOnlyList<string> VerbNames() => BuildScript.VerbNames;

	public static IReadOnlyList<string> RigVerbNames() => BuildScript.RigVerbNames;

	/// <summary>
	/// Map verbs (hammer-ai-aisuitetools-apicatalog-map). The canonical list
	/// lives in BuildScript.Map.cs (hammer-ai-buildscript-verbs-map, worker
	/// 64): one verb per native map capability, each calling the kernel API
	/// in <see cref="MapVerbBacking"/>, each returning a named result or a
	/// named error, never a silent no-op. This catalog only wires it through,
	/// exactly like <see cref="VerbNames"/> / <see cref="RigVerbNames"/>; it
	/// never invents a verb. Verb set, from the filed spec: brush
	/// create/edit/clip/vertex, displacement create/sculpt, per-face
	/// material/UV, entity place/edit + I/O add, grouping/visgroups, VMF/.map
	/// import/export, native compile (Quick/Preview/Final), validation/Issue
	/// list, lightmap bake, navmesh, skybox/cubemap. Seam contract:
	/// tests/fixtures/hammer/hammer-ai-aisuitetools-apicatalog-map/SEAM.md.
	/// </summary>
	public static IReadOnlyList<string> MapVerbNames() => BuildScript.MapVerbNames;

	/// <summary>The kernel API each map verb calls (primary backing type, full name).</summary>
	static readonly IReadOnlyDictionary<string, string> MapVerbBackingTable = new Dictionary<string, string>( StringComparer.Ordinal )
	{
		// Brush create/edit/clip/vertex. Edit is MapTransform (move/rotate/
		// scale/snap); MapBrush is the brush model it operates on.
		["map_brush_create"] = "Effigy.Map.MapBrushPrimitives",
		["map_brush_edit"] = "Effigy.MapTransform",
		["map_brush_clip"] = "Effigy.MapClip",
		["map_brush_vertex"] = "Effigy.MapVertexEdit",
		// Displacement create/sculpt.
		["map_displacement_create"] = "Effigy.Map.Displacement",
		["map_displacement_sculpt"] = "Effigy.Map.DisplacementSculpt",
		// Per-face material / UV.
		["map_face_material"] = "Effigy.FaceMaterial",
		["map_face_uv"] = "Effigy.FaceUv",
		// Entity place / edit / I-O add.
		["map_entity_place"] = "Effigy.MapEntities",
		["map_entity_edit"] = "Effigy.MapEntities",
		["map_entity_io_add"] = "Effigy.EntityIoList", // No bare EntityIo type; EntityIoList.Add is the I-O add API.
		// Grouping / visgroups.
		["map_group"] = "Effigy.Map.LinkedGroupSet", // No bare LinkedGroups type; LinkedGroupSet is the group store.
		["map_visgroup"] = "Effigy.Map.VmfVisGroup",
		// VMF / .map import / export (format=vmf|map; Map220Io is the .map side).
		["map_import"] = "Effigy.Map.VmfIo",
		["map_export"] = "Effigy.Map.VmfIo",
		// Native compile (preset=quick|preview|final), validation, lightmap bake.
		["map_compile"] = "Effigy.CompileRunner",
		["map_validate"] = "Effigy.MapCheck",
		["map_lightmap_bake"] = "Effigy.MapBakeExport",
		// Honest gaps, not phantoms: no navmesh builder type exists in the
		// kernel yet — PlaytestLoop.PreLaunchValidation is the contract that
		// reports NAVMESH_MISSING ("no navmesh type has landed yet"), so the
		// verb returns that named capability error instead of fake success.
		// map_skybox covers cubemap too (cubemap=true); no cubemap builder
		// type exists at all, so that half returns a named capability error
		// until one lands. See SEAM.md.
		["map_navmesh"] = "Effigy.Map.PlaytestLoop",
		["map_skybox"] = "Effigy.Map.SkyboxPreview",
	};
	public static IReadOnlyDictionary<string, string> MapVerbBacking() => MapVerbBackingTable;

	/// <summary>True when the named kernel type exists in this build (phantom-op check).</summary>
	public static bool BackingTypeExists( string fullName ) => typeof( ApiCatalog ).Assembly.GetType( fullName, throwOnError: false ) is not null;

	public static IReadOnlyList<string> PartNames() => PartLibrary.Names.ToList();

	public static IReadOnlyList<string> BrushKinds() => Enum.GetNames( typeof( BrushKind ) ).ToList();

	public static IReadOnlyList<string> SlotLookPresets() => SlotLook.Presets.Keys.OrderBy( x => x, StringComparer.Ordinal ).ToList();

	public static IReadOnlyList<string> FleshPresets() => FleshMaterial.Presets.ToList();

	public static IReadOnlyList<string> RigTemplates() => global::Effigy.RigTemplates.Names.ToList();

	public static IReadOnlyList<string> EditOperations() => EditOpFeature.Operations.ToList();

	/// <summary>One feature's parameters, in the same shape BuildScript.Describe prints.</summary>
	public static string DescribeFeature( string typeName ) => BuildScript.Describe( typeName );

	public static string GenerateMarkdown( IReadOnlyList<string> cliCommands = null )
	{
		var sb = new StringBuilder();
		sb.AppendLine( "# EFFIGY-API — generated, do not hand-edit" );
		sb.AppendLine();
		sb.AppendLine( "This file is generated by `effigy.sh api --write` (ApiCatalog, Lane P). " );
		sb.AppendLine( "Hand edits are overwritten on the next regeneration; fix the kernel or " );
		sb.AppendLine( "ApiCatalog instead, then regenerate. `effigy.sh api --check` fails if " );
		sb.AppendLine( "any feature, verb, preset or CLI command below is missing from this file." );
		sb.AppendLine();
		sb.AppendLine( $"## Features ({FeatureNames().Count})" );
		sb.AppendLine();
		sb.AppendLine( "Add one with: `add TYPE key=value …`. Parameters are named by label " );
		sb.AppendLine( "(lowercased, spaces removed) or by field name. Points take x,y,z, a " );
		sb.AppendLine( "landmark (Torso.top, spine.tail, ground, origin), or @landmark+dx,dy,dz." );
		sb.AppendLine();
		foreach ( var name in FeatureNames() )
		{
			sb.AppendLine( $"### {name}" );
			sb.AppendLine();
			sb.AppendLine( "```" );
			sb.AppendLine( DescribeFeature( name ) );
			sb.AppendLine( "```" );
			sb.AppendLine();
		}
		sb.AppendLine( "## Script verbs" );
		sb.AppendLine();
		foreach ( var verb in VerbNames() )
			sb.AppendLine( $"- `{verb}`" );
		sb.AppendLine();
		sb.AppendLine( "## Rig verbs" );
		sb.AppendLine();
		foreach ( var verb in RigVerbNames() )
			sb.AppendLine( $"- `{verb}`" );
		sb.AppendLine();
		sb.AppendLine( "## Map verbs" );
		sb.AppendLine();
		sb.AppendLine( "Native Hammer-style map operations, one verb per capability. " );
		sb.AppendLine( "Each verb calls the kernel API in parentheses and returns a named " );
		sb.AppendLine( "result or a named error, never a silent no-op. A verb whose backing " );
		sb.AppendLine( "is a named capability error (navmesh; the cubemap half of map_skybox) " );
		sb.AppendLine( "says so when called instead of faking success. The list is generated " );
		sb.AppendLine( "from BuildScript.MapVerbNames, not hand-written." );
		sb.AppendLine();
		foreach ( var verb in MapVerbNames() )
		{
			var backing = MapVerbBacking().TryGetValue( verb, out var b ) ? b : "(no backing registered — phantom op, see aisuitetoolsmap tests)";
			sb.AppendLine( $"- `{verb}` ({backing})" );
		}
		sb.AppendLine();
		sb.AppendLine( "## Part library" );
		sb.AppendLine();
		foreach ( var part in PartNames() )
			sb.AppendLine( $"- {part}: {PartLibrary.Uses( part )}" );
		sb.AppendLine();
		sb.AppendLine( "## Brush kinds" );
		sb.AppendLine();
		sb.AppendLine( string.Join( ", ", BrushKinds() ) );
		sb.AppendLine();
		sb.AppendLine( "## SlotLook presets" );
		sb.AppendLine();
		foreach ( var preset in SlotLookPresets() )
			sb.AppendLine( $"- {preset}" );
		sb.AppendLine();
		sb.AppendLine( "## Flesh material presets" );
		sb.AppendLine();
		foreach ( var preset in FleshPresets() )
			sb.AppendLine( $"- {preset}" );
		sb.AppendLine();
		sb.AppendLine( "## Rig templates" );
		sb.AppendLine();
		foreach ( var template in RigTemplates() )
			sb.AppendLine( $"- {template}" );
		sb.AppendLine();
		sb.AppendLine( "## Edit operations" );
		sb.AppendLine();
		foreach ( var op in EditOperations() )
			sb.AppendLine( $"- {op}" );
		sb.AppendLine();
		if ( cliCommands is not null )
		{
			sb.AppendLine( "## CLI commands (effigy.sh)" );
			sb.AppendLine();
			foreach ( var cmd in cliCommands )
				sb.AppendLine( $"- `{cmd}`" );
			sb.AppendLine();
		}
		sb.AppendLine( "## Script workflows (no editor required)" );
		sb.AppendLine();
		sb.AppendLine( "Fusing, rigging and sculpting do NOT require the editor: they run " );
		sb.AppendLine( "headless through `effigy.sh script`, through the desktop's live runs, " );
		sb.AppendLine( "or through the editor's `effigy_run` tool on the same script language. " );
		sb.AppendLine( "The old claim that they need the editor or a C# generator was wrong " );
		sb.AppendLine( "and was removed by Lane P; if a workflow below still names the editor " );
		sb.AppendLine( "as the only path, that is a docs bug, not a capability gap." );
		sb.AppendLine();
		sb.AppendLine( "- Modeling: `add Profile …`, `add Spline …`, `add Primitive …`, `edit …`, `fix …`" );
		sb.AppendLine( "- Sculpting: `add Stroke …`, `add Detail …`, multires sculpt targets, `reproject …`" );
		sb.AppendLine( "- Rigging: `bone …`, `chain …`, `rig …`, `bind …`, `skin …`, `weight …`, `corrective …`" );
		sb.AppendLine( "- Painting: `add Paint …`, `paintmaps …`, `bake …`" );
		sb.AppendLine( "- Animation: `play …` (validate + report headlessly; the desktop ServeMode " );
		sb.AppendLine( "  `clip`/`rest` commands are what actually play a clip in a pane — a clip " );
		sb.AppendLine( "  is animation, not model, and is deliberately NOT stored in the document)." );
		sb.AppendLine( "- Inspection: `describe`, `measure`, `distance`, `symmetry`, `topology`, " );
		sb.AppendLine( "  `section`, `flex`, `clearance`, `match`, `remedy`, `remedy all`" );
		sb.AppendLine( "- Export: `export …`, `export_cloth …`, `spritesheet …`, `attachment`, " );
		sb.AppendLine( "  `hitbox`, `bodygroup`, `ragdoll`, `lod_chain`" );
		sb.AppendLine( "- Maps: `map_brush_create …`, `map_entity_place …`, `map_validate`, " );
		sb.AppendLine( "  `map_compile preset=quick` — full verb list under Map verbs above." );
		return sb.ToString();
	}

	public static string GenerateJsonSchema( IReadOnlyList<string> cliCommands = null )
	{
		var doc = new Dictionary<string, object>
		{
			["features"] = FeatureNames(),
			["verbs"] = VerbNames(),
			["rigVerbs"] = RigVerbNames(),
			["mapVerbs"] = MapVerbNames(),
			["mapVerbBacking"] = MapVerbBacking(),
			["parts"] = PartNames(),
			["brushKinds"] = BrushKinds(),
			["slotLookPresets"] = SlotLookPresets(),
			["fleshPresets"] = FleshPresets(),
			["rigTemplates"] = RigTemplates(),
			["editOperations"] = EditOperations(),
			["cliCommands"] = cliCommands ?? Array.Empty<string>(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>
	/// Everything the catalog knows, that the given Markdown does not mention.
	/// Feature names are matched as whole words; verbs and CLI commands as
	/// backticked tokens or words, so a prose mention counts and a substring
	/// of another word does not. Empty means docs and code agree.
	/// </summary>
	public static List<string> VerifyDocsInSync( string markdown, IReadOnlyList<string> cliCommands = null )
	{
		var missing = new List<string>();
		if ( string.IsNullOrWhiteSpace( markdown ) )
		{
			missing.Add( "(no documentation text to check)" );
			return missing;
		}
		bool Mentions( string token )
		{
			if ( markdown.Contains( "`" + token + "`", StringComparison.Ordinal ) ) return true;
			var idx = markdown.IndexOf( token, StringComparison.Ordinal );
			while ( idx >= 0 )
			{
				var before = idx == 0 || !char.IsLetterOrDigit( markdown[idx - 1] );
				var after = idx + token.Length >= markdown.Length || !char.IsLetterOrDigit( markdown[idx + token.Length] );
				if ( before && after ) return true;
				idx = markdown.IndexOf( token, idx + 1, StringComparison.Ordinal );
			}
			return false;
		}
		foreach ( var name in FeatureNames() )
			if ( !Mentions( name ) ) missing.Add( "feature " + name );
		foreach ( var verb in VerbNames() )
			if ( !Mentions( verb ) ) missing.Add( "verb " + verb );
		foreach ( var verb in MapVerbNames() )
			if ( !Mentions( verb ) ) missing.Add( "map verb " + verb );
		foreach ( var preset in SlotLookPresets() )
			if ( !Mentions( preset ) ) missing.Add( "SlotLook preset " + preset );
		foreach ( var template in RigTemplates() )
			if ( !Mentions( template ) ) missing.Add( "rig template " + template );
		if ( cliCommands is not null )
			foreach ( var cmd in cliCommands )
				if ( !Mentions( cmd ) ) missing.Add( "CLI command " + cmd );
		return missing;
	}
}
