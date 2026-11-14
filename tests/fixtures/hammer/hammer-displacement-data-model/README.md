# Fixtures — hammer-displacement-data-model

Displacement data-model fixtures. All coordinates are kernel units (inches,
Z-up). Faces are quads with corners ordered around the quad: corner 0 -> 1 is
the grid's row 0, corner 3 -> 2 is its last row.

## Files

- `sew-pair.json` — two coplanar unit-square faces sharing the edge x=1
  (power 2 each, material 3). `edgeOffsetsA` / `edgeAlphasA` run along face A's
  edge 1 from (1,0,0) to (1,1,0); `edgeOffsetsB` / `edgeAlphasB` run along
  face B's edge 3 from (1,1,0) to (1,0,0) — the same physical profile in the
  opposite direction, so a correct Sew() must match them reversed. After
  sewing, both shared edges read [0, 0.5, 1, 1.5, 2] offsets and
  [0, 0.25, 0.5, 0.75, 1] alphas from y=0 to y=1.
- `power3-bump.json` — power-3 displacement on a 4x4 unit square in the XY
  plane (z=0, normal +Z), material 7. Offsets are a centre bump, row-major,
  81 values: 2.0 at the centre vertex, falling to 0 at radius 1 in (u,v)
  space, 0 outside.
- `README.md` — this file.

## Format

Corners are `[x, y, z]` triples. Offset/alpha arrays are row-major over the
grid: index `row * gridSize + col`, gridSize = 2^power + 1. Tests load these
with `System.Text.Json` and fail (skip when the fixture directory cannot be
found from the working directory) if the data does not match the model.
