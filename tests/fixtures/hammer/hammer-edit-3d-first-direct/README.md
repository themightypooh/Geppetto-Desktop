# hammer-edit-3d-first-direct fixtures

JSON fixtures for `tests/DirectEdit3DTests.cs` (group `directedit3d`).
One file per brush, plus one ray file; a test loads them, edits/picks,
and checks the volume, validity, and hit classification.

Format (brushes):
```json
{
  "name": "box",
  "vertices": [[x, y, z], ...],
  "faces": [[vertex indices in outward-wound loop order], ...],
  "materials": ["default", ...],
  "expected": {"volume": 8, "valid": true, "invalid_faces": []}
}
```
Units are the kernel's inches, Z-up. Face order for both box fixtures is:
bottom, top, front(-Y), back(+Y), left(-X), right(+X) — so the top face is
index 1, which is the face the extrude/drag tests move. The top face loop
is [4, 5, 6, 7]; vertex 6 is the (+, +, +) corner.

| file | contents | expected validity |
|---|---|---|
| `box.json` | unit cube, side 2, centred on origin | valid, volume 8 |
| `box-64.json` | cube, side 64, centred on origin | valid, volume 262144, top face area 4096 |
| `pick-rays.json` | 5 rays against `box.json` + expected hits | top-down → face 1; vertex-6 → vertex 6; edge-top → edge (6, 7); miss → no hit; side → face 5 |

Acceptance numbers the tests assert from these fixtures:
- Extrude `box-64` top face by 32 → volume 393216 (delta 4096 × 32),
  10 faces (6 + 4 side quads), watertight, cap at z=64.
- Extrude inward by 16 → volume 196608, cap at z=16, still valid.
- Drag top face along its normal by 16 → volume 327680, still 6 faces.
- Drag top face in-plane by (16, 0, 0) → volume still 8, still valid.
- Shear (axis +Z, direction +X, factor 0.5, pivot origin) → volume 8,
  vertex 6 lands at (1.5, 1, 1).
- Uniform scale ×2 about the origin → volume 64.
