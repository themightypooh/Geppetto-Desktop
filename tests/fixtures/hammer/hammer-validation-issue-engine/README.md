# hammer-validation-issue-engine fixtures

Live Issue engine (pass 1: detection + fix hints, no auto-fix).
Engine: `kernel/Effigy/Map/IssueEngine.cs`. Tests: `tests/IssueEngineTests.cs`
(group `issueengine`).

## How fixtures work

The maps themselves are built in code in `IssueEngineTests.cs`
(`SealedRoom`, `LeakRoom`, `UnsealedRoom`, `BadTargetRoom`, ...): a
sealed 256x256 room (floor z=0, ceiling z=128, 16-unit walls,
`info_player_start` at (0,0,32), `light` at (0,0,100)). The JSON
files in this directory are the oracles the tests compare `Run()`
against — kind + severity + brush/entity id per issue:

| oracle | map | expected issues |
|---|---|---|
| `clean-room.expected.json` | sealed room | none |
| `leak-room.expected.json` | +X wall split, 1-unit gap at y in [-0.5, 0.5] | 1 Leak (Error) |
| `bad-target-room.expected.json` | trigger_multiple (102) targets `missing_door` | 1 MissingTarget (Error, entity 102) |
| `over-limit-room.expected.json` | sealed room, `MaxBrushes = 4` | 1 LimitExceeded (Error) |

Other cases are asserted in code only (no oracle file): unsealed room
(whole +X wall missing -> 1 UnsealedMap, 0 Leak), duplicate
targetname (warning), missing material (face with no material ->
nodraw hint), entity with no class, twisted-vertex brush (non-planar),
flat zero-volume brush, open brush (missing face -> boundary-edge /
watertight failure via GeometryLinter), incremental `ReCheck`,
registry round-trip, null/empty document, kitchen-sink composition.

## Run

From the repo root (the `Register` line in `tests/TestRegistry.cs`
is added by the final integrator):

```
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter issueengine
```

## Known limits (by design, pass 1)

- Leak / UnsealedMap use `LeakProbe`: 6 axis rays from the player
  start. A gap only a diagonal ray can reach is NOT reported — the
  flood-fill in `LeakFinder` (hammer-19) owns the complete check.
- The engine checks its own `IssueDocument` snapshot (see the header
  of `IssueEngine.cs`): `MapDocument` / `MapBrush` had not landed
  when this was written, so the integrator adapts `MapDocument` onto
  `IssueDocument` with one projection. Do not redefine these types.
- No auto-fix: issues carry a fix hint only. Fixes are
  `hammer-issue-autofix-actions`, switching on `Issue.Code`.
