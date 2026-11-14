# Fixtures — hammer-undo-autosave-map-commands

- `sealed-room.json` — a `MapStateSnapshot` (schema 1, see
  `kernel/Effigy/Map/MapUndo.cs` / `MapUndoJson`) of the sealed-room acceptance
  slice: 6 brushes (floor, ceiling, 4 walls; 512x512 room, 128 high, 16 thick,
  kernel inches, Z-up) + `info_player_start` + `light`. No displacements, no
  I/O connections — the tests add those through commands.
- The snapshot format is the autosave payload format: `MapUndoJson.Read` loads
  this file, `MapUndoJson.Write` produces it. A reader meeting a newer
  `SchemaVersion` fails with a named error, never silently.
- Mismatch note (2026-11-13): `MapDocument` (hammer-map-document-model) had not
  landed when these fixtures were written, so there is no MapDocument-native
  `.effigy-map` file to fixture against yet. When it lands, the final integrator
  adds a round-trip check from a real MapDocument save into this snapshot shape;
  the snapshot shape itself is the contract both sides already build against.
