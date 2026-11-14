# Fixture: hammer-brush-primitives-block-tool

Documentation fixture for the block-tool primitives (`MapBrushPrimitives`).
No test loads this directory; the numbers below are the analytic expectations the
tests in `tests/MapBrushPrimitivesTests.cs` assert against.

## Conventions

- Kernel inches, Z-up. Every primitive is centred on its own origin before
  `Position` is applied; `Scale` is applied about the origin, then `Rotation`,
  then `Position` (same order as `PrimitiveFeature`).
- Faces are planes, outward unit normal, `normal . x = distance`, interior
  `normal . x <= distance`. Every face plane is tangent to (or exactly on) the
  analytic solid, so each brush CIRCUMSCRIBES its analytic shape: volume is
  above the analytic value and converges from above as facets increase.
- Material is the `PrimitiveFeature` material SLOT (int), not a name.
- Default UV: offset (0,0), scale (1,1), rotation 0. Default lightmap scale 16
  (Source default). Smoothing group 0.

## Expected values (measured with an independent plane-intersection model)

| Primitive | Faces | Analytic volume | Brush / analytic |
|---|---|---|---|
| Box 128x128x64 | 6 | 1,048,576 exact | 1.0000 |
| Wedge 128x64x32 | 5 | 131,072 exact (half box) | 1.0000 |
| Cylinder r32 h64, 8 sides | 10 | pi*32^2*64 = 205,887.4 | 1.0548 |
| Cylinder r16 h32, 16 sides | 18 | pi*16^2*32 = 25,735.9 | 1.0131 |
| Sphere r16, div 4 / seg 16 | 50 | 4/3*pi*16^3 = 17,157.3 | 1.1000 |
| Sphere r16, div 8 / seg 32 | 226 | 17,157.3 | 1.0231 |
| Arch R64 h32, 180 deg / 8 | 11 (8 outer + 1 diameter + 2 caps) | 1/2*pi*64^2*32 = 205,887.4 | 1.018 |
| Arch R64 h32, 90 deg / 4 | 8 (4 outer + 2 ends + 2 caps) | 1/4*pi*64^2*32 = 102,943.7 | 1.0235 |

## Deliberate non-goals

- Hollow arch (inner radius > 0): non-convex, cannot be one brush. Build it from
  several brushes (CSG ticket); `Arch` has no inner-radius parameter.
- Arch sweep > 180 degrees: concave; rejected with `ArgumentOutOfRangeException`.
- Sphere `divisions = 1`: would be two parallel pole planes (an unbounded slab);
  rejected. `PrimitiveFeature` allows Divisions 1 for meshes; brushes need >= 2.
- At exactly 180 degrees the two arch end caps coincide into ONE diameter plane
  (x = 0, outward normal (-1,0,0)). Emitting both would be a duplicate face,
  which the core `Validate()` rejects.
