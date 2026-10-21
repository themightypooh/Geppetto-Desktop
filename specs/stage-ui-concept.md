# Spec: Geppetto Desktop "Stages" UI Concept (`stage-ui-concept.md`)

**Core Premise:** Kill the gray CAD/DCC dock-soup (no stacked top ribbons, no left tree docks, no right `ControlSheet` property grids, no spreadsheet dope sheets). In Geppetto Desktop, **switching workflows changes the physical room ("Stage") the object sits in**, and each stage has **one signature, physical-feeling panel** that replaces the old sidebars and sub-docks while keeping every underlying kernel function intact.

---

## The 4 Workflow Stages (and Their 1 Signature Panel Each)

### 1. BUILD Stage (CAD Solids ⇄ Poly Cage on One Tape)
- **Why Switching Between CAD and Box/Poly Modeling Works Natively Here:**
  - In traditional CAD (SolidWorks/Fusion), bodies are NURBS B-Reps, so switching to polygon box-modeling requires a destructive one-way conversion. In Effigy's kernel, **every CAD feature (`Primitive`, `Profile`, `Blob`, `Fillet`) already outputs a quad/ngon `PolyMesh` (`Body.Mesh`)**, and [`MeshEditSession`](file:///home/poo/games/projects/geppetto/Effigy/MeshAdjacency.cs#L1514) + [`EditOpFeature`](file:///home/poo/games/projects/geppetto/Effigy/Features/EditOpFeature.cs#L12-L104) + [`MeshEditFeature`](file:///home/poo/games/projects/geppetto/Effigy/Features/SculptFeature.cs#L180) already sit as regular steps in `PartStudio.Rebuild`.
  - That means you can block out a shape with CAD sketches/booleans, flip into **Poly Cage** to `Loop Cut`, `Extrude`, `Bridge`, or pull vertices/edges like a box modeler, and flip right back to **CAD Solid** to run a `Fillet` or `CircularPattern` on top of your poly edits—all on the **same non-destructive history tape**.
- **The Stage Vibe — *The Machinist's Surface Plate*:**
  - The 3D space sits on a dark lapped granite block with scribed brass vernier rulings along its edges and crisp raking work-lamp light. Flipping into **Poly Cage** warms the granite rim to amber and overlays the live sub-D vertex/edge/quad control cage on the active body.
- **The One Panel — *The Blueprint Tape* (with the `[📐 CAD SOLID ⇄ 🕸️ POLY CAGE]` Bezel Latch):**
  - Instead of a vertical tree on the left and a parameter sheet on the right, a single **horizontal perforated tape** runs along the bottom edge of the screen with a two-way mechanical latch on its top bezel:
    - **`📐 CAD SOLID` (`PartStudio`):** Stamps on the tape create/edit parametric solids, sketches, SDF blobs, fillets, and radial patterns.
    - **`🕸️ POLY CAGE` (`MeshEditSession` / `EditOpFeature`):** Flipping the latch reveals an inline sub-object strip (`1 · Vert`, `2 · Edge Loop`, `3 · Quad Face` + `Loop Cut`, `Extrude`, `Inset`, `Bridge`, `Crease`) right on the top lip of the tape. Every poly operation stamps an amber `POLY CAGE` card onto the exact same tape (or records into the active cage card), so upstream CAD edits still flow through downstream poly edits.
  - **How you interact with it:**
    - **Scrubbing history is physical:** Grab the brass reader-bar (`PartStudio` rollback cursor) and slide it left/right across interleaved blue CAD stamps and amber Poly Cage stamps.
    - **No property inspector panel:** Clicking any CAD or Poly Cage stamp unfolds it *in-place* into a blueprint callout with direct leader lines pointing straight to the 3D dimensions or edge loop on the model.

---

### 2. SCULPT & FINISH Stage (Model / Paint / Clothing)
- **The Stage Vibe — *The Sculptor's Turntable & Caliper Stand*:**
  - The granite block drops away into a heavy cast-iron sculptor's armature plinth with a warm clay/studio cyclorama backdrop and a physical shadow-catching rim light that rotates when you spin the plinth.
- **The One Panel — *The Palette & Caliper Tray* (Replaces Sculpt Bar + Material Slots + Paint Layers + UV Panel):**
  - A single curved **swivel tray** tucked into the bottom-left corner of the viewport, angled like a physical workbench tray sitting just inside the frame.
  - **How you interact with it:**
    - **Material Wells, not Material Lists:** Your document materials (`engine_paint`, `engine_chrome`, `engine_soot`, `engine_brass`) sit in recessed circular wells around the rim of the tray, rendered as actual 3D lit spheres using the live PBR shader.
    - **Dip-and-Drop:** Drag from a material well straight onto a body or face in the 3D viewport to assign it, or flick a well onto the brush nib in the center of the tray to paint/sculpt with it.
    - **Brush & Falloff as a Physical Nib:** The center of the tray shows a live cross-section silhouette of your brush/sculpt profile (`Grab`, `Crease`, `Inflate`, `Seam Cut`). Drag the tip of the silhouette up/down to change strength; pinch its base left/right to change radius and falloff curve—no sliders.
    - **Flip the Tray for UVs (`Tab`):** Flipping the tray over reveals a square brass-framed linen square showing the live UV packing (`NormalBake.Measure` coverage gauge on the frame bezel) so you only see UVs when you flip the tray.

---

### 3. RIG Stage (Skeleton, Constraints, Limits, Skin Weights)
- **The Stage Vibe — *The X-Ray Vitruvian Frame*:**
  - The studio darkens into a deep blueprint indigo; the mesh turns into a smoked-glass shell (`alpha 0.35`) suspended inside a subtle brass calibration hoop so internal bones, joint cones, and heat weights glow inside the volume like an anatomical plate.
- **The One Panel — *The Control Cross (Crux)* (Replaces Bone Tree + Constraints Panel + Weight Bar):**
  - A single vertical **anatomical diagram / marionette paddle** floating on the right edge that automatically arranges the rig's bones into a clean 2D symmetrical silhouette (Spine down the center, Left/Right limbs flanked out, cables/tails fanned below) using the skeleton's actual bind hierarchy (`Skeleton.Bones`).
  - **How you interact with it:**
    - **No scrolling through 95 bone names:** You click the left elbow dot on the 2D silhouette, or box-drag across a whole tentacle chain. Mirroring (`RigFlip.SwapSide`) is literally a toggle bar down the center spine of the diagram.
    - **Joint Limits on the Dot:** Selecting a bone expands its dot on the paddle into a circular **protractor dial** showing the exact Pitch/Yaw/Roll cone (`RigLimit`). Drag the brass stops on the rim of the dial to clamp the joint limits while watching the 3D bone test-flex against its stops in real time.
    - **Stringing IK & Springs by Drawing a Line:** To create a `TwoBoneIk`, `ChainIk`, or `ChainSpring`, you don't click "+ Add Constraint" and pick bone names from dropdowns—you **drag a taut cord** on the paddle from `hand_R` up to `arm_upper_R`. Plucking the cord toggles it between rigid IK, FABRIK/Spline, and a spring chain (`ChainSpring.Presets`).

---

### 4. MARIONETTE Stage (Animation, Posing, Curves, Take Check)
- **The Stage Vibe — *The Soundstage Cyclorama & Floor Chalk*:**
  - Not cheesy red velvet curtains—think a **stop-motion / film blocking stage**: a matte charcoal soundstage floor with luminous **chalk blocking marks** (`RigMark`), subtle overhead barn-door key/rim lights you can grab by their rim halos on the edge of the screen, and faint stroboscopic motion-arc trails (`RigViewport.Overlays`) hanging in the air like a long-exposure photograph.
- **The One Panel — *The Music-Box Drum (Choreography Strip)* (Replaces Dope Sheet + Graph Editor + Transport + Take Check):**
  - Instead of a 30-row spreadsheet dope sheet on the bottom and a separate `RigGraphEditor` window, a single **wide, low-profile revolving cylinder / ribbon** at the bottom of the stage that fuses keys, curves, and motion linting into one tactile strip.
  - **How you interact with it:**
    - **Only shows what you touch:** The strip does not list every bone in the rig. It shows **one unified composite lane** for the whole pose, plus the individual curves *only for the bone(s) or IK goals you currently have grabbed*.
    - **Keys & Curves are the Same Thing:** Keyframes sit as brass pins along the strip. Between any two pins, the connecting thread *is* the live easing curve (`RigHermite` / `RigEase`). Instead of opening a separate Graph Editor or right-clicking an interpolation menu, you **grab the thread itself between two pins and bend it**:
      - Pull it taut = `Linear`
      - Snap it flat-and-step = `Stepped`
      - Bend its belly up/down/S-curve = live `Cubic` / `Monotone` tangents (`AutoTangent` / `SetSlope`).
    - **Physical Drum Scrubbing:** Flicking the strip with the mouse wheel or dragging the heavy flywheel on its left edge spins the playhead with inertia (or ticks frame-by-frame with a mechanical detent when holding `Shift`).
    - **Secondary Motion & Take Check Built Into the Thread:**
      - Right-dragging across a span of pins on a bone chain "brushes" a wave or spring follow-through (`ClipBake.Secondary` / `ClipBake.Wave`) directly onto the thread, showing the rippled preview before you release.
      - `TakeAudit` / `MotionLint` runs quietly in the background and marks self-intersections or velocity pops (`44 deg/frame`) as **amber burn-marks directly on the ribbon**—click a burn-mark to snap the stage straight to the offending frame and highlight the colliding mesh pair in red.
