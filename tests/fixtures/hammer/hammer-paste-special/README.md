# hammer-paste-special fixtures

Acceptance numbers for the Paste Special API
(`kernel/Effigy/Map/MapPaste.cs`, board `hammer-paste-special`).
One check in `tests/MapPasteTests.cs` ("fixture cases recompute") loads
`cases.json` and re-runs every case; the rest of the group builds the
same cases synthetically, so a missing fixture can never hide a
geometry failure.

## Schema: `hammer-paste-special-cases/v1`

- `source`: one box — `min`/`max` corners (inches, Z-up), one material
  name per face in face order (bottom, top, front(-Y), back(+Y),
  left(-X), right(+X)), plus its `volume` and `centroid` for reference.
  Source box: 64 x 32 x 32, centroid (32, 16, 16), volume 65536.
- `cases[]`: one paste per case.
  - `mode`: `at-centre` (one copy at the centre of the original),
    `rotate` (N copies, copy k rotated by k * `angle` degrees about the
    pivot), `offset` (copies moved by `offset`, then grid-snapped).
  - `copies`, `angle` (degrees), `offset` ([x, y, z]), `grid`.
  - `expectedCopies`, `expectedVolume` (every copy keeps the source
    volume), `expectedCentroid` (centroid of the LAST copy).

## Cases

- `at-centre`: 1 copy, no rotation, no offset — lands exactly on the
  original, centroid (32, 16, 16).
- `rotate-90-x4`: 4 copies at 90 degrees about the selection centre.
  The 4th copy (360 degrees) returns to its start orientation and
  centroid (32, 16, 16); every copy keeps volume 65536.
- `offset-10-snaps-16`: offset (10, 0, 0) on grid 16 snaps to 16, so
  the copy centroid is (48, 16, 16), volume unchanged.
