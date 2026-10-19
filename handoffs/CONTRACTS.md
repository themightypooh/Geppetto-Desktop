# Cross-ticket contracts

## What is already real

| Responsibility | Existing code |
|---|---|
| Source loading/background publication | `DocumentLoader.Start`, `Completion`, `Status`, `Mesh`, `Player` |
| Render extraction and GPU buffers | `ViewportMesh.FromStudio`, `FromFbx`, `Upload`, `DrawBatched`, `ReleaseGpu` |
| Shared lighting/rendering | `SceneRenderer.Draw`, `KeyShadowMap` |
| No-window GPU context/capture | `HeadlessGlContext`, `HeadlessRender.Run`, `CaptureTarget` |
| Playback and skin palette | `MarionettePlayer.LoadClip`, `Time`, `Step`, `WorldPose`, `GpuBones` |
| Exact triangle picking | `MeshPicker.Add`, `Bounds`, `Pick` |
| CPU tests | executable `tests/Desktop.Tests.csproj`, current `tests/Program.cs` |
| CLI parsing | `DesktopOptions.Parse`; unknown flags fail |

## Fixed conventions

- Geometry is in the kernel's **inches and Z-up**. `FbxImport` normalizes FBX units/axes
  once. Do not multiply imported geometry by 2.54 or rotate it again in a generic loader.
  Camhead's documented authoring-frame correction is a specific adapter, not a global rule.
- `Xform` composes transforms as `world = parentWorld * local`. `Skeleton` is
  parent-before-child. Do not replace the skeleton bind transforms while merely posing it.
- `MarionettePlayer.ToGlMatrix` packs for GLSL column-major upload with transpose=false.
  System.Numerics uses row-vector multiplication at its API boundary; do not “fix” a
  transpose based on appearance. Check the existing kernel-skinning agreement test.
- OpenGL clip depth is -1..1 here. `OrbitCamera.ProjectionMatrix` and the shadow map
  explicitly adapt System.Numerics' default 0..1 projection. Preserve this.
- Keyframe indices are integer **frames**; playback `Time` and CLI `--time` are seconds.
  Convert once using clip FrameRate. Paused scrubbing must reach the exact last frame.
- `Face.UVs` are per corner. Hard normals and UV seams can require duplicated GPU vertices.
- Texture image row 0 from `PngReader` is the top; FBX UV V runs upward. Establish one
  documented upload/sampling convention and verify an asymmetric four-corner fixture.
- Color/emission images are sRGB inputs; roughness, metallic, normals and masks are
  linear data. Shader lighting calculations are linear; tone-map then encode once.
- GPU skinning uses the same normalized top four weights as triangle picking. The
  present palette limit is 220 bones. Reject overflow; do not clamp IDs to hide it.

## New types to implement, not APIs that already exist

The names below are the intended coordination contracts. The owning ticket defines the
actual public signatures and tests before dependent workers import them. A worker must
not invent a competing type with the same responsibility.

| Proposed type/service | Owner | Required meaning |
|---|---|---|
| `AssetReference`, `AssetPathResolver` | P1 | project-relative asset identity; explicit external read-only references |
| `DesktopProject` | P2 | versioned manifest, source/clip/material references, project root |
| `MaterialDescriptor`, `TextureBinding` | M1 | CPU-only material/channel description; no GL handles |
| `DocumentSession` | E1 | retained source document + original clip JSON + editable state + revisions |
| `EditHistory` and edit transaction | E2 | reversible commands, grouped drag, saved-state tracking |
| `DocumentSaveService` | E4 | loss-aware serialization and atomic replacement, not direct source writes |
| `PoseEditService`, `DragSession` | G2 | tentative transform delta, commit/cancel through E2 |
| `ExportService` | I1 | explicit capability-checked export from retained source, not render buffers |

### Session ownership

One `DocumentSession` owns each mutable document. Rendering uses a derived snapshot
tagged with session/document ID and revision. A worker job captures immutable inputs and
a revision; a stale result is discarded. GL snapshots are promoted/released by the GL
thread. The old snapshot remains usable while loading or rebuilding a replacement.

Source identity, display name, persistent object/feature IDs and GPU vertex indices are
different concepts. Never save a selection by “the 17th mesh in today's render list.”

### Save and data-loss contract

- Preserve `.effigy` with `StudioDocument`; preserve `.riganim` with
  `RigAnimWriter.Write(clip, originalJson)`, not `Write(clip)` when opening an existing clip.
- Keep unrecognized JSON fields/objects/events on an edit-and-save round trip. Kernel
  writers only know a subset of some formats. A successful parse is not proof of losslessness.
- A raw imported FBX is an external source, not a newly editable `.effigy` file. Store
  supported edits as project/session data or explicitly convert to a new destination.
- Save a consistent captured revision. If edits continue during IO, the completed save
  does not mark the newer revision clean. Failed saves never clear the dirty state.
- Same-directory temp + complete write/flush + atomic rename/replace. Do not delete the
  existing destination first. Multi-file outputs need a staged directory/manifest policy.

### Baseline capability and scope

- Keep OBJ/FBX/DMX capabilities distinct; do not advertise FBX animation because static
  `FbxWriter.WriteBinaryFile` exists. It takes mesh/rig, not an animation clip.
- Preserve supported DMX exports as optional engine targets. Do not require s&box to
  open a desktop project or decide to remove DMX as part of an unrelated task.
- Persistence/model editing must support Effigy as well as animation eventually, but
  tasks are incremental. E1/E2 are shared foundations; E3/E5 cover different authoring paths.
- No new “universal” editor serialization format replacing `.effigy`/`.riganim`.
  A project manifest references these documents and carries desktop-specific state.
