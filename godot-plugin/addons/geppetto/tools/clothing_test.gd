extends SceneTree
## Clothing workspace test (see ClothingTest.cs). Works on a copy of g1-spike in user://, never on the fixture.
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/clothing_test.gd
## Exits 1 if any check fails.

const SPIKE := "res://addons/geppetto/tools/fixtures/g1-spike.effigy"

func _initialize() -> void:
	await process_frame
	var work := ProjectSettings.globalize_path("user://clothing_test")
	var test = load("res://addons/geppetto/tools/ClothingTest.cs").new()
	var report: Dictionary = test.Run(SPIKE, work)
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[ClothingTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
