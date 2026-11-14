# Visleaf debug fixtures — hammer-optimisation-visleaf-debug-data

Plain-text brush lists, parsed by `tests/VisleafDebugTests.cs` (the only
reader). One `brush <name> [detail]` line, then one `plane nx ny nz d` line per
face: outward unit normal, plane `Dot(n, p) == d`, solid side
`Dot(n, p) <= d`. All coordinates are the kernel's inches, Z-up.

Provenance: the four maps are byte-identical copies of the BSP builder
ticket's fixtures (`tests/fixtures/hammer/hammer-bsp-core-tree-portals-leaves/`,
hammer-bsp-core-tree-portals-leaves), copied into this ticket's own directory
so the debug-data tests do not depend on the dependency ticket's fixture dir.
The expectations are the same as the builder's (see that dir's README.md):

- `sealed-room.txt` — one sealed room, 256x256x128 interior, 16-thick walls.
  Expected dump: 1 visleaf, 0 vis portals, PVS self-only, 1 cluster, sealed.
- `two-room-doorway.txt` — two 256x256x128 rooms side by side along X (A: x in
  [-256, 0], B: x in [0, 256]), shared 16-thick wall at x in [-8, 8] with a
  doorway gap 64 wide (y in [-32, 32]) x 96 high (z in [0, 96]).
  Expected dump: >= 2 visleaves, exactly 1 vis portal crossing x = 0 (64x96 =
  6144 area, centre (0, 0, 48)), every room-A visleaf sees every room-B
  visleaf, sealed.
- `two-room-sealed.txt` — same two rooms, shared wall solid (no doorway).
  Expected dump: no vis portal joins the rooms, each room sees only itself,
  2 clusters.
- `leak-room.txt` — the sealed room minus the +x wall. Expected dump: the
  interior floods to the outside, 0 visleaves, not sealed, empty leaves array.
