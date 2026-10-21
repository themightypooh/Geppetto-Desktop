# Handoff: Particle System (deep scope)

## Goal
Production particle system for effects, hair, and instancing.

## Prerequisites
- Lease on `SceneRenderer.cs`, `Program.cs`, `DocumentSession`.
- V4 performance harness.

## Scope
1. Emitters: point, area, mesh surface, volume.
2. Simulation: forces (gravity, wind, turbulence, vortex), collision (mesh + ground), substeps.
3. Particle types: billboard, mesh instance, hair strand.
4. Rendering: sorted alpha, motion blur vectors, LOD.
5. Caching/baking: bake to animation for export.
6. Hair: guide curves, clumping, grooming (basic).
7. Integration: particles can drive geometry (instance on points).

## Existing seams
- `SceneRenderer.Draw` for billboard/instance pass.
- `OrbitCamera` for view-facing.
- Geometry nodes "instance on points" (18).

## Required tests
- 10k particles @ 60fps step time reported.
- Collision prevents penetration.
- Determinism.
- Baked animation exports via FbxExportAdapter.

## Done criteria
- Visible effect in viewport capture.
- Exportable baked animation.