# BSP fixtures — hammer-bsp-core-tree-portals-leaves

Plain-text brush lists, parsed by `tests/BspBuilderTests.cs` (the only reader).
One `brush <name> [detail]` line, then one `plane nx ny nz d` line per face:
outward unit normal, plane `Dot(n, p) == d`, solid side `Dot(n, p) <= d`.
All coordinates are the kernel's inches, Z-up. Brushes were generated from box
bounds (see the generator notes below), not hand-typed, so the planes are exact.

## Maps

- `sealed-room.txt` — one sealed room, 256x256x128 interior, 16-thick walls
  (6 flush box brushes: floor, ceiling, 4 walls). Expected: 1 visleaf, 0 portals,
  PVS self-only, 1 cluster, sealed.
- `two-room-doorway.txt` — two 256x256x128 rooms side by side along X (A: x in
  [-256, 0], B: x in [0, 256]), shared 16-thick wall at x in [-8, 8] with a
  doorway gap 64 wide (y in [-32, 32]) x 96 high (z in [0, 96]): the wall is
  two segments plus a lintel. Expected: >= 2 visleaves, exactly 1 portal
  crossing x = 0 (64x96 = 6144 area, centre (0, 0, 48)), every room-A visleaf
  sees every room-B visleaf, sealed.
- `two-room-sealed.txt` — same two rooms, shared wall solid (no doorway).
  Expected: no portal joins the rooms, each room sees only itself, 2 clusters.
- `leak-room.txt` — the sealed room minus the +x wall. Expected: the interior
  floods to the outside, 0 visleaves, not sealed.

## Generator

Each brush is an axis-aligned box (min, max) expanded to 6 planes:

```
(-1,0,0,-min.x) (1,0,0,max.x) (0,-1,0,-min.y)
(0,1,0,max.y)   (0,0,-1,-min.z) (0,0,1,max.z)
```

The detail-brush case (a pillar that must not split leaves) is built in code in
`BspBuilderTests`, not as a fixture, because `detail` is a flag on the brush,
not a property of the planes.
