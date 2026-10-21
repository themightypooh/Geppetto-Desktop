# Handoff: Curved Surface IK (scope)

## Goal
IK where targets and chains live on curved surfaces (geodesic distances, surface-following).

## Prerequisites
- IK variants (26).
- Geometry: surface sampling, geodesic distance.

## Scope (minimal)
1. Surface-constrained IK: end effector snaps to mesh surface.
2. Geodesic distance for chain placement on surface.
3. Use case: characters walking on terrain, tentacles on surfaces.

## Existing seams
- `IkTargetService`, `MeshPicker`.

## Required tests
- Target on surface stays on surface.
- Chain follows surface normal.

## Done criteria
- Interactive surface IK.