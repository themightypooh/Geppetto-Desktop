# hammer-ai-aisuitetools-apicatalog-map — seam contract

Owner: muse-3-desktop-hammer-69. Depends on `hammer-ai-buildscript-verbs-map`
(worker 64, in-progress 2026-11-13; `BuildScript.Map.cs` NOT in the tree yet).

## What this ticket owns

- `kernel/Effigy/Script/ApiCatalog.cs` — map-verb catalog wiring (this ticket only;
  worker 64 must NOT edit it).
- `AiSuiteTools.cs` (repo root) — `map_command` tool + `MapVerbs` (this ticket only).
- `tests/AiSuiteToolsMapTests.cs` — group `aisuitetoolsmap`.
- This directory — the seam spec worker 64 builds against.

Everything else is READ-ONLY, including `BuildScript.Map.cs` (worker 64) and
`tests/TestRegistry.cs` (coordinator-owned; temp-register only to run the group,
then revert byte-identical).

## The verb set (filed spec — one verb per capability, `map_` prefix)

| Verb | Backing kernel API (primary) | Notes |
|---|---|---|
| `map_brush_create` | `Effigy.Map.MapBrushPrimitives` | Box/Wedge/Cylinder/Sphere/Arch |
| `map_brush_edit` | `Effigy.MapTransform` | Move/rotate/scale/snap; `MapBrush` is the model it edits |
| `map_brush_clip` | `Effigy.MapClip` | |
| `map_brush_vertex` | `Effigy.MapVertexEdit` | |
| `map_displacement_create` | `Effigy.Map.Displacement` | |
| `map_displacement_sculpt` | `Effigy.Map.DisplacementSculpt` | |
| `map_face_material` | `Effigy.FaceMaterial` | |
| `map_face_uv` | `Effigy.FaceUv` | Offset/scale/rotation/lightmap scale |
| `map_entity_place` | `Effigy.MapEntities` | |
| `map_entity_edit` | `Effigy.MapEntities` | |
| `map_entity_io_add` | `Effigy.EntityIoList` | No bare `EntityIo` type exists; `EntityIoList.Add` is the add API. |
| `map_group` | `Effigy.Map.LinkedGroupSet` | No bare `LinkedGroups` type exists; `LinkedGroupSet` is the group store. |
| `map_visgroup` | `Effigy.Map.VmfVisGroup` | |
| `map_import` | `Effigy.Map.VmfIo` | `format=vmf\|map`; `.map` side is `Map220Io` |
| `map_export` | `Effigy.Map.VmfIo` | `format=vmf\|map`; `.map` side is `Map220Io` |
| `map_compile` | `Effigy.CompileRunner` | `preset=quick\|preview\|final` (see below) |
| `map_validate` | `Effigy.MapCheck` | Returns the Issue list (`MapCheckReport`) |
| `map_lightmap_bake` | `Effigy.MapBakeExport` | Compile-pipeline stage is `CompileRunner` LightmapBake |
| `map_navmesh` | `Effigy.Map.PlaytestLoop` | HONEST GAP — see below |
| `map_skybox` | `Effigy.Map.SkyboxPreview` | Covers cubemap too — see below |

Machine mirror: `map-verbs.json` in this directory. The test file's inline
`SpecVerbs` array is authoritative; if worker 64 lands a different set, the
`map verbs equal the filed spec list` check fails and the coordinator
reconciles the spec — do NOT silently edit the test to match a drifted list.

## Seam worker 64 must provide (in `kernel/Effigy/Script/BuildScript.Map.cs`)

`BuildScript` is already `public static partial class`, so the file is one
more partial part. It must expose:

1. `public static readonly string[] MapVerbNames` — exactly the 20 verbs
   above, in this order. `ApiCatalog.MapVerbNames()` reads it, exactly like
   `VerbNames()` / `RigVerbNames()`. Map verbs stay OUT of `VerbNames`
   (they live in `MapVerbNames` only) so the catalog has one source of truth.
2. Verb handling in `BuildScript.Command`'s switch (or a `TryRunMapVerb`
   called from it before the unknown-command error): each verb calls its
   backing API and returns a named result or a named error — never a silent
   no-op. Unknown verbs keep the existing `Unknown command '…'` failure.

## Honest gaps (registered, NOT phantom — they return named errors)

- **Navmesh.** No navmesh builder type exists in the kernel.
  `PlaytestLoop.PreLaunchValidation` documents it
  ("no navmesh type has landed yet") and reports `NAVMESH_MISSING` when a
  map needs a navmesh and none is present. `map_navmesh` must return that
  named capability error (MapCsg's `csg-no-boolean-provider` is the
  precedent: a named capability error is an honest implemented behaviour,
  not fake success) until a builder lands.
- **Cubemap.** No cubemap builder type exists anywhere in the kernel — the
  only mention is the `map.bake.cubemap` action id in `KeybindRegistry`.
  `map_skybox` covers skybox + cubemap (`cubemap=true`); the cubemap half
  returns a named capability error until a builder lands.
- **No `MapDocument` type exists.** The dep spec says verbs "operate on
  MapDocument" — there is no such class in the kernel (per-subsystem docs
  are `VmfDocument`, `QuickPlayMap`, `MapBakeDocument`,
  `InMemoryMapCheckDocument`, `MapStateSnapshot`). Worker 64 defines where
  map state lives. Constraint from this side: `AiSuiteTools.RunScriptTool`
  round-trips the `PartStudio` through `StudioDocument` before running the
  script, so map state that must survive a `map_command` call needs a home
  that survives that round-trip, or a home outside the `PartStudio`.

## Open reconciliation points (coordinator decides, test flags drift)

- `map_compile` presets: `quick|preview|final`. Quick Play is
  `preset=quickplay` in this spec (BSP-only bake + spawn/walk report;
  headless: no actual play). If worker 64 splits it out as `map_quickplay`,
  the spec-list check fails and the spec is reconciled then.
- `map_skybox` vs a separate `map_cubemap` verb: one verb in this spec
  (see cubemap gap above). Same drift rule.
- The 50-action `KeybindRegistry.DefaultCatalogue()` (dotted ids like
  `map.brush.create`, incl. tool-selection and CSG actions) is a DIFFERENT
  catalogue — keybind actions, not script verbs. Do not conflate the two;
  `KeybindRegistry.ImportCatalogue()` is where they are reconciled when
  the verbs land.

## Verification

- PO override 2026-11-13: edits only, no full build; DONE = edits complete
  + verified by reading, NOT build-verified; the final integration gate
  owns the full build.
- Named group only: `aisuitetoolsmap` (9 checks). `TestRegistry.cs` is
  coordinator-owned: temp-register to run, then revert byte-identical.
- `BuildScript.Map.cs` had NOT landed when this ticket was implemented, so
  the shared tree does not compile with this ticket's `ApiCatalog.cs`
  reference to `BuildScript.MapVerbNames` (expected; see handoff). The
  group was verified in the isolated harness (verbatim copies of this
  ticket's files + stubs, incl. a stub `BuildScript.MapVerbNames` carrying
  exactly this spec list) — see the handoff report for the exact command
  and pass count. Never claim a pass count not actually observed.
