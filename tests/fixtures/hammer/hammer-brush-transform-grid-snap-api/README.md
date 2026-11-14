# hammer-brush-transform-grid-snap-api fixtures

- `box-64.json` — canonical 64-unit box brush (-32..32), 6 faces as
  normal + distance + material id + UV mapping (world-space axes, offset,
  scale, rotation), plus the spec's expected numbers: move 10 on grid 16
  snaps to 16; rotate 17 deg with 15 deg snap gives 15; texture lock keeps
  the +Z face UV (2, 3) at the moved point (12, 3, 32) after a +10 X move
  (unlocked it reads (12, 3)).
- The `maptransform` test group loads `box-64.json` and checks it against the
  in-code box in `tests/MapTransformTests.cs`; that one check SKIPs when the
  file is not found under the repo root (the other 19 checks are in-memory).

API under test: `kernel/Effigy/Map/MapTransform.cs` (`MapTransform` +
`MapUvMapping`, namespace `Effigy`). MapBrush itself has not landed yet
(hammer-brush-core-plane-based-brushes, worker muse-3-desktop-hammer-01), so
the brush-level operations here work on face-plane lists + UV mappings — the
representation the core spec defines — and the file header sketches the thin
`MapBrush.FromPlanes` adapter the final integrator adds once MapBrush lands.
Run the group (temporary `TestRegistry.cs` Register line, removed afterwards):

    dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter maptransform
