# hammer-compile-presets-only-entities fixtures

`entities-room.json` — the sealed room from
`hammer-compile-native-pipeline/sealed-room.json` (same six box brushes,
same player start, same light) plus three entities:

- `#1 light_spot` at `0 0 96`, targetname `lamp1`, keyvalues `_cone=45`,
  `_light=255 255 255`
- `#2 prop_static` at `64 0 32`, targetname `crate1`, keyvalues
  `model=models/crate.mdl`, `skin=0`
- `#3 logic_relay` at `-64 0 32`, targetname `relay1`, keyvalues
  `StartDisabled=0`

Used by `tests/CompilePresetsTests.cs` (group `compilepresets`): a full Final
run writes the `.entities` lump for these three; an Only-Entities run that
moves/renames an entity reuses the prior BSP + PVS + lightmaps and rewrites
only the lump. The sealed-room / leaking-room fixtures from
`hammer-compile-native-pipeline/` are reused for the Fast-VIS and go-to log
entry checks.
