# Geppetto in Poohdot: audit + sketch port, what's left (2026-10-28)

pooh's verdict on the tools: "its allll shit". The job is to click every tool for real, fix whatever is broken, then finish porting the s&box sketch tools. Work through this file top to bottom and tick things off as you go.

> **Status 2026-10-28 (late):** Steps 1–4 are DONE and committed on `sketch-edit` (merge 640c8a9): every workspace audited with real clicks in hidden window runs, Gemini's snapping and sketch tools verified and fixed, all 17 constraints and dimensions on hotkeys. See `GEPPETTO-POOHDOT-STATUS.md` item 6. Left: panels that need pooh's layout OK (sketch toolbar, sketch pie, Constrain menu, dimension box), then merge into `godot-plugin-baseline` and publish.

## Where the work is

- **Worktree:** `~/Desktop/geppetto-desktop/.claude/worktrees/sketch-edit`, branch `sketch-edit`, cut from `geppetto-tools` a8efd6e.
  - Committed (was uncommitted earlier; pooh said just fix).
  - Run `git status` there first: about 20 modified files plus new ones (`Tools/CadPlanes.cs`, `Tools/CadSketch.Edit.cs`, `GeppettoPlugin.LiveRun.cs`, `GeppettoPlugin.SketchRun.cs`, `tools/sketchedit_test.cs`, `tools/live/`).
- **Plugin code:** `godot-plugin/addons/geppetto/`. All paths below are relative to it.
- **Leave alone:** the `geppetto-tools` worktree, where another session has uncommitted edits. Never use bare `git stash` or `git checkout --`. Don't publish to `~/.local/share/poohdot/geppetto`. Don't test in Tamagatchi.
- **s&box reference (the behaviour to match):** `~/games/projects/geppetto/Editor/EffigyEditor/`.
  - Sketch tools: `EffigyViewport.Sketching.cs`, `EffigyWindow.cs` (`BuildSketchStages`), `EffigyWindow.Radial.cs`.
  - Constraints: `EffigyViewport.Constraints.cs`.
  - Feature dialog: `EffigyFeatureDialog.cs`.

## Done in this pass (sketch-edit, uncommitted, checked with real clicks)

| Bug | Fix | Where |
|---|---|---|
| Primitive made a 1u box buried inside the model | Sized from the model and placed beside it (`FitToModel`) | `Tools/Cad.cs` |
| Feature dialog: editing a value didn't update the model | `studio.MarkDirty(feature)` in Preview, Commit and Revert. Rows rebuild when the param set changes (e.g. switching Shape) | `Tools/GeppettoFeatureDialog.cs` |
| A Plane feature drew nothing, and Sketch couldn't use it | Planes drawn as a lilac square with their name. Sketch takes a click inside one (the nearer of plane and face wins) and sets `SketchFeature.PlaneFeatureId` | `Tools/CadPlanes.cs`, `Tools/CadSketch.cs`, `GeppettoPlugin.Viewport.cs` |
| Plane dialog "From" said Top for a plane on a face | Shows "The picked face" | `GeppettoFeatureDialog.cs` |
| A finished sketch vanished from the viewport | Drawn amber until something consumes it | `Tools/CadPlanes.cs` |
| Extrude/Revolve/Sweep/Loft dialog: the Sketch row was blank | Dropdown of the real sketches, which sets `SketchFeatureId` and clears the region seeds. Cancel puts back the sketch id and seeds (`ParamSnapshot`) | `GeppettoFeatureDialog.cs` |
| Refusals ("draw a Sketch first", etc.) only went to the console | `GeppettoToolContext.Warn` shows an editor toast too. All `GD.PushWarning("[Geppetto] …")` in Tools/ and the plugin were swapped over | `Tools/GeppettoTools.cs` + about 15 files |

**Proven by clicks:** Primitive box beside the model → plane on a face, offset 3 → triangle sketched on the plane → Extrude 4u → triangular prism, 0 errors.

## Progress this session (2026-10-28, continued)

Harness set up in `/tmp/opencode/gt` (addons symlinked to this worktree, kernel/core via `Geppetto.props`), editor live on `:2`, output in `/tmp/opencode/live1`.

**Code fixes**
- `Tools/CadSketch.cs`: `out RayHit hit` → `out MeshHit hit` (compile error; `MeshHit` is the kernel's struct in `Effigy/MeshRaycast.cs`).
- `Tools/GeppettoFeatureDialog.cs` (FloatParam): hook the spin slider's lazily-created internal `LineEdit` for `TextSubmitted` so typed values commit `f.Value` and call `Preview()`; added `EnumerateLineEdits` helper.

**Verified**
- Build: 0 errors.
- Script tests (headless): `cadpick_test` 58/58, `sketchedit_test` 17/17, `cad_test` 100/100, `model_test` 68/68 — all pass.
- Chain opens plane → sketch → Extrude dialog; sketch path reports 0 errors. The chain's final face-extrude step reports "That face cannot be pulled this far" (`kernel/.../SketchFeatures.cs` `PullFaces` → `FaceMove.Offset`), a geometry limitation of that pillar face, not a dialog/preview defect.

**Still to do (in order)**
- Step 1 item 1: re-validate the typed-distance preview on a sketch-based Extrude (blocked above by the face-extrude case; needs a clean manual extrude).
- Step 1 items 2–8: click-audit CAD > Solid (face extrude, Revolve, Sweep, Loft, Part, Boolean), CAD > Detail, CAD > Spline, CAD > Profile/Import/Repeat/Finish, feature tree, model workspace, Paint/Rig/Clothing/Animate.
- Step 1 items 10–11: hidden-window run (`GEPPETTO_WINDOW_RUN`/`GEPPETTO_WINDOW_WS`), send pooh before/after shots.
- Step 2: snapping (`SketchSnapper`) in `CadSketch.cs` + `CadSketch.Edit.cs`, snap marker, implied constraints.
- Step 3: port the sketch tools (midpoint line, rectangles, circles, arcs, point, polygons, slot/ellipse/spline, trim/extend/fillet/offset/mirror/cut, project, construction toggle, profile inspector, plane offset while picking).
- Step 4: constrain menu (17 constraints), dimension labels, constraint glyphs.
- Panels needing pooh's sign-off before building: sketch toolbar/pie, constrain menu, dimension edit box (mock screenshot first).

## Step 0: set up the harness (about 5 minutes)

The tests use real XTest clicks into a headless editor. Never send input to display `:0`, and never use `ydotool mousemove -a` (it wrecks pooh's desktop).

1. **Make a throwaway project.**
   - Copy `~/games/godot/geppetto-test` somewhere in your scratchpad, e.g. `$P`.
   - In `$P/addons`, replace `geppetto` with a symlink to this worktree's `godot-plugin/addons/geppetto`.
   - Copy `tools/live/fixtures/sketch.tscn` to `$P/` and `tools/live/fixtures/sketchbox.effigy` to `$P/characters/`.
2. **Point the build at this worktree's kernel and core.**
   - Write a `Geppetto.props` that adds ProjectReferences, in Debug only, to `<worktree>/kernel/Effigy.Kernel.csproj` and `<worktree>/core/Geppetto.Core.csproj`.
   - Add `<Import Project="…/Geppetto.props" />` to `$P/GeppettoTest.csproj`.
   - Run `cd $P && dotnet build -c Debug`.
3. **Start the editor.**
   ```bash
   export LIVE_PROJ=$P LIVE_OUT=$P/../live1
   tools/live/live.sh start
   ```
   - It waits for `$LIVE_OUT/ready`.
   - Input goes to `:2`; check that the log says `Starting Xwayland on :2`. If it says another number, set `LIVE_DISPLAY`.
4. **Drive it and look.**
   - Click: `live.sh x c X Y w 1`.
   - Screenshot: `live.sh shot NAME`. It writes `NAME.png` (taken in-engine; outside screenshots don't work) and `NAME.txt`, which has the active tool, the feature count, the mesh AABBs and the rebuild errors.
   - Crop to the viewport: `live.sh crop NAME`.
   - The plugin's own messages: `grep -a 'Geppetto\]' $LIVE_OUT/log.txt`.
5. **After every code change:**
   - Rebuild with `dotnet build -c Debug` in `$P`.
   - Run `live.sh start` again. The editor doesn't hot-reload in this harness, and a restart loses unsaved features.
   - `live.sh chain` replays plane → sketch → Extrude dialog.
6. **Harness quirks.**
   - While a non-embedded dialog has focus, click coordinates are relative to the dialog, not the screen. `NAME.txt` prints each window's position.
   - The pointer warp can pop up a stray tooltip that eats Escape. Use the dialog's Cancel button instead.
   - The model sometimes vanishes in the viewport while a dialog is open. That's a render artifact of the harness and it comes back afterwards.
   - The dropdown (OptionButton) popups didn't open under XTest. Test those in a script test instead.
7. **Screen coordinates** (1920×1080, `sketch.tscn`, dial bottom-left):

   | Spot | Click |
   |---|---|
   | Dial centre (Back / workspace) | 436 595 |
   | CAD > Sketch stage | 514 640 |
   | Sketch stage: Sketch / Plane / Primitive | 514 648 / 514 725 / 436 768 |
   | Primitive: Box / Cylinder | 470 806 / 483 829 |
   | CAD root: Solid stage | 515 715 |
   | Solid stage: Extrude | 526 685 |
   | The pillar's face (a good face to click) | 940 410 |
   | Primitive dialog (local): Width / Add / Cancel | 280 96 / 122 398 / 296 398 |
   | Plane dialog (local): Offset / Add | 280 95 / 122 217 |
   | Extrude dialog (local): Distance / Add / Cancel | 280 133 / 122 362 / 296 362 |

   Positions on other pie pages: take a shot, crop around the dial (`crop((290,500,700,830))`), and read them off.

## Step 1: finish the audit (do these in order)

For each tool:
1. Click it for real.
2. Take a shot and look at it.
3. Check the `.txt`: the feature count went up by one, the AABB changed, and `report errors=0`.
4. Click Cancel and check that it reverts. Use Ctrl+Z if there's no dialog.
5. Fix what's broken.
6. Add a row to the "Done" table above.

What counts as broken:
- nothing visible happens;
- the result is buried or too small to see;
- a blank or lying dialog row;
- the preview doesn't update;
- Cancel doesn't revert;
- a refusal that only goes to the console;
- any text clipped (the HARD RULE: check at min, default and max zoom).

1. **Check the Extrude distance preview first.**
   - Typing a new Distance + Return in a new Extrude didn't change the AABB in the next shot; Add did use the value.
   - Repeat with `w 3` before the shot. If it still doesn't move, the `EditorSpinSlider.ValueChanged` path isn't firing `Preview()` on typed input. Hook `GetLineEdit().TextSubmitted` too (`GeppettoFeatureDialog.Editor`, `FloatParam` case).
2. **CAD > Solid:** Extrude from a face (not a sketch), Revolve (needs an axis), Sweep (sketch + Spline), Loft (2 sketches on 2 offset planes), Part, Boolean (two Primitives that overlap).
3. **CAD > Detail:** every item on the page (Fillet, Chamfer, Shell and others; read them off a shot). Pick edges on the box primitive.
4. **CAD > Spline:** draw a 3D spline, then use it as the Sweep path.
5. **CAD > Profile, Import, Repeat, Finish:** each one once.
6. **Feature tree** (Inspector dock, "Geppetto Features"):
   - double-click a sketch → Edit sketch;
   - Edit... → the dialog;
   - suppress, hide, delete, rename;
   - Ctrl+Z / Ctrl+Shift+Z after each.
7. **Model workspace:** Add > Primitive; pick a face → Extrude / Inset / Bevel. The body must keep its colours after an edit (it used to go grey).
8. **Paint, Rig, Clothing, Animate:** one real action each, then a shot. Clothing > Check must answer within 2 seconds on the test scene.
9. **Re-run the script tests**, headless:
   ```bash
   env -u DISPLAY -u WAYLAND_DISPLAY $POOHDOT --headless --path $P -s res://addons/geppetto/tools/<x>_test.gd
   ```
   - Run all `tools/*_test.gd`.
   - `cadpick_test` and `sketchedit_test` call `ChoosePlane`/`BuildSketch`, which now take an optional `planeFeatureId`, and the dialog now hides the Sketch ChoiceParam. Last known: cadpick 58/58, sketchedit 17/17.
   - Fix any test the changes broke. Don't delete checks to make them pass.
10. **Hidden window run:** `GEPPETTO_WINDOW_RUN=… GEPPETTO_WINDOW_WS=Workspace` (see `GEPPETTO-POOHDOT-STATUS.md` § Testing) must end with `RESULT fails=0` and 0 clipped text.
11. **Send pooh** the before/after shots (SendUserFile) and a short list of what was broken.

## Step 2: snapping (needed before any of the new sketch tools are any good)

The kernel already has it: `kernel/Effigy/Sketch/SketchSnap.cs` (`SketchSnapper`).

1. In `Tools/CadSketch.cs` + `CadSketch.Edit.cs`, run every plane-space click and hover point through `SketchSnapper`. Copy which targets s&box enables: existing points, midpoints, curve-on, grid, and the host face's edges when the sketch is on a face.
2. Draw the snap marker at the hover point and give it a different colour per snap kind, as s&box does (`EffigyViewport.Sketching.cs`).
3. Add the Coincident / PointOnCurve constraint the snap implies when the click lands, as s&box does. Check how s&box records it before you copy it.
4. **Test:**
   - add checks to `tools/sketchedit_test.cs` (click near a point → lands exactly on it, and the constraint is added);
   - a live shot with the marker showing.

## Step 3: sketch tools (one at a time, same pattern)

Every tool needs:
- a `SketchToolKind`-style mode in the edit session (`CadSketch.Edit.cs` already has Select = S and Line = L);
- a click handler that builds kernel curves (`Sketch.AddLine / AddArc / AddCircle…` — use the kernel's API, check `kernel/Effigy/Sketch/Sketch.cs`);
- a rubber-band preview in `DrawEdit`;
- Esc to cancel the half-drawn shape;
- one undo step for the whole edit session (already how edits commit).

Port the behaviour straight from s&box `EffigyViewport.Sketching.cs`. Its click counts and order are the spec.

1. **Midpoint line:** first click = midpoint, second = end.
2. **Corner rectangle:** 2 clicks. Add Horizontal/Vertical constraints the way s&box does.
3. **Centre rectangle:** centre, then a corner.
4. **Centre circle:** centre, then the radius point.
5. **3-point circle.**
6. **Centre arc:** centre, start, end.
7. **3-point arc:** start, end, then a point on the arc.
8. **Point:** a lone point, as a construction reference.
9. **Inscribed and circumscribed polygon:** the side count goes in an inline field or a hotkey. Copy s&box.
10. **Slot, Ellipse, sketch Spline** (`SketchSpline`). This is not the 3D CAD Spline.
11. **Trim, Extend, Fillet** (corner radius), **Offset, Mirror** (across a picked line), **Cut** (drag a stroke). Use the kernel's `SketchEdit.cs` / `SketchCut.cs`; don't reimplement the geometry.
12. **Project** (one face edge in), **Project all, Construction toggle (Q), Inspector toggle.** Use `SketchReference.cs`.
13. **Profile Inspector:** shade the closed regions and flag loose ends while editing (`Profile.cs` / `ProfileFinder`).
14. **Plane offset when picking:** let the plane be offset as you pick (`SketchFeature.PlaneOffset`). For now it's only in Edit....

**After each tool:**
- a check in `tools/sketchedit_test.cs`;
- one live shot;
- flip its row in `CAD-SKETCH-PARITY.md` to **In**;
- send the shot to pooh.

## Step 4: constraints and dimensions

1. **Constrain menu** with the 17 constraints listed in `CAD-SKETCH-PARITY.md` (kernel: `Constraints.cs`, `ConstraintTools.cs`, `SketchSolver.cs`).
   - Wire each one like `EffigyViewport.Constraints.cs`: select the geometry, pick the constraint, the solver runs, and the view redraws.
2. **Dimension labels** in the viewport: draw them, double-click to type a value, and the solver runs.
3. **Show constraint glyphs** next to the curves they constrain.

## Panels that need pooh's sign-off BEFORE you build them

- the sketch toolbar / sketch pie (right-click);
- the Constrain menu;
- the dimension edit box.

Build a mock screenshot first, send it, and wait for pooh's OK. Until then the tools hang off hotkeys, and the edit hint text lists them (it already says "S select, L line").

## Rules that bite

- Bake, never generate at runtime. Viewport overlays like the planes and sketches above are drawn, not spawned nodes; keep it that way.
- No text clipping, ever. Labels get sized boxes and are clamped inside the overlay; see `GeppettoDatumPlanes.Draw` and `GeppettoPicker.DrawHint`.
- Effigy units are inches, Z up. Godot = 0.0254 × (y, z, x) via `EffigyMeshBridge.TransformPosition`.
- After changing a feature's params, call `studio.MarkDirty(feature)`, or the rebuild cache serves the old result.
- Keep the docs up to date as you go: this file, `CAD-SKETCH-PARITY.md`, and `GEPPETTO-POOHDOT-STATUS.md` item 3.
