# Godot UI mock

Placeholder Geppetto panels inside the Godot 4.7 (.NET) editor, for deciding
where things go before anything real is built. Every button is greyed out.

Open `project.godot` in Godot 4.7. You get:

- Six "Geppetto:" docks (Start, Shape, Dress, Rig, Animate, Check and send)
- A Geppetto menu in the 3D view's toolbar
- A Geppetto workspace tab next to 2D / 3D / Script
- Project › Tools › Geppetto (placeholder)
- `test_room.tscn` as the main scene (sun, floor, capsule character, camera)

The dock arrangement starts from `.godot/editor/editor_layout.cfg`, an s&box-style
layout: Scene on the left, Inspector on the right, FileSystem plus all six
Geppetto tabs along the bottom. Drag the tabs where they feel right, close
Godot, and commit the changed `editor_layout.cfg` so the placement can be read back.
