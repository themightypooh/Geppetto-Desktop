# Hammer Desktop P3: native radiosity-quality bake — handoff (daxter-hammer-radiosity)

Board task: `hammer-radiosity-quality-bake`. Status: implementation done,
**NOT runtime verified** (see Checks). No UI work. No full project build or
app launch per the PO workflow override.

## What was already there (reconciled, not re-implemented)

A prior session had already landed the radiosity engine in the working tree
(untracked): `kernel/Effigy/Map/RadiosityBake.cs` (luxel-level multi-bounce
radiosity, Hammersley AO, HDR, prop vertex lighting, multi-page atlas
packer, 1K GPU trust rule) + `tests/RadiosityBakeTests.cs` (group
`radiositybake`) + fixtures `tests/fixtures/hammer/hammer-radiosity-quality-bake/`
(`two-room.json`, `parallel-plates.json`, `README.md`). Its header claimed
`LightmapBake.cs` did not exist yet — stale: P2 (`hammer-lightmap-progressive-bake`,
worker 57, done) HAS landed since. This ticket wires the engine into the
progressive bake instead of building a second evaluator.

## Changed files (this session, all claimed on the board)

- `kernel/Effigy/Map/LightmapBakeQuality.cs` (NEW, 211 lines) — the quality
  entry point on a `LightmapBakeJob`. Thin routes into `RadiosityBake`, no
  light transport of its own:
  - `BakeRadiosity(job, settings?, progress?, cancel?)` → `RadiosityResult`
    (null settings = job's grid cap + bounce count via `ToRadiositySettings`, AO off)
  - `ToRadiositySettings(options)` — job options → radiosity settings
  - `PackAtlas(baked, atlasSize, paddingTexels = 2)` → `AtlasPackResult`
    (multi-page, padded gutter — the atlas improvement over the progressive
    bake's single-page 1-luxel pack that throws when full)
  - `LightPropVertices(baked, vertices, lights, faces, settings?)`
  - `ToneMap(hdr, exposure = 1f)` (Reinhard)
  - `VerifyGpuTrust(backend, job, settings?, tolerance = 0.05f)` /
    `BakeGpuRadiosity(job, settings, trust, progress?, cancel?)` (1K trust rule)
  - `CompareToProgressive(quality, progressive)` — per-face average
    deviation, same metric as `LightmapBakeJob.CompareAverages`
- `kernel/Effigy/Map/RadiosityBake.cs` — header only: the stale "LightmapBake
  does not exist yet" paragraph replaced with the landed status + the live
  adapter points (all in `LightmapBakeQuality.cs`). No engine code touched.
- `tests/RadiosityQualityIntegrationTests.cs` (NEW, 412 lines) — group
  `radiosityquality`, 10 tests exercising the bake THROUGH the job:
  two-room bounce, AO corner darkening, HDR sun + tone map, prop vertices,
  atlas gutters/bounds/spill, GPU trust (exact passes / diverging fails /
  no-trust refused), direct-only cross-check vs progressive `Refine()`,
  null-settings mapping, determinism, invalid inputs + cancel.
- `tests/RadiosityBakeTests.cs` — 4 fixes to the prior worker's test
  expectations, all proven wrong by execution or by the fixture on disk
  (engine code was correct in every case):
  1. `two-room` face count 18 → 16 (the fixture has 16 faces).
  2. "more bounces" `dark0 == 0f` → average equals the direct average and
     is > 0.01: the doorway luxel (1,0) sees the light directly, so a
     0-bounce bake is one lit luxel out of four, not black.
  3. Shadow-test plate winding reversed so its Newell normal is +Z (was
     -Z, so "lit from above" saw black).
  4. Atlas spill test: 4× 16×16 charts on a 32 atlas with padding 2 =
     4 pages, 1 chart per page, utilization 0.25 (was: 2 pages / 2 per page
     — forgot the gutter at the page edges).
- `tests/fixtures/hammer/hammer-radiosity-quality-bake/README.md` — added
  the P3 entry-point section (`LightmapBakeQuality`, both test groups).
- NOT touched: `kernel/Effigy/Map/LightmapBake.cs` — locked by the
  prop-lighting worker (muse-3-desktop-hammer-84 at 16:33, re-staffed as
  daxter-hammer-prop-lighting at 17:07). The quality options/methods live
  in `LightmapBakeQuality.cs` instead; the integrator folds
  `ToRadiositySettings` into `LightmapBakeOptions` when that lease frees.
  NOT touched: `tests/TestRegistry.cs` (coordinator-owned) — the integrator
  adds `RadiosityBakeTests.Register(registry, settings);` and
  `RadiosityQualityIntegrationTests.Register(registry, settings);`.

## Fixtures

Reused as-is: `two-room.json` (16 faces, 1 point light, no ambient — room B
light can only be bounce) and `parallel-plates.json`. No new fixtures.

## Checks run (all on his PC, 2026-11-13 ~17:06–17:09 EST)

- Isolated harness `/tmp/radiosity-iso` (verbatim repo files + a minimal
  `TestRegistry`/`TestSettings` shim, because the real `TestRegistry.cs`
  needs `Marionette.Desktop`; the harness's `Baker.cs` is a verbatim copy
  of ONLY `BakePhase`/`BakeProgress` from the real file, and `GpuBake.cs`
  is excluded — nothing in the closure references it except doc comments):
  `dotnet build` exit 0, 0 errors; run exit 0 — **38/38 PASS**
  (`radiositybake` 28 + `radiosityquality` 10). Run from the repo root so
  fixture lookup resolves. Reproduce: copy the harness definition from
  `~/workspace/hammer-radiosity/harness/` (this VM) or rebuild from
  `/tmp/radiosity-iso` while it lives; `dotnet
  /tmp/radiosity-iso/bin/Release/net8.0/radiosity-iso.dll`.
- Kernel build before/after diff: baseline 12 errors → after my files 6
  errors → final 6 errors, ALL pre-existing and none in my files
  (`MapBrushPrimitives.cs` `MapFace` ×4 — known landmine, brush-primitives
  ticket; `MapInstances.cs` duplicate `InstanceIoProxy` ×2 — instances
  ticket). Zero new errors, zero warnings for my files in the kernel log.
  (The 12→6 drop between baseline and after was other workers fixing
  `RegisterLibraryResult`/`MapInstanceLibraries` in the meantime, not me.)
- The two failing `radiositybake` tests found on the first harness run
  were test-expectation bugs (listed above), fixed and re-run green.

## Checks deferred (final integration gate)

- Full project build, full test-suite run (`radiositybake` /
  `radiosityquality` groups through the real `DesktopTestHost`), and any
  GPU/EGL or app-launch verification. The shared tree still does not
  compile (the 6 errors above), so the real test runner cannot execute
  yet. TestRegistry registration lines are also the integrator's.
- Performance measurement (form-factor precompute is O(P²·T); the
  `MaxTotalPatches` budget refuses absurd scenes — measurement is for
  the gate, not claimed here).
- No `LightmapBakeResult` conversion: the quality bake's native result
  type is `RadiosityResult`. A conversion would mislabel its
  reflected-average fields as `LightmapFaceBake`'s pre-albedo
  `DirectAverage`/`BounceAverage`; left as integrator follow-up.

## Blockers / notes for the integrator

- `LightmapBake.cs` lease (prop-lighting) + `TestRegistry.cs` ownership
  (above). SSH tunnel flapped once mid-session (~17:09 EST, egress proxy
  reset, recovered on retry; `/root/.ssh/config` verified intact).
- Acceptance, as executed in the harness: two-room unlit room non-zero
  and mostly indirect ✓; AO darkens corners more than the centre ✓; HDR
  sun luxel 4.0 > 1.0 ✓; prop vertex in the unlit room lit by bounce ✓;
  GPU path keeps the 1K trust rule ✓.
