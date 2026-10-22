#!/usr/bin/env python3
"""Render a saved Effigy model into ID-coloured frames for bake.py: top down, rolled about an axis.

The shape comes from Effigy (model/<name>/out/<name>.obj, exported by its build.txt run through
geppetto-live). This only turns the finished mesh for each frame and renders it with Effigy's own
renderer (`effigy.sh shade`). Every material slot is drawn in a flat ID colour, so bake.py can tell
the parts apart and repaint them in the chest's dithered ramps from the rendered light.

  python3 render_frames.py scroll roll=24            the whole scroll rolled about its long axis (Y)
  python3 render_frames.py scroll part=rod slots=1,5  the rod alone, unrolled (one frame)
  python3 render_frames.py pencil part=turn roll=16 turn=16   every roll at every heading on the
      mat (turned about the view axis after rolling), so it can lie any way and stay lit from the
      top left; frames are named HH_RR.png
  python3 render_frames.py paperball part=unfold poses=unfold_   one frame per exported pose, as posed
  python3 render_frames.py paperball part=turn src=poses/unfold_00.obj roll=16 turn=16 ball=1
      a ball rolling any way: rolled about an axis lying at each heading (turned back after
      rolling), so at roll 0 every heading is the same ball and changing course doesn't jump
"""
import math
from concurrent.futures import ThreadPoolExecutor
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
EFFIGY = os.path.expanduser("~/games/projects/geppetto/tools/effigy.sh")
# slot -> flat ID tint (bake.py ID_HUES): paper green, wood blue, ribbon red, aged paper cyan,
# ink yellow, brass magenta, wax orange
IDS = {0: (0, 1, 0), 1: (0, 0, 1), 2: (1, 0, 0), 3: (0, 1, 1), 4: (1, 1, 0), 5: (1, 0, 1), 6: (1, 0.5, 0)}
# extra slots share a hue with a base one (the book row: 7..14 more leathers, 15..18 more lettering pieces);
# bake.py tells those apart by where they sit
IDS.update({n: IDS[0] for n in range(7, 15)})
IDS.update({n: IDS[2] for n in range(15, 19)})


def read_obj(path):
    vs, faces, mat = [], [], None
    with open(path) as f:
        for line in f:
            if line.startswith("v "):
                vs.append(tuple(map(float, line.split()[1:4])))
            elif line.startswith("usemtl"):
                mat = line.split()[1]
            elif line.startswith("f "):
                faces.append((mat, line))
    return vs, faces


def write_obj(path, vs, faces, keep):
    with open(path, "w") as f:
        for v in vs:
            f.write("v %.5f %.5f %.5f\n" % v)
        last = None
        for mat, line in faces:
            if keep and int(mat.rsplit("_", 1)[1]) not in keep:
                continue
            if mat != last:
                f.write("usemtl %s\n" % mat)
                last = mat
            f.write(line)


def main():
    name = sys.argv[1]
    opts = dict(a.split("=", 1) for a in sys.argv[2:])
    frames = int(opts.get("roll", "1"))
    part = opts.get("part", "roll")
    keep = {int(s) for s in opts["slots"].split(",")} if "slots" in opts else None
    size = int(opts.get("size", "512"))
    src = os.path.join(HERE, name, opts.get("src", os.path.join("out", name + ".obj")))
    work = os.path.join(HERE, name, "frames", part)
    shutil.rmtree(work, ignore_errors=True)
    mats = os.path.join(work, "Assets", "materials")
    os.makedirs(mats)
    for slot, (r, g, b) in IDS.items():
        with open(os.path.join(mats, "material_%d.vmat" % slot), "w") as f:
            f.write('Layer0\n{\n\tshader "complex.shader"\n\tg_vColorTint "[%g %g %g 0]"\n'
                    '\tg_flRoughnessScaleFactor "0.7"\n\tg_flMetalness "0"\n}\n' % (r, g, b))
    vs, faces = read_obj(src) if os.path.exists(src) else ([], [])
    turns = int(opts.get("turn", "0"))
    if turns:                               # turn about the middle of the thing, not the model origin
        lo = [min(v[i] for v in vs) for i in range(3)]
        hi = [max(v[i] for v in vs) for i in range(3)]
        mid = [(a + b) / 2 for a, b in zip(lo, hi)]
        vs = [(x - mid[0], y - mid[1], z - mid[2]) for x, y, z in vs]
    jobs = []
    if "poses" in opts:                     # one frame per pose the build exported (poses/<prefix>_NN.obj), as posed
        pd = os.path.join(HERE, name, "poses")
        for i, f in enumerate(sorted(f for f in os.listdir(pd) if f.startswith(opts["poses"]) and f.endswith(".obj"))):
            pvs, pfaces = read_obj(os.path.join(pd, f))
            obj = os.path.join(work, "%s_%02d.obj" % (part, i))
            write_obj(obj, pvs, pfaces, keep)
            jobs.append((obj, "%02d" % i))
        frames = turns = 0
    for h in range(max(1, turns) if frames else 0):
        ph = 2 * math.pi * h / turns if turns else 0
        ch, sh = math.cos(ph), math.sin(ph)
        for i in range(frames):
            th = 2 * math.pi * i / frames
            c, s = math.cos(th), math.sin(th)
            # roll about Y: x and z turn, y (the long axis) stays; then turn it on the mat (about Z)
            rv = vs
            if "ball" in opts:              # turn the axis to the heading first, so the roll is about it
                rv = [(x * ch + y * sh, -x * sh + y * ch, z) for x, y, z in rv]
            rv = [(x * c + z * s, y, -x * s + z * c) for x, y, z in rv]
            rv = [(x * ch - y * sh, x * sh + y * ch, z) for x, y, z in rv]
            tag = "%02d_%02d" % (h, i) if turns else "%02d" % i
            obj = os.path.join(work, "%s_%s.obj" % (part, tag))
            write_obj(obj, rv, faces, keep)
            jobs.append((obj, tag))

    def shade(job):
        obj, tag = job
        subprocess.run(["sh", EFFIGY, "shade", obj, work, "size=%d" % size, "cam=0,0,60", "look=0,0,0",
                        "lens=" + opts.get("lens", "140"), "ground=0", "exposure=" + opts.get("exposure", "0.12"), "assets=" + os.path.join(work, "Assets")], check=True,
                       stdout=subprocess.DEVNULL)
        os.replace(os.path.join(work, "%s_%s_custom.png" % (part, tag)), os.path.join(work, "%s.png" % tag))
        os.remove(obj)
        return tag

    with ThreadPoolExecutor(int(opts.get("jobs", "8"))) as pool:
        for n, tag in enumerate(pool.map(shade, jobs), 1):
            print("frame %s  (%d/%d)" % (tag, n, len(jobs)), flush=True)

if __name__ == "__main__":
    main()
