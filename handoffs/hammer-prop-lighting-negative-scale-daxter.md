# Hammer prop lighting, negative scale — handoff (daxter-hammer-prop-lighting)

Board task `hammer-prop-lighting-negative-scale` (geppetto-desktop), DONE 2026-11-13 ~17:40 EST.
Implementation-done, NOT runtime-verified: the group passed in an isolated harness
(66/66 incl. regressions, see below); the shared tree still does not compile and
the real test runner + final build are the final integration gate's job.

## What it does

A negatively-scaled prop import flips every polygon's winding, so the normal
computed from the mirrored winding points inward and the prop lights wrong
(CS2-era Hammer pain, r/hammer 197j4cx). This ticket corrects it in the bake:

- `PropLighting` (new, in `kernel/Effigy/Map/LightmapBake.cs`): `Determinant` /
  `IsMirrored` / `HasZeroScale` / `ZeroScaleAxes`; `CorrectNormal` (negates the
  mirrored normal — exact and rotation-independent, since the mirrored winding's
  normal is the exact negation of the true outward normal) and `CorrectWinding`
  (reverses the vertex order for mirrored scales). Zero scale throws naming the
  scale — a zero-scale prop has no defined orientation on that axis.
- `LightmapBakeJob.EvaluatePropLighting()`: lights the job's optional
  `PropLightSample` list (prop id, position, as-imported normal, scale) with the
  same direct-light model as the face bake (sun/point/spot + shadows, plus the
  ambient term), so a mirrored prop lights identically to its unmirrored twin.
  A zero-scale prop returns a named `Error` instead of silent black. Props do
  not affect face luxels or fingerprints (not occluders here).
- Bake log: `LightmapBakeResult.PropCorrections` — one line per corrected prop
  (`prop <id> mirrored (scale ...): normal/winding corrected for lighting`),
  threaded through `MakeResult` / `CloneResult`. Zero-scale props are warned
  in `Warnings()` instead (they are not corrections).
- `MapEntity.Scale` (new, `kernel/Effigy/Map/MapEntities.cs`, default
  `(1, 1, 1)`): Geppetto-native, NOT a keyvalue — the registry has no scale key
  for prop classes, so `SetKeyvalue("scales", ...)` is rejected as
  `UnknownKeyValue`. `SetScale` accepts zero (validation flags it) and rejects
  NaN/Infinity with `InvalidScale`. `Validate()` reports a zero-scale prop as a
  `ZeroScale` error naming the axis. Scale predicates (`PropTransformDeterminant`
  / `IsMirroredScale` / `HasZeroScale` / `ZeroScaleAxes` / `IsPropClass`) are
  one-liners duplicated in `PropLighting` because each file must compile
  without the other (worker 57's / worker 14's harnesses); keep them agreeing.
- `IssueEngine.cs`: new `IssueKind.ZeroScaleProp` + `ZeroScalePropRule`
  (id `zero-scale-prop`, entity scope, code `ZERO_SCALE_PROP`, error): flags
  prop-class entities with a zero scale component. `IssueEntity.Scale` field
  added (default `(1, 1, 1)`; the document projection copies it from
  `MapEntity.Scale`). Mirrored scales are NOT issues; non-prop entities are
  never flagged. `IssueAutoFix.Fix` falls through to its `default:` NotApplied
  for the new kind — no auto-fix registered (that ticket's scope, not this one).

## Files changed

- `kernel/Effigy/Map/LightmapBake.cs` — PropLightSample / PropLightingResult /
  PropLighting / job `Props` + `EvaluatePropLighting` / result `PropCorrections`.
- `kernel/Effigy/Map/MapEntities.cs` — `Scale`, `SetScale`, `CodeInvalidScale`,
  `CodeZeroScale`, scale predicates, Validate zero-scale check.
- `kernel/Effigy/Map/IssueEngine.cs` — `ZeroScaleProp`, `IssueEntity.Scale`,
  `ZeroScalePropRule`, registered (now 10 defaults).
- `tests/PropLightingTests.cs` (NEW, group `proplighting`, 10 checks).
- `tests/IssueEngineTests.cs` — count test only: 8 -> 10 defaults (the file
  already said 8 while the engine had 9 with GiantSkybox; this corrects that
  staleness too), 9 -> 11 with the ping rule, 8 -> 10 after unregister.
- `tests/fixtures/hammer/hammer-prop-lighting-negative-scale/` — `props.json`
  (twin pair + double-negative + zero-scale prop) + `README.md`.

## Checks run

- Isolated harness `/var/tmp/proplighting-harness` (real Vec/EntityRegistry/
  MapEntities/IssueEngine/FaceMaterial/LiveLighting/LightmapBake + ToolTexture/
  GeometryLinter for two missing-type gaps + verbatim TestRegistry core +
  BakePhase/BakeProgress stubs from Baker.cs):
  `dotnet run --project /var/tmp/proplighting-harness/Harness.csproj -c Release`
  from the repo root — **66 passed, 0 failed, 0 skipped** (proplighting 10,
  mapentities 19, lightmapbake, issueengine). Harness csproj/program/stubs are
  NOT deliverables; the harness dir is scratch on the VM.
- The twin test proves the acceptance directly: sun + point light, mirrored
  prop radiance == unmirrored twin within 1e-4, uncorrected inward normal
  lights black (sanity), zero-scale is a named error, log lines name props 2+3.

## Deferred / for the integrator

- Add `PropLightingTests.Register( registry, settings );` to
  `tests/TestRegistry.cs` (coordinator-owned; not touched). Same still pending
  for the other hammer groups.
- Full repo build + real-runner `proplighting` group run (shared tree does not
  compile on pre-existing breaks owned by other tickets, none touched here —
  e.g. MapBrushPrimitives `MapFace`). Note: PreCompileGate's IssueKind switch
  has a `_ => MapIssueType.InvalidBrush` default arm, so a ZeroScaleProp issue
  surfaces there as InvalidBrush until the gate maps it explicitly.
- GPU backend results do not carry `PropCorrections` (the backend builds its
  own result); wire through if the GPU path ever takes props.
- No UI work. No build started. No other worker's files touched.
