# Handoff: Hair / Fur (scope)

## Goal
Hair and fur simulation/rendering.

## Prerequisites
- Particle system (17), skinning.
- Lease on `SceneRenderer`.

## Scope (minimal)
1. Hair strands as curves (particle-based).
2. Grooming: comb, cut, clump.
3. Physics: gravity, wind, collision with body.
4. Rendering: strand shading (Marschner), LOD.
5. Export: hair as curves or baked mesh.

## Existing seams
- Particles (17), geometry nodes instance-on-points (18).

## Required tests
- Hair follows gravity.
- Collision with body.
- Determinism.

## Done criteria
- Visible hair in viewport.