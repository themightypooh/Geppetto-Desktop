---
name: geppetto-hanging-ui
description: Build a physically credible hanging desktop menu or sign from a live Geppetto model, bake its hardware into 2D UI layers, and keep swinging controls clickable.
---

# Hanging UI from Geppetto

Read the project's `AGENTS.md` and `~/.claude/skills/geppetto/SKILL.md` first. Model every 3D part through `geppetto-live` in your own pane at a watchable pace. Keep the shared chest open and edit the same run with `geppetto-live more`.

## Physical construction

- Use real hanging hardware as reference. Show the load path: fixed upper plate and fastener → closed eye → alternating, centered chain links → lower eye → plate and through fastener → sign.
- Adjacent links and eyes must be perpendicular and actually threaded. A coplanar overlap only looks connected from one view. Keep link centers on one axis; inspect front, side, quarter, and a close view for intersections, gaps, and anything that could slip free.
- Check the baked result at its actual UI pixel size. Details that disappear there are not solving the UI problem.

## Bake and motion

- Bake fixed upper hardware, moving chain spans, lower mounts, and the panel separately from the saved Effigy model. Keep the panel content driven by QML layout so future actions can increase its width or height without stretching links or fasteners. Use a clean 2D panel edge if stretching the render creates halos.
- One shared pendulum state must position both chain ends and the panel. In screen coordinates a positive lateral angle moves a chain bottom right when the QML rotation is **negative**. Check that sign displacement and rotated chain endpoints agree at both extreme angles.
- Use elapsed time with small integration steps, damping, and a settled stop. A forward depth swing can supply the visible drop and perspective; keep the sign and its controls in the same transformed item.
- Give the animated parent enough margin to contain the moving panel and its hit targets. Keep hover, tooltips, SAVE, SAVE AS, keyboard entry, and drag working during motion.

## Verification

Use offscreen Qt; do not restart or focus the shared cabinet. Capture actual-size open, extreme swing, settled, and longer-menu screenshots at minimum, default, and maximum display scales and font widths. Check every text edge, frame edge, chain joint, hover, and click. Compilation or QML load alone does not verify the moving menu.

The working example is `cabinet/HangingFileMenu.qml`, its Effigy run and bake under `cabinet/model/hanging-menu/`, and `tests/test_hanging_file_menu.py`.
