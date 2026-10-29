# Geppetto as a Godot plugin: handoff (2026-10-28)

> **2026-10-29: superseded for current state by `handoffs/GEPPETTO-POOHDOT-STATUS.md` (section "CURRENT STATE").** Everything is merged at 7a98c8b on godot-plugin-baseline and published.

Work is paused because pooh's usage is too high. This is everything a later session needs.

## Decisions
- **Option B is decided.** Geppetto becomes a plugin inside the Godot editor (Godot 4.7.2 .NET edition), reusing the engine-free C# kernel. This replaces the earlier "export first, plugin later" ordering.
- **The cabinet is parked.** It's the PySide6, AI-first shell (`cabinet/host.py` and `cabinet.qml`). It isn't the main UI in any plan. Nothing is deleted, and the native Silk viewer and ServeMode are parked the same way.
- **pooh designs every panel before it gets built.** No real Geppetto UI goes in without pooh's sign-off.
- **pooh wants the whole editor s&box-like, for every project.** That is owned by the thread "Make Godot feel like s&box", not this one.
- **Hotload** (the Synchronize Script/Scene Changes toggles) also belongs to the s&box thread.

## Docs published (claude.ai)
- Starting point: inventory, pushback, questions. https://claude.ai/artifact/A5c7KEzEp1k4UhufLrZ1G3 (sections 6–8 still describe the old native-app UI).
- Backend scope, tickets G0–G15, critical path G1→G2→G3→G4→G7→G8. https://claude.ai/artifact/Nd9YGiu9sazZ9MQY6QzxSR
- HTML layout mock. pooh said it didn't help, so it is superseded by the real Godot mock. https://claude.ai/artifact/MtMQze8SdbuqqG5Zvxxa13

## What changed on pooh's PC
All of it is inside `~/Desktop/geppetto-desktop/godot-ui-mock/`, a new folder. Nothing else in the repo was edited.

| Change | Status | How to undo |
|---|---|---|
| `project.godot`, plus `addons/geppetto_mock/` (plugin.cfg, plugin.gd): 6 placeholder "Geppetto:" docks (Start, Shape, Dress, Rig, Animate, Check and send) with greyed-out buttons, a Geppetto menu in the 3D toolbar, a Geppetto workspace tab, and a Project › Tools item | **Verified.** It opens in Godot 4.7.2 (/usr/bin/godot, Vulkan, RTX 5060 Ti) with no errors in the log | Delete the `godot-ui-mock` folder |
| `.godot/editor/editor_layout.cfg` [docks]: s&box arrangement. Scene alone on the left, Inspector alone on the right, FileSystem (thumbnails) on the bottom, then Output and all 6 Geppetto tabs | **Verified** by the editor log and report. pooh hasn't confirmed it by eye | Copy `editor_layout.cfg.orig` over it, with Godot closed |
| `test_room.tscn` (TestRoom, Sun, Floor, Character, Camera), set as `run/main_scene` | Added and opens. Not checked visually | Delete the file and the `run/main_scene` line |
| `addons/sbox_chrome`: a symlink to `~/.config/godot/sbox-editor/addons/sbox_chrome`, enabled in project.godot | Added during the restyle that was then stopped. Not checked | Remove the symlink and its entry in `[editor_plugins] enabled` |
| Dock renames and global EditorSettings shortcuts (G for snap, backtick for console) | **Unknown.** The session was stopped mid-task before it reported. Check Editor Settings › Shortcuts | Reset those shortcuts in Editor Settings |
| Debug › Synchronize Script/Scene Changes | **Not done.** `project_metadata.cfg` has no [debug_options] | n/a (the s&box thread owns this) |

`editor_layout.cfg` is tracked by git in the live tree, so it shows as modified.

## Verified facts (Godot 4.7 docs)
- Plugins add docks with `EditorDock` + `add_dock()`. `add_control_to_dock` and `add_control_to_bottom_panel` are deprecated. The slots are DOCK_SLOT_LEFT_UL/BL/UR/BR, RIGHT_UL/BL/UR/BR, BOTTOM, BOTTOM_L and BOTTOM_R. EditorDock has `title`, `title_color`, `layout_key`, `default_slot` and `available_layouts`.
- Other places a plugin can put UI: `add_control_to_container` (CONTAINER_SPATIAL_EDITOR_MENU / SIDE_LEFT / SIDE_RIGHT / BOTTOM, INSPECTOR_BOTTOM), `_has_main_screen` (a workspace tab), and `add_tool_menu_item`.
- Only FileSystem can move into the bottom panel; Scene can't. Editor › Editor Layouts › Save stores a layout that every project can use.

## Not verified
- How pooh actually wants Geppetto's jobs placed. They haven't dragged the tabs yet.
- Whether the sbox_chrome injection or any shortcut change broke anything.

## Next steps
1. pooh opens godot-ui-mock, drags the 6 Geppetto tabs where they feel right, and saves. Read the placement back from `.godot/editor/editor_layout.cfg`.
2. Design each panel's controls one at a time with pooh, in real Godot (they found HTML mocks unhelpful), and get sign-off on each.
3. In parallel, backend tickets G0–G2 from the backend doc. G0 needs pooh's OK to commit a baseline. Task 1.1 is local Copilot only.
4. Open questions for pooh: first test character, bone naming, where `.effigy` files live, and whether ps1_lit maps can be ignored.

---

## Pick-up session, 2026-10-28 evening

### godot-ui-mock: open questions closed
Checked by opening the mock in Godot 4.7.2 (`/usr/bin/godot`), once `--headless --editor`, once as a real editor window inside `gamescope --backend headless` (nothing on pooh's screen), on a throwaway copy.
- **Loads clean.** The editor log has no errors or warnings. Both plugins (`geppetto_mock`, `sbox_chrome`) start.
- **`addons/sbox_chrome` symlink: works.** Its dock renames are live: Scene → Hierarchy, FileSystem → Assets, Output → Console, and the Hotload button shows. Nothing broken. (sbox_chrome belongs to the s&box thread; left alone.)
- **Shortcuts: applied** in `~/.config/godot/editor_settings-4.7.tres`: backtick = toggle Output/Console, G = 3D snap. Backup from before: `editor_settings-4.7.tres.bak-before-G-backtick-20261028`. Opening the mock makes sbox_chrome re-write its Alt-orbit / no-Shift-pan keys into the same file; that is expected and it doesn't touch G or backtick. Ctrl+Alt+K (clear output) dropped out of the list, but that's Godot's default anyway, so nothing changed.
- **`test_room.tscn` opens:** TestRoom, WorldEnvironment, Sun, Floor, Character (Body, Skeleton3D, AnimationPlayer), Camera. A capsule stands on a floor under a sky. The Sun node shows Godot's yellow warning triangle (a node config warning, harmless; not looked into).
- **Dock placement in the saved layout:** all 6 Geppetto tabs sit in the bottom panel next to Assets, Console and Debugger, not in their `default_slot`s. pooh still has to drag them where they want them (Next steps 1 is unchanged).
- Screenshots were sent to pooh (3D view with the Start tab; the Rig tab). No text clipping seen.
- Nothing was fixed or reverted, since nothing was broken. The real mock's `editor_layout.cfg` was restored after the headless run (it had moved the window position only).

### Backend tickets: status per ticket
All of G0–G15 were already committed on `godot-plugin-baseline` by earlier sessions (this handoff's "Next steps 3" was stale). This session re-ran every check, nothing re-implemented. Poohdot = `~/src/poohdot/godot/bin/godot.linuxbsd.editor.x86_64.mono`, all runs headless.
- G0 (572ec62) baseline snapshot: done.
- G1 (cb0db20) kernel inside the Godot .NET plugin: verified, `godot-plugin` builds (`dotnet build -c Debug`, 0 warnings, 0 errors).
- G2 (28a76d1) engine-free `core/Geppetto.Core.csproj`: verified, builds alone, 0 errors.
- G3/G4/G5/G7/G8 (e9ef82d) mesh, skeleton, blend shapes, clips, `.effigy` importer: verified by `tools/harness.gd` on the two fixtures: 0 failed; g1-spike gives 4 bones and clips spin + wheel-rest (12 tracks each).
- G6 (0d81c9f, 9485c68) materials: verified through the harness (materials load) and GLB export (1 material).
- G9 (0d81c9f) live document service + undo adapter: verified through SaveRoundTrip (opens, edits, undoes through it).
- G10 (0a48103) pose/IK + auto-key: verified, `tools/pose_ik_test.gd` 32/32 pass.
- G11 (5e2ebbd) control registry + search: verified, `tools/control-registry-tests` 10 passed, 0 failed.
- G12 (12f08cd) save back to `.effigy` / `.riganim`: verified, `tools/SaveRoundTrip.cs` on 2 .effigy + 1 .riganim, all ok, 0 failed.
- G13 (fee0ff0) GLB export: verified, `tools/export_glb.gd` wrote a 1.1 MB .glb (1 mesh, 1 material, 1 skin, 2 animations).
- G14/G15 (d8d8e12) harness + packaging/install guide: verified (harness above; install guide `addons/geppetto/README.md`).
- CPU suite (`tests/Desktop.Tests.csproj`): 778 passed, 16 failed, 5 skipped (the known viewer failures, none new)

### What's left
- **Backend: nothing on G0–G15.** Known gaps carried forward: `EffigyUndoRedoAdapter` with Godot's UndoRedo can drift from Geppetto's EditHistory; `DocumentSaveService.Save()` deadlocks on Godot's main thread (callers use Task.Run). Stock Godot 4.7.2 from Arch (`/usr/bin/godot`) has no .NET, so the C# plugin is only tested in Poohdot.
- **UI: pooh's call.** Placement of the 6 docks, then each panel designed with pooh and signed off. Not built here.
- **Branch `geppetto-tools`** (worktree `.claude/worktrees/geppetto-tools`) is 26 commits ahead of baseline with the dial, tools and material manager, not merged. It contains real UI (dial, material dialog) that pooh asked for in other threads; whether it merges is pooh's call. Its open bug (Model view goes grey during a Model edit) is in `handoffs/GEPPETTO-POOHDOT-STATUS.md`.
- `handoffs/WHAT-IS-LEFT-ROADMAP.md` is stale: it still lists G10–G15 as TODO. They're done.
- Open questions for pooh unchanged: first test character, bone naming, where `.effigy` files live, whether ps1_lit maps can be ignored.

### Undo for this session
This session changed only this file (the section you're reading). Delete the section to undo. Test output stayed in the session scratchpad; the mock was screenshotted on a copy.
