# hammer-vertex-multi-edit-autotriangulate fixtures

Box layout (all files): vertices 0-3 bottom (z=-1), 4-7 top (z=+1);
face order bottom, top, front(-Y), back(+Y), left(-X), right(+X).
One distinct material per face, so tests can prove triangulation keeps the
parent face's material on every child triangle / hull face.

- `box.json` — valid unit box, volume 8. TriangulateInvalid must return it
  untouched (AlreadyValid, WasTriangulated false).
- `twisted-box.json` — top vertex 6 raised to z=2: only the top face is
  non-planar, volume 9.333333. Fan-triangulates to one valid piece, same
  volume, both top children keep material "top".
- `l-prism.json` — L-section prism (z 0..1), volume 3. Top/bottom hexagons
  are concave (reflex vertex at (1,1)) so the brush is invalid; fan
  triangulation fixes both into one valid 14-face piece, same volume.
- `collapsed-top.json` — all four top vertices collapsed to (0,0,1): the top
  face is degenerate (volume 8/3, a pyramid). No fan can fix it, so
  TriangulateInvalid must fall back to ConvexDecomp hulls — valid piece(s),
  same volume, materials from the original set.
