# hammer-check-problems-fix-actions fixtures

Check-for-Problems API (`MapCheck`). Units: inches, Z-up. Fixtures are
loaded by `MapCheckDocumentJson.Load` — see its schema doc in
`kernel/Effigy/Map/MapCheck.cs`.

## Fixture maps (expected full-check results)

- **map-missing-targets.json** — 3 issues, all `MissingTarget`
  (`e_btn` -> `door_01`, `e_trig` -> `relay_missing`,
  `e_relay` -> `counter_missing`). `e_btn2` -> `door_02` is valid,
  `e_special` -> `!player` is an engine special target (always resolved),
  so neither is an issue. No leak graph: the Leak rule is skipped
  (`SkippedRules` contains `"Leak"`). `FixAllOfType(MissingTarget)` fixes
  all 3, creates 3 `info_target` stubs, and a re-check reports 0.
- **map-mixed-issues.json** — 6 issues, one of every type:
  `InvalidBrush` x2 (`b_inverted`: inverted bounds, repairable;
  `b_flat`: zero volume, not repairable — `CannotFix`),
  `MissingMaterial` x1 (`b_badmat`: `missing/mat_xyz`),
  `BadKeyValue` x1 (`e_badkv`: `speed` = `"fast"`),
  `MissingTarget` x1 (`e_missing` -> `ghost_relay`),
  `Leak` x1 (gap portal at (64, 0, 32), point file
  `0 0 32 / 64 0 32 / 80 0 32`). No rule is skipped.
- **map-clean.json** — 0 issues, sealed leak graph, no skipped rules.

## Compile log

- **compile-log.txt** — realistic compile-log lines. The leaked line
  `Entity info_player_start (0 0 32) leaked!` resolves (against
  map-missing-targets) to entity `e_player` at distance 0. The
  `(-32 0 32)` line resolves to entity `e_btn` (exact origin).
  A coordinate of `(64 0 32)` sits inside brush `b_wall` and resolves to it.
