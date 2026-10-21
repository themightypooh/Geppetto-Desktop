# Handoff: Weight Painting (scope)

## Goal
Interactive weight painting for skinning in the viewport.

## Prerequisites
- Lease on `SceneRenderer.cs`, `ViewportMesh.cs`, input handling.
- `MarionettePlayer.GpuBones`, skinning pipeline.

## Scope (minimal v1)
1. Brush: paint weights onto vertices for selected bone.
2. Visualization: color gradient on mesh (blue=0, red=1).
3. Normalize: auto-normalize weights to sum 1.
4. Tools: brush, blur, smooth, fill, gradient.
5. Undo per stroke (E2).

## Existing seams
- `SkinWeights` (kernel).
- `EditHistory`.

## Required tests
- Paint sets weight on vertex.
- Normalize sums to 1.
- Undo restores.

## Done criteria
- Visible weight gradient in viewport capture.