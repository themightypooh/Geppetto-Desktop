// McpMapPass2Tools.cs — Hammer Desktop AI pass 2: MCP tools for every pass-2
// map operation (board task hammer-ai-pass2-tool-verbs).
//
// Extends the pass-1 AI surfaces (same ones as pass 1 — BuildScript verbs,
// OpsCli commands, MCP tools/resources, AiSuiteTools/ApiCatalog entries) with
// MCP tools for every pass-2 operation. NATIVE Geppetto only: this file wraps
// the pass-1 McpMapTools session and calls the landed kernel APIs through
// BuildScript.RunMapPass2Verb; it never shells out, never invents a stub API,
// never creates a second MCP surface (same McpModelingTools.McpToolCallResult,
// same tools/list shape), and never touches UI.
//
// SEAM (integrator-owned, see tests/fixtures/hammer/hammer-ai-pass2-tool-verbs/SEAM.md):
//   - McpServer appends ListTools() to tools/list and dispatches map_* to
//     CallAsync (same CallMapTool path as the pass-1 tools).
//   - McpSafety.MapTools gains these 21 names; the destructive rules gain
//     RequiresConfirm (this file) — the reference implementation.
//   - The pass-2 stores that live here (layers, visgroup overlay, linked
//     groups, edited-brush overlay, compile artefacts, registries, hotload)
//     move INTO McpMapTools.cs at merge time, where the session privates are
//     reachable; this file keeps them separate so it never edits the shared
//     McpMapTools.cs. Until then, the session VMF bridge (export -> kernel
//     verb -> result) is how read verbs see live session state.
//   - Tests: tests/HammerAiPass2ToolVerbsTests.cs, group "hammerpass2ai"
//     (NOT registered in TestRegistry.cs — integrator adds one line).
//
// Safety (same gates as hammer-ai-safety-gates-map):
//   - No arbitrary shell: any command/shell/exec argument is refused with a
//     named shell-refused error; compile runs CompileRunner only.
//   - Destructive ops require explicit confirm=true (see RequiresConfirm):
//     final-preset compile, only-entities, autofix apply, unlink, paste
//     special, propper convert, hotload reload, layer/visgroup remove.
//   - Outputs carry no secrets (paths are fine, credentials/tokens never);
//     the server scrubs outputs through McpSafety.ScrubMapOutput, same as
//     the pass-1 tools.
//   - Every call is logged by the server with map + operation (same path).
//
// IMPLEMENTATION DONE, NOT RUNTIME VERIFIED (PO workflow override 2026-11-13):
// implemented + targeted checks only, no full project build or app launch.
// Full build + named test-group run are deferred to the final integration gate.

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy;
using MapKit = Effigy.Map;

namespace Marionette.Desktop;

public sealed class McpMapPass2Tools
{
	// ---- tool names (verb name = MCP tool name) ----

	public const string ToolLiveLighting = "map_live_lighting";
	public const string ToolSkyboxPreview = "map_skybox_preview";
	public const string ToolEffectsPreview = "map_effects_preview";
	public const string ToolCompilePreset = "map_compile_preset";
	public const string ToolCompileOnlyEntities = "map_compile_only_entities";
	public const string ToolCheckProblems = "map_check_problems";
	public const string ToolIssueAutofix = "map_issue_autofix";
	public const string ToolLayer = "map_layer";
	public const string ToolVisGroupOps = "map_visgroup_ops";
	public const string ToolLock = "map_lock";
	public const string ToolPasteSpecial = "map_paste_special";
	public const string ToolGizmo = "map_gizmo";
	public const string ToolDirectEdit = "map_direct_edit";
	public const string ToolLinkedGroup = "map_linked_group";
	public const string ToolPropPlace = "map_prop_place";
	public const string ToolPropper = "map_propper";
	public const string ToolHotspot = "map_hotspot";
	public const string ToolBrowser = "map_browser";
	public const string ToolHotload = "map_hotload";
	public const string ToolPlaytest = "map_playtest";
	public const string ToolNavmeshGenerate = "map_navmesh_generate";

	public static readonly IReadOnlyList<string> ToolNames = new[]
	{
		ToolLiveLighting, ToolSkyboxPreview, ToolEffectsPreview,
		ToolCompilePreset, ToolCompileOnlyEntities, ToolCheckProblems, ToolIssueAutofix,
		ToolLayer, ToolVisGroupOps, ToolLock, ToolPasteSpecial, ToolGizmo, ToolDirectEdit,
		ToolLinkedGroup, ToolPropPlace, ToolPropper, ToolHotspot, ToolBrowser, ToolHotload,
		ToolPlaytest, ToolNavmeshGenerate,
	};

	/// <summary>Tools that CAN be destructive (the per-call rule is RequiresConfirm).</summary>
	public static readonly IReadOnlyList<string> DestructiveToolNames = new[]
	{
		ToolCompilePreset, ToolCompileOnlyEntities, ToolIssueAutofix, ToolLinkedGroup,
		ToolPasteSpecial, ToolPropper, ToolHotload, ToolLayer, ToolVisGroupOps,
	};

	/// <summary>Names the integrator adds to McpSafety.MapTools (same set as ToolNames).</summary>
	public static readonly IReadOnlyList<string> SafetyGateTools = ToolNames;

	/// <summary>BuildScript verb for a pass-2 tool (identity: verb name = tool name).</summary>
	public static string BuildScriptVerbFor( string tool ) => ToolNames.Contains( tool )
		? tool
		: throw new ArgumentException( $"Unknown pass-2 map tool '{tool}'.", nameof( tool ) );

	/// <summary>Reference implementation for the McpSafety extension: whether
	/// this call needs explicit confirm=true. Mirrors the safety-gate rules:
	/// final-preset compile, only-entities, autofix apply, unlink, paste
	/// special, propper convert, hotload reload, layer/visgroup remove.</summary>
	public static bool RequiresConfirm( string tool, JsonObject? args )
	{
		string Op() => args?["op"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
		bool Confirm() => args?["confirm"] is JsonValue v && v.TryGetValue<bool>( out var b ) && b;
		if ( Confirm() ) return false;
		return tool switch
		{
			ToolCompilePreset => string.Equals( args?["preset"]?.GetValue<string>(), "final", StringComparison.OrdinalIgnoreCase ),
			ToolCompileOnlyEntities => true,
			ToolIssueAutofix => Op() == "fix",
			ToolLinkedGroup => Op() == "unlink",
			ToolPasteSpecial => true,
			ToolPropper => true,
			ToolHotload => Op() == "reload",
			ToolLayer => Op() == "remove",
			ToolVisGroupOps => Op() == "remove",
			_ => false,
		};
	}

	// ---- definitions ----

	/// <summary>The tool definitions ListTools() serves.</summary>
	public static IReadOnlyList<JsonObject> Definitions { get; } = BuildDefinitions();

	/// <summary>tools/list: the 21 pass-2 map_* tools (same shape as the pass-1 list).</summary>
	public JsonArray ListTools()
	{
		var arr = new JsonArray();
		foreach ( var def in Definitions ) arr.Add( def.DeepClone() );
		return arr;
	}

	static IReadOnlyList<JsonObject> BuildDefinitions() => new[]
	{
		Tool( ToolLiveLighting, "Evaluate live lighting over the map's faces. Read-only.",
			new JsonObject { ["quality"] = Str( "draft or full (default draft)" ) } ),
		Tool( ToolSkyboxPreview, "Build the skybox preview scene from the map. Read-only.",
			new JsonObject() ),
		Tool( ToolEffectsPreview, "Evaluate preview effects: fog from the map's env_fog_controller (op=fog), or list the evaluators (op=list). Read-only.",
			new JsonObject
			{
				["op"] = Str( "fog or list (default fog)" ),
				["fogstart"] = Num( "Fog start distance (overrides the map controller)" ),
				["fogend"] = Num( "Fog end distance (overrides the map controller)" ),
				["fogmaxdensity"] = Num( "Fog density at/after fogend, 0..1" ),
				["fogcolor"] = Vec3Prop( "Fog colour as [r, g, b] 0..255" ),
				["camera"] = Vec3Prop( "Camera position [x, y, z] (default player start)" ),
			} ),
		Tool( ToolCompilePreset, "Run a compile preset (quickplay/preview/final) over the map. Final preset requires confirm=true.",
			new JsonObject
			{
				["preset"] = Str( "quickplay, preview or final (default quickplay)" ),
				["outputFolder"] = Str( "Output folder, inside the work directory (default work directory)" ),
				["lightmapResolution"] = Int( "Lightmap resolution override, 1..64 (0 = preset default)" ),
				["visibility"] = Str( "Visibility override: none, fast or full" ),
				["maxBrushes"] = Int( "Maximum brushes (default 16384)" ),
				["maxPlanesPerBrush"] = Int( "Maximum planes per brush (default 128)" ),
				["confirm"] = Bool( "Required when preset=final" ),
			} ),
		Tool( ToolCompileOnlyEntities, "Rebuild the entity lump only, reusing the prior full build's artefacts. Requires confirm=true. Runs a base preset first when no artefacts are held.",
			new JsonObject
			{
				["preset"] = Str( "Base preset for the first build: quickplay, preview or final (default quickplay)" ),
				["outputFolder"] = Str( "Output folder, inside the work directory (default work directory)" ),
				["confirm"] = Bool( "Required" ),
			} ),
		Tool( ToolCheckProblems, "Run the full map check over the map. Read-only.",
			new JsonObject() ),
		Tool( ToolIssueAutofix, "List check issues with IssueEngine fixability (op=list), or apply a MapCheck fix to one issue (op=fix, requires confirm=true).",
			new JsonObject
			{
				["op"] = Str( "list or fix (default list)" ),
				["issue"] = Str( "Issue id to fix (op=fix)" ),
				["confirm"] = Bool( "Required for op=fix" ),
			} ),
		Tool( ToolLayer, "Layer ops. The session store is the MapLayer model; the file verbs cover the VMF visgroup subset.",
			new JsonObject
			{
				["op"] = Str( "list, define, setvisible, setcolour, setcompilevisible, rename or remove" ),
				["name"] = Str( "Layer name" ),
				["colour"] = Str( "Colour as #RRGGBB" ),
				["visible"] = Bool( "Visible flag" ),
				["compileVisible"] = Bool( "Compile-visible flag" ),
				["newName"] = Str( "New name (op=rename)" ),
				["confirm"] = Bool( "Required for op=remove" ),
			}, "op" ),
		Tool( ToolVisGroupOps, "Visgroup ops beyond the pass-1 visgroup tool: rename, setcolour, setcompilevisible, remove. define/setvisible/assign/list delegate to the pass-1 tool.",
			new JsonObject
			{
				["op"] = Str( "list, define, setvisible, assign, rename, setcolour, setcompilevisible or remove" ),
				["name"] = Str( "Visgroup name" ),
				["colour"] = Str( "Colour as #RRGGBB (define/setcolour)" ),
				["color"] = Str( "Colour as 'r g b' (define, pass-1 form)" ),
				["brushIds"] = Arr( "Brush ids to assign (op=assign)" ),
				["visible"] = Bool( "Visible flag (op=setvisible)" ),
				["compileVisible"] = Bool( "Compile-visible flag (op=setcompilevisible)" ),
				["newName"] = Str( "New name (op=rename)" ),
				["confirm"] = Bool( "Required for op=remove" ),
			}, "op" ),
		Tool( ToolLock, "Evaluate texture locks on one face. Read-only evaluation; locks are FaceMaterial state, applied to the live session face by the integrator merge.",
			new JsonObject
			{
				["brush"] = Str( "Session brush id from map_brush_create (brush-1, ...)" ),
				["face"] = Int( "Face index (default 0)" ),
				["locks"] = Str( "none, scale, uv or both (default uv)" ),
				["translate"] = Vec3Prop( "Optional translation [x, y, z] to verify the lock numerically" ),
				["rotate"] = Num( "Optional rotation degrees to verify the lock numerically" ),
				["axis"] = Str( "Rotation axis: x, y or z (default z)" ),
			}, "brush" ),
		Tool( ToolPasteSpecial, "Paste brushes with copies/rotation/offset/pivot. Requires confirm=true.",
			new JsonObject
			{
				["brushes"] = Arr( "Session brush ids (brush-1, ...) to paste" ),
				["copies"] = Int( "Copies, 1..1024 (default 1)" ),
				["rotation"] = Num( "Rotation degrees (default 0)" ),
				["axis"] = Str( "Rotation axis: x, y or z (default z)" ),
				["offset"] = Vec3Prop( "Offset [x, y, z]" ),
				["pivot"] = Vec3Prop( "Pivot [x, y, z] (default selection centre)" ),
				["grid"] = Num( "Grid size for snapping" ),
				["anglesnap"] = Num( "Angle snap degrees" ),
				["texturelock"] = Bool( "Keep textures locked (default true)" ),
				["confirm"] = Bool( "Required" ),
			}, "brushes" ),
		Tool( ToolGizmo, "Evaluate a gizmo state: health check + snap math. Read-only.",
			new JsonObject
			{
				["mode"] = Str( "translate, rotate or scale (default translate)" ),
				["space"] = Str( "global or local (default global)" ),
				["axis"] = Str( "none, x, y, z, xy, yz or xz (default none)" ),
				["pivot"] = Vec3Prop( "Pivot [x, y, z]" ),
				["snap"] = Bool( "Snap enabled (default false)" ),
				["grid"] = Num( "Grid size" ),
				["anglesnap"] = Num( "Angle snap degrees" ),
				["point"] = Vec3Prop( "Point [x, y, z] to snap" ),
				["angle"] = Num( "Angle degrees to snap" ),
			} ),
		Tool( ToolDirectEdit, "Direct-edit one brush: extrude/drag/shear/scale. Returns the edited geometry + validity; the edited brush is kept in the session overlay.",
			new JsonObject
			{
				["brush"] = Str( "Session brush id from map_brush_create (brush-1, ...)" ),
				["op"] = Str( "extrude, dragface, dragfacealongnormal, dragedge, dragvertex, dragvertexby, shear or scale" ),
				["face"] = Int( "Face index" ),
				["distance"] = Num( "Extrude/drag distance" ),
				["delta"] = Vec3Prop( "Drag delta [x, y, z]" ),
				["vertex"] = Int( "Vertex index" ),
				["a"] = Int( "First edge vertex (op=dragedge)" ),
				["b"] = Int( "Second edge vertex (op=dragedge)" ),
				["to"] = Vec3Prop( "Drag target [x, y, z] (op=dragvertex)" ),
				["pivot"] = Vec3Prop( "Shear/scale pivot [x, y, z]" ),
				["axis"] = Vec3Prop( "Shear axis [x, y, z]" ),
				["direction"] = Vec3Prop( "Shear direction [x, y, z]" ),
				["factor"] = Num( "Shear factor" ),
				["factors"] = Vec3Prop( "Scale factors [x, y, z]" ),
			}, "brush", "op" ),
		Tool( ToolLinkedGroup, "Linked-group ops on this tool's group set: define/link/edit/editviainstance/unlink/list/tojson. Unlink requires confirm=true.",
			new JsonObject
			{
				["op"] = Str( "list, tojson, define, link, edit, editviainstance or unlink" ),
				["state"] = Str( "LinkedGroupSet JSON from a previous call (chains state across calls)" ),
				["name"] = Str( "Definition name (op=define)" ),
				["brushIds"] = Arr( "Session brush ids (brush-1, ...) for the definition" ),
				["entityIds"] = Arr( "Entity ids for the definition (from the map)" ),
				["members"] = Arr( "Explicit members [{kind, name, material, origin, vertices}] (op=define, no map)" ),
				["definition"] = Str( "Definition id" ),
				["instance"] = Str( "Instance id" ),
				["member"] = Str( "Member id" ),
				["offset"] = Vec3Prop( "Link offset [x, y, z]" ),
				["layer"] = Str( "Layer name" ),
				["visgroup"] = Str( "Visgroup name" ),
				["edit"] = Str( "move, setmaterial, setkeyvalue or rename" ),
				["delta"] = Vec3Prop( "Move delta [x, y, z]" ),
				["material"] = Str( "Material (edit=setmaterial)" ),
				["key"] = Str( "Key (edit=setkeyvalue)" ),
				["value"] = Str( "Value (edit=setkeyvalue)" ),
				["newname"] = Str( "New name (edit=rename)" ),
				["confirm"] = Bool( "Required for op=unlink" ),
			}, "op" ),
		Tool( ToolPropPlace, "Seat a prop on the map surface: raycast down + floor pose. seat=true places the prop entity in the session.",
			new JsonObject
			{
				["class"] = Str( "Entity class (default prop_static)" ),
				["model"] = Str( "Model path (.mdl/.vmdl/.glb/.obj/.fbx/.effigy, no whitespace)" ),
				["origin"] = Vec3Prop( "Pose origin [x, y, z]" ),
				["dropfrom"] = Vec3Prop( "Raycast start [x, y, z] (raycasts down to the floor)" ),
				["yaw"] = Num( "Yaw degrees (default 0)" ),
				["roll"] = Num( "Roll degrees (default 0)" ),
				["maxdistance"] = Num( "Raycast max distance (default 4096)" ),
				["seat"] = Bool( "Place the prop entity in the session (default false)" ),
			} ),
		Tool( ToolPropper, "Convert brushes to a model + prop entity (BrushToModel.Convert). Requires confirm=true.",
			new JsonObject
			{
				["brushes"] = Arr( "Session brush ids (brush-1, ...) to convert" ),
				["modelName"] = Str( "Registry key / prop model name (default prop)" ),
				["scale"] = Vec3Prop( "Non-uniform scale [x, y, z]" ),
				["density"] = Num( "Mass density kg per cubic inch (default 0.01)" ),
				["bakeAo"] = Bool( "Bake AO (default true)" ),
				["aoSize"] = Int( "AO map size (default 64)" ),
				["aoRays"] = Int( "AO rays per texel (default 8)" ),
				["buildPhysics"] = Bool( "Build physics hulls (default true)" ),
				["confirm"] = Bool( "Required" ),
			}, "brushes" ),
		Tool( ToolHotspot, "Match/apply hotspot texture rects on one face.",
			new JsonObject
			{
				["op"] = Str( "match, apply or applynamed (default match)" ),
				["brush"] = Str( "Session brush id from map_brush_create (brush-1, ...)" ),
				["face"] = Int( "Face index (default 0)" ),
				["hotspots"] = Arr( "Hotspot rects [{name, materialName, uMin, vMin, uMax, vMax}]" ),
				["rect"] = Str( "Rect name (op=apply)" ),
				["hotspot"] = Str( "Hotspot name (op=applynamed)" ),
				["rotated"] = Bool( "Rect fits rotated 90 degrees (op=apply, default false)" ),
			}, "brush", "hotspots" ),
		Tool( ToolBrowser, "Search the model/particle/sound asset registries. Texture has no registry — named capability error, never fake results.",
			new JsonObject
			{
				["kind"] = Str( "model, particle, sound or texture" ),
				["query"] = Str( "Search text" ),
				["entries"] = Arr( "Assets to register first [{name, path, ...}]" ),
				["limit"] = Int( "Max results (default 20)" ),
			}, "kind", "query" ),
		Tool( ToolHotload, "Watch/poll/reload watched assets. Watches persist across calls on this tool. Reload requires confirm=true.",
			new JsonObject
			{
				["op"] = Str( "watch, list, poll or reload" ),
				["root"] = Str( "Project root (default work directory)" ),
				["watches"] = Arr( "Watches to register [{kind, path}] (kind: model, material, sound, particle)" ),
				["confirm"] = Bool( "Required for op=reload" ),
			}, "op" ),
		Tool( ToolPlaytest, "Playtest loop: bake / pre-launch validation / re-run command. Pure/headless; launches nothing.",
			new JsonObject
			{
				["op"] = Str( "bake, validate or rerun (default validate)" ),
				["map"] = Str( "Map path as the game addresses it (default maps/map.bsp)" ),
				["navmesh"] = Bool( "Navmesh present (default false)" ),
				["sessionRunning"] = Bool( "A play session is running (default false)" ),
			} ),
		Tool( ToolNavmeshGenerate, "Generate the navmesh from the map's axis-aligned brushes. Read-only. Supersedes the pass-1 map_navmesh capability error.",
			new JsonObject
			{
				["cellSize"] = Num( "Voxel cell size in inches (default 8)" ),
				["agentRadius"] = Num( "Agent collision radius in inches (default 16)" ),
				["navtext"] = Bool( "Include the .nav text (default false)" ),
			} ),
	};

	static JsonObject Tool( string name, string description, JsonObject properties, params string[] required ) => new()
	{
		["name"] = name,
		["description"] = description,
		["inputSchema"] = new JsonObject
		{
			["type"] = "object",
			["properties"] = properties,
			["required"] = new JsonArray( required.Select( r => (JsonNode?)r ).ToArray() ),
		},
	};

	static JsonObject Str( string description ) => new() { ["type"] = "string", ["description"] = description };
	static JsonObject Num( string description ) => new() { ["type"] = "number", ["description"] = description };
	static JsonObject Int( string description ) => new() { ["type"] = "integer", ["description"] = description };
	static JsonObject Bool( string description ) => new() { ["type"] = "boolean", ["description"] = description };
	static JsonObject Vec3Prop( string description ) => new()
	{
		["type"] = "array",
		["items"] = new JsonObject { ["type"] = "number" },
		["minItems"] = 3,
		["maxItems"] = 3,
		["description"] = description,
	};
	static JsonObject Obj( string description ) => new() { ["type"] = "object", ["description"] = description };
	static JsonObject Arr( string description ) => new() { ["type"] = "array", ["description"] = description };

	// ---- session ----

	readonly McpMapTools _base;
	MapKit.LinkedGroupSet _groups = new();
	readonly Dictionary<string, MapLayer> _layers = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, string> _brushLayers = new( StringComparer.Ordinal );
	readonly Dictionary<string, bool> _visCompileVisible = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, string> _visColourOverride = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, string> _visRenames = new( StringComparer.OrdinalIgnoreCase );
	readonly HashSet<string> _visRemoved = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, JsonObject> _editedBrushes = new( StringComparer.Ordinal );
	readonly List<MapKit.MapPasteBrush> _pastedBrushes = new();
	readonly HashSet<string> _removedBrushes = new( StringComparer.Ordinal );
	readonly MapKit.AssetRegistries _registries;
	MapKit.AssetHotload? _hotload;
	CompileBuildArtefacts? _artefacts;

	public McpMapPass2Tools( McpMapTools? baseTools = null, EntityRegistry? registry = null, string? workDirectory = null )
	{
		_base = baseTools ?? new McpMapTools( registry, workDirectory );
		_registries = new MapKit.AssetRegistries( _base.WorkDirectory );
	}

	public McpMapTools Base => _base;
	public string WorkDirectory => _base.WorkDirectory;
	public EntityRegistry Registry => _base.Registry;
	public int LayerCount => _layers.Count;
	public int EditedBrushCount => _editedBrushes.Count;
	public int PastedBrushCount => _pastedBrushes.Count;

	// ---- dispatch (mirrors the pass-1 CallAsync contract) ----

	/// <summary>Route one pass-2 tool call. ArgumentException propagates (bad
	/// name, missing required arg, bad arg value, missing confirm);
	/// any other exception becomes an IsError result. Cancellation propagates.</summary>
	public async Task<McpModelingTools.McpToolCallResult> CallAsync( string name, JsonNode? args, CancellationToken ct )
	{
		ct.ThrowIfCancellationRequested();
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Unknown pass-2 map tool." );
		var argsObject = args as JsonObject ?? throw new ArgumentException( "Tool arguments must be a JSON object." );
		var def = FindDefinition( name ) ?? throw new ArgumentException( "Unknown pass-2 map tool." );
		foreach ( var required in RequiredOf( def ) )
			if ( argsObject[required] is null )
				throw new ArgumentException( $"Missing required argument '{required}' for tool '{name}'." );
		foreach ( var forbidden in new[] { "command", "shell", "exec" } )
			if ( argsObject[forbidden] is not null )
				return new McpModelingTools.McpToolCallResult( Err( "shell-refused", $"Pass-2 tools never run a shell; '{forbidden}' is not an argument. Compile runs CompileRunner only." ), IsError: true );
		if ( RequiresConfirm( name, argsObject ) )
			throw new ArgumentException( $"Tool '{name}' is destructive — pass confirm=true." );
		try
		{
			var text = name switch
			{
				ToolLiveLighting => await RunVerbAsync( ToolLiveLighting, argsObject, ct ),
				ToolSkyboxPreview => await RunVerbAsync( ToolSkyboxPreview, argsObject, ct ),
				ToolEffectsPreview => await RunVerbAsync( ToolEffectsPreview, argsObject, ct ),
				ToolCompilePreset => await CompilePresetAsync( argsObject, ct ),
				ToolCompileOnlyEntities => await CompileOnlyEntitiesAsync( argsObject, ct ),
				ToolCheckProblems => await RunVerbAsync( ToolCheckProblems, argsObject, ct ),
				ToolIssueAutofix => await RunVerbAsync( ToolIssueAutofix, argsObject, ct ),
				ToolLayer => LayerTool( argsObject ),
				ToolVisGroupOps => await VisGroupOpsAsync( argsObject, ct ),
				ToolLock => await LockAsync( argsObject, ct ),
				ToolPasteSpecial => await PasteSpecialAsync( argsObject, ct ),
				ToolGizmo => RunVerb( ToolGizmo, argsObject, null ),
				ToolDirectEdit => await DirectEditAsync( argsObject, ct ),
				ToolLinkedGroup => await LinkedGroupAsync( argsObject, ct ),
				ToolPropPlace => await PropPlaceAsync( argsObject, ct ),
				ToolPropper => await PropperAsync( argsObject, ct ),
				ToolHotspot => await HotspotAsync( argsObject, ct ),
				ToolBrowser => BrowserTool( argsObject ),
				ToolHotload => HotloadTool( argsObject ),
				ToolPlaytest => await PlaytestAsync( argsObject, ct ),
				ToolNavmeshGenerate => await RunVerbAsync( ToolNavmeshGenerate, argsObject, ct ),
				_ => throw new ArgumentException( "Unknown pass-2 map tool." ),
			};
			return new McpModelingTools.McpToolCallResult( text, IsError: false );
		}
		catch ( OperationCanceledException ) { throw; }
		catch ( ArgumentException ) { throw; }
		catch ( Exception e ) { return new McpModelingTools.McpToolCallResult( e.Message, IsError: true ); }
	}

	static JsonObject? FindDefinition( string name )
	{
		foreach ( var def in Definitions )
			if ( def["name"]?.GetValue<string>() == name ) return def;
		return null;
	}

	static IEnumerable<string> RequiredOf( JsonObject def )
	{
		if ( def["inputSchema"] is JsonObject schema && schema["required"] is JsonArray required )
			foreach ( var r in required )
				if ( r?.GetValue<string>() is string s ) yield return s;
	}

	// ---- argument helpers (same shapes as the pass-1 tools) ----

	void CheckMap( JsonObject args )
	{
		if ( args["map"] is JsonValue mapValue && mapValue.TryGetValue<string>( out var map )
			&& !string.Equals( map, McpMapTools.MapName, StringComparison.Ordinal ) )
			throw new ArgumentException( $"Unknown map '{map}'. This server has one open map: '{McpMapTools.MapName}'." );
	}

	static string ReqString( JsonObject args, string name )
		=> args[name]?.GetValue<string>() is string s && !string.IsNullOrWhiteSpace( s )
			? s
			: throw new ArgumentException( $"Missing required argument '{name}'." );

	static string? OptString( JsonObject args, string name, string? fallback = null )
		=> args[name]?.GetValue<string>() ?? fallback;

	static double ReqDouble( JsonObject args, string name )
		=> args[name] is JsonValue v && v.TryGetValue<double>( out var d ) ? d : throw new ArgumentException( $"Argument '{name}' must be a number." );

	static double OptDouble( JsonObject args, string name, double fallback )
		=> args[name] is JsonValue v && v.TryGetValue<double>( out var d ) ? d : fallback;

	static int ReqInt( JsonObject args, string name )
		=> args[name] is JsonValue v && v.TryGetValue<int>( out var n ) ? n : throw new ArgumentException( $"Argument '{name}' must be an integer." );

	static int OptInt( JsonObject args, string name, int fallback )
		=> args[name] is JsonValue v && v.TryGetValue<int>( out var n ) ? n : fallback;

	static bool OptBool( JsonObject args, string name, bool fallback )
		=> args[name] is JsonValue v && v.TryGetValue<bool>( out var b ) ? b : fallback;

	static Vec3 ReqVec3( JsonObject args, string name )
	{
		if ( args[name] is JsonArray arr && arr.Count == 3
			&& arr[0] is JsonValue x && x.TryGetValue<double>( out var dx )
			&& arr[1] is JsonValue y && y.TryGetValue<double>( out var dy )
			&& arr[2] is JsonValue z && z.TryGetValue<double>( out var dz ) )
			return new Vec3( (float)dx, (float)dy, (float)dz );
		throw new ArgumentException( $"Argument '{name}' must be [x, y, z]." );
	}

	static Vec3 OptVec3( JsonObject args, string name, Vec3 fallback )
		=> args[name] is not null ? ReqVec3( args, name ) : fallback;

	static string[] StringArray( JsonObject args, string name )
	{
		if ( args[name] is not JsonArray arr ) return Array.Empty<string>();
		var list = new List<string>();
		foreach ( var item in arr ) list.Add( item?.GetValue<string>() ?? "" );
		return list.ToArray();
	}

	static int[] IntArray( JsonObject args, string name )
	{
		if ( args[name] is not JsonArray arr ) return Array.Empty<int>();
		var list = new List<int>();
		foreach ( var item in arr )
			if ( item is JsonValue v && v.TryGetValue<int>( out var n ) ) list.Add( n );
		return list.ToArray();
	}

	static Dictionary<string, string> StringMap( JsonObject args, string name )
	{
		var map = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( args[name] is JsonObject obj )
			foreach ( var pair in obj ) map[pair.Key] = pair.Value?.GetValue<string>() ?? "";
		return map;
	}

	static string Err( string code, string message ) => code + ": " + message;

	static JsonObject Ok( string tool, params (string Key, JsonNode? Value)[] fields )
	{
		var obj = new JsonObject { ["tool"] = tool, ["map"] = McpMapTools.MapName };
		foreach ( var (key, value) in fields ) obj[key] = value;
		return obj;
	}

	static JsonNode? J( string? v ) => v is null ? null : JsonValue.Create( v );
	static JsonNode? J( double v ) => JsonValue.Create( v );
	static JsonNode? J( float v ) => JsonValue.Create( (double)v );
	static JsonNode? J( int v ) => JsonValue.Create( v );
	static JsonNode? J( bool v ) => JsonValue.Create( v );
	static JsonNode? J( Vec3 v ) => new JsonArray( (double)v.x, (double)v.y, (double)v.z );

	// ---- session VMF bridge + kernel runner ----

	/// <summary>Export the live session to pass2-session.vmf in the work
	/// directory and return its path. Read verbs run the kernel runner
	/// against this file, so they always see current session state.</summary>
	async Task<string> SessionVmfPath( CancellationToken ct )
	{
		var res = await _base.CallAsync( McpMapTools.ToolImportExport,
			new JsonObject { ["op"] = "export", ["format"] = "vmf", ["fileName"] = "pass2-session.vmf" }, ct );
		if ( res.IsError ) throw new InvalidOperationException( "pass-2 session export failed: " + res.Text );
		using var doc = JsonDocument.Parse( res.Text );
		if ( doc.RootElement.TryGetProperty( "path", out var path ) && path.GetString() is string p && !string.IsNullOrWhiteSpace( p ) )
			return p;
		throw new InvalidOperationException( "pass-2 session export returned no path." );
	}

	/// <summary>Call the kernel verb runner. mapPath may be null for verbs
	/// that do not need a map (gizmo, browser, hotload, linked-group with
	/// explicit members). JSON args convert: strings/numbers/bools as plain
	/// values, arrays/objects as JSON text (the runner parses vec3/id-list
	/// args from either "x y z" or "[x, y, z]").</summary>
	string RunVerb( string verb, JsonObject args, string? mapPath )
	{
		CheckMap( args );
		return BuildScript.RunMapPass2Verb( verb, mapPath, ArgsDict( args ), _base.WorkDirectory );
	}

	async Task<string> RunVerbAsync( string verb, JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		return RunVerb( verb, args, path );
	}

	async Task<string> CallBase( string name, JsonObject args, CancellationToken ct )
	{
		var res = await _base.CallAsync( name, args, ct );
		if ( res.IsError ) throw new InvalidOperationException( res.Text );
		return res.Text;
	}

	// ---- per-tool implementations ----

	static Dictionary<string, string> ArgsDict( JsonObject args )
	{
		var dict = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var pair in args )
		{
			if ( pair.Value is null ) continue;
			dict[pair.Key] = pair.Value switch
			{
				JsonValue v when v.TryGetValue<string>( out var s ) => s,
				JsonValue v when v.TryGetValue<bool>( out var b ) => b ? "true" : "false",
				JsonValue v when v.TryGetValue<int>( out var n ) => n.ToString( CultureInfo.InvariantCulture ),
				JsonValue v when v.TryGetValue<double>( out var d ) => d.ToString( CultureInfo.InvariantCulture ),
				JsonNode n => n.ToJsonString(),
			};
		}
		return dict;
	}

	async Task<string> CompilePresetAsync( JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		var text = BuildScript.RunMapPass2CompileWithArtefacts( path, ArgsDict( args ), _base.WorkDirectory, out var artefacts );
		if ( artefacts is not null ) _artefacts = artefacts;
		return text;
	}

	async Task<string> CompileOnlyEntitiesAsync( JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		if ( _artefacts is not null )
			return BuildScript.RunMapPass2OnlyEntitiesWithArtefacts( path, _artefacts, ArgsDict( args ), _base.WorkDirectory );
		// No artefacts held: one-shot base + only-entities through the runner.
		// Call map_compile_preset first to seed artefacts for later calls.
		return RunVerb( ToolCompileOnlyEntities, args, path );
	}

	string LayerTool( JsonObject args )
	{
		CheckMap( args );
		var op = ReqString( args, "op" ).ToLowerInvariant();
		switch ( op )
		{
			case "list":
			{
				var arr = new JsonArray();
				foreach ( var pair in _layers )
				{
					var brushIds = new JsonArray();
					foreach ( var bp in _brushLayers )
						if ( string.Equals( bp.Value, pair.Key, StringComparison.OrdinalIgnoreCase ) ) brushIds.Add( bp.Key );
					arr.Add( new JsonObject
					{
						["id"] = pair.Value.Id,
						["name"] = pair.Value.Name,
						["colour"] = pair.Value.Colour,
						["visible"] = pair.Value.Visible,
						["compileVisible"] = pair.Value.CompileVisible,
						["brushes"] = brushIds,
					} );
				}
				return Ok( ToolLayer, ("op", J( "list" )), ("layers", arr ) ).ToJsonString();
			}
			case "define":
			{
				var name = ReqString( args, "name" );
				var colour = OptString( args, "colour", "#808080" ) ?? "#808080";
				if ( !MapLayer.IsValidColour( colour ) )
					throw new ArgumentException( $"Colour must be '#RRGGBB'; got '{colour}'." );
				var layer = new MapLayer
				{
					Id = name,
					Name = name,
					Colour = colour,
					Visible = OptBool( args, "visible", true ),
					CompileVisible = OptBool( args, "compileVisible", true ),
				};
				_layers[name] = layer;
				return Ok( ToolLayer, ("op", J( "define" )), ("name", J( name )), ("colour", J( colour )),
					("visible", J( layer.Visible )), ("compileVisible", J( layer.CompileVisible )) ).ToJsonString();
			}
			case "setvisible":
			{
				var name = ReqString( args, "name" );
				if ( !_layers.TryGetValue( name, out var layer ) ) return Err( "layer-not-found", $"No layer '{name}'." );
				layer.Visible = OptBool( args, "visible", true );
				return Ok( ToolLayer, ("op", J( "setvisible" )), ("name", J( name )), ("visible", J( layer.Visible )) ).ToJsonString();
			}
			case "setcolour":
			{
				var name = ReqString( args, "name" );
				var colour = ReqString( args, "colour" );
				if ( !MapLayer.IsValidColour( colour ) )
					throw new ArgumentException( $"Colour must be '#RRGGBB'; got '{colour}'." );
				if ( !_layers.TryGetValue( name, out var layer ) ) return Err( "layer-not-found", $"No layer '{name}'." );
				layer.Colour = colour;
				return Ok( ToolLayer, ("op", J( "setcolour" )), ("name", J( name )), ("colour", J( colour )) ).ToJsonString();
			}
			case "setcompilevisible":
			{
				var name = ReqString( args, "name" );
				if ( !_layers.TryGetValue( name, out var layer ) ) return Err( "layer-not-found", $"No layer '{name}'." );
				layer.CompileVisible = OptBool( args, "compileVisible", true );
				return Ok( ToolLayer, ("op", J( "setcompilevisible" )), ("name", J( name )), ("compileVisible", J( layer.CompileVisible )) ).ToJsonString();
			}
			case "rename":
			{
				var name = ReqString( args, "name" );
				var newName = ReqString( args, "newName" );
				if ( !_layers.TryGetValue( name, out var layer ) ) return Err( "layer-not-found", $"No layer '{name}'." );
				_layers.Remove( name );
				layer.Id = newName;
				layer.Name = newName;
				_layers[newName] = layer;
				foreach ( var key in _brushLayers.Keys.ToArray() )
					if ( string.Equals( _brushLayers[key], name, StringComparison.OrdinalIgnoreCase ) ) _brushLayers[key] = newName;
				return Ok( ToolLayer, ("op", J( "rename" )), ("name", J( newName )) ).ToJsonString();
			}
			case "remove":
			{
				var name = ReqString( args, "name" );
				if ( !_layers.Remove( name ) ) return Err( "layer-not-found", $"No layer '{name}'." );
				foreach ( var key in _brushLayers.Keys.ToArray() )
					if ( string.Equals( _brushLayers[key], name, StringComparison.OrdinalIgnoreCase ) ) _brushLayers.Remove( key );
				return Ok( ToolLayer, ("op", J( "remove" )), ("removed", J( name )) ).ToJsonString();
			}
			case "assign":
			{
				var name = ReqString( args, "name" );
				if ( !_layers.ContainsKey( name ) ) return Err( "layer-not-found", $"No layer '{name}'." );
				var assigned = 0;
				foreach ( var brushId in StringArray( args, "brushIds" ) )
				{
					if ( _base.FindBrush( brushId ) is null && !_editedBrushes.ContainsKey( brushId ) )
						return Err( "brush-not-found", $"No brush '{brushId}' in map '{McpMapTools.MapName}'." );
					_brushLayers[brushId] = name;
					assigned++;
				}
				return Ok( ToolLayer, ("op", J( "assign" )), ("name", J( name )), ("brushes", J( assigned )) ).ToJsonString();
			}
			default:
				throw new ArgumentException( $"Unknown layer op '{op}'. Known ops: list, define, setvisible, setcolour, setcompilevisible, rename, remove, assign." );
		}
	}

	async Task<string> VisGroupOpsAsync( JsonObject args, CancellationToken ct )
	{
		CheckMap( args );
		var op = ReqString( args, "op" ).ToLowerInvariant();
		switch ( op )
		{
			case "define":
			case "setvisible":
			case "assign":
				// The pass-1 tool owns the visgroup store; delegate.
				return await CallBase( McpMapTools.ToolVisGroup, args, ct );
			case "list":
			{
				var groups = new JsonArray();
				foreach ( var pair in _base.VisGroups )
				{
					if ( _visRemoved.Contains( pair.Key ) ) continue;
					var displayName = _visRenames.TryGetValue( pair.Key, out var renamed ) ? renamed : pair.Key;
					var color = _visColourOverride.TryGetValue( pair.Key, out var c ) ? c : pair.Value.Color;
					var compileVisible = _visCompileVisible.TryGetValue( pair.Key, out var cv ) ? cv : true;
					groups.Add( new JsonObject { ["name"] = displayName, ["color"] = color, ["visible"] = pair.Value.Visible, ["compileVisible"] = compileVisible } );
				}
				return Ok( ToolVisGroupOps, ("op", J( "list" )), ("visgroups", groups ) ).ToJsonString();
			}
			case "rename":
			{
				var name = ReqString( args, "name" );
				var newName = ReqString( args, "newName" );
				if ( !_base.VisGroups.ContainsKey( name ) ) return Err( "visgroup-not-found", $"No visgroup '{name}' in map '{McpMapTools.MapName}'." );
				_visRenames[name] = newName;
				return Ok( ToolVisGroupOps, ("op", J( "rename" )), ("name", J( newName )) ).ToJsonString();
			}
			case "setcolour":
			case "setcolor":
			{
				var name = ReqString( args, "name" );
				var colour = ReqString( args, "colour" );
				if ( !_base.VisGroups.ContainsKey( name ) ) return Err( "visgroup-not-found", $"No visgroup '{name}' in map '{McpMapTools.MapName}'." );
				_visColourOverride[name] = Pass2ColourToRgb( colour );
				return Ok( ToolVisGroupOps, ("op", J( "setcolour" )), ("name", J( name )), ("color", J( _visColourOverride[name] )) ).ToJsonString();
			}
			case "setcompilevisible":
			{
				var name = ReqString( args, "name" );
				if ( !_base.VisGroups.ContainsKey( name ) ) return Err( "visgroup-not-found", $"No visgroup '{name}' in map '{McpMapTools.MapName}'." );
				_visCompileVisible[name] = OptBool( args, "compileVisible", true );
				return Ok( ToolVisGroupOps, ("op", J( "setcompilevisible" )), ("name", J( name )), ("compileVisible", J( _visCompileVisible[name] )) ).ToJsonString();
			}
			case "remove":
			{
				var name = ReqString( args, "name" );
				if ( !_base.VisGroups.ContainsKey( name ) ) return Err( "visgroup-not-found", $"No visgroup '{name}' in map '{McpMapTools.MapName}'." );
				_visRemoved.Add( name );
				return Ok( ToolVisGroupOps, ("op", J( "remove" )), ("removed", J( name )) ).ToJsonString();
			}
			default:
				throw new ArgumentException( $"Unknown visgroup op '{op}'. Known ops: list, define, setvisible, assign, rename, setcolour, setcompilevisible, remove." );
		}
	}

	static string Pass2ColourToRgb( string colour )
	{
		colour = colour.Trim();
		if ( colour.StartsWith( "#", StringComparison.Ordinal ) )
		{
			if ( !MapLayer.IsValidColour( colour ) )
				throw new ArgumentException( $"Colour must be '#RRGGBB' or 'r g b'; got '{colour}'." );
			int r = int.Parse( colour.Substring( 1, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture );
			int g = int.Parse( colour.Substring( 3, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture );
			int b = int.Parse( colour.Substring( 5, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture );
			return $"{r} {g} {b}";
		}
		var parts = colour.Split( new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length == 3 && parts.All( p => int.TryParse( p, out _ ) ) ) return colour;
		throw new ArgumentException( $"Colour must be '#RRGGBB' or 'r g b'; got '{colour}'." );
	}

	async Task<string> PasteSpecialAsync( JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		var text = RunVerb( ToolPasteSpecial, TranslateBrushArgs( args, path ), path );
		// Keep the pasted brushes (planes + materials) in the session overlay
		// for the integrator merge; the base session cannot hold them yet.
		using var doc = JsonDocument.Parse( text );
		if ( doc.RootElement.TryGetProperty( "ok", out var ok ) && ok.GetBoolean()
			&& doc.RootElement.TryGetProperty( "pasted", out var pasted ) )
		{
			foreach ( var brushNode in pasted.EnumerateArray() )
			{
				var planes = new List<System.Numerics.Plane>();
				var materials = new List<string>();
				foreach ( var planeNode in brushNode.GetProperty( "planes" ).EnumerateArray() )
				{
					var n = planeNode.GetProperty( "normal" );
					planes.Add( new System.Numerics.Plane(
						new System.Numerics.Vector3( (float)n[0].GetDouble(), (float)n[1].GetDouble(), (float)n[2].GetDouble() ),
						(float)planeNode.GetProperty( "distance" ).GetDouble() ) );
					materials.Add( planeNode.GetProperty( "material" ).GetString() ?? "" );
				}
				_pastedBrushes.Add( new MapKit.MapPasteBrush( planes, null, materials ) );
			}
		}
		return text;
	}

	async Task<string> DirectEditAsync( JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		var sessionBrush = args["brush"]?.GetValue<string>();
		var text = RunVerb( ToolDirectEdit, TranslateBrushArgs( args, path ), path );
		// Keep the edited geometry in the overlay (as JSON) for the merge;
		// the base session's brush is untouched until the integrator wires
		// the write-back into McpMapTools.cs.
		using var doc = JsonDocument.Parse( text );
		if ( doc.RootElement.TryGetProperty( "editedBrush", out var edited )
			&& sessionBrush is not null )
		{
			var node = JsonNode.Parse( edited.GetRawText() )?.AsObject();
			if ( node is not null ) _editedBrushes[sessionBrush] = node;
		}
		return text;
	}

	async Task<string> LinkedGroupAsync( JsonObject args, CancellationToken ct )
	{
		var op = ReqString( args, "op" ).ToLowerInvariant();
		string? mapPath = null;
		if ( op == "define" && ( args["brushIds"] is not null || args["entityIds"] is not null ) )
			mapPath = await SessionVmfPath( ct );
		var callArgs = (JsonObject)args.DeepClone();
		callArgs["state"] = _groups.ToJson();
		if ( op == "define" && mapPath is not null && callArgs["brushIds"] is JsonArray brushIds )
		{
			// Session ids are "brush-N" (creation numbers); the VMF the runner reads
			// numbers world brushes sequentially and entity (tied) brushes by creation
			// number, so translate each session id to its VMF brush id first.
			var doc = MapKit.VmfIo.ParseFile( mapPath );
			var tied = new HashSet<int>();
			foreach ( var entity in doc.Entities )
				foreach ( var brush in entity.Brushes )
					tied.Add( brush.Id );
			var vmfIds = new JsonArray();
			foreach ( var item in brushIds )
			{
				var sessionId = item?.GetValue<string>() ?? throw new ArgumentException( "brushIds must be session brush ids (brush-1, ...)." );
				vmfIds.Add( SessionBrushToVmfId( sessionId, tied ) );
			}
			callArgs["brushIds"] = vmfIds;
		}
		var text = RunVerb( ToolLinkedGroup, callArgs, mapPath );
		using var doc2 = JsonDocument.Parse( text );
		if ( doc2.RootElement.TryGetProperty( "state", out var state ) && state.GetString() is string newState )
			_groups = MapKit.LinkedGroupSet.FromJson( newState );
		return text;
	}

	// Session brush "brush-N" -> VMF brush id. Tied (brush-entity) brushes keep
	// their creation number as the VMF id; world brushes are numbered in
	// session order, skipping tied ones.
	// Session brush "brush-N" -> VMF brush id. Tied (brush-entity) brushes keep
	// their creation number as the VMF id; world brushes are numbered in
	// session order, skipping tied ones.
	int SessionBrushToVmfId( string sessionId, HashSet<int> tiedNumbers )
	{
		if ( !sessionId.StartsWith( "brush-", StringComparison.Ordinal )
			|| !int.TryParse( sessionId["brush-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n ) )
			throw new ArgumentException( $"brush-not-found: No brush '{sessionId}' in map '{McpMapTools.MapName}'." );
		if ( tiedNumbers.Contains( n ) ) return n;
		var world = 0;
		foreach ( var id in _base.BrushIds )
		{
			if ( id == sessionId ) return world + 1;
			if ( id.StartsWith( "brush-", StringComparison.Ordinal )
				&& int.TryParse( id["brush-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m )
				&& !tiedNumbers.Contains( m ) ) world++;
		}
		throw new ArgumentException( $"brush-not-found: No brush '{sessionId}' in map '{McpMapTools.MapName}'." );
	}

	// Translate session brush ids ("brush-N") in the known brush-arg keys to
	// the VMF brush ids the kernel runner reads. Returns a clone; the
	// caller's original args keep the session ids for overlays/merge output.
	JsonObject TranslateBrushArgs( JsonObject args, string vmfPath )
	{
		var doc = MapKit.VmfIo.ParseFile( vmfPath );
		var tied = new HashSet<int>();
		foreach ( var entity in doc.Entities )
			foreach ( var brush in entity.Brushes )
				tied.Add( brush.Id );
		var copy = (JsonObject)args.DeepClone();
		foreach ( var key in new[] { "brush", "brushId" } )
			if ( copy[key] is JsonValue v && v.TryGetValue<string>( out var s ) )
				copy[key] = SessionBrushToVmfId( s, tied );
		foreach ( var key in new[] { "brushes", "brushIds" } )
			if ( copy[key] is JsonArray arr )
			{
				var ids = new JsonArray();
				foreach ( var item in arr )
					ids.Add( SessionBrushToVmfId( item?.GetValue<string>() ?? throw new ArgumentException( $"{key} must be session brush ids (brush-1, ...)." ), tied ) );
				copy[key] = ids;
			}
		return copy;
	}

	async Task<string> LockAsync( JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		return RunVerb( ToolLock, TranslateBrushArgs( args, path ), path );
	}

	async Task<string> HotspotAsync( JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		return RunVerb( ToolHotspot, TranslateBrushArgs( args, path ), path );
	}

	async Task<string> PropPlaceAsync( JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		var poseArgs = (JsonObject)args.DeepClone();
		poseArgs["seat"] = false;
		var text = RunVerb( ToolPropPlace, poseArgs, path );
		if ( !OptBool( args, "seat", false ) ) return text;
		using var doc = JsonDocument.Parse( text );
		var root = doc.RootElement;
		if ( !root.TryGetProperty( "seated", out var seated ) || !seated.GetBoolean() ) return text;
		var model = OptString( args, "model" ) ?? throw new ArgumentException( "seat=true needs model=<path> for the prop entity." );
		var className = OptString( args, "class", "prop_static" ) ?? "prop_static";
		var pose = root.GetProperty( "pose" );
		var origin = pose.GetProperty( "origin" );
		var yaw = pose.GetProperty( "yawDegrees" ).GetDouble();
		var placeArgs = new JsonObject
		{
			["class"] = className,
			["position"] = new JsonArray( origin[0].GetDouble(), origin[1].GetDouble(), origin[2].GetDouble() ),
			["angles"] = new JsonArray( 0, yaw, 0 ),
			["keyvalues"] = new JsonObject { ["model"] = model },
		};
		var placedText = await CallBase( McpMapTools.ToolEntityPlace, placeArgs, ct );
		var merged = JsonNode.Parse( text )!.AsObject();
		using var placedDoc = JsonDocument.Parse( placedText );
		if ( placedDoc.RootElement.TryGetProperty( "entityId", out var entityId ) )
			merged["entityId"] = entityId.GetInt32();
		merged["seatedInSession"] = true;
		return merged.ToJsonString();
	}

	async Task<string> PropperAsync( JsonObject args, CancellationToken ct )
	{
		var path = await SessionVmfPath( ct );
		var sessionIds = StringArray( args, "brushes" );
		var text = RunVerb( ToolPropper, TranslateBrushArgs( args, path ), path );
		using var doc = JsonDocument.Parse( text );
		var root = doc.RootElement;
		if ( root.TryGetProperty( "ok", out var ok ) && ok.GetBoolean()
			&& root.TryGetProperty( "removedBrushCount", out var removed )
			&& root.TryGetProperty( "keptBrushCount", out var kept ) )
		{
			var merged = JsonNode.Parse( text )!.AsObject();
			if ( removed.GetInt32() == sessionIds.Length && sessionIds.Length > 0 )
			{
				foreach ( var id in sessionIds ) _removedBrushes.Add( id );
				merged["sessionRemovedBrushIds"] = new JsonArray( sessionIds.Select( i => (JsonNode?)i ).ToArray() );
			}
			else if ( removed.GetInt32() > 0 )
			{
				merged["sessionRemovalNote"] = $"Converter removed {removed.GetInt32()} of {sessionIds.Length} brushes but does not name which; none were removed from the session (integrator seam).";
			}
			return merged.ToJsonString();
		}
		return text;
	}

	string BrowserTool( JsonObject args )
	{
		CheckMap( args );
		var kind = ReqString( args, "kind" ).ToLowerInvariant();
		if ( kind == "texture" )
			return Err( "texture-browser-no-registry", "There is no TextureAssetRegistry in the kernel (only Model/Particle/Sound registries). A texture browser needs the asset-registries ticket to land one." );
		if ( kind != "model" && kind != "particle" && kind != "sound" )
			throw new ArgumentException( $"kind must be model, particle, sound or texture; got '{kind}'." );
		var query = ReqString( args, "query" );
		int limit = OptInt( args, "limit", 20 );
		var rejected = new List<string>();
		if ( args["entries"] is JsonArray entries )
		{
			foreach ( var node in entries )
			{
				var o = node?.AsObject() ?? throw new ArgumentException( "Each entry must be a JSON object." );
				bool ok = kind switch
				{
					"model" => _registries.Models.Register( new MapKit.ModelEntry
					{
						Name = o["name"]?.GetValue<string>() ?? "",
						Path = o["path"]?.GetValue<string>() ?? "",
						Attachments = o["attachments"] is JsonArray att ? att.Select( x => x?.GetValue<string>() ?? "" ).ToList() : new List<string>(),
						Scale = o["scale"] is JsonArray sc && sc.Count == 3
							? new Vec3( (float)sc[0]!.GetValue<double>(), (float)sc[1]!.GetValue<double>(), (float)sc[2]!.GetValue<double>() )
							: Vec3.One,
						PropType = o["propType"]?.GetValue<string>() is string pt && Enum.TryParse<MapKit.PropType>( pt, true, out var parsedPt ) ? parsedPt : MapKit.PropType.Static,
					} ),
					"particle" => _registries.Particles.Register( new MapKit.ParticleEntry
					{
						SystemName = o["systemName"]?.GetValue<string>() ?? "",
						DefinitionPath = o["definitionPath"]?.GetValue<string>() ?? "",
						Params = o["params"] is JsonObject po ? po.ToDictionary( kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "" ) : new Dictionary<string, string>(),
					} ),
					_ => _registries.Sounds.Register( new MapKit.SoundEntry
					{
						Name = o["name"]?.GetValue<string>() ?? "",
						Path = o["path"]?.GetValue<string>() ?? "",
						Volume = (float)( o["volume"]?.GetValue<double>() ?? 1.0 ),
					} ),
				};
				if ( !ok ) rejected.Add( o["name"]?.GetValue<string>() ?? o["systemName"]?.GetValue<string>() ?? "(unnamed)" );
			}
		}
		var results = new JsonArray();
		int count = 0;
		if ( kind == "model" )
		{
			foreach ( var e in _registries.Models.Search( query ) )
			{
				if ( count >= limit ) break;
				results.Add( new JsonObject
				{
					["name"] = e.Name,
					["path"] = e.Path,
					["propType"] = e.PropType.ToString(),
					["scale"] = J( e.Scale ),
					["attachments"] = new JsonArray( e.Attachments.Select( x => (JsonNode?)x ).ToArray() ),
					["usedCount"] = e.UsedCount,
				} );
				count++;
			}
		}
		else if ( kind == "particle" )
		{
			foreach ( var e in _registries.Particles.Search( query ) )
			{
				if ( count >= limit ) break;
				var paramsObj = new JsonObject();
				foreach ( var kv in e.Params ) paramsObj[kv.Key] = kv.Value;
				results.Add( new JsonObject
				{
					["systemName"] = e.SystemName,
					["definitionPath"] = e.DefinitionPath,
					["params"] = paramsObj,
					["usedCount"] = e.UsedCount,
				} );
				count++;
			}
		}
		else
		{
			foreach ( var e in _registries.Sounds.Search( query ) )
			{
				if ( count >= limit ) break;
				results.Add( new JsonObject
				{
					["name"] = e.Name,
					["path"] = e.Path,
					["volume"] = (double)e.Volume,
					["usedCount"] = e.UsedCount,
				} );
				count++;
			}
		}
		var obj = Ok( ToolBrowser,
			("kind", J( kind )), ("query", J( query )), ("resultCount", J( count )), ("results", results ),
			("rejectedEntries", new JsonArray( rejected.Select( r => (JsonNode?)r ).ToArray() )) );
		if ( args["entries"] is null )
			obj["note"] = "The registry starts empty: pass entries=[...] to register assets first. The registry persists across calls on this tool. AssetRegistries has no file loader.";
		return obj.ToJsonString();
	}

	string HotloadTool( JsonObject args )
	{
		CheckMap( args );
		var op = ReqString( args, "op" ).ToLowerInvariant();
		string root = OptString( args, "root" ) ?? _base.WorkDirectory;
		_hotload ??= new MapKit.AssetHotload( root );
		if ( args["watches"] is JsonArray watches )
		{
			foreach ( var node in watches )
			{
				var o = node?.AsObject() ?? throw new ArgumentException( "Each watch must be a JSON object." );
				var kindText = o["kind"]?.GetValue<string>() ?? throw new ArgumentException( "Each watch needs kind." );
				var kind = kindText.ToLowerInvariant() switch
				{
					"model" => MapKit.AssetKind.Model,
					"material" => MapKit.AssetKind.Material,
					"sound" => MapKit.AssetKind.Sound,
					"particle" => MapKit.AssetKind.Particle,
					_ => throw new ArgumentException( $"Watch kind must be model, material, sound or particle; got '{kindText}'." ),
				};
				var path = o["path"]?.GetValue<string>() ?? throw new ArgumentException( "Each watch needs path." );
				_hotload.Watch( kind, path );
			}
		}
		var obj = Ok( ToolHotload, ("op", J( op )), ("root", J( root )) );
		if ( op == "watch" || op == "list" )
		{
			var arr = new JsonArray();
			foreach ( var asset in _hotload.WatchedAssets ) arr.Add( HotloadAssetJson( asset ) );
			obj["watched"] = arr;
			return obj.ToJsonString();
		}
		if ( op == "poll" )
		{
			var changes = new JsonArray();
			foreach ( var c in _hotload.PollForChanges() )
				changes.Add( new JsonObject
				{
					["asset"] = HotloadAssetJson( c.Asset ),
					["kind"] = c.Kind.ToString(),
					["detectedAtUtc"] = c.DetectedAtUtc.ToString( "o", CultureInfo.InvariantCulture ),
				} );
			obj["changes"] = changes;
			obj["changeCount"] = changes.Count;
			return obj.ToJsonString();
		}
		if ( op == "reload" )
		{
			var report = _hotload.PollAndReload();
			var reloads = new JsonArray();
			foreach ( var r in report.Reloads )
				reloads.Add( new JsonObject
				{
					["asset"] = HotloadAssetJson( r.Asset ),
					["success"] = r.Success,
					["definitionChanged"] = r.DefinitionChanged,
					["error"] = r.Error,
					["changeKind"] = r.ChangeKind.ToString(),
				} );
			obj["changes"] = report.Changes.Count;
			obj["reloads"] = reloads;
			obj["issues"] = new JsonArray( report.Issues.Select( i => (JsonNode?)JsonValue.Create(i.Code + ": " + i.Message) ).ToArray() );
			obj["hasIssues"] = report.HasIssues;
			obj["note"] = "Reload without a project HotloadDocument reports what the poll found; full definition reload needs the document the integrator wires in.";
			return obj.ToJsonString();
		}
		throw new ArgumentException( $"Unknown hotload op '{op}'. Known ops: watch, list, poll, reload." );
	}

	static JsonObject HotloadAssetJson( MapKit.HotloadAsset asset ) => new()
	{
		["kind"] = asset.Kind.ToString(),
		["path"] = asset.Path,
		["fullPath"] = asset.FullPath,
		["exists"] = asset.Exists,
		["lastWriteTimeUtc"] = asset.LastWriteTimeUtc.ToString( "o", CultureInfo.InvariantCulture ),
		["length"] = asset.Length,
	};

	async Task<string> PlaytestAsync( JsonObject args, CancellationToken ct )
	{
		// The runner re-bakes internally for rerun (correct; the held-bake
		// incremental path — IncrementalRebake/ComputeChangeSet — is a
		// follow-up seam, see SEAM.md).
		return await RunVerbAsync( ToolPlaytest, args, ct );
	}
}
