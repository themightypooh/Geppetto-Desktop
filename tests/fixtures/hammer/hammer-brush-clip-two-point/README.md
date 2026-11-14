# hammer-brush-clip-two-point fixtures

2-point Clip tool for brushes (`kernel/Effigy/Map/MapClip.cs`, tests in
`tests/MapClipTests.cs`, group `mapclip`). Backend only; no UI.

## `clip-cases.json`

Data-driven clip cases, read by the `fixture-json-cases-match` test. Each case:

- `brush`: `box64` — the 64x64x64 box with corners (0,0,0)–(64,64,64),
  volume 262144 — or `wedge` — the triangular-prism wedge built by
  `MapClipTests.Wedge()` (volume 131072).
- `a`, `b`: the two points the mapper drew, in model units (inches, Z-up).
- `axis`: the view axis the line was drawn in (`X` = side view, `Y` = front
  view, `Z` = top view). The clip plane contains the line and runs parallel
  to this axis.
- `keepFront`: which side is the white (kept) side for a Cut.
- `mode`: `cut` or `split`.
- `expectedOutcome`: the named `ClipOutcome`.
- `expectedKeptVolume`: kept-brush volume for a real cut/split (front side
  for a split); null for a no-op, where the kept brush is the original.
- `expectedSecondVolume`: back-side volume for a split; null otherwise.

Expected volumes are exact (a diagonal cut of the box is exactly half);
the test allows 1e-3 relative tolerance for float plane math.

Cases: a diagonal top-view cut of the box (exactly half), a vertical
front-view cut (exactly half, other orientation), the same diagonal as a
split (two halves that sum to the original), a vertical front-view cut of
the wedge (98304 = the x<=32 slice of the triangular prism), and a plane
outside the box (named no-op).
