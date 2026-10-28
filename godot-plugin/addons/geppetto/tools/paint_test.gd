extends SceneTree
## Paint brush + Paint Weights test. Drives the tools on a COPY of g1-spike.effigy with synthetic strokes
## and checks the paint / weights land in the document, one undo puts them back, and they survive save +
## reload (see PaintTest.cs).
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/paint_test.gd
## Exits 1 if any check fails.

const SPIKE := "res://addons/geppetto/tools/fixtures/g1-spike.effigy"

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/tools/PaintTest.cs").new()
	var report: Dictionary = test.Run(SPIKE)
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[PaintTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
