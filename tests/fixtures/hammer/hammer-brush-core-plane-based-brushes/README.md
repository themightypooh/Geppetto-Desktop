# Hammer brush-core fixtures

One brush per file, in the little line format `MapBrushTests.ParseFixture` reads.
Comments start with `#`; blank lines are skipped.

## Format

```
face nx ny nz d material uox uoy usx usy rot lightmap smoothing
loop x y z x y z ...
```

- `face` — one brush face as a plane: unit outward normal `(nx, ny, nz)`, distance
  `d` (the plane is `n.p = d`, the solid is `n.p <= d`), material name (a single
  token like `brick/brickwall` or `tools/toolsnodraw`), then the FaceUv: UV offset
  `(uox, uoy)`, UV scale `(usx, usy)`, rotation degrees, lightmap scale, smoothing
  group. A `face` file builds a brush with `MapBrush.FromPlanes`.
- `loop` — one face as a vertex loop (3+ triples). A `loop` file builds a brush
  with `MapBrush.FromFaceVertices`, which is where twisted-vertex and concave input
  can be detected: the plane comes from the loop's first three non-collinear points.

## Files

- `box-6-planes.txt` — 2 x 3 x 4 box centred on the origin. Valid: volume 24,
  centroid (0, 0, 0), 8 vertices, 6 quad faces.
- `wedge-5-planes.txt` — triangular prism: triangle (-1,0), (1,0), (0,2) in XZ,
  extruded y in [-1, 1]. Valid: volume 4, centroid (0, 0, 2/3), 6 vertices.
- `zero-volume-flat-box.txt` — the box squashed flat: top and bottom are both
  z = 0. Invalid: `ZeroVolume` (plus `DuplicateFace` and `DegenerateFace` for the
  side faces, which have only 2 solved vertices each).
- `twisted-face-loops.txt` — the box as loops, but the top loop's last vertex is
  at z = 2.5 instead of z = 2. Invalid: `NonPlanarFace` (and `ConcaveInput`,
  because that vertex also pokes out of the solid); volume stays 24, so no
  `ZeroVolume` — the two reasons are distinct.
- `duplicate-plane.txt` — the box plus the +X face a second time. Invalid:
  `DuplicateFace`; volume stays 24 (a duplicated plane contributes once).
