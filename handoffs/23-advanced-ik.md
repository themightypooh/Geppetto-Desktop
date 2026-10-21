# Handoff: Advanced IK (scope)

## Goal
Extend IK beyond current G1-G4: foot planting, look-at, constraints, multi-target.

## Prerequisites
- G1-G4 verified (gizmo math, pose edit, IK targets, viewport handles).
- Lease on `IkTargetService.cs`, `ViewportHandleService.cs`.

## Scope
1. Foot planting: raycast to ground, adjust leg IK to plant foot.
2. Look-at: head/eye tracking target.
3. Constraints: angle limits, pole vectors, twist.
4. Multi-target: blend multiple IK goals.
5. Chain IK: full-body (FABRIK) beyond single limb.

## Existing seams
- `IkTargetService`, `PoseEditService`, `DragSession`.
- `MarionettePlayer` live IK.

## Required tests
- Foot plants on ground plane.
- Look-at rotates head toward target.
- Constraint clamps angle.
- Undo integration.

## Done criteria
- Interactive foot planting in viewport.