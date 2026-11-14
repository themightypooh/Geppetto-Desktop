# Handoff — hammer-browsers-used-count-replace-texture (daxter-hammer-browsers-count)

Task: Hammer Desktop P2 asset-reuse surfacing — used-only, use-count, Replace Texture, alpha display.
Project: geppetto-desktop. Status: DONE 2026-11-13 ~17:50 EST (isolated-harness verified; see caveats).
Worker: daxter-hammer-browsers-count (continuation; implementation by the previous session, verification finished here).

## Changed repo files (implementation — previous session, verified present, NOT touched by this session)

- `kernel/Effigy/Map/AssetRegistries.cs` (892 → 1496 lines, untracked in git)
- `tests/AssetRegistriesTests.cs` (447 → 753 lines, untracked in git)

APIs added (all in `AssetRegistries`, backend only, zero UI):
- `UseCount(asset)` / `UseCounts` — per-asset use-count (brushes/entities referencing a model/material/texture/sound/particle)
- `UsedOnly()` — assets referenced at least once
- `ReplaceTexture(old, new, scope)` — all/selected faces, UV-preserving, skips entity-bound brushes (reported), undoable via `ReplaceTextureCommand` through `MapUndo`; preserves displacements on replaced brushes
- `ReplaceModel(old, new, scope)` — all/selected entities, swaps the model keyvalue, undoable; refuses an unregistered new model
- Per-texture alpha data: `TextureAlphaInfo`, `NoteTextureAlpha` / `HasAlpha` / `NoteTextureAlphaFromVmt` (VMT translucency + manual notes)

NOT touched (per brief): `tests/TestRegistry.cs` (coordinator-owned — the integrator adds the
`assetregistries` registration line) and `kernel/Effigy/Map/MapDocument.cs`.
Note: `git status` shows `tests/TestRegistry.cs` modified — that modification predates this
session; this session did not edit it.

## Checks run (this session) — GREEN

Isolated harness: `/tmp/iso-browsers-count` (dotnet 10.0.402), `harness/harness.csproj`
includes the whole kernel copy + `tests/AssetRegistriesTests.cs` only.

- `dotnet build harness/harness.csproj -c Release` — **0 errors** (was 108 at session start).
- `dotnet run --project harness/harness.csproj -c Release` — **33 passed, 0 failed, 0 skipped;
  33/33 selected/registered**, run TWICE (two fresh work dirs, same result).
  - 21 pre-existing assetregistries checks + 12 new checks: fixture-map use-counts,
    UsedOnly excludes unreferenced model/particle/sound, ReplaceTexture all/selected/
    entity-bound-skip/displacement-preserving/no-match-no-op/blank-refusal,
    ReplaceModel all/selected/unregistered-refusal, texture alpha data.
- Repo files untouched by this session (only reads + copies INTO `/tmp`).

## Harness-only changes (this session — `/tmp/iso-browsers-count` ONLY, never the repo)

The previous session left the harness at 26 errors after excluding CompileRunner.cs
(54 errors with it excluded the other way — CompileCordon needs its CompileBrush).
This session closed the cascade:

1. **Restored `kernel/Effigy/Map/CompileRunner.cs` verbatim from the repo** into the harness
   copy. It defines all the `Compile*` types (`CompileInput`, `CompileBrush`, `CompileFace`,
   `CompileLight`, `CompileResult`, `CompileIssue`, `CompileBspResult`, …) that
   CompileCordon / CubemapBake / MapPacker / LightEnvironment / MapLayers need.
2. **Added `kernel/Effigy/Map/PreCompileGate.Stub.cs`** (harness copy only). CompileRunner's
   only references into excluded files are the pre-compile gate block, so the stub supplies
   exactly that surface:
   - `MapIssueType` / `MapIssueSeverity` / `MapIssue` — copied VERBATIM from `MapCheck.cs`
     (minus `MapIssue.GoTo()`, which needs the excluded `GoToTarget`; nothing here calls it).
   - `PreCompileGateResult` — copied VERBATIM from `PreCompileGate.cs`.
   - `PreCompileGate.IssueCode` / `.ToCompileIssue` — copied VERBATIM from `PreCompileGate.cs`.
   - `PreCompileGate.Check(CompileInput, CompileOptions)` — DELIBERATE THROW
     (`NotSupportedException`): the real gate runs MapCheck/IssueEngine, excluded here.
     The assetregistries group never calls CompileRunner, so this path is never exercised
     in this harness. A fake clean pass would lie; the throw is honest.
3. **Extended the existing `kernel/Effigy/Script/BuildScript.Map.cs` harness stub** with
   `MapPass2VerbNames` + `MapPass2VerbBacking()` copied VERBATIM from the real
   `BuildScript.MapPass2.cs` (pure string data, no excluded-type references), and a
   `TryRunMapVerb` with the repo signature that throws `NotSupportedException` (the real
   dispatcher lives in worker 64's missing `BuildScript.Map.cs`; map verbs are not runnable
   in this harness and the assetregistries group never calls them).
   `RunMapPass2Verb` was NOT copied — nothing in the harness references it.

Files still EXCLUDED from the harness copy (the known-broken set, unchanged):
BrushToModel, ClipPolygon, HintSkip, InstancesCollab, MapBakeExport, MapBrushPrimitives,
MapCheck, MapClip, MapDocumentOverlays, MapDocumentSkybox, MapExportRoundtrip,
OverlaysDecals, PreCompileGate, PropFade, RoomHelpers, SkyboxPreview, SkyboxWorldspawn,
BuildScript.MapPass2. Plus copy-only edits carried over from the previous session:
MapLayers.cs (ternary fix) and the BuildScript.Map.cs stub above.

## Checks DEFERRED (final integration gate, not this ticket)

- Full repo build (`dotnet build` on the real tree) — the shared tree still does not compile
  (other tickets' files: MapFace/MapBrushFace, duplicate InstanceIoProxy, MapLayers:299,
  ApiCatalog BuildScript.MapVerbNames, etc.). NOT this ticket's files.
- Real test-runner `assetregistries` group run + `TestRegistry.cs` registration line.
- The real PreCompileGate / CompileRunner gate path — unexercised in the harness by design
  (stub throws); runs for real only on the real tree.

## Blockers

None remaining for this ticket. The harness is green and the group passes twice.

## Honest caveat

Implementation-done is NOT runtime-verified on the real tree: the 33/33 pass is an
isolated-harness result with 19 broken kernel files excluded and 3 harness-only stubs
(PreCompileGate.Check throws, TryRunMapVerb throws, MapVerbNames empty). The real
`assetregistries` group on the real test runner is the integration gate's job.
