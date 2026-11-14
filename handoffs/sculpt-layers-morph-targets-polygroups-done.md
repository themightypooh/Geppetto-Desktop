# Sculpt layers + morph targets + polygroups — DONE (edits complete)

Board task `godlike-sculpt-layers-morph-targets-polygroups` (Wave 1 P0), owner
muse-3-desktop-d. Edits complete 2026-11-13 ~10:51 EST; marked DONE per Pooh's
10:30 rule (done = edits complete, NOT build-verified; final build verifies,
reopens on failure). Edits only, no build of the app, no commit.

## What it is

Non-destructive sculpt layers stored as displacement deltas on the multires
grid (NOT full mesh copies), morph-target checkpoints that export as blend
shapes, and polygroups / Face Sets. Backend + data model only — no UI panels
(PO override; the layer-list panel belongs to
`godlike-unified-layers-panel-sculpt-paint`).

## Files changed (on the PC, `~/Desktop/geppetto-desktop`)

- NEW `kernel/Effigy/SculptLayers.cs` (888 lines)
  - `SculptLayerBlend` (Add/Subtract/Multiply/Replace — displacement modes
    only; colour modes don't apply to offset vectors).
  - `SculptLayerEntry` (Name, Deltas, Intensity clamped −1..1, per-vertex
    Mask copied+clamped on set, Blend, Enabled, HasDetail, Clone).
  - `MorphTarget` (Name, frame-space Deltas, Weight 0..1 = preview weight
    only; export uses full deltas).
  - `SculptLayerStack` — owns `Base` (snapshot of the level layer at attach)
    + entries. Single `Fold(acc, entry)` primitive serves composition AND
    baking, so bake-all == sequential bake bit-identically for every blend
    mode. Ops: Attach/Add/Duplicate/Branch/Delete/Move/SetActive/SetIntensity/
    SetBlend/SetEnabled/SetMask, HasUnbakedDetail, TopologyChangeWarning,
    HasUncapturedChanges, ApplyStroke, CaptureNewLayer, CaptureMorph/
    DeleteMorph/SetMorphWeight/EvaluateWithMorphs/AddMorphsToMesh,
    BakeCurrent/BakeAll/Flatten, StoredBytes/FullMeshCopyBytes, WriteTo/ReadFrom.
  - ApplyStroke/CaptureNewLayer restore the level layer to a CLONE of Base
    (never the Base object — ApplyEdit mutates deltas in place, aliasing
    would corrupt Base). Both refuse while uncaptured strokes exist outside
    the stack; CaptureNewLayer is the way to take session strokes in.
- NEW `kernel/Effigy/Polygroups.cs` (306 lines)
  - `PolygroupSet`: per-face group ids + names (group 0 = "Default"),
    Assign/AssignFaces/AddGroup (ids never reused)/Rename/NameOf/Groups/
    FacesInGroup/FaceGroups, VisibleFaces/Isolate, Centroid (Pose-brush
    pivot; throws on an empty group), MaskForGroup/ApplyMaskTo (delegate to
    the existing `FaceSets`), FromMaterials, Validate, WriteTo/ReadFrom
    (topology id checked on read).
- EDIT `kernel/Effigy/SculptBlob.cs` (826 lines, was 723)
  - `Version` 2 = max readable; plain `Write(sculpt)` still emits format 1
    (byte-identical). New `Write(sculpt, stack, groups)` emits format 2:
    levels + stack section + groups section, validating stack level/count
    and group face count. `Read(bytes, cage, out stack, out groups)` parses
    format 2, returns nulls for format 1, refuses mismatched stack level /
    vertex count / group topology. WriteLayer/ReadLayer/Quantise/Dequantise/
    Write(Vec3)/ReadVec made `internal static` for the stack serialiser.
- EDIT `kernel/Effigy/Features/SculptFeature.cs` (705 lines, was 614)
  - Fields `_layers`, `_groups`, `_builtLayersRevision`,
    `_builtGroupsRevision`; `LayerStack`/`Polygroups` properties;
    `AttachLayerStack()` / `AttachPolygroups()` (throw if no rebuild yet);
    `LayerTopologyWarning()`; IsStale gains layer/group revision checks;
    SaveDeltas uses v2 Write only when layers/groups present (layer-less
    sculpts save byte-identical v1 blobs); Execute load path assigns layers/
    groups only after a successful Read; compose block after
    `body.Mesh = _sculpt.Evaluate(TopLevel)` and BEFORE Preserve
    (ApplyEntries + AddMorphsToMesh, Fail on level/count mismatch); the
    `!CanRebase` Fail and the Reproject Warn both carry the bake warning;
    Reproject DROPS `_layers`/`_groups` (deltas are per-vertex on the old
    cage — they cannot follow a resample; bake first is the remedy).
- NEW `tests/SculptLayerTests.cs` (404 lines, group `"sculpt-layers"`,
  15 checks): intensity 0/−1/clamp, mask gating + copy-on-set, blend modes
  on a non-zero base, duplicate/branch independence, delete/reorder
  (Replace order matters), bake-current shape preservation, bake-all ==
  sequential bake == flatten, delta storage < full mesh copy at L2, blob v2
  round-trip (layers+morphs+groups, 1e-4 tolerance) + v1 reads with null
  stack/groups, morph checkpoint → `mesh.ValidMorphs`, polygroup
  isolate/hide/mask/centroid/FromMaterials, topology warning fires only
  with unbaked detail, ApplyStroke lands in the active entry + refuses with
  no active entry / over uncaptured changes, CaptureNewLayer moves session
  strokes without moving the shape.

## Deliberately NOT touched

- `SculptSession.cs` — the interactive session still records into the level
  layer; the stack takes strokes over through `CaptureNewLayer`. A session
  stroke-hook (ApplyStroke on every stroke) is a named follow-up, not done
  here. Removed from the board claim's files list.
- `tests/TestRegistry.cs` — coordinator-owned (integrator adds the one line):
  `SculptLayerTests.Register( registry, settings );` after the
  SculptTopologyTests line. NOT registered yet; the test file is on disk.
- No UI panel (unified-layers-panel ticket owns it). No voxel remesh /
  auto-retopo (OpenVDB ticket owns it).

## Verification (edits only, per PO override)

- md5-verified transfer of all 5 files to the PC.
- Every external API grep-confirmed against live PC sources before writing:
  SculptLayer ctor/Deltas/Count, SculptFrames.At/Count/ToWorld,
  BrushUndo.Previous (IReadOnlyDictionary) / Count, BrushStroke/BrushSample,
  MeshMorph fields + ValidMorphs filter, FaceSets.FromMaterials/MaskForSet/
  ApplyToMask, MultiresSculpt ctor/AddLevel/LayerAt/SetLayer/Evaluate/
  FramesFor/TopLevel/Stroke, Feature.Fail (throws) / Warn, PolyMesh
  ctors/Clone/VertexCount/FaceCount, Face.Indices/Material.
- Scratch kernel build in /tmp (`/tmp/sculpt-check/kernel`, dotnet 10.0.402,
  Release): 0 errors and 0 warnings in my 4 kernel files. The build as a
  whole FAILS on siblings' files only (PoseLibrary.cs CS1513/CS1022 syntax;
  duplicate ApplyResult / DualQuat / RigCheckReport across sibling files;
  FeatherFeature deriving from sealed PlumageFeature; plus cascade errors
  in CharacterDna/BuildScriptAnim/etc. once the broken files are excluded).
  NOT my files, NOT touched — final build / integrator owns them.
- Test file compiled clean in the same scratch build (with TestRegistry /
  TestSettings stubs): 0 errors in `SculptLayerTests.cs`. The 15 checks have
  NOT RUN — the full test harness needs the whole kernel to compile, which
  the sibling errors currently block. Integrator runs them.
- Two review fixes caught before transfer: `CaptureMorph` now refuses over
  uncaptured strokes (a checkpoint would silently leave them out);
  `StoredBytes` uses ASCII like the blob writer so the byte count matches
  the real blob (non-ASCII layer names degrade to '?' on round-trip, same
  as the existing blob convention — stream stays aligned).

## Known limits / follow-ups for the integrator or later tickets

- Preserve + morphs: with Preserve on, morph offsets are computed against
  the un-preserved rest frame (off by the rescale factor). Preserve is off
  by default; note it, don't silently "fix" it by rescaling morphs.
- Morph export path is via `PolyMesh.Morphs` — GLB/FBX/DMX writers already
  read `ValidMorphs`; the feature exports nothing itself.
- Layers are dropped (with warning) on Reproject; bake first to keep them.
- Constant/Stepped-style caveat: none — Fold is exact per blend mode.
- `inventory.py check` / parity baseline regen left to the final integrator
  (siblings landing fast all morning).

## Board

Marked DONE 2026-11-13 ~10:51 EST (status done, owner muse-3-desktop-d,
files list updated to the 5 files above — SculptSession.cs and
TestRegistry.cs removed). Keep-going loop after this task: only
final-integrate + virtualized-viewport remain as todos, both blocked
(openvdb-remesh in-progress since 10:31); nothing claimable until
openvdb-remesh or an assemblies task flips to done.
