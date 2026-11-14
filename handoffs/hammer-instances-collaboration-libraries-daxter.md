# Handoff — hammer-instances-collaboration-libraries (daxter-hammer-instances)

Board: `geppetto-desktop` / `hammer-instances-collaboration-libraries`
("Hammer Desktop P3: instances as collaboration — shared libraries, name
fix-up, IO proxy, VC settings"). Worker: daxter-hammer-instances, carry-on
from stopped worker muse-3-desktop-hammer-81. Branch: rigskin-wave1,
HEAD 99b942d. Date: 2026-11-13 ~17:15 EST.

## What was already there (worker 81, never marked done)

Worker 81 had already implemented the whole ticket on disk, but the board
`files` field was empty, so it looked unstarted. The implementation lives
in `kernel/Effigy/Map/InstancesCollab.cs` (1878 lines at handover) +
`tests/InstancesCollabTests.cs` (621 lines, group `instancescollab`, 18
checks) + fixtures in
`tests/fixtures/hammer/hammer-instances-collaboration-libraries/`
(registry, room-small-v1/v2, compile-settings, README). It was NOT
runnable as found — see "Defects fixed" below.

## What this session did

1. **Duplicate type fixed (shared-tree break).** `InstancesCollab.cs`
   defined `InstanceIoProxy` in `Effigy.Map`, colliding with P2's
   `Effigy.Map.InstanceIoProxy` in `MapInstances.cs`. Renamed 81's type to
   `LibraryIoProxy` (and `InstanceIoProxySet` → `LibraryIoProxySet`).
   The JSON section name is unchanged (`instance_io_proxies`). Also
   qualified all `MapCompileSettings` uses in the tests as
   `Effigy.Map.MapCompileSettings` (bare name is ambiguous with
   MapDocument's `Effigy.MapCompileSettings`).
2. **Rename-aware fix-up (the one real gap).** 81's update kept names
   stable only when a target kept its name. A library update that
   RENAMES a target now takes an explicit rename map:
   - `InstanceLibraryService.UpdateInstance(instance, subMap,
     hostTargetNames = null, renames = null)` — `renames` is old → new
     targetname. The renamed target keeps its old RESOLVED (prefixed)
     name, stale references inside the new sub-map (keyvalues, IO rows)
     are rewritten before fix-up, renamed targets count as Kept (under
     the new name), not Added/Removed. Refusals (all atomic, adopt
     nothing): blank or `!`-special names, rename-to-itself, new name not
     defined in the new version (`instancescollab-rename-unknown-target`),
     old name still defined there (`instancescollab-rename-collision`),
     old name never resolved by this instance.
     `UpdateInstanceResult` gains `RenamedNames` (`"old -> new"`,
     unprefixed). Pinned-version refusal still applies (re-added the pin
     check UpdateInstance lost when it stopped calling ResolveInstance).
   - `LibraryIoProxy.RemapRenames(renames)` — rewrites forward targets
     and backward sources through the same map; backward host targets,
     `#id` sources and `!` specials never move. Returns legs remapped.
     Call it with the same map, then re-validate the proxy.
   - `ResolveInstance` gained an internal core (`ResolveWithStableMap`)
     so UpdateInstance can pass the rename-carried stable map; public
     resolve behaviour is unchanged.
3. **Pre-existing defects fixed so the group actually compiles/runs:**
   - `InstancesCollab.cs:246` `Path.IsPathRooted` inside static
     `ValidateRelativePath` bound to the instance property `Path`, not
     `System.IO.Path` (CS0120) — now fully qualified.
   - `InstancesCollabTests.cs` `RequireVertex` was a `static` local
     function calling non-static local `Require` (CS8421) — `static`
     dropped.
   - `InstanceLibraryRegistry.Update` validated the manifest BEFORE the
     version/path conflict check, so a same-version manifest moved to a
     new path failed entry validation (entries still under the old path)
     and the `version-conflict` branch was unreachable. Conflict checks
     now run first (guarded by `IsValid(version)`); full validation
     still runs before any replace.
4. **Tests + fixture.** 2 new checks (20 total): rename keeps the
   resolved name + remaps both proxy legs; rename refusals are atomic.
   New fixture `room-small-v3.json` (1.2.0: `room_door` → `room_gate`,
   same entity id, relay keyvalue + IO row deliberately still naming the
   old name), documented in the fixture README.

## Files changed (all untracked, edits only, no commit)

- `kernel/Effigy/Map/InstancesCollab.cs` — 2070 lines (was 1878)
- `tests/InstancesCollabTests.cs` — 734 lines (was 621)
- `tests/fixtures/hammer/hammer-instances-collaboration-libraries/room-small-v3.json` — new
- `tests/fixtures/hammer/hammer-instances-collaboration-libraries/README.md` — v3 bullet added

Superseded drafts (NOT pushed, do not use): an earlier
`MapInstanceLibraries.cs` + `MapInstanceLibrariesTests.cs` + `library.json`
draft was removed from the PC before the 81 implementation was found; it
also had a CS0050 error. Local copies only, in the worker's workspace.

## Checks run

Isolated harness `/tmp/iso-instances-collab/` on the PC (dotnet 10.0.402):
verbatim `Vec.cs` + `MapInstances.cs` from the repo, edited
`InstancesCollab.cs` + `InstancesCollabTests.cs`, verbatim
`CompilePreset`/`VisibilityQuality` enums from `CompileRunner.cs`
(the file itself pulls the whole compile pipeline, so only the enums),
stub TestRegistry/TestSettings + Main (real `tests/TestRegistry.cs`
depends on `Marionette.Desktop`). **22/22 PASS, exit 0.**
Compiling MapInstances + InstancesCollab together is the proof the
duplicate-type fix works. Fixtures loaded from the real fixture dir via
the tests' own `FindFixture` climb.

## Deferred to the final integration gate (NOT done here)

- Full project build / app launch / official test-runner run of the
  `instancescollab` group.
- `tests/TestRegistry.cs` registration (coordinator-owned):
  `InstancesCollabTests.Register(registry, settings);`
- Integrator reconciliation of the parallel types (NOT resolved here):
  `LibrarySubMap` vs MapInstances' sub-map model, `LibraryNameFixup` vs
  `InstanceNameFixUp`, `Effigy.Map.MapCompileSettings` vs
  `Effigy.MapCompileSettings` (MapDocument's, persisted as `op
  set-compile`). MapDocument embedding of the four JSON sections
  (`instance_libraries` / `library_instances` / `instance_io_proxies` /
  `compile_settings`) is a documented seam; `MapDocument.cs` /
  `MapDocumentIo.cs` were NOT edited.

## Caveats

- Implementation done is NOT runtime verified: the harness is isolated,
  headless, and uses stub enums + stub registry. No UI was added
  (backend only, per the ticket).
- The shared tree still does not compile for unrelated reasons (other
  workers' files: MapFace/MapBrushFace, MapDocument EntityOutput, etc.)
  — not this ticket's scope.
- Compile settings needed NO new code: already satisfied twice over
  (81's `Effigy.Map.MapCompileSettings` JSON section + MapDocument's
  landed `op set-compile` persistence).
