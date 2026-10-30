extends SceneTree
## Rig NLA strips test (see Tools/RigNlaTest.cs). Works on a temp COPY of the g1-spike fixture
## (the g1-spike_spin.riganim take is copied alongside).
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/rig_nla_test.gd
## Exits 1 if any check fails.

const SPIKE := "res://addons/geppetto/tools/fixtures/g1-spike.effigy"

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/Tools/RigNlaTest.cs").new()
	var report: Dictionary = test.Run(SPIKE, root)
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[RigNlaTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
