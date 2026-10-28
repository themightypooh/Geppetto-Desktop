extends SceneTree
## CAD parametric tools test (see CadTest.cs). Works on a copy of g1-spike in user://cad_test.
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/cad_test.gd
## Exits 1 if any check fails.

const SPIKE := "res://addons/geppetto/tools/fixtures/g1-spike.effigy"

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/tools/CadTest.cs").new()
	var report: Dictionary = test.Run(SPIKE)
	for c in report.checks:
		print("%s  %s  %s" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[CadTest] PASS %d  FAIL %d  (of %d)" % [report.total - report.failed, report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
