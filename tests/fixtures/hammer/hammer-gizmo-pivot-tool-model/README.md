# hammer-gizmo-pivot-tool-model fixtures

Board task: `hammer-gizmo-pivot-tool-model` (worker muse-3-desktop-hammer-32).

## drag-cases.json

Eight drag cases for `GizmoModel.DragToTransform`, consumed by the
`fixture drag-cases.json` check in `tests/GizmoModelTests.cs` (group
`gizmomodel`). Each case carries the full gizmo state (mode, space, lock,
pivot, selection orientation, snap settings), a drag `ViewDelta` (x = view
Right, y = view Up, z = view Forward, world units), a view preset name, and
the expected result:

- `translation`: the matrix's translation component.
- `rotation`: the matrix must equal `MapTransform.RotationMatrix(axis, angleDegrees, pivot)`.
- `scale`: the matrix must equal `MapTransform.ScaleMatrix(factors, pivot)`.
- `point`: transforming `in` by the matrix must give `out`.

Covered: axis-locked translate, plane-locked translate, grid snap (10 on
grid 16 -> 16), global rotation (45 deg about Z), local rotation differing
from global (selection rotated 90 deg about Y, locked Z rotates about world
X by -45 deg), rotate snap (17 deg -> 15 deg), axis-locked scale, and the
pivot as rotation centre (pivot [10,0,0] stays fixed; [11,0,0] swings to
[10+cos45, sin45, 0]).
