# Fixtures — hammer-interop-shared-scene-layer-model

Shared scene / layer model bridge (`kernel/Effigy/Map/SceneMapBridge.cs`).
The map side here is the FILED SPEC shape, not a canonical type: the
layers-model worker owns `MapLayers.cs` / `MapDocument.cs` and the instances
worker owns `MapInstances.cs`; neither existed when the bridge was written.
These fixtures are written in the spec shapes so the integrator can swap in
the real types without changing the bridge or the tests.

## scene-with-map-ref.gtscn

A canonical `.gtscn` text scene (see `core/TextScene.cs`) that references a
map document. The reference is an `asset` record with Kind `map`:

```
asset	room01	map	maps/room01.gmap
```

It is placed by the node whose Name equals the asset id (`room01`), on the
`Map` layer, with an `xform`. That node-name rule is the pairing convention
`SceneMapBridge.ParseGtscnMapReferences` uses. The file is canonical text:
`TextSceneDocument.Parse` + `ToText()` re-emits it byte-identical.

## layers.json

Two map layers and one visgroup in the filed layers-spec shape:

- layer: `id`, `name`, `color` (`#RRGGBB`), `visible` (eye toggle),
  `compileVisible` (compile-visible flag).
- visgroup: same fields plus `members` — string ids of the brushes /
  entities in the set.

`Geometry` is visible + compile-visible; `Detail` is hidden + compile-
invisible (visibility and the compile flag are independent); `func_detail`
is a visgroup with three members.
