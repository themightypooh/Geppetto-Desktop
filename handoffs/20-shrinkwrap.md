# Handoff: Shrinkwrap (deep scope)

## Goal
Production shrinkwrap modifier for garment fit and retopo projection.

## Prerequisites
- `ShrinkwrapService.cs` (vertex + surface projection foundations).
- Lease on `DocumentSession`, `ViewportMesh`.

## Scope
1. Modes: nearest surface point, normal projection, target normal.
2. Offset along normal.
3. Vertex group / mask support.
4. Live modifier in stack (19-modifiers).
5. Quality: handle open meshes, holes, multiple targets.

## Existing seams
- `ShrinkwrapService.Wrap` / `WrapToSurface`.
- `ClosestPointOnTriangle`.

## Required tests
- Surface projection lands on triangle.
- Offset moves along normal.
- Mask limits affected vertices.
- Determinism.

## Done criteria
- Garment fits body via shrinkwrap modifier.