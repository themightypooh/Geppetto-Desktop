# hammer-brush-csg-subtract-merge-hollow fixtures

CSG subtract / merge / hollow for map brushes (board
`hammer-brush-csg-subtract-merge-hollow`). Implementation:
`kernel/Effigy/Map/MapCsg.cs`. Tests: `tests/MapCsgTests.cs`, group
`mapcsg` (24 checks: 14 acceptance + 10 failure-safety, board
`hammer-csg-failure-safety` — see
`tests/fixtures/hammer/hammer-csg-failure-safety/README.md`).

## Files

- `cases.json` — the four acceptance cases (schema
  `hammer-csg-acceptance/v1`). The `mapcsg` group recomputes every case
  from this file (`fixture acceptance cases match computed volumes`); the
  other checks build the same cases synthetically so a missing fixture can
  never hide a geometry failure.
- This README.

## The cases

| case | op | expected volume |
|---|---|---|
| subtract-nested | 4-cube minus nested 2-cube | 56 |
| subtract-through | 4-cube minus through-cut 2x4x2 | 48 |
| merge-coplanar | two 2-cubes sharing a face | 16, 6 faces, convex |
| hollow-128-t8 | 128-cube, 8 walls | 692224, 12 faces, not convex |

## Rules the implementation enforces

- Solve is deferred: cuts are staged on `MapCsgPlan` and only run at
  bake (`Solve()`). Staging never runs a boolean and snapshots the
  brushes (clones), so later edits cannot change what bake solves.
- Provider order is Manifold first, `ExactMeshBoolean` fallback, via
  `IMeshBoolean`. No provider at all fails with the named capability
  reason `csg-no-boolean-provider` — never a fake uncut mesh.
- A Merge whose union is not convex is refused with `merge-not-convex`.
- There is no Carve operation, ever.

## Known mismatch (2026-11-13)

The `MapBrush` type this ticket depends on
(`hammer-brush-core-plane-based-brushes`) did not exist in the tree when
this was written — no `kernel/Effigy/Map/MapBrush.cs`, no references
anywhere. `MapCsg` is therefore built on `PolyMesh` plus the plane layer
(`BrushPlanes` / `MeshFromPlanes`, the `FromPlanes` equivalent). When
`MapBrush` lands, the integrator adds the thin adapters; see the header
of `MapCsg.cs` for the exact expected members.

## Run

From `/home/poo/Desktop/geppetto-desktop` (after the integrator adds the
one `Register` line to the coordinator-owned `tests/TestRegistry.cs`):

```
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter mapcsg
```
