# hammer-brush-vertex-edit-invalid-face fixtures

JSON brush fixtures for `tests/MapVertexEditTests.cs` (group `mapvertexedit`).
One file per brush; a test loads it, edits it, and checks the per-face validity.

Format:
```json
{
  "name": "box",
  "vertices": [[x, y, z], ...],
  "faces": [[vertex indices in outward-wound loop order], ...],
  "materials": ["default", ...],
  "expected": {"volume": 8, "valid": true, "invalid_faces": [], "defects": ["non-planar"]}
}
```
Units are the kernel's inches, Z-up. Face order for `box.json` is:
bottom, top, front(-Y), back(+Y), left(-X), right(+X) — so the top face is
index 1, which is the face the twist tests flag.

| file | contents | expected validity |
|---|---|---|
| `box.json` | unit cube, side 2, centred on origin | valid, volume 8 |
| `pyramid.json` | square pyramid, base 2x2 on z=0, apex (0,0,2) | valid, volume 8/3 |
| `twisted-box.json` | box with top vertex 6 raised to z=2 | INVALID: face 1 non-planar only |
| `l-prism.json` | L-section prism (concave top/bottom faces) | INVALID: faces 0,1 non-convex only |

The twisted and L-prism fixtures are the "never silently repair" cases: the
brushes load exactly as filed, and `MapVertexEdit.Validate` must flag the
named faces rather than fixing them.
