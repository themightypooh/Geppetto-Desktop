# hammer-displacement-sculpt-brush-modes fixtures

Backend only, no UI. Both fixtures are loaded by `tests/DisplacementSculptTests.cs`
(the tests fail if the files are missing or the numbers drift).

## Fixtures

- `dab-centre.json` — power-3 grid on a 64x64 face, one Raise dab (radius 20,
  strength 2, Smooth falloff) at the grid centre. Expected: centre +2.0,
  one step out +1.296, two steps out +0.208, outside the radius 0.
- `two-panels-sew.json` — two adjacent power-2 grids sharing the x=32 edge.
  A Raise dab on panel A near the shared edge must leave the shared edge
  equal on both panels after auto-sew (mean of raised and unraised: centre
  1.111, corners 0), with 5 shared vertices matched.

## Dependency status (2026-11-13)

`Displacement` (hammer-displacement-data-model, muse-3-desktop-hammer-08) HAS
landed (`kernel/Effigy/Map/Displacement.cs`: `Create(MapBrushFace, power)`,
`GridSize`, `Offsets`, `Alphas`, `Sew` returning bool, `Subdivide`, `ToMesh`,
`Validate`). The sculpt engine binds to it through `DisplacementAdapter`
(`kernel/Effigy/Map/DisplacementSculpt.cs`), which maps the landed names onto
the `IDisplacementSurface` seam (`GridSize` → `Side`, `Alphas` → `Alpha`,
`Sew` bool → matched-vertex count). Three checks in
`tests/DisplacementSculptTests.cs` drive the real `Displacement` through the
adapter; the rest run against the faithful in-test stand-in `TestDisplacement`
(same grid semantics, no brush face needed). No competing displacement type
is invented here.

## What the sculpt engine guarantees

- Raise/Lower along the face normal or X/Y/Z (X/Y/Z contribute only their
  component along the face normal), Smooth (snapshot-based neighbour
  average), Flatten (toward the height under the dab centre), Paint Alpha
  (blend toward a 0..1 target, never outside 0..1).
- Radius, falloff (`Brush.Falloff`), stamp (`BrushStroke.Alpha`), and
  mirror/radial symmetry (`BrushStroke.SymmetryCopies`) are all reused from
  `kernel/Effigy/Brush.cs`, which is untouched.
- Auto-sew runs after every dab on every registered neighbour, so a shared
  edge can never disagree after a stroke.
- `BrushKind` values with no displacement meaning (Grab, Pinch, Twist, ...)
  throw instead of corrupting the grid; Paint Alpha has no `BrushKind` and
  is selected explicitly as a mode.
