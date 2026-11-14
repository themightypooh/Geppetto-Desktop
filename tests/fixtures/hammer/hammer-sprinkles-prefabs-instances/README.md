# Fixtures — hammer-sprinkles-prefabs-instances

Map-instances document sections (`MapInstanceStore.ToJson` / `FromJson`,
schema version 1). Coordinates are kernel inches, Z-up, preset/prefab/
sub-map-local unless a placement origin/yaw applies.

- `instances.json` — the "room_a" store: one sprinkle preset
  (`sp-1` "barrel-cluster": 2 oil drums + 1 physics wood crate), one
  prefab (`pf-1` "door-frame": frame brush + `light` entity
  `frame_lamp`), one sub-map (`sm-1` "room_a" from
  `maps/rooms/room_a.vmf`: floor + wall brushes, `light` entity
  `room_light`, `logic_relay` entity `room_relay` whose `OnTrigger`
  output targets `room_light`, whose `target` names the main-map
  `main_door` (an outward reference fix-up must leave alone), and whose
  `OnTrigger2` names `!activator` (an engine special fix-up must never
  touch). One instance (`mi-1` at +512 X, prefix `room_a_`) and one IO
  proxy (`iop-1`: input `OpenRoom` → `room_relay`/`Trigger`, output
  `RoomLit` ← `room_light`/`OnTurnOn`). This is the store the fixture
  check loads and resolves; the relay output must resolve to
  `room_a_room_light,TurnOn,,0,-1` while `main_door` stays unprefixed.
- `instances-roundtrip.json` — a second store (barrel preset with 2
  items, `pf-1` "pillar" prefab with one shaft brush, the same room
  sub-map, two instances `mi-1`/`mi-2` at 0 and +512 X, one proxy).
  This is the store the round-trip check loads, re-serialises and
  reloads; resolved members must be identical before and after, and the
  next ids after reload are `sp-2` / `mi-3`.

Mismatch note (2026-11-13): `MapDocument`
(hammer-map-document-model) has not landed, so sub-maps here are
registered in-memory content bound by string id + source path
(`RegisterSubMap`), not loaded documents, and `PlacePreset` returns
`PlacedSprinkle` data for the MapEntities/PropPlacement adapter to seat
(see `MapInstances.cs` header). Phase 3
(hammer-instances-collaboration-libraries) builds on `MapInstances.cs`:
`InstanceNameFixUp` is the fix-up engine it reuses for library updates.
