# Hammer compile presets — Only-Entities + Fast-VIS + named presets + go-to log entries

Worker: daxter-hammer-compile-presets. Board: `hammer-compile-presets-only-entities`.
Date: 2026-11-13 ~17:11 EST. Status: **implementation done, NOT runtime-verified by a full build**
(see "Checks" below).

## What it does

Extends pass-1 `CompileRunner` (`kernel/Effigy/Map/CompileRunner.cs`, now 3000 lines)
with four features, all runner options + data, **zero UI code, zero external processes**:

1. **Only-Entities** — `CompileRunner.RunOnlyEntities(input, lastBuild, options, progress, cancel)`
   (+ Async). Rebuilds the entity lump only; reuses the prior build's BSP + PVS +
   lightmaps by object reference, writes only `<name>.entities`, never rewrites prior
   files. Refuses a changed brush with a `stale-build` issue (FNV-1a fingerprint over
   map name + brushes + lights + player start — entities excluded by design); throws
   `ArgumentException` on null input / artefacts with no BSP (API misuse).
2. **Fast-VIS** — `CompilePreset.FastVis` preset (BSP + Fast 1-hop PVS, no lightmap) and
   `CompileOptions.FastVis` flag: downgrade-only, forces Fast when a visibility stage
   exists, never adds visibility to QuickPlay, never upgrades to Full.
3. **Named presets** — `CompileNamedPreset` (file-safe name ≤64 chars, base preset,
   visibility/lightmap overrides, FastVis, description; JSON via System.Text.Json.Nodes)
   + `CompilePresetStore` (case-insensitive, Save/Get/TryGet/List sorted/Remove,
   ToJson/FromJson, SaveToFile/LoadFromFile — missing file = empty store). Store is a
   **sidecar file**, because `MapCompileSettings` has no preset slot. `CompileRunner`
   ctor + static Run/RunAsync overloads accept a named preset; explicit per-run
   options win where set.
4. **Go-to log entries** — `CompileLogEntry` (Timestamp/Stage/Severity/Code/Message/
   SourceRef) + `CompileSourceRef` (BrushId?/EntityId?/LeafId?/Location?/Subject).
   `CompileIssue` gained optional brushId/entityId/leafId + `SourceRef` + `ToLogEntry(stage)`.
   Invalid-brush issues carry BrushId; leak issues carry LeafId + gap coordinates;
   invalid-entity issues carry EntityId. `CompileResult.LogEntries` (count == Log + Issues).
   Issue→stage mapping in FinishLog covers entity (`invalid-entity`, `stale-build`),
   pack (UnknownModel/MISSING_MODEL/UnknownSound/MISSING_SOUND/UnknownParticle/
   MISSING_PARTICLE), cubemap (probe-inside-solid / probe-too-close-to-wall /
   probe-outside-map / duplicate-probe / invalid-probe), `stage-failed` → FailedStage,
   else BspBuild.

Also: `CompileEntity` (Id/ClassName/Origin/TargetName/Keyvalues, validated in the BSP
stage of full runs); `CompileEntityLump` (`GEPPETTO-ENTITIES 1`, deterministic,
id-ordered, keyvalues sorted) — full runs build/write the lump too when input has
entities; `CompileBuildArtefacts` + `CompileResult.ToArtefacts(input)`; `CompileInput`
gained `Entities`; `CompileInput.FromDocument` also fills Entities (id = sequential in
document order — MapEntityData.Id is a GUID string, not parseable); `CompileStage/
CompilePhase.EntityLump`; `CompileSeverity.Info` (log-only).

## Files changed (repo)

- `kernel/Effigy/Map/CompileRunner.cs` — rebased P2 onto the packing + parallel
  versions (see below); sha256 `00327a4cfde701ccef0325a449374a05a46cd7eae6b5b59f2f05545267bef859`.
- `tests/CompilePresetsTests.cs` — new, group `compilepresets`, 15 checks.
- `tests/fixtures/hammer/hammer-compile-presets-only-entities/entities-room.json` — new
  (sealed room + playerStart + light + 3 entities: light_spot, prop_static, logic_relay).
- `tests/fixtures/hammer/hammer-compile-presets-only-entities/README.md` — new.

NOT touched: `OpsCli.cs`, `McpMapTools.cs`, `tests/TestRegistry.cs` (coordinator/integrator
owned). `OpsCli.ParsePreset` has no "fastvis" string — integrator-owned, deferred.

## Rebase note (multi-worker hotspot)

The PC file changed mid-task (16:59:59 EST, 1800 → 2128 lines). The P2 was rebased onto
it and **all sibling work preserved**: packing worker's `using Effigy.Map`, CubemapBuild/
Pack stages, 7-arg `CompilePresetDef` ctor, `AssetRegistries`, `CompileInput.FromDocument`,
`MapPacker` calls; parallel worker's `MaxDegreeOfParallelism`/`SingleThreaded`,
`_progressLock`, parallel brush-validation / PVS / lightmap branches. Verified: diff vs
the 16:59 PC version = 913 lines, every removed line an intended P2 replacement.
Pre-push backup of the pre-P2 file: `/tmp/opencode/compile-presets-backup/CompileRunner.cs.before-presets-20261113`
(sha256 `16e00bc33acedd27416dcc87ff5061e9c23d9d0e68a9e165815821f1715bbabc`).
Cordon worker has NOT landed — `PrepareInput` still returns input unchanged.

## Checks

Isolated harness `/tmp/opencode/compile-presets-isolated/` (real CompileRunner.cs +
real Vec/LeakFinder/ParallelStageRunner/ToolTexture/FaceMaterial + faithful stubs for
MapDocument/MapBrushData/MapEntityData/MapBrushFaceData and for the pack/cubemap
surface — see `MapStubs.cs` / `PackStubs.cs` headers; cubemap/pack stages are NOT
verified by this harness, they belong to the packing ticket):
`dotnet run --project /tmp/opencode/compile-presets-isolated/isolated.csproj -c Release -- all`
run from the repo root → **34 passed, 0 failed** (19 compilerunner + 15 compilepresets),
including: Fast-VIS def + run shape, FastVis flag semantics, Only-Entities reference
reuse + no rewrite + stale-build refusal + no-prior-BSP throw + duplicate-id / classless
entity rejection, named-preset JSON round-trip + apply-on-run, leak gap coordinates +
brush-id log entries, deterministic lump text, source scan for Process/shell spawn
tokens (clean), pre-cancelled Only-Entities.

DEFERRED to the final integration gate: prescribed suite run
`dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter compilepresets`
(shared tree does not compile: pre-existing `MapBrushPrimitives.cs` MapFace break,
`McpMapTools.cs:1771` references nonexistent `CompilePreset.Quick`, etc.); TestRegistry
registration for `CompileRunnerTests` and `CompilePresetsTests`; `OpsCli` "fastvis"
string. **Implementation done is NOT runtime-verified by a full build.**
