# hammer-issue-autofix-actions fixtures

Acceptance numbers for `kernel/Effigy/Map/IssueAutoFix.cs` and
`tests/IssueAutoFixTests.cs` (board: hammer-issue-autofix-actions).

- `cases.json` — the L-prism split (footprint, volume 7, pieces 6 + 1 with
  exact bounds), the inverted-bounds brush (volume 262144), the missing-target
  stub (class `info_target`, flag `mapcheck_stub=1` — the landed MapCheck
  convention), the missing-material replacement (`tools/toolsnodraw`), and the
  over-limit contributors.
- The tests build the same cases synthetically and only use this file to
  cross-check the numbers, so a missing fixture can never hide a geometry
  failure; one check asserts the file itself parses and matches.

Dependency note (2026-11-13): the Issue engine
(hammer-validation-issue-engine), MapUndo, MapBrush and MapCheck have all
landed, so the fix consumes the landed engine types (`Issue` /
`IssueDocument`) and the stub flag follows the landed MapCheck convention
(`mapcheck_stub=1`). MapDocument HAS landed (it implements
IMapUndoTarget and projects via ToIssueDocument()), but its ids are 8-hex
Guid strings while IssueDocument ids are ints, so the integrator must pass
the index-to-id maps when wiring a real MapDocument; the tests still build
their cases synthetically and load no engine-produced fixture map.
