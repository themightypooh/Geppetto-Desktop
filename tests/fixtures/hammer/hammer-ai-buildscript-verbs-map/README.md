# hammer-ai-buildscript-verbs-map — fixtures + grammar

Board: `hammer-ai-buildscript-verbs-map` — "Hammer Desktop AI: BuildScript verbs
for native map tools." Implementation: `kernel/Effigy/Script/BuildScript.Map.cs`.
Tests: `tests/BuildScriptMapTests.cs` (group `buildscriptmap`). The 20 verbs, in
SEAM order (`tests/fixtures/hammer/hammer-ai-aisuitetools-apicatalog-map/SEAM.md`):

```
map_brush_create, map_brush_edit, map_brush_clip, map_brush_vertex,
map_displacement_create, map_displacement_sculpt, map_face_material, map_face_uv,
map_entity_place, map_entity_edit, map_entity_io_add, map_group, map_visgroup,
map_import, map_export, map_compile, map_validate, map_lightmap_bake, map_navmesh, map_skybox
```

## Grammar

`map_verb [positional] key=value ...` — vectors are `x,y,z`, Vec2s are `u,v`.
The first bare token (no `=`) is the verb's target: brush/entity/displacement id,
or the name for creates. Values with spaces or `;` need double quotes
(`Tokenise` is escape-aware). Unknown options are rejected with the verb's usage
text. Every verb takes `map=name` (default `"map"`); sessions are per-name and
live outside the PartStudio, so they survive AiSuiteTools' round-trip.

Every verb returns a named result in the log or a named error via `Fail` — never
a silent success. A kernel no-op outcome (e.g. clip plane outside the brush) is
logged with its outcome name (`no-op NoOpPlaneOutside`).

## Per-verb reference

- `map_brush_create [name] shape=box|wedge|cylinder|sphere|arch|planes ...`
  box: `min= max=` or `center=/position= size=`; wedge/cylinder/sphere/arch:
  `position=` (centre) + `size=` / `radius= height= segments=` / `radius=
  divisions= segments=` / `radius= height= sweep= segments=`; planes:
  `faces="nx,ny,nz,distance[,material];..."` (≥4 faces). `material=` default
  `dev/dev_measuregeneric01`. `detail=1` marks a detail brush (Quick Play /
  issue projections only — `CompileInput.FromDocument` does not carry detail).
  `id=` optional (default `brush-N`).
- `map_brush_edit brush=id op=move|translate|rotate|scale|snap|setmaterial`
  move: `delta=`; rotate: `axis=x|y|z|x,y,z angle=deg [pivot=]` (default pivot
  is the brush centroid); scale: `factor=n` or `scale=x,y,z [pivot=]`; snap:
  `grid=` (default 1, snaps the bounds-min); setmaterial: `material=` (all
  faces). Rotate/scale go through `MapTransform.TransformFaces` with each
  face's UV locks; faces with no lock keep their UVs as-is.
- `map_brush_clip brush=id a= b= axis=x|y|z | normal= distance= [mode=cut|split]
  [keep=front|back] [cutmaterial=...] [cutnodraw=0|1]` — cut replaces the brush
  with the kept piece; split adds the second piece as a new brush (new id,
  logged); whole-brush-cut-away removes the brush; plane-outside/coplanar
  logs a named no-op and succeeds.
- `map_brush_vertex brush=id vertex=n to=x,y,z | delta=x,y,z` — via
  `MapBrush.ToMesh` → `MapVertexBrush` → `MapVertexEdit`; per-face UVs are
  preserved (unlike the MCP tool, which resets them). An invalid result fails
  with the defects named.
- `map_displacement_create brush=id face=n [power=2|3|4] | corners="x,y,z;..."
  [materialid=0] [power=3]` — brush+face binds to the face's quad corners;
  corners-only creates a standalone displacement (`BrushId ""`, `FaceIndex -1`,
  not tied to a brush: usable for sculpt tests, not for compile). Power 2..4,
  grid `(1<<power)+1`.
- `map_displacement_sculpt disp=id mode=raise|lower|smooth|flatten|paintalpha
  position=x,y,z [radius=64] [strength=0.5] [falloff=smooth|linear|sharp|constant]
  [raiseaxis=facenormal|x|y|z] [paintalpha=0..1]` — persists via
  remove+re-add, so Offsets AND Alphas survive (`SetDisplacement` drops Alphas).
- `map_face_material brush=id face=n|all material=...` — `all` uses
  `doc.SetBrushMaterial`; a single face rebuilds just that face's material
  (UV preserved via `WithMaterial`).
- `map_face_uv brush=id face=n|all [offset=u,v] [scale=u,v] [rotation=deg]
  [lightmapscale=n] [smoothing=n]` — at least one change required.
- `map_entity_place class=... origin=x,y,z [angles=x,y,z] [targetname=...]
  [brush=id1,id2] [key=value ...]` — validated by a transient `MapEntities`
  against the session registry (unknown class / wrong kind / bad keyvalue fail
  with the kernel's named codes). Brush entities need `brush=`; everything
  else in `key=value` form becomes a keyvalue.
- `map_entity_edit entity=id|targetname [origin=x,y,z] [angles=x,y,z]
  [remove=key] [key=value ...]` — origin/angles go through
  `SetEntityProperty` (comma triples are converted to the space triples the
  fold parses).
- `map_entity_io_add source=id|targetname event=... target=name|!special
  input=... [parameter=...] [delay=0] [maxfires=-1]` — the target must resolve
  to a targetname in the doc or be a `!`-special. Rows live in a session
  `EntityIoList` by design (MCP pattern — see "Known gaps").
- `map_group op=define|link|editmember|unlink|list ...` — define:
  `name= brush=id1,id2 entity=id1,id2 [layer=] [visgroup=]`; link:
  `definition= offset= [layer=] [visgroup=]` (linked copies live in the group
  set only — they are NOT added to the document); editmember: `definition=
  member= edit=move|setmaterial|setkeyvalue|rename [delta=|value=|key=]`;
  unlink: `instance=`.
- `map_visgroup op=define|setvisible|assign|list ...` — define: `name=
  [color=#rrggbb|"r g b"]`; setvisible: `name= visible=0|1`; assign: `name=
  brush=id1,id2`.
- `map_import format=vmf|map|gmap file=path | text="..."` — replaces the named
  map (fresh doc, groups, I/O, navmesh, cubemap). VMF/.map import skips
  brushes with <4 usable faces or invalid planes (logged, never silently
  fixed); displacement grids are NOT imported (warned once). Id counters
  advance past imported ids so a later create cannot collide.
- `map_export format=vmf|map|gmap file=path` — `file=` is required; the path
  is recorded in `Result.Written`.
- `map_compile preset=quick|preview|final|quickplay [out=folder]` — quick →
  `CompilePreset.QuickPlay` (BSP-only), preview → Preview, final → Final, via
  `CompileRunner.Run(CompileInput.FromDocument(doc), ...)`; quickplay →
  `QuickPlay.Bake` on the Quick Play projection (sealed check + spawn check;
  a leaky map fails `quickplay-unsealed` with the leak path). An empty map
  fails `compile-empty-map`.
- `map_validate [registry=path]` — `MapCheck.RunFullCheck` over the check
  projection + the live `IssueEngine` over `doc.ToIssueDocument()` +
  `doc.Validate(registry)`, de-duplicated by message. Always returns Ok with
  the issue list in the log — validation reports, it does not fail the script,
  so a leaky room can still reach `map_compile` in the same script.
- `map_lightmap_bake [export=none|obj] [file=basename]` — bakes brushes AND
  displacements (with the doc face's real materials). `export=obj` writes
  `<basename>.obj` (+ .mtl). `export=glb|both` fails `glb-export-stub`:
  `MapBakeExport.ExportGlb` writes a header + asset JSON with no mesh payload
  until the core GlbExport wiring lands.
- `map_navmesh [cellsize=8] [agentradius=16] [agentheight=72] [stepheight=18]
  [maxdrop=72] [maxslope=45]` — real `NavMeshGen.Generate` on the Quick Play
  projection (SEAM's "no builder" claim is stale), then
  `PlaytestLoop.PreLaunchValidation(..., navmeshPresent: true)`. A generation
  failure fails with the `NavMeshException` code; a failed pre-launch fails
  `navmesh-prelaunch-failed`.
- `map_skybox [sky=name] [camera=0|1] [cameraorigin=x,y,z] [camerascale=16]
  [cubemap=0|1] [resolution=32]` — `sky=` sets the compile settings skyname;
  `camera=1` places a `sky_camera` entity; `cubemap=1` bakes via the real
  `CubemapBake.Bake` from `env_cubemap` probes (no probes → named fail
  `cubemap-no-probes`). Without `cubemap=`, reports
  `SkyboxPreview.BuildPreview`.

## Registry

Entity validation needs an `EntityRegistry`. Resolution order: `registry=`
path, then `tests/fixtures/hammer/hammer-ai-mcp-map-tools/entity-registry.json`
by walking up from `baseDir`, then
`kernel/Effigy/Map/registry/core-source1.json`, then an embedded 11-class
registry (the fixture's 10 classes + `env_cubemap`, with fuller
keyvalues/inputs). The chosen source is logged on the first entity verb.

- Neither file registry declares `env_cubemap`, so the embedded class is
  merged into whichever registry resolves (`EntityRegistry.Add`).
- The MCP fixture does NOT declare `light._light` (core-source1 does), so a
  script that sets `_light` needs `registry=` pointing at core-source1.json or
  a fuller registry. The sealed-room script does not set it.

## Known gaps (not this ticket)

- `MapDocument.cs` / `MapDocumentIo.cs`: the `EntityOutput` →
  `EntityIoOutput` rename has LANDED (verified 2026-11-13: zero bare
  `EntityOutput` in either file; both compile in the harness). Document I/O
  rows are session-only by design (MCP pattern), not because the doc is
  broken — the integrator can move them into `doc.AddIo`; the doc's I/O
  cascade on `RemoveEntity` has nothing to cascade.
- Two `MapDocument.cs` defects fixed by this ticket (2026-11-13, backup
  `/tmp/MapDocument.cs.bak-daxter-20261113`): (1) `MapBrushData.Validate`
  tested `|d1-d2|` for opposite normals too, so every symmetric box failed
  its own Validate — directions now split (same-dir `|d1-d2|`, opposite-dir
  `|d1+d2|`); (2) the `RemoveDisplacement` fold matched `d.Id == TargetId`
  (`"brushId#faceIndex"`), which could never match, so `RemoveDisplacement`
  always threw in the fold — it now matches `(BrushId, FaceIndex)`.
- `MapBrushPrimitives` is built against a nonexistent `MapFace`/`MapPlane`
  stub. Shapes are built in `BuildScript.Map.cs` from `MapBrush` +
  `MapBrushFace` (plane builders adapted from `McpMapTools.cs`).
- `MapClip.cs`'s only `MapFace` mention is a doc comment — its code is real.
- `McpMapTools.BuildScriptVerbFor` maps its `map_io_add` tool to the verb
  name `"map_io_add"`, which does not exist — the BuildScript verb is
  `map_entity_io_add` (SEAM). The MCP ticket's seam mapping needs that rename
  if MCP ever routes through BuildScript verbs.
- `CompileInput.FromDocument` does not carry the detail-brush flag, so
  `detail=1` only affects the Quick Play / issue projections.
- Linked `map_group` copies live in the group set only, not in the document.
- Implementation done is NOT runtime verified: no full project build or app
  launch was run for this ticket (PO workflow override). The isolated-harness
  run of the `buildscriptmap` group is the verification on record; the full
  build + official test run are deferred to the final integration gate.
