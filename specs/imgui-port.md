# Spec: Marionette ImGui.NET Port (`imgui-port.md`)

This specification documents all 34 files (18,387 lines) in `/home/poo/games/projects/geppetto/Editor/RigControlEditor/` and their relationships to `/home/poo/games/projects/geppetto/Code/RigControl/` and `/home/poo/games/projects/geppetto/Effigy/Rig/`. It is designed so Lane B can implement the standalone Linux OpenGL 4.6 + ImGui.NET Marionette application without re-reading the s&box editor source.

---

## 1. Every Panel, Window, Widget & Helper Today: Function and Owned State

### 1.1 Top-Level Host Window: `RigControlWindow`
- **File & Lines:** `Editor/RigControlEditor/RigControlWindow.cs:23-1816` (`public sealed class RigControlWindow : DockWindow, IAssetEditor`)
- **What It Does:**
  - Top-level dockable editor shell for `.riganim` (`RigAnimDocument`) and `.ctrlrig` (`RigDocument`).
  - Builds the main menu bar (`BuildMenuBar`, `:207-225`: `File`, `Edit`, `View`, `Help`), main toolbar (`BuildToolbar`, `:505-545`: `New`, `Open`, `Save`, `Undo`, `Redo`, `Key Bone (K)`, `Auto-Key`, `Mode: Rotate (W)` / `Move (E)` / `Scale (R)`), status bar (`RigStatusBar`), central 3D viewport dock (`_centralDock`), and 10 dockable panels (`Tutorial`, `Events`, `Scene`, `Constraints`, `Inspector`, `Lights`, `Cameras`, `Take Check`, `Graph`, `Timeline`).
  - Coordinates document lifecycle (`New` `:106`, `Open` `:138`, `Save` `:1443`, `SaveAs` `:1456`, `ImportObj` `:1538`, `OpenExport` `:1522`), pose mirroring (`MirrorPose(bool flip)` `:281-323`, `MirrorIkGoals` `:327-366`), mark creation (`AddMark` `:370-389`), keyframe shortcuts (`KeySelectedBone` `K` `:564-590`, `CopyKeyframes` `Ctrl+C` `:742`, `CutKeyframes` `Ctrl+X` `:761`, `PasteKeyframes` `Ctrl+V` `:777`, `CopyPoseAtPlayhead` `Ctrl+Shift+C` `:797`, `ClearSelectedBoneKeyframes` `:393`), gizmo mode shortcuts (`W` Rotate `:823`, `E` Move `:829`, `R` Scale `:835`), auto-key callbacks (`OnBonePosed` `:1260`, `OnPartPosed` `:1281`, `OnIkGoalPosed` `:1344`), playhead evaluation (`OnScrub(float frame)` `:1308-1336`), and full-document snapshot undo/redo (`PushUndo` `:1378`, `Undo` `:1398`, `Redo` `:1413`).
- **State It Owns:**
  - `private Asset _asset` (`:27`), `private RigAnimDocument _anim` (`:28`), `private RigDocument _rig` (`:29`), `private Model _lastModel` (`:30`), `private bool _dirty` (`:31`).
  - Child panel references (`:33-43, 427-429`): `_viewport` (`RigViewport`), `_timeline` (`RigTimeline`), `_events` (`RigEventProperties`), `_bones` (`RigBonesPanel`), `_constraints` (`RigConstraintsPanel`), `_lights` (`RigLightsPanel`), `_cameras` (`RigCamerasPanel`), `_inspector` (`RigInspectorPanel`), `_takeCheck` (`RigTakeCheckPanel`), `_graph` (`RigGraphEditor`), `_centralDock` (`DockWidget`), `_statusBar` (`RigStatusBar`), `_tutorialPanel` (`RigTutorialPanel`), `_tutorial` (`RigTutorial`).
  - Toolbar/Menu `Option` handles: `_saveOption` (`:45`), `_linkOption` (`:502` Auto-Key), `_dragModeOption` (`:503`), `_undoOption` (`:1369`), `_redoOption` (`:1370`).
  - Scratch & Undo state: `_morphScratch` (`Dictionary<string, float>`, `:1338`), `_undoStack` (`RigUndoStack`, `:1366`), `_baseline` (`RigSnapshot`, `:1367`).

---

### 1.2 3D Viewport & Overlays: `RigViewport`
- **File & Lines:**
  - `Editor/RigControlEditor/RigViewport.cs:38-3575` (`internal sealed partial class RigViewport : Widget`) + `enum BoneDragMode { Rotate, Move, Scale }` (`:14-20`)
  - `Editor/RigControlEditor/RigViewport.Drivers.cs:15-62`
  - `Editor/RigControlEditor/RigViewport.Overlays.cs:20-428`
- **Nested Types:**
  - `private sealed class RigBone` (`RigViewport.cs:849-874`): `Subject`, `Renderer`, `Bone`, `Radius`, `Key`, `Name`, `Index`, `Parent`, `TryGetWorld(out Transform)`.
  - `private readonly record struct RigSubject(string Name, SkinnedModelRenderer Renderer, float Radius)` (`RigViewport.cs:879`).
  - `private sealed class RigMovable` (`RigViewport.cs:1211-1240`): unifies `RigObject`, `RigObjectPart`, and `ReferenceProp` transforms and `FollowBone` attachments.
  - `private sealed class Collider` (`RigViewport.cs:1252-1257`): `PolyMesh` + `MeshBVH` for prop surface push-out.
- **What It Does:**
  - Renders the 3D scene, skeleton bones, IK goals, constraints, spring chains, reference props/objects, lights, cameras, onion skins, and 3D motion trails.
  - Hosts a top viewport toggle bar (`BuildToggleBar`, `RigViewport.cs:518-661`): `First-Person`, `Player Body`, `Move Model`, `Bones`, `Twist`, `Pixel Shader`, `Collide`, `Onion Skin`, `Motion Trail`, and a `Frame (F)` button.
  - Evaluates the multi-stage pose pipeline (`EvaluatePose`, `:2525-2629`), handles bone/object/IK goal picking and gizmo manipulation (`DrawBoneRotatemoveHandles` `:3180`, `DrawGroupHandles` `:3287`, `DrawIkHandles` `:2915`, `DrawMovableHandles` `:2071`), and manages orbit/pan/WASD-fly camera navigation (`:3400-3547`).
- **State It Owns:**
  - **Camera state (`:1986, 3500-3510`):** `_cameraAngles` (`Angles(18f, 180f, 0f)`), `_orbitPivot` (`Vector3(0f, 0f, 40f)`), `_orbitDistance` (`120f`), `_orbitDrag`, `_panDrag`, `_flyDrag`, `_lastMousePos`, `_lookThrough` (`RigCamera`).
  - **Viewmodel / Display toggles (`:53-88, 145-327, 2631`):** `BoneHandleScale` (`_boneHandleScale`), `CollideWithProps` (`_collideWithProps`), `ViewmodelMode` (`_viewmodelMode`), `LockCameraToView` (`_lockCameraToView`), `ShowBoneHandles` (`_showBoneHandles`), `MoveWholeModel` (`_moveWholeModel`), `ViewmodelOffset`, `ViewmodelRotation`, `ViewmodelFov` (`90f`), `ShowPlayerController` (`_showPlayerController`), `ShowHiddenBones`, `ShowTwistBones`, `PixelStyle`.
  - **Selection & Picking (`:311, 971-979, 1995-1997, 2247-2248`):** `_hoveredBone` (`string`), `_selectedBones` (`HashSet<string>`), `_primaryBone` (`string`), `_selectedMovable` (`string`), `_pickMovable` (`Action<string>`), `_pickPrompt` (`string`).
  - **Pose & Solver Dictionaries (`:3554-3574`, `RigViewport.Drivers.cs:18`):** `_restLocal` (`Dictionary<string, Transform>`), `_restWorld` (`Dictionary<string, Transform>`), `_boneDelta` (`Dictionary<string, Transform>`), `_ikGoalLocal` (`Dictionary<string, Transform>`), `_ikWeights` (`Dictionary<string, float>`), `_ikSpaces` (`Dictionary<string, string>`), `_springStates` (`Dictionary<string, RigEvaluators.SpringState>`), `_poseWorlds` (`Dictionary<string, Transform>`), `_morphsShown` (`HashSet<string>`, `:2487`).
  - **Gizmo Drag State (`:99, 105, 2067-2069, 2760, 3002-3005, 3102-3115, 3549`):** `DragMode` (`BoneDragMode`), `ObjectDragMode` (`BoneDragMode`), `AutoKeyEnabled` (`bool`), `_draggingBone`, `_dragBoneName`, `_dragStart` (`Transform`), `_moveDelta` (`Vector3`), `_groupDragTargets` (`List<(RigBone, Transform)>`), `_groupPivot` (`Vector3`), `_groupMoveDelta` (`Vector3`), `_draggingModel`, `_modelDragStart`, `_modelRotateStart`, `_modelMoveDelta`, `_propDragKey`, `_propDragStart`, `_propMoveDelta`.
  - **Overlays State (`RigViewport.Overlays.cs:22-74`):** `OnionStep = 4`, `OnionCount = 2`, `TrailFrames = 30`, `_showOnion` (`ShowOnionSkin`), `_showTrail` (`ShowMotionTrail`), `_overlayAnim`, `_overlayFrame`, `_overlayDirty`, `_ghosts` (`List<(float Offset, Dictionary<string, Transform> World)>`), `_trail` (`List<Vector3>`), `_trailKeys` (`List<Vector3>`), `_trailBone` (`string`), `_trailStart` (`int`), `_cameraFrame` (`Dictionary<RigCamera, (Transform, float)>`).

---

### 1.3 Dope Sheet Timeline & Rulers: `RigTimeline`, `RigTimelineLanes`, `RigTimelineRuler`, `RigBoneColumn`
- **File & Lines:**
  - `Editor/RigControlEditor/RigTimeline.cs:17-543` (`internal sealed class RigTimeline : Widget`)
  - `Editor/RigControlEditor/RigTimeline.cs:546-681` (`internal readonly struct RigTimelineLayout`)
  - `Editor/RigControlEditor/RigTimeline.cs:683-2386` (`internal sealed class RigTimelineLanes : Widget`) + `ViewRange` (`:744-770`) + `CopiedKeyframe` (`:1722-1737`)
  - `Editor/RigControlEditor/RigTimeline.cs:2388-2725` (`internal sealed class RigTimelineRuler : Widget`)
  - `Editor/RigControlEditor/RigBoneColumn.cs:26-135` (`internal sealed class RigBoneColumn : Widget`)
- **What It Does:**
  - `RigTimeline`: Hosts top timecode ruler (`_ruler`), middle track area (`_boneColumn` + scrollable `_lanes`), bottom frame/mark ruler (`_frameRuler`), and bottom transport bar (`Key (K)`, skip to `0`, step `-1`, `Play/Pause`, step `+1`, skip to `LastFrame`, `Loop`, `M:SS / M:SS` label, `PlaybackSpeed` menu `[0.1x..2x]`, `FPS` menu `[10..60]`). Steps playhead in `[EditorEvent.Frame] Frame()` (`:487-535`).
  - `RigTimelineLanes`: Draws one horizontal row (`RowHeight = 22f`) per `BoneTrack` (`Bone`, `Part`, `IkGoal`, `Camera`, `IkWeight`, `Morph`), keyframe shapes by `KeyInterpolation` (`Linear` = diamond, `Cubic` = hexagon, `Monotone` = flat hexagon, `Stepped` = square, `Smooth`/`EaseIn`/`EaseOut` = circle, `:1498-1542`), and 20-sample easing curves between keys (`:1367-1477`). Supports click/Ctrl-click/Shift-range/Ctrl+A/marquee box selection (`:776-961`), multi-key integer-frame dragging (`:889-932`), Ctrl+Wheel zoom and Shift+Wheel pan (`:1000-1035`), inline X/Y/Z/P/Y/R key value editing in the right-click menu (`:2145-2221`), key tools (`Reverse`, `Retime`, `Offset Down Chain`, `Shift ±1`, `Make Loop`, `Reduce Keys`, `:1899-1942`), and bone row procedural tools (`Bake Secondary Motion`, `Key From Targets`, `Wave Down Chain`, `Extract Root Motion`, `Layer Clip`, `:1180-1334`).
  - `RigTimelineRuler`: Draws frame/timecode ticks, playhead flag, and pink `RigMark` flags (`PaintMarks`, `:2567-2591`) with right-click mark editing (`Name`, `Note`, `Move to Playhead`, `Delete Mark`, `:2609-2652`).
  - `RigBoneColumn`: Fixed 130px left gutter drawing `BoneTrack.DisplayName` rows synced with vertical scroll (`RigBoneColumn.cs:26-135`).
- **State It Owns:**
  - `RigTimeline`: `_anim` (`RigAnimDocument`), `_lastStep` (`RealTimeSince`), `_playing` (`bool`), `_looping` (`bool = true`), `Playhead` (`float`), `PlaybackSpeed` (`float = 1f`), `SelectedBone` (`string`).
  - `RigTimelineLanes`: `View` (`ViewRange`: `PixelsPerFrame = 8f`, `ScrollX = 0f`), `_selection` (`List<(BoneTrack, BoneKeyframe)>`), `_dragKeys` (`List<(BoneTrack, BoneKeyframe, int StartFrame)>`), `_grabbed`, `_dragged`, `_grabKey`, `_grabOffset`, `_grabAnchorFrame`, `_appliedDelta`, `_hover` (`(BoneTrack, BoneKeyframe)?`), `_marqueeStart` (`Vector2?`), `_marqueeEnd` (`Vector2`), `_marqueeBase` (`List<(BoneTrack, BoneKeyframe)>`), `_clipboard` (`static List<CopiedKeyframe>`), `_bakeAsLoop` (`static bool`), `_rootMotionEven` (`static bool = true`).
  - `RigTimelineRuler`: `FrameCount`, `Playhead`, `Fps`, `ShowTimecode`, `_scrubbing`, `View` (`ViewRange`).

---

### 1.4 Curve Graph Editor: `RigGraphEditor`
- **File & Lines:** `Editor/RigControlEditor/RigGraphEditor.cs:31-819` (`internal sealed class RigGraphEditor : Widget`) + nested `enum Channel { X, Y, Z, Pitch, Yaw, Roll, Scale }` (`:118`) + nested `private sealed class GraphCanvas : Widget` (`:267-818`) + `enum DragKind { None, Key, HandleIn, HandleOut, Pan, Scrub }` (`:281`).
- **What It Does:**
  - Top bar with channel group checkboxes (`Position` default `true`, `Rotation` default `true`, `Scale / FOV` default `false`) and a `Frame` fit button (`:52-82`).
  - Plots continuous sampled curves (`_track.Evaluate(f)`) for the currently selected `BoneTrack`, unwrapping Euler angles via `Unwrap(angle, near)` (`:168-173`, `:314-345`) so rotation curves never jump across $\pm 180^\circ$.
  - Supports vertical keyframe value dragging (`DragKind.Key`, `:687-695`), `Cubic`/`Monotone` tangent handle dragging (`DragKind.HandleIn`/`HandleOut`, `:654-664, 697-727`, holding `Shift` breaks `TangentIn` from `TangentOut` via `_breakTangent`), middle-click/Alt+left-click pan (`DragKind.Pan`), wheel zoom (plain = both axes, `Ctrl` = value axis, `Shift` = time axis, `:742-763`), and right-click key menu (`Auto Tangents`, `Flat Tangents`, interpolation modes, `:767-809`).
- **State It Owns:**
  - `RigGraphEditor`: `TrackSource` (`Func<BoneTrack>`), `Playhead` (`Func<float>`), `_position`/`_rotation`/`_scale` (`Checkbox`), `_shown` (`BoneTrack`).
  - `GraphCanvas`: `_track` (`BoneTrack`), `_f0 = 0f, _f1 = 30f, _v0 = -10f, _v1 = 10f` (visible frame/value window, `:272`), `_fit = true` (`:273`), `_selected` (`(int Key, Channel Channel)?`, `:275`), `_drag` (`DragKind`, `:276`), `_breakTangent` (`bool`, `:277`), `_panFrom` (`Vector2`, `:278`), `_panView` (`(float F0, float F1, float V0, float V1)`, `:279`), `_lastMouse` (`Vector2`, `:765`).

---

### 1.5 Dockable Side Panels, Dialogs, and Helpers

| Class & Base Type | File & Lines | Function | Owned State |
| :--- | :--- | :--- | :--- |
| **`RigBonesPanel : Widget`**<br>+ `BoneTreeNode : TreeNode<string>` | `RigBonesPanel.cs:15-217` | `"Scene"` dock. Displays clip header fields (`SourceModel`, `RigAsset`, `AnimationSpeed`, `FrameCount`), hierarchical skeleton bone tree (with hidden bone toggle), and embeds `RigObjectsPanel`. | `_viewport`, `_tree` (`TreeView`), `_header` (`ControlSheet`), `_assetPathValue`, `_objects` (`RigObjectsPanel`), `_anim`, `_asset`. |
| **`RigObjectsPanel : Widget`**<br>+ `RigObjectNode : TreeNode<string>` | `RigObjectsPanel.cs:32-931` | Embedded in `"Scene"` dock. Manages `RigAnimDocument.Objects` (`RigObject`, `RigObjectPart` hierarchy) and `ReferenceProps`: `+ Import OBJ`, `+ Add Model`, eye visibility toggle, `Rename...`, `Follow` bone/part/prop re-homing (`Rehome` `:689-767` re-expresses existing keys in new parent space), `Duplicate`, `Delete`, and `"Pick in Viewport..."`. | `_viewport`, `_tree`, `_nodes` (`Dictionary<string, RigObjectNode>`), `_anim`, `_shape` (structural hash string), `_syncing` (`bool`). |
| **`RigInspectorPanel : Widget`**<br>+ `BoneTransform`<br>+ `MovableTransform` | `RigInspectorPanel.cs:36-398` | `"Inspector"` dock. Edits local `Rotation` (`Angles`), `Position` (`Vector3`), and uniform `Scale` (`float`) of the selected bone (`BoneTransform`) or object/part/prop (`MovableTransform`, plus `FollowBone`), and `"Reset to Rest Pose"`. | `_viewport`, `_sheet`, `_kindLabel`, `_boneLabel`, `_values` (`BoneTransform`), `_movable` (`MovableTransform`), `_builtFor` (`string`). |
| **`RigConstraintsPanel : Widget`** | `RigConstraintsPanel.cs:29-450` | `"Constraints"` dock. CRUD, reorder, enable/disable, and property editing for `RigDocument.IkConstraints` (`IkConstraint`) and `LimitConstraints` (`LimitConstraint`), plus per-clip `IkGoalSpace` dropdown (`Model`, `World`, or object/part/prop) and quick `IkWeight` key buttons (`1, 0, 0.25, 0.5, 0.75, Clear`). | `_rig` (`RigDocument`), `_anim` (`RigAnimDocument`), `_list` (`Widget`), `Playhead` (`Func<float>`). |
| **`RigCamerasPanel : Widget`** | `RigCamerasPanel.cs:30-348` | `"Cameras"` dock. Manages `RigAnimDocument.Cameras` (`RigCamera`): `+ Add Camera at View`, `Look Through` toggle, `Place At Camera`, `Name`/`Position`/`Rotation`/`FieldOfView`/`FollowBone` fields, and `Key` / `Key From View` / `Clear Keys` on `TrackTarget.Camera` tracks. | `_viewport`, `_list`, `_anim`, `Playhead` (`Func<int>`). |
| **`RigLightsPanel : Widget`** | `RigLightsPanel.cs:34-323` | `"Lights"` dock. Manages `RigAnimDocument.Lights` (`RigLight`): `+ Point Light`, `+ Directional`, `+ Spot Light`, `Copy Default Lighting`, per-light `On`/`Off`, `Place At Camera`, `Delete`, and light parameters (`Color`, `Brightness`, `Range`, `Cone`). | `_viewport`, `_list`, `_anim`. |
| **`RigEventProperties : Widget`** | `RigEventProperties.cs:15-319` | `"Events"` dock. Edits: (1) Prop attach `RigEvent`s (`Events`), (2) frame-range `MorphEvent`s (`MorphEvents`), (3) keyframed `TrackTarget.Morph` curves (`0 / 0.5 / 1` key buttons), and lists read-only pose-driven `MorphDrivers`. | `_asset` (`RigAnimDocument`), `_selected` (`RigEvent`), `_list`, `_detail`, `Playhead` (`Func<float>`). |
| **`RigTakeCheckPanel : Widget`**<br>+ `TakeCheckSettings` | `RigTakeCheckPanel.cs:34-766` | `"Take Check"` dock. Runs `Effigy.TakeAudit.AuditClip` asynchronously (`Task.Run`) to detect self-clipping (`Self`), part collisions (`PartPairs`), and prop penetrations (`Props`); renders summary counts and clickable flagged frame-span pills that scrub the timeline. | `_viewport`, `_settings` (`TakeCheckSettings`), `_anim`, `_asset`, `_summary` (`string`), `_generation` (`int`), `_running` (`bool`). |
| **`RigTutorialPanel : Widget`**<br>+ `RigStatusBar`<br>+ `RigTutorial` | `RigTutorialPanel.cs:19-545`<br>`RigStatusBar.cs:23-466` | `"Tutorial"` dock + bottom status bar. 6-step interactive onboarding walkthrough (`RigTutorial.Evaluate` inspects live clip state to advance steps) and bottom hover hint bar (`RigStatusBar.Show`). | `_tutorial` (`RigTutorial`: `_steps`, `Active`, `CurrentIndex`, `_furthest`), `RigStatusBar.Hint` (`static string`). |
| **`RigAnimExport`**<br>+ `RigAnimExportDialog : Window`<br>+ `DmxCameraWriter` | `RigAnimExport.cs:27-559`<br>`RigAnimExportDialog.cs:17-142`<br>`DmxCameraWriter.cs:28-84` | Modal export dialog and exporter (`OutputPath`, `AnimationName`, `SampleRate`, `WriteModelSidecar`, `IncludeReferenceProps`, `BakeViewmodelTransform`, `CreatePreviewScene`). Delegates DMX/VMDL/FBX output to kernel exporters and writes camera DMX via `DmxCameraWriter`. | `_anim`, `_rig`, `_options` (`ExportOptions`), `_statusLabel`. |
| **`RigUndoStack`**<br>+ `RigSnapshot` | `RigUndoStack.cs:17-328` | Deep-copy snapshot undo/redo stack (`MaxDepth = 128`) capturing `RigAnimDocument` and `RigDocument` before each edit. | `_undo` (`List<RigSnapshot>`), `_redo` (`List<RigSnapshot>`). |
| **Procedural & CLI/MCP Bridges** | `RigSecondaryBake.cs:27-295`<br>`RigKeyFromTargets.cs:18-170`<br>`RigRootMotionTool.cs:17-106`<br>`RigSampleBuilder.cs:22-185`<br>`RigPoseCapture.cs:42-445`<br>`Marionette*Mcp.cs` | Editor wrappers around Secondary Bake (`ChainSpring`), Wave (`ChainWave`), Key From Targets (`ClipKeys`), Root Motion (`RootMotion`), Sample Wave clip builder, offscreen pose screenshotter, and MCP automation endpoints. | Stateless static utility classes. |

---

## 2. What the Timeline and Curve Editor Actually Need From the Kernel

### 2.1 Why `Code/RigControl/` Exists Today vs. `Effigy/Rig/`
In the s&box repository, `Code/RigControl/` (19 files) exists because s&box runtime `Component`s and `GameResource`s use `Sandbox.Transform`, `Vector3`, `Rotation`, `Angles`, and `Model`. Meanwhile, the engine-free kernel in `Effigy/Rig/` **already implements the identical math and data structures** using `Effigy.Xform`, `Vec3`, `Quat`, `RigPose`, `RigAnimFile`, `RigTrack`, `RigKey`, and `Skeleton`:

| `Code/RigControl/` Type | `Effigy/Rig/` Kernel Equivalent | Status in `Effigy/Rig/` Today |
| :--- | :--- | :--- |
| `RigCurve` (`Code/RigControl/RigCurve.cs:1-189`) | `RigHermite` (`Effigy/Rig/RigAnimFile.cs:247-384`) | **100% in kernel** (`Channels = 10`, `Evaluate`, `Slope`, `Align`, `Normalise`, `Pack`, `Unpack`). |
| `RigDriverMath` (`Code/RigControl/RigDriverMath.cs:1-104`) | `MorphDriver` (`Effigy/Rig/MorphDriver.cs:52-182`) | **100% in kernel** (`RotationAbout`, `SwingOf`, `Map`, `Reading`, `Evaluate`, `WeightsAt`). |
| `RigSpringPresets` (`Code/RigControl/RigSpringPresets.cs:1-23`) | `ChainSpring.Presets` (`Effigy/Rig/ChainSpring.cs:31-49`) | **100% in kernel**. |
| `KeyInterpolation` (`RigAnimDocument.cs:983`) | `RigEase` (`Effigy/Rig/RigAnimFile.cs:15-24`) | **100% in kernel** (`Smooth, Linear, Stepped, EaseIn, EaseOut, Cubic, Monotone`). |
| `TrackTarget` (`RigAnimDocument.cs:479`) | `RigTrackKind` (`Effigy/Rig/RigAnimFile.cs:30-38`) | **100% in kernel** (`Bone, Part, IkGoal, Camera, IkWeight, Morph`). |
| `IkGoalSpace` (`RigAnimDocument.cs:536`) | `RigGoalSpace` (`Effigy/Rig/RigAnimFile.cs:80-85`) | **100% in kernel** (`Model, World, Object`). |
| `BoneKeyframe` (`RigAnimDocument.cs:1017`) | `RigKey` (`Effigy/Rig/RigAnimFile.cs:124-152`) | **In kernel** (`int Frame`, `RigPose Local`, `RigEase Ease`, `float[] TangentIn, TangentOut`, `RigKey.Apply(RigEase, float)`). |
| `BoneTrack` (`RigAnimDocument.cs:764`) | `RigTrack` (`Effigy/Rig/RigAnimFile.cs:154-235`) | **In kernel** (`Name`, `Kind`, `Keys`, `Evaluate(float)`, `EvaluateCubic`). Needs 3 small helper methods (see §2.2 below). |
| `RigSecondaryBake` (`Editor/RigControlEditor/RigSecondaryBake.cs`) | `ClipBake` (`Effigy/Rig/ClipBake.cs:14-245`) | **100% in kernel** (`ClipBake.Secondary` `:35`, `ClipBake.Wave` `:107`, `ClipBake.RopeLine` `:86`, `ClipBake.LiveIkBones` `:228`). |
| `RigKeyFromTargets` (`Editor/RigControlEditor/RigKeyFromTargets.cs`) | `ClipKeys` (`Effigy/Rig/ClipKeys.cs:24-312`) | **100% in kernel** (`ClipKeys.Parse`, `ClipKeys.Goals`, `ClipKeys.Solve`, `ClipKeys.Morph`). |
| `RigRootMotion` + `RigRootMotionTool` | `RootMotion` (`Effigy/Rig/RootMotion.cs:27-372`) | **100% in kernel** (`RootMotion.Extract` `:158`, `RootMotion.Plan` `:175`, `RootMotion.InPlace` `:359`). |
| `RigClipLayer` (`Code/RigControl/RigClipLayer.cs`) | `ClipLayer` (`Effigy/Rig/ClipLayer.cs:35-204`) | **100% in kernel** (`ClipLayer.Merge(RigAnimFile, RigAnimFile, Settings, Skeleton)` `:62`). |
| `RigConstraintSolver` (`Code/RigControl/RigConstraintSolver.cs`) | `ChainIk` (`Effigy/Rig/ChainIk.cs`) + `TwoBoneIk` (`TwoBoneIk.cs`) + `RigLimit` (`RigLimit.cs`) | **100% in kernel**. |
| `RigAnimDocument` + `RigDocument` | `RigAnimFile` (`Effigy/Rig/RigAnimFile.cs:344-898`) + `RigAnimWriter` (`RigAnimWriter.cs:13-147`) | **In kernel** (`ReadFile`, `Read`, `UseRig`, `SampleLocal`, `ModelSpace`, `LocalSpace`, `RigAnimWriter.Write`). |

### 2.2 Exact Additions / Bridges Needed for `RigTimeline` and `RigGraphEditor` to Run Directly on `Effigy.RigAnimFile`
To let the ImGui.NET timeline and curve editor operate directly on `Effigy.RigAnimFile`, `RigTrack`, `RigKey`, and `RigPose` (eliminating `RigAnimDocument` and `BoneTrack` entirely), Lane B needs only the following thin helpers in `geppetto-desktop/` (or requested from Lane A in `Effigy/Rig/`):

1. **Auto-Tangent & Hemisphere Helpers on `RigTrack` (from `Code/RigControl/RigAnimDocument.cs:862-965`):**
   - `SameHemisphere( Quat reference, Quat q ) -> Quat` (`RigAnimDocument.cs:862-865`): negates `q` if `Quat.Dot(reference, q) < 0f`.
   - `AutoTangent( RigTrack track, int index, bool monotone ) -> float[]` (`RigAnimDocument.cs:886-925`): packs `Keys[index - 1]`, `Keys[index]`, `Keys[index + 1]` via `RigHermite.Pack` (`Effigy/Rig/RigAnimFile.cs:367`), aligns hemispheres via `RigHermite.Align` (`:358`), and returns `float[10]` slopes via `RigHermite.Slope` (`:326`). Required by `RigGraphEditor.TangentOf` (`Editor/RigControlEditor/RigGraphEditor.cs:178-190`).
   - `SetKeyframe( RigTrack track, int frame, RigPose local )` (`RigAnimDocument.cs:944-965`): aligns `local.Rotation` to the neighboring key's hemisphere (`SameHemisphere`) before inserting or updating in sorted `Frame` order, preserving existing `TangentIn`/`TangentOut` when re-keying an existing frame.
2. **Euler Angle Channel Extraction & Slope Conversion for `RigGraphEditor` (`Editor/RigControlEditor/RigGraphEditor.cs:130-264`):**
   - Replace s&box `Rotation.Angles()` and `Angles.ToRotation()` with the kernel's existing functions:
     - `RigLimit.ToAngles( Quat q ) -> Vec3` (`Effigy/Rig/RigLimit.cs:106-125`, returns `(pitch, yaw, roll)` in degrees).
     - `Quat.FromAngles( float pitch, float yaw, float roll ) -> Quat` (`Effigy/Quat.cs:52-64`).
3. **`RigKeyOps` Ported to `(RigTrack, RigKey)` (`Code/RigControl/RigKeyOps.cs:22-332`):**
   - Port the 6 static operations (~250 lines of pure math) to operate on `(RigTrack Track, RigKey Key)`:
     - `RigKeyOps.Reverse` (`RigKeyOps.cs:33-75`)
     - `RigKeyOps.Retime` (`RigKeyOps.cs:85-117`)
     - `RigKeyOps.Offset` (`RigKeyOps.cs:126-147`)
     - `RigKeyOps.Reduce` (`RigKeyOps.cs:158-205`)
     - `RigKeyOps.MakeLoop` (`RigKeyOps.cs:213-245`)
     - `RigKeyOps.Bake` (`RigKeyOps.cs:253-282`)
4. **`RigTrackName` (`Code/RigControl/RigTrackName.cs:1-71`):**
   - Already 100% engine-free C# (`Qualify`, `Split`, `SubjectOf`, `BoneOf`, `Display`). Copy or include directly for `track.DisplayName` in the left bone column.
5. **Timeline Marks (`RigMark` in `Code/RigControl/RigAnimDocument.cs:565-586`):**
   - Simple `{ int Frame, string Name, string Note }` list for `RigTimelineRuler` flags (`RigTimeline.cs:2567-2652`).

---

## 3. What Is s&box Chrome to Drop vs. Replace vs. Port

### 3.1 DROP Completely (Do Not Port)
1. **s&box Asset System & Editor Registration:**
   - `[EditorForAssetType("riganim")]`, `[EditorApp(...)]`, `IAssetEditor`, `Asset`, `AssetSystem.FindByPath`, `AssetSystem.RegisterFile`, `AssetPicker`, `EditorUtility.OpenFileDialog` (`RigControlWindow.cs:21-27, 138-205, 1731-1798`).
2. **s&box Reflection Inspector Chrome (`ControlSheet` / `SerializedObject`):**
   - All `GetSerialized()`, `ControlSheet`, `[Property]`, `[Group]`, `[Title]`, `[Range]` usages in `RigBonesPanel.cs:95-126`, `RigInspectorPanel.cs:109-215`, `RigConstraintsPanel.cs:158-245`, `RigCamerasPanel.cs:215-268`, `RigLightsPanel.cs:204-285`, `RigEventProperties.cs:85-180`, `RigTakeCheckPanel.cs:115-145`. In ImGui.NET, these become direct 1-line `ImGui.DragFloat` / `ImGui.Checkbox` / `ImGui.Combo` calls against the plain C# fields.
3. **s&box Scene & Component Graph in Viewport & Runtime:**
   - `SceneWorld`, `Scene`, `SceneRenderingWidget`, `GameObject`, `SkinnedModelRenderer`, `CameraComponent`, `DirectionalLight`, `PointLight`, `SpotLight`, `AmbientLight` (`RigViewport.cs:40-45, 422-502, 1298-1461`), plus the runtime-only components `RigAnimPlayerComponent.cs`, `RigEventPlayerComponent.cs`, `CorrectiveMorphDriver.cs`, and `RigSoftChain.cs`.
4. **s&box Walkable Preview Scene & `.vanmgrph` Sidecar Spawner:**
   - `RigAnimExport.WritePreviewScene` (`RigAnimExport.cs:465-558`, which writes an s&box `.scene` file with a player controller) and s&box `ModelDoc` compile triggers.
5. **First-Person Arms Pixel Shader Override & Citizen Player Capsule:**
   - `RigViewport.PixelStyle` (`Material.Create("marionette_pixel_arms", "shaders/pixel.shader")` and `models/first_person/arms_skin_color.vtex`, `RigViewport.cs:2631-2716`) and `ShowPlayerController` (`_playerBodyObject` spawning `models/citizen/citizen.vmdl`, `RigViewport.cs:206-224`).
6. **`EditorCookie` Persistence & `MarionetteIcon` Generator:**
   - `EditorCookie.Get/Set` (`RigViewport.cs:55-84`, `RigHelpBox.cs:27`, `RigStatusBar.cs:166`) and `MarionetteIcon.cs` (`1-107`).
7. **s&box Editor MCP Attribute Plumbing & `CameraComponent.RenderToBitmap`:**
   - `MarionetteMcp.cs`, `MarionetteClipMcp.cs`, `MarionetteKeyMcp.cs`, `MarionetteMorphKeyMcp.cs` (`[McpTool]` attributes bound to `RigControlWindow.Current`), and `RigPoseCapture.cs` (`CameraComponent.RenderToBitmap` — headless/CLI rendering already exists in the kernel via `Effigy/Render/ShadedRender.cs`).

### 3.2 REPLACE With OpenGL 4.6 + ImGui.NET Equivalents
1. **UI Widget Tree (`Widget`, `DockWindow`, `Editor.Label`, `TreeView`, `ScrollArea`, `Menu`, `Window`):**
   - Replace with `ImGui.DockSpaceOverViewport`, `ImGui.BeginMainMenuBar`, `ImGui.Begin` panels, `ImGui.TreeNodeEx` (bone/object trees), `ImGui.BeginPopupContextItem` (right-click menus), and `ImGui.GetWindowDrawList()` (`ImDrawListPtr` for custom 2D timeline/ruler/curve canvas drawing).
2. **3D Viewport Gizmos & Ray-Picking (`Gizmo.Instance`, `Gizmo.Control.*`, `Gizmo.Draw.*`, `Gizmo.Hitbox.*`):**
   - Replace `Gizmo.Draw.*` with a lightweight GL line/primitive batcher (or projected 2D `ImDrawList` overlays for joint circles, bone sticks, IK diamonds, onion skins, and motion trails).
   - Replace `Gizmo.Hitbox.Sphere` / `Line` with analytical ray-sphere (bone joint heads) and ray-segment (bone shafts) intersection against `_worldPose[b].Origin` using the camera ray through `ImGui.GetMousePos()`.
   - Replace `Gizmo.Control.Rotate / Position / Scale` with either custom 3D ring/arrow/box ray-plane drag handles (~400 lines) or `ImGuizmo`, feeding the exact same parent-local delta math (`RigViewport.cs:3180-3350`).

---

## 4. Recommended ImGui.NET Panel Layout & Build Order

### 4.1 Recommended ImGui.NET Dock Layout

Enable `ImGuiConfigFlags.DockingEnable` and host a full-window dockspace (`ImGui.DockSpaceOverViewport`) with a top menu bar + transport/mode toolbar and a bottom status bar:

```
+-----------------------------------------------------------------------------------+
| Menu Bar: File | Edit | Pose | Constraints | Bake | View                          |
| Toolbar:  [New][Open][Save] | [Undo][Redo] | [Key (K)] [x] Auto-Key | [W][E][R]   |
+-------------------+-------------------------------------------+-------------------+
|                   | [Bones] [Twist] [IK] [Onion] [Trail] [F]  |                   |
|   Scene & Bones   |                                           |     Inspector     |
|   (Left Dock)     |                                           |   (Right Dock)    |
|                   |                                           |                   |
| - Clip Settings   |              3D GL Viewport               | - Selected Bone / |
|   Fps / Frames    |         (Central Passthrough /            |   Object Transform|
| - Skeleton Tree   |          Framebuffer Image)               |   Pos / Rot / Scl |
|   (Search + Filter|                                           |   [Reset to Bind] |
|    + Eye toggles) |                                           |                   |
| - Props & Objects |                                           +-------------------+
|                   |                                           | Constraints /     |
|                   |                                           | Take Check (Tabs) |
+-------------------+-------------------------------------------+-------------------+
| [Timeline (Dope Sheet)] [Graph (Curve Editor)]       [|<][<][Play][>][>|] [Loop]  |
|-----------------------------------------------------------------------------------|
| Bone / Track      | 0    5    10   15   20   25   30   35   40   45   50   55     |
| pelvis            | ◆---------●--------------●-------------------◆                |
| spine_0           | ◆------------------------●-------------------◆                |
| hand_R [IK]       | ◆--------------⬡---------⬡-------------------◆                |
+-------------------+---------------------------------------------------------------+
| Status Bar: Frame 15 / 60 (0.50s) | 30.0 FPS (0.42 ms) | Selected: hand_R         |
+-----------------------------------------------------------------------------------+
```

- **Why render the 3D GL Viewport directly to the main window backbuffer (or a multisampled FBO displayed in the central dock):**
  Using `ImGuiDockNodeFlags.PassthruCentralNode` lets OpenGL render the 3D scene directly into the central dock rectangle (`glViewport` set to the central node bounds) with zero FBO blit overhead, while `ImGui.GetBackgroundDrawList()` can draw crisp anti-aliased 2D screen-space bone circles, labels, IK goal diamonds, and marquee selection boxes directly over the 3D viewport.

---

### 4.2 Concrete Dependency-Ordered Build Order (for Lane B)

#### Phase 1: Minimum Viable Animator ("Step 5" Target — Scrubber + Bone Tree + Playback)
1. **Viewport Player State (`MarionettePlayer`):**
   - Hold `Skeleton`, `Xform[] BindWorld`, `Xform[] InvBindWorld`, `RigAnimFile Clip`, `float PlayheadFrame`, `bool IsPlaying`, `bool Looping = true`, `float PlaybackSpeed = 1f`, `int SelectedBoneIndex = -1`, `HashSet<int> SelectedBones`.
   - Implement the zero-allocation per-frame evaluation loop from `specs/viewport-frame.md` (§4).
2. **Bone List Panel (`Scene & Bones` Dock, replacing `RigBonesPanel.cs:15-217`):**
   - Search filter (`ImGui.InputText`), recursive `ImGui.TreeNodeEx` walk over `skeleton.Bones` (using prebuilt child lists), click to select bone (`SelectedBoneIndex`), Shift-click to multi-select.
   - Display clip metadata at top: `FrameRate`, `FrameCount`, `Looping`.
3. **Transport & Scrubber Bar (Minimal `RigTimeline.cs:132-249, 487-535`):**
   - Play/Pause (`Space`), Step `-1`/`+1` (`Left`/`Right` arrow), Jump Start/End (`Home`/`End`), `Loop` checkbox, `PlaybackSpeed` combo (`0.1x..2x`), and a full-width frame scrubber slider (`ImGui.SliderFloat("##scrub", ref PlayheadFrame, 0f, clip.LastFrame)`).
   - Advance `PlayheadFrame += dt * clip.FrameRate * PlaybackSpeed` when `IsPlaying` is true.

#### Phase 2: 3D Bone Overlay, Picking & Pose Gizmos ("Step 6" Target)
4. **Viewport Skeleton Overlay & Ray Picking (`RigViewport.cs:971-1065, 2750-2914`):**
   - Project `_worldPose[b].Origin` to screen space; draw bone sticks (`worldPose[parent].Origin` $\to$ `worldPose[b].Origin`) and joint circles via `ImDrawList` (or GL lines), coloring hovered/selected bones yellow/orange.
   - Left-click ray/screen-distance pick to select bone; `F` key frames `_orbitPivot` on `_worldPose[SelectedBoneIndex].Origin`.
5. **Local Inspector + Rotate/Move/Scale Gizmos (`RigInspectorPanel.cs:36-308`, `RigViewport.cs:3180-3350`):**
   - Inspector panel with `ImGui.DragFloat3("Position")`, `ImGui.DragFloat3("Rotation (P/Y/R)")` (via `RigLimit.ToAngles` / `Quat.FromAngles`), `ImGui.DragFloat("Scale")`, `"Reset to Bind Pose"`, and `"Key Bone (K)"`.
   - 3D gizmo handles (`W` Rotate, `E` Move, `R` Scale) + `Auto-Key` toggle writing `RigKey`s to `RigTrack` via `SetKeyframe`.
   - Two-Bone & Chain IK Goal handles (`DrawIkHandles`, `RigViewport.cs:2915-2998`): drag IK goal in 3D to key `RigTrackKind.IkGoal` tracks.

#### Phase 3: Full Dope-Sheet Timeline, Curve Editor & Undo ("Step 7" Target)
6. **Snapshot Undo/Redo Stack (`RigUndoStack.cs:17-328`):**
   - Port `RigUndoStack` (`MaxDepth = 128`) capturing `RigAnimFile` before any keyframe/pose edit (`Ctrl+Z` / `Ctrl+Y`).
7. **Full Dope-Sheet `RigTimelineLanes` & `RigTimelineRuler` (`RigTimeline.cs:546-2725`):**
   - Custom `ImDrawList` canvas using the exact layout metrics from `RigTimelineLayout` (`RowHeight = 22f`, `Gutter = 130f`, `DiamondSize = 9f`, `PixelsPerFrame` zoom & scroll).
   - Draw key silhouettes by `RigEase`, 20-sample easing connector lines, playhead line, and `RigMark` flags.
   - Multi-key selection (Click, Ctrl, Shift, Marquee box), integer-frame group dragging, Copy/Cut/Paste (`Ctrl+C`/`X`/`V`, `Ctrl+Shift+C` Copy Pose at Playhead), and right-click context menus (`RigKeyOps` + `ClipBake.Secondary` + `ClipBake.Wave` + `ClipKeys` + `RootMotion.Extract` + `ClipLayer.Merge`).
8. **Curve Editor `RigGraphEditor` (`RigGraphEditor.cs:31-819`):**
   - Custom `ImDrawList` canvas plotting `X, Y, Z, Pitch, Yaw, Roll, Scale` curves with `Unwrap` Euler continuity, `Fit` view framing, vertical key value dragging, and `Cubic`/`Monotone` tangent handle dragging (`RigHermite.Slope` + `AutoTangent`, `Shift` to break tangents).

#### Phase 4: Constraints, Secondary Tools, Take Check & Exporters
9. **Constraints Panel (`RigConstraintsPanel.cs:29-450`):**
   - Edit `RigAnimFile.IkConstraints` (`RigIk`: `EndBone`, `ChainLength`, `Solver` TwoBone/CCD/FABRIK/Spline, `Pole`, `Weight`, `GoalSpace`) and `RigAnimFile.Limits` (`RigLimit`: Pitch/Yaw/Roll min/max).
   - Onion Skinning & 3D Motion Trail overlays (`RigViewport.Overlays.cs:82-345`).
10. **Take Check & Export Dialog (`RigTakeCheckPanel.cs`, `RigAnimExportDialog.cs`):**
    - Wire `Effigy.TakeAudit.AuditClip` on a background `Task.Run` with clickable frame-span pills.
    - Wire native export to `FbxWriter` (`.fbx` for Roblox/Godot/general interchange), `DmxAnimWriter` (`.dmx` for Source 2/s&box), and `.riganim` save (`RigAnimWriter.Write`).
