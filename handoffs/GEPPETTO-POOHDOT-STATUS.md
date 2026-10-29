# Geppetto in Poohdot: status and plan

Last updated: 2026-10-29 (keep this file current as work lands).

Goal: Geppetto works inside Poohdot just as it did in s&box. It should work the first time and load automatically in every Poohdot project.

Poohdot is `/home/poo/src/poohdot/godot/bin/godot.linuxbsd.editor.x86_64.mono`. Always call it Poohdot, and don't edit the engine.

## CURRENT STATE (read this first, 2026-10-29)

- **Code:** `godot-plugin-baseline` @ **2ac1768** (sketch-mode-look, fast-forwarded 2026-10-29; before it 8ef2022 sketch-calm) holds everything, and every tool branch is merged into it. The main tree still has uncommitted files that aren't mine (the godot-ui-mock cache and handoffs docs). Another agent shares that tree, so don't revert them.
- **Installed:** 2ac1768 is published (2026-10-29) to `~/.local/share/poohdot/geppetto` with `install.sh publish`. Injected projects, from `injected.txt`: `~/games/godot/geppetto-test`, `~/games/godot/tamagatchi/godot` (never test there), and a scratch copy. Both real projects build (0 errors on 2ac1768). Poo just reloads the project in Poohdot.
- **Verified on 7a98c8b:**
  - All 11 headless `tools/*_test.gd` suites pass (sketchedit 46/46).
  - All 10 hidden window runs end `fails=0`: Default, Blank, CadAudit, Deep, SketchTools, NoModelAudit, SketchEdit, RigAudit, ClothingAudit and AnimateAudit.
  - The published-addon copy of geppetto-test passes Blank and Default.
  - On 8ef2022: the published-addon copy passes Blank (`RESULT fails=0`, with the new calm checks). The other runs weren't re-run for this small change.
  - On 2ac1768 (sketch-mode-look): all 11 headless suites pass (sketchedit 51/51), and Blank, CadAudit, SketchTools, SketchEdit and Default end `fails=0`.
    - sketch-calm had broken CadAudit (5 fails): the new model is no longer selected, so the camera isn't brought in and its 4-unit clicks snapped together. Phase 1 now scales its coordinates until 4 units is ~60px.
    - CadAudit's import step could crash the editor: its .obj was written under res://, got auto-imported, and the import progress dialog was parented to the file dialog being freed. It now writes to user://.
- **Sketch mode look (2ac1768):** amber 4px frame + SKETCH tag, a pill bar at top centre ("Sketching on <plane>", Line/Rectangle/Circle/Arc/Trim/Dimension, Finish, Cancel, ?), plane-coloured grid (major line every 4th), model dimmed via a RenderingServer overlay. Other tools can use the same frame/bar through `IGeppettoModeTool`. Cancel discards; Esc still finishes. No camera ease: Poohdot has no editor camera API.
- **Window-run runner:** `winruns.sh` (scratchpad, so it may be gone). It is just a loop. For each `Mode:scene` it copies the project, then runs the gamescope command under Testing with `GEPPETTO_WINDOW_WS=<Mode> GEPPETTO_WINDOW_SCENE=res://<scene>`.
  - Scenes: Default/RigAudit/ClothingAudit/AnimateAudit use test.tscn. Blank/CadAudit/Deep/SketchTools/NoModelAudit use blank.tscn. SketchEdit uses sketch.tscn.
  - The test project for SketchEdit needs `sketchbox.effigy`. If it was damaged, restore it from `tools/live/fixtures/sketchbox.effigy`.
- **What Poo asked for and got:** sketching works in an empty scene. A CAD or Model tool makes `res://models/modelN.effigy`, and the Top/Front/Right planes show. Gemini's 14 sketch tools and snapping work, along with all 17 constraints plus D (dimension), on hotkeys and the CAD > Sketch dial rings (Draw/Shapes/Modify, 24 tiles). CAD, Model, Rig, Clothing and Animate are audited and fixed (item 6 below).
- **Changes made at the merge:**
  - The Default run's primitive check now expects `GeppettoCad.FitToModel` placement (beside the model, sized to it; 12u at the origin when the document is empty) instead of "at origin".
  - `GeppettoTools.Warn` uses `GeppettoToast` (cut to fit).
- **Known bugs, not fixed:**
  - A Garment on line_cook.effigy comes out a flat sliver (kernel ClothingFeatures fit).
  - The camera doesn't re-frame on a new model.
  - Editing a library material doesn't update characters already using it.
  - `GarmentCheck` double-counts vertices across bodies.
- **WAITING ON POO (new panels, design + sign-off before building):** the sketch toolbar, the right-click sketch pie, the Constrain menu and the editable in-viewport dimension box. Poo wants mocks in real Poohdot, not HTML.
  - Built but still unapproved: ModelOpPopup, Shape Keys panel, Events row, Wearer file dialog, Shape garment popup, Material box and the material right-click menu.
- **Next work if Poo says go:**
  1. Mock the 4 sketch panels in Poohdot for sign-off.
  2. Re-frame the camera on a new model.
  3. Fix the Garment sliver in the kernel.
- **sketch-calm (2026-10-29, Poo approved; merged and published as 8ef2022):** a calmer "pick a plane" moment.
  - The auto-made model isn't selected, so Godot's move/rotate rings don't show. Tools and the Features dock find it as the scene's first character.
  - The Top/Front/Right and datum planes only draw while a tool wants a plane (Sketch before one is picked, or the Plane tool): a faint wash and a thin outline, with no name boxes. The hovered plane gets a stronger wash, a solid outline, and its name beside the mouse.
  - The planes are twice as big: empty model 48in half-size, 2.0x model extent, datum 0.8x.
  - Choosing a plane shows one hint line: "Sketch: click a plane · Esc to cancel".
  - Blank run has new checks: nothing selected, planes wanted, a plane hovered, planes gone once one is picked. Shot: `shots/sketch-calm.png`.
- **Docs:** `AUDIT-AND-SKETCH-NEXT.md` (Steps 1–4 done) and `CAD-SKETCH-PARITY.md` (the s&box vs Poohdot sketch table).

## Branches and where the code is

| What | Where |
|---|---|
| Backend G0–G15 (bridges, importer, save, GLB, harness) | branch `godot-plugin-baseline` in `~/Desktop/geppetto-desktop`. No git remote, so no PR. |
| Dial and all tools (CAD, Model, Rig, Paint, Animate, Clothing, materials, sketch) | **All merged into `godot-plugin-baseline` at 7a98c8b (2026-10-28).** geppetto-tools, sketch-edit, sketch-snap and audit-cad/model/rig are all merged, so there are no open feature branches. The last working worktree was `.claude/worktrees/sketch-edit` (same commit). |
| Plugin code | `godot-plugin/addons/geppetto/`, with `Dial/` for the dial, ring and material library, and `Tools/` for each workspace's tools |
| Engine-free kernel | `kernel/` (Effigy, Marionette) and `core/` |
| s&box original, for parity | `~/games/projects/geppetto` |

Build the plugin with `cd <worktree>/godot-plugin && dotnet build -c Debug`, which builds G1Spike.csproj.

## Install, launcher and watcher (every project gets Geppetto)

- `~/.config/godot/geppetto/install.sh` keeps one shared copy in `~/.local/share/poohdot/geppetto` and wires each project to it.
  - `publish <checkout>` copies kernel/, core/ and addons/geppetto into the shared copy. **Run this after every change**, then `dotnet build` in each project, or let the next launch rebuild it.
  - `inject <proj>` symlinks `addons/geppetto`, adds a csproj, .sln and nuget.config that import `Geppetto.props` (Debug-only refs, so exports strip it), enables the plugin, and builds.
  - `inject-all` injects every project in the list.
  - `inject-new` injects new projects and rebuilds stale ones. It skips projects open in a running editor.
  - `eject` undoes an inject and keeps `*.bak-geppetto-*` backups.
- `~/.local/bin/poohdot` is the launcher wrapper. It runs inject-new, then starts Poohdot.
- `~/.config/systemd/user/geppetto-inject.path` (with its `.service`) watches Poohdot's project list (`~/.local/share/godot/projects.cfg`) and runs inject-new.
- Tamagatchi (`~/games/godot/tamagatchi/godot`) is injected. **Don't run tests in it, and don't touch its scene files.**

## Testing

- **Test project:** `~/games/godot/geppetto-test`, scene `test.tscn`, with 6 cookin players plus the barracuda, plus `characters/*.effigy` and `materials/`.
  - Automatic window runs save the scene, so run them on a throwaway copy. The current copy is scratchproj in the session scratchpad.
- **Headless tool tests:** `addons/geppetto/tools/*_test.gd`. They cover animate, cadpick, cad, clothing, document_smoke, model, paintshow, paint, pose_ik and rig.
  ```
  env -u DISPLAY -u WAYLAND_DISPLAY $POOHDOT --headless --path <proj> -s res://addons/geppetto/tools/<x>_test.gd
  ```
- **Hidden window run** (the real editor, offscreen, so nothing pops up on Poo's screen):
  ```
  env -u WAYLAND_DISPLAY GEPPETTO_WINDOW_RUN=<outdir> [GEPPETTO_WINDOW_WS=<Workspace|Materials>] [GEPPETTO_WINDOW_SCENE=res://...] \
    timeout 600 gamescope --backend headless -W 1920 -H 1080 -w 1920 -h 1080 -- \
    $POOHDOT --editor --path <copy> --display-driver x11 --resolution 1920x1080
  ```
  - It writes `<outdir>/run.txt` (ok/FAIL lines ending in `RESULT fails=N`) and numbered screenshots.
  - The code is in `GeppettoPlugin.WindowRun.cs`. It checks clipped text, that the dial is on screen, and that the editor's minimum size fits the window.
- **CPU suite:** `dotnet run --project tests/Desktop.Tests.csproj -c Release`. Its 18 failures are in the parked desktop viewer, not the plugin.

## Material format (agreed with the Linework thread)

- Each material lives at `<root>/<name>/<name>.tres`, a StandardMaterial3D.
  - Optional maps: `<name>_albedo.png`, `_orm.png` (R=AO, G=rough, B=metal), `_normal.png`, `_height.png`, `_emission.png`.
  - Sidecar `<name>.json`: `{name, tags, swatch, rough, metal, source}`.
- There are two roots:
  - `res://materials`: this project only
  - `~/.local/share/poohdot/materials`: the user library that every project sees. Tests must not write here.
- Delete moves the folder to `<root>/.trash/<name>-<timestamp>`.
- The code is `Dial/GeppettoMaterialLibrary.cs`, the box is `Dial/GeppettoMaterialDialog.cs`, and the menu and applying are in `GeppettoPlugin.Materials.cs`.
- A packed ORM is read from G/B in `EffigyMaterialBridge.cs`.

## Done

- **Backend G0–G15:** import, live rebuild, save to .effigy/.riganim, GLB export, pose/IK, control registry, harness.
- **Dial and tools:** CAD (with picking), Model ops, Rig, Paint (with display), Animate (timeline, shape keys, events), and Clothing.
- **Global install,** launcher and watcher. Tamagatchi and geppetto-test are injected.
- **Material manager** (commit a8efd6e), passing the hidden window run with 0 fails:
  - the "+" ball makes a custom material: colour, roughness, metal, an optional texture, and where to save it
  - right-click a ball for: Use on selected faces, Use on whole slot, Edit, Copy, Rename, Delete
  - picking in Model with faces selected moves those faces onto the material's slot as one undo step
- **Features dock height bug** (commit 847a035). An unsized wrapped label made the editor about 1800 px tall, which pushed the bottom panel off screen. Labels now have a width, and the dock scrolls.

## In progress / known problems

1. **FIXED 2026-10-28 (geppetto-tools, uncommitted): the Model view went grey after any edit.** Cause: `GeppettoDocument` built its MaterialResolver on the .effigy's own folder, but .vmat slots resolve from `EffigyMaterialBridge.MaterialRoot` (the folder holding `materials/`), like the importer. So every rebuild after an edit, of any kind, lost the slot looks. The window run now counts coloured Body surfaces before and after the Model face pick and fails if any go grey (5 → 6 in the run).
2. **FIXED 2026-10-28: "+" is now ball 0 on ring page 1** (`GeppettoDialData.RingMaterials`). The Materials run fails if it isn't.
3. **Hang FIXED 2026-10-28: the full window run froze at Clothing > Check.** The check itself takes seconds, but on the test scene it finds ~263k clip points, and `ClothingCheck.MarksTool` drew a circle for every one each frame. It now draws the 4000 deepest (the message still gives the full count) and prints at most 12 console lines per garment. Check, Push out and Clear marks all pass now.
   - **Not reproduced (2026-10-28): the editor segfaulted once in `SaveScene` at the end of the full run** (full2). A later full run with save probes and a plain full run (full3) both ended `RESULT fails=0` with no crash, after the dial redraw fix. Leaving the notes in case it comes back:, right after the .effigy and .riganim are written. No backtrace (the Poohdot build has no symbols). Every pair saves cleanly (Animate, Clothing, Rig+Animate, Clothing+Animate, Paint+Animate, CAD+Model+Animate, Materials+Animate, Primitives+Animate), so it takes the whole run's state. The core dump (`coredumpctl`) only shows the crash in stripped engine code. `GEPPETTO_WINDOW_SAVE_EACH=1` saves between workspaces to find the step that leaves the scene unsaveable. `GEPPETTO_WINDOW_WS` now takes a list, e.g. `Clothing,Animate`.
   - Side note (kernel, not fixed): `GarmentCheck.Run` counts a vertex once per worn body, so "N vertices clip" can be more than the garment has.
4. **Editing a library material doesn't update characters already using it,** because looks are copied by value. The delete box says so.

5. **FIXED 2026-10-28 (sketch-edit 409fcec): "Select a Geppetto character first" / nothing to sketch on in an empty scene.** Tools now pick up the scene's first character without a selection. In a scene with none, a CAD or Model tool writes `res://models/modelN.effigy` (empty), imports it, adds it at the origin as one undo step ("Add Geppetto model"), then runs the tool, so the Top/Front/Right planes show and can be sketched on. Empty documents import as an empty mesh. Hidden run `GEPPETTO_WINDOW_WS=Blank` (blank.tscn, nothing selected: Sketch → square → Extrude → box) passes with fails=0.
6. **DONE 2026-10-28: parallel audit-and-fix, merged into sketch-edit (640c8a9).** Branches `audit-cad`, `audit-model`, `audit-rig`, `sketch-snap` (cut from 409fcec) all merged. Highlights:
   - CAD: "pick X first" now a toast; settings box shows part names not body ids and sizes to its rows; plane names step apart. Hidden run `CadAudit`.
   - Model: every Model op works in an empty scene; Bevel on the dial; material ring Edit/Rename/Delete keep slots; Alt-click picks a hole's whole rim. Hidden run `Deep`.
   - Rig/Clothing/Animate: first viewport click no longer dropped (SetInputEventForwardingAlwaysEnabled); Move/Rotate/Scale press the real toolbar buttons; clothing details move onto cloth; long toasts are cut to fit (GeppettoToast). Hidden runs `RigAudit`, `ClothingAudit`, `AnimateAudit`, `NoModelAudit`.
   - Sketch: Gemini's snapping + tools verified by real clicks (`SketchTools` run), Trim/Extend picking fixed, CAD > Sketch has Draw/Shapes/Modify rings (24 tiles), all 17 constraints + D dimension on hotkeys. Table: `CAD-SKETCH-PARITY.md`.
   - All 11 tools/*_test.gd pass on the merge (sketchedit 46/46).
   - Known, not fixed: Garment on line_cook.effigy comes out a flat sliver (kernel ClothingFeatures fit); a new model isn't re-framed by the camera.
   - Still needs pooh's layout OK (not built): sketch toolbar, right-click sketch pie, Constrain menu, editable in-viewport dimension box.

## Remaining, in order

1. Fix items 1–3 above. Done when a hidden run shows coloured faces in Model, "+" is on page 1, and the full run finishes with `RESULT fails=0`.
2. **Add Primitive DONE 2026-10-28 (uncommitted).** CAD already had it: the shape menu, then the settings box with size and segments, added at the origin like s&box (Godot has no 3D cursor; Position is in the box). Now it's also first in Model's Add stage, through the same code. A new Torus starts with tube radius 0.15, because the kernel default (tube = radius) refuses to build. The hidden run `GEPPETTO_WINDOW_WS=Primitives` adds all 9 shapes from both fans, checks +1 feature, +1 body, at the origin, then undo/redo/undo, and shoots each box: `RESULT fails=0`.
3. **CAD sketch tools parity with s&box** (`~/games/projects/geppetto`):
   - lines, arcs, circles, rectangles, splines, constraints, extrude/revolve from a sketch
   - write an in/missing table here first, then close the gaps
   - **Table DONE:** `handoffs/CAD-SKETCH-PARITY.md`. Poohdot has one tool, a one-shot closed polyline; the kernel already has everything else. **Blocked on pooh:** the next steps (re-edit a sketch + Select, then lines/rectangle/circle/arc with snapping, then the Constrain menu) need a sketch toolbar and a Constrain menu, which are new panels, so pooh signs off on their layout first.
   - done when each is exercised in the window run
   - **2026-10-28 audit + next steps:** pooh found the CAD tools broken in real use. Primitive/Plane/Sketch-on-plane/Extrude fixed and click-tested on branch `sketch-edit` (uncommitted). **Step-by-step of everything left (audit the rest, snapping, every sketch tool, constraints) + the real-click harness: `handoffs/AUDIT-AND-SKETCH-NEXT.md`.**
4. **DONE (7a98c8b, published).** Merge `geppetto-tools` into `godot-plugin-baseline`, carefully, because another agent shares the tree. Then publish and rebuild geppetto-test and Tamagatchi.
5. **Give Poo try-it steps.** Flag the new panels for sign-off: ModelOpPopup, Shape Keys panel, Events row, Wearer file dialog, Shape garment popup, Material box, and the material right-click menu.
   - **Try-it steps (written 2026-10-28).** These fixes are only on `geppetto-tools`. Your `geppetto-test` uses the published addon (`~/.local/share/poohdot/geppetto`), so they show up there after step 4 (merge + publish).
     1. Open `geppetto-test` in Poohdot, select Stringbean, and pick **Model** on the dial. Pick a face and do any edit (Extrude, say). The body keeps its colours. It used to go grey.
     2. Open the material ring. **"+"** (new material) is the first ball on page 1.
     3. **Model > Add > Primitive** (also CAD > Sketch > Primitive): pick a shape, set the size in the box, then OK. It lands at the origin. Ctrl+Z removes it, and Ctrl+Shift+Z brings it back. Torus works too.
     4. **Clothing > Check** on Stringbean: it answers in a second or two and marks the deepest clips yellow to red. The editor stays responsive. Then **Push out** and **Clear marks**.
     5. Ctrl+S, close the scene and reopen it: the edits are still there.
   - **Panels that need your sign-off** (built, but no layout OK yet): ModelOpPopup, Shape Keys panel, Events row, Wearer file dialog, Shape garment popup, Material box, and the material right-click menu. Add Primitive adds no new panel: it reuses CAD's shape menu and settings box.

## Rules that bite

- Never use bare `git stash`, never `git checkout --` over others' edits, and don't delete obj/bin.
- Bake, never generate at runtime.
- No text clipping. Verify with screenshots.
- Replies to Poo stay short and plain.
- Linework is in its own thread. Don't build it here.
