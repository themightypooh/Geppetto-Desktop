# hammer-panels-docked-layout-model fixtures

Docked panel layout data model only — no UI. Read by `tests/PanelLayoutTests.cs`
(group `panellayout`); the fixture dir is found by walking up from the test
binary / working directory to `tests/fixtures/hammer/hammer-panels-docked-layout-model/`.

- `layout-default.json` — what `LayoutDocument.CreateDefault()` produces:
  every default-registry panel at its default zone / size / order, all visible.
  `face-edit` is `FloatingDocked` at (100, 100, 480x360).
- `layout-roundtrip.json` — a lived-in layout: panels in all five dock zones,
  `properties` resized to 320, `face-edit` floating at (120, 90, 480x360),
  `build` HIDDEN but keeping its Bottom zone / 200 size / order 1 (hiding must
  never clear dock position). Used by `roundtrip-from-fixture`.
- `layout-old-version.json` — `schemaVersion` 99. Must load with a named
  `unknown-schema-version` error, never silently. Used by
  `old-version-load-fails-named`.

Schema (version 1): `schemaVersion`, `mapId`, `workspaceId`,
`activePanelId`, `panels[]` with `panelId`, `zone`
(`Left|Right|Bottom|Top|FloatingDocked`), `size` (width for left/right,
height for top/bottom, width for floating-docked), `visible`, `order`,
`floatX`, `floatY`, `floatWidth`, `floatHeight`.
