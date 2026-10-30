extends SceneTree
## Rig IK chains test (see Tools/RigIkTest.cs). Works on a temp COPY of the ik-arm fixture.
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/rig_ik_test.gd
## Exits 1 if any check fails.

const IKARM := "res://addons/geppetto/tools/fixtures/ik-arm.effigy"

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/Tools/RigIkTest.cs").new()
	var report: Dictionary = test.Run(IKARM, root)
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[RigIkTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
