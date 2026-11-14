# hammer-gizmo-state-persistence fixtures

Board task: `hammer-gizmo-state-persistence` (worker muse-3-desktop-hammer-47).

## saved-states.json

Four map-document cases for `GizmoModel.LoadFromDocument`, consumed by the
`fixture saved-states.json` check in `tests/GizmoStatePersistenceTests.cs`
(group `gizmostatepersistence`). Each case carries a whole document object
(the `gizmo` section is what loads; sibling fields such as `name` must
survive untouched) and the expected gizmo state after loading it:

- `valid-v1`: a complete schema-version-1 section — Rotate/Local/XY lock,
  pivot [16, 32, -8], 90-degree-Y orientation, snap on (grid 8, angle 30).
  Loads cleanly: 0 warnings.
- `corrupted`: unknown mode ("Banana"), negative grid size, a string pivot,
  a zero angle snap, plus an unknown field (`hyperdrive`). Every bad field
  falls back to its default with a named warning (5 warnings minimum);
  the model stays valid and the health check passes.
- `future-version`: schemaVersion 3 with a known field (mode Scale) and an
  unknown field (`quantumLock`). Warns about the newer version and the
  unknown field, still loads the known field.
- `fresh`: a document with no `gizmo` section at all — silent defaults,
  0 warnings, health check passes.

Expected values were hand-computed from the migration rules in
`kernel/Effigy/Map/GizmoModel.cs` (per-field fallback to defaults, unknown
fields warned by name and preserved for re-emit), not copied from the
implementation.
