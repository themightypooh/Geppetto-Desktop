# Handoff: Geometry Nodes / Procedural (deep scope)

## Goal
Full node-based procedural geometry (Blender geometry nodes parity, scoped).

## Prerequisites
- Kernel mesh primitives and transforms.
- Lease on `DocumentSession`, `ViewportMesh`, `SceneRenderer`.

## Scope
1. Node graph: typed sockets, evaluation order, caching/memoization.
2. Primitives: box, sphere, cylinder, torus, plane, grid, ico sphere, monkey.
3. Mesh ops: transform, join, separate, subdivide, bevel, extrude, inset.
4. Attribute ops: set position, normal, color, UV; map range, math.
5. Instance on points: with rotation/scale from attributes.
6. Curve nodes: spline, resample, curve to mesh.
7. Volume nodes: basic (mesh to volume, volume to mesh) — optional.
8. Animation: node params can be keyed via clip system (A-series).

## Existing seams
- `SdfShapes.cs` for primitives.
- `DesktopProject` for persistence.
- `ClipEditService` for keying.

## Required tests
- Complex graph (primitive → subdivide → instance) produces expected counts.
- Attribute-driven instance rotation.
- Deterministic.
- Perf on 100k-instance graphs.

## Done criteria
- JSON graph loads, edits, renders.
- Keyed params animate.