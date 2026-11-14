# Handoff: hammer-compile-cordon-region — DONE (implementation, not runtime verified)

Worker: daxter-hammer-compile-cordon. Task: Hammer Desktop P1 cordon / region
compile + compile-visible-layers only. PO workflow override applied:
implemented + tested WITHOUT a full project build or app launch; full build
and the real TestRegistry group run are deferred to the final integration
gate. Implementation done is NOT runtime verified — say so downstream.

## Changed files (on the PC, /home/poo/Desktop/geppetto-desktop)

- `kernel/Effigy/Map/CompileCordon.cs` (NEW, ~730 lines) — Cordon,
  CordonAxis, CordonHalf, CordonReport, CordonBisectResult, static
  CompileCordon.
- `tests/CompileCordonTests.cs` (NEW) — test group `compilecordon`, 20 checks.
  NOT registered in TestRegistry.cs (integrator adds the one line).
- `tests/fixtures/hammer/hammer-compile-cordon-region/` — README.md,
  `two-room-cordon.json`, `leaking-two-room.json`.
- Local staging: `~/workspace/compile-cordon/` (source of truth for the
  three files above + `build_harness.py`).

## API (namespace Effigy; CompileCordon is `public static class`)

- `Cordon(Min, Max)` — strict ctor (throws on swapped bounds / zero volume);
  `Contains`, `Intersects` (boundary-inclusive), `Center`, `Size`,
  `FromBounds`, `FromBrushes(brushes, margin)`, `FromSelection(input,
  brushIds, margin=16)`, `FromLayer(input, layerId, layerOf, margin=16)`;
  `SealThickness = 16`, `SealMaterial = "tools/cordon_seal"`.
- `CompileCordon.ApplyCordon(input, cordon[, out report])` /
  `ApplyCordon(input, IEnumerable<Cordon>[, out report])` — sealed
  sub-document; output name = source + " (cordon)" (appended once).
- `CompileCordon.ApplyVisibleOnly(input, visibleLayers, layerOf[, out
  dropped])` — keeps unlayered + visible layers; lights/player start kept.
- `CompileCordon.BisectLeak(input, cordon, axis[, firstStart,
  secondStart])` — QuickPlay on both halves; `LeakingHalf` None/First/
  Second/Both; flood origin = explicit start, else input PlayerStart if
  inside that half, else the half's Centre (origins resolved before either
  half compiles).
- `CordonReport`: Kept/Dropped/ClippedBrushes (+Names), SealBrushes,
  LightsKept/Dropped, PlayerStartKept, Cordons.

## Seal design (the part most likely to be misread)

- A kept brush is one with INTERIOR VOLUME inside a cordon (overlap > Eps
  0.01 on every axis). A brush merely touching the boundary from outside
  (e.g. a seal box from a previous pass) is dropped — that is what makes
  ApplyCordon idempotent.
- Straddling brushes are clipped by adding the crossed cordon faces as
  extra planes (seal material) and removing original faces lying entirely
  beyond a cut; cuts fire only past Eps; an invalid clip falls back to
  keeping the brush whole.
- Seals are PER FACE, not a 6-box shell: a cordon face gets ONE seal box
  (just outside the face, extended past the corners) exactly when the
  cordon cuts geometry there — some brush straddles that face, or some
  brush lies beyond it and faces it (tangential overlap). A face where the
  boundary coincides with the hull gets NO seal: the hull itself seals it,
  and a real gap in that hull stays a real leak. That is what makes bisect
  work. Known limit (documented in the file header): a real gap in a wall
  the boundary itself cuts through is sealed over — move the boundary off
  the wall to bisect that gap.
- Multi-cordon = union; a brush overlapping 2+ cordons is kept whole; a
  face's seal box is skipped when it would land inside another cordon
  (a union covering the whole map gets zero seals — no phantom walls).
- Seal brushes: negative ids (-101..-106 cordon 0, -201..-206 cordon 1),
  names `cordon_seal_<face>[_<index>]`, all faces SealMaterial.

## Why CompileCordon.cs is a separate file (deferred integrator patch)

`kernel/Effigy/Map/CompileRunner.cs` is LOCKED by
daxter-hammer-compile-parallel (`hub edit --files` returned CONFLICT), so
nothing in this ticket edits it. The file header carries the exact patch
the final integrator applies:
1. `CompileOptions` += `List<Cordon> Cordons`, `bool CompileVisibleOnly`,
   `List<string> VisibleLayers`.
2. `CompileBrush` += `string Layer` (copied from MapBrushData.LayerId by
   FromDocument).
3. `PrepareInput` applies `CompileCordon.ApplyVisibleOnly` then
   `ApplyCordon`.
4. `tests/TestRegistry.cs` += `CompileCordonTests.Register(registry,
   settings);`

## Checks run — isolated harness 20/20 PASS

Harness `/tmp/iso-cordon` (built by `~/workspace/compile-cordon/
build_harness.py`, copy on PC at `/tmp/build_harness.py`; re-run with
`ssh cachyos 'python3 /tmp/build_harness.py'`, then
`ssh cachyos 'dotnet /tmp/iso-cordon/bin/Release/net8.0/IsoCordon.dll'`).
It copies the CURRENT repo Vec.cs, LeakFinder.cs, ParallelStageRunner.cs,
CompileRunner.cs (FromDocument deleted from the copy only — it needs the
broken MapDocument), CompileCordon.cs, CompileCordonTests.cs; stubs the
pack/cubemap surface and the pre-compile gate (stubbed to a clean pass —
see below). dotnet on PC is 10.0.402; build ~1s; 0 errors, 3 CS8602
warnings in the test file (Require-then-dereference, same pattern the
existing tests use).

Notable passing checks: one-room cordon keeps 8 / drops wall_east /
clips 4 / 1 east seal; whole-map cordon adds no seal; idempotent (2nd
pass: 1 dropped old seal, 1 re-added, 0 re-clipped); touching-from-outside
brush dropped; union of both room cordons = whole map, 0 seals; room-A
compile of the two-room map succeeds with only room A visible; sealed
half of the leaking map compiles clean; leaking half fails with a `leak`
issue; bisect isolates Second; hiding a structural wall leaks.

Two things the harness caught and this ticket fixed (do not regress):
- The old 6-box seal shell plugged the hull gap (seal flush against the
  wall) so the leaking half compiled clean — fixed by the per-face rule.
- The leak-location assertion: the runner reports the gap portal's
  centroid, (132, 0, 64) for this fixture (room midpoint cross-section,
  NOT the slot at x 264) — verified identical with and without the cordon
  (same gapLeaf 2 / gapPortal 11 / path / exit), so the test now asserts
  the cordoned leak location EQUALS the full map's, plus that it lies in
  room B. That centroid semantics belongs to the BSP builder
  (native-pipeline ticket), not to cordons.

## Checks deferred (final integration gate)

- Full project build + real TestRegistry `compilecordon` group run.
- The PrepareInput / CompileOptions / CompileBrush.Layer wiring above.
- The pre-compile gate runs for real there: the harness stubs
  PreCompileGate to a clean pass (its real implementation needs the
  MapCheck/IssueEngine tree). Gate behaviour is NOT verified by the
  harness; if the real gate blocks a fixture compile, that surfaces at
  the gate, not here.
- CompileRunner.cs grew during this session (parallel worker, packing
  worker, pre-compile gate worker all landed); the harness copies it
  fresh each build, so a future new referenced type breaks the harness
  build the same way PreCompileGateResult did — add a stub, do not edit
  repo files to satisfy the harness.

## Blockers / notes for the integrator

- CompileRunner.cs lock (above) — this ticket touched zero shared files.
- Shared tree still does not compile for reasons owned by other tickets
  (MapBrushPrimitives uses nonexistent `MapFace`; BuildScript.Map.cs
  missing; MapDocument EntityOutput/EntityIoOutput mixup + missing
  `using System.IO;`; MapLayers.cs:299; InstanceIoProxy defined twice).
  Do not fix those here.
- VmfIo round-trips a VMF cordon section (HasCordon/CordonMins/CordonMaxs/
  CordonActive) — noted as a future seam only; this ticket does not wire it.
