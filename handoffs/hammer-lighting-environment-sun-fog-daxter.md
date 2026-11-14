# Handoff — hammer-lighting-environment-sun-fog (daxter-hammer-lighting)

Worker: daxter-hammer-lighting. Date: 2026-11-13 ~17:20 EST.
Status: **implementation done, NOT runtime verified in the real tree.**
The shared tree does not compile (see blockers); verification ran in an isolated
harness with verbatim copies of the real files. The prescribed full-suite run and
full build are deferred to the final integration gate, per the PO workflow override.

## What this ticket is

Hammer Desktop P2: lighting entities — light, light_spot, light_environment +
env_sun, fog, tonemap. Backend / data-model / API / headless only; no Desktop UI.

## Files changed (mine only — nothing else in the repo was edited by me)

- `kernel/Effigy/Map/LightEnvironment.cs` (1195 lines, namespace `Effigy.Map`,
  untracked): adapter from placed MapEntities to (a) the native bake via
  `LightingSetup.ToCompileLights()` and (b) the preview via `ToLiveLights()`.
  - Enums `LightingIssueSeverity`, `BakeLightKind` (Point/Spot/Sun/Environment/
    Dynamic); `LightingIssue`; `BakeLight` (factories Point/Spot/Sun/Environment/
    Dynamic, `ToLiveLight()`, `ToCompileLight(mapCenter)` — null for Environment/
    Dynamic; sun uses a distant-light approximation: `SunApproximationDistance =
    8192`, intensity x(1+(8192/128)^2) = x4097 to cancel CompileRunner's falloff
    exactly); `FogSettings` (linear FogFactor), `ShadowSettings`, `TonemapSettings`
    (ClampExposure); `CompileLightConversion`; `LightingSetup` (Lights, Issues, Sun,
    Environment, HasEnvSun, EnvSunColor/Size/OverlayColor/OverlaySize, Fog, Shadow,
    Tonemap, HasIssue, IssuesWithCode, ToLiveLights, ToCompileLights).
  - `LightEnvironment`: issue-code consts, embedded `LightingRegistryJson` (the SIX
    classes core doesn't ship), `CreateLightingRegistry()`, `MergeInto(registry)`,
    `LightingClassNames` (8 names), direction helpers (Source convention: pitch
    positive-down, elevation = -pitch; non-zero `pitch` key overrides angles.x),
    `TryParseLightColour` ("r g b [brightness]", 0-255 -> 0..1, brightness raw,
    default 1), `TryParseColour255`, `FromEntities(entities, resolveTarget = null)`
    — never throws for content, warn-and-default, first-wins for singleton
    controllers, pair validation (`SunWithoutEnvSun`, `EnvSunWithoutLightEnvironment`,
    `EnvSunDirectionMismatch` > 5 deg).
  - **New in this session:** `LightingSetup.ApplyToCompileInput(CompileInput input,
    Vec3 mapCenter)` — appends the converted bake lights to a CompileInput and
    returns the conversion (ambient/dynamic reported, not added). This is the seam
    `CompileRunner.FromDocument` should call to carry light_spot and
    light_environment into the bake. ArgumentNullException on null input.
  - **New in this session:** `public const string CodeInvalidEnvSunValue =
    "InvalidEnvSunValue"` — env_sun `size`/`overlaysize` parse failures used to
    warn under the mislabeled `CodeInvalidLightColour`. That warn path is
    unreachable via the public API (MapEntities type-checks keyvalues and throws
    on bad values first), so it has no test — flag as untested-via-public-API.
  - Doc fixes: "five" -> "six" lighting classes, "seven" -> "eight" class names,
    core-count mentions depinned (see below).
- `tests/LightEnvironmentTests.cs` (670 lines, group `lightenvironment`, **23 checks**,
  `Register(TestRegistry, TestSettings)`; NOT registered in tests/TestRegistry.cs —
  integrator's line): registry file loads 6 classes; embedded JSON == fixture;
  merged registry = core + 6 with all 8 lighting names; key types per class;
  duplicate merge rejected with DuplicateClass; point/spot resolution (spot target
  aim + angles fallback + cone clamp); sun direction/pitch; pair warnings both ways;
  mismatch warning; first-wins; dynamic reaches preview but not bake; fog/shadow/
  tonemap resolve + validate; non-lighting ignored + null throws; style flicker
  warns; analytic preview sun test; sun-room fixture resolves clean; **one sun bakes
  a lit (non-black) lightmap through CompileRunner** (Final preset; roof face mean
  > 0.9, interior floor mean == 0 shadowed, overall Mean > 0); point light bakes lit;
  sun approximation math exact; BakeLight rejects invalid construction; **new:
  ApplyToCompileInput wires a sun setup into a CompileInput and bakes lit**.
  - **Changed in this session:** the two count assertions no longer pin the core
    count (merged = `LoadCore().Count + 6`; failed-merge assert is count-before vs
    count-after). Core grew 14 -> 15 while this ticket ran (see below).
- `tests/fixtures/hammer/hammer-lighting-environment-sun-fog/`:
  `lighting-source1.json` (the six lighting classes), `sun-room.json` (sealed
  288x288x144 room, 6 box brushes, light_environment + env_sun + fog/shadow/tonemap,
  playerStart [0,0,32]), `README.md`. Doc fixes: five -> six, merged-total wording
  depinned from the core count.

## Class-count settlement

The ticket's "seven" counts the sun pair once; the registry carries EIGHT names.
Core registry (`kernel/Effigy/Map/registry/core-source1.json`) ships light +
light_spot; the lighting JSON adds 6 (light_environment, env_sun, light_dynamic,
env_fog_controller, shadow_control, env_tonemap_controller). Core was 14 classes
at dispatch, **15 when verified ~17:13** (new class present; registry file still
being edited by the registry worker, mtime 17:12 — count may move again; my tests
no longer pin it). Merged total is therefore core + 6 (21 at verification).

## Checks run (isolated harness — the evidence)

Harness: `/tmp/iso-lighting/` (isolated.csproj, net8.0; Program.cs = faithful
TestRegistry stub; CWD = repo root so tests' RepoRoot() finds the registry).
Real files verbatim (sha-verified at copy time): Vec.cs, EntityRegistry.cs,
MapEntities.cs, LightEnvironment.cs, CompileRunner.cs, ParallelStageRunner.cs,
LeakFinder.cs, LiveLighting.cs, FaceMaterial.cs, ToolTexture.cs, AssetRegistries.cs,
CubemapBake.cs, MapPacker.cs, AssetHotload.cs + LightEnvironmentTests.cs.
Harness-only shims in Shims.cs (NEVER copy back): `MapDocument` +
`MapBrushData` / `MapBrushFaceData` / `MapEntityData` (the exact surface
CompileRunner.FromDocument reads — the real MapDocument.cs can't compile
standalone: it needs MapBrush.cs's CAD Feature closure and Displacement.cs's
PolyMesh rig/paint closure, and it doesn't compile in the shared tree right now
anyway) and the `IssueSeverity` enum (AssetRegistries.cs line 180 only).
FromDocument is never called by this test group; Pack/Cubemap stages never reached.
- **Final run: `TOTAL lightenvironment: passed=23 failed=0`, dotnet exit 0**
  (log `/tmp/iso-lighting/run5.log`). Two runs before it caught real issues in
  MY session's edits and were fixed here, not hidden: (1) my inserted 23rd check
  landed nested inside check 22's lambda (rfind anchor) — crashed the stub's
  enumerator with InvalidOperationException "Collection was modified" until the
  tail was repaired; (2) the two hard-coded count==20 assertions failed when core
  grew to 15 — relativized (above).
- SSH flapped 3x during the session (egress proxy resets, ~17:08); recovered on
  retry; no work lost.

## Checks deferred (final integration gate)

- Prescribed run: `dotnet run --project tests/Desktop.Tests.csproj -c Release --
  --filter lightenvironment`, plus the full project build. Deferred because the
  shared tree does not compile (see blockers) — same as the registry/compile
  workers did.
- No real-tree runtime verification: no app launch, no UI (out of scope).

## Integrator TODOs (not mine — shared entry points / other owners' files)

1. Add `LightEnvironmentTests.Register( registry, settings );` to
   tests/TestRegistry.cs (coordinator-owned; my group is NOT registered there —
   verified: its current diff is other workers' registrations only).
2. Copy `tests/fixtures/hammer/hammer-lighting-environment-sun-fog/lighting-source1.json`
   into `kernel/Effigy/Map/registry/` AND update
   tests/EntityRegistryTests.cs:262-264, which asserts LoadDirectory(registry/)
   count == 14 — core is 15 now and still moving. Do NOT copy the file without
   updating that expectation.
3. `CompileRunner.FromDocument` (packing worker's file, locked by
   daxter-hammer-compile-parallel): (a) call
   `LightingSetup.ApplyToCompileInput` to carry light_spot/light_environment into
   the bake — the one-line call this ticket's seam exists for; (b) BUG FLAGGED,
   not fixed: FromDocument carries only "light" entities, and it looks them up
   via key `"light"` while the registry defines `_light` — a light placed through
   MapEntities is missed there. Same for the light_spot/light_environment gap
   its doc comment already notes as this ticket's.
4. Regenerate the kernel-parity inventory baseline for the new kernel files
   (EntityRegistry.cs, LightEnvironment.cs, registry JSON).

## Blockers / locks (read-only for me)

- CompileRunner.cs locked by daxter-hammer-compile-parallel (since 4:51 PM).
  MapEntities.cs + LightmapBake.cs locked by muse-3-desktop-hammer-84
  (prop-lighting, since 4:33 PM).
- Shared-tree breakage confirmed (not mine, final-gate scope): MapBrushPrimitives.cs
  uses nonexistent `MapFace` (real: MapBrushFace); MapDocument.cs uses
  `EntityOutput` where it means `EntityIoOutput` + lacks `using System.IO;`
  (worker 12's file); BuildScript.Map.cs does not exist; InstanceIoProxy defined
  twice; MapInstanceLibraries.cs CS0050; MapLayers.cs:299 CS0173; ApiCatalog.cs
  references missing BuildScript.MapVerbNames.
- FromDocument's real input types are `MapEntityData`/`MapBrushData` (capital-V
  `KeyValues`, `TargetName` from targetname) — NOT `MapEntity`/`MapBrush`. Any
  future FromDocument work should build against those.

## Prior-session note

The bulk of this ticket was already implemented on disk by a prior session
(files timestamped 2026-11-13 16:31-16:35) that never marked the task done; the
task was reset to todo/Unassigned. I read all of it in full, verified it against
the real types (EntityRegistry, MapEntity, CompileLight ctor, CompileRunner.Run,
CompileInput, CompileBrush.Box, LiveLight/LiveLighting.Evaluate, Vec3), and kept
it — my own changes are the items marked "New/Changed in this session" above.
No TODO/FIXME/stub markers in LightEnvironment.cs.
