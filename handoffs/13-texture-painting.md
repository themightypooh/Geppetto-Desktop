# Handoff: 3D Texture Painting in Viewport

## Goal
Paint directly onto a mesh's texture maps in the viewport, with a brush, layers, and undo.

## Prerequisites
- Lease on `SceneRenderer.cs`, `ViewportMesh.cs`, shaders, `Program.cs`.
- M1-M4 material/texture pipeline verified.
- E1-E2 retained document + undo.

## Scope
1. Render-to-texture: paint into an offscreen FBO texture bound as the material's albedo (and other channels).
2. Brush: radius, hardness, color, opacity; stamp-based (texture stamp) or procedural.
3. Layers: at least base + one paint layer, blend modes.
4. Undo: each stroke = one EditHistory transaction (E2).
5. Save: write painted textures to disk via DocumentSaveService (E4), update MaterialDescriptor references.

## Existing seams
- `MaterialDescriptor` / `TextureBinding` (M1).
- `ViewportMesh` UV attribute (M2) — paint in UV space.
- `EditHistory` (E2) — transaction grouping.
- `TexturePaintService.cs` — 2D CPU brush foundation.

## Required tests
- Stamp produces non-white pixels in expected region.
- Undo restores previous texture.
- Save round-trip preserves painted pixels.
- No GL readback per frame (performance rule).

## Done criteria
- Real brush stroke visible in viewport capture (V3 image check).
- Undo/redo works.
- Save persists.