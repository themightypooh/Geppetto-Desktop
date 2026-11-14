# hammer-leak-detection-pointfile fixtures

Leak detection (flood-fill) + point-file export. Units: inches, Z-up.

## Fixture graphs (built in code: `LeakFinder.LeakGraph`)

- **SealedRoom()** — one interior leaf (128 x 128 floor, 128 high,
  centre (0, 0, 64)) + one void leaf, no portal between them.
  Player start (0, 0, 32). Expected: sealed, no point file.
- **RoomWithGap()** — sealed room + one open 1-unit gap portal in the
  +X wall at (64, 0, 32). Player start (0, 0, 32). Expected: LEAKED,
  gap leaf 0 / gap portal 0, path (0, 0, 32) -> (64, 0, 32) -> (80, 0, 32).
- **TwoRooms(leakInFarRoom)** — rooms A (x -128..0) and B (x 0..128)
  joined by a doorway portal at (0, 0, 32); optional gap portal from B
  to the void at (128, 0, 32). Player start in A (-64, 0, 32).

## Files

- `gap-room.lin` — expected point file for RoomWithGap() (Valve .lin
  format: one `x y z` line per point, no header). The `leakfinder`
  test asserts the computed point file is byte-identical to this file.
- `gap-room.pts` — same content, `.pts` extension (same line format).
- A sealed map has no leak path, so there is deliberately no
  `sealed-room.lin` — a sealed result exports empty point-file text.
