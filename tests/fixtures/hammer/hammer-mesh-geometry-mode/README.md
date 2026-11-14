# Fixtures: hammer-mesh-geometry-mode

Mesh-based geometry mode (Hammer Desktop P3). Board: `hammer-mesh-geometry-mode`.
Tests: `tests/MeshModeTests.cs`, group `meshmode`.

## Format (parsed by `MeshMode.ParseMesh`)

One mesh per file, kernel inches, Z-up, faces wound outward (counter-clockwise
seen from outside, so the Newell normal points out of the solid):

```
# comment
v x y z
f i j k ... [materialSlot]
```

Indices are 0-based. A face line needs at least 3 corners; a trailing integer
after the corners is the material slot (default 0).

## Files

- `box.mesh` — 2x3x4 box, x -1..1, y -1.5..1.5, z -2..2. 8 verts, 6 faces,
  volume 24. Faces are [bottom, top, front, back, left, right]; the top-front
  edge is vertices (4,5).
- `lshape.mesh` — L-prism, footprint (0,0) (4,0) (4,2) (2,2) (2,4) (0,4),
  z 0..2. 12 verts, 8 faces, volume 24. Concave: the reflex corners (2,2,0)
  and (2,2,2) are not hull vertices. Exact convex split is two boxes,
  16 + 8 = 24.
- `twobox.mesh` — two stacked 2x3x4 boxes, same footprint. Lower z -2..2
  (faces 0-5, top is face 1), upper z 4..8 (faces 6-11, bottom is face 6).
  Bridging faces 1 and 6 makes one solid of volume 24 + 24 + tube 12 = 60.

## Expected numbers (pinned by the tests)

- Extrude box top face by +1: volume 30, 10 faces. By -1: volume 18.
- Bevel box edge (4,5) width 0.5, 1 segment: volume 23.75, 7 faces.
  2 segments: volume between 23.75 and 24, 8 faces.
- Inset box top face by 0.5: volume unchanged 24, 10 faces, inset face area ~3.13.
- Export box to brushes: 1 brush, exact, volume 24.
- Export L to brushes: 2 brushes, exact, total volume 24.
- Export L with `MaxBrushes = 1`: 1 brush, NOT exact, excess ratio 1/3
  (single hull volume 32 vs mesh 24), named in the conversion report.

## Running

Named group only, once the integrator adds the Register line to
`tests/TestRegistry.cs` (that file is coordinator-owned; this ticket does not
touch it):

```
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter meshmode
```

No UI is involved: mesh mode is backend / data-model / API only, and the
`MeshModeFeature` parametric feature is backend-only too.
