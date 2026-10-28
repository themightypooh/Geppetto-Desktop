extends SceneTree
## Painted textures + painted weights show on the imported / refreshed Body (see paintshow_test.cs).
## Works on a COPY of g1-spike.effigy. Run headless, from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/paintshow_test.gd
## Exits 1 if any check fails.

const SPIKE := "res://addons/geppetto/tools/fixtures/g1-spike.effigy"

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/tools/paintshow_test.cs").new()
	var report: Dictionary = test.Run(SPIKE)
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[PaintShowTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
