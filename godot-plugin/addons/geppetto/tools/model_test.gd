extends SceneTree
## Model workspace test: every mesh-edit op on a copy of g1-spike, undo, live preview, save and reload
## (see Tools/ModelTest.cs).
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/model_test.gd
## Exits 1 if any check fails.

const SPIKE := "res://addons/geppetto/tools/fixtures/g1-spike.effigy"
const WORK := "user://model_test"

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/Tools/ModelTest.cs").new()
	var report: Dictionary = test.Run(SPIKE, WORK)
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[ModelTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
