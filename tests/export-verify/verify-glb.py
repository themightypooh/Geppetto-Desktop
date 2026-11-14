#!/usr/bin/env python3
"""Validate an exported .glb against an expected manifest (stdlib only).

Usage:
    python3 verify-glb.py <file.glb> <manifest.json>

Exit 0 iff every check passes. One PASS/FAIL line per check, then a summary.
The manifest is written by the fixture author; see expected-manifest.example.json.
Units in the manifest are metres, glTF space (Y-up, +Z toward the viewer):
a source bound of N inches must appear as N * 0.0254 m.

This validates the EXPORTED file only. It does not run Godot/Poohdot and does
not re-import: Desktop's GlbImport is static-only and refuses skins, so
skin/animation survival is checked here (export side) and in import-check.gd
(engine side), never by re-importing through Desktop.
"""
import json
import math
import struct
import sys

CHECKS = []


def check(name, ok, detail=""):
    CHECKS.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ((" -- " + detail) if detail else ""))


def finite3(v):
    return (
        isinstance(v, list)
        and len(v) == 3
        and all(isinstance(x, (int, float)) and math.isfinite(x) for x in v)
    )


def close(a, b, tol):
    return abs(a - b) <= tol


def aabb_ok(actual_min, actual_max, exp_min, exp_max, tol):
    if not (finite3(actual_min) and finite3(actual_max)):
        return False, "accessor min/max missing or non-finite"
    for i in range(3):
        if not (close(actual_min[i], exp_min[i], tol) and close(actual_max[i], exp_max[i], tol)):
            return False, "got min %s max %s, expected min %s max %s (tol %g)" % (
                actual_min, actual_max, exp_min, exp_max, tol)
    return True, "min %s max %s" % (actual_min, actual_max)


def main(argv):
    if len(argv) != 3:
        print("usage: verify-glb.py <file.glb> <manifest.json>")
        return 2
    glb_path, manifest_path = argv[1], argv[2]
    with open(manifest_path) as f:
        manifest = json.load(f)
    with open(glb_path, "rb") as f:
        data = f.read()
    tol = float(manifest.get("toleranceMetres", 0.001))

    # ---- container ----
    if len(data) < 20:
        check("container: at least 20 bytes", False, "file is %d bytes" % len(data))
        print("SUMMARY: 0/%d passed" % len(CHECKS))
        return 1
    magic, version, length = struct.unpack_from("<III", data, 0)
    check("container: magic 'glTF'", magic == 0x46546C67, "magic=0x%08x" % magic)
    check("container: version 2", version == 2, "version=%d" % version)
    check("container: length matches file", length == len(data),
          "header=%d file=%d" % (length, len(data)))
    chunk_len, chunk_type = struct.unpack_from("<II", data, 12)
    check("container: first chunk is JSON", chunk_type == 0x4E4F534A,
          "type=0x%08x" % chunk_type)
    if chunk_type != 0x4E4F534A:
        print("SUMMARY: %d/%d passed" % (sum(1 for _, ok, _ in CHECKS if ok), len(CHECKS)))
        return 1
    try:
        gltf = json.loads(data[20:20 + chunk_len].decode("utf-8"))
    except Exception as e:  # noqa: BLE001 - report, don't crash the harness
        check("container: JSON chunk parses", False, str(e))
        print("SUMMARY: 0/%d passed" % len(CHECKS))
        return 1
    check("container: JSON chunk parses", True, "asset=%s" % gltf.get("asset", {}))
    check("container: asset version 2.0",
          str(gltf.get("asset", {}).get("version")) == "2.0",
          "version=%s" % gltf.get("asset", {}).get("version"))

    accessors = gltf.get("accessors", [])
    meshes = gltf.get("meshes", [])
    nodes = gltf.get("nodes", [])
    node_names = [n.get("name", "") for n in nodes]

    # ---- node names ----
    for name in manifest.get("nodes", []):
        check("node present: %s" % name, name in node_names,
              "nodes=%s" % node_names if name not in node_names else "")
    dups = [n for n in node_names if node_names.count(n) > 1 and n]
    check("node names: no duplicates", not dups, "duplicates=%s" % dups)

    # ---- accessor sanity (index ranges + finite bounds) ----
    bad = []
    for i, acc in enumerate(accessors):
        mn, mx = acc.get("min"), acc.get("max")
        if mn is not None and not all(math.isfinite(x) for x in mn):
            bad.append("accessor %d min non-finite" % i)
        if mx is not None and not all(math.isfinite(x) for x in mx):
            bad.append("accessor %d max non-finite" % i)
    check("accessors: min/max finite", not bad, "; ".join(bad))
    pos_counts = {}
    bad_idx = []
    for mi, mesh in enumerate(meshes):
        for prim in mesh.get("primitives", []):
            pos_idx = prim.get("attributes", {}).get("POSITION")
            if pos_idx is not None and 0 <= pos_idx < len(accessors):
                pos_counts[mi] = accessors[pos_idx].get("count", 0)
            idx = prim.get("indices")
            if idx is not None and 0 <= idx < len(accessors):
                max_index = accessors[idx].get("max", [0])
                if max_index and max_index[0] >= pos_counts.get(mi, 0):
                    bad_idx.append("mesh %d index %d out of range (positions %d)"
                                   % (mi, max_index[0], pos_counts.get(mi, 0)))
    check("accessors: indices in range", not bad_idx, "; ".join(bad_idx))

    # ---- mesh bounds (units + axes) ----
    for exp in manifest.get("meshes", []):
        name = exp["name"]
        mesh = next((m for m in meshes if m.get("name") == name), None)
        check("mesh present: %s" % name, mesh is not None)
        if mesh is None:
            continue
        prim = mesh["primitives"][0] if mesh.get("primitives") else {}
        pos_idx = prim.get("attributes", {}).get("POSITION")
        acc = accessors[pos_idx] if pos_idx is not None and pos_idx < len(accessors) else {}
        ok, detail = aabb_ok(acc.get("min"), acc.get("max"),
                              exp["aabbMinMetres"], exp["aabbMaxMetres"], tol)
        check("mesh bounds (m, Y-up): %s" % name, ok, detail)

    # ---- skins ----
    exp_skins = manifest.get("skins", {})
    skins = gltf.get("skins", [])
    if "count" in exp_skins:
        check("skins: count == %d" % exp_skins["count"],
              len(skins) == exp_skins["count"], "found %d" % len(skins))
    if skins and "minJoints" in exp_skins:
        joints = skins[0].get("joints", [])
        check("skins: joints >= %d" % exp_skins["minJoints"],
              len(joints) >= exp_skins["minJoints"], "found %d" % len(joints))
        bad_joints = [j for j in joints if j < 0 or j >= len(nodes)]
        check("skins: joint indices reference nodes", not bad_joints,
              "bad=%s" % bad_joints)

    # ---- animations ----
    anims = {a.get("name", ""): a for a in gltf.get("animations", [])}
    for exp in manifest.get("animations", []):
        name = exp["name"]
        anim = anims.get(name)
        check("animation present: %s" % name, anim is not None)
        if anim is None:
            continue
        inputs = [s.get("input") for s in anim.get("samplers", [])]
        times = []
        for inp in inputs:
            if inp is not None and inp < len(accessors):
                acc = accessors[inp]
                if acc.get("min") and acc.get("max"):
                    times.append((acc["min"][0], acc["max"][0]))
        if times:
            t0 = min(t[0] for t in times)
            t1 = max(t[1] for t in times)
            check("animation time range: %s" % name,
                  close(t0, exp.get("startSec", 0), 0.05)
                  and close(t1, exp.get("durationSec", t1), 0.05),
                  "got %.3f..%.3fs expected 0..%.3fs" % (t0, t1, exp.get("durationSec", t1)))
        check("animation has channels: %s" % name,
              len(anim.get("channels", [])) > 0,
              "%d channels" % len(anim.get("channels", [])))

    # ---- morph targets ----
    for exp in manifest.get("morphs", []):
        mesh = next((m for m in meshes if m.get("name") == exp["mesh"]), None)
        check("morph mesh present: %s" % exp["mesh"], mesh is not None)
        if mesh is None:
            continue
        prim = mesh["primitives"][0] if mesh.get("primitives") else {}
        targets = prim.get("targets", [])
        check("morph target count: %s == %d" % (exp["mesh"], exp["targets"]),
              len(targets) == exp["targets"], "found %d" % len(targets))
        names = mesh.get("extras", {}).get("targetNames", [])
        for want in exp.get("targetNames", []):
            check("morph target name: %s" % want, want in names, "names=%s" % names)
        weights = mesh.get("weights", [])
        check("morph weights start at 0: %s" % exp["mesh"],
              all(w == 0 for w in weights), "weights=%s" % weights)

    # ---- materials / textures ----
    materials = {m.get("name", ""): m for m in gltf.get("materials", [])}
    images = gltf.get("images", [])
    for exp in manifest.get("materials", []):
        name = exp["name"]
        mat = materials.get(name)
        check("material present: %s" % name, mat is not None)
        if mat is None:
            continue
        pbr = mat.get("pbrMetallicRoughness", {})
        for slot in ("baseColor", "normal", "emissive", "occlusion"):
            want = exp.get("textures", {}).get(slot, False)
            if slot == "baseColor":
                have = "baseColorTexture" in pbr
            elif slot == "normal":
                have = "normalTexture" in mat
            elif slot == "emissive":
                have = "emissiveTexture" in mat
            else:
                have = "occlusionTexture" in mat
            check("material %s: %s texture %s" % (name, slot, "present" if want else "absent"),
                  have == want, "have=%s want=%s" % (have, want))
        if "alphaMode" in exp:
            check("material %s: alphaMode == %s" % (name, exp["alphaMode"]),
                  mat.get("alphaMode") == exp["alphaMode"],
                  "got %s" % mat.get("alphaMode"))
        if "emissiveFactor" in exp:
            got = mat.get("emissiveFactor", [0, 0, 0])
            want = exp["emissiveFactor"]
            ok = (isinstance(got, list) and len(got) == 3
                  and all(close(got[i], want[i], 0.02) for i in range(3)))
            check("material %s: emissiveFactor == %s" % (name, want), ok,
                  "got %s" % got)
    if "imageCount" in manifest:
        check("images: count == %d" % manifest["imageCount"],
              len(images) == manifest["imageCount"], "found %d" % len(images))
    for img in images:
        check("image %s is PNG" % img.get("name", "?"),
              img.get("mimeType") == "image/png", "mime=%s" % img.get("mimeType"))

    passed = sum(1 for _, ok, _ in CHECKS if ok)
    print("SUMMARY: %d/%d passed" % (passed, len(CHECKS)))
    return 0 if passed == len(CHECKS) else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
