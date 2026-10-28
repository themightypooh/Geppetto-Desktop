extends SceneTree
## Animate workspace test (see AnimateTest.cs): takes, keys, save/reload, clip switching, live GLB
## export with an unsaved key, OBJ/FBX, shape-key keying, events and joint limits, on a copy of the
## g1-spike fixture in user://animate_test/.
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/animate_test.gd
## Exits 1 if any check fails.

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/tools/AnimateTest.cs").new()
	var report: Dictionary = test.Run()
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[AnimateTest] PASS %d  FAIL %d" % [report.total - report.failed, report.failed])
	quit(1 if report.failed > 0 else 0)
