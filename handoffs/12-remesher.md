# Handoff: Real Quad Remesher (field-aligned)

## Goal
Replace the triangle-pairing foundation in `RetopoService.cs` with a real quad-dominant remesher that can take 300k+ tri AI output and produce clean, rig-ready quads with proper edge flow.

## Prerequisites
- Kernel ticket for any new mesh-editing primitives (half-edge or derived adjacency).
- Lease on `ViewportMesh.cs`, `SceneRenderer.cs` if viewport integration needed.
- V4 performance harness for perf validation.

## Scope
1. Target-count reduction: collapse edges like `Decimate` but prefer quad-preserving collapses (remove a vertex, merge two tris into a quad where possible).
2. Field-aligned edge flow: align edges to principal curvature directions; place poles at caps/extremities.
3. Feature preservation: UV seams, material boundaries, hard edges become constraints (never collapse across them).
4. Output quality: report quad ratio, max angle deviation, worst geometric error, closed-manifold validation.
5. Perf: must run on 300k-tri inputs within a rebuild budget (measure with `RemeshGen`-style harness).

## Existing seams
- `RetopoService.cs` (foundation) — triangle pairing, decimation-to-target, boundary/manifold tracking.
- Kernel `Decimate` (proven on 450k-tri Meshy export via `RemeshGen.cs`).
- `MeshValidator.Validate` for closed-manifold checks.

## Required tests
- Sphere: quad ratio > 0.9, closed manifold.
- Cylinder: edge loops around circumference.
- Meshy-style 300k-tri input: reaches target, no crash, perf reported.
- Seam preservation: UV seam edges not collapsed.
- Material boundary preservation.

## Done criteria
- Used by a fixture harness; not a stub.
- Perf numbers reported (cold rebuild time).
- No second lighting/shader implementation.