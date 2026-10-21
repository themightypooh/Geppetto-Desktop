# 11. Foundation slices delivered this session

These are CPU-only foundations with tests, not full features. Each is `implemented-unverified`
pending coordinator certification. They live in the desktop project (no kernel edits).

## Delivered

- `RetopoService.cs` — triangle-pairing quadifier (seam/material aware, deterministic).
  Tests: `RetopoTests` (8). Follow-up: real field-aligned remesher (R2).
- `KnifeService.cs` — plane classification/crossing count. Tests: `KnifeTests` (4).
  Follow-up: actual face splitting along arbitrary planes/polylines.
- `ShrinkwrapService.cs` — vertex-nearest + surface closest-point projection.
  Tests: `ShrinkwrapTests` (5). Follow-up: full garment drape integration.
- `SdfShapes.cs` — box/cylinder/torus mesh generators.
- `DataTransferService.cs` — skin-weight transfer by nearest vertex.
  Tests: `DataTransferTests` (5). Follow-up: per-corner UV transfer.
- `PolypaintService.cs` — vertex-color brush. Tests: `PolypaintTests` (6).
- `UvEditService.cs` — UV Laplacian relax with pin. Tests: `UvEditTests` (4).
- `DyntopoService.cs` — edge subdivision within brush. Tests: `DyntopoTests` (5).
- `ClothSimService.cs` — Verlet cloth sim (already present). Tests: `ClothSimTests` (6).

## Still needed (engine/GPU/physics)

- Real quad remesher (field-aligned, target count, perf on 300k+ tris).
- Freehand/polyline knife with face splitting.
- 3D texture painting in viewport (GPU).
- Interactive UV editing beyond relax (seams, pin UI).
- Full cloth sim validation (self-collision, attachment).
- Polypaint without UVs is done (vertex colors); texture painting is not.

## Sample models

/home/poo/Desktop/sample-model/: box.obj, sphere.obj, torus.obj, ps4_controller.obj.
EOF