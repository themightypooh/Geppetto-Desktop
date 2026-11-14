# hammer-face-material-uv-model fixtures

- `box-faces.json` — the six faces of a brush box as `FaceMaterial` JSON
  (`{"material": ..., "uv": {...}}` per face). Top and walls are textured, the
  unseen bottom is `tools/toolsnodraw`. The `FaceMaterialTests` "box fixture"
  check loads this file and asserts 6 faces, 5 baked, bottom nodraw, and the
  east wall's 90-degree rotation / lightmap 8 / smoothing group 2 survive.
- No MapBrush document lives here: MapBrush (hammer-brush-core-plane-based-brushes)
  had not landed when this ticket was written, so the fixture is the per-face
  record its faces will carry, not a whole brush file.
