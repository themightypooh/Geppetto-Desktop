# Handoff: IK Variants (spline, two-bone, stretchy)

## Goal
Additional IK solver types beyond basic limb IK.

## Prerequisites
- G1-G4 verified (IK targets, viewport handles).
- Lease on `IkTargetService.cs`, `MarionettePlayer`.

## Scope
1. Two-bone IK: standard arm/leg solver with pole vector.
2. Spline IK: chain follows a spline curve (spine, tail, tentacle).
3. Stretchy IK: bones stretch to reach target when out of range.
4. CCD (Cyclic Coordinate Descent): fast heuristic, per-joint.
5. FABRIK: forward/backward reaching, multi-end-effector, closed loops.
6. Jacobian transpose / pseudoinverse / DLS (Levenberg-Marquardt): smooth, singularity-stable.
7. Analytical IK for simple chains (2-3 DOF).
8. Constraint integration: angle limits, pole vectors, twist.
9. Integration with existing IK targets and undo.

## Existing seams
- `IkTargetService`, `PoseEditService`.
- `MarionettePlayer` live IK.

## Required tests
- Two-bone reaches target within tolerance.
- Spline chain follows curve.
- Stretchy extends beyond rest length.
- Undo integration.

## Done criteria
- Interactive in viewport.