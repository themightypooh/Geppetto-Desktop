// McpMapTools.cs — MCP tools for native map operations (task hammer-ai-mcp-map-tools).
//
// The map side of the Geppetto Desktop MCP server, on top of the existing
// server (mcp-server-stdio-transport-and-protocol) and modeling provider
// (mcp-tools-modeling-operations). This file is the transport-neutral
// provider the transport calls, mirroring McpModelingTools:
//
//   ListTools()                    -> tools/list: the 18 map_* tools.
//   CallAsync(name, args, ct)      -> tools/call: run one map tool.
//
// The transport maps a CallAsync result to the MCP tools/call shape:
// content [{ type: "text", text: result.Text }], isError result.IsError.
// Error channels, by MCP convention (same as McpModelingTools):
//   - ArgumentException = invalid params: unknown tool, arguments that are
//     not a JSON object, a missing required argument, a bad enum/number, or
//     a `map` reference that is not the open map. The transport maps this
//     to JSON-RPC -32602.
//   - McpToolCallResult with IsError = true = the call was well-formed but
//     the tool failed (unknown brush/entity id, invalid brush, unresolved
//     I/O target, compile failure, capability not yet in the kernel...).
//     The text names the failure (e.g. "brush-not-found: ..."), never a
//     stack trace.
//   - OperationCanceledException propagates untouched (the transport owns
//     cancellation).
//
// Safety gating is applied by the transport, not here: it calls
// McpSafety.ValidateDefinitions on the tool list once at startup and wraps
// every tools/call in BeginCall/GuardedCall. Do NOT double-wrap here.
// Destructive-flag enforcement for the map tools (import replaces the
// session; Final compile overwrites outputs; a clip can cut a brush away
// entirely) belongs to the safety ticket (hammer-ai-safety-gates-map,
// worker 68): it owns McpSafety.cs / GateCheck.cs. This file exposes the
// candidate set as DestructiveToolNames so that ticket can register it
// without guessing; until then the tools are honest about what they do
// in their descriptions and results.
//
// SESSION, NOT MapDocument (yet). MapDocument HAS landed in the working
// tree (kernel/Effigy/Map/MapDocument.cs, hammer-map-document-model, in
// progress by a sibling worker) but is not this ticket's dependency, so
// this provider owns one in-memory session per instance — brushes,
// entities, displacements, I/O rows, groups, visgroups, sky name — built
// from the kernel types (MapBrush, MapEntities, Displacement,
// CompileInput, IssueDocument, MapBakeDocument, LinkedGroupSet,
// VmfDocument), and projects it onto each API's view type when a tool
// runs. When MapDocument lands, the integrator replaces this session
// with a thin adapter onto it; the tool names, schemas and result shapes
// stay the same. Every tool takes an optional `map` argument: the name of
// the open map ("map"). A different name is an invalid-params refusal,
// not a silent switch — there is exactly one open map per provider
// instance.
//
// BUILDSCRIPT SEAM (dep hammer-ai-buildscript-verbs-map, worker 64, in
// parallel). BuildScript.Map.cs does NOT exist yet, so these tools call
// the kernel APIs directly. Each tool documents its BuildScript verb in
// BuildScriptVerbFor(); when that ticket lands, the tools route through
// its verbs and this file becomes a thin MCP mapping over them. No second
// scripting surface is invented here.
//
// NATIVE ONLY. No Hammer app, no shelling out to Hammer/Hammer++/VBSP/
// VVIS/VRAD. Compile runs CompileRunner (native kernel), bake runs
// MapBakeExport, validation runs IssueEngine, import/export runs
// VmfIo/Map220Io. Two capabilities the ticket names have NO kernel
// implementation yet and are reported honestly instead of faked:
//   - map_navmesh: no navmesh type has landed (PlaytestLoop says so);
//     the tool runs PreLaunchValidation and reports navmeshRequired /
//     NAVMESH_MISSING, and generation is a named not-implemented.
//   - map_skybox_cubemap: the skybox half is real (skyname + sky_camera
//     + SkyboxPreview); cubemap capture has no kernel builder and is a
//     named not-implemented in the result.
// Also honest: MapBrushPrimitives (wedge/cylinder/sphere/arch as plane
// brushes) references MapFace/MapPlane types that do not exist against
// the landed MapBrush, so it cannot be called; the plane builders for
// those shapes live in this file (BuildWedge/BuildCylinder/BuildSphere/
// BuildArch) until that file is reconciled with the landed MapBrush.
//
// This file owns no transport, no protocol framing and no Program.cs
// dispatch. Do not add them here. It is wired into McpServer the way
// McpModelingTools is wired: McpServer appends ListTools() to tools/list
// and dispatches map_* names to CallAsync through SafetyGates.BeginMapCall
// (the map gate from hammer-ai-safety-gates-map).
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy;
using MapKit = Effigy.Map;

namespace Marionette.Desktop;

/// <summary>MCP tools for native Hammer map operations, over one in-memory
/// map session. See the file header for the session model, the error
/// contract, and the seams (BuildScript.Map.cs, MapDocument, safety).</summary>
public sealed class McpMapTools
{
	public const string ToolBrushCreate = "map_brush_create";
	public const string ToolBrushEdit = "map_brush_edit";
	public const string ToolBrushClip = "map_brush_clip";
	public const string ToolBrushVertex = "map_brush_vertex";
	public const string ToolDisplacementCreate = "map_displacement_create";
	public const string ToolDisplacementSculpt = "map_displacement_sculpt";
	public const string ToolFaceMaterial = "map_face_material";
	public const string ToolEntityPlace = "map_entity_place";
	public const string ToolEntityEdit = "map_entity_edit";
	public const string ToolIoAdd = "map_io_add";
	public const string ToolGroup = "map_group";
	public const string ToolVisGroup = "map_visgroup";
	public const string ToolImportExport = "map_import_export";
	public const string ToolCompile = "map_compile";
	public const string ToolValidate = "map_validate";
	public const string ToolBakeLightmap = "map_bake_lightmap";
	public const string ToolNavmesh = "map_navmesh";
	public const string ToolSkyboxCubemap = "map_skybox_cubemap";

	/// <summary>Every map_* tool name, in tools/list order.</summary>
	public static readonly IReadOnlyList<string> ToolNames = new[]
	{
		ToolBrushCreate, ToolBrushEdit, ToolBrushClip, ToolBrushVertex,
		ToolDisplacementCreate, ToolDisplacementSculpt, ToolFaceMaterial,
		ToolEntityPlace, ToolEntityEdit, ToolIoAdd, ToolGroup, ToolVisGroup,
		ToolImportExport, ToolCompile, ToolValidate, ToolBakeLightmap,
		ToolNavmesh, ToolSkyboxCubemap,
	};

	/// <summary>Candidate destructive set for the safety ticket
	/// (hammer-ai-safety-gates-map) to register in McpSafety: import
	/// replaces the whole session, and map_compile with preset=final
	/// overwrites product files when an output folder is given. A clip
	/// that cuts a whole brush away removes that brush; it is reported
	/// in the result (outcome WholeBrushCutAway), not hidden.</summary>
	public static readonly IReadOnlyList<string> DestructiveToolNames = new[]
	{
		ToolImportExport, ToolCompile,
	};

	/// <summary>The BuildScript.Map.cs verb each MCP tool will route
	/// through once that ticket lands (worker 64). Until then the tools
	/// call the kernel APIs directly; the verb name is the seam.</summary>
	public static string BuildScriptVerbFor( string tool ) => tool switch
	{
		ToolBrushCreate => "map_brush_create",
		ToolBrushEdit => "map_brush_edit",
		ToolBrushClip => "map_brush_clip",
		ToolBrushVertex => "map_brush_vertex",
		ToolDisplacementCreate => "map_displacement_create",
		ToolDisplacementSculpt => "map_displacement_sculpt",
		ToolFaceMaterial => "map_face_material",
		ToolEntityPlace => "map_entity_place",
		ToolEntityEdit => "map_entity_edit",
		ToolIoAdd => "map_io_add",
		ToolGroup => "map_group",
		ToolVisGroup => "map_visgroup",
		ToolImportExport => "map_import_export",
		ToolCompile => "map_compile",
		ToolValidate => "map_validate",
		ToolBakeLightmap => "map_bake_lightmap",
		ToolNavmesh => "map_navmesh",
		ToolSkyboxCubemap => "map_skybox_cubemap",
		_ => "",
	};

	/// <summary>Minimal entity registry used when the host does not inject
	/// one: the classes the map tools need for a sealed room + I/O demo.
	/// Same classes as tests/fixtures/hammer/hammer-ai-mcp-map-tools/
	/// entity-registry.json (the checked-in copy; this embedded copy is
	/// the no-files fallback for the server).</summary>
	public const string DefaultRegistryJson = """
		{"classes":[
		{"description":"Player spawn point.","kind":"point","name":"info_player_start"},
		{"description":"Navigation node; a map with one needs a navmesh.","kind":"point","name":"info_node"},
		{"description":"Point light.","kind":"point","name":"light"},
		{"description":"Directional sun light.","kind":"point","name":"light_environment"},
		{"description":"3D skybox camera.","keyvalues":[{"description":"3D skybox scale (Source default 16).","name":"scale","type":"float"}],"kind":"point","name":"sky_camera"},
		{"description":"Brush door.","keyvalues":[{"description":"Move speed in inches per second.","name":"speed","type":"float"},{"description":"Seconds before the door returns.","name":"wait","type":"float"}],"kind":"brush","name":"func_door"},
		{"description":"Brush trigger volume.","kind":"brush","name":"trigger_multiple"},
		{"description":"Logic relay; fires outputs when triggered.","kind":"point","name":"logic_relay"},
		{"description":"Fires outputs when the map spawns.","kind":"point","name":"logic_auto"},
		{"description":"Static prop.","keyvalues":[{"description":"Model file.","name":"model","type":"model"}],"kind":"point","name":"prop_static"}
		],"description":"Minimal registry for the MCP map tools (sealed room + I/O).","name":"mcp-map-tools","schemaVersion":1}
		""";

	/// <summary>The one open map's name. Every tool accepts an optional
	/// `map` argument that must equal this.</summary>
	public const string MapName = "map";

	/// <summary>Default material for new brush faces when the caller
	/// names none: nodraw, the same default MapBrush uses.</summary>
	public const string DefaultMaterial = ToolTexture.NoDrawName;

	readonly EntityRegistry _registry;
	MapEntities _entities;
	EntityIoList _io = new();
	readonly MapKit.LinkedGroupSet _groups = new();
	readonly Dictionary<string, MapBrush> _brushes = new( StringComparer.Ordinal );
	readonly List<string> _brushOrder = new();
	readonly Dictionary<string, string> _brushNames = new( StringComparer.Ordinal );
	readonly Dictionary<string, int> _brushNumbers = new( StringComparer.Ordinal );
	readonly HashSet<string> _detailBrushes = new( StringComparer.Ordinal );
	readonly Dictionary<string, string> _brushVisGroup = new( StringComparer.Ordinal );
	readonly Dictionary<string, MapKit.Displacement> _displacements = new( StringComparer.Ordinal );
	readonly Dictionary<string, string> _displacementBrush = new( StringComparer.Ordinal );
	readonly Dictionary<string, (string Color, bool Visible)> _visGroups = new( StringComparer.Ordinal );
	readonly Dictionary<int, List<string>> _entityBrushIds = new();
	string _skyName = "";
	int _nextBrush = 1;
	int _nextDisplacement = 1;

	/// <summary>Files the tools write (bake exports, import/export
	/// fileName) land here. Defaults to a per-process temp dir.</summary>
	public string WorkDirectory { get; }

	/// <summary>The entity registry this session validates classes and
	/// keyvalues against.</summary>
	public EntityRegistry Registry => _registry;

	/// <summary>The session's sky name (worldspawn skyname), "" when unset.</summary>
	public string SkyName => _skyName;

	/// <summary>Session brushes, in creation order.</summary>
	public IReadOnlyList<string> BrushIds => _brushOrder;

	/// <summary>Number of session brushes.</summary>
	public int BrushCount => _brushes.Count;

	/// <summary>Session entities, in placement order.</summary>
	public IReadOnlyList<MapEntity> Entities => _entities.Entities;

	/// <summary>Number of session entities.</summary>
	public int EntityCount => _entities.Count;

	/// <summary>Session I/O rows.</summary>
	public int IoCount => _io.Count;

	/// <summary>Session displacements.</summary>
	public int DisplacementCount => _displacements.Count;

	/// <summary>Session linked-group definitions.</summary>
	public IReadOnlyList<MapKit.LinkedGroupDefinition> GroupDefinitions => _groups.Definitions;

	/// <summary>Session linked-group copies.</summary>
	public IReadOnlyList<MapKit.LinkedGroupInstance> GroupInstances => _groups.Instances;

	/// <summary>Session visgroups by name: colour + visibility.</summary>
	public IReadOnlyDictionary<string, (string Color, bool Visible)> VisGroups => _visGroups;

	public McpMapTools() : this( null, null ) { }

	/// <summary>Build a provider over one map session. A null registry
	/// loads <see cref="DefaultRegistryJson"/>; a null work directory
	/// uses a per-process temp dir.</summary>
	public McpMapTools( EntityRegistry? registry, string? workDirectory = null )
	{
		_registry = registry ?? EntityRegistry.LoadFromJson( DefaultRegistryJson, "mcp-map-tools-default" );
		_entities = new MapEntities( _registry );
		WorkDirectory = string.IsNullOrWhiteSpace( workDirectory )
			? Path.Combine( Path.GetTempPath(), "geppetto-mcp-map-" + Environment.ProcessId )
			: workDirectory;
		Directory.CreateDirectory( WorkDirectory );
	}

	/// <summary>A session brush by id, or null.</summary>
	public MapBrush? FindBrush( string id ) => id is null ? null : _brushes.TryGetValue( id, out var b ) ? b : null;

	/// <summary>A session brush's display name (its id when unnamed).</summary>
	public string BrushDisplayName( string id ) => _brushNames.TryGetValue( id, out var n ) ? n : id;

	/// <summary>True when the brush is a detail brush (does not seal).</summary>
	public bool IsDetailBrush( string id ) => _detailBrushes.Contains( id );

	/// <summary>A session brush's visgroup, or "" when unassigned.</summary>
	public string BrushVisGroup( string id ) => _brushVisGroup.TryGetValue( id, out var g ) ? g : "";

	/// <summary>A session entity by id, or null.</summary>
	public MapEntity? FindEntity( int id ) => _entities.FindById( id );

	/// <summary>A session displacement by id, or null.</summary>
	public MapKit.Displacement? FindDisplacement( string id ) => id is null ? null : _displacements.TryGetValue( id, out var d ) ? d : null;

	// ---- tools/list ----

	/// <summary>MCP tools/list: the 18 map_* tool definitions. Fresh
	/// objects per call; the caller may mutate them.</summary>
	public JsonArray ListTools()
	{
		var tools = new JsonArray();
		foreach ( var def in Definitions )
			tools.Add( def.DeepClone() );
		return tools;
	}

	/// <summary>The tool definitions ListTools() serves.</summary>
	public static IReadOnlyList<JsonObject> Definitions { get; } = BuildDefinitions();

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

	static IReadOnlyList<JsonObject> BuildDefinitions()
	{
		var mapProp = Str( "Map document reference: the open map's name ('map'). Omit to use the open map." );
		return new[]
		{
			Tool( ToolBrushCreate,
				"Create a convex brush in the open map (box, wedge, cylinder, sphere, arch, or explicit planes). Delegates to the native MapBrush plane kernel; an invalid brush is a named error, not a brush. BuildScript verb: map_brush_create.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["shape"] = Str( "box | wedge | cylinder | sphere | arch | planes." ),
					["min"] = Vec3Prop( "Box min corner (inches, Z-up). Box needs min+max or center+size." ),
					["max"] = Vec3Prop( "Box max corner." ),
					["center"] = Vec3Prop( "Box/prism centre." ),
					["size"] = Vec3Prop( "Box size (sizeX, sizeY, sizeZ)." ),
					["radius"] = Num( "Cylinder/sphere/arch radius (inches, > 0)." ),
					["height"] = Num( "Cylinder/arch height (inches, > 0)." ),
					["segments"] = Int( "Cylinder/sphere/arch sides around (3..512; sphere/arch also need divisions/sweep)." ),
					["divisions"] = Int( "Sphere latitude bands (2..64)." ),
					["sweepDegrees"] = Num( "Arch sweep in degrees (0, 180]." ),
					["position"] = Vec3Prop( "Prism centre (default origin)." ),
					["material"] = Str( "Material for every face (default tools/toolsnodraw)." ),
					["faces"] = Arr( "Planes shape: [{normal:[x,y,z], distance:number, material?:string, uv?:{offset:[u,v], scale:[u,v], rotation:number, lightmapScale:number, smoothingGroup:number}}]." ),
					["detail"] = Bool( "True = detail brush (does not seal the map)." ),
					["name"] = Str( "Display name for the brush (default its id)." ),
				}, "shape" ),
			Tool( ToolBrushEdit,
				"Edit a session brush: translate it, or set one/all face materials or UVs. Rebuilds the brush from its planes (MapBrush is immutable); the result is re-validated. BuildScript verb: map_brush_edit.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["brushId"] = Str( "Brush id from map_brush_create (brush-1, ...)." ),
					["op"] = Str( "translate | setMaterial | setUv." ),
					["delta"] = Vec3Prop( "Translate by this delta (inches)." ),
					["face"] = new JsonObject { ["description"] = "Face index, or \"all\" for every face." },
					["material"] = Str( "New material name." ),
					["uv"] = Obj( "UV overrides: {offset:[u,v], scale:[u,v], rotation:number, lightmapScale:number, smoothingGroup:number}." ),
				}, "brushId", "op" ),
			Tool( ToolBrushClip,
				"Clip a brush by a 2-point line drawn in a 2D view (MapClip): cut one side away, or split into two brushes. A plane that misses the brush is a named NoOp outcome, not a silent success. BuildScript verb: map_brush_clip.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["brushId"] = Str( "Brush id." ),
					["a"] = Vec3Prop( "First clip point." ),
					["b"] = Vec3Prop( "Second clip point (must differ from a)." ),
					["viewAxis"] = Str( "View the line was drawn in: x | y | z (default z, the top view)." ),
					["mode"] = Str( "cut | split (default cut)." ),
					["keepFront"] = Bool( "Keep the side the clip plane normal points to (default true)." ),
					["cutFaceMaterial"] = Str( "Material for the new cut face (default the brush's first face material)." ),
					["cutFaceNoDraw"] = Bool( "New cut face is tools/toolsnodraw (default true)." ),
				}, "brushId", "a", "b" ),
			Tool( ToolBrushVertex,
				"Move one vertex of a brush to an absolute position (or by a delta) via MapVertexEdit. A twisted result comes back twisted with its invalid faces flagged in the result, never silently repaired. BuildScript verb: map_brush_vertex.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["brushId"] = Str( "Brush id." ),
					["vertex"] = Int( "Vertex index (0-based, in the brush's solved vertex order)." ),
					["to"] = Vec3Prop( "Absolute position for the vertex." ),
					["delta"] = Vec3Prop( "Move the vertex by this delta. Give to or delta, not both." ),
				}, "brushId", "vertex" ),
			Tool( ToolDisplacementCreate,
				"Bind a fresh displacement grid to a quad face (power 2/3/4): flat offsets and alphas, ready to sculpt. Corners are the face's 4 ordered corners; the plane normal comes from Newell's method. A displacement never seals the map. BuildScript verb: map_displacement_create.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["corners"] = Arr( "Four [x,y,z] corners, in order." ),
					["power"] = Int( "Subdivision power 2..4 (grid is (2^power+1) per side; default 3)." ),
					["materialId"] = Int( "Material id for the face (kernel integer id; default 0)." ),
					["brushId"] = Str( "Optional session brush this displacement belongs to (recorded, not yet bound to the brush face — see the file header)." ),
				}, "corners" ),
			Tool( ToolDisplacementSculpt,
				"Apply one sculpt dab to a displacement (DisplacementSculpt): raise, lower, smooth, flatten, or paint alpha. BuildScript verb: map_displacement_sculpt.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["displacementId"] = Str( "Displacement id from map_displacement_create (disp-1, ...)." ),
					["mode"] = Str( "raise | lower | smooth | flatten | paintAlpha (default raise)." ),
					["position"] = Vec3Prop( "Dab centre in world space." ),
					["radius"] = Num( "Dab radius in inches (> 0)." ),
					["strength"] = Num( "Dab strength (offset inches for raise/lower; 0..1 blend for paintAlpha)." ),
					["falloff"] = Str( "smooth | linear | sharp | constant (default smooth)." ),
					["raiseAxis"] = Str( "faceNormal | x | y | z (default faceNormal)." ),
					["paintAlphaTarget"] = Num( "PaintAlpha target 0..1 (default 1)." ),
				}, "displacementId", "position", "radius", "strength" ),
			Tool( ToolFaceMaterial,
				"Set a brush face's material and/or UV transform (FaceMaterial/FaceUv). Face is an index or \"all\". BuildScript verb: map_face_material.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["brushId"] = Str( "Brush id." ),
					["face"] = new JsonObject { ["description"] = "Face index, or \"all\" for every face." },
					["material"] = Str( "New material name." ),
					["uv"] = Obj( "UV overrides: {offset:[u,v], scale:[u,v], rotation:number, lightmapScale:number, smoothingGroup:number}." ),
				}, "brushId", "face" ),
			Tool( ToolEntityPlace,
				"Place an entity (MapEntities, registry-validated): a point entity at a position, or a brush entity tied to session brushes. Unknown class/key or mistyped value is a named error. BuildScript verb: map_entity_place.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["class"] = Str( "Entity class (info_player_start, light, func_door, ...)." ),
					["position"] = Vec3Prop( "Point-entity origin (inches, Z-up)." ),
					["angles"] = Vec3Prop( "Pitch/yaw/roll in degrees (default 0,0,0)." ),
					["keyvalues"] = Obj( "Keyvalues as string->string (registry type-checked)." ),
					["brushIds"] = Arr( "Brush-entity only: session brush ids to tie (at least one)." ),
					["origin"] = Vec3Prop( "Brush-entity origin (default 0,0,0)." ),
				}, "class" ),
			Tool( ToolEntityEdit,
				"Edit a placed entity: set keyvalues (registry type-checked), move its origin, or set its angles. BuildScript verb: map_entity_edit.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["entityId"] = Int( "Entity id from map_entity_place." ),
					["keyvalues"] = Obj( "Keyvalues to set (string->string)." ),
					["origin"] = Vec3Prop( "New origin." ),
					["angles"] = Vec3Prop( "New pitch/yaw/roll in degrees." ),
				}, "entityId" ),
			Tool( ToolIoAdd,
				"Add one entity I/O output row (EntityIoList): when `event` fires on `source`, send `input` to `target`. The target must resolve to a session entity targetname or an engine special target (!self, !player, ...). BuildScript verb: map_io_add.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["source"] = Str( "Source entity targetname, or \"#id\"." ),
					["event"] = Str( "Output event (e.g. OnTrigger)." ),
					["target"] = Str( "Target entity targetname, or an engine special target." ),
					["input"] = Str( "Input to send (e.g. Open, Toggle)." ),
					["parameter"] = Str( "Override parameter (default empty)." ),
					["delay"] = Num( "Delay in seconds (default 0)." ),
					["maxFires"] = Int( "Times to fire, -1 = forever (default -1)." ),
				}, "source", "event", "target", "input" ),
			Tool( ToolGroup,
				"Linked groups (LinkedGroupSet): define a group from session brushes/entities, place linked copies, edit a member (every copy updates), unlink one copy, or list groups. BuildScript verb: map_group.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["op"] = Str( "define | link | editMember | unlink | list." ),
					["name"] = Str( "Group name (define)." ),
					["brushIds"] = Arr( "Member brush ids (define)." ),
					["entityIds"] = Arr( "Member entity ids (define)." ),
					["layer"] = Str( "Layer for the definition / copy (\"\" = inherit)." ),
					["visGroup"] = Str( "Visgroup for the definition / copy (\"\" = inherit)." ),
					["definitionId"] = Str( "Definition id (link, editMember)." ),
					["offset"] = Vec3Prop( "Copy offset (link)." ),
					["instanceId"] = Str( "Copy id (unlink)." ),
					["memberId"] = Str( "Member id within the definition (editMember)." ),
					["edit"] = Obj( "Edit: {kind:\"move\", delta:[x,y,z]} | {kind:\"setMaterial\", value} | {kind:\"setKeyValue\", key, value} | {kind:\"rename\", value}." ),
				}, "op" ),
			Tool( ToolVisGroup,
				"Visgroups: define a named group with a colour, toggle its visibility, assign session brushes to it, or list groups. Visibility lives on the session; VMF export writes the groups. BuildScript verb: map_visgroup.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["op"] = Str( "define | setVisible | assign | list." ),
					["name"] = Str( "Visgroup name." ),
					["color"] = Str( "Colour as \"R G B\" (default \"255 255 255\")." ),
					["visible"] = Bool( "Visibility (setVisible)." ),
					["brushIds"] = Arr( "Brush ids to assign (assign)." ),
				}, "op" ),
			Tool( ToolImportExport,
				"Import or export the session as VMF or Valve 220 .map (VmfIo/Map220Io). Import takes inline text and replaces the session; export returns the text and optionally writes it under the tool work directory. Invalid brushes/entities are reported as warnings and skipped, never silently fixed. BuildScript verb: map_import_export.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["op"] = Str( "import | export." ),
					["format"] = Str( "vmf | map (default vmf)." ),
					["text"] = Str( "Map text to import (import)." ),
					["fileName"] = Str( "Optional file name (no directories) to write the export under the work directory." ),
				}, "op" ),
			Tool( ToolCompile,
				"Compile the session with the native kernel pipeline (CompileRunner: BSP -> visibility -> lightmap bake). No Hammer/VBSP/VVIS/VRAD, no shell. Presets: quick, preview, final. BuildScript verb: map_compile.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["preset"] = Str( "quick | preview | final (default quick)." ),
					["outputFolder"] = Str( "Optional folder (under the work directory) for .bsp/.pvs/.lightmap/.lin outputs and the compile log. Omit for an in-memory run." ),
				} ),
			Tool( ToolValidate,
				"Validate the session with the IssueEngine (invalid brushes, missing materials/targets, duplicate targetnames, leaks, unsealed map, limits) plus entity validation. Returns the issue list with stable codes. BuildScript verb: map_validate.",
				new JsonObject { ["map"] = mapProp.DeepClone() } ),
			Tool( ToolBakeLightmap,
				"Bake the session to a static mesh (MapBakeExport) and report face/vertex/material counts and exclusions; optionally export OBJ and/or GLB under the work directory. Displacements are listed as skipped: the kernel displacement face carries an integer material id with no name mapping yet. BuildScript verb: map_bake_lightmap.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["export"] = Str( "none | obj | glb | both (default none)." ),
					["fileName"] = Str( "Optional base file name (no directories) for the export." ),
				} ),
			Tool( ToolNavmesh,
				"Navmesh status for the session (PlaytestLoop pre-launch validation): whether a navmesh is required, which entities need it, and whether one is present. Navmesh GENERATION is not implemented in the kernel yet (no navmesh type has landed), so generation is a named not-implemented in the result. BuildScript verb: map_navmesh.",
				new JsonObject { ["map"] = mapProp.DeepClone() } ),
			Tool( ToolSkyboxCubemap,
				"Skybox for the session: set the sky name, optionally place a sky_camera, and build the SkyboxPreview scene (warnings included). Cubemap CAPTURE has no kernel builder yet, so it is a named not-implemented in the result. BuildScript verb: map_skybox_cubemap.",
				new JsonObject
				{
					["map"] = mapProp.DeepClone(),
					["skyName"] = Str( "Sky name for worldspawn skyname." ),
					["placeSkyCamera"] = Bool( "Place a sky_camera entity (default false)." ),
					["skyCameraOrigin"] = Vec3Prop( "sky_camera origin (default 0,0,0)." ),
					["skyCameraScale"] = Num( "sky_camera scale (default 16)." ),
				} ),
		};
	}

	// ---- tools/call ----

	/// <summary>MCP tools/call. Unknown tool, non-object arguments, a
	/// missing required argument, or a bad enum/number/map reference
	/// throws ArgumentException (invalid params, -32602). A well-formed
	/// call that fails returns IsError with the tool's own named message.
	/// Cancellation propagates.</summary>
	public Task<McpModelingTools.McpToolCallResult> CallAsync( string name, JsonNode? args, CancellationToken ct )
	{
		ct.ThrowIfCancellationRequested();
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Unknown map tool." );
		var argsObject = args as JsonObject ?? throw new ArgumentException( "Tool arguments must be a JSON object." );
		var def = FindDefinition( name ) ?? throw new ArgumentException( "Unknown map tool." );
		foreach ( var required in RequiredOf( def ) )
			if ( argsObject[required] is null )
				throw new ArgumentException( $"Missing required argument '{required}' for tool '{name}'." );
		try
		{
			var text = name switch
			{
				ToolBrushCreate => BrushCreate( argsObject ),
				ToolBrushEdit => BrushEdit( argsObject ),
				ToolBrushClip => BrushClip( argsObject ),
				ToolBrushVertex => BrushVertex( argsObject ),
				ToolDisplacementCreate => DisplacementCreate( argsObject ),
				ToolDisplacementSculpt => DisplacementSculpt( argsObject ),
				ToolFaceMaterial => FaceMaterialTool( argsObject ),
				ToolEntityPlace => EntityPlace( argsObject ),
				ToolEntityEdit => EntityEdit( argsObject ),
				ToolIoAdd => IoAdd( argsObject ),
				ToolGroup => GroupTool( argsObject ),
				ToolVisGroup => VisGroupTool( argsObject ),
				ToolImportExport => ImportExport( argsObject ),
				ToolCompile => CompileTool( argsObject ),
				ToolValidate => ValidateTool( argsObject ),
				ToolBakeLightmap => BakeLightmap( argsObject ),
				ToolNavmesh => NavmeshTool( argsObject ),
				ToolSkyboxCubemap => SkyboxCubemap( argsObject ),
				_ => throw new ArgumentException( "Unknown map tool." ),
			};
			return Task.FromResult( new McpModelingTools.McpToolCallResult( text, IsError: false ) );
		}
		catch ( OperationCanceledException ) { throw; }
		catch ( ArgumentOutOfRangeException e ) { return Task.FromResult(new McpModelingTools.McpToolCallResult(e.Message, IsError: true)); }
        catch ( EntityRegistryException e ) { return Task.FromResult(new McpModelingTools.McpToolCallResult(e.Code + ": " + e.Message, IsError: true)); }
		catch ( ArgumentException ) { throw; }
		catch ( Exception e ) { return Task.FromResult( new McpModelingTools.McpToolCallResult( e.Message, IsError: true ) ); }
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

	// ---- argument helpers (ArgumentException = invalid params) ----

	void CheckMap( JsonObject args )
	{
		if ( args["map"] is JsonValue mapValue && mapValue.TryGetValue<string>( out var map )
			&& !string.Equals( map, MapName, StringComparison.Ordinal ) )
			throw new ArgumentException( $"Unknown map '{map}'. This server has one open map: '{MapName}'." );
	}

	static string ReqString( JsonObject args, string name )
	{
		if ( args[name] is JsonValue v && v.TryGetValue<string>( out var s ) && !string.IsNullOrEmpty( s ) ) return s;
		throw new ArgumentException( $"Argument '{name}' must be a non-empty string." );
	}

	static string OptString( JsonObject args, string name, string fallback = "" )
		=> args[name] is JsonValue v && v.TryGetValue<string>( out var s ) ? s ?? fallback : fallback;

	static double ReqDouble( JsonObject args, string name )
	{
		if ( args[name] is JsonValue v && v.TryGetValue<double>( out var d ) && double.IsFinite( d ) ) return d;
		throw new ArgumentException( $"Argument '{name}' must be a finite number." );
	}

	static double OptDouble( JsonObject args, string name, double fallback )
		=> args[name] is JsonValue v && v.TryGetValue<double>( out var d ) && double.IsFinite( d ) ? d : fallback;

	static int ReqInt( JsonObject args, string name )
	{
		if ( args[name] is JsonValue v && v.TryGetValue<int>( out var i ) ) return i;
		if ( args[name] is JsonValue d && d.TryGetValue<double>( out var f ) && f == Math.Truncate( f ) ) return (int)f;
		throw new ArgumentException( $"Argument '{name}' must be an integer." );
	}

	static int OptInt( JsonObject args, string name, int fallback )
	{
		if ( args[name] is null ) return fallback;
		return ReqInt( args, name );
	}

	static bool OptBool( JsonObject args, string name, bool fallback )
		=> args[name] is JsonValue v && v.TryGetValue<bool>( out var b ) ? b : fallback;

	static Vec3 ReqVec3( JsonObject args, string name )
	{
		if ( args[name] is JsonArray a && a.Count == 3
			&& a[0] is JsonValue x && x.TryGetValue<double>( out var dx )
			&& a[1] is JsonValue y && y.TryGetValue<double>( out var dy )
			&& a[2] is JsonValue z && z.TryGetValue<double>( out var dz )
			&& double.IsFinite( dx ) && double.IsFinite( dy ) && double.IsFinite( dz ) )
			return new Vec3( (float)dx, (float)dy, (float)dz );
		throw new ArgumentException( $"Argument '{name}' must be [x, y, z] of finite numbers." );
	}

	static Vec3 OptVec3( JsonObject args, string name, Vec3 fallback )
		=> args[name] is null ? fallback : ReqVec3( args, name );

	static Vec2 OptVec2( JsonObject args, string name, Vec2 fallback )
	{
		if ( args[name] is not JsonArray a || a.Count != 2 ) return fallback;
		if ( a[0] is JsonValue x && x.TryGetValue<double>( out var dx )
			&& a[1] is JsonValue y && y.TryGetValue<double>( out var dy )
			&& double.IsFinite( dx ) && double.IsFinite( dy ) )
			return new Vec2( (float)dx, (float)dy );
		throw new ArgumentException( $"Argument '{name}' must be [u, v] of finite numbers." );
	}

	static string[] StringArray( JsonObject args, string name )
	{
		if ( args[name] is not JsonArray a ) return Array.Empty<string>();
		var list = new List<string>();
		foreach ( var item in a )
		{
			if ( item is not JsonValue v || !v.TryGetValue<string>( out var s ) )
				throw new ArgumentException( $"Argument '{name}' must be an array of strings." );
			list.Add( s );
		}
		return list.ToArray();
	}

	static int[] IntArray( JsonObject args, string name )
	{
		if ( args[name] is not JsonArray a ) return Array.Empty<int>();
		var list = new List<int>();
		foreach ( var item in a )
		{
			if ( item is JsonValue v && v.TryGetValue<int>( out var i ) ) list.Add( i );
			else if ( item is JsonValue d && d.TryGetValue<double>( out var f ) && f == Math.Truncate( f ) ) list.Add( (int)f );
			else throw new ArgumentException( $"Argument '{name}' must be an array of integers." );
		}
		return list.ToArray();
	}

	static Dictionary<string, string> StringMap( JsonObject args, string name )
	{
		var map = new Dictionary<string, string>( StringComparer.Ordinal );
		if ( args[name] is not JsonObject obj ) return map;
		foreach ( var pair in obj )
		{
			if ( pair.Value is not JsonValue v || !v.TryGetValue<string>( out var s ) )
				throw new ArgumentException( $"Argument '{name}' must be an object of strings." );
			map[pair.Key] = s;
		}
		return map;
	}

	static Vec3 Vec3Of( JsonArray a, string what )
	{
		if ( a.Count == 3
			&& a[0] is JsonValue x && x.TryGetValue<double>( out var dx )
			&& a[1] is JsonValue y && y.TryGetValue<double>( out var dy )
			&& a[2] is JsonValue z && z.TryGetValue<double>( out var dz )
			&& double.IsFinite( dx ) && double.IsFinite( dy ) && double.IsFinite( dz ) )
			return new Vec3( (float)dx, (float)dy, (float)dz );
		throw new ArgumentException( $"{what} must be [x, y, z] of finite numbers." );
	}

	static string Err( string code, string message ) => throw new InvalidOperationException(code + ": " + message);

	static JsonObject Ok( string tool, params (string Key, JsonNode? Value)[] fields )
	{
		var obj = new JsonObject { ["tool"] = tool, ["map"] = MapName };
		foreach ( var (key, value) in fields ) obj[key] = value;
		return obj;
	}

	static JsonNode? J( string? v ) => v is null ? null : JsonValue.Create( v );
	static JsonNode? J( double v ) => JsonValue.Create( v );
	static JsonNode? J( float v ) => JsonValue.Create( (double)v );
	static JsonNode? J( int v ) => JsonValue.Create( v );
	static JsonNode? J( bool v ) => JsonValue.Create( v );
	static JsonNode? J( Vec3 v ) => new JsonArray( (double)v.x, (double)v.y, (double)v.z );
	static JsonNode? J( Vec2 v ) => new JsonArray( (double)v.x, (double)v.y );

	// ---- brushes ----

	string BrushCreate( JsonObject args )
	{
		CheckMap( args );
		var shape = ReqString( args, "shape" ).ToLowerInvariant();
		var material = OptString( args, "material", DefaultMaterial );
		if ( string.IsNullOrWhiteSpace( material ) ) throw new ArgumentException( "Argument 'material' must not be blank." );
		var position = OptVec3( args, "position", Vec3.Zero );
		MapBrush brush;
		switch ( shape )
		{
			case "box":
			{
				Vec3 min, max;
				if ( args["min"] is not null && args["max"] is not null ) { min = ReqVec3( args, "min" ); max = ReqVec3( args, "max" ); }
				else if ( args["center"] is not null && args["size"] is not null )
				{
					var c = ReqVec3( args, "center" );
					var s = ReqVec3( args, "size" );
					min = c - s * 0.5f; max = c + s * 0.5f;
				}
				else throw new ArgumentException( "A box needs min+max or center+size." );
				brush = MapBrush.Box( min, max, material );
				break;
			}
			case "planes":
			{
				if ( args["faces"] is not JsonArray faces || faces.Count == 0 )
					throw new ArgumentException( "Planes shape needs a non-empty 'faces' array." );
				var list = new List<MapBrushFace>();
				foreach ( var item in faces )
				{
					if ( item is not JsonObject f ) throw new ArgumentException( "Each plane face needs {normal:[x,y,z], distance:number}." );
					var n = ReqVec3( f, "normal" );
					var d = (float)ReqDouble( f, "distance" );
					var faceMaterial = OptString( f, "material", material );
					var uv = ReadUv( f["uv"] as JsonObject, FaceUv.Default ) ?? FaceUv.Default;
					list.Add( new MapBrushFace( n, d, faceMaterial, uv ) );
				}
				brush = MapBrush.FromPlanes( list );
				break;
			}
			case "wedge":
			{
				var size = ReqVec3( args, "size" );
				RequirePositive( size, "size" );
				brush = FromPlanesTranslated( BuildWedge( size, material ), position );
				break;
			}
			case "cylinder":
			{
				var radius = (float)ReqDouble( args, "radius" );
				var height = (float)ReqDouble( args, "height" );
				var segments = OptInt( args, "segments", 16 );
				brush = FromPlanesTranslated( BuildCylinder( radius, height, segments, material ), position );
				break;
			}
			case "sphere":
			{
				var radius = (float)ReqDouble( args, "radius" );
				var divisions = OptInt( args, "divisions", 4 );
				var segments = OptInt( args, "segments", 16 );
				brush = FromPlanesTranslated( BuildSphere( radius, divisions, segments, material ), position );
				break;
			}
			case "arch":
			{
				var radius = (float)ReqDouble( args, "radius" );
				var height = (float)ReqDouble( args, "height" );
				var sweep = (float)OptDouble( args, "sweepDegrees", 180.0 );
				var segments = OptInt( args, "segments", 8 );
				brush = FromPlanesTranslated( BuildArch( radius, height, sweep, segments, material ), position );
				break;
			}
			default:
				throw new ArgumentException( $"Unknown brush shape '{shape}'. Known shapes: box, wedge, cylinder, sphere, arch, planes." );
		}
		var defects = brush.Validate();
		if ( defects.Count > 0 )
			return Err( "brush-invalid", string.Join( "; ", defects.Select( d => d.ToString() ) ) );
		var id = AddBrush( brush, OptString( args, "name", "" ) );
		if ( OptBool( args, "detail", false ) ) _detailBrushes.Add( id );
		return Ok( ToolBrushCreate,
			("brushId", J( id )),
			("shape", J( shape )),
			("faces", J( brush.FaceCount )),
			("volume", J( brush.Volume )),
			("centroid", J( brush.Centroid )),
			("valid", J( true )),
			("detail", J( _detailBrushes.Contains( id ) )) ).ToJsonString();
	}

	string AddBrush( MapBrush brush, string name )
	{
		var id = "brush-" + _nextBrush;
		var number = _nextBrush;
		_nextBrush++;
		_brushes[id] = brush;
		_brushOrder.Add( id );
		_brushNames[id] = string.IsNullOrWhiteSpace( name ) ? id : name;
		_brushNumbers[id] = number;
		return id;
	}

	MapBrush RequireBrush( JsonObject args )
	{
		var id = ReqString( args, "brushId" );
		return FindBrush( id ) ?? throw new InvalidOperationException( Err( "brush-not-found", $"No brush '{id}' in map '{MapName}'." ) );
	}

	string BrushIdOf( JsonObject args ) => ReqString( args, "brushId" );

	string BrushEdit( JsonObject args )
	{
		CheckMap( args );
		var id = BrushIdOf( args );
		var brush = RequireBrush( args );
		var op = ReqString( args, "op" ).ToLowerInvariant();
		MapBrush next;
		switch ( op )
		{
			case "translate":
			{
				var delta = ReqVec3( args, "delta" );
				next = MapBrush.FromPlanes( brush.Faces.Select( f =>
					new MapBrushFace( f.Normal, f.Distance + Vec3.Dot( f.Normal, delta ), f.Material ) ) );
				break;
			}
			case "setmaterial":
			{
				var material = ReqString( args, "material" );
				var faces = TargetFaces( args, brush.FaceCount );
				next = MapBrush.FromPlanes( brush.Faces.Select( ( f, i ) =>
					faces.Contains( i ) ? new MapBrushFace( f.Normal, f.Distance, new FaceMaterial( material, f.Uv ) ) : f ) );
				break;
			}
			case "setuv":
			{
				if ( args["uv"] is not JsonObject uvObj ) throw new ArgumentException( "setUv needs a 'uv' object." );
				var faces = TargetFaces( args, brush.FaceCount );
				// Merge per face: omitted UV components keep the face's
				// own values, they do not reset to defaults.
				next = MapBrush.FromPlanes( brush.Faces.Select( ( f, i ) =>
					faces.Contains( i ) ? new MapBrushFace( f.Normal, f.Distance, new FaceMaterial( f.MaterialId, ReadUv( uvObj, f.Uv ) ?? f.Uv ) ) : f ) );
				break;
			}
			default:
				throw new ArgumentException( $"Unknown brush edit op '{op}'. Known ops: translate, setMaterial, setUv." );
		}
		var defects = next.Validate();
		if ( defects.Count > 0 )
			return Err( "brush-invalid", string.Join( "; ", defects.Select( d => d.ToString() ) ) );
		_brushes[id] = next;
		return BrushJson( ToolBrushEdit, id, next ).ToJsonString();
	}

	/// <summary>Face selector: an index, or "all".</summary>
	static HashSet<int> TargetFaces( JsonObject args, int faceCount )
	{
		if ( args["face"] is JsonValue s && s.TryGetValue<string>( out var str ) && str == "all" )
			return new HashSet<int>( Enumerable.Range( 0, faceCount ) );
		if ( args["face"] is JsonValue v && v.TryGetValue<int>( out var i ) )
		{
			if ( i < 0 || i >= faceCount ) throw new ArgumentException( $"Face {i} is outside 0..{faceCount - 1}." );
			return new HashSet<int> { i };
		}
		throw new ArgumentException( "Argument 'face' must be a face index or \"all\"." );
	}

	/// <summary>UV overrides merged over a base UV; null when the caller
	/// passed no uv object at all.</summary>
	static FaceUv? ReadUv( JsonObject? uv, FaceUv? fallback )
	{
		if ( uv is null ) return fallback;
		var offset = OptVec2( uv, "offset", fallback?.Offset ?? FaceUv.Default.Offset );
		var scale = OptVec2( uv, "scale", fallback?.Scale ?? FaceUv.Default.Scale );
		var rotation = (float)OptDouble( uv, "rotation", fallback?.RotationDegrees ?? 0f );
		var lightmap = (float)OptDouble( uv, "lightmapScale", fallback?.LightmapScale ?? FaceUv.Default.LightmapScale );
		var smoothing = OptInt( uv, "smoothingGroup", fallback?.SmoothingGroup ?? 0 );
		return new FaceUv( offset, scale, rotation, lightmap, smoothing );
	}


	string BrushClip( JsonObject args )
	{
		CheckMap( args );
		var id = BrushIdOf( args );
		var brush = RequireBrush( args );
		var a = ReqVec3( args, "a" );
		var b = ReqVec3( args, "b" );
		var axis = OptString( args, "viewAxis", "z" ).ToLowerInvariant() switch
		{
			"x" => ClipViewAxis.X,
			"y" => ClipViewAxis.Y,
			"z" => ClipViewAxis.Z,
			var other => throw new ArgumentException( $"Unknown viewAxis '{other}'. Known axes: x, y, z." ),
		};
		var options = new MapClipOptions
		{
			Mode = OptString( args, "mode", "cut" ).ToLowerInvariant() switch
			{
				"cut" => ClipMode.Cut,
				"split" => ClipMode.Split,
				var other => throw new ArgumentException( $"Unknown clip mode '{other}'. Known modes: cut, split." ),
			},
			KeepFront = OptBool( args, "keepFront", true ),
			CutFaceNoDraw = OptBool( args, "cutFaceNoDraw", true ),
			CutFaceMaterial = args["cutFaceMaterial"] is JsonValue cv && cv.TryGetValue<string>( out var cm ) ? cm : null,
		};
		var result = MapClip.Clip( brush, a, b, axis, options );
		if ( result.Outcome == ClipOutcome.RejectedInvalidBrush )
			return Err( "brush-invalid", result.Reason ?? "invalid brush" );
		string? secondId = null;
		switch ( result.Outcome )
		{
			case ClipOutcome.Cut:
			case ClipOutcome.Split:
				_brushes[id] = result.Kept!;
				if ( result.Second is not null )
				{
					secondId = AddBrush( result.Second, BrushDisplayName( id ) + "-split" );
					if ( _detailBrushes.Contains( id ) ) _detailBrushes.Add( secondId );
				}
				break;
			case ClipOutcome.WholeBrushCutAway:
				_brushes.Remove( id );
				_brushOrder.Remove( id );
				_brushNames.Remove( id );
				_detailBrushes.Remove( id );
				_brushVisGroup.Remove( id );
				break;
		}
		var obj = Ok( ToolBrushClip,
			("brushId", J( id )),
			("outcome", J( result.Outcome.ToString() )),
			("changed", J( result.Changed )),
			("reason", J( result.Reason )),
			("secondBrushId", J( secondId )) );
		if ( result.Kept is not null )
		{
			obj["faces"] = J( result.Kept.FaceCount );
			obj["volume"] = J( result.Kept.Volume );
		}
		if ( result.Second is not null ) obj["secondVolume"] = J( result.Second.Volume );
		return obj.ToJsonString();
	}

	string BrushVertex( JsonObject args )
	{
		CheckMap( args );
		var id = BrushIdOf( args );
		var brush = RequireBrush( args );
		if ( !brush.IsValid )
			return Err( "brush-invalid", string.Join( "; ", brush.Validate().Select( d => d.ToString() ) ) );
		var vertex = ReqInt( args, "vertex" );
		// MapBrush -> MapVertexBrush via the baked mesh (face order is
		// preserved by ToMesh, and the brush is valid so no face is
		// dropped); materials travel by slot.
		var materialNames = new List<string>();
		var slotOf = new Dictionary<string, int>( StringComparer.Ordinal );
		int Slot( string name )
		{
			if ( !slotOf.TryGetValue( name, out var slot ) ) { slot = materialNames.Count; slotOf[name] = slot; materialNames.Add( name ); }
			return slot;
		}
		var mesh = brush.ToMesh( Slot );
		var vertexBrush = new MapVertexBrush(
			mesh.Positions.ToArray(),
			mesh.Faces.Select( f => (int[])f.Indices.Clone() ).ToArray(),
			mesh.Faces.Select( f => materialNames[f.Material] ).ToArray() );
		MapVertexEditResult moved;
		if ( args["to"] is not null ) moved = MapVertexEdit.MoveVertex( vertexBrush, vertex, ReqVec3( args, "to" ) );
		else if ( args["delta"] is not null ) moved = MapVertexEdit.MoveVertexBy( vertexBrush, vertex, ReqVec3( args, "delta" ) );
		else throw new ArgumentException( "map_brush_vertex needs 'to' or 'delta'." );
		var edited = moved.Brush;
		var rebuilt = MapBrush.FromPlanes( edited.Faces.Select( ( loop, i ) =>
		{
			var (normal, distance) = edited.FacePlane( i );
			return new MapBrushFace( normal, distance, edited.Materials[i] );
		} ) );
		_brushes[id] = rebuilt;
		var defects = moved.Validity?.Defects ?? new List<string>();
		var obj = Ok( ToolBrushVertex,
			("brushId", J( id )),
			("vertex", J( vertex )),
			("affectedFaces", new JsonArray( moved.AffectedFaces.Select( f => (JsonNode?)f ).ToArray() ) ),
			("valid", J( moved.IsValid )),
			("invalidFaces", new JsonArray( moved.InvalidFaceIndices.Select( f => (JsonNode?)f ).ToArray() ) ),
			("defects", new JsonArray( defects.Select( d => (JsonNode?)d ).ToArray() ) ),
			("volume", J( rebuilt.Volume )),
			("centroid", J( rebuilt.Centroid )) );
		return obj.ToJsonString();
	}

	JsonObject BrushJson( string tool, string id, MapBrush brush ) => Ok( tool,
		("brushId", J( id )),
		("faces", J( brush.FaceCount )),
		("volume", J( brush.Volume )),
		("centroid", J( brush.Centroid )),
		("valid", J( brush.IsValid )),
		("materials", new JsonArray( brush.Faces.Select( f => (JsonNode?)f.MaterialId ).ToArray() )) );

	// ---- plane builders for the prism shapes ----
	//
	// MapBrushPrimitives cannot be called: it references MapFace/MapPlane
	// types that do not exist against the landed MapBrush. These builders
	// emit the same plane sets (same conventions: centred on the origin
	// before Position, tangent planes for cylinder/sphere, slope plane
	// for the wedge, sector planes for the arch) directly as
	// Effigy.MapBrushFace values, until that file is reconciled.

	static void RequirePositive( Vec3 size, string name )
	{
		if ( !( size.x > 0f ) || !( size.y > 0f ) || !( size.z > 0f ) || !float.IsFinite( size.x ) || !float.IsFinite( size.y ) || !float.IsFinite( size.z ) )
			throw new ArgumentException( $"Argument '{name}' needs three positive finite components." );
	}

	static MapBrush FromPlanesTranslated( IReadOnlyList<MapBrushFace> faces, Vec3 position )
		=> MapBrush.FromPlanes( faces.Select( f => new MapBrushFace( f.Normal, f.Distance + Vec3.Dot( f.Normal, position ), f.Material ) ) );

	static MapBrushFace Plane( Vec3 normal, float distance, string material )
		=> new( normal, distance, new FaceMaterial( material ) );

	static IReadOnlyList<MapBrushFace> BuildWedge( Vec3 size, string material )
	{
		var hx = size.x * 0.5f;
		var hy = size.y * 0.5f;
		var hz = size.z * 0.5f;
		var slope = new Vec3( hz, 0, hx ).Normal;
		return new[]
		{
			Plane( new Vec3( 0, 0, -1 ), hz, material ),
			Plane( new Vec3( -1, 0, 0 ), hx, material ),
			Plane( slope, 0f, material ),
			Plane( new Vec3( 0, -1, 0 ), hy, material ),
			Plane( new Vec3( 0, 1, 0 ), hy, material ),
		};
	}

	static IReadOnlyList<MapBrushFace> BuildCylinder( float radius, float height, int segments, string material )
	{
		if ( !( radius > 0f ) || !float.IsFinite( radius ) ) throw new ArgumentException( "Cylinder radius must be positive and finite." );
		if ( !( height > 0f ) || !float.IsFinite( height ) ) throw new ArgumentException( "Cylinder height must be positive and finite." );
		if ( segments < 3 || segments > 512 ) throw new ArgumentOutOfRangeException( nameof( segments ), segments, "A brush cylinder needs 3..512 sides." );
		var hz = height * 0.5f;
		var faces = new List<MapBrushFace>( segments + 2 );
		for ( var i = 0; i < segments; i++ )
		{
			var a = i / (float)segments * MathF.Tau;
			faces.Add( Plane( new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 ), radius, material ) );
		}
		faces.Add( Plane( new Vec3( 0, 0, 1 ), hz, material ) );
		faces.Add( Plane( new Vec3( 0, 0, -1 ), hz, material ) );
		return faces;
	}

	static IReadOnlyList<MapBrushFace> BuildSphere( float radius, int divisions, int segments, string material )
	{
		if ( !( radius > 0f ) || !float.IsFinite( radius ) ) throw new ArgumentException( "Sphere radius must be positive and finite." );
		if ( divisions < 2 || divisions > 64 ) throw new ArgumentOutOfRangeException( nameof( divisions ), divisions, "A brush sphere needs 2..64 latitude bands." );
		if ( segments < 3 || segments > 512 ) throw new ArgumentOutOfRangeException( nameof( segments ), segments, "A brush sphere needs 3..512 facets around." );
		var faces = new List<MapBrushFace>( 2 + ( divisions - 1 ) * segments );
		faces.Add( Plane( new Vec3( 0, 0, 1 ), radius, material ) );
		faces.Add( Plane( new Vec3( 0, 0, -1 ), radius, material ) );
		for ( var k = 1; k < divisions; k++ )
		{
			var lat = ( -90f + 180f * k / divisions ) * MathF.PI / 180f;
			var cosLat = MathF.Cos( lat );
			var sinLat = MathF.Sin( lat );
			for ( var j = 0; j < segments; j++ )
			{
				var az = j / (float)segments * MathF.Tau;
				faces.Add( Plane( new Vec3( cosLat * MathF.Cos( az ), cosLat * MathF.Sin( az ), sinLat ), radius, material ) );
			}
		}
		return faces;
	}

	static IReadOnlyList<MapBrushFace> BuildArch( float outerRadius, float height, float sweepDegrees, int segments, string material )
	{
		if ( !( outerRadius > 0f ) || !float.IsFinite( outerRadius ) ) throw new ArgumentException( "Arch outerRadius must be positive and finite." );
		if ( !( height > 0f ) || !float.IsFinite( height ) ) throw new ArgumentException( "Arch height must be positive and finite." );
		if ( !( sweepDegrees > 0f ) || sweepDegrees > 180f || !float.IsFinite( sweepDegrees ) )
			throw new ArgumentOutOfRangeException( nameof( sweepDegrees ), sweepDegrees, "An arch sweep must be in (0, 180] degrees." );
		if ( segments < 2 || segments > 512 ) throw new ArgumentOutOfRangeException( nameof( segments ), segments, "An arch needs 2..512 outer facets." );
		var hz = height * 0.5f;
		var half = sweepDegrees * 0.5f * MathF.PI / 180f;
		var faces = new List<MapBrushFace>( segments + 4 );
		for ( var j = 0; j < segments; j++ )
		{
			var a = segments == 1 ? 0f : -half + 2f * half * j / ( segments - 1 );
			faces.Add( Plane( new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 ), outerRadius, material ) );
		}
		// Radial end planes through the Z axis. At exactly 180 degrees the
		// two ends are one diameter plane — emit it once, or the brush
		// would carry a duplicate face.
		if ( MathF.Abs( sweepDegrees - 180f ) < 1e-6f )
			faces.Add( Plane( new Vec3( -1, 0, 0 ), 0f, material ) );
		else
		{
			faces.Add( Plane( new Vec3( -MathF.Sin( half ), MathF.Cos( half ), 0 ), 0f, material ) );
			faces.Add( Plane( new Vec3( -MathF.Sin( half ), -MathF.Cos( half ), 0 ), 0f, material ) );
		}
		faces.Add( Plane( new Vec3( 0, 0, 1 ), hz, material ) );
		faces.Add( Plane( new Vec3( 0, 0, -1 ), hz, material ) );
		return faces;
	}

	// ---- displacements + face material ----

	string DisplacementCreate( JsonObject args )
	{
		CheckMap( args );
		if ( args["corners"] is not JsonArray corners || corners.Count != 4 )
			throw new ArgumentException( "map_displacement_create needs 'corners': four [x,y,z] points." );
		var points = new Vec3[4];
		for ( var i = 0; i < 4; i++ )
		{
			if ( corners[i] is not JsonArray p )
				throw new ArgumentException( $"Corner {i} must be [x, y, z]." );
			points[i] = Vec3Of( p, $"Corner {i}" );
		}
		var power = OptInt( args, "power", 3 );
		var materialId = OptInt( args, "materialId", 0 );
		// The kernel displacement binds Effigy.Map.MapBrushFace (4 stored
		// corners), not the brush core's Effigy.MapBrushFace (planes only):
		// building one from a brush face's solved polygon belongs to the
		// MapDocument integrator, so this tool takes explicit corners.
		var face = MapKit.MapBrushFace.FromCorners( points, materialId );
		var disp = MapKit.Displacement.Create( face, power );
		var id = "disp-" + _nextDisplacement++;
		_displacements[id] = disp;
		if ( args["brushId"] is JsonValue bv && bv.TryGetValue<string>( out var brushId ) && !string.IsNullOrEmpty( brushId ) )
		{
			if ( FindBrush( brushId ) is null )
				return Err( "brush-not-found", $"No brush '{brushId}' in map '{MapName}'." );
			_displacementBrush[id] = brushId;
		}
		return Ok( ToolDisplacementCreate,
			("displacementId", J( id )),
			("power", J( disp.Power )),
			("gridSize", J( disp.GridSize )),
			("vertexCount", J( disp.VertexCount )),
			("valid", J( disp.IsValid )),
			("sealsMap", J( disp.SealsMap )),
			("brushId", J( _displacementBrush.TryGetValue( id, out var b ) ? b : null )) ).ToJsonString();
	}

	string DisplacementSculpt( JsonObject args )
	{
		CheckMap( args );
		var id = ReqString( args, "displacementId" );
		var disp = FindDisplacement( id ) ?? throw new InvalidOperationException( Err( "displacement-not-found", $"No displacement '{id}' in map '{MapName}'." ) );
		var mode = OptString( args, "mode", "raise" ).ToLowerInvariant() switch
		{
			"raise" => MapKit.DisplacementSculptMode.Raise,
			"lower" => MapKit.DisplacementSculptMode.Lower,
			"smooth" => MapKit.DisplacementSculptMode.Smooth,
			"flatten" => MapKit.DisplacementSculptMode.Flatten,
			"paintalpha" => MapKit.DisplacementSculptMode.PaintAlpha,
			var other => throw new ArgumentException( $"Unknown sculpt mode '{other}'. Known modes: raise, lower, smooth, flatten, paintAlpha." ),
		};
		var falloff = OptString( args, "falloff", "smooth" ).ToLowerInvariant() switch
		{
			"smooth" => BrushFalloff.Smooth,
			"linear" => BrushFalloff.Linear,
			"sharp" => BrushFalloff.Sharp,
			"constant" => BrushFalloff.Constant,
			var other => throw new ArgumentException( $"Unknown falloff '{other}'. Known falloffs: smooth, linear, sharp, constant." ),
		};
		var options = new MapKit.DisplacementSculptOptions
		{
			RaiseAxis = OptString( args, "raiseAxis", "faceNormal" ).ToLowerInvariant() switch
			{
				"facenormal" => MapKit.DisplacementRaiseAxis.FaceNormal,
				"x" => MapKit.DisplacementRaiseAxis.X,
				"y" => MapKit.DisplacementRaiseAxis.Y,
				"z" => MapKit.DisplacementRaiseAxis.Z,
				var other => throw new ArgumentException( $"Unknown raiseAxis '{other}'. Known axes: faceNormal, x, y, z." ),
			},
			PaintAlphaTarget = (float)OptDouble( args, "paintAlphaTarget", 1.0 ),
		};
		var sample = new BrushSample( ReqVec3( args, "position" ), disp.Normal, (float)ReqDouble( args, "radius" ), (float)ReqDouble( args, "strength" ) );
		var stamp = new BrushAlpha { Values = new[] { 1f }, Width = 1, Height = 1 };
		var result = MapKit.DisplacementSculpt.ApplyDab( disp, sample, mode, falloff, stamp, options );
		return Ok( ToolDisplacementSculpt,
			("displacementId", J( id )),
			("mode", J( OptString( args, "mode", "raise" ) )),
			("verticesTouched", J( result.VerticesTouched )),
			("maxOffsetDelta", J( result.MaxOffsetDelta )),
			("maxAlphaDelta", J( result.MaxAlphaDelta )) ).ToJsonString();
	}

	string FaceMaterialTool( JsonObject args )
	{
		CheckMap( args );
		var id = BrushIdOf( args );
		var brush = RequireBrush( args );
		var faces = TargetFaces( args, brush.FaceCount );
		var material = args["material"] is JsonValue mv && mv.TryGetValue<string>( out var m ) ? m : null;
		if ( material is not null && string.IsNullOrWhiteSpace( material ) )
			throw new ArgumentException( "Argument 'material' must not be blank." );
		var uvPatch = args["uv"] as JsonObject;
		if ( material is null && uvPatch is null )
			throw new ArgumentException( "map_face_material needs 'material' and/or 'uv'." );
		var next = MapBrush.FromPlanes( brush.Faces.Select( ( f, i ) =>
		{
			if ( !faces.Contains( i ) ) return f;
			var uv = uvPatch is null ? f.Uv : ( ReadUv( uvPatch, f.Uv ) ?? f.Uv );
			foreach ( var defect in uv.Validate() )
				throw new InvalidOperationException( Err( "invalid-uv", $"Face {i}: {defect}" ) );
			return new MapBrushFace( f.Normal, f.Distance, new FaceMaterial( material ?? f.MaterialId, uv ) );
		} ) );
		var defects = next.Validate();
		if ( defects.Count > 0 )
			return Err( "brush-invalid", string.Join( "; ", defects.Select( d => d.ToString() ) ) );
		_brushes[id] = next;
		var first = faces.Min();
		return Ok( ToolFaceMaterial,
			("brushId", J( id )),
			("face", J( first )),
			("facesChanged", J( faces.Count )),
			("material", J( next.Faces[first].MaterialId )),
			("uv", J( next.Faces[first].Uv.Offset ) ),
			("volume", J( next.Volume )),
			("valid", J( true )) ).ToJsonString();
	}

	// ---- entities + I/O ----

	string EntityPlace( JsonObject args )
	{
		CheckMap( args );
		var className = ReqString( args, "class" );
		var keyvalues = StringMap( args, "keyvalues" );
		var angles = OptVec3( args, "angles", Vec3.Zero );
		MapEntity entity;
		if ( args["brushIds"] is JsonArray )
		{
			var brushIds = StringArray( args, "brushIds" );
			if ( brushIds.Length == 0 ) throw new ArgumentException( "A brush entity needs at least one 'brushIds' entry." );
			var brushes = new List<MapBrush>();
			foreach ( var brushId in brushIds )
				brushes.Add( FindBrush( brushId ) ?? throw new InvalidOperationException( Err( "brush-not-found", $"No brush '{brushId}' in map '{MapName}'." ) ) );
			var origin = OptVec3( args, "origin", Vec3.Zero );
			entity = _entities.PlaceBrushEntity( className, brushes, keyvalues, origin );
			_entityBrushIds[entity.Id] = new List<string>( brushIds );
		}
		else
		{
			var position = ReqVec3( args, "position" );
			entity = _entities.PlacePointEntity( className, position, keyvalues, angles );
		}
		return Ok( ToolEntityPlace,
			("entityId", J( entity.Id )),
			("class", J( entity.ClassName )),
			("kind", J( entity.Kind.ToString() )),
			("origin", J( entity.Origin )),
			("targetname", J( entity.TargetName )) ).ToJsonString();
	}

	string EntityEdit( JsonObject args )
	{
		CheckMap( args );
		var entityId = ReqInt( args, "entityId" );
		var entity = FindEntity( entityId ) ?? throw new InvalidOperationException( Err( "entity-not-found", $"No entity #{entityId} in map '{MapName}'." ) );
		foreach ( var pair in StringMap( args, "keyvalues" ) )
			_entities.SetKeyvalue( entity, pair.Key, pair.Value );
		if ( args["origin"] is not null ) _entities.SetOrigin( entity, ReqVec3( args, "origin" ) );
		if ( args["angles"] is not null ) _entities.SetAngles( entity, ReqVec3( args, "angles" ) );
		var kvs = new JsonObject();
		foreach ( var pair in entity.Keyvalues ) kvs[pair.Key] = pair.Value;
		return Ok( ToolEntityEdit,
			("entityId", J( entity.Id )),
			("class", J( entity.ClassName )),
			("origin", J( entity.Origin )),
			("angles", J( entity.Angles )),
			("targetname", J( entity.TargetName )),
			("keyvalues", kvs ) ).ToJsonString();
	}

	MapEntity? FindEntityByReference( string reference )
	{
		if ( reference.StartsWith( "#", StringComparison.Ordinal ) && int.TryParse( reference[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id ) )
			return FindEntity( id );
		var byName = _entities.FindByTargetName( reference );
		return byName.Count == 1 ? byName[0] : null;
	}

	string IoAdd( JsonObject args )
	{
		CheckMap( args );
		var sourceRef = ReqString( args, "source" );
		var source = FindEntityByReference( sourceRef )
			?? throw new InvalidOperationException( Err( "io-source-not-found", $"No entity '{sourceRef}' in map '{MapName}'." ) );
		var target = ReqString( args, "target" );
		var targetIsSpecial = target.StartsWith( "!", StringComparison.Ordinal );
		if ( !targetIsSpecial && _entities.FindByTargetName( target ).Count == 0 )
			return Err( "io-target-not-found", $"No entity with targetname '{target}' in map '{MapName}'." );
		var sourceName = string.IsNullOrEmpty( source.TargetName ) ? "#" + source.Id : source.TargetName;
		var output = new EntityIoOutput(
			sourceName, ReqString( args, "event" ), target, ReqString( args, "input" ),
			OptDouble( args, "delay", 0.0 ), OptInt( args, "maxFires", -1 ),
			OptString( args, "parameter", "" ) );
		var error = _io.Add( output );
		if ( error is not null ) return Err( "io-invalid", error );
		return Ok( ToolIoAdd,
			("source", J( sourceName )),
			("sourceEntityId", J( source.Id )),
			("event", J( output.Event )),
			("target", J( output.TargetName )),
			("input", J( output.Input )),
			("delay", J( output.DelaySeconds )),
			("maxFires", J( output.MaxFires )),
			("resolved", J( true )),
			("outputs", J( _io.Count )) ).ToJsonString();
	}

	// ---- groups + visgroups ----

	string GroupTool( JsonObject args )
	{
		CheckMap( args );
		var op = ReqString( args, "op" ).ToLowerInvariant();
		switch ( op )
		{
			case "define":
			{
				var name = ReqString( args, "name" );
				var members = new List<MapKit.LinkedGroupMember>();
				foreach ( var brushId in StringArray( args, "brushIds" ) )
				{
					var brush = FindBrush( brushId ) ?? throw new InvalidOperationException( Err( "brush-not-found", $"No brush '{brushId}' in map '{MapName}'." ) );
					members.Add( new MapKit.LinkedGroupMember
					{
						Id = brushId,
						Kind = MapKit.LinkedGroupMemberKind.Brush,
						Name = BrushDisplayName( brushId ),
						Material = brush.Faces.Count > 0 ? brush.Faces[0].MaterialId : "",
					} );
					members[^1].Vertices.AddRange( brush.Vertices );
				}
				foreach ( var entityId in IntArray( args, "entityIds" ) )
				{
					var entity = FindEntity( entityId ) ?? throw new InvalidOperationException( Err( "entity-not-found", $"No entity #{entityId} in map '{MapName}'." ) );
					var member = new MapKit.LinkedGroupMember
					{
						Id = "ent-" + entity.Id,
						Kind = MapKit.LinkedGroupMemberKind.Entity,
						Name = entity.ClassName,
						Origin = entity.Origin,
					};
					member.KeyValues["classname"] = entity.ClassName;
					if ( !string.IsNullOrEmpty( entity.TargetName ) ) member.KeyValues["targetname"] = entity.TargetName;
					foreach ( var pair in entity.Keyvalues ) member.KeyValues[pair.Key] = pair.Value;
					members.Add( member );
				}
				var defined = _groups.DefineLinkedGroup( name, members, OptString( args, "layer", "" ), OptString( args, "visGroup", "" ) );
				if ( !defined.Ok ) return Err( "group-invalid", defined.Message );
				return Ok( ToolGroup,
					("op", J( "define" )),
					("definitionId", J( defined.Definition!.Id )),
					("name", J( defined.Definition.Name )),
					("members", J( defined.Definition.Members.Count )) ).ToJsonString();
			}
			case "link":
			{
				var definitionId = ReqString( args, "definitionId" );
				var linked = _groups.LinkCopy( definitionId, OptVec3( args, "offset", Vec3.Zero ), OptString( args, "layer", "" ), OptString( args, "visGroup", "" ) );
				if ( !linked.Ok ) return Err( "group-invalid", linked.Message );
				return Ok( ToolGroup,
					("op", J( "link" )),
					("instanceId", J( linked.Instance!.Id )),
					("definitionId", J( linked.Instance.DefinitionId )),
					("offset", J( linked.Instance.Offset )) ).ToJsonString();
			}
			case "editmember":
			{
				var definitionId = ReqString( args, "definitionId" );
				var memberId = ReqString( args, "memberId" );
				if ( args["edit"] is not JsonObject edit ) throw new ArgumentException( "editMember needs an 'edit' object." );
				var kind = ReqString( edit, "kind" ).ToLowerInvariant();
				MapKit.LinkedMemberEdit memberEdit = kind switch
				{
					"move" => MapKit.LinkedMemberEdit.Move( ReqVec3( edit, "delta" ) ),
					"setmaterial" => MapKit.LinkedMemberEdit.SetMaterial( ReqString( edit, "value" ) ),
					"setkeyvalue" => MapKit.LinkedMemberEdit.SetKeyValue( ReqString( edit, "key" ), ReqString( edit, "value" ) ),
					"rename" => MapKit.LinkedMemberEdit.Rename( ReqString( edit, "value" ) ),
					_ => throw new ArgumentException( $"Unknown member edit kind '{kind}'. Known kinds: move, setMaterial, setKeyValue, rename." ),
				};
				var edited = _groups.EditMember( definitionId, memberId, memberEdit );
				if ( !edited.Ok ) return Err( "group-invalid", edited.Message );
				return Ok( ToolGroup,
					("op", J( "editMember" )),
					("definitionId", J( definitionId )),
					("memberId", J( memberId )),
					("affectedInstances", J( edited.AffectedInstances )),
					("affectedInstanceIds", new JsonArray( edited.AffectedInstanceIds.Select( i => (JsonNode?)i ).ToArray() )) ).ToJsonString();
			}
			case "unlink":
			{
				var instanceId = ReqString( args, "instanceId" );
				var unlinked = _groups.Unlink( instanceId );
				if ( !unlinked.Ok ) return Err( "group-invalid", unlinked.Message );
				return Ok( ToolGroup,
					("op", J( "unlink" )),
					("instanceId", J( instanceId )),
					("groupId", J( unlinked.Group!.Id )),
					("members", J( unlinked.Group.Members.Count )) ).ToJsonString();
			}
			case "list":
			{
				var defs = new JsonArray();
				foreach ( var d in _groups.Definitions )
					defs.Add( new JsonObject { ["id"] = d.Id, ["name"] = d.Name, ["members"] = d.Members.Count, ["layer"] = d.Layer, ["visGroup"] = d.VisGroup } );
				var insts = new JsonArray();
				foreach ( var i in _groups.Instances )
					insts.Add( new JsonObject { ["id"] = i.Id, ["definitionId"] = i.DefinitionId, ["offset"] = J( i.Offset ), ["layer"] = i.Layer, ["visGroup"] = i.VisGroup } );
				return Ok( ToolGroup, ("op", J( "list" )), ("definitions", defs ), ("instances", insts ) ).ToJsonString();
			}
			default:
				throw new ArgumentException( $"Unknown group op '{op}'. Known ops: define, link, editMember, unlink, list." );
		}
	}

	string VisGroupTool( JsonObject args )
	{
		CheckMap( args );
		var op = ReqString( args, "op" ).ToLowerInvariant();
		switch ( op )
		{
			case "define":
			{
				var name = ReqString( args, "name" );
				_visGroups[name] = (OptString( args, "color", "255 255 255" ), true);
				return Ok( ToolVisGroup, ("op", J( "define" )), ("name", J( name )), ("color", J( _visGroups[name].Color )), ("visible", J( true )) ).ToJsonString();
			}
			case "setvisible":
			{
				var name = ReqString( args, "name" );
				if ( !_visGroups.TryGetValue( name, out var state ) )
					return Err( "visgroup-not-found", $"No visgroup '{name}' in map '{MapName}'." );
				_visGroups[name] = (state.Color, OptBool( args, "visible", true ));
				return Ok( ToolVisGroup, ("op", J( "setVisible" )), ("name", J( name )), ("visible", J( _visGroups[name].Visible )) ).ToJsonString();
			}
			case "assign":
			{
				var name = ReqString( args, "name" );
				if ( !_visGroups.ContainsKey( name ) )
					return Err( "visgroup-not-found", $"No visgroup '{name}' in map '{MapName}'." );
				var assigned = 0;
				foreach ( var brushId in StringArray( args, "brushIds" ) )
				{
					if ( FindBrush( brushId ) is null )
						return Err( "brush-not-found", $"No brush '{brushId}' in map '{MapName}'." );
					_brushVisGroup[brushId] = name;
					assigned++;
				}
				return Ok( ToolVisGroup, ("op", J( "assign" )), ("name", J( name )), ("brushes", J( assigned )) ).ToJsonString();
			}
			case "list":
			{
				var groups = new JsonArray();
				foreach ( var pair in _visGroups )
					groups.Add( new JsonObject { ["name"] = pair.Key, ["color"] = pair.Value.Color, ["visible"] = pair.Value.Visible } );
				return Ok( ToolVisGroup, ("op", J( "list" )), ("visgroups", groups ) ).ToJsonString();
			}
			default:
				throw new ArgumentException( $"Unknown visgroup op '{op}'. Known ops: define, setVisible, assign, list." );
		}
	}

	// ---- import / export ----

	string ImportExport( JsonObject args )
	{
		CheckMap( args );
		var op = ReqString( args, "op" ).ToLowerInvariant();
		var format = OptString( args, "format", "vmf" ).ToLowerInvariant();
		if ( format is not ("vmf" or "map") ) throw new ArgumentException( $"Unknown format '{format}'. Known formats: vmf, map." );
		switch ( op )
		{
			case "import":
			{
				var text = ReqString( args, "text" );
				MapKit.VmfDocument doc;
				try { doc = format == "vmf" ? MapKit.VmfIo.Parse( text ) : MapKit.Map220Io.Parse( text ); }
				catch ( Exception e ) { return Err( "import-parse-failed", e.Message ); }
				var warnings = ImportDocument( doc );
				return Ok( ToolImportExport,
					("op", J( "import" )),
					("format", J( format )),
					("brushes", J( BrushCount )),
					("entities", J( EntityCount )),
					("warnings", new JsonArray( warnings.Select( w => (JsonNode?)w ).ToArray() )) ).ToJsonString();
			}
			case "export":
			{
				var doc = ToVmfDocument();
				string text;
				var warnings = new List<string>();
				if ( format == "vmf" ) text = MapKit.VmfIo.Write( doc );
				else text = MapKit.Map220Io.Write( doc, out warnings );
				var obj = Ok( ToolImportExport,
					("op", J( "export" )),
					("format", J( format )),
					("brushes", J( BrushCount )),
					("entities", J( EntityCount )),
					("warnings", new JsonArray( warnings.Select( w => (JsonNode?)w ).ToArray() )),
					("text", J( text )) );
				if ( args["fileName"] is JsonValue fv && fv.TryGetValue<string>( out var fileName ) && !string.IsNullOrWhiteSpace( fileName ) )
				{
					var path = WriteExportFile( fileName, text, format == "vmf" ? ".vmf" : ".map" );
					obj["path"] = path;
				}
				return obj.ToJsonString();
			}
			default:
				throw new ArgumentException( $"Unknown import/export op '{op}'. Known ops: import, export." );
		}
	}

	/// <summary>Replace the session from a parsed VmfDocument. Invalid
	/// brushes and unknown entity classes are warnings + skips, never
	/// silent fixes and never a crash.</summary>
	List<string> ImportDocument( MapKit.VmfDocument doc )
	{
		var warnings = new List<string>();
		_brushes.Clear();
		_brushOrder.Clear();
		_brushNames.Clear();
		_brushNumbers.Clear();
		_detailBrushes.Clear();
		_brushVisGroup.Clear();
		_displacements.Clear();
		_displacementBrush.Clear();
		_nextBrush = 1;
		_nextDisplacement = 1;
		// MapEntities has no Clear: rebuild it (ids restart at 1, which
		// matches a fresh import).
		ResetEntities();
		_io = new EntityIoList();
		_skyName = doc.WorldspawnGet( "skyname" ) ?? "";
		foreach ( var pair in _visGroups.Keys.ToList() ) _visGroups.Remove( pair );
		foreach ( var vg in doc.VisGroups )
		{
			_visGroups[vg.Name] = (vg.Color, true);
		}

		var vmfBrushToSession = new Dictionary<MapKit.VmfBrush, List<string>>();
		foreach ( var vmfBrush in doc.WorldBrushes )
		{
			var id = ImportBrush( vmfBrush, warnings );
			if ( id is not null ) vmfBrushToSession[vmfBrush] = new List<string> { id };
		}
		foreach ( var entity in doc.Entities )
		{
			var brushIds = new List<string>();
			foreach ( var vmfBrush in entity.Brushes )
			{
				var id = ImportBrush( vmfBrush, warnings );
				if ( id is not null ) brushIds.Add( id );
			}
			ImportEntity( entity, brushIds, warnings );
		}
		// I/O rows ride on the entities' connections blocks.
		foreach ( var entity in doc.Entities )
			foreach ( var output in entity.Outputs )
			{
				var sourceName = entity.TargetName ?? "";
				if ( string.IsNullOrEmpty( sourceName ) ) sourceName = "#" + entity.Id;
				var error = _io.Add( new EntityIoOutput( sourceName, output.Event, output.Target, output.Input,
					output.Delay, output.FireCount, output.Parameter ) );
				if ( error is not null ) warnings.Add( $"I/O row {output.Event} -> {output.Target}: {error}" );
			}
		foreach ( var vmfBrush in doc.AllBrushes() )
			foreach ( var face in vmfBrush.Faces )
				if ( face.Displacement is not null )
					warnings.Add( $"Brush #{vmfBrush.Id}: displacement grids are not imported yet (power {face.Displacement.Power}); the brush face is imported flat." );
		return warnings;
	}

	void ResetEntities()
	{
		// MapEntities owns its list privately with no Clear; the session
		// rebuilds the collection. The registry is shared, so class
		// validation is unchanged.
		_entities = new MapEntities( _registry );
		_entityBrushIds.Clear();
	}

	string? ImportBrush( MapKit.VmfBrush vmfBrush, List<string> warnings )
	{
		var faces = new List<MapBrushFace>();
		foreach ( var vmfFace in vmfBrush.Faces )
		{
			var (normal, distance) = vmfFace.ToPlane();
			var uv = new FaceUv(
				new Vec2( (float)vmfFace.UAxis.Offset, (float)vmfFace.VAxis.Offset ),
				new Vec2( (float)vmfFace.UAxis.Scale, (float)vmfFace.VAxis.Scale ),
				(float)vmfFace.Rotation, vmfFace.LightmapScale, vmfFace.SmoothingGroups );
			faces.Add( new MapBrushFace(
				new Vec3( (float)normal.X, (float)normal.Y, (float)normal.Z ), (float)distance,
				string.IsNullOrEmpty( vmfFace.Material ) ? DefaultMaterial : vmfFace.Material, uv ) );
		}
		var brush = MapBrush.FromPlanes( faces );
		var defects = brush.Validate();
		if ( defects.Count > 0 )
		{
			warnings.Add( $"Brush #{vmfBrush.Id} skipped: invalid brush ({string.Join( "; ", defects.Select( d => d.Code ) )})." );
			return null;
		}
		return AddBrush( brush, "brush-" + vmfBrush.Id );
	}

	void ImportEntity( MapKit.VmfEntity vmfEntity, List<string> brushIds, List<string> warnings )
	{
		var keyvalues = new Dictionary<string, string>( StringComparer.Ordinal );
		foreach ( var pair in vmfEntity.KeyValues )
		{
			if ( pair.Key is "origin" or "angles" or "classname" ) continue;
			keyvalues[pair.Key] = pair.Value;
		}
		var origin = Vec3.Zero;
		if ( vmfEntity.Get( "origin" ) is string originText && MapEntities.TryParseOrigin( originText, out var parsedOrigin ) )
			origin = parsedOrigin;
		var angles = Vec3.Zero;
		if ( vmfEntity.Get( "angles" ) is string anglesText && MapEntities.TryParseAngles( anglesText, out var parsedAngles ) )
			angles = parsedAngles;
		try
		{
			if ( brushIds.Count == 0 )
				_entities.PlacePointEntity( vmfEntity.ClassName, origin, keyvalues, angles );
			else
			{
				var brushes = brushIds.Select( id => FindBrush( id )! ).ToList();
				var entity = _entities.PlaceBrushEntity( vmfEntity.ClassName, brushes, keyvalues, origin );
				_entityBrushIds[entity.Id] = new List<string>( brushIds );
			}
		}
		catch ( Exception e ) { warnings.Add( $"Entity '{vmfEntity.ClassName}'#{vmfEntity.Id} skipped: {e.Message}" ); }
	}

	/// <summary>Session -> VmfDocument (the transfer model both VMF and
	/// Valve 220 writers consume).</summary>
	MapKit.VmfDocument ToVmfDocument()
	{
		var doc = new MapKit.VmfDocument();
		if ( !string.IsNullOrEmpty( _skyName ) ) doc.WorldspawnSet( "skyname", _skyName );
		var tied = new HashSet<string>( _entityBrushIds.Values.SelectMany( ids => ids ), StringComparer.Ordinal );
		var number = 0;
		foreach ( var id in _brushOrder )
		{
			if ( tied.Contains( id ) ) continue;
			doc.WorldBrushes.Add( ToVmfBrush( _brushes[id], ++number, _brushVisGroup.TryGetValue( id, out var g ) ? VisGroupId( g ) : 0 ) );
		}
		foreach ( var entity in _entities.Entities )
		{
			var vmfEntity = new MapKit.VmfEntity { Id = entity.Id, ClassName = entity.ClassName };
			vmfEntity.Set( "origin", MapEntities.FormatOrigin( entity.Origin ) );
			if ( entity.Angles.x != 0f || entity.Angles.y != 0f || entity.Angles.z != 0f )
				vmfEntity.Set( "angles", MapEntities.FormatAngles( entity.Angles ) );
			foreach ( var pair in entity.Keyvalues ) vmfEntity.Set( pair.Key, pair.Value );
			if ( _entityBrushIds.TryGetValue( entity.Id, out var brushIds ) )
				foreach ( var brushId in brushIds )
					if ( FindBrush( brushId ) is MapBrush brush )
						vmfEntity.Brushes.Add( ToVmfBrush( brush, _brushNumbers[brushId], 0 ) );
			var sourceName = string.IsNullOrEmpty( entity.TargetName ) ? "#" + entity.Id : entity.TargetName;
			foreach ( var output in _io.Outputs )
			{
				if ( output.SourceEntity != sourceName ) continue;
				vmfEntity.Outputs.Add( new MapKit.VmfOutput
				{
					Event = output.Event, Target = output.TargetName, Input = output.Input,
					Parameter = output.Parameter, Delay = output.DelaySeconds, FireCount = output.MaxFires,
				} );
			}
			doc.Entities.Add( vmfEntity );
		}
		var visNumber = 0;
		foreach ( var pair in _visGroups )
			doc.VisGroups.Add( new MapKit.VmfVisGroup { Id = ++visNumber, Name = pair.Key, Color = pair.Value.Color } );
		return doc;
	}

	int VisGroupId( string name )
	{
		var i = 0;
		foreach ( var key in _visGroups.Keys )
		{
			i++;
			if ( key == name ) return i;
		}
		return 0;
	}

	static MapKit.VmfBrush ToVmfBrush( MapBrush brush, int id, int visGroupId )
	{
		var vmfBrush = new MapKit.VmfBrush { Id = id, VisGroupId = visGroupId };
		for ( var i = 0; i < brush.Faces.Count; i++ )
		{
			var face = brush.Faces[i];
			var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
			var p0 = face.Normal * face.Distance;
			var u = uAxis * 64f;
			var v = vAxis * 64f;
			vmfBrush.Faces.Add( new MapKit.VmfFace
			{
				Id = i,
				PlanePoints = new[]
				{
					new MapKit.VmfVec( p0.x, p0.y, p0.z ),
					new MapKit.VmfVec( p0.x + u.x, p0.y + u.y, p0.z + u.z ),
					new MapKit.VmfVec( p0.x + v.x, p0.y + v.y, p0.z + v.z ),
				},
				Material = face.MaterialId,
				UAxis = new MapKit.VmfUvAxis( new MapKit.VmfVec( uAxis.x, uAxis.y, uAxis.z ), face.Uv.Offset.x, face.Uv.Scale.x ),
				VAxis = new MapKit.VmfUvAxis( new MapKit.VmfVec( vAxis.x, vAxis.y, vAxis.z ), face.Uv.Offset.y, face.Uv.Scale.y ),
				Rotation = face.Uv.RotationDegrees,
				LightmapScale = (int)MathF.Round( face.Uv.LightmapScale ),
				SmoothingGroups = face.Uv.SmoothingGroup,
			} );
		}
		return vmfBrush;
	}

	/// <summary>File name only, under the work directory. A path with
	/// separators or ".." is an invalid-params refusal, not a write
	/// somewhere else.</summary>
	string WriteExportFile( string fileName, string text, string defaultExtension )
	{
		if ( fileName.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0 || fileName.Contains( "..", StringComparison.Ordinal ) )
			throw new ArgumentException( $"File name '{fileName}' must be a plain file name (no directories)." );
		if ( string.IsNullOrEmpty( Path.GetExtension( fileName ) ) ) fileName += defaultExtension;
		var path = Path.GetFullPath( Path.Combine( WorkDirectory, fileName ) );
		if ( !path.StartsWith( Path.GetFullPath( WorkDirectory ), StringComparison.Ordinal ) )
			throw new ArgumentException( $"File name '{fileName}' escapes the work directory." );
		File.WriteAllText( path, text );
		return path;
	}

	// ---- compile / validate / bake / navmesh / skybox ----

	string CompileTool( JsonObject args )
	{
		CheckMap( args );
		var preset = OptString( args, "preset", "quick" ).ToLowerInvariant() switch
		{
			"quick" => CompilePreset.QuickPlay,
			"preview" => CompilePreset.Preview,
			"final" => CompilePreset.Final,
			var other => throw new ArgumentException( $"Unknown compile preset '{other}'. Known presets: quick, preview, final." ),
		};
		var options = new CompileOptions();
		if ( args["outputFolder"] is JsonValue ov && ov.TryGetValue<string>( out var folder ) && !string.IsNullOrWhiteSpace( folder ) )
		{
			var resolved = Path.GetFullPath( Path.Combine( WorkDirectory, folder ) );
			if ( !resolved.StartsWith( Path.GetFullPath( WorkDirectory ), StringComparison.Ordinal ) )
				throw new ArgumentException( $"Output folder '{folder}' escapes the work directory." );
			options.OutputFolder = resolved;
		}
		var input = ToCompileInput();
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var result = CompileRunner.Run( input, preset, options );
		clock.Stop();
		var issues = new JsonArray();
		foreach ( var issue in result.Issues )
			issues.Add( new JsonObject
			{
				["code"] = issue.Code, ["severity"] = issue.Severity.ToString(),
				["message"] = issue.Message, ["subject"] = issue.Subject,
			} );
		var obj = Ok( ToolCompile,
			("preset", J( preset.ToString() )),
			("succeeded", J( result.Succeeded )),
			("failedStage", J( result.FailedStage?.ToString() )),
			("error", J( result.Error )),
			("cancelled", J( result.Cancelled )),
			("completedStages", new JsonArray( result.CompletedStages.Select( s => (JsonNode?)s.ToString() ).ToArray() ) ),
			("issues", issues ),
			("logLines", J( result.Log.Count )),
			("logTail", J( string.Join( "\n", result.Log.TakeLast( 8 ) ) )),
			("outputFiles", new JsonArray( result.OutputFiles.Select( f => (JsonNode?)f ).ToArray() ) ),
			("durationMs", J( clock.Elapsed.TotalMilliseconds )) );
		if ( result.Leak is not null )
			obj["leak"] = new JsonObject { ["isLeaked"] = result.Leak.IsLeaked, ["pathPoints"] = result.Leak.Path.Count };
		if ( result.Bsp is not null ) obj["bspLeaves"] = J( result.Bsp.LeafCount );
		if ( result.Lightmaps is not null ) obj["lightmapFaces"] = J( result.Lightmaps.Faces.Count );
		return obj.ToJsonString();
	}

	string ValidateTool( JsonObject args )
	{
		CheckMap( args );
		var engine = new MapKit.IssueEngine();
		var issues = engine.Run( ToIssueDocument() );
		var list = new JsonArray();
		var errors = 0;
		var warnings = 0;
		foreach ( var issue in issues )
		{
			if ( issue.Severity == MapKit.IssueSeverity.Error ) errors++;
			else if ( issue.Severity == MapKit.IssueSeverity.Warning ) warnings++;
			list.Add( new JsonObject
			{
				["code"] = issue.Code, ["severity"] = issue.Severity.ToString(),
				["kind"] = issue.Kind.ToString(), ["message"] = issue.Message,
				["brushId"] = issue.BrushId, ["entityId"] = issue.EntityId,
				["fixHint"] = issue.FixHint,
			} );
		}
		foreach ( var entityIssue in _entities.Validate() )
		{
			if ( entityIssue.Severity == MapEntityIssueSeverity.Error ) errors++;
			else if ( entityIssue.Severity == MapEntityIssueSeverity.Warning ) warnings++;
			list.Add( new JsonObject
			{
				["code"] = entityIssue.Code, ["severity"] = entityIssue.Severity.ToString(),
				["kind"] = "Entity", ["message"] = entityIssue.Message,
				["brushId"] = -1, ["entityId"] = entityIssue.EntityId,
				["fixHint"] = "",
			} );
		}
		return Ok( ToolValidate,
			("issues", list ),
			("issueCount", J( list.Count )),
			("errors", J( errors )),
			("warnings", J( warnings )),
			("rulesRun", J( engine.Rules.Count )) ).ToJsonString();
	}

	string BakeLightmap( JsonObject args )
	{
		CheckMap( args );
		var export = OptString( args, "export", "none" ).ToLowerInvariant();
		if ( export is not ("none" or "obj" or "glb" or "both") )
			throw new ArgumentException( $"Unknown bake export '{export}'. Known exports: none, obj, glb, both." );
		var bakeBrushes = _brushOrder.Select( id => MapBakeBrush.FromMapBrush( _brushes[id], BrushDisplayName( id ) ) ).ToList();
		var document = new MapBakeDocument( MapName, bakeBrushes, Array.Empty<MapBakeDisplacement>() );
		var baked = MapBakeExport.Bake( document );
		var obj = Ok( ToolBakeLightmap,
			("faces", J( baked.FaceCount )),
			("vertices", J( baked.VertexCount )),
			("materials", new JsonArray( baked.Materials.Select( m => (JsonNode?)m ).ToArray() ) ),
			("excludedNoDrawFaces", J( baked.ExcludedNoDrawFaces )),
			("excludedDegenerateFaces", J( baked.ExcludedDegenerateFaces )),
			("brushFaceCount", J( baked.BrushFaceCount )),
			("displacementFaceCount", J( baked.DisplacementFaceCount )),
			("displacementsSkipped", J( _displacements.Count )),
			("displacementsSkippedReason", J( "Displacement faces carry an integer material id with no name mapping yet, so MapBakeDisplacement cannot be built from them; the bake covers brushes only." )),
			("issues", new JsonArray( baked.Issues.Select( i => (JsonNode?)i ).ToArray() )) );
		if ( export is "obj" or "both" )
		{
			var fileName = OptString( args, "fileName", "map-bake.obj" );
			if ( !fileName.EndsWith( ".obj", StringComparison.OrdinalIgnoreCase ) ) fileName += ".obj";
			var path = ExportPath( fileName );
			var exported = MapBakeExport.ExportObj( baked, path );
			obj["objPath"] = exported.ObjPath;
			obj["objWritten"] = exported.ObjWritten;
			obj["mtlPath"] = exported.MtlPath;
		}
		if ( export is "glb" or "both" )
		{
			var fileName = OptString( args, "fileName", "map-bake.glb" );
			if ( export == "both" ) fileName = Path.ChangeExtension( OptString( args, "fileName", "map-bake" ), ".glb" );
			else if ( !fileName.EndsWith( ".glb", StringComparison.OrdinalIgnoreCase ) ) fileName += ".glb";
			var path = ExportPath( fileName );
			var exported = MapBakeExport.ExportGlb( baked, path );
			obj["glbPath"] = exported.GlbPath;
			obj["glbWritten"] = exported.GlbWritten;
			obj["glbNote"] = MapBakeExport.GlbNote;
		}
		return obj.ToJsonString();
	}

	string ExportPath( string fileName )
	{
		if ( fileName.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0 || fileName.Contains( "..", StringComparison.Ordinal ) )
			throw new ArgumentException( $"File name '{fileName}' must be a plain file name (no directories)." );
		var path = Path.GetFullPath( Path.Combine( WorkDirectory, fileName ) );
		if ( !path.StartsWith( Path.GetFullPath( WorkDirectory ), StringComparison.Ordinal ) )
			throw new ArgumentException( $"File name '{fileName}' escapes the work directory." );
		return path;
	}

	string NavmeshTool( JsonObject args )
	{
		CheckMap( args );
		var result = Effigy.Map.PlaytestLoop.PreLaunchValidation( ToIssueDocument(), navmeshPresent: false );
		var issues = new JsonArray();
		foreach ( var issue in result.Issues )
			issues.Add( new JsonObject
			{
				["code"] = issue.Code, ["severity"] = issue.Severity.ToString(),
				["message"] = issue.Message, ["entityId"] = issue.EntityId,
			} );
		return Ok( ToolNavmesh,
			("passed", J( result.Passed )),
			("navmeshRequired", J( result.NavmeshRequired )),
			("hasPlayerStart", J( result.HasPlayerStart )),
			("playerStartCount", J( result.PlayerStartCount )),
			("issues", issues ),
			("generation", new JsonObject
			{
				["status"] = "not-implemented",
				["reason"] = "No navmesh type has landed in the kernel yet (PlaytestLoop can only report NAVMESH_MISSING when NPC / info_node entities are present). Navmesh generation is a follow-up ticket.",
			}) ).ToJsonString();
	}

	string SkyboxCubemap( JsonObject args )
	{
		CheckMap( args );
		if ( args["skyName"] is JsonValue sv && sv.TryGetValue<string>( out var skyName ) && !string.IsNullOrWhiteSpace( skyName ) )
			_skyName = skyName;
		if ( OptBool( args, "placeSkyCamera", false ) )
		{
			var origin = OptVec3( args, "skyCameraOrigin", Vec3.Zero );
			var scale = (float)OptDouble( args, "skyCameraScale", 16.0 );
			_entities.PlacePointEntity( "sky_camera", origin,
				new Dictionary<string, string> { ["scale"] = scale.ToString( CultureInfo.InvariantCulture ) } );
		}
		var scene = MapKit.SkyboxPreview.BuildPreview( ToVmfDocument() );
		var warnings = new JsonArray();
		foreach ( var warning in scene.Warnings )
			warnings.Add( new JsonObject { ["code"] = warning.Code, ["message"] = warning.Message } );
		var obj = Ok( ToolSkyboxCubemap,
			("skyName", J( scene.SkyName )),
			("hasSkyCamera", J( scene.HasSkyCamera )),
			("skyCameraOrigin", J( scene.SkyCameraOrigin )),
			("scale", J( scene.Scale )),
			("has3DSection", J( scene.Has3DSection )),
			("sectionFaces", J( scene.Faces.Count )),
			("warnings", warnings ),
			("cubemap", new JsonObject
			{
				["status"] = "not-implemented",
				["reason"] = "No cubemap builder has landed in the kernel (KeybindRegistry only names the Alt+C keybind). The skybox preview above is real; cubemap capture is a follow-up ticket.",
			}) );
		return obj.ToJsonString();
	}

	// ---- session projections onto the kernel view types ----

	/// <summary>Session -> IssueDocument (the IssueEngine's view, and
	/// PlaytestLoop's). Face vertices are the brush's solved vertices on
	/// the face plane, ordered around the face centroid, so the engine's
	/// fan-triangulated volume matches the brush's own volume.</summary>
	MapKit.IssueDocument ToIssueDocument()
	{
		var doc = new MapKit.IssueDocument { Name = MapName };
		foreach ( var id in _brushOrder )
		{
			var brush = _brushes[id];
			var faces = new List<MapKit.IssueFace>();
			foreach ( var face in brush.Faces )
				faces.Add( new MapKit.IssueFace( face.Normal, face.Distance, face.MaterialId, FacePolygon( brush, face ) ) );
			doc.Brushes.Add( new MapKit.IssueBrush( _brushNumbers[id], faces, _detailBrushes.Contains( id ) ) );
		}
		foreach ( var entity in _entities.Entities )
		{
			var issueEntity = new MapKit.IssueEntity( entity.Id, entity.ClassName, entity.Origin );
			foreach ( var pair in entity.Keyvalues ) issueEntity.KeyValues[pair.Key] = pair.Value;
			if ( _entityBrushIds.TryGetValue( entity.Id, out var brushIds ) )
				foreach ( var brushId in brushIds )
					if ( _brushNumbers.TryGetValue( brushId, out var number ) ) issueEntity.BrushIds.Add( number );
			doc.Entities.Add( issueEntity );
		}
		return doc;
	}

	static List<Vec3> FacePolygon( MapBrush brush, MapBrushFace face )
	{
		var onPlane = brush.Vertices.Where( v => MathF.Abs( face.SignedDistance( v ) ) <= MapBrush.PlaneEps * 4f ).ToList();
		if ( onPlane.Count < 3 ) return onPlane;
		var centroid = Vec3.Zero;
		foreach ( var point in onPlane ) centroid += point;
		centroid /= onPlane.Count;
		var (u, v) = FaceUv.PlaneAxes( face.Normal );
		var c = centroid;
		onPlane.Sort( ( a, b ) =>
			MathF.Atan2( Vec3.Dot( a - c, v ), Vec3.Dot( a - c, u ) )
				.CompareTo( MathF.Atan2( Vec3.Dot( b - c, v ), Vec3.Dot( b - c, u ) ) ) );
		return onPlane;
	}

	CompileInput ToCompileInput()
	{
		var input = new CompileInput( MapName );
		foreach ( var id in _brushOrder )
		{
			var brush = _brushes[id];
			input.Brushes.Add( new CompileBrush( BrushDisplayName( id ), _brushNumbers[id],
				brush.Faces.Select( f => new CompileFace( f.Normal, f.Distance, f.MaterialId ) { LightmapScale = f.Uv.LightmapScale } ),
				_detailBrushes.Contains( id ) ) );
		}
		foreach ( var entity in _entities.Entities )
        {
            if (entity.ClassName == "info_player_start") input.PlayerStart ??= entity.Origin;
			if ( entity.ClassName is "light" or "light_environment" )
				input.Lights.Add( new CompileLight( entity.Origin ) );
        }
		return input;
	}
}
