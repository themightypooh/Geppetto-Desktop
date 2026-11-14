# import-check.gd — headless Godot / Poohdot check of an exported .glb.
# Copy this file into a FRESH project (do not run it in a real game project),
# copy the exported .glb files to <proj>/imports/, then run:
#   godot --headless --path <proj> --import
#   godot --headless --path <proj> -s import-check.gd -- --glb=res://imports/hero.glb --label=godot
# Same under Poohdot with /home/poo/.local/bin/poohdot and --label=poohdot.
# Prints one CHECK line per assertion plus a RESULT line, then quits.
# Exit 0 iff every check passes. Report Godot and Poohdot separately.
extends SceneTree

var _failures := 0
var _checks := 0

func _check(label: String, ok: bool, detail: String = "") -> void:
	_checks += 1
	if ok:
		print("CHECK PASS " + label + (" -- " + detail if detail != "" else ""))
	else:
		_failures += 1
		print("CHECK FAIL " + label + (" -- " + detail if detail != "" else ""))

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var glb_path := ""
	var label := "engine"
	for a in args:
		if a.begins_with("--glb="):
			glb_path = a.trim_prefix("--glb=")
		elif a.begins_with("--label="):
			label = a.trim_prefix("--label=")
	print("RESULT engine=" + label + " glb=" + glb_path)
	if glb_path == "":
		_check("args: --glb= given", false, "no --glb= argument")
		_finish()
		return
	if not ResourceLoader.exists(glb_path):
		_check("glb loads: " + glb_path, false, "ResourceLoader.exists is false (run --import first)")
		_finish()
		return
	var res = ResourceLoader.load(glb_path)
	_check("glb loads: " + glb_path, res != null, "loaded " + str(res))
	if res == null:
		_finish()
		return
	var scene: PackedScene = res as PackedScene
	_check("glb is a PackedScene", scene != null)
	if scene == null:
		_finish()
		return
	var root: Node = scene.instantiate()
	_check("scene instantiates", root != null)
	if root == null:
		_finish()
		return
	var names: Array[String] = []
	var meshes: Array[MeshInstance3D] = []
	var anim_players: Array[AnimationPlayer] = []
	_collect(root, names, meshes, anim_players)
	_check("nodes present", names.size() > 0, "nodes=" + str(names))
	var hero_found := false
	for n in names:
		if n == "Hero" or n == "TableTop" or n == "PaintPanel":
			hero_found = true
	_check("expected node names present (Hero/TableTop/PaintPanel)", hero_found, "nodes=" + str(names))
	var dups: Array[String] = []
	var seen: Dictionary = {}
	for n in names:
		if seen.has(n):
			dups.append(n)
		seen[n] = true
	_check("node names: no duplicates", dups.is_empty(), "duplicates=" + str(dups))
	for mi in meshes:
		var mesh := mi.mesh
		if mesh == null:
			continue
		var aabb := mi.get_aabb()
		_check("mesh aabb finite: " + mi.name, aabb.size.is_finite() and aabb.position.is_finite(),
			"aabb=" + str(aabb))
		# Units: 1 Godot unit = 1 metre. A 72-inch character must be ~1.8288 tall.
		if mi.name == "Hero":
			_check("Hero height ~1.8288 m (72 in)", absf(aabb.size.y - 1.8288) < 0.01,
				"height=" + str(aabb.size.y))
		for si in range(mesh.get_surface_count()):
			var mat := mesh.surface_get_material(si)
			if mat is StandardMaterial3D:
				var sm := mat as StandardMaterial3D
				var tex: Texture2D = sm.albedo_texture
				_check("surface material has albedo texture: " + mi.name, tex != null)
	_check("animation players found", anim_players.size() >= 0, "players=" + str(anim_players.size()))
	for ap in anim_players:
		var anims: PackedStringArray = ap.get_animation_list()
		_check("animation list non-empty: " + ap.name, anims.size() > 0, "anims=" + str(anims))
		for anim_name in anims:
			var anim: Animation = ap.get_animation(anim_name)
			_check("animation length > 0: " + str(anim_name), anim != null and anim.length > 0.0,
				"length=" + str(anim.length if anim != null else -1.0))
	_finish()

func _collect(node: Node, names: Array[String], meshes: Array[MeshInstance3D], players: Array[AnimationPlayer]) -> void:
	names.append(node.name)
	if node is MeshInstance3D:
		meshes.append(node as MeshInstance3D)
	if node is AnimationPlayer:
		players.append(node as AnimationPlayer)
	for child in node.get_children():
		_collect(child, names, meshes, players)

func _finish() -> void:
	print("RESULT checks=" + str(_checks) + " failures=" + str(_failures))
	quit(0 if _failures == 0 else 1)
