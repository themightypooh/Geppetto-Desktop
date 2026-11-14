# hammer-csg-intersect fixtures

Acceptance cases for CSG intersect on map brushes (board
`hammer-csg-intersect`, Geppetto Desktop Hammer P2).

`cases.json` (schema `hammer-csg-acceptance/v1`) lists axis-aligned boxes
centred on the origin unless `toolOffset` moves the tool. Volumes are in
Geppetto Desktop kernel units (inches, Z-up). The `mapcsgintersect` test
group (`tests/MapCsgIntersectTests.cs`) recomputes every case from this
file:

- `op` is always `intersect` here.
- `expectedVolume` / `expectedFaces` / `convex` assert the overlap volume,
  the coplanar-merged face count, and convexity of a successful result.
- `"empty": true` cases assert `TryIntersect` refuses with reason
  `csg-empty-result` and no mesh: disjoint solids, or solids that merely
  touch on a face, share no volume, and the empty result is named — never
  a zero-volume brush smuggled through.

Failure safety (board `hammer-csg-failure-safety`) adds 5 checks to
this group: on any intersect failure the source brushes survive
byte-identical to pre-call snapshots, with a named reason and no mesh
— including a mutating/refusing/throwing provider, the
manifold-missing fallback failure, and no provider at all. Its cases
live in `tests/fixtures/hammer/hammer-csg-failure-safety/cases.json`,
recomputed by the `fixture failure cases leave inputs intact with named
reasons` check in this group.
