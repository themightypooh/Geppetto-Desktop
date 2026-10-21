# Handoff: Auto-Weight (Bone Heat) (scope)

## Goal
Automatic skin weight assignment from bones to mesh (bone heat algorithm).

## Prerequisites
- Lease on `MarionettePlayer`, skinning pipeline.
- Kernel `AutoWeight` exists (Effigy.Tests/AutoWeightTests.cs).

## Scope
1. Compute weights from bone proximity + raycast (bone heat).
2. Handle multiple bones, normalization.
3. Preview weights (color gradient, like weight painting).
4. Refine: smooth, prune small weights.
5. Export weights via FBX/DMX.

## Existing seams
- Kernel `AutoWeight` (exists).
- `SkinWeights` (kernel).

## Required tests
- Bone heat assigns nearest bone dominant weight.
- Normalization sums to 1.
- Export includes weights.

## Done criteria
- Auto-weighted mesh rigs without manual painting.