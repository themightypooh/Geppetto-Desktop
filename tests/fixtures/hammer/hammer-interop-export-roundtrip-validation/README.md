# hammer-interop-export-roundtrip-validation fixtures

Sealed room for the export round-trip suite (`exportroundtrip` group,
`tests/MapExportRoundtripTests.cs`, harness
`kernel/Effigy/Map/MapExportRoundtrip.cs`).

- `room.vmf` / `room.map` — the same sealed room, generated from the same
  box math as `MapExportRoundtrip.SealedRoom()`: interior 256x256x128,
  16-unit walls (brushes 1-6), one crate with fractional, non-dyadic
  coordinates (brush 7: min -48.125,16.375,0 / max -16.0625,48.5,64.1),
  `info_player_start` + `light`. 7 brushes, 42 faces, 4 materials, 2 entities.
- Faces per brush in -X, +X, -Y, +Y, -Z, +Z order; default UV axes
  (U [1 0 0 0] 0.25, V [0 -1 0 0] 0.25) on every face.
- No visgroups / cameras / viewsettings / editor blocks: the builder has
  none, and the VMF comparison counts unrecognised top-level blocks.

Regenerate (do not hand-edit): the generator script is kept with the
worker notes, not in the repo — the test "checked-in fixtures match the
builder" proves the files agree with the builder exactly. If the builder
changes, regenerate both files from the builder's box math and re-run
that test.
