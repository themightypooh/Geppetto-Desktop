# hammer-lightmap-progressive-bake fixtures

- `bake-reference.json` — per-face average luxels (HDR linear) from the REAL
  `LightmapBake` refined pass on the shared room fixture
  (`../hammer-preview-live-lighting/room.json`: sealed 256x256 room, 128 high,
  centre point light, overhead sun, light_environment ambient). Recorded from
  the bake itself, NOT from the preview engine. The `LightmapBakeTests`
  room-fixture check compares the bake against this file (tolerance 1e-4).
  The final integrator copies this file over worker 23's
  `hammer-preview-live-lighting/bake-reference.json`, which was recorded from
  the preview engine's own Full evaluation and is not an independent bake.
- `room.json` is NOT copied here: the shared fixture lives in
  `../hammer-preview-live-lighting/room.json` and the tests load it from there,
  so the two tickets can never drift apart.
