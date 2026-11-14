# hammer-compile-native-pipeline fixtures

Fixtures for the native Geppetto compile pipeline
(`kernel/Effigy/Map/CompileRunner.cs`, test group `compilerunner`).
Units are Source inches, Z-up — the kernel convention.

## Schema

```json
{
  "name": "sealed-room",
  "playerStart": [x, y, z],            // null = no player start (leak check skipped, warning)
  "lights": [{ "position": [x, y, z], "color": [r, g, b], "intensity": 1, "range": 0 }],
  "brushes": [{
    "name": "floor", "id": 0, "detail": false, "material": "concrete",
    "box": { "min": [x, y, z], "max": [x, y, z] }   // axis-aligned box -> 6 planes
  }],
  "expected": { "leaked": false }      // what the tests assert
}
```

A brush may alternatively carry explicit `"faces"` instead of `"box"`:
`"faces": [{ "normal": [x, y, z], "distance": d, "material": "..." }]` —
one entry per plane (`normal . p = distance`, outward normal). The test
loader (`CompileRunnerTests.LoadFixture`) supports both forms. No VMF or
`.map` fixture lives here: VMF/.map parsing is the VMF ticket's job
(hammer-vmf-map-import-export, not landed); when it lands, a VMF round-trip
fixture can be added and loaded through `VmfIo` instead of this JSON.

## Maps

- `sealed-room.json` — 6 box brushes forming a hollow room (interior
  256 x 256 x 128, walls 16 thick), player start at the centre, one light.
  Compiles clean on every preset; the interior leaf sees only itself.
- `two-room.json` — two 248-wide rooms sharing a 16-thick wall with a
  doorway (64 wide, 64 high: two jamb segments + a lintel). Player start in
  room A, one light in room A. Each room sees the other through the doorway;
  the far room's lit faces are dimmer (shadowed by the shared wall).
- `leaking-room.json` — the sealed room with the east wall split into two
  brushes leaving a 1-unit gap (y -0.5..0.5, full height). Fails at the BSP
  stage with a named `leak` issue; the point file traces the player start
  out through the gap. The gap opening is centred at (136, 0, 64) — the
  middle of the 16-thick wall — and the reported leak portal sits on either
  face of the wall (x 128 or 144).

## Dependency status (2026-11-13)

`LeakFinder` (hammer-leak-detection-pointfile) HAS landed and is reused by
the runner. `BspBuilder` / `PortalGraph`, `MapDocument` / `MapBrush`,
`VmfIo` and `IssueEngine` had NOT landed when these fixtures were written;
the runner implements the BSP / visibility / lightmap stages natively per
the filed descs and its input model (`CompileInput` / `CompileBrush` /
`CompileFace`) follows the plane-based brush shape those descs specify.

## Outputs (written by the runner on success)

`<name>.bsp` (GEPPETTO-BSP 1), `<name>.pvs` (GEPPETTO-PVS 1),
`<name>.lightmap` (GEPPETTO-LIGHTMAP 1), `<name>.lin` (Valve point file,
leak only), `<name>.compile.log` — all plain text, deterministic.
