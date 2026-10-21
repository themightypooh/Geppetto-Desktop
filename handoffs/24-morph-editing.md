# Handoff: Morph Target Editing (scope)

## Goal
Interactive morph target (blendshape) editing in the viewport.

## Prerequisites
- A2 morph evaluation verified.
- Lease on `MorphEvaluator.cs`, `ViewportMesh.cs`.

## Scope
1. Select morph target, sculpt deltas on mesh.
2. Visualize morph influence (slider 0-1).
3. Combine multiple morphs.
4. Export morphs to FBX/DMX (I2).
5. Undo per stroke (E2).

## Existing seams
- `MorphEvaluator` (A2).
- `PolypaintService`-style brush for deltas.

## Required tests
- Morph delta applied to vertices.
- Combined influence sums.
- Undo restores.
- Export includes morph.

## Done criteria
- Morph visible in viewport capture.