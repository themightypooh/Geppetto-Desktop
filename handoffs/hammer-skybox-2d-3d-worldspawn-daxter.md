# Handoff — hammer-skybox-2d-3d-worldspawn (daxter-hammer-skybox)

Board: `geppetto-desktop` / `hammer-skybox-2d-3d-worldspawn` — "Hammer Desktop P2:
2D/3D skybox + sky_camera + worldspawn map properties (native)".
Status: **implementation done, NOT runtime verified** (see "Checks" below).
Worker: daxter-hammer-skybox. Repo: `/home/poo/Desktop/geppetto-desktop`, branch
`rigskin-wave1` (all files below are untracked there, like the rest of the Map area).

## What was built

Pass 1 (on disk since 16:29, verified, not re-done here) already covered the
**VmfDocument leg**: `kernel/Effigy/Map/SkyboxWorldspawn.cs` (SkyCamera,
WorldspawnFog, SkyboxBakeData, SkyboxWorldspawn aggregate, worldspawn key
mapping, bake scaling) + `tests/SkyboxWorldspawnTests.cs` + fixtures.
This session added the **MapDocument leg** as a NEW file only — no edits to
`MapDocument.cs` / `MapDocumentIo.cs` (locked by daxter-hammer-map-document):

### Changed files (exact)
- `kernel/Effigy/Map/MapDocumentSkybox.cs` (NEW, ~590 lines) — `public static
  class MapDocumentSkybox` in `namespace Effigy.Map`. API:
  - `FromMapDocument(MapDocument) -> (SkyboxWorldspawn, List<SkyboxWarning>)`
  - `ApplyToMapDocument(SkyboxWorldspawn, MapDocument) -> List<SkyboxWarning>`
  - `ToBakeData(MapDocument) -> SkyboxBakeData`
  - `ToMapBakeBrushes(MapDocument) -> (List<MapBakeBrush>, int Skipped, List<SkyboxWarning>)`
  - `ToSkyCamera(MapEntityData, List<SkyboxWarning>) -> SkyCamera`
  - `ToMapEntity(SkyCamera) -> MapEntityData`
  - `ToVmfBrush(MapBrushData, int vmfId) -> VmfBrush`
  - `FromVmfBrush(VmfBrush, out List<SkyboxWarning>) -> MapBrushData`
  - Warning codes: `WorldspawnNotInMapDocument` =
    `skybox-worldspawn-not-in-map-document`, `SectionDisplacementDropped` =
    `skybox-section-displacement-dropped`, `SectionUvDefaulted` =
    `skybox-section-uv-defaulted`; `SectionVisGroupColour = "#0080FF"`.
  - Slots: SkyName <-> `CompileSettings.SkyName`; sky_camera <-> MapEntityData
    (Origin field, KeyValues["scale"], targetname); section <-> MapBrushData
    in the "3D Skybox" visgroup (visgroup rule ONLY on the Map leg — no
    spatial fallback, documented in the file header).
  - Apply is diff-based/idempotent (history appended only for real changes,
    SameAs-gated); removes sky_camera/section items absent from the
    aggregate; creates the "3D Skybox" MapVisGroup only when section brushes
    exist. Plane rule: export PlanePoints via the
    PlanePointsFromNormalDistance formula (exact by construction);
    StoredVertices written only when the solved loop is within 1e-6 of the
    plane. UV rule: export writes PlaneAxes-derived axes; import recovers
    FaceUv exactly only on a PlaneAxes match, else FaceUv.Default + one
    warning per brush. Displaced faces convert as planes + one warning per
    brush. Zero-normal faces convert faithfully (default axes, no throw).
- `tests/MapDocumentSkyboxTests.cs` (NEW, 13 checks, group `mapdocumentskybox`).
- `tests/fixtures/hammer/hammer-skybox-2d-3d-worldspawn/mapdocument-skybox.gmap`
  (NEW) — hand-written .gmap: sky_day01_01, sky_camera id 3 at (512,512,512)
  scale 16, one 6-face section brush (512..672, NATURE/CLIFF) in the
  "3D Skybox" visgroup, docid a1b2c3d4.
- `kernel/Effigy/Map/registry/core-source1.json` (EDIT) — sky_camera appended
  (15 classes now): point kind, Targetname base, float `scale` default "16".
  File is minified single-line JSON; edit was structural (json load/append/
  dump with sort_keys + compact separators), verified: first 14 classes
  byte-identical in structure.
- `tests/EntityRegistryTests.cs` (EDIT) — counts 14→15 (load, failed-merge,
  LoadDirectory), 15→16 after LoadAdditional, expected array += "sky_camera".
- `tests/SkyboxWorldspawnTests.cs` (EDIT) — two changes: (1) the registry
  check flipped from "sky_camera NOT in registry (integrator follow-up)" to
  asserting it IS present with a float scale + valid/bad-scale validation;
  (2) the pass-1 idempotence check's doc now names a 2D sky — see "Bugs
  found" below.

### The worldspawn gap (named, not silent — the design decision)
MapDocument has NO slot for chapter / title / detail props / fog preset /
structured fog (`.gmap` "op set-compile" carries only the sky name).
- `FromMapDocument` reads those fields as the type defaults.
- `ApplyToMapDocument` warns `skybox-worldspawn-not-in-map-document`
  whenever the aggregate carries a value the Map leg cannot hold: non-empty
  chapter/title/fog preset, present structured fog, or **DetailProps OFF**.
  The DetailProps rule is precise, not "warn when set": the Map leg always
  reads the Source default ON (`WorldspawnProperties.DetailProps` defaults
  true), so only "off" is lost; "on" round-trips by coincidence of the
  default, not by being carried. (First version warned on ON — wrong, and
  it made every clean aggregate warn; fixed during verification.)
- FOLLOW-UP for the MapDocument owner (exact, also in the file header): add
  a worldspawn section to MapCompileSettings (Chapter, Title, DetailProps,
  DetailVbsp, DetailMaterial, Fog preset, fogstart/fogend/fogcolor/
  fogmaxdensity/fogenable), a new `op set-worldspawn ...` history line in
  MapDocumentIo.Write/Read, and the fold case in MapDocument.ApplyEntry;
  v1 files without the line read defaults with a LoadNote. When that lands,
  this file's warning goes away and From/ApplyToMapDocument carry the fields.

## Bugs found + fixed during verification (all in this ticket's files except the last)
1. **Gap-rule over-warning** (MapDocumentSkybox.cs): warned on DetailProps
   ON. Fixed to warn only on OFF (see above).
2. **Vmf id minting bug** (MapDocumentSkybox.cs `FromMapDocument`): the
   caller pre-filled `usedIds` with every parseable section-brush id, so
   `VmfIdForSectionBrush`'s keep-rule (`usedIds.Add(parsed)`) always failed
   and every numeric id was minted as max+1 (id "2" became 3, breaking the
   VMF round-trip test). Fixed: `usedIds` starts empty, `nextFresh = 1`.
3. **Pass-1 idempotence check could never pass** (SkyboxWorldspawnTests.cs):
   its doc has no skyname, so `Validate()` always warns
   `skybox-empty-sky-name` on first/second/third reads alike. Fixed by
   naming a 2D sky in that check's doc only.
4. **My test used a parameterless `VmfDisplacement()` ctor** — none exists
   (ctor is `VmfDisplacement(int power)`, 2..4). Fixed to `new
   VmfDisplacement(2)`.

## NOT fixed (dependency bug, reported, file locked by another worker)
- **MapBrushData.Validate false-positives on origin-centred boxes**
  (`kernel/Effigy/Map/MapDocument.cs`, ~lines 519-524, owned by
  daxter-hammer-map-document — DO NOT fix here). The same-plane check
  treats opposite normals as "same normal" but tests distance with
  `|d0-d1| < eps`, which only identifies same-plane for same-direction
  normals; for opposite normals same-plane is `|d0+d1| < eps`. Net effect:
  any box with min == -max on an axis (e.g. the obvious test room
  -512..512) fails validation with "faces 0 and 1 are the same plane",
  and the else-if "zero-thickness slab" branch misfires the same way
  (opposite normals + equal distances = a normal slab of thickness 2d,
  not zero thickness). My tests work around it with an asymmetric room
  brush (max 384/448/256, noted in the test file). The final integration
  gate must not assume symmetric boxes validate clean until the owner
  fixes this.

## Checks
- **Isolated harness `/tmp/skybox-harness` — 50/50 PASS, 0 failed, 0 skipped**
  (run 2026-11-13 ~17:25 EST, `dotnet
  /tmp/skybox-harness/bin/Release/net8.0/harness.dll`):
  mapdocumentskybox 13/13, skyboxworldspawn 17/17 (incl. the flipped
  registry check and the fixed idempotence check), entityregistry 20/20.
  Harness = verbatim copies of the current repo files for the closure
  (Vec, ConvexHull, MapDocument, MapDocumentIo, MapDocumentSkybox,
  SkyboxWorldspawn, SkyboxPreview, VmfIo, EntityRegistry, EntityIo,
  FaceMaterial, MapBakeExport, MapBrush [minus the trailing
  MapBrushFeature class — Features world], Displacement, StarterTemplates,
  ToolTexture, MapUndo, IssueEngine, PolyMesh, GlTfExporter,
  ExportOptions, MapVertexEdit, GeometryLinter, PaintCanvas, SkinWeights,
  Xform, CatmullClark, ObjWriter, MeshAdjacency-free: MeshNormals,
  Triangulate, registry JSON, both fixture dirs) + 3 harness-only files:
  `StudioDocumentStub.cs` (6 helpers copied VERBATIM from
  StudioDocument.cs so .gmap text is byte-identical), `MapCsgStub.cs`
  (BrushPlane + MapCsg.MeshFromPlanes + 3 helpers copied VERBATIM from
  MapCsg.cs — needed for the spatial fallback test; the full MapCsg.cs
  pulls in the mesh-boolean world), `VertexFacesStub.cs` (real
  VertexFaces shape + Build, verbatim from MeshAdjacency.cs) +
  `NormalBakeStub.cs` (real FaceTriangles + Fan/Clean, verbatim) +
  `RigStubs.cs` (MorphDriver/PaintCanvas minimal stubs — PolyMesh only
  holds them as fields) + trimmed TestRegistry/TestSettings/Program
  (registers only the 3 groups; EntityRegistryTests copy has the unused
  `using Marionette.Desktop;` stripped). Fixtures/registry live at
  harness-root kernel//tests/ so the tests' walk-up RepoRoot finds them.
- **Deferred (NOT run, NOT claimed):** full project build (shared tree
  does not compile — BuildScript.Map.cs missing, MapFace in
  MapBrushPrimitives, duplicate InstanceIoProxy, MapLayers.cs:299,
  ApiCatalog BuildScript.MapVerbNames, old-Displacement MapBrushFace in
  several files — all other tickets'); the official test runner
  (`dotnet run --project tests/Desktop.Tests.csproj -- --filter ...`);
  app launch. Implementation done is NOT runtime verified.
- **Integrator:** add `MapDocumentSkyboxTests.Register(registry,
  settings);` to `tests/TestRegistry.cs` (coordinator-owned — left
  untouched) and delete the harness when the real runner goes green.
- Tunnel note: SSH died twice during this session (egress reset +
  missing `/root/.ssh/config` — the known recurrence); restored from
  `~/workspace/ssh-access/config` and work continued. No repo files were
  touched while the tunnel was down.
