# hammer-interop-shared-material-library fixtures

`library.json` — a shared library with three entries, one per origin story the
ticket covers:

- `brick/brickwall001` — a map material (origin `Map`): brick/concrete floor,
  `$surfaceprop concrete`, textured PBR with a `.vtf` base colour map (the VMT
  export strips the extension).
- `metal/metalwall001a` — a CAD material (origin `Cad`): full metal, one
  combined metallic-roughness map (the glTF export emits a single
  `metallicRoughnessTexture`).
- `tools/toolsnodraw` — a tool material (origin `Map`): a minimal PBR (name
  only, everything else defaults) whose identity is the tool name; it exports
  as a tool marker in both presets, not as a renderable look.

Used by `SharedMaterialLibraryTests` ("fixture library imports").
