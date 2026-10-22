#!/usr/bin/env python3
"""Cut the tool icon set (out/toolicons.obj, built from build.txt through geppetto-live) back into its
cells and render each icon with Effigy's renderer (`effigy.sh shade`) from a three-quarter view, every
material slot in a flat ID colour (frames/<key>.png). bake_icons.py then repaints those in the chest's
dithered pixel art with an outline.

  python3 render_icons.py [size=256] [only=blob,hole]
"""
import json
import os
import subprocess
import sys


HERE = os.path.dirname(os.path.abspath(__file__))
EFFIGY = os.path.expanduser("~/games/projects/geppetto/tools/effigy.sh")
# slot -> flat ID tint, read back by bake_icons.py (bake.py ID_HUES, plus 270 for the eighth slot):
# steel green, paper cyan, red red, flesh orange, ink yellow, brass magenta, cloth blue, glow violet, brown sea-green (150)
IDS = {0: (0, 1, 0), 1: (0, 1, 1), 2: (1, 0, 0), 3: (1, 0.5, 0), 4: (1, 1, 0), 5: (1, 0, 1), 6: (0, 0, 1), 7: (0.5, 0, 1), 8: (0, 1, 0.5)}


def main():
    opts = dict(a.split("=", 1) for a in sys.argv[1:])
    size = int(opts.get("size", "256"))
    only = set(opts["only"].split(",")) if "only" in opts else None
    cells = json.load(open(os.path.join(HERE, "cells.json")))
    half = cells["cell"] / 2
    piv = None
    for line in open(os.path.join(HERE, "out", "layout.txt")):
        if not line.startswith("#"):
            piv = tuple(map(float, line.split("\t")[2:5]))
    vs, faces, mat = [], [], None
    for line in open(os.path.join(HERE, "out", "toolicons.obj")):
        if line.startswith("v "):
            x, y, z = map(float, line.split()[1:4])
            vs.append((x + piv[0], y + piv[1], z + piv[2]))      # back to build coordinates
        elif line.startswith("usemtl"):
            mat = line.split()[1]
        elif line.startswith("f "):
            faces.append((mat, [int(t.split("/")[0]) - 1 for t in line.split()[1:]]))

    work = os.path.join(HERE, "frames")
    mats = os.path.join(work, "Assets", "materials")
    os.makedirs(mats, exist_ok=True)
    for slot, (r, g, b) in IDS.items():
        with open(os.path.join(mats, "material_%d.vmat" % slot), "w") as f:
            f.write('Layer0\n{\n\tshader "complex.shader"\n\tg_vColorTint "[%g %g %g 0]"\n'
                    '\tg_flRoughnessScaleFactor "0.7"\n\tg_flMetalness "0"\n}\n' % (r, g, b))

    for key, c in cells["icons"].items():
        if only and key not in only:
            continue
        cx, cy = c["cx"], c["cy"]
        keep = [(m, fv) for m, fv in faces
                if abs(sum(vs[i][0] for i in fv) / len(fv) - cx) < half and abs(sum(vs[i][1] for i in fv) / len(fv) - cy) < half]
        used = sorted({i for _, fv in keep for i in fv})
        remap = {i: n + 1 for n, i in enumerate(used)}
        name = key.replace(":", "_")
        obj = os.path.join(work, name + ".obj")
        with open(obj, "w") as f:
            for i in used:
                x, y, z = vs[i]
                f.write("v %.5f %.5f %.5f\n" % (x - cx, y - cy, z))
            last = None
            for m, fv in keep:
                if m != last:
                    f.write("usemtl %s\n" % m)
                    last = m
                f.write("f " + " ".join(str(remap[i]) for i in fv) + "\n")
        subprocess.run(["sh", EFFIGY, "shade", obj, work, "size=%d" % size, "cam=" + opts.get("cam", "6,-8,6.5"),
                        "look=0,0,0.5", "lens=" + opts.get("lens", "120"), "ground=0", "exposure=" + opts.get("exposure", "0.2"),
                        "assets=" + os.path.join(work, "Assets")], check=True, stdout=subprocess.DEVNULL)
        os.replace(os.path.join(work, name + "_custom.png"), os.path.join(work, name + ".png"))
        os.remove(obj)
        print(key, flush=True)


if __name__ == "__main__":
    main()
