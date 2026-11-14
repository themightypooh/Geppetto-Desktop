# hammer-displacement-nodraw-backing fixtures

## `backed-terrain.json`

One displacement on the +X wall inner face of a 128x128x128 room
(x=64 plane, normal -X into the room, power 2, group 7, thickness 16),
plus the leak gap it leaves open: portal centre (64, 0, 32) at player
height, matching `LeakFinder.LeakGraph.RoomWithGap`.

The test (`displacementbacking` group, "fixture: backed-terrain.json ...")
builds the real `Displacement` from the fixture corners, generates its
backing, and checks:

- the backing validates and is all nodraw, in group 7;
- the unbacked leak graph leaks through the gap;
- `DisplacementBacking.SealGraph` with the fixture backing seals it.

Corner order matters: the corners are listed so `MapBrushFace.FromCorners`
(Newell's method) returns the -X normal — reversed from the +X order.
Units are kernel inches, Z-up.
