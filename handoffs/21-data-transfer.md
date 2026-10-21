# Handoff: Data Transfer (deep scope)

## Goal
Transfer attributes (weights, UVs, colors, normals) between mesh versions.

## Prerequisites
- `DataTransferService.cs` (skin-weight by nearest vertex).
- Lease on `DocumentSession`.

## Scope
1. Channels: skin weights, UVs (per-corner), vertex colors, normals, custom attributes.
2. Methods: nearest vertex, nearest face, raycast (project along normal).
3. Topology mapping: handle added/removed vertices.
4. Mixed: transfer from multiple sources with weights.
5. Validation: report unmatched vertices.

## Existing seams
- `DataTransferService.Transfer`.
- `MeshPicker` for raycast.

## Required tests
- Weights transfer by nearest vertex (done).
- UV corner transfer.
- Raycast method lands on correct face.
- Unmatched vertex reported.

## Done criteria
- Retopo'd mesh inherits original weights/UVs.