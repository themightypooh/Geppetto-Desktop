extends SceneTree
## Headless smoke test for GeppettoDocument (edit, undo, redo, save). Exits 1 on any failure.
##   godot --headless --path . -s res://addons/geppetto/tools/document_smoke_test.gd

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/tools/DocumentSmokeTest.cs").new()
	var failed: int = test.Run("res://addons/geppetto/tools/fixtures/g1-spike.effigy")
	quit(1 if failed > 0 else 0)
