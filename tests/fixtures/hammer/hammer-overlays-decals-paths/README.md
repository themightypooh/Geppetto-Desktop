# hammer-overlays-decals-paths fixtures

- `overlays.json` — one `OverlayDecalSet` as JSON: overlay `road-marking`
  (`decals/roadline` projected from origin (0,0,64) with U = world X, V = world Y,
  1 UV per 64 inches) onto two faces of the 64-inch room box the tests build —
  the top face (face 5, +Z, `plaster/plaster`) and the +X wall (face 1,
  `metal/metal`); decal `poster` (`decals/poster01`) stamped at the centre of the
  top face, radius 8; path chain `train-loop` (track_a -> track_b -> track_c ->
  track_a, a loop covering every node). The `OverlaysDecalsTests`
  "fixture-json-loads-and-validates" check loads this file, asserts the
  1/1/1 counts and ids, validates the set clean against the room box, and
  round-trips it through JSON.
- Face indices match the box the tests build (same order as the clip tests):
  0:-X brick, 1:+X metal, 2:-Y wood, 3:+Y glass, 4:-Z concrete, 5:+Z plaster.
- No MapDocument lives here: the fixture is the overlay/decal/path set on its
  own, resolved against brushes the caller supplies — not a whole map file.
  MapDocument has since landed; the document binding is MapDocumentOverlays
  (kernel/Effigy/Map/MapDocumentOverlays.cs, tested in
  tests/MapDocumentOverlaysTests.cs), which resolves the set against a real
  document's brushes.
