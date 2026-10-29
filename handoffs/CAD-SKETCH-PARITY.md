# CAD sketch: s&box vs Poohdot (2026-10-28)

Step 1 of "CAD sketch tools parity" in `GEPPETTO-POOHDOT-STATUS.md`: the in/missing table. **What to do next, step by step: `AUDIT-AND-SKETCH-NEXT.md`.**

**Update (2026-10-28, branch `sketch-edit`, worktree `.claude/worktrees/sketch-edit`, from `geppetto-tools` a8efd6e, not committed):** step 1 below is done: edit an existing sketch, plus Select and open Line chains. Files: `Tools/CadSketch.Edit.cs` (new), `Tools/CadSketch.cs` (partial + edit dispatch), `Tools/GeppettoFeatureTree.cs` (double-click / Edit sketch), `GeppettoPlugin.SketchRun.cs` (new) + one line in `GeppettoPlugin.WindowRun.cs` (`GEPPETTO_WINDOW_WS=SketchEdit`). Tests: `tools/sketchedit_test.gd` 17/17, `cadpick_test.gd` still 58/58, window run 20/20 checks, 0 clipped text. Snapping and constraints are still missing. The hint text has the S/L keys because the sketch toolbar needs pooh's sign-off.

**Update (2026-10-28, branch `sketch-snap`, commits 409fcec..dc844e0):** every sketch tool and snap was driven with real clicks and keys in the hidden gamescope window (`GEPPETTO_WINDOW_WS=SketchTools`, fails=0; Blank and SketchEdit modes also fails=0). Fixed on the way: curve picking (Trim deleted the wrong curve, Extend found nothing), Select now picks what's under the mouse, a click on a selected line narrows to it, the Fillet radius click no longer snaps back to the corner, glyphs no longer overlap. All three sketch stages are now on the dial (CAD > Sketch > Draw / Shapes / Modify rings). Constraints and dimensions are in through hotkeys only (table below). `tools/sketchedit_test.gd` 46/46, the other 10 `tools/*_test.gd` pass.

- s&box source: `~/games/projects/geppetto/Editor/EffigyEditor/EffigyWindow.cs` (`BuildSketchStages`), `EffigyViewport.Sketching.cs` (`SketchToolKind`), `EffigyWindow.Radial.cs` (sketch pie).
- Poohdot source: branch `geppetto-tools`, `godot-plugin/addons/geppetto/Tools/CadSketch.cs`, `CadPick.cs`, `Cad.cs`.
- **The kernel is not the gap.** `kernel/Effigy/Sketch/` has the same 16 files as s&box (`Sketch`, `SketchEdit`, `SketchCut`, `SketchSolver`, `Constraints`, `ConstraintTools`, `SketchSnap`, `SketchReference`, `Profile`, ...). Everything missing below is editor wiring and UI, not geometry.

## What Poohdot's Sketch does today

One tool. You click a face (or empty space for the global plane facing you), click points, then click the first point or press Enter. That makes a **closed polyline** `SketchFeature`, one undo step. Backspace removes the last point. You can't reopen or edit a sketch after it's made.

## Sketch session

**Audit fixes (2026-10-28, `sketch-edit`, real clicks in a live editor):** Primitive was a 1u speck inside the model, now sized and placed beside it; the feature dialog preview didn't update (missing `MarkDirty`); Plane features drew nothing and couldn't be sketched on; the Plane dialog's "From" row showed Top for a face plane; finished sketches were never drawn in the viewport (now amber until something consumes them, `CadPlanes.cs`); refusals were console-only, now toasts (`GeppettoToolContext.Warn`); the Extrude/Revolve/Sweep/Loft dialog's Sketch row was a blank placeholder, now a dropdown of the real sketches, and Cancel puts back the picked sketch and regions (`ParamSnapshot`).

| s&box | Poohdot | Notes |
|---|---|---|
| Pick a plane: Top/Front/Right, a face, or a Plane feature | **Partial** (branch `sketch-edit`) | Face, a Plane feature (drawn lilac with its name, click inside its square; nearer than the face wins), or the global plane most square to the camera. No plane offset while picking (`SketchFeature.PlaneOffset`, still settable in Edit...). |
| Re-enter / edit an existing sketch | **In** (branch `sketch-edit`) | Double-click a sketch in Features, or **Edit sketch** in its menu (**Edit...** still opens the plane/offset box). Edited live; Esc or Enter finishes as one undo step. Script sketches (Curves text) are refused. |
| Finish sketch | **In** (branch `sketch-edit`) | In edit mode: Esc, or Enter with no line chain open. |
| Snapping (`SketchSnapper`: points, midpoints, grid, face edges) | **In** (branch `sketch-edit`) | Points, face reference edges, midpoints, grid, and alignment inference (Horizontal/Vertical). Snap rings and inference guides drawn. |
| Profile Inspector (shade closed regions, flag loose ends) | **In** (branch `sketch-edit`) | Shaded polygon fill for closed regions (`ProfileFinder.ShadedRegions`), red rings on unclosed loose endpoints. |
| Sketch pie menu (right-click) | **Missing** (new panel, needs pooh) | The dial covers it instead: CAD > Sketch opens Draw, Shapes and Modify rings (24 tiles incl. Construction, Project, Finish sketch). |

## Draw stage

| Tool | Poohdot | Notes |
|---|---|---|
| Select (pick, drag points, box-select) | **In** (branch `sketch-edit`) | S key. Click points/curves (Shift adds), drag a point or a curve's middle grip (`SketchHandles.Drag`), box left→right = inside, right→left = touching; a selected point drags the selection. Delete removes curves. |
| Line (chains until Esc) | **In** in edit mode (branch `sketch-edit`) | L key. Chains; clicking an existing point joins it, the first point closes; Enter/right-click ends the chain, Backspace takes back a line. Snap inference auto-adds H/V and PointOnLine constraints. |
| Midpoint line | **In** (branch `sketch-edit`) | M key. Click centre, then end; grows both ways symmetrically. |
| Corner rectangle | **In** (branch `sketch-edit`) | R key. 2 clicks opposite corners. Adds 4 lines with Horizontal/Vertical constraints. |
| Centre rectangle | **In** (branch `sketch-edit`) | Shift+R. Centre then corner. Adds 4 lines with Horizontal/Vertical constraints. |
| Centre circle | **In** (branch `sketch-edit`) | C key. Centre then rim point. |
| 3-point circle | **In** (branch `sketch-edit`) | Shift+C. 3 points on rim. Collinear refused. |
| Centre arc | **In** (branch `sketch-edit`) | A key. Centre, start, end direction. |
| 3-point arc | **In** (branch `sketch-edit`) | Shift+A. Start, end, through-point. |
| Point | **In** (branch `sketch-edit`) | P key. Lone point as construction reference. |

## Shapes stage

| Tool | Poohdot | Notes |
|---|---|---|
| Inscribed polygon | **In** (branch `sketch-edit`) | G key. Centre then vertex. PolygonSides (default 6). |
| Circumscribed polygon | **In** (branch `sketch-edit`) | Shift+G. Centre then edge midpoint. |
| Slot | **In** (branch `sketch-edit`) | W key. 3 clicks: axis endpoints then width. Creates 2 lines + 2 arc caps. |
| Ellipse | **In** (branch `sketch-edit`) | E key. 3 clicks: centre, major end, minor radius point. |
| Spline (in-sketch, `SketchSpline`) | **In** (branch `sketch-edit`) | N key. Click points, Enter finishes. |

## Modify stage

| Tool | Poohdot | Notes |
|---|---|---|
| Trim | **In** (branch `sketch-edit`) | T key. Click curve between crossings (`SketchEdit.Trim`). |
| Extend | **In** (branch `sketch-edit`) | X key. Click curve near end to extend to next crossing (`SketchEdit.Extend`). |
| Fillet (sketch corner) | **In** (branch `sketch-edit`) | F key. Click corner point then radius point (`SketchEdit.Fillet`). |
| Offset | **In** (branch `sketch-edit`) | O key. Click curve then offset distance/side (`SketchEdit.Offset`). |
| Mirror (across a picked line) | **In** (branch `sketch-edit`) | Shift+M. Select geometry, arm Mirror, click axis line (`SketchEdit.Mirror`). |
| Cut (drag a stroke) | **In** (branch `sketch-edit`) | Z key. Drag across curves to slice/trim (`SketchCut.Cut`). |

## Reference stage

| Tool | Poohdot | Notes |
|---|---|---|
| Project (copy one face edge in) | **In** (branch `sketch-edit`) | U key. Click highlighted green face edge (`SketchReference.UseEdge`). |
| Project all | **In** (branch `sketch-edit`) | Shift+U, or `ProjectAll()`: copies all face boundary edges into the sketch. Dial: CAD > Sketch > Modify > Project. |
| Construction toggle (Q) | **In** (branch `sketch-edit`) | Q key toggles construction mode on/off. Construction curves dimmed, skipped by profiles. |
| Inspector toggle | **In** (branch `sketch-edit`) | Integrated into viewport draw. |

## Constraints and dimensions

**In through hotkeys** (branch `sketch-snap`), no new panel. Select first (S, click; Shift adds), then:

| Key | Constraint (kernel kind) |
|---|---|
| H / V | Horizontal / Vertical |
| I | Coincident |
| Shift+E | Equal length |
| B / Shift+B | Parallel / Perpendicular |
| K / Shift+K | Tangent or Tangent arcs / Concentric |
| Y / Shift+Y | Midpoint / Symmetric |
| J | Fixed |
| Shift+P | Point on line |
| D | Dimension: Distance, Radius, Diameter or Angle, whichever fits the selection |

All 17 kernel kinds are reachable. D takes the current measurement; typing digits then Enter sets it, and a value the sketch can't hold (including one that would shrink a line to nothing) is refused and the old one kept. Each constraint draws a small glyph beside its curve (laid out so they don't overlap); clicking a glyph removes that constraint, Shift+Delete removes every constraint on the selection. Snap inference still adds H/V and Point on line while drawing.

**Still missing (new panels, need pooh's sign-off):** the Constrain menu, an editable dimension box/label in the viewport (the typed value shows in the glyph and the hint instead), a sketch toolbar.

## Sketch-consuming features

| Feature | Poohdot | Notes |
|---|---|---|
| Extrude from a sketch region | **In** | `CadPick` picks a region or a face. Covered by `tools/cadpick_test.cs`. |
| Revolve | **In** | Picks the selected sketch, or asks. |
| Sweep (profile + path) | **In** | Needs a sketch plus a Spline or a second sketch. |
| Loft (2+ sketches) | **In** | |

## Suggested order to close the gaps

1. **Edit an existing sketch** plus **Select**. Everything else needs an open sketch to add to.
2. **Open lines, rectangle, circle, arc, point**, with **snapping**. That's enough to draw real profiles.
3. **Constraints and dimensions** (the Constrain menu and the dimension labels).
4. **Trim, Extend, Fillet, Offset, Mirror, Cut**.
5. **Polygon, Slot, Ellipse, sketch Spline**.
6. **Project, Project all, Construction, Inspector**.

Each needs a window-run step and a screenshot to count as done (per the status file). The sketch toolbar and the Constrain menu are new panels, so **pooh signs off on their layout first**.
