# hammer-entity-place-edit-validate fixtures

## What this ticket is

`kernel/Effigy/Map/MapEntities.cs` — the entity place/edit/validate API plus
tie/untie brushes to brush entities (Hammer Ctrl+T / Untie to World), backend only,
no Desktop UI. Tests: `tests/MapEntitiesTests.cs`, group `mapentities`.

## API (all on `MapEntities`, constructed with a real `EntityRegistry`)

- `PlacePointEntity(class, position, keyvalues?, angles?)` — point classes only.
- `PlaceBrushEntity<TBrush>(class, brushes, keyvalues?, origin?)` — brush classes only.
- `Tie<TBrush>(class, brushes, keyvalues?, origin?)` — Hammer Ctrl+T; same creation
  path as `PlaceBrushEntity`.
- `Untie<TBrush>(entity)` — detaches the brushes (same references, in order) and
  removes the entity.
- `SetKeyvalue(entity, key, value)` / `GetKeyvalue(entity, key)` — registry type
  checks; a bad set is rejected and leaves the stored value unchanged.
- `SetOrigin` / `SetAngles` — origin is a property, not a keyvalue (the registry has
  no implicit `origin` key); `angles` is mirrored into `keyvalues["angles"]`.
- `FindByClass(class)` / `FindByTargetName(name)` / `FindById(id)` / `Remove(entity)`.
- `Validate()` — missing target = `MissingTarget` error; duplicate targetname =
  `DuplicateTargetname` warning; registry keyvalue problems = errors with the
  registry's codes. Targetname matching is case-insensitive (matches EntityIo).

## Error codes

Registry rejections propagate as `EntityRegistryException` with the registry's codes
(`UnknownEntityClass`, `UnknownKeyValue`, `InvalidKeyValueType`). This layer raises
`MapEntityException` with: `NotPointEntity`, `NotBrushEntity`, `NoBrushes`, `NotTied`,
`UnknownEntity`, `BrushTypeMismatch`, `InvalidOrigin`, `InvalidAngles`,
`ReadOnlyKeyvalue`.

## Dependency status at write time (2026-11-13)

- `EntityRegistry` LANDED and is used directly (core registry:
  `kernel/Effigy/Map/registry/core-source1.json`, 14 Source-1 classes).
- `MapBrush` NOT landed: tie/untie take the brush payload as generic `TBrush`, so
  the spec-shaped call compiles today and the same calls take
  `IReadOnlyList<MapBrush>` once it lands. The test file uses a `TestBrush` stand-in
  for the same reason. When `MapBrush` lands the integrator may add a non-generic
  `PlaceBrushEntity(string, IReadOnlyList<MapBrush>)` overload that defaults the
  origin to the brushes' centroid (today it defaults to (0, 0, 0) unless passed).

## Files in this folder

- `placement.json` — four entities placed by the fixture test: a player start, a
  `light_spot` targeting missing `ghost_sign`, and two `func_door`s sharing
  targetname `door_main`. `Validate()` must report exactly one `MissingTarget`
  error and one `DuplicateTargetname` warning.

## Run

From the repo root (register line added temporarily by the worker, then removed):

```sh
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter mapentities
```
