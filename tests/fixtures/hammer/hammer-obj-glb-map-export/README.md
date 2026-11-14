# hammer-obj-glb-map-export fixtures

## sealed-room.json

A 256x256x128 sealed room, the acceptance case from the ticket: 6 box
brushes (floor, ceiling, 4 walls), each with ONE textured inner face and
`tools/toolsnodraw` on the other five faces, plus one power-2 displacement
dome (5x5 grid, centre raised 4 units) on the floor. It mirrors
`MapBakeExportTests.BuildSealedRoom` exactly; the
`sealed-room-fixture-matches-code` test bakes both and requires identical
counts.

### Format

- `brushes[]`: `{ "name", "faces": [{ "normal": [x,y,z], "distance": d, "faceMaterial": {...} }] }`.
  A face plane is the half-space Dot(normal, p) <= distance, normal outward.
  `faceMaterial` is the `FaceMaterial` JSON form (`{"material": name,
  "uv": {...}}`; `uv` optional, defaults to `FaceUv.Default`).
- `displacements[]`: `{ "name", "corners": [4x [x,y,z]] (CCW from outside),
  "power": 2|3|4, "offsets": [(2^power+1)^2 numbers],
  "alphas": [same count, 0..1], "materialA": {...}, "materialB": {...} }`.

### Expected bake (asserted in tests)

- Mesh faces: 38 (6 brush quads + 32 displacement triangles)
- Mesh vertices: 120 (24 brush + 96 displacement; face-split, no welding)
- Materials, first-appearance order: `concrete/floor001`,
  `plaster/ceiling001`, `brick/brickwall001`, `nature/grass001`
- Excluded nodraw faces: 30 (5 per box); `dirt001` absent (all alphas 0)
- Issues: none
- OBJ: `usemtl` lines for all 4 materials, `mtllib sealed-room.mtl`,
  round-trip via `ObjReader` gives 38 faces / 4 material slots
- GLB: valid `glTF` header (magic + version 2); mesh/material counts on
  the export result match the bake (see `MapBakeExport.GlbNote` — the
  kernel GLB exporter is currently a minimal stub and does not embed the
  mesh payload; the full writer is core/GlbExport.Write).
