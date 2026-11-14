# hammer-entity-report-find-replace fixtures

## report-map.json

Fixture map for the Entity Report tests (`EntityReportTests`, group
`entityreport`). Schema (this file's own format, loaded by the test file —
not a VMF and not a MapDocument; those owners define their own formats):

- `schemaVersion` (1), `name`, `description`
- `worldBrushes`: compiler brush numbers owned by worldspawn (no entity).
- `entities`: array of `{ id, classname, keyvalues, brushes?, outputs? }`
  - `id`: stable VMF entity id.
  - `keyvalues`: string map; `targetname` is the entity's target name.
  - `brushes`: compiler brush numbers owned by the entity (brush entities).
  - `outputs`: `{ event, target, input, delay, times }` — one VMF
    connections-block row / Hammer Outputs-tab row. `times` -1 = forever.

Contents: 5 `npc_citizen` (`npc_01`–`npc_05`; squads alpha x3, beta x2),
`info_player_start`, `light`, `light_spot` (aimed at `door_01` via its
target-typed `target` keyvalue), `func_button` (`button_01`, brushes [103],
`OnPressed -> door_01 Open`), `func_door` (`door_01`, brushes [101, 102],
`OnFullyOpen -> relay_01 Trigger`), `logic_relay` (`relay_01`,
`OnTrigger -> door_01 Open`). 11 entities total; world brushes 1–2.

Acceptance mapping: SelectAllByClass(`npc_citizen`) returns all 5;
Find/Replace renaming target `door_01` -> `door_02` updates the door's
targetname, the button's and relay's output targets, and the spotlight's
`target` keyvalue; brush 101 resolves to the `func_door` entity.

## npc-registry.json

Fixture registry defining `npc_citizen` and `npc_combine_s` (the core
registry has no NPC class). Tests load the core registry and merge this
file with `EntityRegistry.LoadAdditional`. Do NOT copy it into
`kernel/Effigy/Map/registry/` — that directory belongs to the registry
worker.
