# Geppetto Desktop — Constraint & Secondary Motion Parity Inventory

## 1. Executive Summary

This specification and audit matrix establishes the authoritative constraint and secondary-motion parity boundary for **Geppetto Desktop**. It defines how control rig (`.ctrlrig`) features map to the engine-free core kernel (`Effigy.Kernel`), identifies standalone desktop-supported features versus engine-dependent capabilities, and provides specifications for deterministic secondary simulation and constraint evaluation.

Geppetto Desktop is a standalone Linux 3D animation, posing, and assembly application. It executes completely independent of proprietary game engines (such as s&box/Source 2, Unity, or Godot). All animation evaluation, inverse kinematics, limit enforcement, and secondary motion must function deterministically without relying on engine scenes, physics engines, or display servers.

---

## 2. Control Rig (`.ctrlrig`) Constraint Specification & Inventory

In the Geppetto ecosystem, `.ctrlrig` files define bone constraints, limits, drivers, and secondary dynamics authored against a skeleton. The format separates reusable skeletal constraints from individual animation clips (`.riganim`).

### 2.1 Inverse Kinematics (`IkConstraints`)
- **End Bone (`TargetBone` / `EndBone`)**: Terminal joint of the kinematic chain that reaches toward the goal.
- **Chain Length (`ChainLength`)**: Number of bones upwards from the target bone participating in the solve:
  - `ChainLength = 1`: Single-bone aim/look-at solve toward target.
  - `ChainLength = 2`: Closed-form two-bone analytical IK (Law of Cosines).
  - `ChainLength >= 3`: Iterative chain solve.
- **Solvers (`IkSolver`)**:
  - `CCD`: Cyclic Coordinate Descent. Iterates from tip to root, rotating each bone to aim at target. Fast convergence for short chains; bends distal joints first.
  - `FABRIK`: Forward and Backward Reaching Inverse Kinematics. Reaches joint positions forward to target and backward to root, preserving segment lengths, then turns bones onto solved positions. Distributes curvature evenly.
  - `Spline`: Circular arc fitting matching chain chord length with constant curvature. Eliminates joint kinks on tentacles, cables, and tails.
- **Pole Vector (`PoleDirection`)**: Direction in model space guiding the bend plane of middle joints (elbow/knee) in two-bone solves and initial bowing direction for spline chains.
- **Weight (`Weight`)**: Linear/spherical blend factor $[0, 1]$ between input kinematic pose and solved IK pose.
- **Goal Rotation (`GoalRotation`)**: When enabled, copies the goal's orientation to the terminal bone or distributes twist down the chain (`Twist`).
- **Max Stretch (`MaxStretch`)**: Allows chain segments to lengthen by up to the stretch factor ($1.0$ = rigid, $1.5$ = $50\%$ extension) when target exceeds total chain length. Lengthens via joint translation rather than non-uniform scaling to maintain Source 2 / DMX bone compatibility.
- **Live vs. Baked (`Live`)**: Determines whether constraints solve interactively during playback from animated goals or were authoring-time drags baked to FK keys.

### 2.2 Limit Constraints (`LimitConstraints`)
- **Target Bone (`TargetBone` / `Bone`)**: Bone whose parent-space orientation is restricted.
- **Euler Angle Bounds (`MinAngles`, `MaxAngles`)**: Degrees of pitch (around Y), yaw (around Z), and roll (around X), matching standard Euler decomposition.
- **Weight (`Weight`)**: Soft-stop blend factor. A weight of $1.0$ enforces a hard clamp; weights $< 1.0$ soften joint resistance.
- **Global Evaluation**: Evaluated everywhere a pose is constructed: keyframe sampling, interactive gizmo editing, within each step of IK solves (`ChainIk.Limit`), and during secondary spring relaxation.

### 2.3 Secondary Motion & Soft Chains (`SoftChains`)
- **Root Bone (`Bone`)**: Top bone of the soft chain; it and all descendant bones simulate lag, drag, and inertial settling.
- **Presets (`Preset`)**:
  - `Loose`: High compliance, moderate damping ($\text{stiffness} = 120$, $\text{damping} = 0.001$, $\text{weight} = 0.3$, $\text{maxAngle} = 60^\circ$). Trails and whips (cables, tentacles, tails). Settles in $\approx 2$ seconds.
  - `Heavy`: High inertia, low frequency ($\text{stiffness} = 50$, $\text{damping} = 0.0005$, $\text{weight} = 1.0$, $\text{maxAngle} = 25^\circ$). Sags and sloshes (flesh, bellies, hanging sacs).
  - `Springy`: Fast response, quick dissipation ($\text{stiffness} = 300$, $\text{damping} = 0.003$, $\text{weight} = 0.1$, $\text{maxAngle} = 30^\circ$). Jiggle, ears, antennas.
  - `Stiff`: High stiffness, near-zero overshoot ($\text{stiffness} = 500$, $\text{damping} = 0.0001$, $\text{weight} = 0.05$, $\text{maxAngle} = 15^\circ$). Fingers, mechanical tips.
  - `Rope`: Pure distance-constrained particle chain ($\text{stiffness} = 0$, $\text{damping} = 0.05$, $\text{weight} = 1.0$, $\text{maxAngle} = 180^\circ$). Drapes and sags without spring return.
  - `Custom`: User-specified stiffness, damping, gravity weight, and cone angle.
- **Simulation Mechanics**: Time-corrected Verlet integration on bone tails (first child position), spring restoring force toward animated pose, gravity vector ($386\text{ in/s}^2$), angular cone restriction around animated direction, and length conservation.
- **Soft Colliders (`SoftColliders`)**: Capsule obstacles on specified body bones (from bone head to child tail, with radius $R$). Soft tails are pushed outside capsules with additional clearance ($SoftThickness$).
- **Ground Floor (`Ground`, `GroundHeight`)**: Planar floor constraint in model space preventing soft chains from sagging beneath ground level.
- **Pinning (`PinBone`)**: Pins terminal or intermediate rope bones to animated or IK-solved goals (e.g. holding a dangling cable in a hand).

### 2.4 Look-At / Aim Constraints (`LookAtConstraints`)
- **Target (`TargetBone` / `TargetPosition`)**: Entity or position toward which the bone orients.
- **Aim Vector (`AimAxis`)**: Local bone axis directed toward target (default $+Y$ or $+Z$).
- **Up Vector (`UpAxis`)**: Local bone axis aligned toward reference up vector to resolve roll.
- **Weight (`Weight`)**: Interpolation blend factor $[0, 1]$ between unconstrained rotation and target look-at rotation.

### 2.5 Copy Rotation & Transform Constraints (`CopyRotationConstraints`, `CopyTransformConstraints`)
- **Source Bone (`SourceBone`)**: Driver bone supplying transform or rotation data.
- **Target Bone (`TargetBone`)**: Driven bone receiving orientation or position.
- **Space (`Space`)**: Local (parent-space) or Model (world/character-space) copying.
- **Weight (`Weight`)**: Spherical linear interpolation (`Quat.Slerp`) scale factor $[0, 1]$.
- **Channels**: Independent copying masks for Position, Rotation, and Scale.

### 2.6 Morph Drivers & Correctives (`MorphDrivers`)
- **Target Morph (`Morph`)**: Name of the blend shape / morph target driven by skeletal movement.
- **Reading Bone (`Bone`)**: Bone whose rotational or spatial displacement drives the shape.
- **Channel (`Channel`)**:
  - `Rotation`: Signed degrees of twist/hinge rotation about designated axis from bind pose.
  - `Swing`: Unsigned angular deflection of the designated axis away from bind orientation.
  - `Distance`: Euclidean distance between `Bone` and `OtherBone`.
- **Mapping Curve (`Rest`, `Target`, `Value`, `Ease`, `Clamp`)**: Two-key interpolation curve evaluating the weight of the corrective shape key.
- **Compound Drivers**: Multiple drivers targeting the same morph target multiply their evaluated weights together.

---

## 3. Kernel Support Cross-Reference Matrix

The table below cross-references all `.ctrlrig` constraints and secondary motion features against the engine-free kernel (`Effigy.Kernel`) and Desktop runtime implementations:

| Constraint / Feature | `.ctrlrig` Key | Kernel / Desktop Implementation | Standalone Status | Notes |
|---|---|---|---|---|
| **Two-Bone IK** | `IkConstraints` (`ChainLength=2`) | `Effigy.TwoBoneIk.TrySolve`, `Effigy.TwoBoneIk.Apply` | **Supported (Engine-Free)** | Closed-form analytical solve, pole vector, optional softness curve. Zero engine dependencies. |
| **CCD Chain IK** | `IkConstraints` (`Solver=Ccd`, `ChainLength>=3`) | `Effigy.ChainIk.Apply` | **Supported (Engine-Free)** | Iterative tip-to-root cyclic coordinate descent. Tested and deterministic. |
| **FABRIK Chain IK** | `IkConstraints` (`Solver=Fabrik`, `ChainLength>=3`) | `Effigy.ChainIk.Apply`, `Effigy.ChainIk.Fabrik` | **Supported (Engine-Free)** | Forward/backward reaching position solve with shortest-arc rotational reconstruction. |
| **Spline IK** | `IkConstraints` (`Solver=Spline`) | `Effigy.ChainIk.Spline`, `Effigy.ChainIk.ArcJoints`, `Effigy.ChainIk.Twist` | **Supported (Engine-Free)** | Circular arc bisection, constant curvature, twist distribution along spine. |
| **IK Chain Stretch** | `IkConstraints` (`MaxStretch > 1.0`) | `Effigy.ChainIk.Stretch` | **Supported (Engine-Free)** | Translates joints along bone axes without non-uniform scaling; preserves bone cross-section. |
| **Rig Limits** | `LimitConstraints` | `Effigy.RigLimit.ReadRig`, `Effigy.RigLimit.Clamp` | **Supported (Engine-Free)** | Euler angle clamping with gimbal protection and soft weighting. Integrated in IK and key sampling. |
| **Secondary Springs** | `SoftChains` | `Effigy.ChainSpring.Bake`, `Effigy.ChainSpring.Step`, `Marionette.Desktop.SecondarySpringSimulation` | **Supported (Engine-Free)** | Time-corrected Verlet integration on bone tails. Fully deterministic with fixed stepping. |
| **Spring Presets** | `SoftChains.Preset` | `Effigy.ChainSpring.Presets`, `Effigy.SoftBone` | **Supported (Engine-Free)** | Five standard presets (`loose`, `heavy`, `springy`, `stiff`, `rope`). |
| **Soft Capsule Collision**| `SoftColliders` | `Effigy.ChainSpring.Push`, `ChainSpring.Capsule` | **Supported (Engine-Free)** | Analytical segment-to-point capsule projection with soft thickness offset. |
| **Soft Ground Plane** | `Ground`, `GroundHeight`| `Effigy.ChainSpring.Push` | **Supported (Engine-Free)** | Model-space horizontal floor plane constraint ($Z \ge \text{GroundHeight} + \text{Thickness}$). |
| **Rope Distance Chains** | `SoftChains.Rope` | `Effigy.ChainSpring.StepRope`, `ChainSpring.Rope` | **Supported (Engine-Free)** | Gauss-Seidel distance constraints ($12$ iterations) holding joint spacing. |
| **Rope Pinning** | `SoftChains.PinBone`| `Effigy.ChainSpring.StepRope` | **Supported (Engine-Free)** | Pinned bone follows animation/IK pose while cable sags between root and pin. |
| **Look-At / Aim** | `LookAtConstraints` | `Marionette.Desktop.ConstraintParityInventory.EvaluateLookAt` | **Supported (Desktop-Extended)** | Slerp-weighted orientation aiming bone axis toward target position. Engine-free. |
| **Copy Rotation** | `CopyRotationConstraints` | `Marionette.Desktop.ConstraintParityInventory.EvaluateCopyRotation` | **Supported (Desktop-Extended)** | Slerp-weighted orientation transfer in parent or model space. Engine-free. |
| **Copy Transform** | `CopyTransformConstraints` | `Marionette.Desktop.ConstraintParityInventory.EvaluateCopyTransform` | **Supported (Desktop-Extended)** | Position lerp, rotation slerp, scale lerp with weight control. Engine-free. |
| **Morph Drivers** | `MorphDrivers` | `Effigy.MorphDriver.ReadRig`, `Effigy.MorphDriver.WeightsAt` | **Supported (Engine-Free)** | Swing-twist decomposition, distance measurement, curve evaluation, compound multiplication. |
| **Root Motion** | `RootMotion` | `Effigy.RootMotion.Extract`, `Effigy.RootMotion.Apply` | **Supported (Engine-Free)** | Planar and 3D root motion extraction and in-place animation offsetting. |
| **Physics Ragdoll** | N/A (Engine only) | Requires PhysX/Rubikon engine scene | **Engine-Dependent (Not Supported)** | Dynamic rigid bodies, joint limits, contact impulse solvers require full game physics world. |
| **Dynamic Cloth / Soft Mesh** | N/A (Engine only) | Requires volumetric FEM / GPU cloth solver | **Engine-Dependent (Not Supported)** | Full mesh deforming cloth/flesh dynamics beyond skeletal spring chains. |
| **Scene Raycast IK** | N/A (Engine only) | Requires engine scene spatial acceleration | **Engine-Dependent (Not Supported)** | Foot ground alignment probing static level geometry. Desktop uses explicit floor height. |

---

## 4. Deterministic Secondary Motion & Stepping Lifecycle

### 4.1 Fixed Time Stepping
Stateful secondary spring simulation must never advance according to variable render frame rates ($\Delta t_{\text{render}}$). Frame-rate-dependent simulation causes:
1. Divergent trajectories across different display refresh rates (e.g. 60Hz vs 144Hz vs headless test execution).
2. Numerical instability or explosion during large frame drops or viewport resizing pauses.
3. Non-reproducible test failures and headless verification drift.

**Mandatory Stepping Architecture**:
- Simulation is discretized into fixed sub-steps: $h = \frac{1}{120}\text{ s} \approx 8.333\text{ ms}$ (or $h = \frac{1}{60}\text{ s}$).
- Variable delta times $\Delta t$ accumulate into a simulation buffer:
  $$\text{accumulator} \leftarrow \text{accumulator} + \Delta t$$
- The physics engine advances strictly in whole increments of $h$:
  $$\text{while } \text{accumulator} \ge h \text{ do Step}(h)$$
- Any remainder $\text{accumulator} < h$ is preserved for subsequent frames or interpolated.
- Headless testing and bake operations advance with exact fixed steps without accumulator jitter.

### 4.2 Explicit Reset Lifecycle on Transport Events
Spring simulation carries persistent kinetic state (previous positions $\mathbf{p}_{\text{prev}}$ representing velocity in Verlet integration). When transport events occur, stale momentum must be eliminated immediately:
1. **Clip Reload**: Reset all nodes. Set current and previous tail positions to the target animated positions ($\mathbf{p} = \mathbf{p}_{\text{target}}$, $\mathbf{p}_{\text{prev}} = \mathbf{p}_{\text{target}}$).
2. **Seek / Scrubbing**: Scrubbing backward or jumping forward across the timeline invalidates continuous velocity. An explicit `ResetOnSeek()` reinitializes state to the new playhead frame.
3. **Loop Boundary**: When looping an animation clip, the simulation can either use a warm-up cycle (`loop = true` in `ChainSpring.Bake`) so the end flows seamlessly into the start, or reset cleanly at frame 0.
4. **Teleport Threshold**: A jump greater than $200\text{ inches}$ in a single frame is classified as a teleport rather than physical movement, automatically resetting the tail without velocity impulses.

---

## 5. Diagnostics and Error Handling Policy

When inspecting control rigs or evaluating constraints:
1. **Never Crash on Unsupported Constraints**: Unknown or experimental constraint types in a `.ctrlrig` file (e.g. `PhysicsConstraint`, `ClothConstraint`, `FutureConstraint_v9`) must be categorized as `Unsupported` or `EngineDependent` with clear diagnostic messages. They must never throw unhandled exceptions or corrupt valid constraints.
2. **Graceful Degeneracy Handling**:
   - Zero-length bone segments or collinear poles must fall back to stable perpendiculars without producing `NaN` or `Infinity`.
   - Out-of-reach IK goals must clamp cleanly to maximum chain reach and report positive miss distance.
   - Non-finite coordinates (`float.NaN`, `float.PositiveInfinity`) in constraint parameters must be rejected during audit with actionable error messages naming the offending bone and property.
3. **Partial Target Matches**:
   - Missing bones produce warnings naming the specific unmapped joints, allowing compatible portions of the rig to pose and solve.

---

## 6. Parity Follow-Up Roadmap & Action Items

Following this inventory, the next independent animation engineering tickets are:
1. **A2 — Morph Keys, Events & Pose-Driven Correctives**: Precedence integration between keyed curves, morph events, and `MorphDriver` evaluated weights in `SceneRenderer`.
2. **A4 — Allocation-Free Live IK**: Zero-allocation hot path for `ChainIk` and `TwoBoneIk` using pre-allocated matrix and vector scratch buffers.
3. **A6 — Part & Camera Track Evaluation**: Hierarchical composition for qualified part names and camera FOV channels separate from orbit camera controls.
4. **Follow-Up (Secondary Collision Expansion)**: Analytical mesh-bounding hull collision for secondary chains as a future engine-free extension to capsule colliders.
