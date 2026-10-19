# 3. Animation completeness — A1 to A7

This category contains several independent features. Do not assign “finish animation.”
Read RULES.md and CONTRACTS.md. No UI and no automatic startup animation generation.

## Source of truth and existing behaviour

- Desktop: `MarionettePlayer.cs`, `DocumentLoader.cs`, `ViewportMesh.cs`, `MeshPicker.cs`.
- Kernel: `Rig/RigAnimFile.cs` (`SampleLocal`, `SolveIk`, `ModelSpace`, `MorphWeightsAt`),
  `RigLimit.cs`, `ChainIk.cs`, `ClipLayer.cs`, `RootMotion.cs`, `MorphDriver.cs`,
  `PoseSpaceCorrective.cs`, `ChainSpring.cs`, `MotionLint.cs`, `TakeAudit.cs`.
- `.ctrlrig` is loaded by `MarionettePlayer.LoadClip(string)` when `RigAsset` names one.
- Bone keys and limits have an optimized desktop path. Live IK / in-place root-motion
  evaluation uses the kernel sampler. Preserve fractional-frame correctness.
- The source `RigTrackKind` ordinals are a file-format contract. Never reorder/insert enum
  values or reinterpret Part/Camera/Morph tracks as bones to make them “work.”

## A1 — Capability validation before playback

**Prerequisites:** none beyond the baseline. **Own:** capability validator + focused tests;
player integration under lease. Suitable as a small, bounded first ticket.

1. Inspect tracks AND clip-level fields: keyed targets, morph events, drivers, root-motion
   settings, placements and referenced control rig. Current track-only rejection is not a
   complete capability audit. Keep supported root-motion/IK cases supported.
2. Return a structured supported/unsupported/invalid report with field/track/target names.
   Unknown bone names must not silently make a completely incompatible clip look successful.
   Define whether partial target matches are warnings or hard errors, and test that policy.
3. Validate into a temporary evaluator first. A failed load must not overwrite the previous
   valid `Clip`, cached tracks, palette, time or playing state.
4. Resolve rig paths via P1 when available. Missing/malformed rigs get a useful error naming
   the requested reference; do not silently drop constraints to allow playback.

**Tests:** unsupported track; morph event without a morph track; all targets missing; partial
match; missing rig; unknown future enum; old clip remains playable after rejected replacement.
**Done:** validator is called before state changes, not merely added as an unused utility.

## A2 — Morph keys, events and pose-driven correctives

**Prerequisites:** A1, E1; material/tangent integration must coordinate the shared layout.
**Difficulty:** experienced worker. Split extraction and GPU integration if necessary.

1. Retain `PolyMesh.Morphs` / `MeshMorph` from import/build. Preserve original vertex →
   duplicated render-corner mapping so every UV/hard-normal duplicate receives the same delta.
   Never assume GPU vertex index equals source mesh vertex index.
2. Evaluate precedence using the real `RigAnimFile.MorphWeightsAt` / `MorphDriver` behaviour:
   driven values, keyed curves, then active events. Correctives use the constrained local pose.
3. Apply shape deltas in bind/model space **before skinning**. Handle normals/tangents with
   an explicit tested policy; changed positions with stale shading are not completion.
4. Define supported active-target counts and memory limits. Reject/report overflow rather
   than dropping targets. Preallocate per-frame weight buffers/lookups where possible.
5. Use the same deformed positions for shadow drawing and triangle picking. Keep bind/base
   geometry immutable; switching clips or zeroing all weights must restore the base exactly.

**Tests:** weights 0/0.5/1; two targets; UV duplicates; keyed/event/driver precedence; missing
morph; corrective at a bent joint; skin-after-morph order; animated picker/shadow agreement;
reset/switch-clip; memory/overflow. Use synthetic fixtures, not Survey-only counts.
**Done:** headless GPU image and exact numeric deformation assertions both pass.

## A3 — Overlay/additive blending and crossfades

**Prerequisites:** E1, A1. A2 is needed only for morph-layer support; don't pretend it's present.

1. Implement a CPU blend/evaluation service with explicit source clips, times, weights,
   masks, layer mode and reference pose. Keep it separate from transport (play/pause).
2. Inspect/reuse `ClipLayer.Compose` for scale/rotation/additive semantics; additive is a
   delta from a reference pose, not quaternion component addition or Euler addition.
3. State evaluation order for base keys, layers, limits and IK. Preserve existing kernel
   behaviour where equivalent; do not blend already-solved goals as unrelated bone rotations.
4. Weight 0 must be exactly base; weight 1 exactly target for overlay; renormalize rotations.
   Support missing tracks as the documented reference/bind pose, not identity translation.
5. Avoid rebuilding parsed clips or dictionaries per frame. Changing layers must not alter
   the saved clips unless an explicit bake command is requested.

**Tests:** weights 0/1/mid; antipodal quaternions; scale additive identity; masks; unkeyed bones;
looping vs one-shot layer; different frame rates; crossfade duration 0; allocation measurement.
**Done:** deterministic API + actual player integration with declared supported channel types.

## A4 — Allocation-free live IK without semantic drift

**Prerequisites:** A1 and kernel maintainer approval. **Expert/kernel ticket, not a quick fix.**

1. Measure current live IK allocations/time on fixed two-bone, CCD/FABRIK and spline fixtures.
2. Propose a reusable-buffer kernel sampling/solver API. First inventory allocations in
   `SampleLocal`, `SolveIk`, goal collection, model/local conversions and chain solvers.
   Do not just replace the outer pose array and claim the entire path allocates zero.
3. Preserve solver order, spaces, keyed IK weight, goal rotation, limits, stretch and
   in-place root-motion semantics. Keep the old sampler as the numeric oracle.
4. All callers must receive consistent output. Keep an explicit fallback for unsupported
   fast-path cases instead of skipping their solver. Do not bake only integer frames as a
   substitute for live fractional-frame evaluation.
5. Kernel changes require mirror synchronization and full kernel regression checks under
   their own authorized scope. Desktop cannot reach kernel-internal methods by moving code
   into a fake namespace or recompiling kernel sources into the app again.

**Tests:** old/new every local and world transform at integer/fractional/random-seeded frames;
reach boundary; zero weight; stacked constraints; limits; root motion; warmed allocation counter.
**Done:** measured zero allocation on every advertised fast path and documented fallbacks.

## A5 — Timeline marks/events with deterministic crossing rules

**Prerequisites:** E1, A1. Read existing file schema in `Code/RigControl` first; do not invent
event field names. Preserve unrecognized payloads through E4 even if not executable.

1. Add a CPU event dispatcher returning typed notifications. Do not automatically play
   audio, spawn content or execute arbitrary scripts while opening/importing a file.
2. Dispatch crossings over `(previousFrame, currentFrame]`, with an explicit start-frame
   policy. Handle multiple loop wraps in one update without losing/duplicating marks.
3. Seeking/scrubbing and ordinary loading emit no play events by default. Explicit replay
   may request them. Pause/resume must not refire events at an unchanged time.
4. Define same-frame ordering, one-shot stop, backward seek and bounded huge-delta policy.
5. Keep side effects behind consumer callbacks; headless sims record notifications only.

**Tests:** exact boundary; two events same frame; skipped frames; multiple wraps; first/last
frame; pause; seek backwards; clip replacement; exceptions in a consumer do not corrupt transport.

## A6 — Part and camera track evaluation

**Prerequisites:** E1, A1. Separate from UI camera controls and from skeleton FK tracks.

1. Use existing placement/qualified-name semantics in `RigAnimFile` for objects, parts and
   props. Stable target IDs/names must distinguish identical part names on different objects.
2. Produce evaluated part transforms and camera state as CPU output. Do not overwrite the
   user's orbit camera merely because a loaded clip contains camera keys; shot playback is opt-in.
3. Honour follow-bone vs parent-part composition and avoid dependency cycles. Object-space
   IK goals must see the same placement as the rendered part at the sampled frame.
4. Integrate part transforms in drawing, bounds/picking, shadows and export where supported.
   Preserve the clip's camera FOV encoding rather than treating its scale channel as mesh scale.

**Tests:** nested part; follow-bone; duplicate part names; missing target/cycle; object-space IK;
camera FOV/pose interpolation; opening a camera track does not change the orbit camera.

## A7 — Remaining constraint/secondary-motion parity inventory

**Prerequisites:** A1. This ticket's deliverable is an audited matrix + one separately selected
small binding, NOT permission to port every remaining animation feature in one pass.

1. Inventory `.ctrlrig` behaviours in `Code/RigControl` versus actual kernel support:
   copy/look-at constraints, secondary chains/collisions, drivers and root-motion options.
2. For each, name existing reader/evaluator/tests or state the missing kernel API precisely.
   Names in old handoff notes are not evidence that a feature is engine-free today.
3. Coordinator selects one already-supported kernel behaviour to wire with a numeric fixture.
   Missing kernel behaviours get separate approved tickets; no engine-reference shortcut.
4. Stateful secondary simulation must reset deterministically on load/seek and use fixed
   stepping or an explicit bake. Never advance springs according to render FPS accidentally.

**Done:** checked capability matrix, tests for the selected binding, and explicit follow-up
tickets. Do not mark all animation parity complete from this inventory.
