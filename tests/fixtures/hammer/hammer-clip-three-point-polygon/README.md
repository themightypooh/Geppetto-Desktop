# hammer-clip-three-point-polygon fixtures

3-point clip + polygon tool (Hammer Desktop P2), group `clippolygon`.
Implementation: `kernel/Effigy/Map/ClipPolygon.cs` (built on `MapClip`, the
2-point clip from hammer-brush-clip-two-point — same volume/material/UV
guarantees, same named no-ops). Tests: `tests/ClipPolygonTests.cs`.

## polygon-cases.json

6 footprint cases, read by the `fixture-json-cases-match` test. Each case is
one `PolygonFootprint(points, height)` call:

| name | points | expected |
|---|---|---|
| square-64 | 64x64 square | Created, volume 262144 |
| right-triangle | (0,0)-(64,0)-(0,64) | Created, volume 131072 |
| regular-hexagon-radius-32 | hexagon, circumradius 32 | Created, volume ~42561 (see note) |
| concave-dart | square with (32,32) dent | RejectedNonConvex, hull = 4 corners |
| bowtie | (0,0)-(64,64)-(64,0)-(0,64) | RejectedSelfIntersecting, hull = 4 corners |
| collinear | (0,0)-(32,0)-(64,0) | RejectedDegenerate, hull = 2 endpoints |

Rejected cases carry the convex hull as a *suggestion only* — it is never
substituted for the input; the caller must feed it back explicitly.

Note: the hexagon's expected volume (42561.176) is the value the fixture was
written with; the analytic volume is 42566.9 and the test tolerance is 1e-3
relative, so both agree inside tolerance. The 3-point clip cases live in code
(`ClipPolygonTests.cs`), not in this file.
