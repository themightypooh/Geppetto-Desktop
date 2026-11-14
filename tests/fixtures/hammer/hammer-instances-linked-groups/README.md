# Fixtures — hammer-instances-linked-groups

Linked-group document sections (`LinkedGroupSet.ToJson` / `FromJson`, schema version 1).
Coordinates are kernel inches, Z-up, definition-local unless an instance offset applies.

- `linked-groups.json` — the "crate-stack" definition (`lg-1`): two brush members
  (crate 64x64x64 + lid 64x64x8) and one `light` entity, in layer `detail` /
  visgroup `props`. Two linked copies (`lgi-1` at origin, `lgi-2` at +128 X)
  share definition id `lg-1`. One independent group (`lgi-3`) is the snapshot
  of a former copy unlinked at +256 X — its vertices already carry the offset,
  so a later definition edit must NOT move them.
- `linked-groups-roundtrip.json` — the "pillar" definition (`lg-1`): one brush
  member (32x32x128 shaft) + one `light` entity, three linked copies. This is
  the set the round-trip check loads, re-serialises and reloads; resolved
  members must be identical before and after.

Mismatch note (2026-11-13): `MapLayers` (hammer-selection-grouping-layers-visgroups-model)
and `MapInstances` (hammer-sprinkles-prefabs-instances) had not landed when this
ticket was implemented, so layers/visgroups here are plain names on the
definition/instance (see `LinkedGroups.cs` header) and the VMF seam is
`ToInstanceRecords` / `FromInstanceRecords`, not a VmfIo call.
