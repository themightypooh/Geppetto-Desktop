extends SceneTree
## Rig workspace test (see Tools/RigTest.cs). Works on a temp COPY of the g1-spike fixture: add, rename,
## reparent, mirror, delete bones, pin a part, soft bones; undo/redo of each; ApplyTo's skeleton rebuild;
## save + reload; and the importer's bone count.
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/rig_test.gd
## Exits 1 if any check fails.

const SPIKE := "res://addons/geppetto/tools/fixtures/g1-spike.effigy"

func _initialize() -> void:
	await process_frame
	var test = load("res://addons/geppetto/Tools/RigTest.cs").new()
	var report: Dictionary = test.Run(SPIKE, root)
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[RigTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	quit(1 if report.failed > 0 else 0)
