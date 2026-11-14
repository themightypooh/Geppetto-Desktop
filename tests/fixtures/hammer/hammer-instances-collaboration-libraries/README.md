# Fixtures — hammer-instances-collaboration-libraries

Shared instance libraries, name fix-up, IO proxy and per-map compile
settings (`InstancesCollab.cs`, schema version 1 throughout). Coordinates
are kernel inches, Z-up, sub-map-local unless an instance offset applies.
Paths are project-relative with forward slashes (an absolute path, a
backslash or a `..` segment is refused by manifest validation).

- `library-registry.json` — the "room_kit" library, version 1.1.0, at
  `libraries/room_kit`, with two entries: `room_small` 1.1.0 and
  `room_large` 1.0.0. This is the registry JSON
  (`InstanceLibraryRegistry.ToJson` / `FromJson`).
- `room-small-v1.json` — the `room_small` sub-map at 1.0.0: a 256x256 room
  (floor + wall brushes), four entities (player start, `room_light`,
  `door_relay` whose `target` keyvalue names `room_door`, and `room_door`
  itself) and one IO output (`door_relay` OnTrigger -> `room_door` Open).
  The resolve check prefixes this with `room_a` at offset +512 X.
- `room-small-v2.json` — the same sub-map at 1.1.0: everything from v1
  plus a `room_prop` prop_static and a ceiling brush. The update check
  re-resolves v1 -> v2 and requires the v1 prefixed names to survive
  unchanged, with only `room_prop` added.
- `room-small-v3.json` — the same sub-map at 1.2.0, with the author
  renaming `room_door` to `room_gate` (same entity id `e-door`) and
  deliberately leaving the stale references behind: the relay's `target`
  keyvalue and the IO row still name `room_door`. The rename update check
  resolves v1 -> v3 with the rename map `room_door -> room_gate` and
  requires the renamed door to keep its v1 resolved name
  (`room_a_room_door`), the stale references to follow the rename, and
  the IO proxy to remap both legs through the same map.
- `compile-settings.json` — a `MapCompileSettings` section: Final preset,
  no overrides, limits lowered to 8192 brushes / 64 planes per brush (a
  real version-controlled choice for this map, not the 16384/128 defaults).

Mismatch note (2026-11-13): `MapInstances`
(hammer-sprinkles-prefabs-instances, worker 59) and `MapDocument`
(hammer-map-document-model, worker 12) had not landed when this ticket was
implemented, so the sub-map here is the instance-relevant subset
(entities + brushes + IO outputs) rather than a full map document, and
the map-document embedding (`instance_libraries` / `library_instances` /
`instance_io_proxies` / `compile_settings` sections) is a documented seam
for MapDocument, not a call into it. See `InstancesCollab.cs` header.
