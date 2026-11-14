# hammer-propper-brush-to-model fixtures

Board: `hammer-propper-brush-to-model` (Propper++ — brush-built detail
becomes a model). Implementation: `kernel/Effigy/Map/BrushToModel.cs`;
checks: `tests/BrushToModelTests.cs` (group `brushtomodel`).

## cases.json

One case, `trim`: a 4x1x1 bar plus a 1x1x1 riser on top of its +X end.
The numbers are hand-derived, not measured off a run:

- expectedVolume 5.0 = 4 (bar) + 1 (riser). The boxes share exactly one
  face (the bar's top, z=0.5, over x 1..2), so the merged mesh's signed
  volume is exact — the shared faces cancel in the divergence sum.
- expectedFaces 12 = 6 + 6 (no faces are dropped; no material is nodraw).
- expectedCentre (0, 0, 0.5): the selection spans x -2..2, y -0.5..0.5,
  z -0.5..1.5.

The check `fixture cases.json trim volume recomputes` rebuilds this trim
from the JSON and converts it; the remaining checks build the same trim
synthetically, so a missing or edited fixture can never hide a geometry
failure.
