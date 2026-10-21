# Handoff: Interactive UV Editing

## Goal
Interactive seams, pin, relax, and unwrap in the viewport.

## Prerequisites
- `UvEditService.cs` (relax foundation) and `UvSeamService.cs` (seam marking) exist.
- Lease on `SceneRenderer.cs`, `ViewportMesh.cs`, input handling in `Program.cs`.
- E2 undo.

## Scope
1. Seam marking: click edges to mark/unmark seams (uses `UvSeamService`).
2. Pin: pin vertices in UV space so relax keeps them fixed.
3. Relax: live relax with strength slider (uses `UvEditService.Relax`).
4. Unwrap: basic LSCM/angle-based unwrap for selected faces.
5. Visualization: draw UV islands, seams, pins in a 2D UV viewport or overlay.

## Existing seams
- `UvEditService.Relax` — Laplacian relax with pin support.
- `UvSeamService.MarkSeams` — vertex duplication along seams.
- `Face.UVs` per-corner (kernel).

## Required tests
- Seam marking duplicates vertices (existing test).
- Relax respects pins (existing test).
- Unwrap produces non-overlapping islands (new).
- Undo restores (E2).

## Done criteria
- Interactive: click to mark seam, see UV update.
- No GL readback per frame.