# Hint/skip fixtures — hammer-optimisation-hint-skip-brushes

Plain-text hint-brush lists, parsed by `tests/HintSkipTests.cs` (the only
reader). One `brush <name>` line, then one `face nx ny nz d <material>` line
per face: outward unit normal, plane `Dot(n, p) == d`, solid side
`Dot(n, p) <= d`, material is a full material name (`tools/toolshint`,
`tools/toolsskip`). All coordinates are the kernel's inches, Z-up. Faces are
listed in MapBrush.Box order: 0 = -X, 1 = +X, 2 = -Y, 3 = +Y, 4 = -Z, 5 = +Z.

The rooms these hints cut come from the BSP ticket's fixtures (read-only,
`hammer-bsp-core-tree-portals-leaves`): `sealed-room.txt` is one sealed room
(interior x/y in [-128, 128], z in [0, 128], 1 visleaf) and
`two-room-doorway.txt` is two rooms joined by a doorway gap (y in [-32, 32],
z in [0, 96]) through the shared wall at x in [-8, 8].

## Maps

- `doorway-hint.txt` — thin slab across the doorway throat: x in [-2, 2],
  y in [-32, 32] (the doorway width), z in [0, 96] (the doorway height). The
  +X face (plane x = 2) is hint; the other five faces are skip. Expected: the
  doorway throat leaf splits along x = 2 (one new 64x96 portal, centre
  (2, 0, 48)), visleaf count rises, both sides stay void, map stays sealed.
- `doorway-hint-allskip.txt` — the same slab with all six faces skip.
  Expected: the tree is identical to the no-hint build; Validate reports
  exactly NoHintFace.
- `room-hint.txt` — thin slab across the sealed room: x in [-2, 2],
  y in [-32, 32], z in [0, 128]. The +X face (plane x = 2) is hint; the rest
  are skip. Expected: exactly 2 visleaves — one hint cut, and no cut across
  the y = +/-32 skip planes.
- `room-hint-allskip.txt` — the same slab with all six faces skip. Expected:
  the tree is identical to the no-hint build.
- `room-hint-on-wall.txt` — slab spanning the whole sealed room in Y:
  x in [-2, 2], y in [-128, 128], z in [0, 128]. The +Y face (plane y = 128)
  is hint — coincident with the room wall's own plane — and the rest are skip
  (the x = +/-2 skip faces straddle the room and must not cut). Expected: the
  tree is identical to the no-hint build.
