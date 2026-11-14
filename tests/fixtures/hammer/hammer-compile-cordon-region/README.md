# hammer-compile-cordon-region fixtures

Fixtures for the cordon / region compile (`kernel/Effigy/Map/CompileCordon.cs`,
test group `compilecordon`). Units are Source inches, Z-up — the kernel
convention. Same schema as the native-pipeline fixtures
(`tests/fixtures/hammer/hammer-compile-native-pipeline/README.md`), plus one
field:

```json
{
  "name": "two-room-cordon",
  "playerStart": [x, y, z],
  "lights": [{ "position": [x, y, z], "color": [r, g, b], "intensity": 1, "range": 0 }],
  "brushes": [{
    "name": "wall_west", "id": 2, "detail": false, "material": "concrete",
    "layer": "room-a",
    "box": { "min": [x, y, z], "max": [x, y, z] }
  }],
  "expected": { "leaked": false }
}
```

`"layer"` is the layer/visgroup id the brush belongs to (the
`MapBrushData.LayerId` the future FromDocument adapter copies into
`CompileBrush.Layer`); absent = unlayered, always compile-visible. The test
loader (`CompileCordonTests`) reads it into a brush-id -> layer map that the
tests pass as the `layerOf` lookup to `ApplyVisibleOnly` / `FromLayer`.

## Maps

- `two-room-cordon.json` — the native-pipeline `two-room.json` (two 248-wide
  rooms sharing a 16-thick wall with a doorway 64 wide, 64 high) plus layer
  tags: `wall_west` is `room-a`, `wall_east` is `room-b`, everything else
  (floor, ceiling, south/north walls, shared-wall segments) is unlayered.
  Player start in room A, one light in room A. A cordon around room A
  (x -272..8) keeps 8 brushes, drops `wall_east`, clips floor / ceiling /
  south / north at x 8, and seals the doorway with one temporary seal brush
  on its east face (the other five cordon faces coincide with the hull, so
  they need no seal).
- `leaking-two-room.json` — the same two rooms, but the east wall (room B)
  is split into two brushes leaving a 1-unit gap (y -0.5..0.5, full height),
  gap centre (264, 0, 64). The full map leaks; the leak is in room B only:
  a cordon around room A compiles clean, a cordon around room B (with a
  player start inside room B) fails with a named `leak` issue, and bisecting
  the whole-map cordon along X isolates the leaking half (Second).

## Dependency status (2026-11-13)

`CompileRunner` / `CompileInput` / `CompileBrush` / `LeakFinder` are real
(native-pipeline ticket, done) and are reused through their real APIs.
`MapLayer` / `MapVisGroup` have landed in `MapDocument.cs` with
`CompileVisible` flags, and `CompileInput.FromDocument(MapDocument)` has
landed (it filters via `CompileVisibleBrushes()` /
`CompileVisibleEntities()`), but it does not copy `LayerId` into
`CompileBrush` (no `Layer` tag exists yet) — so no test here depends on
`MapDocument`: layer identity comes from this fixture's `"layer"` field. The
`CompileRunner.PrepareInput` / `CompileOptions` wiring for cordons is
deferred to the final integrator (see the header of `CompileCordon.cs`):
`CompileRunner.cs` is locked on the Hub by the parallel-stages worker, so
this ticket applies the cordon explicitly (`ApplyCordon` then `Run`)
instead of through options.
