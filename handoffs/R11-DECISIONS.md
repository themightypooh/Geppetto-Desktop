# R11 — wire-or-delete decisions (2026-10-20, claude subagent)

Re-checked against the current source on 2026-10-20. Method: top-level type names of each file
(nested names such as `Options`/`Result`/`Buffers` excluded), word-matched in production sources
with comments and string literals stripped. Production = every `*.cs` the desktop project
compiles (repo root; `tests/`, `kernel/`, `tools/`, `bin/`, `obj/` excluded). "Reachable" = the
type graph reaches it from `Program.cs`. Pending-ticket plans come from `grep handoffs/*.md`.
The same scanner is `tests/ServiceSurfaceTests.cs` (group `service-surface`).

**Nothing has been deleted and no service file was edited.** Deletion is the user's call. Files
marked "awaiting decision" are the only entries in the test's allowlist; once the user decides,
delete the file (or wire + test it) and remove its entry from `ServiceSurfaceTests.AwaitingDecision`.

Recommendation keys: **KEEP-wired**, **KEEP-needs-caller** (ticket named), **KEEP-offline-tool-needs-test**,
**DELETE-candidate**.

## 2a — no caller and no test on the board (19 files, 3 766 lines)

| File | Lines | Non-test callers (file:line) | Tests | Pending ticket plan | Recommendation | Reason |
|---|---|---|---|---|---|---|
| `ParticleSystem.cs` | 953 | LiveGeometry.cs:91,93,96,111; LiveFixtures.cs:34,35,37,55 (reachable: Program → SceneRenderer.Live → LiveGeometry) | RenderArchitectureTests.cs (EGL live-layer fixture) | R9 item 1 (done, implemented-unverified) | KEEP-wired | R9 now draws it through the shared SceneRenderer; no longer a 2a file |
| `GeometryNodes.Core.cs` | 694 | LiveGeometry.cs:260,269; LiveFixtures.cs:117,119,122 (reachable) | RenderArchitectureTests.cs | R9 item 1 | KEEP-wired | R9 renders `GeometrySet` instances; the node graph itself still has no editor (UI out of scope) |
| `BaseMotionLibrary.cs` | 824 | none | none | none (README.md:35 only says it "remains") | KEEP-offline-tool-needs-test — **awaiting decision** | Spec item 3. It is a real offline baker (`BuildAll(rig, clipsDir)` writes `.riganim` files via `RigAnimWriter`), which matches the "bake, never generate at runtime" rule, but nothing can invoke it: no CLI entry, no test. Keep only if Camhead clips are still wanted: add an explicit `--bake-motion-library <rig> <out-dir>` entry and a fixture test that bakes to a temp dir and re-reads the clips. Otherwise delete. |
| `TexturePaintLayers.cs` | 186 | none | none | ticket 13 (13-texture-painting.md item 3: "Layers ... blend modes") | KEEP-needs-caller (ticket 13) — **awaiting decision** | Layer stack/blend modes are in ticket 13's scope but were never hooked into `TexturePaintService` (35 lines) and have no test. Wire+test under ticket 13 or delete. |
| `SkinningLodService.cs` | 8 | none | none | none | DELETE-candidate — **awaiting decision** | A single triangle-count threshold function with no consumer |
| `GpuAnimationBlendService.cs` | 159 | none | none | none | DELETE-candidate — **awaiting decision** | Duplicates `ClipBlendService` (A3) as a GPU palette lerp; no consumer, no ticket |
| `GpuConstraintSolverService.cs` | 128 | none | none | none | DELETE-candidate — **awaiting decision** | Distance-constraint toy; `ClothSimGpuService` already owns GPU constraints |
| `GpuExportBakeService.cs` | 22 | none | none | none | DELETE-candidate — **awaiting decision** | Compute stub, no ticket |
| `GpuFluidSimService.cs` | 29 | none | none | none | DELETE-candidate — **awaiting decision** | Compute stub, no ticket |
| `GpuIkSolverService.cs` | 21 | none | none | none (IK lives in kernel + `LiveIkSolverBuffers`) | DELETE-candidate — **awaiting decision** | Moves points toward a target; not an IK solver |
| `GpuLodGeneratorService.cs` | 30 | none | none | none | DELETE-candidate — **awaiting decision** | Compute stub, no ticket |
| `GpuMorphService.cs` | 137 | none | none | none | DELETE-candidate — **awaiting decision** | Duplicates the `MorphEvaluator`/`ViewportMesh` morph path |
| `GpuPhysicsService.cs` | 139 | none | none | none | DELETE-candidate — **awaiting decision** | Gravity + floor particle toy; no physics ticket |
| `GpuRayTracingService.cs` | 32 | none | none | none (30-renderer-quality.md: "stay stubs until a path-tracing ticket exists") | DELETE-candidate — **awaiting decision** | Renderer wave is explicitly one step below Cycles |
| `GpuSkinningService.cs` | 189 | none | none | none | DELETE-candidate — **awaiting decision** | Duplicates the shared SceneRenderer vertex-shader skinning (a second skinning path is against RULES.md) |
| `GpuTerrainService.cs` | 29 | none | none | none | DELETE-candidate — **awaiting decision** | Compute stub, no ticket |
| `GpuTextureCompressionService.cs` | 31 | none | none | none | DELETE-candidate — **awaiting decision** | Compute stub, no ticket |
| `GpuVoxelizerService.cs` | 33 | none | none | none | DELETE-candidate — **awaiting decision** | Compute stub, no ticket |
| `PathTracerService.cs` | 122 | none | none | none (30-renderer-quality.md, 16-blender-compare.md: out of scope) | DELETE-candidate — **awaiting decision** | CPU path-tracer stub; a path tracer would be a separate architecture, not this file |

2a now: 2 files wired (1 647 lines), 17 files awaiting decision (2 119 lines), of which 15 are
DELETE candidates (1 109 lines), `BaseMotionLibrary.cs` (824) and `TexturePaintLayers.cs` (186)
are keep-if-wanted.

### Added after the board lists: `SsrPass.cs`

| File | Lines | Non-test callers | Tests | Pending ticket plan | Recommendation | Reason |
|---|---|---|---|---|---|---|
| `SsrPass.cs` | 2 | none | none (coordinator replaced the R5 stub `tests/ScreenSpaceTests.cs` with real EGL probes at 12:09, which no longer use it) | R5 (SSR is implemented in `ScreenSpaceShaders.Lit`, spliced into the lit shader) | DELETE-candidate — **awaiting decision** | A 96-byte `Enabled`-flag class; the real SSR has no use for it |

## 2b — tested but not wired into the app (16 services)

All 16 still have tests; none was deleted. "Reachable" is from `Program.cs`.

| File | Lines | Non-test callers (file:line) | Tests | Pending ticket plan | Recommendation | Reason |
|---|---|---|---|---|---|---|
| `ExportService.cs` | 814 | DmxExportAdapter.cs:34…; FbxExportAdapter.cs:137,172 (adapters reachable only through the `FbxExportAdapter.InchesToCentimetres` constant at FbxClipConverter.cs:332; no export entry point) | ExportServiceTests.cs, FbxDmxExportTests.cs | I1/I2 done; no open ticket for an export entry | KEEP-needs-caller (coordinator: export CLI/host entry, no ticket yet) | Real capability-checked export backend; only the entry point is missing |
| `DependencyWatcher.cs` | 1024 | none | DependencyWatcherTests.cs | P3 done; host integration unticketed | KEEP-needs-caller (coordinator host integration, no ticket yet) | Reload-on-change belongs in the host/DocumentLoader |
| `AutosaveScheduler.cs` | 359 | none (it calls DocumentSaveService) | PreferencesAutosaveTests.cs | P4 done; host integration unticketed | KEEP-needs-caller (coordinator host integration) | Needs a session-owning host to drive it |
| `PartTrackEvaluator.cs` | 703 | none | PartTrackTests.cs | A6 done; MarionettePlayer integration unticketed | KEEP-needs-caller (coordinator `MarionettePlayer` integration) | Part/camera tracks evaluate but are not played |
| `ClipBlendService.cs` | 632 | none real (scanner hit TexturePaintLayers.cs:49 is a `BlendMode` property name, not a use) | ClipBlendTests.cs | A3 done; 28-euphoria.md lists it as a dependency | KEEP-needs-caller (coordinator `MarionettePlayer` integration / ticket 28) | Blending is tested, not played |
| `ClipCapabilityValidator.cs` | 626 | none | ClipCapabilityTests.cs | A1 done | KEEP-needs-caller (coordinator: DocumentLoader load-time diagnostics) | Natural caller is clip load |
| `TimelineEventDispatcher.cs` | 721 | none | TimelineEventTests.cs | A5 done | KEEP-needs-caller (coordinator `MarionettePlayer` integration) | Events never fire during playback |
| `ConstraintParityInventory.cs` | 806 | none | ConstraintParityTests.cs | A7 done (inventory) | KEEP-offline-tool-needs-test (already tested) | An inventory checked against packaging/CONSTRAINTS.md; a report, not a runtime feature |
| `AdvancedIkService.cs` | 169 | none | AdvancedIkTests.cs | ticket 23/26 (IK variants not started) | KEEP-needs-caller (ticket 26) | Waits for the IK variants ticket |
| `MorphEditService.cs` | 636 | LiveModifiers.cs:137 (LiveModifiers.cs not yet reachable) | MorphEditTests.cs | R9 item 2 (live modifier stack, in progress) | KEEP-needs-caller (R9 item 2) | `LiveModifiers.cs` landed 09:48 today and already uses it; needs SceneRenderer hookup |
| `WeightPaintService.cs` | 608 | LiveModifiers.cs:98 (not yet reachable) | WeightPaintTests.cs | R9 item 2 | KEEP-needs-caller (R9 item 2) | Same |
| `RetopoService.cs` | 749 | LiveModifiers.cs:68,70 (not yet reachable) | RetopoTests.cs | R9 item 2 | KEEP-needs-caller (R9 item 2) | Same |
| `TexturePaintService.cs` | 35 | none | TexturePaintTests.cs | ticket 13 (viewport brush is UI-adjacent) | KEEP-needs-caller (ticket 13) | Small tested core; the paint tool needs viewport input |
| `UvEditService.cs` | 84 | none | UvEditTests.cs | ticket 14 | KEEP-needs-caller (ticket 14) | Same pattern as 13 |
| `ShrinkwrapService.cs` | 226 | LiveModifiers.cs:117,122,209 (not yet reachable) | ShrinkwrapTests.cs | R9 item 2 | KEEP-needs-caller (R9 item 2) | Live modifier |
| `DataTransferService.cs` | 101 | none | DataTransferTests.cs | ticket 21 done; could be a live modifier | KEEP-needs-caller (R9 item 2 candidate) | Blender's Data Transfer is a modifier; not in R9's list yet |

## Also enumerated by the test (`*Service.cs` not on the board lists)

All tested; caller status for the board's next reconciliation. Tested-but-unwired:
`AutoWeightService.cs` (226), `ClothSimGpuService.cs` (445; R9 item 1 names it, but LiveGeometry
only uses `ClothSimService`), `CrossFieldService.cs` (238), `DyntopoService.cs` (142),
`EuphoriaService.cs` (139), `KnifeService.cs` (180), `ParametricEditService.cs` (694),
`PolypaintService.cs` (50), `UvSeamService.cs` (75). Wired/reachable: `ClipEditService.cs`,
`IkTargetService.cs`, `PoseEditService.cs`, `ViewportHandleService.cs`, `ClothSimService.cs`,
`HairStrandService.cs`. `DocumentSaveService.cs` is called by AutosaveScheduler/ParametricEdit
but not reachable from the app.

## Scanner limits (documented, not hidden)

- A caller is any production file naming the type, even if that file is itself unreachable
  (e.g. `LiveModifiers.cs`). The test enforces "caller or test", as the R11 spec asks; it does
  not prove reachability from the viewer. The reachability column above is from a separate graph walk.
- A top-level type name reused as a member name elsewhere (the `BlendMode` case) counts as a
  caller. This only makes the check more lenient; the case found has a test anyway.

## Stray file noticed (not touched)

The repo root contains a directory whose name begins `IblBaker.cs">?using System;...` (created
08:01 today, apparently a broken shell write). It is not a `.cs` file, is not compiled, and was
left alone.
