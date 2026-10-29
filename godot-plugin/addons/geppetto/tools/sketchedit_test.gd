extends SceneTree
## Sketch edit test (re-open a sketch, Line chains, Select, drag, delete, one undo step).
## Builds its own box document in user://, so no fixture is touched. See sketchedit_test.cs.
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/sketchedit_test.gd
## Exits 1 if any check fails.

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/tools/sketchedit_test.cs").new()
	var report: Dictionary = test.Run()
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[SketchEditTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
