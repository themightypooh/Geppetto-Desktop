# Spec: GPU Viewport Per-Frame Animation & Skinning Pipeline (`viewport-frame.md`)

This specification defines the exact sequence of engine-free kernel calls (`Effigy/Rig/`) required to evaluate and draw one animated frame in the OpenGL 4.6 viewport, how bone transforms move from a `.riganim` clip (`RigAnimFile`) to a GPU-skinned vertex, which calls are cheap vs. expensive, and every heap allocation trap in the current kernel that must be bypassed or cached to keep the render loop strictly zero-allocation.

---

## 1. One-Time Setup vs. Per-Frame Execution Order

### 1.1 One-Time Setup (Load / Bind Phase — Off the Per-Frame Path)

All file parsing, mesh binding, inverse-bind matrix inversion, and track-to-bone index resolution must happen once when a model, rig, or clip is loaded or edited—never per frame.

1. **Load Skeleton, Bind World Pose, and Skinned Mesh Parts**
   - **From a `.modeldump` JSON:**
     - `ModelDump.ReadFile( string path ) -> ModelDump` (`Effigy/Rig/ModelDump.cs:93`)
     - `ModelDump.Read( string json ) -> ModelDump` (`Effigy/Rig/ModelDump.cs:95-184`)
       - Populates `ModelDump.Skeleton` (`Skeleton`, `ModelDump.cs:77`) with bones ordered topologically (`parent < child`, `Effigy/Rig/Skeleton.cs:26-27, 79`).
       - Precomputes `ModelDump.BindWorld` (`Xform[]`, `ModelDump.cs:80, 146-148`) by calling `Skeleton.WorldBind( int index )` (`Effigy/Rig/Skeleton.cs:197-209`).
       - Populates `ModelDump.Meshes` (`List<ModelDump.Part>`, `ModelDump.cs:86`), where each `Part` (`ModelDump.cs:40-54`) owns `Positions` (`Vec3[]` already transformed by `RestTransform` into model space at load time, `ModelDump.cs:650-657`), `Triangles` (`int[]`), `Material` (`string`), and `Weights` (`SkinWeights`, up to 4 pruned & normalized `BoneWeight` influences per vertex, `ModelDump.cs:660-691`).
   - **From Procedural Stand-In Rigs (`SimRig`):**
     - `SimRig.Tentacle( int cables = 5, int bones = 12, float length = 90f, bool fingers = true ) -> ModelDump` (`Effigy/Rig/SimRig.cs:29-98`)
     - `SimRig.Citizen() -> ModelDump` (`Effigy/Rig/SimRig.cs:102-108`) -> calls `CitizenSkeleton.Build() -> Skeleton` (`Effigy/Rig/CitizenSkeleton.cs:31-145`)
   - **From an `.effigy` Document (`PartStudio`):**
     - `StudioDocument.ReadFile( string path ) -> StudioDocument` (`Effigy/StudioDocument.cs:42`)
     - `PartStudio` builds `Skeleton` (`Effigy/FeatureSkeleton.cs`) and binds `SkinWeights` via `SkinBinder.Bind( PolyMesh mesh, Skeleton skeleton, float falloff = 2.5f, int maxInfluences = 4 ) -> SkinWeights` (`Effigy/Rig/SkinBinder.cs:25-70`).
   - **Precompute Inverse Bind Matrices (`InvBindWorld`):**
     - Compute `Xform[] invBindWorld = new Xform[skeleton.Count]` once at load time:
       ```csharp
       for ( int b = 0; b < skeleton.Count; b++ )
           invBindWorld[b] = bindWorld[b].Inverse; // Effigy/Xform.cs:116
       ```
     - Note: `SkinBinder.Deform` (`Effigy/Rig/SkinBinder.cs:279`) recomputes `bindWorld[b].Inverse` every call; the GPU viewport must cache `invBindWorld[b]` once at load time.

2. **Load Clip (`.riganim`) and Control Rig (`.ctrlrig`)**
   - `RigAnimFile.ReadFile( string path ) -> RigAnimFile` (`Effigy/Rig/RigAnimFile.cs:516`)
   - `RigAnimFile.Read( string json ) -> RigAnimFile` (`Effigy/Rig/RigAnimFile.cs:524-571`)
     - Populates `FrameRate` (`float`, `:348`), `FrameCount` (`int`, `:349`), `Looping` (`bool`, `:350`), `Tracks` (`List<RigTrack>`, `:351`), `MorphEvents` (`List<RigMorphEvent>`, `:355`), `GoalSpaces` (`Dictionary<string, RigGoalSpace>`, `:367`), `Placements` (`Dictionary<string, RigPlacement>`, `:373`), `RootMotion` (`RootMotionSettings`, `:379`), `PlayInPlace` (`bool`, `:385`).
   - `RigAnimFile.UseRig( string json ) -> void` (`Effigy/Rig/RigAnimFile.cs:432-438`):
     - `IkConstraints = RigIk.ReadRig( json )` (`Effigy/Rig/ChainIk.cs:66-103` -> `List<RigIk>`)
     - `Limits = RigLimit.ReadRig( json )` (`Effigy/Rig/RigLimit.cs:36-61` -> `List<RigLimit>`)
     - `SoftCollision = RigSoftCollision.ReadRig( json )` (`Effigy/Rig/RigSoftCollision.cs:26-52` -> `RigSoftCollision`)
     - `Drivers = MorphDriver.ReadRig( json )` (`Effigy/Rig/MorphDriver.cs:244-278` -> `List<MorphDriver>`)

3. **Initialize Stateful Secondary-Motion Buffers (Once per Rig)**
   - **Live SoftBone Verlet (`SoftSolver`):**
     - `new SoftPose( int boneCount )` (`Effigy/Rig/SoftBone.cs:109-115`): allocates `Tail` (`Vec3[B]`), `PrevTail` (`Vec3[B]`), `Settled` (`bool[B]`).
   - **Stepped ChainSpring / Rope Verlet (`ChainSpring`):**
     - `ChainSpring.Ropes( Skeleton skeleton, IReadOnlyList<Xform> restWorld ) -> List<RopeState>` (`Effigy/Rig/ChainSpring.cs:190-210`)
     - `SoftCollision.ToOptions( Skeleton skeleton ) -> ChainSpring.Options` (`Effigy/Rig/RigSoftCollision.cs:58-70`)

---

### 1.2 Per-Frame Execution Order

Given playback time $t$ (in seconds), the frame cursor is `float frame = t * clip.FrameRate` (wrapped to `[0, clip.LastFrame]` when `clip.Looping` is true, matching `RigTimeline.Frame()` at `Editor/RigControlEditor/RigTimeline.cs:487-535`).

The authoritative kernel evaluation order (used by `TakeAudit.Pose( float frame )` at `Effigy/Rig/TakeAudit.cs:215` and `MotionLint.Pose( float frame )` at `Effigy/Rig/MotionLint.cs:135`) is:

```
[Stage 1: Track Curve Sampling & Local Joint Limit Clamping]
  RigAnimFile.SampleLocal(skeleton, frame)                                (Effigy/Rig/RigAnimFile.cs:578-591)
    ├─ 1a. Per-bone local pose evaluation (b = 0 .. skeleton.Count - 1)
    │    ├─ RigAnimFile.Find(bone.Name) -> RigTrack                       (Effigy/Rig/RigAnimFile.cs:514)
    │    ├─ If track found and Keys.Count > 0:
    │    │    ├─ RigTrack.Evaluate(frame) -> RigPose                      (Effigy/Rig/RigAnimFile.cs:173-202)
    │    │    │    ├─ Non-cubic: RigKey.Apply(ease, t) + RigPose.Lerp     (Effigy/Rig/RigAnimFile.cs:141-151, 120-121)
    │    │    │    └─ Cubic/Monotone: RigTrack.EvaluateCubic              (Effigy/Rig/RigAnimFile.cs:204-234)
    │    │    │         └─ RigHermite.Evaluate(...)                       (Effigy/Rig/RigAnimFile.cs:263-318)
    │    │    └─ RigPose.ToXform() -> Xform                               (Effigy/Rig/RigAnimFile.cs:115)
    │    └─ Else: fallback to bind local `skeleton.Bones[b].Local`        (Effigy/Rig/RigAnimFile.cs:585)
    │
    ├─ 1b. Per-bone local rotation limit clamping
    │    └─ RigLimit.Clamp(Limits, bone.Name, pose[b]) -> Xform           (Effigy/Rig/RigAnimFile.cs:586 -> RigLimit.cs:92-98)
    │         └─ Quat.FromXform -> RigLimit.ToAngles -> Math.Clamp -> Quat.FromAngles -> ToXform (RigLimit.cs:69-125)
    │
[Stage 2: Live IK Evaluation (called inside SampleLocal)]
    ├─ RigAnimFile.SolveIk(skeleton, local, frame) -> Xform[]             (Effigy/Rig/RigAnimFile.cs:589, 598-633)
    │    ├─ RigAnimFile.ModelSpace(skeleton, local) -> Xform[]            (Effigy/Rig/RigAnimFile.cs:609, 872-883)
    │    ├─ For each active RigIk constraint in IkConstraints:
    │    │    ├─ RigAnimFile.IkGoalAt(constraint.EndBone, frame)          (Effigy/Rig/RigAnimFile.cs:614, 647-662)
    │    │    │    └─ RigAnimFile.PlacementAt(subject, frame)             (Effigy/Rig/RigAnimFile.cs:658, 670-715)
    │    │    ├─ RigAnimFile.IkWeightAt(constraint.EndBone, frame)        (Effigy/Rig/RigAnimFile.cs:619, 637-640)
    │    │    └─ ChainIk.Apply(skeleton, world, live, goal, Limits)       (Effigy/Rig/RigAnimFile.cs:628 -> ChainIk.cs:164-280)
    │    │         ├─ ChainIk.Stretch(w, target, maxStretch)              (Effigy/Rig/ChainIk.cs:192, 367-396)
    │    │         ├─ Solver branch:
    │    │         │    ├─ (n == 2): TwoBoneIk.TrySolve(...)              (Effigy/Rig/ChainIk.cs:194 -> TwoBoneIk.cs:35-88)
    │    │         │    ├─ (n >= 3 && Spline): ChainIk.Spline + Twist     (Effigy/Rig/ChainIk.cs:209-215, 466-493, 559-582)
    │    │         │    ├─ (n >= 3 && Fabrik): ChainIk.Fabrik             (Effigy/Rig/ChainIk.cs:219, 400-452)
    │    │         │    └─ (n == 1 or CCD): Iterative CCD loop            (Effigy/Rig/ChainIk.cs:227-245)
    │    │         ├─ GoalRotation end-effector orientation               (Effigy/Rig/ChainIk.cs:248-249)
    │    │         ├─ ChainIk.Limit(w, above, names, limits, target, it)  (Effigy/Rig/ChainIk.cs:251-255, 291-353)
    │    │         ├─ Partial weight blend (Vec3.Lerp + Quat.Slerp)       (Effigy/Rig/ChainIk.cs:257-267)
    │    │         └─ LocalSpace(skeleton, world) -> ModelSpace(...)      (Effigy/Rig/ChainIk.cs:270-279)
    │    └─ RigAnimFile.LocalSpace(skeleton, world) -> Xform[]            (Effigy/Rig/RigAnimFile.cs:632, 886-897)
    │
[Stage 3: Optional In-Place Root Motion Stripping (called inside SampleLocal)]
    └─ RootMotion.InPlace(this, skeleton, pose, frame) -> Xform[]         (Effigy/Rig/RigAnimFile.cs:590 -> RootMotion.cs:359-371)

[Stage 4: Forward Kinematics (Parent-Local -> Model Space)]
  RigAnimFile.ModelSpace(skeleton, local) -> Xform[]                      (Effigy/Rig/RigAnimFile.cs:872-883)

[Stage 5: Optional Live Secondary Motion (SoftBone Verlet)]
  SoftSolver.Solve(skeleton, worldPose, softPose, dt, gravity) -> void    (Effigy/Rig/SoftBone.cs:154-197)

[Stage 6: Optional Pose-Driven & Curve Morph Weights]
  RigAnimFile.MorphWeightsAt(frame, skeleton, local) -> Dictionary<string, float> (Effigy/Rig/RigAnimFile.cs:451-459)
    ├─ MorphDriver.WeightsAt(Drivers, skeleton, local)                    (Effigy/Rig/MorphDriver.cs:187-211)
    └─ RigAnimFile.MorphWeightsAt(frame)                                  (Effigy/Rig/RigAnimFile.cs:477-491)

[Stage 7: Skinning Matrix Palette Generation & GPU Upload]
  For b = 0 .. skeleton.Count - 1:
    skinPalette[b] = worldPose[b] * invBindWorld[b]                       (Effigy/Rig/SkinBinder.cs:279)
  Upload `skinPalette` (as 3x4 or 4x4 float matrices) to GPU UBO / SSBO
```

---

### 1.3 Kernel Call Reference: Signatures, Return Types, and Cheap vs. Expensive Classification

Let:
- $B$ = bone count (`skeleton.Count`, e.g. 95 for Citizen, 67 for Tentacle)
- $T$ = track count (`clip.Tracks.Count`)
- $K$ = keyframe count on a track (`track.Keys.Count`)
- $L$ = joint limit count (`clip.Limits.Count`)
- $C_{\text{IK}}$ = active IK constraint count (`clip.IkConstraints.Count`)
- $N$ = IK chain bone count (`ik.ChainLength`, typically $2 \le N \le 12$)
- $I$ = solver iterations (`ik.Iterations`, default 16, clamped to $[1, 64]$)
- $V$ = vertex count across meshes

| Stage | Method Signature & `file:line` | Return Type | Cost Tier | Complexity & Why It Is Cheap or Expensive |
| :--- | :--- | :--- | :--- | :--- |
| **1a** | `RigAnimFile.Find( string boneName )`<br>`Effigy/Rig/RigAnimFile.cs:514` | `RigTrack` | **Moderate** (allocates!) | $O(T)$ linear scan per bone ($O(B \cdot T)$ per frame) + allocates a LINQ closure per bone. Should be replaced by an $O(1)$ pre-resolved `RigTrack[]` array indexed by bone ID. |
| **1a** | `RigTrack.Evaluate( float frame )`<br>`Effigy/Rig/RigAnimFile.cs:173-202` | `RigPose` | **Cheap** | $O(K)$ linear scan to find segment `[i, i+1]`, then 1 `Vec3.Lerp` + 1 `Quat.Slerp` (`System.Numerics.Quaternion.Slerp`, `Effigy/Quat.cs:67`) or `EvaluateCubic`. Zero heap allocations. |
| **1a** | `RigTrack.EvaluateCubic( int seg, float frame, bool monotone )`<br>`Effigy/Rig/RigAnimFile.cs:204-234`<br>`RigHermite.Evaluate(...)`<br>`Effigy/Rig/RigAnimFile.cs:263-318` | `RigPose` / `void` | **Cheap** | $O(1)$ per evaluated bone track: packs 4 neighboring poses into `stackalloc float[10]` spans, aligns quaternion hemispheres (`RigHermite.Align`, `:358`), evaluates 10-channel Hermite cubic (with Fritsch-Carlson clamp if `monotone`, `:326-352`), and normalizes the quaternion (`:375-383`). Zero heap allocations. |
| **1b** | `RigLimit.Clamp( IReadOnlyList<RigLimit> limits, string boneName, Xform local )`<br>`Effigy/Rig/RigLimit.cs:92-98` | `Xform` | **Cheap** | $O(L)$ string comparisons per bone; for bones with an enabled limit, performs $O(1)$ `Quat.FromXform` $\to$ `RigLimit.ToAngles` $\to$ `Math.Clamp` $\to$ `Quat.FromAngles` (`RigLimit.cs:69-125`). |
| **2** | `TwoBoneIk.TrySolve( Vec3 root, Vec3 middle, Vec3 end, Vec3 target, Vec3 pole, float softness, out Solution solution )`<br>`Effigy/Rig/TwoBoneIk.cs:35-88` | `bool` + `TwoBoneIk.Solution` (`struct`) | **Very Cheap** | $O(1)$ closed-form law of cosines + pole-plane hinge + optional exponential knee softness (`TwoBoneIk.cs:164-175`) + two `DeltaBetween` shortest-arc rotations. Pure value-type math, zero allocations. |
| **2** | `ChainIk.Fabrik( IReadOnlyList<Vec3> positions, Vec3 target, int iterations )`<br>`Effigy/Rig/ChainIk.cs:400-452` | `Vec3[]` | **Cheap** | $O(I \cdot N)$ forward/backward point passes (with $I \le 64, N \le 12$, $< 800$ vector ops) + $O(N)$ `DeltaBetween` basis updates (`ChainIk.cs:221-225`). |
| **2** | `ChainIk.Spline( Skeleton skeleton, Xform[] w, int[] chain, Vec3 target, Vec3 pole, int iterations )`<br>`Effigy/Rig/ChainIk.cs:466-493` | `void` | **Cheap** | $O(N + I \cdot N)$: fits quadratic Bezier control point (`ArcControl`, `:586-615`), samples 32 arc chords (`ArcJoints`, `:499-554`), runs `Fabrik`, and distributes end-to-root roll linearly (`Twist`, `:559-582`). |
| **2** | `ChainIk.Apply` CCD loop (`Effigy/Rig/ChainIk.cs:227-245`) & `ChainIk.Limit` recovery (`Effigy/Rig/ChainIk.cs:291-353`) | `Xform[]` | **Moderate** | $O(I \cdot N^2)$ for CCD; if joint limits clamp any bone in the chain, `ChainIk.Limit` runs up to $I$ recovery passes re-clamping and propagating `ModelSpace` across the chain ($O(I \cdot N \cdot (N + L))$). Still $< 0.05\text{ ms}$ per chain, but allocates temporary arrays. |
| **2** | `RigAnimFile.PlacementAt( string subject, float frame )`<br>`Effigy/Rig/RigAnimFile.cs:670-715` | `Xform` | **Cheap to Moderate** | Walks up to 8 parent `RigPlacement` attachments, evaluating prop/bone tracks at `frame` when an IK goal uses `RigGoalSpace.World` or `RigGoalSpace.Object`. |
| **3** | `RootMotion.InPlace( RigAnimFile clip, Skeleton skeleton, Xform[] local, float frame )`<br>`Effigy/Rig/RootMotion.cs:359-371` | `Xform[]` | **VERY EXPENSIVE (Trap!)** | Calls `RootMotion.Plan( clip, skeleton )` (`RootMotion.cs:175-248`) and `RootMotion.MotionAt( clip, skeleton, frame )` (`RootMotion.cs:295-315`), each of which resamples **all $F$ frames of the entire clip** from frame `0` to `LastFrame` via `SampleLocal`! Only runs when `clip.PlayInPlace && clip.RootMotion is { Enabled: true }`. **Must cache `RootMotionPlan` once per clip/edit.** |
| **4** | `RigAnimFile.ModelSpace( Skeleton skeleton, IReadOnlyList<Xform> local )`<br>`Effigy/Rig/RigAnimFile.cs:872-883` | `Xform[]` | **Very Cheap** | $O(B)$ single forward sweep (`world[i] = p >= 0 ? world[p] * local[i] : local[i]`). 95 matrix multiplies for Citizen ($\sim 1\ \mu\text{s}$). |
| **4** | `RigAnimFile.LocalSpace( Skeleton skeleton, IReadOnlyList<Xform> world )`<br>`Effigy/Rig/RigAnimFile.cs:886-897` | `Xform[]` | **Cheap** | $O(B)$ sweep (`local[i] = p >= 0 ? world[p].Inverse * world[i] : world[i]`). Performs $B$ $3 \times 3$ cofactor matrix inversions (`Xform.Inverse`, `Effigy/Xform.cs:116-141`). |
| **5** | `SoftSolver.Solve( Skeleton skeleton, Xform[] animated, SoftPose pose, float dt, Vec3? gravity = null )`<br>`Effigy/Rig/SoftBone.cs:154-197` | `void` (in-place on `animated`) | **Cheap & Zero-Alloc** | $O(B \cdot S)$ where $S = \lceil dt / (1/60) \rceil \le 8$ substeps (`MaxStep = 1f / 60f`, `MaxSubsteps = 8`, `SoftBone.cs:145-146`). Mutates `animated` in-place; zero heap allocations. |
| **5** | `ChainSpring.Step(...)`<br>`Effigy/Rig/ChainSpring.cs:216-349` | `Xform[]` | **Moderate** | $O(B + R \cdot 8 \cdot N_{\text{rope}} \cdot N_{\text{cap}} + B_s \cdot (N_{\text{cap}} + L))$ where $R$ = ropes (`RopeIterations = 8`, `:115`), $N_{\text{cap}}$ = collision capsules (`RigSoftCollision.ToOptions`, `RigSoftCollision.cs:58-70`). Note: in Marionette, `ChainSpring` is normally baked offline into bone keys via `ClipBake.Secondary` (`Effigy/Rig/ClipBake.cs:35-79`), not stepped per viewport frame unless previewing live physics. |
| **6** | `RigAnimFile.MorphWeightsAt( float frame, Skeleton skeleton, IReadOnlyList<Xform> local )`<br>`Effigy/Rig/RigAnimFile.cs:451-459`<br>`MorphDriver.WeightsAt(...)`<br>`Effigy/Rig/MorphDriver.cs:187-211` | `Dictionary<string, float>` | **Cheap** (allocates!) | $O(D \cdot B + T_{\text{morph}} \cdot K + M_{\text{events}})$ where $D$ = `Drivers.Count`. Math is cheap (1 `Xform.Inverse` + Swing/Twist/Distance per driver, `MorphDriver.cs:156-178`), but allocates 2 `Dictionary<string, float>` instances per frame. |
| **7** | **GPU Skinning Palette Build**<br>`skin[b] = poseWorld[b] * invBindWorld[b]`<br>(`Effigy/Rig/SkinBinder.cs:279`) | `Xform[B]` / `float[12*B]` | **Very Cheap** | $O(B)$ matrix multiplications (95 bones = 95 `Xform * Xform` ops $\approx 1\ \mu\text{s}$), followed by `glBufferSubData` of $95 \times 48 = 4,560$ bytes (or $95 \times 64 = 6,080$ bytes for `mat4`). |
| **Alt 7** | **CPU Skinning (Reference Only)**<br>`SkinBinder.Deform( IReadOnlyList<Vec3> positions, SkinWeights weights, IReadOnlyList<Xform> bindWorld, IReadOnlyList<Xform> poseWorld )`<br>`Effigy/Rig/SkinBinder.cs:267-298` | `Vec3[]` | **Expensive on CPU** | $O(B + V \cdot I_{\text{max}})$ on CPU + allocates `new Vec3[V]` per part per frame. Do **not** use for the real-time GPU viewport; evaluate LBS in the GLSL 4.60 vertex shader. |

---

## 2. How Bone Matrices Get From a Clip to a Skinned Vertex

### 2.1 Coordinate Space and Matrix Conventions in `Effigy/`

1. **World / Model Coordinate Space:**
   - Right-handed **Z-up** (matches Source 2 / s&box): `+Z` is up (gravity is `(0, 0, -386)` at `Effigy/Rig/SoftBone.cs:140` and `ChainSpring.cs:109`), `+X` is forward, `+Y` is left (`CitizenSkeleton.cs:31-145`, `DmxWriter.cs:320-326`).
   - All vertex positions in `PolyMesh.Positions` (`Effigy/PolyMesh.cs:122`) and `ModelDump.Part.Positions` (`Effigy/Rig/ModelDump.cs:44, 657`) are stored in **model space (bind pose)**.

2. **`Xform` Memory Layout and Multiplication Order (`Effigy/Xform.cs:13-190`):**
   - `public readonly struct Xform` (`Effigy/Xform.cs:13-22`) contains four `Vec3` fields: `X`, `Y`, `Z`, `Origin` (12 `float`s = 48 bytes).
   - `X`, `Y`, `Z` are the **columns** of the $3 \times 3$ rotation+scale basis, and `Origin` is the translation column:
     $$M = \begin{bmatrix} X_x & Y_x & Z_x & \text{Origin}_x \\ X_y & Y_y & Z_y & \text{Origin}_y \\ X_z & Y_z & Z_z & \text{Origin}_z \\ 0 & 0 & 0 & 1 \end{bmatrix}$$
   - `Xform.TransformPoint( Vec3 p )` (`Effigy/Xform.cs:29-30`) computes column-vector multiplication $M \cdot [p_x, p_y, p_z, 1]^T$:
     $$\text{TransformPoint}(p) = X \cdot p_x + Y \cdot p_y + Z \cdot p_z + \text{Origin}$$
   - `Xform.operator *( Xform a, Xform b )` (`Effigy/Xform.cs:102-106`) composes transforms such that `(a * b).TransformPoint(p) == a.TransformPoint(b.TransformPoint(p))` (i.e. `b` is applied first, then `a`).

3. **`RigPose` and Quaternion Conventions (`Effigy/Rig/RigAnimFile.cs:98-123`, `Effigy/Quat.cs:18-103`):**
   - Keyframes in `.riganim` tracks store parent-local `RigPose( Vec3 Position, Quat Rotation, Vec3 Scale )` (`Effigy/Rig/RigAnimFile.cs:111`).
   - `RigPose.ToXform()` (`RigAnimFile.cs:115`) calls `Rotation.ToXform( Position, Scale )` (`Effigy/Quat.cs:70-71`), which rotates unit vectors `(1,0,0)`, `(0,1,0)`, `(0,0,1)` by `Rotation` and scales the resulting basis columns by `Scale.x`, `Scale.y`, `Scale.z`:
     ```csharp
     public Xform ToXform( Vec3 origin, Vec3 scale ) =>
         new( Rotate( new Vec3( 1, 0, 0 ) ) * scale.x,
              Rotate( new Vec3( 0, 1, 0 ) ) * scale.y,
              Rotate( new Vec3( 0, 0, 1 ) ) * scale.z,
              origin );
     ```

4. **Bone Local Axis Agnosticism (`Effigy/Rig/SimRig.cs:17-19`):**
   - Procedural Effigy bones created via `Skeleton.AddBoneFromPoints` place the bone shaft along local `+Y` (`Effigy/Rig/Skeleton.cs:16, 89-93`).
   - Citizen / Source 2 bones place the bone shaft along local `+X` (`Effigy/Rig/CitizenSkeleton.cs:22-25`).
   - `SimRig.Tentacle` rolls cable bones by $90^\circ$ per joint around their shaft (`Effigy/Rig/SimRig.cs:71`).
   - **Rule for Viewport Bone Overlay & Gizmos:** Never draw bones by assuming local `+X` or `+Y` has length `bone.Length`. Draw bone segments from `worldPose[b].Origin` to `worldPose[child].Origin` for bones with children, or use `Skeleton.BoneDirection( int index )` (`Effigy/Rig/Skeleton.cs:242-270`) for leaf bones (matching `SimRig.Skin` at `Effigy/Rig/SimRig.cs:144-154`).

---

### 2.2 Step-by-Step Mathematical Transform Chain

1. **Bind Pose (`BindLocal` $\to$ `BindWorld` $\to$ `InvBindWorld`):**
   - Each `Bone` (`Effigy/Rig/Skeleton.cs:19-47`) stores its bind parent-local transform `Bone.Local` (`Xform`, `:30`) and `int Parent` (`:27`, `-1` for root).
   - Because `Skeleton.Bones` guarantees `Parent < i`, the model-space bind transform is:
     $$\text{BindWorld}[b] = \begin{cases} \text{Bones}[b].\text{Local} & \text{if } \text{Parent} = -1 \\ \text{BindWorld}[\text{Parent}] \times \text{Bones}[b].\text{Local} & \text{if } \text{Parent} \ge 0 \end{cases}$$
   - The **Inverse Bind Matrix (IBM)** for bone $b$ is:
     $$\text{InvBindWorld}[b] = \text{BindWorld}[b]^{-1} \quad (\text{\texttt{Effigy/Xform.cs:116-141}})$$
     Multiplying a model-space bind vertex $p_{\text{bind}}$ by $\text{InvBindWorld}[b]$ transforms $p_{\text{bind}}$ into bone $b$'s local coordinate space.

2. **Animated Pose (`RigTrack` $\to$ `LocalPose` $\to$ `PoseWorld`):**
   - At frame $f$, `RigAnimFile.SampleLocal( skeleton, f )` (`Effigy/Rig/RigAnimFile.cs:578-591`) evaluates each bone's `RigTrack` into a parent-local `Xform` `local[b]` (falling back to `skeleton.Bones[b].Local` when a bone has no track or zero keys, `:585`), clamps local rotation via `RigLimit.Clamp` (`:586`), solves active IK chains via `SolveIk` (`:589`), and optionally strips root motion via `RootMotion.InPlace` (`:590`).
   - `RigAnimFile.ModelSpace( skeleton, local )` (`Effigy/Rig/RigAnimFile.cs:872-883`) sweeps `b = 0 .. skeleton.Count - 1` in topological order:
     $$\text{PoseWorld}[b] = \begin{cases} \text{local}[b] & \text{if } \text{Parent} = -1 \\ \text{PoseWorld}[\text{Parent}] \times \text{local}[b] & \text{if } \text{Parent} \ge 0 \end{cases}$$
   - If live `SoftBone` physics is enabled on any bones (`bone.Soft.Enabled`), `SoftSolver.Solve( skeleton, poseWorld, softPose, dt )` (`Effigy/Rig/SoftBone.cs:154-197`) updates `poseWorld` in-place.

3. **Skinning Matrix Palette (`SkinPalette[b]`):**
   - For each bone $b \in [0, B)$, the skinning transform that maps a vertex from model-space bind pose to model-space animated pose is (`Effigy/Rig/SkinBinder.cs:277-280`):
     $$\text{SkinPalette}[b] = \text{PoseWorld}[b] \times \text{InvBindWorld}[b]$$

4. **Per-Vertex Weights (`SkinWeights` $\to$ GPU Vertex Attributes):**
   - In the kernel, `SkinWeights` (`Effigy/Rig/SkinWeights.cs:31-60`) stores `List<BoneWeight[]> Vertices` (`:37`), where `BoneWeight` (`:8-20`) is `readonly struct BoneWeight( int Bone, float Weight )`.
   - `SkinWeights.Prune( BoneWeight[] weights, int max = 4 )` (`Effigy/Rig/SkinWeights.cs:147-176`) and `ModelDump.ReadPart` (`Effigy/Rig/ModelDump.cs:660-691`) enforce at most **4 bone influences per vertex** (`MaxInfluences = 4`), sorted descending by weight and renormalized so $\sum_{k=0}^{3} w_k = 1.0$.
   - When uploading a mesh to the GPU VBO (once at load time), pack two vertex attributes per vertex $v$:
     - `uvec4 aBoneIndices` (or `ivec4`): `[b0, b1, b2, b3]`, padded with `0` for unused slots.
     - `vec4 aBoneWeights`: `[w0, w1, w2, w3]`, padded with `0.0f` for unused slots.
     - **Unweighted vertex fallback:** If `weights.Vertices[v].Length == 0` (or `part.Weights == null`), either set `aBoneIndices = (0, 0, 0, 0)` and `aBoneWeights = (0, 0, 0, 0)` and branch in the vertex shader (`float wSum = dot(aBoneWeights, vec4(1.0));`), or bind an identity matrix at slot `0` / use a `uSkinned` uniform flag for static parts. Matching `SkinBinder.Deform` (`Effigy/Rig/SkinBinder.cs:294`):
       `result[v] = weights[v].Length == 0 ? p : acc;`

5. **GPU Vertex Shader Linear Blend Skinning (GLSL 4.60):**
   - Packing each `Xform s = SkinPalette[b]` into a column-major `mat4` (16 floats) or row-major `mat3x4` (3 `vec4` rows = 12 floats) for OpenGL:
     - **As `mat4` (column-major for GLSL `mat4 uBones[MAX_BONES]`):**
       - Column 0: `vec4(s.X.x, s.X.y, s.X.z, 0.0)`
       - Column 1: `vec4(s.Y.x, s.Y.y, s.Y.z, 0.0)`
       - Column 2: `vec4(s.Z.x, s.Z.y, s.Z.z, 0.0)`
       - Column 3: `vec4(s.Origin.x, s.Origin.y, s.Origin.z, 1.0)`
     - **GLSL 4.60 Vertex Deformation:**
       ```glsl
       mat4 skinMat =
           aBoneWeights.x * uBones[aBoneIndices.x] +
           aBoneWeights.y * uBones[aBoneIndices.y] +
           aBoneWeights.z * uBones[aBoneIndices.z] +
           aBoneWeights.w * uBones[aBoneIndices.w];

       float weightSum = aBoneWeights.x + aBoneWeights.y + aBoneWeights.z + aBoneWeights.w;
       vec4 modelPos = (weightSum > 0.0) ? (skinMat * vec4(aPosition, 1.0)) : vec4(aPosition, 1.0);
       vec3 modelNormal = (weightSum > 0.0) ? normalize(mat3(skinMat) * aNormal) : aNormal;
       ```

---

## 3. Where Per-Frame Allocations Sneak In (And How to Stay Allocation-Free)

Calling `clip.SampleLocal( skeleton, frame )` and `RigAnimFile.ModelSpace( skeleton, local )` directly as written in `Effigy/Rig/` today will **allocate on every single frame**. Below is the complete inventory of heap allocations in the kernel's animation path, with exact `file:line` citations and how the desktop viewport player must avoid them.

### 3.1 Unconditional Per-Frame Allocations in `SampleLocal` & `ModelSpace`

1. **`Effigy/Rig/RigAnimFile.cs:514` (called inside `SampleLocal` at `:584`) — Closure + Delegate + Boxed Enumerator on Every Bone**
   - **What happens:**
     ```csharp
     public RigTrack Find( string boneName ) =>
         Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == boneName );
     ```
     Called inside `for ( var b = 0; b < skeleton.Count; b++ )` (`RigAnimFile.cs:582-587`).
   - **Allocation:** Allocates $B$ closure instances (`<>c__DisplayClass`), $B$ `Func<RigTrack, bool>` delegates, and $B$ boxed `List<RigTrack>.Enumerator` structs **every frame** (for Citizen with 95 bones, **285 heap objects per frame**).
   - **Zero-Alloc Fix:** In the desktop viewport's clip player, build a `RigTrack[] _boneTrackByBoneIndex` array of length `skeleton.Count` once when binding a `RigAnimFile` to a `Skeleton` (and refresh only when tracks are added/removed). Evaluate `_boneTrackByBoneIndex[b]?.Evaluate(frame)` directly.

2. **`Effigy/Rig/RigAnimFile.cs:361-362` & `:600` — `IkGoalTracks.ToList()` Allocates Even When a Clip Has Zero IK**
   - **What happens:**
     ```csharp
     // RigAnimFile.cs:361-362
     public IEnumerable<RigTrack> IkGoalTracks =>
         Tracks.Where( t => t.Kind == RigTrackKind.IkGoal && t.Keys.Count > 0 );
     // RigAnimFile.cs:600 (first line of SolveIk, called unconditionally by SampleLocal at :589)
     var goals = IkGoalTracks.ToList();
     ```
   - **Allocation:** Allocates a LINQ `WhereListIterator<RigTrack>` and a `new List<RigTrack>()` on **every frame**, even when `IkConstraints == null` and no IK goal tracks exist.
   - **Zero-Alloc Fix:** Pre-resolve active IK bindings at clip/rig bind time into a flat struct array `BoundIkConstraint[]` pairing each `RigIk` with its cached `RigTrack GoalTrack` and `RigTrack WeightTrack`. Skip IK entirely when that array is empty.

3. **`Effigy/Rig/RigAnimFile.cs:580` & `:874` — `new Xform[skeleton.Count]` in `SampleLocal` and `ModelSpace`**
   - **What happens:**
     - `SampleLocal` (`RigAnimFile.cs:580`): `var pose = new Xform[skeleton.Count];`
     - `ModelSpace` (`RigAnimFile.cs:874`): `var world = new Xform[skeleton.Count];`
   - **Allocation:** Allocates at least two `Xform[B]` arrays ($2 \times 4.5\text{ KB} = 9\text{ KB}$ for 95 bones) every frame.
   - **Zero-Alloc Fix:** Pre-allocate `Xform[] _localPose = new Xform[B]`, `Xform[] _worldPose = new Xform[B]`, and `Xform[] _skinPalette = new Xform[B]` once per skeleton on the viewport player and compute the loops in-place.

---

### 3.2 Conditional Per-Frame Allocations (When IK, Root Motion, Morphs, or Springs Are Active)

4. **`Effigy/Rig/RigAnimFile.cs:625` & `Effigy/Rig/ChainIk.cs:58-63` — `RigIk.Weighted` Calls `MemberwiseClone()`**
   - **What happens:** When an `IkWeight` track evaluates to $0 < w < 1$, `SolveIk` calls `constraint.Weighted( keyedWeight )`, which executes `(RigIk)MemberwiseClone()` (`ChainIk.cs:60`).
   - **Allocation:** 1 heap `RigIk` clone per partially-weighted IK chain per frame.
   - **Zero-Alloc Fix:** Temporarily set `constraint.Weight = keyedWeight` before calling `ChainIk` (and restore afterward), or use a pre-allocated scratch `RigIk` per constraint.

5. **`Effigy/Rig/RigAnimFile.cs:639` & `:649` — LINQ `FirstOrDefault` Closures in `IkWeightAt` and `IkGoalAt`**
   - **What happens:** Both `IkWeightAt` (`:639`) and `IkGoalAt` (`:649`) call `Tracks.FirstOrDefault( t => t.Kind == ... && t.Name == endBone )`.
   - **Allocation:** 2 closures + 2 delegates + 2 boxed enumerators per active IK constraint per frame.
   - **Zero-Alloc Fix:** Pre-bind `GoalTrack` and `WeightTrack` references per `RigIk` constraint when loading/editing tracks.

6. **`Effigy/Rig/RigAnimFile.cs:676, 695, 714` — `HashSet<string>` and String Allocations in `PlacementAt`**
   - **What happens:** When an IK goal is anchored to a prop/object (`GoalSpaces`), `PlacementAt` allocates `new HashSet<string>( StringComparer.Ordinal )` (`:676`), slices strings via `RigTrackName.Split` (`:695`), and interpolates `Find( $"{subject}/{bone.Name}" )` (`:714`).
   - **Allocation:** `HashSet<string>` + substring + interpolated string allocations per anchored IK goal per frame.
   - **Zero-Alloc Fix:** Pre-resolve parent subject/bone indices and qualified track references when `Placements` or `GoalSpaces` change.

7. **`Effigy/Rig/ChainIk.cs:172, 176, 189, 190, 200, 253, 263, 270, 278, 379, 406, 506, 526, 540` — 9 to 12 `new[]` Arrays Inside `ChainIk.Apply`**
   - **What happens:** Every call to `ChainIk.Apply` allocates:
     - `var chain = new int[n + 1];` (`:176`)
     - `var w = new Xform[n + 1];` (`:189`)
     - `var original = new Xform[n + 1];` (`:190`)
     - `var names = new string[n];` (`:253`, when limits exist)
     - `var blended = new Xform[n + 1];` (`:263`, when `ik.Weight < 1f`)
     - `var local = LocalSpace( skeleton, world );` (`:270` -> `new Xform[skeleton.Count]` at `:284`)
     - `return ModelSpace( skeleton, local );` (`:278` -> `new Xform[skeleton.Count]` at `RigAnimFile.cs:874`)
     - Plus inside `Stretch` (`new float[n]` at `:379`), `Fabrik` (`new Vec3[count]` and `new float[n]` at `:406-407`), and `ArcJoints` (`new float[n]`, `new float[33]`, `new Vec3[n + 1]` at `:506, 526, 540`).
   - **Zero-Alloc Fix:**
     - Note that `TwoBoneIk.TrySolve` (`Effigy/Rig/TwoBoneIk.cs:35-88`) is **already 100% allocation-free**! For 2-bone IK without stretch/spline/FABRIK, calling `TwoBoneIk.TrySolve` directly on the pre-allocated `_worldPose` buffer and updating the 2 bone transforms in-place allocates **zero bytes**.
     - If Lane A adds a non-allocating `ChainIk.ApplyInPlace` overload (via `TASKS.md` REQUESTS), all chain buffers ($N \le 64$) fit on the stack via `stackalloc`.

8. **`Effigy/Rig/RootMotion.cs:359-371` (called at `RigAnimFile.cs:590`) — Full-Clip Resampling + Array Allocations in `RootMotion.InPlace`**
   - **What happens:** When `clip.PlayInPlace && clip.RootMotion is { Enabled: true }`, `RootMotion.InPlace` calls `Plan( clip, skeleton )` (`:361`) and `MotionAt( clip, skeleton, frame )` (`:365`, which calls `Plan( clip, skeleton )` a second time!), resampling every frame `0..LastFrame` of the clip twice per rendered viewport frame and allocating `Xform[last + 1][]`, `Xform[last + 1]`, and `(Xform[])local.Clone()` (`:366`).
   - **Zero-Alloc Fix:** Never call `RootMotion.InPlace` inside the per-frame loop. Instead, call `RootMotion.Plan( clip, skeleton ) -> RootMotion.RootPlan` (`Effigy/Rig/RootMotion.cs:175`) **once** when `RootMotion` settings or root bone tracks change, cache the `RootPlan`, and in the per-frame loop interpolate `plan.Frames` at `frame` and apply `plan.InPlaceLocal[f]` to `_localPose[plan.RootIndex]` in-place.

9. **`Effigy/Rig/RigAnimFile.cs:451-459, 477-491` & `Effigy/Rig/MorphDriver.cs:187-211` — Two `new Dictionary<string, float>` per Frame in `MorphWeightsAt`**
   - **What happens:** `MorphDriver.WeightsAt` (`MorphDriver.cs:190`) and `RigAnimFile.MorphWeightsAt( float frame )` (`RigAnimFile.cs:479`) each allocate a new `Dictionary<string, float>( StringComparer.Ordinal )` on every call.
   - **Zero-Alloc Fix:** Pre-map morph names to integer morph target indices `0..MorphCount-1` at load time, and evaluate `MorphDriver.Evaluate( driver, skeleton, _localPose )` (`Effigy/Rig/MorphDriver.cs:180`, which is a pure value-type calculation returning `float`) and morph `RigTrack.Evaluate( frame ).Position.x` directly into a pre-allocated `float[] _morphWeights` array.

10. **`Effigy/Rig/SkinBinder.cs:267-298` — `new Xform[B]` and `new Vec3[V]` in `SkinBinder.Deform`**
    - **What happens:** `SkinBinder.Deform` allocates `var skin = new Xform[bindWorld.Count]` (`:277`) and `var result = new Vec3[positions.Count]` (`:282`) on every call.
    - **Zero-Alloc Fix:** Do not call `SkinBinder.Deform` in the GPU viewport; compute `_skinPalette[b] = _worldPose[b] * _invBindWorld[b]` into a pre-allocated `Xform[]` (or `float[]`) buffer once per frame and perform vertex deformation in the GLSL 4.60 vertex shader.

---

## 4. Reference Zero-Allocation Per-Frame Viewport Loop (for Lane B)

By pre-binding track/limit/IK indices at load/edit time and reusing three `Xform[B]` arrays (`_localPose`, `_worldPose`, `_skinPalette`), the entire per-frame animation + soft-bone + GPU matrix palette step runs in **< 0.05 ms with 0 bytes allocated**:

```csharp
// Pre-allocated once when Skeleton is loaded (B = skeleton.Count):
//   Xform[] _invBindWorld = new Xform[B];
//   Xform[] _localPose    = new Xform[B];
//   Xform[] _worldPose    = new Xform[B];
//   float[] _gpuMatrices  = new float[B * 16]; // column-major mat4 palette for UBO/SSBO
//   SoftPose _softPose    = new SoftPose(B);
//
// Pre-resolved once when RigAnimFile / RigLimits / Tracks change:
//   RigTrack[] _boneTracks = new RigTrack[B]; // null if bone b has no Bone track
//   RigLimit[] _boneLimits = new RigLimit[B]; // null if bone b has no enabled RigLimit

public void EvaluateFrame( Skeleton skeleton, float frame, float dt )
{
    int count = skeleton.Count;
    var bones = skeleton.Bones;

    // 1. Sample bone tracks & clamp joint limits in-place (0 allocations)
    for ( int b = 0; b < count; b++ )
    {
        var track = _boneTracks[b];
        Xform local = (track != null && track.Keys.Count > 0)
            ? track.Evaluate( frame ).ToXform()
            : bones[b].Local;

        var limit = _boneLimits[b];
        if ( limit != null && limit.Enabled )
            local = limit.Clamp( local );

        _localPose[b] = local;
    }

    // 2. Forward kinematics to model space in-place (0 allocations)
    for ( int b = 0; b < count; b++ )
    {
        int parent = bones[b].Parent;
        _worldPose[b] = parent >= 0 ? _worldPose[parent] * _localPose[b] : _localPose[b];
    }

    // 3. Optional live SoftBone Verlet in-place (0 allocations; SoftSolver.Solve is already allocation-free)
    if ( _hasSoftBones && dt > 0f )
    {
        SoftSolver.Solve( skeleton, _worldPose, _softPose, dt );
    }

    // 4. Build skinning palette & pack column-major mat4 for OpenGL UBO/SSBO (0 allocations)
    for ( int b = 0; b < count; b++ )
    {
        Xform s = _worldPose[b] * _invBindWorld[b];
        int o = b * 16;
        // Column 0 (X)
        _gpuMatrices[o +  0] = s.X.x;      _gpuMatrices[o +  1] = s.X.y;      _gpuMatrices[o +  2] = s.X.z;      _gpuMatrices[o +  3] = 0f;
        // Column 1 (Y)
        _gpuMatrices[o +  4] = s.Y.x;      _gpuMatrices[o +  5] = s.Y.y;      _gpuMatrices[o +  6] = s.Y.z;      _gpuMatrices[o +  7] = 0f;
        // Column 2 (Z)
        _gpuMatrices[o +  8] = s.Z.x;      _gpuMatrices[o +  9] = s.Z.y;      _gpuMatrices[o + 10] = s.Z.z;      _gpuMatrices[o + 11] = 0f;
        // Column 3 (Origin)
        _gpuMatrices[o + 12] = s.Origin.x; _gpuMatrices[o + 13] = s.Origin.y; _gpuMatrices[o + 14] = s.Origin.z; _gpuMatrices[o + 15] = 1f;
    }
}
```
*(Note: If active `ChainIk` constraints with $N \ge 3$ or `PlayInPlace` root motion are used on a clip, either invoke `ChainIk.Apply` only when the playhead/goal changes or request an in-place `Span<Xform>` overload from Lane A in `TASKS.md` so Stage 2 also remains 0-allocation.)*
