# Handoff: Procedural Physics Animation (Euphoria-style) (scope)

## Goal
Procedural, physics-driven character animation (balance, reach, stumble) like Euphoria.

## Prerequisites
- Full cloth sim (15), advanced IK (23), constraints.
- Lease on `MarionettePlayer`, `PoseEditService`.

## Scope (minimal)
1. Balance controller: center-of-mass tracking, foot placement.
2. Reach: arm IK toward target with body lean.
3. Stumble/recover: when pushed, procedural recovery.
4. Muscle stiffness parameters.
5. Blend with authored animation (A3 clip blend).

## Existing seams
- `ClipBlendService` (A3).
- `IkTargetService`, `PoseEditService`.

## Required tests
- Balance maintains COM over support polygon.
- Reach touches target.
- Determinism.

## Done criteria
- Procedural reaction visible in viewport.