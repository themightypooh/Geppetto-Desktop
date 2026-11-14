# Handoff — hammer-ai-buildscript-verbs-map (daxter-hammer-ai-buildscript)

**Status: implementation done, NOT runtime verified.** Isolated harness: `buildscriptmap` group **15/15 PASS** (see "Checks run"). Full project build + official test-runner runs are deferred to the final integration gate, per the PO workflow override.

## Task

Board id `hammer-ai-buildscript-verbs-map` — "Hammer Desktop AI: BuildScript verbs for native map tools." Reassigned from stopped worker muse-3-desktop-hammer-64. Spec: implement `kernel/Effigy/Script/BuildScript.Map.cs` with BuildScript verbs for brush create/edit/clip/vertex, displacement create/sculpt, per-face material/UV, entity place/edit + I/O add, grouping/visgroups, VMF/.map import/export, native compile (Quick/Preview/Final), validation/Issue list, lightmap bake, navmesh, skybox/cubemap. Verbs operate on MapDocument through the same `add`/`set` key=value grammar as existing verbs; each returns a named result or named error, never a silent no-op. Acceptance: AI can build/edit/validate/compile a map end-to-end through BuildScript verbs, via ScriptLive. No new scripting surface — verbs only. No Desktop UI in this ticket.

## Changed files (on the PC, /home/poo/Desktop/geppetto-desktop)

| File | What |
|---|---|
| `kernel/Effigy/Script/BuildScript.Map.cs` | **New.** The 20 verbs (see API). ~1000+ lines, `#nullable enable`. |
| `kernel/Effigy/Script/BuildScript.cs` | **Hook only** (SEAM-sanctioned): `Mutates()` lists `map_validate`/`map_export` as non-mutating; `Command` default branch calls `TryRunMapVerb` after the `RigCommand` check, before the unknown-command error. Backup: `/tmp/BuildScript.cs.bak-daxter-20261113`. |
| `tests/BuildScriptMapTests.cs` | **New.** Group `buildscriptmap`, 15 checks (see Tests). |
| `tests/fixtures/hammer/hammer-ai-buildscript-verbs-map/sealed-room.effigy-script` + `README.md` | **New.** Sealed-room acceptance script (map=room1) + full verb grammar reference. |
| `kernel/Effigy/Map/MapDocument.cs` | **Two correctness fixes OUTSIDE this ticket's files field** (see below). Backup: `/tmp/MapDocument.cs.bak-daxter-20261113` (pre-both-fixes). |

## API — the 20 verbs (exact SEAM order)

`map_brush_create, map_brush_edit, map_brush_clip, map_brush_vertex, map_displacement_create, map_displacement_sculpt, map_face_material, map_face_uv, map_entity_place, map_entity_edit, map_entity_io_add, map_group, map_visgroup, map_import, map_export, map_compile, map_validate, map_lightmap_bake, map_navmesh, map_skybox`

Exposed as `BuildScript.MapVerbNames` (string[]); verbs stay OUT of `VerbNames`. Grammar: `map_verb [positional] key=value …`, `map=name` per line (sessions are process-wide static, keyed by map name, default `"map"`; state lives OUTSIDE PartStudio so it survives the AiSuiteTools round-trip). Full grammar is in the fixture README. Highlights: create positional = the brush id (and name unless `id=` overrides); `map_compile preset=quick|preview|final|quickplay` (quick→QuickPlay preset; there is no `Quick` preset); `map_validate` always returns Ok — issues are the result, not a failure; `map_lightmap_bake` refuses glb (`glb-export-stub`, ExportGlb is a header-only stub); every bare verb fails named except validate/lightmap/skybox/group-list/visgroup-list, which legitimately succeed on empty maps.

## MapDocument.cs fixes by this ticket (flagged — not in my files field)

Both were blocking defects, not polish; both verified by the harness (the sealed-room/displacement checks fail without them). Worker 12 / the integrator can keep or revert them; the backups are on the PC.

1. **`MapBrushData.Validate` opposite-normal bug.** The duplicate-plane check tested `|d1-d2| < eps` for BOTH same-direction and opposite normals. For opposite normals the same plane is `|d1+d2| < eps`, so every symmetric box (`MapBrushData.Box` with -min == max on an axis — i.e. the sealed-room floor/walls) failed its own Validate. Fixed by splitting the directions (same-dir `|d1-d2|`, opposite-dir `|d1+d2|`), matching `MapBrush.Validate` (which only flags dot ~ +1).
2. **`RemoveDisplacement` fold key bug.** The fold matched `d.Id == entry.TargetId`, but TargetId is `"brushId#faceIndex"` while Id is `disp-N` — so the remove could never match and `RemoveDisplacement` always threw transactionally in the fold (the pre-check uses the correct (BrushId, FaceIndex) key and passed, then the fold failed). The fold now matches (BrushId, FaceIndex). Sculpt persistence depends on this (RemoveDisplacement + AddDisplacement of the updated clone; `SetDisplacement` zeroes Alphas, so it is never used for sculpt).

Also verified while working (stale claims corrected in the code header + fixture README): the `EntityOutput`→`EntityIoOutput` rename has LANDED — zero bare `EntityOutput` in MapDocument.cs/MapDocumentIo.cs, `using System.IO;` present, both compile in the harness. Session `EntityIoList` is now a design choice (MCP pattern), not a workaround; `doc.AddIo` may be usable — integrator's call.

## Design decisions (encoded in the file header + fixture README)

- **Session/state:** `MapSession` {MapDocument, LinkedGroupSet, EntityIoList, lazy EntityRegistry + source, id counters, DetailBrushes, Navmesh, Cubemaps, LastQuickPlay, LastCompile}. Import resets groups/io/navmesh/cubemap and advances id counters past imported ids.
- **Brush shapes:** box via `MapBrushData.Box`; wedge/cylinder/sphere/arch via MCP-style plane builders (adapted from McpMapTools.cs) emitting `MapBrushFace` → `MapBrush.FromPlanes` → `MapBrushData.FromMapBrush`. `MapBrushPrimitives` is NOT used (built against nonexistent `MapFace`/`MapPlane`).
- **Face-level edits** (material, UV, rotate/scale, clip, vertex, visgroup assign) go through a ReplaceBrush helper: snapshot displacements + tied entities → RemoveBrush → AddBrush (same Id) → re-add displacements → restore entity ties. Whole-brush material uses `doc.SetBrushMaterial`; translate uses `doc.MoveBrush`.
- **Displacement sculpt persistence:** RemoveDisplacement + AddDisplacement of the updated clone (preserves Offsets AND Alphas). Create: brush+face path via `TryComputeFaceVertices` (first 4 corners) + `Displacement.Create`; also a standalone `corners=` form (not tied to a brush; sculpt refuses it named).
- **Entities:** place validates via transient `MapEntities` (registry), stores `MapEntityData`. I/O rows: session `EntityIoList`; source resolves via doc FindEntityByTargetName/FindEntity; unknown target fails `io-target-not-found` (unless `!`-special).
- **Registry resolution:** `registry=` path → walk-up MCP fixture registry → walk-up core-source1.json → embedded 11-class registry (fixture's 10 + env_cubemap, fuller keyvalues/inputs). Neither file registry declares env_cubemap or (fixture) light `_light`; the sealed-room script does not set `_light`.
- **Import/export:** vmf/map/gmap. Import skips <4-face/invalid-plane brushes with warnings, never silent fixes; displacement grids are NOT imported (warned once). `map_compile` empty map fails `compile-empty-map`; QuickPlay failure names `quickplay-unsealed` / `quickplay-no-spawn`.
- **Navmesh/skybox:** `map_navmesh` calls real `NavMeshGen.Generate` (QuickPlay bake errors surface as `navmesh-failed (code): ...`), then `PlaytestLoop.PreLaunchValidation(..., navmeshPresent: true)`. `map_skybox cubemap=1` calls real `CubemapBake.Bake` (probes from env_cubemap doc entities; none → `cubemap-no-probes`).
- **Clip semantics:** `PlaneFromTwoPoints` normal is `Cross(line, axis)` — for a vertical line with axis=z the normal is +X, so `keep=front` keeps the +X side. The named no-op test uses `keep=back` for a plane outside the brush on the +X side.
- **Vertex:** a single-vertex move on a box corner always breaks one face (3 mutually perpendicular faces share it) and the verb correctly refuses it named. The success check uses a wedge: every wedge vertex accepts a y-move (probe 2026-11-13: v0..v5 all OK with delta=0,8,0).

## Tests (tests/BuildScriptMapTests.cs, group `buildscriptmap`, 15 checks)

1. MapVerbNames == SEAM list in order. 2. Sealed-room acceptance (floor/walls/ceiling 512×512, info_player_start, light, door-panel brush + func_door targetname=door1, logic_auto targetname=auto1, I/O OnMapSpawn→door1 Open delay=1, map_validate + map_compile preset=quickplay; asserts "quickplay sealed"). 3. Shapes wedge/cylinder/sphere/arch/planes. 4. Edit move/rotate/scale/snap/setmaterial. 5. Clip cut + split + named no-op. 6. Vertex move (wedge, see above). 7. Displacement on floor face=5 power=3 (grid 9x9) + raise/smooth sculpts. 8. Face material + UV. 9. Entity named errors (unknown class, NotPointEntity, io-target-not-found, entity-not-found). 10. Group define/list. 11. Visgroup define/assign/setvisible/list. 12. Export/import round-trip vmf, map, gmap. 13. Bare verbs fail named (15 pairs). 14. Lightmap bake obj + navmesh + skybox (incl. cubemap resolution=16). 15. Native compile preset=quick. Every check uses a fresh map name (sessions are process-wide static).

## Checks run

- **Isolated harness** `/tmp/buildscriptmap-harness/` (builder: `~/workspace/hammer-map-recon/setup-harness.sh` on Daxter's VM, re-runnable; copies the current repo `kernel/Effigy/**` + my test file; harness-only exclusions/patches, real repo files never touched): **build 0 errors, group `buildscriptmap` 15/15 PASS** (final run 2026-11-13 ~17:5x EST). Harness exclusions (shared-tree-broken, owned by other tickets): MapBrushPrimitives, InstancesCollab, MapInstanceLibraries, OverlaysDecals, HintSkip, ClipPolygon, MapExportRoundtrip, StarterTemplates, BuildScript.MapPass2.cs, ApiCatalogMapPass2.cs, MapDocumentOverlays, SkyboxWorldspawn, MapDocumentSkybox. Harness-copy-only patches: MapLayers.cs:299 ternary, AssetRegistries.cs CS0051 (private nested BrushReplacement → internal). EntityOutput→EntityIoOutput rename patch found 0 occurrences (rename already landed).
- Bugs found and fixed during harness runs (all in my files unless noted): `MapDocument.FindDisplacement` is keyed by (brushId, faceIndex) not id; QuickPlayBrush/Entity/Displacement use positional ctors; clip enums are `ClipMode`/`ClipOutcome` not `MapClip*`; `Vec3` has no 1-arg ctor; `map_brush_create` positional is now the brush id; `map_navmesh` now also catches `QuickPlayException` (Generate bakes with QuickPlay first; empty-map throws that, not NavMeshException).

## Checks deferred (final integration gate)

- Full project build (`dotnet build` of the real tree — currently blocked by the shared-tree breaks below, not by this ticket).
- Official test-runner runs of the `buildscriptmap` group AND the `aisuitetoolsmap` group (check 8 there calls each verb bare; my verb audit: create/edit/clip/vertex/displacement create+sculpt/face material+uv/entity place+edit+io/import/export/navmesh fail named bare; compile fails `compile-empty-map`; validate/lightmap/skybox/group-list/visgroup-list legitimately succeed bare).
- `TestRegistry.cs` registration line for the integrator (coordinator-owned file — do NOT add it here): `BuildScriptMapTests.Register(registry, settings)`.

## Blockers / notes for the integrator

- **Shared tree does not compile** (not this ticket): MapBrushPrimitives (MapFace stub), InstancesCollab (dup InstanceIoProxy), MapInstanceLibraries (CS0050), OverlaysDecals/HintSkip/ClipPolygon/MapExportRoundtrip/StarterTemplates (old Displacement MapBrushFace), BuildScript.MapPass2.cs (PreviewFogController, ReRunSignal, HotloadIssue errors), ApiCatalogMapPass2.cs (depends on MapPass2), MapDocumentOverlays/SkyboxWorldspawn/MapDocumentSkybox (types from the excluded files). **New since ~17:45:** untracked `kernel/Effigy/Map/AssetRegistries.cs` landed with a real CS0051 (private nested `BrushReplacement` vs internal ctor taking `List<BrushReplacement>`) — harness fixes it in-copy; the real file needs the same one-word fix. Its users MapPacker.cs + CompileRunner.cs mean it cannot just be excluded.
- `McpMapTools.BuildScriptVerbFor` maps its `map_io_add` tool to verb name `"map_io_add"`, which does not exist — the BuildScript verb is `map_entity_io_add`. The MCP ticket's seam mapping needs that rename.
- Group linked copies live in the group set only, NOT added to the document (logged as such).
- `CompilePreset` has no `Quick` (quick→QuickPlay, preview→Preview, final→Final). `quickplay` is a preset value of `map_compile`, not a verb.
- `ExportGlb` is a minimal stub (header + asset JSON, no mesh payload) — `map_lightmap_bake` refuses glb named until core GlbExport wiring lands.
- `detail=1` on brush create is recorded in the session detail set only; `CompileInput.FromDocument` does not carry detail.
