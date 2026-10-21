# Handoff: Full Cloth Sim Validation

## Goal
Validate and harden the existing `ClothSimService` (Verlet) for production garment drape.

## Prerequisites
- `ClothSimService.cs` exists (Verlet, pinned, gravity/wind, floor, obstacles, self-collision, tearing).
- V4 performance harness.

## Scope
1. Self-collision correctness: tune radius, avoid jitter, measure cost.
2. Garment attachment: pin vertices to body mesh, follow animated body.
3. Performance: profile on a 5k-vertex garment; report step time.
4. Determinism: same input → same output (for tests).
5. Integration with garment suite (shrinkwrap → cloth drape).

## Existing seams
- `ClothSimService.Create/Step/ToMesh`.
- `ShrinkwrapService` for initial fit.

## Required tests
- Determinism: identical steps produce identical positions.
- Floor constraint holds.
- Pinned vertices don't move.
- Performance: step time reported.

## Done criteria
- Garment drapes over a body without explosion.
- Step time documented.