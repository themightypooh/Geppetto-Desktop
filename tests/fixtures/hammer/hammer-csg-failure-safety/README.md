# hammer-csg-failure-safety fixtures

Failure-safety cases for map CSG (board
`hammer-csg-failure-safety`). Implementation:
`kernel/Effigy/Map/MapCsg.cs`. Tests: `tests/MapCsgTests.cs` (group
`mapcsg`, 10 new checks) and `tests/MapCsgIntersectTests.cs` (group
`mapcsgintersect`, 5 new checks).

## Files

- `cases.json` — the six failure cases (schema
  `hammer-csg-failure/v1`). Every case must fail with its
  `expectedReason`, return no mesh, and leave its inputs byte-identical
  to snapshots taken before the call. The `mapcsg` group recomputes the
  subtract/merge/hollow cases (`fixture failure cases leave inputs
  intact with named reasons`); the `mapcsgintersect` group recomputes
  the intersect cases under the same check name.
- This README.

## The cases

| case | op | expected reason |
|---|---|---|
| subtract-no-provider | subtract, provider `none` | `csg-no-boolean-provider` |
| hollow-too-thick | hollow 128-cube, thickness 100 | `hollow-too-thick` |
| hollow-thickness-zero | hollow 4-cube, thickness 0 | `hollow-thickness-not-positive` |
| merge-not-convex | merge two 2-cubes offset (1,1,0) | `merge-not-convex` |
| intersect-disjoint | intersect, tool 10 away on X | `csg-empty-result` |
| intersect-no-provider | intersect, provider `none` | `csg-no-boolean-provider` |

## The contract the cases pin down

A failed CSG/Hollow operation must never destroy the source brush —
the Reddit failure this ticket answers (`make hollow deletes the
brush itself`). The guard is structural: `MapCsg.Apply()` hands the
provider clones of the inputs, never the caller's meshes, and every
`Try*` op runs inside `Guard()`, which turns an unexpected exception
into the named reason `csg-operation-failed`. The synthetic checks in
both test groups go further than the fixture: provider doubles that
deliberately destroy their inputs before refusing/throwing, a
degenerate (empty) boolean result, an open boolean result, and a
failed staged op that must leave the plan snapshots intact for
re-bake — all with full-geometry snapshot comparison (positions,
faces, UVs, materials), not volume alone.

## Run

From `/home/poo/Desktop/geppetto-desktop` (after the integrator adds
the `Register` lines to the coordinator-owned `tests/TestRegistry.cs`):

```sh
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter mapcsg
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter mapcsgintersect
```
