# Geppetto Godot Plugin: Status & What Is Left (2026-10-28)

This document details the exact status of the Geppetto-as-a-Godot-plugin port, what has been completed and verified on pooh's PC, and the breakdown of all remaining tasks across backend and UI.

---

## 1. Executive Summary & Verification State

- **Branch**: `godot-plugin-baseline` (Git repository at `/home/poo/Desktop/geppetto-desktop`)
- **Target Platform**: Godot 4.7.2 .NET Edition (`/usr/bin/godot-mono`) & `poohdot` custom fork
- **Architecture Principle**: Geppetto Core (`core/Geppetto.Core.csproj`) remains completely engine-free with zero Silk.NET or OpenGL dependencies.
- **Verification Result**: Headless automated test suite passes with **zero errors and zero warnings**:
  - **G3** (Mesh Bridge): 6,332 triangles, 12,664 vertices, verified bounds, unit normals, inverted tangent chirality.
  - **G4** (Skeleton Bridge): 4 bones, parent hierarchy, rest origins, 4 skin binds.
  - **G5** (Blend Shapes): Multi-surface morph target deltas mapped.
  - **G6** (Material Bridge): StandardMaterial3D mapping for PBR channels (Albedo, Roughness, Metallic, Emission, Normal, AO), extended lobes (Clearcoat, SubsurfScatter, Refraction, Rim, Anisotropy), and texture channel caching.
  - **G7** (Clip Bridge): AnimationLibrary with sampled Position3D, Rotation3D, and Scale3D tracks.
  - **G8** (Scene Importer): `.effigy` format importer producing full Node3D scene with Skeleton3D, MeshInstance3D, materials, and AnimationPlayer.
  - **G9** (Live Document Service): Background thread rebuild queue with cancellation tokens, revision dispatching, and `EffigyUndoRedoAdapter` bridging Geppetto's `EditHistory` to Godot's `UndoRedo`.

---

## 2. Complete Ticket Matrix (G0 to G15)

| Ticket | Scope | Status | Verification / Artifacts |
| :--- | :--- | :---: | :--- |
| **G0** | Baseline snapshot of tree | **DONE** | Commit `572ec62` on `godot-plugin-baseline`. CPU verify tests recorded (766 passed, 18 failed due to display/healer absence). |
| **G1** | Spike: Kernel in Godot plugin | **DONE** | Commit `cb0db20`. Verified headless rebuild in Godot 4.7.2. |
| **G2** | Core split: pure `Geppetto.Core` | **DONE** | Commit `28a76d1`. `core/Geppetto.Core.csproj` created with 0 engine dependencies. Desktop app & tests pass. |
| **G3** | Mesh bridge (`CpuMesh` → `ArrayMesh`) | **DONE** | Commit `e9ef82d`. `godot-plugin/addons/geppetto/EffigyMeshBridge.cs`. $M$ transform, CW winding, tangent chirality. |
| **G4** | Skeleton & Skin bridge | **DONE** | Commit `e9ef82d`. `godot-plugin/addons/geppetto/EffigySkeletonBridge.cs`. Skeleton3D rests + Skin bind matrices. |
| **G5** | Blend shape bridge | **DONE** | Commit `e9ef82d`. Integrated in `EffigyMeshBridge.cs`. Multi-target delta positions and delta normals per surface. |
| **G6** | Material bridge (`StandardMaterial3D`) | **DONE** | Commit `0d81c9f`. `godot-plugin/addons/geppetto/EffigyMaterialBridge.cs`. Full PBR, extended lobes, texture decoding, ps1_lit warnings. |
| **G7** | Clip bridge (`.riganim` → `Animation`) | **DONE** | Commit `e9ef82d`. `godot-plugin/addons/geppetto/EffigyClipBridge.cs`. Bone tracks, frame stepping, loop modes. |
| **G8** | Scene Importer (`EditorSceneFormatImporter`) | **DONE** | Commit `e9ef82d` & `0d81c9f`. `godot-plugin/addons/geppetto/EffigySceneImporter.cs`. Full scene graph import. |
| **G9** | Live document service & Undo adapter | **DONE** | Commit `0d81c9f`. `godot-plugin/addons/geppetto/EffigyLiveDocumentService.cs`. Task.Run rebuild, UndoRedo adapter. |
| **G10** | Pose and IK backend on Skeleton3D | **TODO** | Pose evaluation writeback to Skeleton3D; comparison of Geppetto IK (`LiveIkSolverBuffers`) vs Godot IKModifier3D; auto-keying. |
| **G11** | Control registry and search index | **TODO** | Data-only catalog of all 118 feature capabilities and parameters from `ApiCatalog`; keyword search index; draft labels. |
| **G12** | Saving back to `.effigy` / `.riganim` | **TODO** | Document serialization from Godot session using `DocumentSaveService` (source of truth remains `.effigy` text). |
| **G13** | GLB export bridge | **TODO** | Portable `.glb` export via Godot's `GLTFDocument` for external engines (Roblox, Unreal, Blender). |
| **G14** | Verification harness & JSON report | **TODO** | Automated CLI runner producing structured JSON benchmark reports (triangles, verts, bone count, import timings). |
| **G15** | Addon packaging & export setup | **TODO** | Final `plugin.cfg`, export presets, documentation on enabling in standard Godot and poohdot. |

---

## 3. Detailed Breakdown of What Is Left

### Category A: Remaining Backend Tickets (G10 – G15)

#### 1. G10: Pose and IK Backend
- **Objective**: Direct authoring of skeletal poses and IK goals from Godot onto `Skeleton3D`.
- **Tasks**:
  1. Create `EffigyPoseBridge.cs` to write sampled kernel poses directly into Godot bone poses (`SetBonePosePosition`, `SetBonePoseRotation`, `SetBonePoseScale`).
  2. Implement IK solve execution: Evaluate Geppetto's `LiveIkSolverBuffers` / `IkTargetService` (fast two-bone, CCD, and FABRIK solvers) and compare accuracy/performance against Godot's built-in `SkeletonIK3D` / `TwoBoneIK3D`.
  3. Wire auto-keying: Changing a bone transform or dragging an IK handle writes keyframes through `ClipEditService` as a single undoable transaction.

#### 2. G11: Control Registry & Search Index (Data Only)
- **Objective**: Expose Geppetto's full parametric authoring capabilities to future UI panels without hardcoding or widget coupling.
- **Tasks**:
  1. Create `EffigyControlRegistry.cs` by reflecting over `Effigy.ApiCatalog`.
  2. Index all 118 feature types (e.g., `TorsoFeature`, `LimbFeature`, `FleshMaterialFeature`, `HairStrandFeature`, `EyeFeature`) and their editable properties.
  3. Build a fast keyword and synonym search index (e.g., searching "waist" finds Torso waist parameters; "roughness" finds material lobes).
  4. Keep all labels and tooltips as unfinalized drafts marked specifically for pooh's review.

#### 3. G12: Saving Back (`DocumentSaveService`)
- **Objective**: Allow edits made within the Godot session to serialize back to clean `.effigy` and `.riganim` files on disk.
- **Tasks**:
  1. Create `EffigySaveBridge.cs` connecting Godot editor save actions to `core/DocumentSaveService.cs`.
  2. Ensure `.effigy` human-readable formatting, version headers, and comments are preserved.
  3. Verify round-trip test: `File -> Import -> Mutate -> Save -> Reopen` produces an identical document without loss.

#### 4. G13: GLB Export Bridge
- **Objective**: One-click export of rigged, skinned, and animated models to Roblox, Blender, or external tools.
- **Tasks**:
  1. Create `EffigyGlbExporter.cs` leveraging Godot's `GLTFDocument` and `GLTFState`.
  2. Convert imported Geppetto `Node3D` hierarchy to GLTF format with embedded textures and animations.
  3. Verify compliance with Khronos glTF Validator.

#### 5. G14: Verification Harness & Benchmark Reporter
- **Objective**: Standardized verification tool for testing character imports across upstream Godot and `poohdot`.
- **Tasks**:
  1. Create `godot-plugin/spike/Harness.cs` executed via `/usr/bin/godot-mono --headless --script ...`.
  2. Output structured JSON: import time (ms), surface count, vertex count, triangle count, bone count, clip names, frame rate, and any material warnings.
  3. Run against both `g1-spike.effigy` and complex rigged characters.

#### 6. G15: Addon Packaging
- **Objective**: Turn `godot-plugin/addons/geppetto/` into a drop-in plugin for any Godot 4.7.2 / `poohdot` project.
- **Tasks**:
  1. Complete `plugin.cfg` metadata.
  2. Verify autoload and plugin toggle in `Project Settings -> Plugins`.
  3. Write installation and build instructions for `poohdot`.

---

### Category B: UI Design & Implementation (On Hold for Pooh's Sign-Off)

Per project guidelines, **no UI widgets, panels, docks, or inspectors are to be built until pooh approves the designs**:

1. **Dock Layout Positioning (`editor_layout.cfg`)**:
   - Pooh has 6 placeholder docks in `godot-ui-mock`:
     1. `Geppetto: Start`
     2. `Geppetto: Shape`
     3. `Geppetto: Dress`
     4. `Geppetto: Rig`
     5. `Geppetto: Animate`
     6. `Geppetto: Check and send`
   - *Status*: Waiting for pooh to arrange the docks in Godot and save the layout.
2. **Individual Panel Control Mockups**:
   - Design controls one panel at a time directly in Godot.
   - Pooh signs off on buttons, sliders, tree views, and menus before real data binding is hooked up.
3. **Viewport Gizmos & 3D Manipulators**:
   - Handle dragging for bones, IK goals, and parametric landmark points.

---

### Category C: Engine & Fork Coordination (`poohdot`)

1. **Claude's Source Build**:
   - Claude is compiling the custom `poohdot` engine from source.
2. **Engine Independence Validation**:
   - Verify that all C# binaries built in `godot-plugin/` load and execute identically under both `/usr/bin/godot-mono` (v4.7.2.stable) and `/path/to/poohdot`.
   - Maintain zero engine-specific internal hacks in `Geppetto.Core`.

---

## 4. Suggested Next Step

Continue with the remaining non-UI backend tickets in order:
`G10 (Pose/IK) → G11 (Control Registry) → G12 (Save Bridge) → G13 (GLB Export) → G14 (Verification Harness) → G15 (Packaging)`.
This will leave the backend 100% complete, tested, and waiting for pooh's UI layout approval.
