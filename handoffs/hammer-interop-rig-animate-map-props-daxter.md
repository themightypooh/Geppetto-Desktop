# hammer-interop-rig-animate-map-props — handoff (daxter-hammer-interop-rig)

Ticket: Hammer Desktop Interop: rig / animate on map props + entities
(prop_static/dynamic/physics). Board id `hammer-interop-rig-animate-map-props`,
status at handoff: implementation done, isolated-harness verified 18/18.
**Implementation done is NOT runtime verified** (PO workflow override
2026-11-13). No full project build, no app launch, no UI work.

## What was already on disk vs what this session did

The implementation, tests and fixtures were ALREADY in the working tree,
uncommitted, from a prior session that never marked the ticket done
(RigFeatures.cs +823 lines, RigAnimateMapTests.cs + fixtures, all dated
2026-11-13 ~16:27). This session changed NO repo source files. It:

- pulled the spec from the board, claimed + started the ticket;
- read AGENTS.md, handoffs/README.md, handoffs/RULES.md, handoffs/CONTRACTS.md;
- verified every API the implementation uses against the landed types and
  the registry (see "Verified against source" below) — including correcting
  two stale claims in flight: the hammer-14 note that EntityInput /
  EntityOutput are defined twice is STALE (one definition each now, both in
  EntityRegistry.cs), and the hammer-14 note that EntityReport.cs has a
  CS1513 brace error is STALE (EntityReport.cs compiled clean in the harness
  glob — no error observed);
- built the isolated harness below and ran group `riganimatemap` TWICE:
  **18/18 PASS, 0 failed, 0 skipped, exit 0, both runs.**

## Changed files (all pre-existing uncommitted work, verified here)

- `kernel/Effigy/Features/RigFeatures.cs` lines 430-1252 (+823, purely
  additive; HEAD is 429 lines, untouched). The PropClip block. Block sha256
  at verification time: `e12d0ceddca327a4ec630f7e018db48da1bb5e6a02d5b80d56acb272b937895d`
  (re-checked after the runs — unchanged).
- `tests/RigAnimateMapTests.cs` (NEW, 569 lines, group `riganimatemap`,
  18 checks).
- `tests/fixtures/hammer/hammer-interop-rig-animate-map-props/props.json`
  + `README.md` (NEW). Fixture drives placement / binding / playback names
  only; clips are built in code by the tests.
- `tests/TestRegistry.cs` — UNTOUCHED (coordinator-owned). The group is NOT
  registered there. Integrator must add
  `RigAnimateMapTests.Register( registry, settings );`.

## API (all in namespace Effigy, RigFeatures.cs lines 430-1252)

- `PropClipException` — codes: UnknownClip, UnplayableClip,
  NotAnimatableProp, UnknownInput, UnsupportedInput, NoBinding,
  UnknownTarget, AmbiguousTarget, InvalidRootMotion.
- `PropClipBinding` — EntityId / ClassName / Model / ClipName / Input
  ("" = default/spawn clip) / Loop / RootMotion; root-motion modes
  `as-authored` / `extract` / `strip` / `inplace`.
- `PropClipBindings` — `BindDefault(entity, clips, loop, rootMotion)`,
  `BindInput(entity, input, clipName, clips, loop, rootMotion)`,
  `Find(entityId, input)`, `ForEntity(entityId)`, `Remove(entityId, input)`,
  `IsAnimatableClass` (prop_dynamic, func_door, func_button only),
  `BindableInputs` (prop_dynamic: TurnOn; func_door: Open/Close/Toggle;
  func_button: Press). Bindings are records keyed by entity id + input, NOT
  keyvalues (the registry has no clip keyvalues on movers — see Limits).
- `PropClipIssue` + `PropClipIssueSeverity` (Error/Warning) — codes
  UnknownClip, LoopSeamPops, UnplayableClip, MissingModel,
  NotAnimatableProp, UnknownInput.
- `PropClipValidator` — `ResolveClip(clips, name)`,
  `ValidateBinding(binding, clips, skeleton, registry?)`,
  `Validate(entities, bindings, clips, skeleton)` (stored bindings + any
  prop_dynamic DefaultAnim with no stored default binding + MissingModel
  warnings).
- `PropClipPlayback` — ClipName / Frame / Playing / Finished / Loop /
  DoorOpen / LastFiredOutput; `LastFrame`, `TimeSeconds`.
- `PropClipPlayer(entities, clips, bindings, skeleton)` — `Fire(EntityIoInput)`,
  `Fire(entityId, input, parameter)`, `Advance(entityId, frames)`,
  `Sample(entityId, skeleton)` (RigAnimFile.SampleLocal), `State(entityId)`,
  `Stop(entityId)`. I/O inputs: Open/Close/Toggle/Press/TurnOn via bindings;
  SetAnimation plays the clip its parameter names; SetDefaultAnimation
  re-points the DefaultAnim keyvalue (registry-checked via
  MapEntities.SetKeyvalue) and rebinds; TurnOff stops; finished one-shots
  fire registry outputs (prop_dynamic OnAnimationFinished, func_door
  OnFullyOpen/OnFullyClosed, func_button OnIn). Target is a targetname
  (case-insensitive) or `#id`; unknown target and shared targetname are
  named errors. Root motion via RootMotionPolicy Extract / Strip / InPlace.
- `PropClipExportRow` + `PropClipExport.Manifest(entities, bindings, clips, skeleton)`
  — one row per binding plus unbound prop_dynamic defaults: entity, model,
  clip, input, frames, frame rate, root bone, HasRootMotion, seam pass.
  Manifest is DATA ONLY: it writes no files and no clip bytes (MapBakeExport
  owns OBJ/GLB writing).

## Verified against source (read, not assumed)

- `core-source1.json` (schemaVersion 1): prop_dynamic declares model /
  DefaultAnim / RandomAnimation / DisableBoneFollowers and inputs
  SetAnimation / SetDefaultAnimation / TurnOn / TurnOff / Enable / Disable,
  output OnAnimationFinished; prop_static model only, NO inputs; prop_physics
  model only + Wake/Sleep/EnableMotion/DisableMotion/Break (physics inputs,
  not animation); func_door Open/Close/Toggle/Lock/Unlock, outputs
  OnFullyOpen/OnFullyClosed; func_button Press/Lock/Unlock, output OnIn.
  The binding / player input lists and output mapping match this file.
- `ClipLibrary.Resolve()` returns a NEW RigAnimFile on every call (tracks
  remapped, RootMotionSettings object shared by reference but never mutated
  by Extract/Strip — Extract is called with a fresh settings object, Strip
  clears RootMotion on the copy only). The "library entry is never rewritten"
  claim in the block header holds for the tested paths.
- `MapEntities` API used (Registry, Entities, PlacePointEntity,
  PlaceBrushEntity\<TBrush\>, SetKeyvalue, GetKeyvalue, FindByTargetName,
  FindById, MapEntity.TargetName) all exist as used. `EntityIoInput`
  ctor (target, input, parameter = "") exists in EntityIo.cs.
  `LoopSeamChecker.Check(clip, skeleton)`, `RootMotionPolicy.RootBoneOf /
  Extract / Strip`, `RigAnimFile.SampleLocal / LastFrame / PlayInPlace /
  RootMotion / SourceModel`, `Skeleton.AddBone / RootBone / IndexOf`,
  `RigPose / RigTrack / RigKey / RigEase / RigTrackKind` all exist as used.
- No UI was added. No shared entry point was edited (no TestRegistry.cs,
  no .csproj, no MapEntities.cs).

## Checks run (isolated harness — the only build this session did)

Harness: `/var/tmp/daxter-riganimatemap-harness/` (on the PC; also staged at
`~/workspace/daxter-hammer-interop-rig/harness/` in Daxter's workspace):
`Harness.csproj` (full kernel glob `kernel/Effigy/**/*.cs` by absolute path
+ real `tests/RigAnimateMapTests.cs` + `Program.cs` + `TestCore.cs`
(verbatim TestRegistry/TestCase/TestOutcome/TestSettings core, registration
manifest NOT copied) + `Stub.cs` (minimal Marionette.Desktop.DesktopOptions
so the verbatim core compiles) + `extract.py` + `close.py` + `prune.py`),
`PropClip.cs` (RigFeatures.cs lines 430-1252 extracted VERBATIM by
extract.py — diff-checked byte-identical, sha256 above — because the whole
RigFeatures.cs cannot be built here: its HEAD half is the Feature framework
and drags in the world; the block itself references none of the HEAD types).

Build: `dotnet build Harness.csproj -c Release --artifacts-path
/var/tmp/daxter-riganimatemap-harness/artifacts` — succeeded after
excluding 19 uncompilable files (below). No repo `bin/obj` touched.

Run (CWD = repo root, so the tests find `core-source1.json` + fixtures):
`dotnet /var/tmp/daxter-riganimatemap-harness/artifacts/bin/Harness/release/RigAnimateMapHarness.dll --filter riganimatemap`
— run 1: 18/18 PASS, exit 0. Run 2 (repeat, determinism): 18/18 PASS,
exit 0. All 18 checks:

prop_dynamic looping default validates clean; unknown default clip is
UnknownClip; popping loop seam is LoopSeamPops (one-shot is not); looping
default wraps on Advance; prop_static + prop_physics cannot bind;
no-model warns MissingModel; door Open/Close bind + validate (unknown input
/ clip rejected); door Open plays on the Open input (headless: Fire an
EntityIoInput, step frames, sampled pose, finish at last frame, DoorOpen,
OnFullyOpen); Close after Open ends closed + OnFullyClosed; Toggle
alternates; Open with no binding is NoBinding; SetAnimation plays the
named clip + TurnOff stops; SetAnimation on a door is UnknownInput;
SetDefaultAnimation re-points DefaultAnim + rebinds; missing target is
UnknownTarget / shared targetname is AmbiguousTarget / `#id` addressing;
root-motion extract moves hips travel onto the root + strip plays in place;
export manifest lists prop + door (3 rows, frames/root bone/seam);
fixture props.json end-to-end (0 issues, 3 manifest rows, door ends open
firing OnFullyOpen).

## Checks deferred (final integration gate)

- Full repo build (`dotnet run --project tests/Desktop.Tests.csproj`) and
  the real-runner `riganimatemap` group run (needs the TestRegistry.cs
  registration line above).
- App launch / headless render / GPU checks (none apply: headless data-model
  ticket, no render path).
- No full-suite regression run (shared tree does not compile — see below).

## Blockers / shared-tree breakage (NOT this ticket's files, do not fix here)

The shared kernel does not compile as a whole. Harness exclusions (19):
MapBrushPrimitives.cs (uses MapFace, which does not exist — real API is
MapBrushFace), InstancesCollab.cs (InstanceIoProxy defined a second time;
MapInstances.cs:631 defines it too, different shape), MapInstanceLibraries.cs
(CS0050), ApiCatalog.cs (references BuildScript.MapVerbNames, which does not
exist), OverlaysDecals.cs, HintSkip.cs, ClipPolygon.cs,
MapExportRoundtrip.cs (build against the old Displacement MapBrushFace, not
the landed MapBrush), MapDocument.cs (uses EntityOutput where it means
EntityIoOutput + lacks `using System.IO;`), MapLayers.cs (CS0173 at :299),
CadToMap.cs, CompileRunner.cs, MapDocumentIo.cs, MapDocumentOverlays.cs,
MapLayerHierarchy.cs, MapPacker.cs, CompileCordon.cs, CubemapBake.cs,
LightEnvironment.cs (last 9 excluded only because they reference types from
the files above; prune.py records the chain). Also from other workers'
handoffs, still open at gate time: BuildScript.Map.cs does not exist
(worker 64); ClipPlane is ambiguous (CompileRunner.cs vs MeshClip.cs).
The final gate must reconcile these; this ticket's files compile clean
inside the harness.

## Limits, stated plainly

- One skeleton per validator / player call: a map whose props were authored
  on different rigs validates / plays them one rig at a time. There is still
  no model-path -> skeleton lookup (ModelEntry in AssetRegistries.cs carries
  no rig reference).
- A looping clip is seam-checked; a one-shot clip is not (its seam is the cut
  back to idle). A clip whose motion lives only in Lane K channel curves
  bakes its bind pose (GameClipPipeline limit, not this layer's).
- prop_physics can reference a rigged model but cannot play clips (registry
  gives it no animation inputs; physics owns its motion). prop_static cannot
  play clips either.
- Door / button clip bindings live in PropClipBindings records, not in
  keyvalues (registry has no clip keyvalues on movers; MapEntities rejects
  unknown keyvalues at placement — do NOT invent keyvalues here).
- The "model exists" check lives in AssetRegistries.cs (ValidateFiles),
  not here: this layer validates the clip binding.
- Tests use a TestBrush stand-in through generic PlaceBrushEntity\<TBrush\>;
  MapBrush itself was not exercised by this group.
