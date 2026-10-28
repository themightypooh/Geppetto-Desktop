# Geppetto as a Godot plugin: handoff (2026-10-28)

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
