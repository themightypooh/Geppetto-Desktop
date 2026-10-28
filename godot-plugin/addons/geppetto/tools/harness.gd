extends SceneTree
## G14 verification harness. Imports .effigy files through the Geppetto importer and writes a JSON report.
##
## Run headless (no window), from the project folder:
##   godot --headless --path . -s res://addons/geppetto/tools/harness.gd -- [--out report.json] file.effigy [more.effigy ...]
## With no files it checks res://spike/g1-spike.effigy. Exits 1 if any file fails to import.

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var out_path := ""
	var files: Array[String] = []
	var i := 0
	while i < args.size():
		if args[i] == "--out" and i + 1 < args.size():
			out_path = args[i + 1]
			i += 1
		else:
			files.append(args[i])
		i += 1
	if files.is_empty():
		files.append("res://spike/g1-spike.effigy")

	var importer = load("res://addons/geppetto/EffigySceneImporter.cs").new()
	var results := []
	var failed := 0
	for f in files:
		var r := _check(importer, f)
		if not r.ok:
			failed += 1
		results.append(r)

	var info := Engine.get_version_info()
	var report := {
		"engine": "%s %s" % [Engine.get_version_info().string, info.get("hash", "").left(9)],
		"poohdot": info.get("build", "") == "custom_build",
		"files": results,
		"failed": failed,
	}
	var text := JSON.stringify(report, "  ")
	print(text)
	if out_path != "":
		var fh := FileAccess.open(out_path, FileAccess.WRITE)
		if fh:
			fh.store_string(text)
		else:
			printerr("[Harness] Could not write ", out_path)
	quit(1 if failed > 0 else 0)

func _check(importer, path: String) -> Dictionary:
	var r := {"file": path, "ok": false}
	if not FileAccess.file_exists(path):
		r.error = "file not found"
		return r
	var t := Time.get_ticks_usec()
	var root = importer._import_scene(path, 0, {})
	r.import_ms = snappedf((Time.get_ticks_usec() - t) / 1000.0, 0.1)
	if root == null:
		r.error = "importer returned nothing (see log)"
		return r

	var surfaces := 0
	var verts := 0
	var tris := 0
	var blend_shapes := 0
	var materials := []
	var warnings := []
	for mi in root.find_children("*", "MeshInstance3D", true, false):
		var mesh: Mesh = mi.mesh
		if mesh == null:
			warnings.append("%s has no mesh" % mi.name)
			continue
		blend_shapes += mesh.get_blend_shape_count() if mesh is ArrayMesh else 0
		for s in mesh.get_surface_count():
			surfaces += 1
			var arrays := mesh.surface_get_arrays(s)
			var n: int = (arrays[Mesh.ARRAY_VERTEX] as PackedVector3Array).size()
			var idx = arrays[Mesh.ARRAY_INDEX]
			verts += n
			tris += (idx.size() if idx != null else n) / 3
			var mat := mesh.surface_get_material(s)
			if mat == null:
				warnings.append("%s surface %d has no material" % [mi.name, s])
			else:
				var m := {"surface": s, "name": mesh.surface_get_name(s)}
				if mat is BaseMaterial3D:
					m.albedo = mat.albedo_color.to_html()
					m.roughness = mat.roughness
					m.metallic = mat.metallic
					m.transparency = mat.transparency
					if mat.emission_enabled:
						m.emission = mat.emission.to_html(false)
						m.emission_energy = mat.emission_energy_multiplier
					if mat.clearcoat_enabled:
						m.clearcoat = mat.clearcoat
				materials.append(m)

	var bones := 0
	for sk in root.find_children("*", "Skeleton3D", true, false):
		bones += sk.get_bone_count()

	var clips := []
	for ap in root.find_children("*", "AnimationPlayer", true, false):
		for name in ap.get_animation_list():
			var a: Animation = ap.get_animation(name)
			clips.append({"name": name, "length_s": a.length, "step_s": a.step, "tracks": a.get_track_count(), "loop": a.loop_mode != Animation.LOOP_NONE})
	if clips.is_empty() and bones == 0:
		var base := path.get_file().get_basename()
		var dir := DirAccess.open(path.get_base_dir())
		if dir:
			for fn in dir.get_files():
				if fn.begins_with(base) and fn.ends_with(".riganim"):
					warnings.append("clip %s skipped: model has no rig" % fn)

	r.ok = true
	r.surfaces = surfaces
	r.vertices = verts
	r.triangles = tris
	r.blend_shapes = blend_shapes
	r.bones = bones
	r.clips = clips
	r.materials = materials
	r.warnings = warnings
	root.free()
	return r
