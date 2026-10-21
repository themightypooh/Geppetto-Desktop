# 4. Real transform and IK manipulation — G1 to G4

Coloured axes currently draw, but cannot be dragged. This brief separates testable math,
editing transactions and viewport integration. No UI panels or new toolbar design.

## Read first

`OrbitCamera.ScreenRay`, `MeshPicker`, `SkeletonOverlay`, `MarionettePlayer.WorldPose`,
`MarionettePlayer.ToGlMatrix`, kernel `Xform`, `RigPose`, `TwoBoneIk`, `ChainIk`, `ClipKeys`.
The current picker uses logical window pixels for input, not framebuffer pixels.

## G1 — Ray/handle math and snapping, no window input

**Prerequisites:** none. **Own:** new pure math files + separate CPU tests. Good bounded ticket.

1. Define axis/plane/ring handles in an explicit local or world coordinate frame. Give
   them typed IDs so selecting X translation is not confused with X rotation.
2. Implement hit tests and closest-point/ray-plane calculations. Parallel or near-parallel
   configurations return “no stable solution”; never divide by tiny values or jump arbitrarily.
3. Derive world handle size from the camera projection/depth to keep roughly constant pixel
   size. Do not use a fixed 4-unit axis as the hit-size definition at every zoom level.
4. Translation snapping operates in the selected reference frame, relative to the drag's
   starting state. Rotation snapping uses explicit radians internally/degrees at API boundary.
   Zero/disabled snap leaves motion continuous. Reject nonpositive/nonfinite step sizes.
5. Rotation uses a stable signed angle and unwraps across ±π. Negative scale is not an
   accidental side effect of dragging through the origin; start with positive scale only.

**Tests:** every axis; hit/miss; behind camera; orthographic/perspective reference projections;
parallel ray; camera looking straight down an axis; near/far zoom; ±π crossing; positive and
negative translation snapping; invalid snap; finite output for all degenerate fixtures.
**Done:** pure math API and tests. No claim of a usable gizmo yet.

## G2 — Pose transforms with commit/cancel history

**Prerequisites:** G1, E1/E2. **Own:** proposed `PoseEditService` / `DragSession`, not input hooks.

1. Capture the starting local/world poses and selected IDs at drag start. Derive every
   preview from that start state, not from accumulating deltas onto yesterday's preview.
2. World translation/rotation becomes local via `inverse(parentWorld) * desiredWorld`.
   Rotate about the selected bone's head, not world origin. Respect nonidentity parents.
3. Posing never edits `Skeleton.Bones[b].Local` (the bind pose) or recomputes inverse binds.
   Keep transient pose overrides separate from the source skeleton.
4. Pause playback for the edit session, with a documented restore/cancel policy. Recompute
   descendants and the GPU palette from the edited local pose. Keep picker/shadows consistent.
5. Commit one E2 command; cancel restores the exact previous pose and transport state.
   No-op does not dirty the document. A preview does not auto-save or silently add keys.

**Tests:** rotated/scaled parent; root edit; child follows; bind/inverse-bind unchanged;
100 updates one command; cancel; undo/redo; deleted selection; invalid transform leaves state intact.

## G3 — IK target manipulation, not direct hand-bone translation

**Prerequisites:** G2 and A1; use the existing kernel IK path before any optimization work.

1. Resolve the selected end bone's actual live constraint and goal-space anchor. Do not
   assume every hand uses a two-bone chain with a world-up pole.
2. Translate a model/world drag target into the goal's declared Model/World/Object space
   before solving/keying. Do not translate the hand bone's local position to fake IK.
3. Use the kernel's limits, chain solver, weight, goal rotation, pole and stretch settings.
   Out-of-reach targets produce the kernel's clamped result plus a measurable miss, not NaNs.
4. A tentative solve must not mutate bind data or the stored constraint on every update.
   Commit/cancel through E2; render and pick the solved preview from the same pose.

**Tests:** two-bone/FABRIK fixtures; bent pole direction; zero IK weight; limit; unreachable
goal; object moving under an anchored target; cancel/undo; missing constraint is explicit.
**Done:** a fake-ray/drag harness moves a target and verifies the actual end-effector result.

## G4 — Shared viewport draw/input integration and optional keying

**Prerequisites:** G1–G3, E3 for keying. **Coordinator/shared-file ticket** for `Program.cs`,
`SkeletonOverlay`, input dispatch and shared draw seams. No visible manual testing without approval.

1. Draw the same typed handles the math tests hit. Hover/active state must map to those IDs.
   Do not add a decorative handle whose geometry differs from its selection shape.
2. Dispatch handle drag before orbit/body selection; an active drag must not orbit the camera.
   Use logical coordinates/viewport rectangles, independent of DPI/capture framebuffer size.
3. Capture the pointer for the drag and release on commit, escape/cancel, focus loss, source
   reload or window close. Do not leave the cursor hidden/captured after an error.
4. On commit, apply explicit keying policy: unkeyed pose override unless auto-key was
   deliberately enabled; FK writes local bone keys, live IK writes goal/weight keys through E3.
5. Provide a headless event-sequence test seam: press ray → move rays → release/cancel.
   Never rely on desktop clicks/screenshots as the only proof of working interaction.

**Tests:** handles drawn/picked agree; drag doesn't orbit; escape/focus-loss cancel; DPI ratio;
one undo entry; undo restores palette; auto-key off/on; headless captured handle state.
**Done:** real routing works through the editing backend. An axis arrowhead alone is not done.
