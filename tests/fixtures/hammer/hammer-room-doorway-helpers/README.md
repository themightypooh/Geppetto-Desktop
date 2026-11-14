# hammer-room-doorway-helpers fixtures

Acceptance numbers for `tests/RoomHelpersTests.cs` (group `roomhelpers`).
All units are kernel inches, Z-up. Room: interior centred on the origin in
X/Y, floor top at Z = 0, outer shell = interior + wall thickness on every
side. North is +Y, South -Y, East +X, West -X.

- `room-128.json` — 128 x 128 x 128 room, 8 walls. Six convex solids:
  floor/ceiling (144 x 144 x 8), North/South walls (144 x 8 x 128),
  East/West walls (128 x 8 x 128). Total solid volume = outer box minus
  interior void = 144^3 - 128^3 = 888832. Visible faces: 6 (one per solid);
  nodraw faces: 30 (five per solid).
- `doorway-128.json` — same room, doorway 48 wide x 96 high in the North
  wall, centred. North wall after the cut: 147456 - 48*96*8 = 110592.
  Opening centre (mid-thickness): (0, 68, 48). Jambs |x| in [24, 72];
  lintel z in [96, 128].
- `window-128.json` — same room, window 64 wide x 48 high, sill 32, in the
  East wall, centred. East wall after the cut: 131072 - 64*48*8 = 106496.
  Opening centre (mid-thickness): (68, 0, 56). Sill z in [0, 32];
  lintel z in [80, 128].

The tests recompute these numbers from the JSON; the JSON is the
acceptance record, not the only source (the same cases are also built
synthetically in the test file).
