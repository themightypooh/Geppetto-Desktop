# Fixtures: hammer-optimisation-areaportal-occluder
#
# The map is the two-room doorway map (same brush list as the BSP core fixture
# tests/fixtures/hammer/hammer-bsp-core-tree-portals-leaves/two-room-doorway.txt):
# room A x -256..0, room B x 0..256, both y -128..128, z 0..128; doorway at
# x = 0, y -32..32, z 0..96 (64 wide x 96 high, centre (0, 0, 48)).
#
# - two-room-doorway.txt — the map brushes (brush/plane lines, same format as the
#   BSP core fixture: `brush <name> [detail]`, then `plane nx ny nz d` per face).
# - areaportals.txt — one areaportal, door-01, gating the doorway, starting
#   closed, controlled by the func_door targetnamed door_01 (placed by the
#   tests against the real core registry).
# - occluders.txt — curtain-b, a 128x128 quad at x = 128 in room B facing -X
#   (spans y -64..64, z 0..128); postage-stamp, a 16x16 quad, deliberately
#   below the 4096 minimum occluder area.
#
# Viewer used by the tests: (-128, 0, 64) in room A. Props behind the curtain
# (200, 0, 64) are hidden; props in front of it (64, 0, 64) or past its side
# edge (200, 100, 64) are not.
