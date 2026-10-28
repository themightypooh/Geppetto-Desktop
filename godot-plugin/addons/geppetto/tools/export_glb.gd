extends SceneTree
## G13 headless GLB export. Imports a .effigy (with its companion .riganim clips) and writes a .glb.
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/export_glb.gd -- in.effigy out.glb
## Paths may be res:// or absolute. Exits 1 on failure.

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 2:
		printerr("usage: -s res://addons/geppetto/tools/export_glb.gd -- in.effigy out.glb")
		quit(2)
		return
	var exporter = load("res://addons/geppetto/EffigyGlbExporter.cs").new()
	var err: int = exporter.ExportEffigyFile(args[0], args[1])
	if err != OK:
		printerr("[export_glb] failed: ", error_string(err))
		quit(1)
		return
	print("[export_glb] ok: ", args[1])
	quit(0)
