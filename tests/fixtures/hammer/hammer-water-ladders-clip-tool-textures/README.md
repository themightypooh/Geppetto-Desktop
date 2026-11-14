# hammer-water-ladders-clip-tool-textures fixtures

Water, ladders, clip / invisible geometry via tool textures (native).

- `water-cases.json` — water-brush classify cases for
  `WaterLaddersTests.water-fixture-cases`: one valid pool and four named-defect
  brushes (side face brick, top face brick, water everywhere, water on one
  side). Faces are in `MapBrush.Box` order; see the `readme` field in the file.
- `func-water-analog.json` — the `func_water_analog` registry fragment, in
  `EntityRegistry` schemaVersion 1. Byte-identical to
  `FuncWaterAnalog.RegistryFragmentJson` in
  `kernel/Effigy/Map/WaterLadders.cs` (a test enforces this); the class is not
  in the core registry, so callers merge this fragment into their registry
  with `FuncWaterAnalog.EnsureClass` / `EntityRegistry.LoadAdditional`.
