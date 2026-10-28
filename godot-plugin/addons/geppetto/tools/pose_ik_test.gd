extends SceneTree
## G10 pose / IK / auto-key test. Imports two rigged .effigy fixtures through the Geppetto importer,
## writes clip poses and kernel IK solves onto the Skeleton3D and checks Godot's bone transforms
## numerically against the kernel's own answer (see EffigyPoseIkTest.cs).
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/pose_ik_test.gd [-- --out report.json]
## Exits 1 if any check fails.

const SPIKE := "res://addons/geppetto/tools/fixtures/g1-spike.effigy"
const ARM := "res://addons/geppetto/tools/fixtures/ik-arm.effigy"

func _initialize() -> void:
	# Wait one frame so the root window is in the tree and the imported skeletons are inside it,
	# as they would be in the editor.
	await process_frame
	var args := OS.get_cmdline_user_args()
	var out_path := ""
	if args.size() >= 2 and args[0] == "--out":
		out_path = args[1]

	var importer = load("res://addons/geppetto/EffigySceneImporter.cs").new()
	var spike = importer._import_scene(SPIKE, 0, {})
	var arm = importer._import_scene(ARM, 0, {})
	if spike == null or arm == null:
		printerr("[PoseIkTest] import failed (spike=%s arm=%s)" % [spike, arm])
		quit(1)
		return
	root.add_child(spike)
	root.add_child(arm)

	var test = load("res://addons/geppetto/tools/EffigyPoseIkTest.cs").new()
	var report: Dictionary = test.Run(spike, SPIKE, arm, ARM)
	var text := JSON.stringify(report, "  ")
	print(text)
	for c in report.checks:
		print("%s  %s  (%s)" % ["PASS" if c.ok else "FAIL", c.name, c.value])
	print("[PoseIkTest] %d/%d checks passed" % [report.total - report.failed, report.total])
	if out_path != "":
		var fh := FileAccess.open(out_path, FileAccess.WRITE)
		if fh:
			fh.store_string(text)
	spike.queue_free()
	arm.queue_free()
	quit(1 if report.failed > 0 else 0)
