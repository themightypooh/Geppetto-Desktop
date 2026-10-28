@tool
extends EditorPlugin

# Placeholder panels only. Every button is greyed out on purpose:
# this is for deciding where things go, not what they do.

const AMBER := Color(0.94, 0.65, 0.23)

const PANELS := [
	["Start", EditorDock.DOCK_SLOT_LEFT_UL, [
		"New / open / save character",
		"Reference pictures (front, side, top)"]],
	["Shape", EditorDock.DOCK_SLOT_RIGHT_UL, [
		"Block out from simple shapes",
		"Sketch a profile, pull it into a shape",
		"Sculpt: push, pull, smooth, inflate",
		"Edit mesh: extrude, bevel, loop cut, knife",
		"Mirror one side to the other"]],
	["Dress", EditorDock.DOCK_SLOT_RIGHT_UL, [
		"Add clothes",
		"Add hair or fur",
		"Pick and paint materials and colours"]],
	["Rig", EditorDock.DOCK_SLOT_RIGHT_BL, [
		"Add bones / rig template",
		"Bind skin, fix weights with a brush",
		"Face shapes and expressions"]],
	["Animate", EditorDock.DOCK_SLOT_BOTTOM, [
		"Pose by dragging handles (IK)",
		"Keys on a timeline, play back",
		"Clips (idle, walk) and blending"]],
	["Check and send", EditorDock.DOCK_SLOT_LEFT_BL, [
		"Check: symmetry, clipping, floating",
		"Send to Godot / reload after a change"]],
]

var docks: Array[EditorDock] = []
var toolbar_button: MenuButton
var workspace: Control


func _enter_tree() -> void:
	for p in PANELS:
		var dock := EditorDock.new()
		dock.title = "Geppetto: " + p[0]
		dock.layout_key = "geppetto_" + String(p[0]).to_snake_case()
		dock.title_color = AMBER
		dock.default_slot = p[1]
		dock.available_layouts = EditorDock.DOCK_LAYOUT_ALL
		dock.add_child(_make_panel(p[0], p[2]))
		add_dock(dock)
		docks.append(dock)

	toolbar_button = MenuButton.new()
	toolbar_button.text = "Geppetto"
	toolbar_button.tooltip_text = "Placeholder: a Geppetto menu in the 3D view's toolbar."
	toolbar_button.add_theme_color_override("font_color", AMBER)
	for p in PANELS:
		for job in p[2]:
			toolbar_button.get_popup().add_item(job)
	for i in toolbar_button.get_popup().item_count:
		toolbar_button.get_popup().set_item_disabled(i, true)
	add_control_to_container(CONTAINER_SPATIAL_EDITOR_MENU, toolbar_button)

	workspace = _make_workspace()
	EditorInterface.get_editor_main_screen().add_child(workspace)
	_make_visible(false)

	add_tool_menu_item("Geppetto (placeholder)", _on_tool_menu)


func _exit_tree() -> void:
	for dock in docks:
		remove_dock(dock)
		dock.queue_free()
	docks.clear()
	remove_control_from_container(CONTAINER_SPATIAL_EDITOR_MENU, toolbar_button)
	toolbar_button.queue_free()
	workspace.queue_free()
	remove_tool_menu_item("Geppetto (placeholder)")


func _has_main_screen() -> bool:
	return true


func _make_visible(visible: bool) -> void:
	if workspace:
		workspace.visible = visible


func _get_plugin_name() -> String:
	return "Geppetto"


func _get_plugin_icon() -> Texture2D:
	return EditorInterface.get_editor_theme().get_icon("Skeleton3D", "EditorIcons")


func _on_tool_menu() -> void:
	print("Geppetto: placeholder Tools menu item. Nothing happens yet.")


func _make_panel(group: String, jobs: Array) -> Control:
	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	var box := VBoxContainer.new()
	box.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(box)

	var head := Label.new()
	head.text = "PLACEHOLDER: " + group.to_upper()
	head.add_theme_color_override("font_color", AMBER)
	box.add_child(head)

	for job in jobs:
		var b := Button.new()
		b.text = job
		b.disabled = true
		b.alignment = HORIZONTAL_ALIGNMENT_LEFT
		b.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
		b.tooltip_text = job + "\n(Placeholder. Does nothing yet.)"
		box.add_child(b)

	var hint := Label.new()
	hint.text = "Drag this tab to any dock spot to try it there."
	hint.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	hint.modulate = Color(1, 1, 1, 0.6)
	box.add_child(hint)
	return scroll


func _make_workspace() -> Control:
	var panel := PanelContainer.new()
	panel.size_flags_vertical = Control.SIZE_EXPAND_FILL
	var label := Label.new()
	label.text = "GEPPETTO WORKSPACE (placeholder)\n\nThis is the option of a full-screen tab next to 2D / 3D / Script,\njust for building and animating a character."
	label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	label.add_theme_color_override("font_color", AMBER)
	label.add_theme_font_size_override("font_size", 22)
	panel.add_child(label)
	return panel
