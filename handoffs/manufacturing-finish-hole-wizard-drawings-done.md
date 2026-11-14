# Manufacturing finish: Hole Wizard, threads/fasteners, direct-edit/heal, 2D drawings — done

Board task `godlike-manufacturing-finish-hole-wizard-drawings` (Wave 3 P2),
worker muse-3-desktop-12. Claimed 11:05 EST, edits complete same morning.
Edits only, no build, no commit.

## Provenance (read this before assuming who wrote what)

The 5 files were implemented by sibling muse-3-desktop-e (10:38–10:41 EST,
1,956 lines) but the board task sat todo/Unassigned; this worker claimed it
11:05, verified the implementation against the ticket line by line, fixed
2 real test bugs (below), and completed it. The 4 kernel files are the
sibling's work, untouched; only the test file changed here.

## What changed (Desktop only)

- `kernel/Effigy/Features/HoleWizardFeature.cs` (435): ISO metric coarse
  tables M3–M20 (tap drill = D − pitch, ISO 273 clearances, ISO 4762
  counterbores, ISO 10642 countersinks), ISO 286-2 H7 upper deviations;
  HoleSpec + HoleWizardFeature (Feature, mesh-boolean cut via HoleOperation +
  MeshBoolean, B-rep cut honestly reported as BrepPending, never claimed).
- `kernel/Effigy/Features/FastenerLibrary.cs` (261): ISO 4014 hex bolts,
  ISO 4762 socket heads, ISO 4032 nuts, ISO 7089 washers, ISO 15 bearings,
  ISO 3601 O-rings + groove guidance; linear/circular patterns + mirror.
  Specs only — the B-rep core builds the solids.
- `kernel/Effigy/Features/DirectEditFeature.cs` (290): FaceRecord table +
  Move/Delete/Replace face, Offset surface, Modify fillet as parametric
  ops; ImportHealing (naked-endpoint gap stitch, honest GapsRemaining).
- `kernel/Effigy/Features/DrawingDocument.cs` (527): 4 views (front/top/
  section-as-labelled-front-projection/detail), H7 dimensions, hole table,
  BOM, title block, revision, FNV-1a source fingerprint + out-of-date flag,
  real DXF R12 text + minimal valid PDF 1.4 with computed xref; plus
  DrawingSheetFeature (Feature) wiring it into the studio.
- `tests/ManufacturingFinishTests.cs` (443, group "manufacturing", 20 checks):
  ISO numbers, fastener tables, pattern/mirror math, direct-edit ops,
  healing, drawing views/H7/hole table, out-of-date flag, DXF + PDF
  (incl. xref-offset validation), bracket workflow (M6 counterbore cut in
  the studio, volume drops 15000 -> ~14450) and drawing-sheet feature.

## Fixes made here (test-side, implementation was sound)

1. Healing "missing one edge" fixture was wrong: removing one edge from a
   closed box creates NO naked endpoints (every corner keeps two edges),
   so Heal reported 0 gaps and the check would fail. Replaced with two
   dangling edge ends 0.05 mm apart inside the 0.1 mm stitch tolerance —
   one real gap, healed, closed. Comment in the test explains why.
2. PDF xref assertion was off by one: "0 6" is xrefLines[0], not [1]
   (the free entry sits at [1]); the offset loop was already correct.

## Verification (reading + source checks only, no build per PO override)

- All 5 files read in full; every external API grep-confirmed against its
  declaration (HoleOperation.Build signature incl. through/segments,
  MeshBoolean.Apply/Available, Feature.Fail/Warn/Note/Error, FaceRef ctor,
  PartStudio.Add<T> return, Primitives.Box, Xform.RotateAbout/TransformPoint,
  all param ctors, GeometryKind.Face).
- Test arithmetic hand-verified: ISO tables, countersink depth
  (11−6.6)/2/tan45 = 2.2, blind thread depth 10−1.5×pitch = 8.5, bolt head
  = counterbore dia (M6/M8/M10/M12), pattern/mirror coordinates, H7 bands,
  DXF contents, PDF xref offsets, bracket volume bounds.
- Roslyn parse check 5/5 OK (0 diagnostics), md5-verified transfer.
- `inventory.py check` (baseline 10:50, no regen): the 4 kernel files are
  already in the baseline; no drift from this task.

## Integrator flags

- Add `ManufacturingFinishTests.Register(registry, settings);` to
  tests/TestRegistry.cs. TestRegistry.cs deliberately NOT edited here.
- Honest boundaries (in the code, not hidden): fastener/bearing/O-ring
  solids are spec-only until the B-rep core lands; modelled threads record
  a helix profile, mesh cut is the tap drill; section view is a labelled
  front projection, not a computed B-rep section cut; H7 table ends at
  50 mm (larger bores refuse, never invent).
