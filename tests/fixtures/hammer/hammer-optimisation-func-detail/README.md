# Fixtures for hammer-optimisation-func-detail (FuncDetailTests, group `funcdetail`)

Plain-text brush lists, one `brush <name> [detail|blocker|illusionary]` line
then one `plane nx ny nz d` line per face (outward unit normal, plane
Dot(n, p) == d, solid side Dot(n, p) <= d — the MapBrushFace / BspPlane
convention). Parsed only by FuncDetailTests.LoadFixture; a stale fixture fails
the tests instead of silently passing.

- `decorated-room.txt` — the sealed room (interior void -128..128 x -128..128 x
  0..128, same numbers as the BSP fixture sealed-room.txt) plus four
  func_detail brushes inside the void (three stair treads + a trim beam).
  Must build to the same leaf count as the bare room and validate clean.
- `sealing-wall.txt` — the same room with its wall-x-pos shell brush flagged
  `detail`. Must produce exactly one SealingBrushFuncDetail warning (brush #3)
  and an unsealed tree.
