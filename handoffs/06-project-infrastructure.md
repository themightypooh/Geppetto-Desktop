# 6. Projects, paths, reload and settings — P1 to P4

Read RULES.md / CONTRACTS.md. No asset browser, file dialog, preference panel or other UI work.
This is infrastructure for reliable documents, not permission to relocate the user's games.

## Existing seams

- `DesktopOptions.Model` currently defaults to an absolute path on this machine.
- `DocumentLoader`, `SurveyPlayerAssembly` and `CamheadAssembly` load external source files.
- `MarionettePlayer.LoadClip(string)` resolves `RigAsset` relative to the clip directory.
- `StudioCache` keys geometry on source/dependencies/kernel build; do not replace it with
  a filename-only cache. App-only builds should continue to leave kernel stamps unchanged.
- `HeadlessRender` and the interactive app must open the same project/source options.

## P1 — Asset references and deterministic path resolution

**Prerequisites:** none. **Own:** new CPU-only resolver/types + focused tests. Start here.

1. Implement `AssetReference` distinguishing project-relative content from explicitly
   declared external read-only assets. Preserve a stable logical ID separately from paths.
2. Resolve against the owning project/document root, never process working directory.
   Return both canonical identity and a useful display/reference path.
3. Normalize `.`/`..` and separators; on Linux preserve case sensitivity. Enforce allowed
   roots for project-owned IO, including symlink escape where files already exist. External
   assets are allowed only when explicitly declared; they do not make arbitrary traversal safe.
4. For writes to nonexistent paths, validate existing ancestors/target policy. Asset reads
   must not create directories. Path resolution must not rename/copy the user's source files.
5. Provide a reference-relocation operation for Save As/project moves. Do not embed
   `/home/poo/...` in serialized project content as the only way to reopen it.

**Tests:** different cwd; spaces/Unicode; two same basenames; relative dot segments; missing
file; symlink escaping a project root; external opt-in; writable output path; moving a fixture
project; case mismatch diagnosed. Existing absolute developer `--model` remains supported.

## P2 — Versioned manifest and source assemblies

**Prerequisites:** P1, E1; E4 before claiming safe save support.

1. Implement the proposed `DesktopProject` manifest, initially JSON with explicit schema
   version (proposed extension `.geppetto-project.json`). It references source documents,
   clips/control rigs, material overrides and assembly parts; it does not replace their formats.
2. Persist stable IDs and source/import options. Keep ephemeral caches, GL handles, world
   pose arrays and temporary selections out of the authored manifest.
3. Make assembly membership/filtering/transforms/material remaps data-driven. Preserve
   Survey's separate head/neck bone merge and Camhead's documented authoring-frame adapter;
   do not apply either correction generically to all FBX imports.
4. Select project-open in both headless/interactive entry points through an explicit option
   integrated by the coordinator. Remove mandatory local source paths from the portable path.
   Do not silently select an unrelated fallback character if a referenced asset is missing.
5. Validate schema/version, duplicate IDs, missing required fields, dependency cycles and
   incompatible rigs. Unknown future versions fail clearly; unknown optional fields are
   retained or explicitly diagnosed, not silently destroyed on save.

**Tests:** two-source assembly; moved project; missing head/rig; duplicated ID; cyclic parent;
material override identity; reopen round-trip; unknown version. Survey-specific counts are
fixture assertions only. Source FBX/model documents remain unchanged.

## P3 — Dependency watching and atomic reload promotion

**Prerequisites:** P1/P2, E1/E4. **Experienced integration worker**; concurrency matters.

1. Watch the resolved dependency set, not just the top-level model: textures, assembly
   meshes, control rigs and clips. Deduplicate filesystem events and debounce bursts.
2. A watcher event queues work; it must not call GL, mutate the live session or rebuild
   geometry on the event thread. Treat partially written files as retryable bounded failures.
3. Build/validate a detached new snapshot tagged with document/revision/job generation.
   Promote only the latest still-relevant result on the render thread. Last good data stays
   visible through reload failures; release obsolete GL resources on their owner thread.
4. Do not overwrite dirty local edits on an external edit. Produce a conflict result with
   source identity/revisions; leave user-authored data untouched until an explicit resolution.
5. Suppress/reconcile own atomic saves to avoid a save→reload→save loop. Cancel watchers
   and queued work when closing/changing project; callbacks must not hit disposed objects.

**Tests:** 20 duplicate notifications produce one build; A then B complete out of order;
delete/recreate; partial write then valid file; bad replacement retains old snapshot;
dirty conflict; own-save echo; project switch/dispose during reload; no GL calls on workers.
**Done:** tests use injected clock/watcher/task scheduler, not timing-sensitive sleeps alone.

## P4 — Preferences, autosave scheduling and recovery discovery

**Prerequisites:** P1/P2 and E4. Keep preferences separate from authored project state.

1. Store user preferences under XDG config (fallback documented), caches under XDG cache,
   recoveries under a documented application-owned state location. Respect environment
   overrides; never use cwd, the source asset folder or a hardcoded home directory.
2. Define defaults/validation for supported backend settings: exposure, playback/snap defaults,
   autosave interval/retention and recent-project references. Do not serialize GL state.
3. Write preferences atomically. A corrupt preferences file reports a diagnostic and uses
   safe defaults; it must not prevent explicit headless model rendering from working.
4. Autosave captures a consistent dirty revision via E4 with bounded scheduling: no overlapping
   save storms or disk work on render/timer callbacks. It never marks the main document clean.
5. Discover recoveries and return metadata through a service/CLI test seam. Do not automatically
   overwrite the original on launch or claim recovery is done without a reopen test.

**Tests:** XDG override; missing/readonly/corrupt settings; bad values; portable project path;
dirty vs clean scheduling; close during save; retention deletes only owned recovery entries;
recovery parse + source preserved. No UI dialog implementation in this ticket.
