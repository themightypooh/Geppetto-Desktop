# 2. Retained editing, undo, keys, safe saves — E1 to E5

No UI. These services must be exercisable from CPU tests before a panel/gizmo calls them.
Read RULES.md and CONTRACTS.md. One ticket at a time.

## Existing APIs and traps

- `DocumentLoader` currently discards editable source state after making `ViewportMesh`.
  GPU arrays are deliberately nulled after upload; they are not an authoring document.
- `PartStudio.Add`, `Insert`, `Remove`, `Move`, `MarkDirty`, `Rebuild` already exist.
- `StudioDocument.Read`/`Write` serialize `.effigy`; their file helpers are not atomic saves.
- `RigAnimFile` contains mutable tracks/keys. `RigAnimWriter.Write(clip, baseJson)` preserves
  fields its partial representation does not own. `Write(clip)` alone can discard metadata.
- `MarionettePlayer` caches `_lastFrame`, tracks and limits. Editing a clip requires explicit
  evaluator invalidation; changing a List does not refresh these caches automatically.
- `../geppetto/Code/RigControl/` and `Editor/RigControlEditor/` may be read for key/undo
  semantics, but desktop must not depend on their engine/UI assemblies.

## E1 — Retain a document/session independent of rendering

**Prerequisite:** P1 for portable references; CPU fixture work can start independently.
**Own:** new session/state files; `DocumentLoader` integration only under a shared-file lease.

1. Implement `DocumentSession` with stable document identity, source kind/path, revisions,
   saved revision, current playhead/selection, and retained authoring state.
2. An Effigy session retains its `PartStudio`. An imported FBX retains original source/import
   data and import options; it must not masquerade as a feature tree. Retain original clip JSON
   alongside its parsed `RigAnimFile`; preserve the distinction between source and derived data.
3. Expose a typed change/result boundary for building a derived render snapshot. Include
   document ID and revision so an old async build cannot replace a newer session's snapshot.
4. Keep GL handles and UI widgets out of session serialization and CPU tests.
5. Loading failure leaves the previous valid session/snapshot usable. Ownership of old/new
   meshes must be explicit; do not mutate the currently drawn skeleton from a worker thread.

**Tests:** open two documents; failed second load retains the first; source exists after GPU
upload; stale build completion is rejected; editing a session does not mutate a detached snapshot.
**Done:** real loader → session → derived snapshot integration, not an unreferenced state class.

## E2 — Reversible commands, grouped transactions and dirty state

**Prerequisite:** E1. No disk IO in this ticket.

1. Implement `EditHistory` with execute/undo/redo and a transaction that supports begin,
   update preview, commit, cancel. A drag becomes **one** history entry, not one per mouse move.
2. Capture before-state, not live references to mutable Lists. Redo must restore the same
   IDs/values; do not rerun randomized feature generators as the definition of redo.
3. No-op edits create no entry. A new committed edit after undo discards the redo branch.
4. Track saved-state identity separately from stack length. Undo back to the last saved state
   becomes clean; undo then branch to a different state at the same depth stays dirty.
5. Failed validation/command execution leaves state and history unchanged. Cancel restores
   the pre-transaction state and does not mark the document saved or change its bind pose.
6. Provide bounded history policy with explicit behaviour when the saved state is evicted.

**Tests:** execute/undo/redo exact equality; 100 preview updates produce one entry; cancel;
no-op; execute throws; branching; save marker; history eviction; two sessions isolated.
**Done:** an actual pose/feature/key fixture goes through the history service.

## E3 — Clip-key editing commands and playback invalidation

**Prerequisites:** E1/E2. Scope is keys, not the entire animation engine.

1. Implement insert/replace/delete/move key operations keyed by `(track kind, target name,
   frame)`. Keep frame ordering stable; define duplicate-frame replacement rather than letting
   whichever List iteration wins decide the result. Reject negative frames/nonfinite poses.
2. Preserve interpolation enum values, tangent arrays and unedited tracks. A move onto an
   occupied frame replaces that key under an explicit undoable policy.
3. Extend length when adding beyond the current end. Deleting a late key must not silently
   destroy intentionally held clip duration; state the length policy in tests.
4. Rebuild/invalidate player track/limit/end-frame caches once per committed revision.
   Preserve the user's playhead/play/pause state during reevaluation; do not reset to frame 0
   because `LoadClip` currently does so as its public load behaviour.
5. All changes use E2. Keep bone-local FK keys distinct from model/object-space IK goals.
   Use kernel `ClipKeys.Goals`/`Solve` where their semantics match, not ad-hoc Euler keying.

**Tests:** first/last/only key; every ease mode; tangents round-trip; occupied-frame move;
undo; end-frame cache update; paused last-frame scrub; negative/NaN rejection; unlike track
kinds with the same target name remain distinct.

## E4 — Atomic save, save-as and recovery snapshots

**Prerequisites:** E1/E2; P1 for path handling. Do not ship autosave without this primitive.

1. Implement `DocumentSaveService` capturing an immutable revision on the owner thread;
   serialization/disk IO happens in a worker. Save `.effigy` with `StudioDocument.Write`,
   existing `.riganim` with `RigAnimWriter.Write(clip, originalJson)`.
2. Validate/reparse serialized output before replacement. Use a same-directory unique temp,
   complete write + flush, then atomic replace. On any failure preserve the old destination;
   clean up only this operation's temp. Inject a filesystem abstraction for failure tests.
3. Save-as writes a new destination and changes source identity only after success. Relocate
   relative references using P1, not by copying opaque strings into the new directory.
4. A save of revision N cannot mark revision N+1 clean. Keep unknown clip JSON fields/events/
   props intact. Unsupported imported-FBX authoring gets a clear save-capability result;
   it does not overwrite the source FBX or emit a fake `.effigy` file.
5. Recovery snapshots go to a documented application-owned directory, with source identity,
   schema/version, revision and timestamp. They never replace the user's main file on open.

**Tests:** corrupt input; unknown metadata sentinel survives; save-as relocation; write/flush/
rename failures; cancellation; edits during save; read-only directory; old target byte-identical
after failure; staged recovery can be parsed after simulated interruption.

## E5 — Parametric model edits and background rebuilds

**Prerequisites:** E1/E2/E4. Start with a deliberately small documented feature subset.

1. Implement undoable feature add/remove/reorder and parameter changes against the retained
   `PartStudio`. Call the real `MarkDirty(feature/index)` then `Rebuild`; don't mutate only
   generated `Bodies` and claim you edited the parametric model.
2. Validate fields/types with a whitelist or existing feature metadata, not unrestricted
   reflection accepting any property. Preserve feature IDs and downstream references.
3. Rebuild a detached working state off-thread. Keep last valid rendered geometry during
   work; report feature errors without corrupting the editable session or replacing it with empty geometry.
4. Coalesce repeated edits, tag jobs with revisions, discard stale results, and publish GPU
   buffers only on the GL thread. Respect bounded work/memory: don't clone the full studio
   on every mouse move or run competing full rebuilds for every keystroke.
5. Save/reopen via E4 and reproduce the final geometry/feature order. Test the first edit
   after `StudioCache` restore; it intentionally needs a fresh rebuild.

**Tests:** add → parameter edit → reorder → undo/redo; invalid downstream reference; failed
feature; stale async job; cached open then edit; save/reopen semantic + geometry equality.
**Do not claim:** general sculpt/paint/cloth/terrain authoring until separately ticketed and tested.
