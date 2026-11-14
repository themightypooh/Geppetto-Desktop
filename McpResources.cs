// McpResources.cs — MCP resources + prompts (task mcp-resources-prompts-document-mesh-tests,
// extended by hammer-ai-mcp-map-resources-prompts with the map resources + prompts).
//
// Read-only MCP surface for the Geppetto Desktop MCP server (scope:
// ~/workspace/geppetto-desktop-mcp-cli-scope.md). The stdio transport task
// (mcp-server-stdio-transport-and-protocol) owns --mcp dispatch in Program.cs
// and the JSON-RPC 2.0 framing; this file is the transport-neutral provider
// it calls for resources/list, resources/read, prompts/list and prompts/get:
//
//   ListResources()            -> the eight geppetto:// resources, verbatim URIs.
//   ReadResource(uri, source)  -> McpResourceContent (uri, mimeType, text).
//   ListPrompts()              -> the seven prompts with their arguments.
//   GetPrompt(name, args)      -> McpPromptResult (description + messages).
//
// Live state comes in through McpDocumentSource, which the host fills in:
// CurrentDocument returns the open DocumentSession (null = no document open),
// LatestTestRun returns the most recent test run recorded in this process
// (null = none recorded yet). The map state comes in the same way:
// CurrentMap returns the current map as an IssueDocument (null = no map open),
// CurrentRegistry returns the loaded entity registry (null = none loaded),
// LatestCompile returns the most recent compile run recorded in this process
// (null = none recorded yet). Nothing here opens files, runs tests, edits the
// model, or touches the network: resources are read-only by construction, and
// prompts are text the client sends back through the normal tool calls.
// Unknown URIs / prompt names / missing required arguments throw
// ArgumentException so the transport can answer MCP -32602 (invalid params).
// No new dependency: System.Text.Json is the house JSON stack.
//
// MAP SEAM (hammer-ai-mcp-map-resources-prompts): MapDocument has NOT landed
// (see the IssueEngine header: "MapDocument has not landed, so the integrator
// adapts it onto this shape with one projection"). The map resources therefore
// read the IssueDocument projection — the same document view the IssueEngine
// itself checks — and the entity registry (EntityRegistry, FGD-like JSON) and
// the compile log (CompileResult) that already exist. When MapDocument lands,
// the host swaps the CurrentMap delegate to project from it; nothing in this
// file changes shape. The issue resource runs the live IssueEngine on the
// current map on every read, so it always reflects live map state; the
// compile-log resource only ever reports a run the host recorded, it never
// replays a stale file and never starts a compile itself (compiles run
// through the map_compile tool, owned by hammer-ai-mcp-map-tools).
//
// PROMPT SEAM (hammer-ai-mcp-map-tools, worker 65, in parallel): the map
// prompts name exactly the tool names that ticket files (map_brush_create,
// map_brush_edit, map_brush_clip, map_displacement_create, map_face_material,
// map_entity_place, map_entity_edit, map_io_add, map_group, map_import_export,
// map_validate, map_compile, map_bake_lightmap, map_navmesh,
// map_skybox_cubemap). If that ticket renames a tool, the prompt text must be
// updated to match — the prompts call tools, they never implement them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

namespace Marionette.Desktop;

/// <summary>One read-only MCP resource, as resources/list reports it.</summary>
public sealed record McpResource( string Uri, string Name, string Description, string MimeType );

/// <summary>The body of a resource, as resources/read returns it.</summary>
public sealed record McpResourceContent( string Uri, string MimeType, string Text );

/// <summary>One argument of an MCP prompt.</summary>
public sealed record McpPromptArgument( string Name, string Description, bool Required );

/// <summary>One MCP prompt, as prompts/list reports it.</summary>
public sealed record McpPrompt( string Name, string Description, IReadOnlyList<McpPromptArgument> Arguments );

/// <summary>One message of a rendered prompt (role is "user" or "assistant").</summary>
public sealed record McpPromptMessage( string Role, string Text );

/// <summary>A rendered prompt, as prompts/get returns it.</summary>
public sealed record McpPromptResult( string Description, IReadOnlyList<McpPromptMessage> Messages );

/// <summary>One test outcome, in transport-neutral form (TestOutcome is internal to Desktop.Tests).</summary>
public sealed record McpTestOutcome( string Group, string Name, string Status, string? Reason, double Milliseconds );

/// <summary>One recorded test run: its outcomes plus when it was recorded (UTC).</summary>
public sealed record McpTestRun( IReadOnlyList<McpTestOutcome> Outcomes, DateTimeOffset RecordedAtUtc );

/// <summary>
/// Live state the MCP host injects. Both delegates are optional: a null
/// CurrentDocument means no document is open, a null LatestTestRun means no
/// test run has been recorded in this process yet. The server never fabricates
/// either — a client asking before any run sees recorded:false plus how to run.
/// The map delegates follow the same rule: a null CurrentMap means no map is
/// open, a null CurrentRegistry means no entity registry is loaded, and a null
/// LatestCompile means no compile has run in this process yet.
/// </summary>
public sealed class McpDocumentSource
{
	public Func<DocumentSession?>? CurrentDocument { get; set; }
	public Func<McpTestRun?>? LatestTestRun { get; set; }
	public Func<IssueDocument?>? CurrentMap { get; set; }
	public Func<EntityRegistry?>? CurrentRegistry { get; set; }
	public Func<CompileResult?>? LatestCompile { get; set; }
}

public static class McpResources
{
	public const string DocumentUri = "geppetto://document";
	public const string DocumentMeshUri = "geppetto://document/mesh";
	public const string TestsLatestUri = "geppetto://tests/latest";
	public const string CatalogUri = "geppetto://catalog";
	public const string MapUri = "geppetto://map";
	public const string MapEntitiesUri = "geppetto://map/entities";
	public const string MapIssuesUri = "geppetto://map/issues";
	public const string MapCompileLogUri = "geppetto://map/compile-log";

	static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	public static IReadOnlyList<McpResource> ListResources() => new[]
	{
		new McpResource( DocumentUri, "document", "The open document: id, source, revision, dirty state, feature list, bodies and selection. Empty (open:false) when no document is open.", "application/json" ),
		new McpResource( DocumentMeshUri, "document-mesh", "Mesh summary of the open document: per-body vertex/face counts and bounds diagonal, plus totals. Empty (open:false) when no document is open.", "application/json" ),
		new McpResource( TestsLatestUri, "tests-latest", "The most recent Desktop.Tests run recorded in this process: counts and failures. recorded:false when nothing has run yet.", "application/json" ),
		new McpResource( CatalogUri, "catalog", "The API catalog: feature types, script verbs, rig verbs, parts, brush kinds, presets and edit operations, as JSON.", "application/json" ),
		new McpResource( MapUri, "map", "The current map: name, brush and entity counts, limits, and every brush (faces, materials, volume, centroid) and entity (classname, origin, keyvalues). Empty (open:false) when no map is open.", "application/json" ),
		new McpResource( MapEntitiesUri, "map-entities", "The loaded entity registry (FGD-like JSON): every entity class with its kind, keyvalues, spawnflags, inputs, outputs and helpers, plus the implicit base keyvalues. registered:false when no registry is loaded.", "application/json" ),
		new McpResource( MapIssuesUri, "map-issues", "Live IssueEngine output for the current map: counts by severity and kind, run stats, and every issue with its code, message, fix hint and location. Empty (open:false) when no map is open.", "application/json" ),
		new McpResource( MapCompileLogUri, "map-compile-log", "The most recent compile run recorded in this process: preset, stages, BSP/PVS/lightmap summaries, leak result, issues and the timestamped log lines. recorded:false when nothing has compiled yet.", "application/json" ),
	};

	public static McpResourceContent ReadResource( string uri, McpDocumentSource? source = null )
	{
		if ( string.IsNullOrWhiteSpace( uri ) )
			throw new ArgumentException( "A resource URI is required.", nameof( uri ) );
		return uri switch
		{
			DocumentUri => new McpResourceContent( uri, "application/json", DocumentJson( source?.CurrentDocument?.Invoke() ) ),
			DocumentMeshUri => new McpResourceContent( uri, "application/json", MeshJson( source?.CurrentDocument?.Invoke() ) ),
			TestsLatestUri => new McpResourceContent( uri, "application/json", TestsLatestJson( source?.LatestTestRun?.Invoke() ) ),
			CatalogUri => new McpResourceContent( uri, "application/json", ApiCatalog.GenerateJsonSchema() ),
			MapUri => new McpResourceContent( uri, "application/json", MapJson( source?.CurrentMap?.Invoke() ) ),
			MapEntitiesUri => new McpResourceContent( uri, "application/json", RegistryJson( source?.CurrentRegistry?.Invoke() ) ),
			MapIssuesUri => new McpResourceContent( uri, "application/json", IssuesJson( source?.CurrentMap?.Invoke() ) ),
			MapCompileLogUri => new McpResourceContent( uri, "application/json", CompileLogJson( source?.LatestCompile?.Invoke() ) ),
			_ => throw new ArgumentException( $"Unknown MCP resource: {uri}. Known resources: {DocumentUri}, {DocumentMeshUri}, {TestsLatestUri}, {CatalogUri}, {MapUri}, {MapEntitiesUri}, {MapIssuesUri}, {MapCompileLogUri}.", nameof( uri ) ),
		};
	}

	static string DocumentJson( DocumentSession? session )
	{
		if ( session is null )
			return JsonSerializer.Serialize( new Dictionary<string, object?>
			{
				["uri"] = DocumentUri,
				["open"] = false,
				["note"] = "No document is open in this server process. Open one in Geppetto Desktop first, then read this resource again.",
			}, Json );

		var studio = session.Studio;
		var features = new List<object>();
		if ( studio is not null )
			foreach ( var f in studio.Features )
				features.Add( new Dictionary<string, object?>
				{
					["id"] = f.Id,
					["name"] = f.Name ?? f.Id,
					["type"] = f.GetType().Name,
					["suppressed"] = f.Suppressed,
				} );
		var bodies = new List<object>();
		if ( studio is not null )
			foreach ( var b in studio.Bodies )
				bodies.Add( new Dictionary<string, object?>
				{
					["id"] = b.Id,
					["name"] = b.Name ?? b.Id,
				} );
		var doc = new Dictionary<string, object?>
		{
			["uri"] = DocumentUri,
			["open"] = true,
			["documentId"] = session.DocumentId,
			["sourceKind"] = session.SourceKind.ToString(),
			["sourcePath"] = session.SourcePath,
			["revision"] = session.Revision,
			["savedRevision"] = session.SavedRevision,
			["isDirty"] = session.IsDirty,
			["hasStudio"] = studio is not null,
			["features"] = features,
			["bodies"] = bodies,
			["selection"] = new Dictionary<string, object?>
			{
				["featureId"] = session.Selection.FeatureId,
				["bodyId"] = session.Selection.BodyId,
				["boneName"] = session.Selection.BoneName,
				["selectedBodyIds"] = session.Selection.SelectedBodyIds.ToArray(),
			},
			["hasClip"] = session.Clip is not null,
			["clipPath"] = session.ClipPath,
			["playheadTime"] = session.PlayheadTime,
		};
		if ( studio is null )
			doc["note"] = "FBX source has no PartStudio, so no feature list is available; use geppetto://document/mesh for geometry.";
		return JsonSerializer.Serialize( doc, Json );
	}

	static string MeshJson( DocumentSession? session )
	{
		if ( session is null )
			return JsonSerializer.Serialize( new Dictionary<string, object?>
			{
				["uri"] = DocumentMeshUri,
				["open"] = false,
				["note"] = "No document is open in this server process. Open one in Geppetto Desktop first, then read this resource again.",
			}, Json );

		var studio = session.Studio;
		var bodies = new List<object>();
		long vertices = 0, faces = 0;
		if ( studio is not null )
			foreach ( var b in studio.Bodies )
			{
				var v = b.Mesh.VertexCount;
				var f = b.Mesh.FaceCount;
				vertices += v;
				faces += f;
				bodies.Add( new Dictionary<string, object?>
				{
					["id"] = b.Id,
					["name"] = b.Name ?? b.Id,
					["vertices"] = v,
					["faces"] = f,
					["boundsDiagonal"] = b.Mesh.BoundsDiagonal,
				} );
			}
		var doc = new Dictionary<string, object?>
		{
			["uri"] = DocumentMeshUri,
			["open"] = true,
			["documentId"] = session.DocumentId,
			["bodies"] = bodies,
			["totals"] = new Dictionary<string, object?>
			{
				["bodies"] = bodies.Count,
				["vertices"] = vertices,
				["faces"] = faces,
			},
		};
		if ( studio is null )
			doc["note"] = "FBX source has no PartStudio, so no per-body mesh is available from this resource.";
		return JsonSerializer.Serialize( doc, Json );
	}

	static string TestsLatestJson( McpTestRun? run )
	{
		if ( run is null )
			return JsonSerializer.Serialize( new Dictionary<string, object?>
			{
				["uri"] = TestsLatestUri,
				["recorded"] = false,
				["howToRun"] = "No test run has been recorded in this server process yet. Run `marionette-desktop ops test` (or Desktop.Tests directly); the MCP server reports runs recorded in this process only, it never replays a stale file.",
			}, Json );

		var outcomes = run.Outcomes;
		var passed = outcomes.Count( o => o.Status == "PASS" );
		var failed = outcomes.Where( o => o.Status == "FAIL" ).ToArray();
		var skipped = outcomes.Where( o => o.Status != "PASS" && o.Status != "FAIL" ).ToArray();
		return JsonSerializer.Serialize( new Dictionary<string, object?>
		{
			["uri"] = TestsLatestUri,
			["recorded"] = true,
			["recordedAtUtc"] = run.RecordedAtUtc,
			["counts"] = new Dictionary<string, object?>
			{
				["total"] = outcomes.Count,
				["passed"] = passed,
				["failed"] = failed.Length,
				["other"] = skipped.Length,
			},
			["failures"] = failed.Select( o => new Dictionary<string, object?>
			{
				["group"] = o.Group,
				["name"] = o.Name,
				["reason"] = o.Reason,
			} ).ToArray(),
			["outcomes"] = outcomes.Select( o => new Dictionary<string, object?>
			{
				["group"] = o.Group,
				["name"] = o.Name,
				["status"] = o.Status,
				["reason"] = o.Reason,
				["milliseconds"] = o.Milliseconds,
			} ).ToArray(),
		}, Json );
	}

	static double[] VecJson( Vec3 v ) => new[] { (double)v.x, (double)v.y, (double)v.z };

	static string MapJson( IssueDocument? map )
	{
		if ( map is null )
			return JsonSerializer.Serialize( new Dictionary<string, object?>
			{
				["uri"] = MapUri,
				["open"] = false,
				["note"] = "No map is open in this server process. Open a map in Geppetto Desktop first, then read this resource again.",
			}, Json );

		var brushes = new List<object>();
		var faceCount = 0;
		foreach ( var b in map.Brushes ?? new List<IssueBrush>() )
		{
			if ( b is null )
				continue;
			var faces = new List<object>();
			var materials = new List<string>();
			foreach ( var f in b.Faces ?? new List<IssueFace>() )
			{
				if ( f is null )
					continue;
				faceCount++;
				if ( !string.IsNullOrEmpty( f.Material ) && !materials.Contains( f.Material ) )
					materials.Add( f.Material );
				faces.Add( new Dictionary<string, object?>
				{
					["normal"] = VecJson( f.Normal ),
					["distance"] = f.Distance,
					["material"] = f.Material,
					["vertices"] = ( f.Vertices ?? new List<Vec3>() ).Select( v => VecJson( v ) ).ToArray(),
				} );
			}
			brushes.Add( new Dictionary<string, object?>
			{
				["id"] = b.Id,
				["isDetail"] = b.IsDetail,
				["faces"] = faces,
				["faceCount"] = faces.Count,
				["materials"] = materials.ToArray(),
				["volume"] = b.Volume(),
				["centroid"] = VecJson( b.Centroid() ),
			} );
		}
		var entities = new List<object>();
		foreach ( var e in map.Entities ?? new List<IssueEntity>() )
		{
			if ( e is null )
				continue;
			entities.Add( new Dictionary<string, object?>
			{
				["id"] = e.Id,
				["className"] = e.ClassName,
				["origin"] = VecJson( e.Origin ),
				["targetName"] = e.TargetName,
				["keyValues"] = e.KeyValues ?? new Dictionary<string, string>(),
				["brushIds"] = ( e.BrushIds ?? new List<int>() ).ToArray(),
			} );
		}
		return JsonSerializer.Serialize( new Dictionary<string, object?>
		{
			["uri"] = MapUri,
			["open"] = true,
			["name"] = map.Name,
			["counts"] = new Dictionary<string, object?>
			{
				["brushes"] = brushes.Count,
				["faces"] = faceCount,
				["entities"] = entities.Count,
			},
			["limits"] = new Dictionary<string, object?>
			{
				["maxBrushes"] = map.Limits?.MaxBrushes ?? 0,
				["maxFaces"] = map.Limits?.MaxFaces ?? 0,
				["maxEntities"] = map.Limits?.MaxEntities ?? 0,
				["maxVertices"] = map.Limits?.MaxVertices ?? 0,
			},
			["brushes"] = brushes,
			["entities"] = entities,
		}, Json );
	}

	static string RegistryJson( EntityRegistry? registry )
	{
		if ( registry is null )
			return JsonSerializer.Serialize( new Dictionary<string, object?>
			{
				["uri"] = MapEntitiesUri,
				["registered"] = false,
				["note"] = "No entity registry is loaded in this server process. Load a registry (the shipped source1-core registry or a registry JSON file) first, then read this resource again.",
			}, Json );

		var classes = new List<object>();
		foreach ( var c in registry.Classes ?? Array.Empty<EntityClass>() )
		{
			if ( c is null )
				continue;
			classes.Add( new Dictionary<string, object?>
			{
				["name"] = c.Name,
				["kind"] = c.Kind.ToString(),
				["description"] = c.Description,
				["baseClasses"] = ( c.BaseClasses ?? Array.Empty<string>() ).ToArray(),
				["keyValues"] = ( c.KeyValues ?? Array.Empty<EntityKeyValue>() ).Select( kv => new Dictionary<string, object?>
				{
					["name"] = kv.Name,
					["type"] = kv.Type.ToString(),
					["default"] = kv.DefaultValue,
					["description"] = kv.Description,
					["choices"] = ( kv.Choices ?? Array.Empty<string>() ).ToArray(),
				} ).ToArray(),
				["spawnflags"] = ( c.Spawnflags ?? Array.Empty<EntitySpawnflag>() ).Select( sf => new Dictionary<string, object?>
				{
					["value"] = sf.Value,
					["name"] = sf.Name,
					["defaultOn"] = sf.DefaultOn,
					["description"] = sf.Description,
				} ).ToArray(),
				["inputs"] = ( c.Inputs ?? Array.Empty<EntityInput>() ).Select( i => new Dictionary<string, object?>
				{
					["name"] = i.Name,
					["argumentType"] = i.ArgumentType,
					["description"] = i.Description,
				} ).ToArray(),
				["outputs"] = ( c.Outputs ?? Array.Empty<EntityOutput>() ).Select( o => new Dictionary<string, object?>
				{
					["name"] = o.Name,
					["argumentType"] = o.ArgumentType,
					["description"] = o.Description,
				} ).ToArray(),
				["helpers"] = ( c.Helpers ?? Array.Empty<EntityHelper>() ).Select( h => new Dictionary<string, object?>
				{
					["kind"] = h.Kind.ToString(),
					["key"] = h.Key,
					["path"] = h.Path,
				} ).ToArray(),
			} );
		}
		var implicitKeyValues = EntityRegistry.ImplicitKeyValues
			.Select( kv => new Dictionary<string, object?> { ["name"] = kv.Key, ["type"] = kv.Value.ToString() } )
			.ToArray();
		return JsonSerializer.Serialize( new Dictionary<string, object?>
		{
			["uri"] = MapEntitiesUri,
			["registered"] = true,
			["name"] = registry.Name,
			["schemaVersion"] = registry.SchemaVersion,
			["sourcePath"] = registry.SourcePath,
			["classCount"] = classes.Count,
			["implicitKeyValues"] = implicitKeyValues,
			["classes"] = classes,
		}, Json );
	}

	static string IssuesJson( IssueDocument? map )
	{
		if ( map is null )
			return JsonSerializer.Serialize( new Dictionary<string, object?>
			{
				["uri"] = MapIssuesUri,
				["open"] = false,
				["note"] = "No map is open in this server process. Open a map in Geppetto Desktop first, then read this resource again.",
			}, Json );

		// Live output: the engine runs on the current map on every read, so
		// this resource always reflects live map state. Run() never mutates
		// the document, so the read stays read-only.
		var engine = new IssueEngine();
		var issues = engine.Run( map );
		var stats = engine.LastStats;
		var byKind = new Dictionary<string, object?>();
		foreach ( var group in issues.GroupBy( i => i.Kind.ToString() ).OrderBy( g => g.Key ) )
			byKind[group.Key] = group.Count();
		return JsonSerializer.Serialize( new Dictionary<string, object?>
		{
			["uri"] = MapIssuesUri,
			["open"] = true,
			["mapName"] = map.Name,
			["counts"] = new Dictionary<string, object?>
			{
				["total"] = issues.Count,
				["errors"] = issues.Count( i => i.Severity == IssueSeverity.Error ),
				["warnings"] = issues.Count( i => i.Severity == IssueSeverity.Warning ),
				["info"] = issues.Count( i => i.Severity == IssueSeverity.Info ),
				["byKind"] = byKind,
			},
			["stats"] = new Dictionary<string, object?>
			{
				["wasIncremental"] = stats.WasIncremental,
				["brushesChecked"] = stats.BrushesChecked,
				["entitiesChecked"] = stats.EntitiesChecked,
				["rulesRun"] = stats.RulesRun,
				["issuesFound"] = stats.IssuesFound,
				["elapsedMilliseconds"] = stats.ElapsedMilliseconds,
			},
			["issues"] = issues.Select( i => new Dictionary<string, object?>
			{
				["kind"] = i.Kind.ToString(),
				["severity"] = i.Severity.ToString(),
				["code"] = i.Code,
				["message"] = i.Message,
				["fixHint"] = i.FixHint,
				["brushId"] = i.BrushId,
				["entityId"] = i.EntityId,
				["location"] = i.HasLocation ? VecJson( i.Location ) : null,
				["hasLocation"] = i.HasLocation,
			} ).ToArray(),
		}, Json );
	}

	static string CompileLogJson( CompileResult? result )
	{
		if ( result is null )
			return JsonSerializer.Serialize( new Dictionary<string, object?>
			{
				["uri"] = MapCompileLogUri,
				["recorded"] = false,
				["howToRun"] = "No compile has run in this server process yet. Run map_compile on the current map (QuickPlay for a BSP-only check); the MCP server reports compiles recorded in this process only, it never replays a stale log file.",
			}, Json );

		object? BspJson()
		{
			if ( result.Bsp is null )
				return null;
			return new Dictionary<string, object?>
			{
				["leafCount"] = result.Bsp.LeafCount,
				["portalCount"] = result.Bsp.PortalCount,
				["emptyLeafCount"] = result.Bsp.EmptyLeafCount,
				["solidLeafCount"] = result.Bsp.SolidLeafCount,
				["voidLeafCount"] = result.Bsp.VoidLeafCount,
			};
		}
		object? PvsJson()
		{
			if ( result.Pvs is null )
				return null;
			return new Dictionary<string, object?>
			{
				["sets"] = result.Pvs.Sets.Count,
				["quality"] = result.Pvs.Quality.ToString(),
			};
		}
		object? LightmapsJson()
		{
			if ( result.Lightmaps is null )
				return null;
			return new Dictionary<string, object?>
			{
				["faces"] = result.Lightmaps.Faces.Count,
				["resolution"] = result.Lightmaps.Resolution,
				["mean"] = result.Lightmaps.Mean,
			};
		}
		object? LeakJson()
		{
			if ( result.Leak is null )
				return null;
			return new Dictionary<string, object?>
			{
				["isLeaked"] = result.Leak.IsLeaked,
				["startLeafId"] = result.Leak.StartLeafId,
				["gapLeafId"] = result.Leak.GapLeafId,
				["gapPortalId"] = result.Leak.GapPortalId,
				["gapLocation"] = VecJson( result.Leak.GapLocation ),
				["exitPoint"] = VecJson( result.Leak.ExitPoint ),
			};
		}
		return JsonSerializer.Serialize( new Dictionary<string, object?>
		{
			["uri"] = MapCompileLogUri,
			["recorded"] = true,
			["preset"] = result.Preset.ToString(),
			["succeeded"] = result.Succeeded,
			["cancelled"] = result.Cancelled,
			["error"] = result.Error,
			["failedStage"] = result.FailedStage?.ToString(),
			["cancelledStage"] = result.CancelledStage?.ToString(),
			["completedStages"] = result.CompletedStages.Select( s => s.ToString() ).ToArray(),
			["durationSeconds"] = result.Duration.TotalSeconds,
			["bsp"] = BspJson(),
			["pvs"] = PvsJson(),
			["lightmaps"] = LightmapsJson(),
			["leak"] = LeakJson(),
			["issues"] = result.Issues.Select( i => new Dictionary<string, object?>
			{
				["code"] = i.Code,
				["severity"] = i.Severity.ToString(),
				["message"] = i.Message,
				["subject"] = i.Subject,
				["fixHint"] = i.FixHint,
				["location"] = VecJson( i.Location ),
			} ).ToArray(),
			["log"] = result.Log.ToArray(),
			["logPath"] = result.LogPath,
			["outputFiles"] = result.OutputFiles.ToArray(),
		}, Json );
	}

	public static IReadOnlyList<McpPrompt> ListPrompts() => new[]
	{
		new McpPrompt( "sculpt-face-from-reference", "Sculpt a face from a reference photo: measure the photo, build with native features, validate the silhouette.",
			new[]
			{
				new McpPromptArgument( "reference", "Reference photo: a saved ref id, a file path under refs/, or a public image URL.", true ),
				new McpPromptArgument( "view", "Which view the reference shows: front, side, top, three-quarter or close-up.", false ),
				new McpPromptArgument( "notes", "Anything the sculpt must respect: character, proportions, features to keep or change.", false ),
			} ),
		new McpPrompt( "fix-topology-report", "Work through a topology validation report: interpret each problem, fix it, and re-validate until clean.",
			new[]
			{
				new McpPromptArgument( "report", "The validate_topology report text to work from. Omit to run validate_topology first.", false ),
				new McpPromptArgument( "body", "Body id to fix. Omit to fix the whole model.", false ),
			} ),
		new McpPrompt( "export-for-godot", "Export the current model for Godot: validate the export, bake what it needs, and write the file in a supported format.",
			new[]
			{
				new McpPromptArgument( "format", "Export format: glb (default), obj, effigy or riganim. fbx, vmdl and dmx are not supported and will be refused.", false ),
				new McpPromptArgument( "target", "Target use in Godot: character, prop or environment. Shapes which checks run.", false ),
			} ),
		new McpPrompt( "review-model-sheet", "Review a model sheet before finishing: every part needs measured dimensions from a real source, sampled colours, a cross-section, a material and a place in build order.",
			new[]
			{
				new McpPromptArgument( "sheet", "The model sheet JSON to review. Omit to review the sheet of the current document.", false ),
			} ),
		new McpPrompt( "build-a-sealed-room", "Build a sealed room from nothing: six brushes, a player start and a light, then validate and QuickPlay-compile it until it is sealed.",
			new[]
			{
				new McpPromptArgument( "size", "Interior size in inches as WxDxH (Z-up). Default 256x256x128.", false ),
				new McpPromptArgument( "material", "Wall/floor/ceiling material. Default concrete/wall.", false ),
				new McpPromptArgument( "name", "Map name. Default room.", false ),
			} ),
		new McpPrompt( "find-the-leak", "Find the leak in a map that will not seal: read the live issues, compile to locate the gap, plug it with real geometry, and re-validate until sealed.",
			new[]
			{
				new McpPromptArgument( "report", "The map_validate report or issue list to work from. Omit to run map_validate first.", false ),
			} ),
		new McpPrompt( "optimise-this-map", "Optimise a map for compile time and visibility: read the live issues and compile log, detail what does not seal, and re-compile to prove the gain.",
			new[]
			{
				new McpPromptArgument( "focus", "What to optimise for: compile-time-and-visibility (default), lightmap quality, or navigation.", false ),
			} ),
	};

	public static McpPromptResult GetPrompt( string name, IReadOnlyDictionary<string, string>? arguments = null )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A prompt name is required.", nameof( name ) );
		arguments ??= new Dictionary<string, string>();
		return name switch
		{
			"sculpt-face-from-reference" => new McpPromptResult(
				"Sculpt a face from a reference photo.",
				new[] { new McpPromptMessage( "user", SculptFaceText( arguments ) ) } ),
			"fix-topology-report" => new McpPromptResult(
				"Work through a topology validation report.",
				new[] { new McpPromptMessage( "user", FixTopologyText( arguments ) ) } ),
			"export-for-godot" => new McpPromptResult(
				"Export the current model for Godot.",
				new[] { new McpPromptMessage( "user", ExportForGodotText( arguments ) ) } ),
			"review-model-sheet" => new McpPromptResult(
				"Review a model sheet before finishing.",
				new[] { new McpPromptMessage( "user", ReviewModelSheetText( arguments ) ) } ),
			"build-a-sealed-room" => new McpPromptResult(
				"Build a sealed room from nothing.",
				new[] { new McpPromptMessage( "user", BuildSealedRoomText( arguments ) ) } ),
			"find-the-leak" => new McpPromptResult(
				"Find the leak in a map that will not seal.",
				new[] { new McpPromptMessage( "user", FindTheLeakText( arguments ) ) } ),
			"optimise-this-map" => new McpPromptResult(
				"Optimise a map for compile time and visibility.",
				new[] { new McpPromptMessage( "user", OptimiseMapText( arguments ) ) } ),
			_ => throw new ArgumentException( $"Unknown MCP prompt: {name}. Known prompts: sculpt-face-from-reference, fix-topology-report, export-for-godot, review-model-sheet, build-a-sealed-room, find-the-leak, optimise-this-map.", nameof( name ) ),
		};
	}

	static string Required( IReadOnlyDictionary<string, string> arguments, string name )
	{
		if ( !arguments.TryGetValue( name, out var value ) || string.IsNullOrWhiteSpace( value ) )
			throw new ArgumentException( $"Prompt argument '{name}' is required.", nameof( arguments ) );
		return value;
	}

	static string Optional( IReadOnlyDictionary<string, string> arguments, string name, string fallback )
		=> arguments.TryGetValue( name, out var value ) && !string.IsNullOrWhiteSpace( value ) ? value : fallback;

	static string SculptFaceText( IReadOnlyDictionary<string, string> arguments )
	{
		var reference = Required( arguments, "reference" );
		var view = Optional( arguments, "view", "front" );
		var notes = Optional( arguments, "notes", "none given" );
		return "Sculpt a face from the reference photo below. Measure before you build, and build with native features so the result stays editable.\n\n"
			+ $"Reference: {reference}\nView: {view}\nNotes: {notes}\n\n"
			+ "1. Get the photo into refs/ with save_image (tag its view), then measure it with sample_colors: the object's box, long axis, and the width/colour profile along it. Do not guess proportions from memory.\n"
			+ "2. Call catalog first if you are unsure of a feature type's exact parameter fields, defaults and choices. Units are inches, Z-up.\n"
			+ "3. Build with add_feature, one native feature per part (head mass, brow, nose, cheeks, chin, jaw). Use edit_feature on the stable feature id to refine; failed builds roll back, so re-read the error and fix the fields.\n"
			+ "4. Validate the silhouette with validate_silhouette for the reference view, and render a labelled multi-angle evidence set with render. Fix what the evidence shows is wrong; do not claim the face matches until the silhouette check passes.\n"
			+ "5. Keep the model symmetric unless the notes say otherwise (validate_symmetry / symmetrize_model).";
	}

	static string FixTopologyText( IReadOnlyDictionary<string, string> arguments )
	{
		var body = Optional( arguments, "body", "whole model" );
		var report = arguments.TryGetValue( "report", out var r ) && !string.IsNullOrWhiteSpace( r )
			? $"\n\nReport to work from:\n{r}"
			: "\n\nNo report was pasted: run validate_topology first and work from its output.";
		return "Work through this topology report until the model is clean. Fix problems, do not hide them.\n\n"
			+ $"Body: {body}{report}\n\n"
			+ "1. Run validate_topology (pass the body id when one is given) and read every problem: non-manifold edges, inverted winding, boundary edges, degenerate faces, ngons, and the quad/tri split.\n"
			+ "2. Fix each class with the right tool: heal_mesh for non-manifold edges, inverted winding and degenerate faces; edit_op (extrude, inset, bevel, loopcut, knife, spin, slide, bridge, gridfill, symmetrize) for the structural ones. Prefer the smallest fix that closes the problem.\n"
			+ "3. Re-run validate_topology after every fix. The model is only done when a fresh run reports no problems — never claim it is fixed from the fix step alone.\n"
			+ "4. If a fix fails or makes things worse, roll it back with the edit history and try the next approach; report what did not work.";
	}

	static string ExportForGodotText( IReadOnlyDictionary<string, string> arguments )
	{
		var format = Optional( arguments, "format", "glb" );
		var target = Optional( arguments, "target", "character" );
		return "Export the current model for Godot. Validate first, bake what the export needs, and write only a format Geppetto actually supports.\n\n"
			+ $"Format: {format}\nTarget: {target}\n\n"
			+ "1. Run validate_export for the requested format before exporting. Supported formats are obj, glb, effigy and riganim only — fbx, vmdl and dmx are honestly refused, so if the format is one of those, say so and stop instead of inventing a file.\n"
			+ "2. Bake what the target needs with bake_export (bake, paintmaps, spritesheet as the target requires) and check the export preset for Godot with export_preset.\n"
			+ "3. Export with bake_export, then re-open the written file and confirm it loads: the export is only done when the file exists and reads back, not when the export call returns.\n"
			+ "4. If validation fails, fix the model (see the fix-topology-report prompt) and re-validate; do not ship a file that failed validation.";
	}

	static string ReviewModelSheetText( IReadOnlyDictionary<string, string> arguments )
	{
		var sheet = arguments.TryGetValue( "sheet", out var s ) && !string.IsNullOrWhiteSpace( s )
			? $"\n\nSheet to review:\n{s}"
			: "\n\nNo sheet was pasted: read the sheet of the current document (modelsheet_sync / write_sheet) and review that.";
		return "Review this model sheet before it is finished. A sheet is only ready when every part is measured, coloured from a real source, and ordered.\n\n"
			+ $"Sheet:{sheet}\n\n"
			+ "1. Every part needs measured dimensions, and every measurement needs a source you searched or fetched — a dimension with no source is an error, not an estimate.\n"
			+ "2. Colours must be sampled from saved photos with sample_colors, not picked from memory.\n"
			+ "3. Every part needs a cross_section, a material, and a place in build_order. A part missing any of the three blocks the sheet.\n"
			+ "4. Use write_sheet to write the corrected sheet (model-sheet.json + model-sheet.md) and let Geppetto's checks run: fix what they report, then re-check. The sheet is done when Geppetto's checks pass, not when the text looks finished.";
	}

	static string BuildSealedRoomText( IReadOnlyDictionary<string, string> arguments )
	{
		var size = Optional( arguments, "size", "256x256x128" );
		var material = Optional( arguments, "material", "concrete/wall" );
		var name = Optional( arguments, "name", "room" );
		return "Build a sealed room from nothing. Six brushes seal it; a player start and a light make it playable. Validate and compile until it is sealed — never claim it is sealed from the build step alone.\n\n"
			+ $"Map name: {name}\nInterior size (inches, Z-up): {size}\nMaterial: {material}\n\n"
			+ "1. Read geppetto://map first: if a map is already open, say so and stop instead of overwriting it.\n"
			+ "2. Build the six sealing brushes with map_brush_create, one call per brush, 16-unit walls around the interior: floor, ceiling, -X wall, +X wall, -Y wall, +Y wall. Every brush is a convex box with the given material on all faces; detail brushes do NOT seal, so none of these six may be detail.\n"
			+ "3. Place the entities with map_entity_place: info_player_start at the room centre, 32 units above the floor, and a light near the ceiling. Check the class names and keyvalues against geppetto://map/entities first — an unknown class or keyvalue is refused, not guessed.\n"
			+ "4. Run map_validate and read every issue it reports. Fix what it names with map_brush_edit / map_entity_edit; a missing material or a bad brush is a build error, not something to work around.\n"
			+ "5. Run map_compile with the QuickPlay preset (BSP-only) and read geppetto://map/compile-log: the BSP stage must succeed and the leak check must report sealed. If it leaks, follow the find-the-leak prompt.\n"
			+ "6. Only once validation is clean and the compile reports sealed: set face materials with map_face_material if the room needs more than one material, and cut openings with map_brush_clip — never before the room seals.";
	}

	static string FindTheLeakText( IReadOnlyDictionary<string, string> arguments )
	{
		var report = arguments.TryGetValue( "report", out var r ) && !string.IsNullOrWhiteSpace( r )
			? $"\n\nReport to work from:\n{r}"
			: "\n\nNo report was pasted: run map_validate first and work from its output.";
		return "Find the leak in a map that will not seal. Locate the gap, plug it with real geometry, and re-validate until the compile reports sealed.\n\n"
			+ $"Report:{report}\n\n"
			+ "1. Read geppetto://map/issues (the live IssueEngine output) and run map_validate. A LEAK issue names the gap; an UNSEALED_MAP issue means a whole wall is missing. Read both before touching a brush.\n"
			+ "2. Run map_compile with the QuickPlay preset and read geppetto://map/compile-log: the leak result names the gap leaf, the gap portal and the gap location, and a leaking compile writes a .lin point file — follow that path from the player start to the gap.\n"
			+ "3. Plug the gap with real sealing geometry: map_brush_create for a missing wall section, map_brush_edit to close a 1-unit gap between brushes. Detail brushes do NOT seal, so the plug must be a structural brush; never plug a leak with a detail brush, a displacement, or a nodraw face.\n"
			+ "4. Re-run map_validate and map_compile (QuickPlay) after the fix. The map is only sealed when a fresh compile reports sealed and the issue list is clean — never claim it is sealed from the fix step alone.\n"
			+ "5. If the gap is not where the log says, re-read geppetto://map and check the brush list: a brush in the wrong place or with the wrong size is the usual cause, not the leak finder.";
	}

	static string OptimiseMapText( IReadOnlyDictionary<string, string> arguments )
	{
		var focus = Optional( arguments, "focus", "compile-time and visibility" );
		return "Optimise a map for compile time and visibility. Measure first, change what the numbers blame, and re-compile to prove the gain — report before/after numbers, not adjectives.\n\n"
			+ $"Focus: {focus}\n\n"
			+ "1. Read geppetto://map/issues and geppetto://map/compile-log before changing anything: BSP leaf and portal counts, PVS set counts, lightmap face counts and means, and the compile duration are the baseline. Run map_validate and map_compile (Preview preset) if no compile is recorded yet.\n"
			+ "2. Detail what does not seal: small trim, pillars and decoration become func_detail with map_brush_edit — detail brushes are real visible geometry with lightmaps, but they are excluded from the BSP solid, so the leaf and portal counts fall. Never detail a brush that seals the map.\n"
			+ "3. Cut what the player never sees: map_face_material with nodraw on faces hidden inside solids or outside the playable space, and map_brush_clip to remove dead volume. Do not delete brushes the map needs.\n"
			+ "4. Bake only what the focus asks for: map_bake_lightmap for lightmap quality, map_navmesh when the focus is navigation, map_skybox_cubemap only when the skybox changed. Do not run a Final compile to check an optimisation — Preview is the measuring preset.\n"
			+ "5. Re-run map_validate and map_compile (Preview) and compare the new geppetto://map/compile-log against the baseline: leaves, portals, PVS sets, lightmap faces and duration. The optimisation is only done when the numbers improve and validation stays clean; if they got worse, revert with the edit history and try the next change.";
	}
}
