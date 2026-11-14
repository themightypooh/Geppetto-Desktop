# Handoff — hammer-ai-mcp-map-tools (daxter-hammer-ai-mcp)

Status: implementation done, NOT runtime verified. Full build + test run deferred to the final integration gate (PO workflow override 2026-11-13).

## What the previous worker had already done (muse-3-desktop-hammer-65, stopped)

- `McpMapTools.cs` (repo root, 2023 lines): all 18 map_* tools, session provider, `ListTools()` / `CallAsync`, `BuildScriptVerbFor` seam, `DestructiveToolNames`.
- `tests/McpMapToolsTests.cs` (496 lines): group `mcpmaptools`, one check per tool group over a sealed room built through the tools; invalid calls return named errors.
- `tests/fixtures/hammer/hammer-ai-mcp-map-tools/entity-registry.json`: 10 classes, matches the embedded `DefaultRegistryJson`.
- `tests/TestRegistry.cs`: `McpMapToolsTests.Register( registry, settings );` already present (working-tree diff vs HEAD).

## What this session fixed (2 real compile errors + 2 stale comments)

1. **`McpMapTools.cs` — `ReadUv` was missing its opening brace** (line ~895). The method body parsed as a syntax error; the file could not compile. Fixed: added `{`.
2. **`McpMapTools.cs` — `IoAdd` used `output.Target`** (line ~1333). `EntityIoOutput` has `TargetName`, not `Target` (the `Target` field lives on `VmfOutput`, a different type — the two other `output.Target` uses at ~1596 are `VmfOutput` and are correct). Fixed to `output.TargetName`.
3. **Stale header comment**: "No MapDocument type has landed" is wrong — `kernel/Effigy/Map/MapDocument.cs` exists in the working tree (`hammer-map-document-model`, in-progress, owner `daxter-hammer-map-document`). Comment corrected; the session provider stays interim until that ticket lands and the integrator adapts the tools onto it. Tool names/schemas/results unchanged.
4. **Stale test header**: "NOT registered in TestRegistry.cs" is wrong — it IS registered. Comment corrected.

## What this session wired (the missing piece: tools did not exist on the server)

`Mcp/McpServer.cs` (untracked, this push's file) now wires the map tools, following the modeling-tools pattern and the seam the safety ticket (`hammer-ai-safety-gates-map`, done) explicitly left in `McpSafety.cs`:

- `Run()`: `Gates.ValidateMapDefinitions( MapTools.ListTools() )` after the modeling validation.
- `ListTools()`: appends `MapTools.ListTools()` (18 map_* tools) after the modeling tools, `echo` last. Total becomes 43 AiSuiteTools + `run_script` + 18 map + `echo` = 63. `McpServerTests` asserts `tools.Count >= 45`, so it still passes.
- `CallTool()`: `SafetyGates.IsMapTool( name )` routes to new `CallMapTool()`, which runs `Gates.BeginMapCall( "mcp", name, args, map, ... )` (map ref from `args["map"]`, default `McpMapTools.MapName` = `"map"`; `confirm` from args), dispatches to `MapTools.CallAsync`, scrubs the result with `SafetyGates.ScrubMapOutput` then the generic `Scrubbed` wrapper. `ArgumentException` from the provider (unknown tool / missing required arg / bad enum) is `Refuse`d in the audit log and rethrown so the transport maps it to `-32602`, not a crash.
- Lazy `static McpMapTools MapTools` instance next to `Modeling`, same pattern. No `Program.cs` change (integrator-owned; `--mcp` dispatch already calls `McpServer.Run`).

## API — the 18 tools (all take optional `map`, must be `"map"`)

`map_brush_create` (box/wedge/cylinder/sphere/arch/planes), `map_brush_edit` (translate/setMaterial/setUv), `map_brush_clip` (cut/split, MapClip), `map_brush_vertex` (MapVertexEdit), `map_displacement_create` (power 2/3/4), `map_displacement_sculpt` (raise/lower/smooth/flatten/paintAlpha), `map_face_material`, `map_entity_place` (point/brush, registry-validated), `map_entity_edit`, `map_io_add` (target must resolve or be an engine special like `!self`), `map_group` (define/link/editMember/unlink/list), `map_visgroup` (define/setVisible/assign/list), `map_import_export` (VMF / Valve 220, import replaces the session), `map_compile` (quick/preview/final, native `CompileRunner`), `map_validate` (`IssueEngine` + entity validation), `map_bake_lightmap` (`MapBakeExport`, OBJ/GLB export), `map_navmesh` (status only), `map_skybox_cubemap` (skybox real, cubemap not-implemented).

Results are structured JSON (`{tool, map, ...}`); well-formed failures return `IsError` with a named code (`brush-not-found`, `brush-invalid`, `io-target-not-found`, `import-parse-failed`, `visgroup-not-found`, `displacement-not-found`, `entity-not-found`, `group-invalid`, `invalid-uv`, `io-invalid`, ...); invalid params throw `ArgumentException` (transport → `-32602`).

## Audit result (every kernel call checked against the tree)

All kernel references resolve to real types/methods — verified by grep over the repo (audit scripts kept in the VM at `~/workspace/hammer-ai-mcp-map-tools/audit*.py`, outputs `audit2.out`/`audit3.out`/`audit4.out`):

- `MapBrush.Box/FromPlanes/Validate/ToMesh/Vertices/Volume/Centroid/IsValid/PlaneEps`, `MapBrushFace` both ctors + `MaterialId`/`Uv`/`SignedDistance`, `FaceMaterial` ctors, `FaceUv` ctor/`PlaneAxes`/`Validate`/`Default`, `ToolTexture.NoDrawName`.
- `MapClip.Clip` + `MapClipOptions`/`ClipMode`/`ClipOutcome` (incl. `NoOpPlaneOutside`, `clip-two-points-coincident`), `MapVertexBrush`/`MapVertexEdit.MoveVertex/MoveVertexBy`/`MapVertexEditResult`/`FacePlane`.
- `Effigy.Map.MapBrushFace.FromCorners`, `Displacement.Create` (power 2..4 enforced by `GridSizeForPower`), `DisplacementSculpt.ApplyDab` + `BrushSample`/`BrushAlpha`/`BrushFalloff`/`DisplacementSculptOptions`.
- `EntityRegistry.Load/LoadFromJson/Count/Find`, `MapEntities` + generic `PlaceBrushEntity<TBrush>` (type-inferred from `List<MapBrush>`), `MapEntity` (`Kind` is `EntityKind`, `Brushes` is `IReadOnlyList<object>`), `SetKeyvalue/SetOrigin/SetAngles` (unknown key throws), `TryParse/FormatOrigin/Angles`, `EntityIoList.Add` (returns error string or null), `EntityIoOutput` ctor/`TargetName`.
- `LinkedGroupSet` + all four result types, `LinkedMemberEdit` statics, `VmfIo`/`Map220Io` Parse/Write, `VmfDocument`/`VmfBrush`/`VmfFace.ToPlane`/`VmfEntity`/`VmfOutput`/`VmfVisGroup`/`VmfUvAxis`/`VmfVec`.
- `CompileRunner.Run` + `CompileInput`/`CompileBrush`/`CompileFace`/`CompilePreset`/`CompileOptions` + result members (`Succeeded`, `FailedStage`, `Leak.IsLeaked/Path`, `Bsp.LeafCount`, `Lightmaps.Faces`).
- `IssueEngine.Run`/`Rules`, `IssueDocument`/`IssueBrush`/`IssueFace`/`IssueEntity`, `MapBakeExport.Bake/ExportObj/ExportGlb/GlbNote` + `MapBakeBrush.FromMapBrush(brush, name)` + `MapBakeDocument`, `PlaytestLoop.PreLaunchValidation` + `PreLaunchResult`/`PreLaunchIssue`, `SkyboxPreview.BuildPreview` + `SkyboxPreviewScene`.
- No TODO / NotImplemented / stub markers in `McpMapTools.cs` or the tests (the two honest `not-implemented` results below are data, not stubs).

## Honest gaps (named, not faked)

- **`map_navmesh` generation is `not-implemented`** in the result: no navmesh type has landed in the kernel; the tool runs `PlaytestLoop.PreLaunchValidation` and reports `navmeshRequired` / `NAVMESH_MISSING`. Generation is a follow-up ticket.
- **`map_skybox_cubemap` capture is `not-implemented`** in the result: skyname + `sky_camera` + `SkyboxPreview` are real; no cubemap builder exists in the kernel. Capture is a follow-up ticket.
- **`map_brush_delete` / `map_entity_delete`** are in `SafetyGates.MapTools` (safety spec) but have NO implementation in `McpMapTools`: they pass `BeginMapCall` and fail as unknown map tools (`-32602`), and are not in `tools/list`. Delete verbs are a follow-up ticket.
- **MapBrushPrimitives is broken (not this ticket)**: it builds against the stub `MapFace`/`MapPlane` API that never landed; the real API is `MapBrushFace`. `McpMapTools` correctly does NOT call it (own plane builders for wedge/cylinder/sphere/arch). Reconciling it is the brush-primitives / integrator job. The shared tree does not compile today for this and other unrelated reasons (see below).
- **BuildScript seam**: `BuildScript.Map.cs` does not exist yet (`hammer-ai-buildscript-verbs-map`, in-progress, owner `daxter-hammer-ai-buildscript`). Tools call the kernel APIs directly; `BuildScriptVerbFor()` documents the verb each tool will route through. No second scripting surface invented.
- **Session, not MapDocument**: see above. When `hammer-map-document-model` lands, the integrator adapts the session onto `MapDocument`; tool names/schemas/results stay the same.

## Files changed by this session

- `McpMapTools.cs` — 2 compile fixes + header comment corrections (was already written by the previous worker).
- `tests/McpMapToolsTests.cs` — header comment correction only.
- `Mcp/McpServer.cs` — map-tools wiring (`MapTools` lazy instance, `ValidateMapDefinitions` at startup, `tools/list` append, `CallMapTool` dispatch).
- `handoffs/hammer-ai-mcp-map-tools-daxter.md` — this file.
- NOT touched: `tests/TestRegistry.cs` (registration already present), `Program.cs`, `McpSafety.cs` / `GateCheck.cs` (safety ticket's, done), `Mcp/README.md` (still documents 43 + `run_script` + `echo` — integrator should update the count to 63).

## Checks run (cheap only, per PO override)

- Brace/paren/bracket balance delta vs pre-edit copies: `McpMapTools.cs` braces −1 → 0 (exactly the missing `{`); `McpMapToolsTests.cs` and `McpServer.cs` unchanged/balanced. (Naive counts include string-literal artifacts: `McpMapTools.cs` parens +2 / brackets −2, identical before and after the edit.)
- Full kernel-reference audit by grep (see "Audit result" above): all references resolve; the only two bad ones were fixed.
- Tool-name list verified: 18 tools, matches the spec list exactly (the brief's "19" was a miscount; the spec enumerates 18).
- Fixture verified: `tests/fixtures/hammer/hammer-ai-mcp-map-tools/entity-registry.json` loads 10 classes and matches the embedded `DefaultRegistryJson`.
- `McpServerTests` tools/list assertions are `>= 45`, so the 18 added tools do not break them.
- Invalid-input path read end-to-end: unknown tool / missing required arg / non-object args / bad `map` ref → `ArgumentException` → transport `-32602`; unknown brush/entity/displacement ids → named `IsError` results, session unmutated (tests assert this).

## Checks deferred (final integration gate)

- Full project build (`dotnet build`) and the `mcpmaptools` test-group run — NOT run here per the PO override. The shared tree is known not to compile today for unrelated reasons (see "Blockers" below), so a green run was not achievable from this ticket alone.
- Numeric test expectations are the previous worker's computations, not execution-verified: cylinder volume 3258.4, arch volume 1608.5, sphere face count 50, bake face count 36 on the sealed room, clip half-volume 1048576. The gate must run the group and confirm them.
- Live MCP transcript check (`--mcp` stdio: `tools/list` shows the 18 map tools, one `tools/call` round-trips) — not run here; needs the gate + sandbox flag (`--mcp` refuses to start without it, `GateCheck`).

## Blockers (not this ticket's files — do not fix here)

- Shared tree does not compile today: `MapBrushPrimitives.cs` references nonexistent `MapFace`/`MapPlane`; sibling workers also reported `MapDocument.cs` using `EntityOutput` where it means `EntityIoOutput` + a missing `using System.IO;`, `InstanceIoProxy` defined twice, `MapInstanceLibraries.cs` CS0050, `MapLayers.cs:299` CS0173, `ApiCatalog.cs` referencing nonexistent `BuildScript.MapVerbNames`, and several files building against the old displacement `MapBrushFace`. All belong to their owning tickets / the final integrator.
- `BuildScript.Map.cs` missing (worker 64, in progress) — blocks the BuildScript seam, not the MCP tools themselves.
- `MapDocument` in progress (sibling worker) — blocks the session→document adaptation, not the MCP tools themselves.
