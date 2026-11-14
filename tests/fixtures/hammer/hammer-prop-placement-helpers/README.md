# hammer-prop-placement-helpers fixtures

Prop placement helpers (PropPlacement): Move-to-Floor/Ceiling, align-by-normal,
drop-to-rest pose. All coordinates are kernel inches, Z-up, same convention as
the rest of Effigy.

- `sloped-room.json` — sealed room (floor top z=0, ceiling bottom z=128) + one
  sloped displacement patch (x/y -128..128, heights 0/32/64/96/128 across +X:
  slope dz/dx = 0.5, 26.57 deg, normal (-0.4472, 0, 0.8944)). At (0,0) the
  displacement is 64 high. `crate_on_slope` (prop_static at 0,0,100, below the ceiling) must land
  on the slope, not the flat floor; `lamp_under_ceiling` (prop_static at 64,0,100,
  yaw 90) must hang from the ceiling at z=128.
- `flat-room.json` — sealed room, flat floor (top z=0) and flat ceiling (bottom
  z=128), no displacements. Baseline for MoveToFloor / MoveToCeiling / DropToRest.
- `registry.json` — minimal EntityRegistry (schemaVersion 1) with
  info_player_start, prop_static, prop_dynamic. Same shape as
  kernel/Effigy/Map/registry/core-source1.json, cut down so this group is
  hermetic.

Map JSON shape (mirrors the Quick Play fixtures): `brushes` (id/min/max/
detail/material), `entities` (class/origin/yaw/keyvalues), `displacements`
(origin/sizeX/sizeY/columns/rows/heights as rows of columns, row = +Y).
