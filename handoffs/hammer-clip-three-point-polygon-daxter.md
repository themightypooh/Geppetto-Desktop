# hammer-clip-three-point-polygon — handoff (daxter-hammer-clip-3pt)

Ticket: **hammer-clip-three-point-polygon** — Hammer Desktop P2: 3-point clip +
polygon tool (native). Status: **implementation done, NOT runtime verified**
(per PO workflow override 2026-11-13: no full project build or app launch;
full build + official test run deferred to the final integration gate).

## What was already on disk when I claimed it

The implementation was already written by an earlier session (files created
2026-11-13 16:23, untracked, board still todo/unassigned, no TestRegistry
registration, no done note): `kernel/Effigy/Map/ClipPolygon.cs` (488 lines),
`tests/ClipPolygonTests.cs`, `tests/fixtures/hammer/hammer-clip-three-point-polygon/polygon-cases.json`.
I adopted it, reviewed it against the spec and the landed APIs, and found
**3 real bugs** in it (below). I did NOT reimplement from scratch and did NOT
touch `MapClip.cs` (worker 06's file) or `MapBrushPrimitives.cs` (broken stub
API, its owner's ticket).

## Bugs found by verification, all fixed in this ticket's files

1. **Mid-edge-point footprint threw** (`polygon-mid-edge-point-still-builds`
   repro): a convex footprint with a redundant point on a straight edge
   (e.g. square with (32,0) on the bottom edge) emitted two faces on one
   plane, so `MapBrush.Validate` reported `DuplicateFace` and
   `PolygonFootprint` threw `InvalidOperationException`. Fix: new
   `DropCollinearVertices()` drops consecutive collinear vertices before
   building faces (same epsilon as `TurnSign`); the solid is unchanged.
   Regression test added.
2. **Bowtie misclassified as degenerate**: a self-intersecting bowtie has
   exactly zero NET shoelace area (lobes cancel), and the old code rejected
   on `|area| <= 1e-9` before the self-intersection check, so it reported
   `RejectedDegenerate` instead of `RejectedSelfIntersecting` (test + fixture
   case both failed). Fix: the zero-area degenerate check is now
   `AllTurnsZero()` (all points on one line); anything else with no net area
   falls through to the self-intersection check. Collinear footprints still
   report `RejectedDegenerate` with "zero-area" in the reason.
3. **Preview outside-plane case was wrong** (test + doc, not the
   implementation): the test expected `RemovedVolume = 0` for a plane at
   x=100 with default `KeepFront=true`, but the brush sits entirely on the
   CUT side there, so committing the cut removes the WHOLE brush
   (`WholeBrushCutAway` per `MapClip`) and the honest preview is
   `RemovedVolume = full volume`. The `Preview` doc sentence claiming
   "removed is 0 when the plane misses" was also wrong. Fix: doc corrected;
   test now asserts the kept-side case with `KeepFront=false` (removed 0,
   mirrors the clip no-op test) AND the cut-side case with default options
   (removed = full volume, kept 0, fraction 1).
4. **`MapBrushFace` bound to the wrong type**: `ClipPolygon.cs` lives in
   namespace `Effigy.Map`, and `Displacement.cs` also defines an
   `Effigy.Map.MapBrushFace` (a copy-only alias with a 10-param ctor), so
   unqualified `MapBrushFace` did not compile against the landed
   `Effigy.MapBrushFace` — same bug class the export-roundtrip worker fixed
   in `MapExportRoundtrip.cs:209`. Fix: every `MapBrushFace` use in
   `ClipPolygon.cs` and in `ClipPolygonTests.cs` is spelled
   `global::Effigy.MapBrushFace`, with a comment saying why. The duplicate
   itself is NOT removed here — `Displacement.cs` is another ticket's file
   and `HintSkip`/`MapExportRoundtrip`/`OverlaysDecals` still build against
   the old face; the gate owns reconciling it.

## Changed files (real tree only)

- `kernel/Effigy/Map/ClipPolygon.cs` (542 lines): `PlaneFromThreePoints`,
  `Clip` (3-point, delegates to `MapClip.Clip`), `Preview`
  (`ThreePointClipPreview`: plane + front/back/kept/removed volumes +
  fraction), `PolygonFootprint` (2D `Vec2` + 3D `Vec3` overloads),
  `ConvexHull`, `FootprintArea`; outcomes `PolygonFootprintOutcome`
  (Created / RejectedNonConvex / RejectedSelfIntersecting /
  RejectedDegenerate) with the convex hull as a suggestion, never silently
  substituted. No UI. Fixes (1), (2), (3-doc), (4) above, plus the
  `DropCollinearVertices` / `AllTurnsZero` helpers.
- `tests/ClipPolygonTests.cs` (425 lines, group `clippolygon`, 21 checks):
  3-point plane construction + degenerate input, 3-point clip halves a
  64-cube (131072) + split volumes sum + horizontal clip + outside no-op +
  UV preservation, preview (half + kept-side outside + cut-side outside),
  polygon square->box (262144) + triangle prism + clockwise input + hexagon
  + 3D points/base-Z + concave rejected with hull + self-intersecting
  rejected + degenerate rejected + bad height throws + hull helper +
  mid-edge-point regression + fixture-JSON case match.
- `tests/fixtures/hammer/hammer-clip-three-point-polygon/polygon-cases.json`
  (6 cases, unchanged) + `README.md` (new: case table + the hexagon
  expected-volume note — fixture says 42561.176, analytic is 42566.9, both
  inside the 1e-3 relative tolerance).
- `tests/TestRegistry.cs` NOT edited (coordinator/integrator-owned): the
  integrator adds `ClipPolygonTests.Register( registry, settings );`.

## Checks run (isolated harness, /tmp/clip3-harness)

The shared tree does not compile as a whole (see Blockers), so — same pattern
as the interop-cad worker — I ran the group in an isolated harness:
verbatim kernel files (267, auto-closed from the clip/polygon dependency
closure) + verbatim `ClipPolygonTests.cs` (minus its unused
`using Marionette.Desktop;`, which has no types in the harness) + verbatim
trimmed `TestRegistry.cs` (`DesktopTestHost` removed — it references the
whole suite; `TestSettings.SurveyPath` default neutralized, the tests never
read settings) + verbatim fixture. `Displacement.cs` IS included, so the
`MapBrushFace` binding fix is genuinely exercised.

- Pre-fix repro (original 16:23 `ClipPolygon.cs` + new tests, no
  `Displacement.cs`): **17 passed, 4 failed** — the mid-edge
  `DuplicateFace` throw, the preview outside case, the bowtie
  misclassification (direct test + fixture case). All three bugs reproduced.
- Post-fix (fixed files + `Displacement.cs`): **21 passed, 0 failed,
  0 skipped; 21 selected; exit 0** (`dotnet run -c Release` in
  `/tmp/clip3-harness`). Harness sources are exact copies of the real-tree
  files (verified by `diff`, except the one unused-using line in the test
  copy). Harness staging/closure scripts (for the record, not part of the
  ticket): `/tmp/clip3-harness-setup.py`, `/tmp/clip3-harness-close.py`.

## Checks deferred (final integration gate)

- Full project build; official test run
  (`dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter clippolygon`)
  with `ClipPolygonTests` registered in the real `TestRegistry.cs`.
- No app launch, no viewport/UI check (no UI in this ticket — 3D clip
  handles and the polygon drawing UI are deferred to a UI ticket).

## Blockers / shared-tree breakage (NOT this ticket's files, do not fix here)

- `MapBrushPrimitives.cs` still builds against nonexistent `MapFace` /
  `MapPlane` (CS0246) — its owner's ticket.
- `InstanceIoProxy` defined twice (`MapInstances.cs` Inputs/Outputs variant
  vs `InstancesCollab.cs` Name/Forward/Backward variant) — gate to reconcile.
- `MapDocument.cs` / `MapDocumentIo.cs` use `EntityOutput` where the landed
  type is `EntityIoOutput` (+ `MapDocument.cs` lacks `using System.IO;`).
- `Displacement.cs` defines the duplicate `Effigy.Map.MapBrushFace`
  (see bug 4); `HintSkip.cs`, `MapExportRoundtrip.cs`, `OverlaysDecals.cs`
  still build against it.
- `MapInstanceLibraries.cs` CS0050, `MapLayers.cs:299` CS0173,
  `ApiCatalog.cs` references nonexistent `BuildScript.MapVerbNames`.

## Known edges (mine, documented, not hidden)

- A microscopic footprint (area so small its extruded volume is at/below
  `MapBrush.VolumeEps` = 1e-6 cu in) throws `InvalidOperationException`
  ("generator bug") instead of a named rejection — no mapper will hit
  1e-9-inch features, and no test covers it.
- The hexagon fixture volume (42561.176) differs from analytic (42566.9) by
  0.013%, inside tolerance; noted in the fixture README.

## Safety notes

- All real-tree writes were rename-replace via a temp file (these files are
  hardlinked to `/home/poo/iaf-scratch-copy/` — never written in place).
- The frozen full-tree copy `/tmp/clip3-verify` (which carried copy-only
  deletions/renames for its own build attempts) was deleted after the
  harness run so nobody copies from it. `/tmp/clip3-harness` is kept as
  the run evidence.
