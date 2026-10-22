#!/usr/bin/env python3
"""Write build.txt for the tool wheel's icons: one little Effigy Blob per modelling tool, laid out
on a grid (cell CELL apart, row by category), so the whole set builds as one live run in Geppetto.
render_icons.py then cuts the exported mesh back into cells and renders each one for bake.py.

Each icon is a small object that reads at ~40 px: Z up, about 2 units across, centred on its cell.
Slots: 0 steel (the subject), 1 paper, 2 red (the action), 3 flesh, 4 ink, 5 brass, 6 cloth, 7 glow, 8 brown.
"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CELL = 5.0


def f(v):
    return ",".join("%g" % round(c, 4) for c in v)


class Icon:
    def __init__(self, key, label, blend=0.03):
        self.key, self.label, self.blend, self.shapes = key, label, blend, []
        self.o = (0, 0, 0)

    def at(self, p):
        return (p[0] + self.o[0], p[1] + self.o[1], p[2] + self.o[2])

    def add(self, text, m, cut=False, paint=False, rot=None, r=None):
        s = ("-" if cut else "") + ("paint " if paint else "") + text
        if r is not None:
            s += " r=%g" % r
        if rot is not None:
            s += " rot=" + f(rot)
        s += " mat=%d" % m
        self.shapes.append(s)
        return self

    def sphere(self, c, rad, m=0, **k): return self.add("sphere %s %g" % (f(self.at(c)), rad), m, **k)
    def ell(self, c, rr, m=0, **k): return self.add("ellipsoid %s %s" % (f(self.at(c)), f(rr)), m, **k)
    def box(self, c, s, m=0, **k): return self.add("box %s %s" % (f(self.at(c)), f(s)), m, **k)
    def cyl(self, a, b, rad, m=0, **k): return self.add("cylinder %s %s %g" % (f(self.at(a)), f(self.at(b)), rad), m, **k)
    def cap(self, a, b, rad, m=0, **k): return self.add("capsule %s %s %g" % (f(self.at(a)), f(self.at(b)), rad), m, **k)
    def cone(self, a, ra, b, rb, m=0, **k): return self.add("cone %s %g %s %g" % (f(self.at(a)), ra, f(self.at(b)), rb), m, **k)
    def torus(self, c, ring, tube, m=0, **k): return self.add("torus %s %g %g" % (f(self.at(c)), ring, tube), m, **k)

    def tube(self, pts, rads, m=0, **k):
        return self.add("tube %s %s" % ("|".join(f(self.at(p)) for p in pts), "|".join("%g" % x for x in rads)), m, **k)

    def arrow(self, a, b, rad=0.07, m=2):
        """a shaft from a to b with a cone head at b"""
        d = [b[i] - a[i] for i in range(3)]
        n = sum(x * x for x in d) ** 0.5
        u = [x / n for x in d]
        neck = [b[i] - u[i] * 0.32 for i in range(3)]
        self.cyl(a, neck, rad, m)
        return self.cone(neck, rad * 2.6, b, 0.0, m)


def icons():
    I = []

    cols = {}

    def new(key, label, **k):
        i = Icon(key, label, **k)
        row = 4 if key.startswith("edit:") else ROWS[key]
        col = cols.get(row, 0)
        cols[row] = col + 1
        i.row, i.col = row, col
        i.o = (col * CELL, -row * CELL, 0)
        I.append(i)
        return i

    # ---- FORM: making a body from nothing
    new("primitive", "Primitive").box((-0.55, 0.35, 0.45), (0.9, 0.9, 0.9), r=0.04).sphere((0.6, 0.45, 0.42), 0.42).cone((0.05, -0.65, 0), 0.45, (0.05, -0.65, 1.0), 0.0)
    new("blob", "Blob", blend=0.35).sphere((0, 0, 0.6), 0.6).sphere((0.6, 0.35, 0.4), 0.42).sphere((-0.5, 0.45, 0.35), 0.36).sphere((0.15, -0.6, 0.35), 0.38)
    new("part", "Part").box((0, 0, 0.35), (1.6, 1.0, 0.7), r=0.05).cyl((-0.4, 0, 0.6), (-0.4, 0, 1.05), 0.2, m=5).cyl((0.4, 0, 0.6), (0.4, 0, 1.05), 0.2, m=5)
    sk = new("sketch", "Sketch").box((0, 0, 0.03), (2.0, 1.6, 0.06), m=1)
    sk.box((-0.3, 0.15, 0.08), (0.06, 0.9, 0.04), m=4).box((0.1, -0.3, 0.08), (0.8, 0.06, 0.04), m=4).box((0.25, 0.3, 0.08), (0.06, 0.6, 0.04), m=2, rot=(0, 0, 40))
    sk.cyl((0.55, -0.5, 0.15), (0.9, 0.5, 0.75), 0.07, m=5).cone((0.55, -0.5, 0.15), 0.07, (0.5, -0.64, 0.07), 0.0, m=4)
    pr = new("profile", "Profile").box((0, 0, 0.03), (1.9, 1.4, 0.06), m=1)
    pr.box((-0.35, 0, 0.45), (0.5, 0.08, 0.84), m=2).box((0.25, 0, 0.2), (0.7, 0.08, 0.34), m=2)
    new("spline", "Spline").tube([(-0.9, -0.5, 0.2), (-0.2, 0.6, 0.7), (0.4, -0.4, 0.9), (0.9, 0.5, 0.4)], [0.08, 0.08, 0.08, 0.08], m=2) \
        .sphere((-0.9, -0.5, 0.2), 0.16, m=5).sphere((-0.2, 0.6, 0.7), 0.16, m=5).sphere((0.4, -0.4, 0.9), 0.16, m=5).sphere((0.9, 0.5, 0.4), 0.16, m=5)
    pl = new("plane", "Plane").box((0, 0, 0.4), (1.9, 1.9, 0.05), rot=(18, -12, 0))
    pl.arrow((0, 0, 0.45), (0, 0, 1.3))
    im = new("import", "Import").box((0, 0, 0.35), (1.4, 1.4, 0.7), m=5, r=0.03).box((0, 0, 0.75), (1.1, 1.1, 0.6), cut=True, m=5)
    im.arrow((0, 0, 1.6), (0, 0, 0.55), rad=0.1)
    bo = new("bodyfrombones", "Body from bones").cap((-0.7, 0, 0.5), (0.7, 0, 0.5), 0.12, m=1)
    for x in (-0.75, 0.75):
        bo.sphere((x, 0.13, 0.5), 0.18, m=1).sphere((x, -0.13, 0.5), 0.18, m=1)
    bo.ell((0, 0, 0.5), (0.8, 0.45, 0.42), m=3, paint=False).ell((0, 0, 0.5), (0.85, 0.5, 0.47), m=3, paint=True)
    bo.box((0.3, -0.3, 0.75), (1.2, 1.2, 0.8), cut=True, m=1)

    # ---- BUILD: solids from sketches and other solids
    ex = new("extrude", "Extrude").box((0, 0, 0.03), (1.9, 1.9, 0.06), m=1).box((0, 0, 0.4), (1.0, 1.0, 0.7), r=0.02)
    ex.arrow((0, 0, 0.75), (0, 0, 1.5))
    rv = new("revolve", "Revolve").tube([(0, 0, 0.0), (0, 0, 0.6), (0, 0, 1.2)], [0.55, 0.3, 0.45]).torus((0, 0, 0.6), 0.75, 0.05, m=2)
    rv.cone((0.75, 0, 0.6), 0.14, (0.75, 0.35, 0.6), 0.0, m=2)
    lo = new("loft", "Loft").cone((0, 0, 0), 0.8, (0, 0, 1.2), 0.35).torus((0, 0, 0), 0.82, 0.06, m=2).torus((0, 0, 1.2), 0.37, 0.06, m=2)
    sw = new("sweep", "Sweep").tube([(-0.9, -0.4, 0.3), (-0.3, 0.4, 0.5), (0.3, -0.4, 0.6), (0.9, 0.4, 0.4)], [0.22, 0.22, 0.22, 0.22])
    sw.torus((-0.9, -0.4, 0.3), 0.3, 0.05, m=2, rot=(90, 0, 135))
    new("boolean", "Boolean").box((0, 0, 0.5), (1.3, 1.3, 1.0), r=0.03).sphere((0.55, -0.55, 0.95), 0.7, cut=True, m=2)
    mi = new("mirror", "Mirror").box((0, 0, 0.6), (0.06, 1.6, 1.2), m=2)
    mi.cone((-0.6, 0, 0), 0.4, (-0.75, 0.2, 1.0), 0.15).cone((0.6, 0, 0), 0.4, (0.75, 0.2, 1.0), 0.15)
    rw = new("linearpattern", "Row pattern")
    for x in (-0.75, 0, 0.75):
        rw.cyl((x, 0, 0), (x, 0, 0.9), 0.28)
    rw.arrow((-0.9, -0.55, 0.05), (1.0, -0.55, 0.05), rad=0.05)
    cp = new("circularpattern", "Circle pattern").cyl((0, 0, 0), (0, 0, 0.3), 0.15, m=5)
    for k in range(6):
        a = k * math.pi / 3
        cp.cyl((0.75 * math.cos(a), 0.75 * math.sin(a), 0), (0.75 * math.cos(a), 0.75 * math.sin(a), 0.6), 0.2)
    new("shell", "Shell").box((0, 0, 0.5), (1.4, 1.4, 1.0), r=0.03).box((0, 0, 0.75), (1.15, 1.15, 1.0), cut=True, m=2)
    new("hole", "Hole").box((0, 0, 0.35), (1.6, 1.6, 0.7), r=0.03).cyl((0, 0, -0.2), (0, 0, 1.0), 0.38, cut=True, m=2)

    # ---- REFINE: edges and surfaces
    new("fillet", "Fillet").box((0, 0, 0.55), (1.4, 1.4, 1.1), r=0.35).box((0, 0, 0.55), (1.5, 1.5, 0.18), m=2, paint=True)
    new("chamfer", "Chamfer").box((0, 0, 0.55), (1.4, 1.4, 1.1)).box((0.7, -0.7, 1.1), (1.0, 1.0, 1.0), cut=True, m=2, rot=(35, 0, 45))
    new("draft", "Draft").cone((0, 0, 0), 0.85, (0, 0, 1.2), 0.5).box((0.95, 0, 0.6), (0.05, 0.05, 1.3), m=2, rot=(0, -16, 0))
    tr = new("trim", "Trim").box((0, 0, 0.35), (1.4, 1.4, 0.7)).box((0, 0, 1.1), (1.4, 1.4, 0.5), rot=(0, 0, 0))
    tr.box((0, 0, 0.78), (1.9, 1.9, 0.04), m=2)
    sd = new("subdivide", "Subdivide").box((-0.55, 0, 0.4), (0.8, 0.8, 0.8)).box((0.55, 0, 0.4), (0.8, 0.8, 0.8), r=0.35)
    sd.arrow((-0.1, -0.7, 0.1), (0.25, -0.7, 0.1), rad=0.05)
    rm = new("remesh", "Remesh").sphere((0, 0, 0.75), 0.75)
    for z in (0.35, 0.75, 1.15):
        rm.torus((0, 0, z), 0.75 if z == 0.75 else 0.62, 0.02, m=4, paint=True)
    for a in (0, 60, 120):
        rm.torus((0, 0, 0.75), 0.75, 0.02, m=4, paint=True, rot=(90, 0, a))
    de = new("detail", "Detail").box((0, 0, 0.3), (1.6, 1.6, 0.6), r=0.05)
    for x in (-0.55, 0.55):
        for y in (-0.55, 0.55):
            de.sphere((x, y, 0.6), 0.12, m=5)
    de.box((0, 0, 0.6), (0.9, 0.08, 0.06), m=4)
    st = new("stroke", "Stroke").box((0, 0, 0.15), (1.9, 1.5, 0.3))
    st.tube([(-0.8, -0.4, 0.3), (-0.2, 0.3, 0.32), (0.4, -0.2, 0.32), (0.8, 0.4, 0.3)], [0.13, 0.17, 0.17, 0.1], m=2)

    # ---- DEFORM: pushing a body around
    sc = new("sculpt", "Sculpt", blend=0.08).ell((-0.2, 0, 0.45), (0.75, 0.6, 0.45), m=3).sphere((-0.3, 0.1, 0.85), 0.3, m=3)
    sc.cyl((0.2, -0.2, 0.7), (0.95, -0.6, 1.25), 0.08, m=5).torus((0.12, -0.15, 0.62), 0.15, 0.025, m=0, rot=(60, 0, 30))
    tf = new("transform", "Move").box((0, 0, 0.35), (0.7, 0.7, 0.7))
    tf.arrow((0.35, 0, 0.35), (1.15, 0, 0.35)).arrow((0, 0.35, 0.35), (0, 1.15, 0.35), m=5).arrow((0, 0, 0.7), (0, 0, 1.45), m=4)
    la = new("lattice", "Lattice").sphere((0, 0, 0.6), 0.45, m=3)
    for (a, b) in [((-1, -1, 0), (1, -1, 0)), ((-1, 1, 0), (1, 1, 0)), ((-1, -1, 0), (-1, 1, 0)), ((1, -1, 0), (1, 1, 0)),
                   ((-1, -1, 1), (1, -1, 1)), ((-1, 1, 1), (1, 1, 1)), ((-1, -1, 1), (-1, 1, 1)), ((1, -1, 1), (1, 1, 1)),
                   ((-1, -1, 0), (-1, -1, 1)), ((1, -1, 0), (1, -1, 1)), ((-1, 1, 0), (-1, 1, 1)), ((1, 1, 0), (1, 1, 1))]:
        la.cyl(tuple(c * 0.75 if i < 2 else c * 1.2 for i, c in enumerate(a)), tuple(c * 0.75 if i < 2 else c * 1.2 for i, c in enumerate(b)), 0.035, m=4)
    for x in (-0.75, 0.75):
        for y in (-0.75, 0.75):
            for z in (0, 1.2):
                la.sphere((x, y, z), 0.09, m=2)
    new("simpledeform", "Bend").tube([(-0.9, 0, 0.2), (-0.3, 0, 0.6), (0.3, 0, 0.75), (0.9, 0, 0.55)], [0.25, 0.25, 0.25, 0.25]) \
        .box((0, 0, 0.07), (1.9, 0.5, 0.04), m=2)
    cd = new("curvedeform", "Curve").tube([(-0.9, -0.5, 0.35), (-0.3, 0.4, 0.4), (0.3, -0.4, 0.4), (0.9, 0.5, 0.35)], [0.22, 0.22, 0.22, 0.22])
    cd.tube([(-0.9, -0.5, 0.03), (-0.3, 0.4, 0.03), (0.3, -0.4, 0.03), (0.9, 0.5, 0.03)], [0.04, 0.04, 0.04, 0.04], m=2)
    mf = new("moveface", "Push face").box((0, 0, 0.35), (1.3, 1.3, 0.7)).box((0, 0, 0.85), (0.8, 0.8, 0.4), m=2)
    mf.arrow((0, 0, 1.05), (0, 0, 1.7), rad=0.06, m=4)

    # ---- MESH: edit operations on the polygons
    e = new("edit:extrude", "Extrude faces").box((0, 0, 0.35), (1.4, 1.4, 0.7)).box((0.35, 0.35, 0.9), (0.7, 0.7, 0.5)).box((0.35, 0.35, 1.15), (0.72, 0.72, 0.06), m=2, paint=True)
    e = new("edit:inset", "Inset").box((0, 0, 0.35), (1.6, 1.6, 0.7)).box((0, 0, 0.72), (0.9, 0.9, 0.12), cut=True, m=2)
    e = new("edit:bevel", "Bevel").box((0, 0, 0.55), (1.4, 1.4, 1.1), r=0.12)
    for (c, s) in [((0, 0.7, 1.1), (1.5, 0.2, 0.2)), ((0.7, 0, 1.1), (0.2, 1.5, 0.2)), ((0.7, 0.7, 0.55), (0.2, 0.2, 1.2))]:
        e.box(c, s, m=2, paint=True)
    e = new("edit:loopcut", "Loop cut").box((0, 0, 0.6), (1.2, 1.2, 1.2), r=0.03).box((0, 0, 0.6), (1.3, 1.3, 0.05), m=2, paint=True)
    e = new("edit:bridge", "Bridge").box((-0.7, 0, 0.45), (0.5, 0.8, 0.9)).box((0.7, 0, 0.45), (0.5, 0.8, 0.9)).box((0, 0, 0.8), (0.95, 0.7, 0.2), m=2)
    e = new("edit:symmetrize", "Symmetrize").ell((0, 0, 0.55), (0.85, 0.5, 0.55)).box((0, 0, 0.55), (0.05, 1.2, 1.3), m=2, paint=True)
    e.sphere((-0.45, -0.38, 0.8), 0.12, m=4).sphere((0.45, -0.38, 0.8), 0.12, m=4)
    e = new("edit:dissolve", "Dissolve").box((-0.2, 0, 0.45), (1.0, 1.0, 0.9))
    for (c, s) in [((0.6, 0.2, 0.8), 0.18), ((0.85, -0.2, 1.05), 0.13), ((0.75, 0.45, 1.3), 0.09)]:
        e.box(c, (s, s, s), m=2, rot=(20, 30, 10))
    e = new("edit:delete", "Delete").box((0, 0, 0.5), (1.0, 1.0, 1.0)).box((0, -0.3, 0.65), (1.6, 0.14, 0.14), m=2, rot=(0, 45, 0)).box((0, -0.3, 0.65), (1.6, 0.14, 0.14), m=2, rot=(0, -45, 0))
    e = new("meshedit", "Mesh edit").box((0, 0, 0.5), (1.1, 1.1, 1.0))
    for x in (-0.55, 0.55):
        for y in (-0.55, 0.55):
            for z in (0, 1.0):
                e.sphere((x, y, z), 0.11, m=5)
    e.arrow((0.55, -0.55, 1.0), (1.0, -0.95, 1.5), rad=0.04)
    e = new("deleteface", "Delete face").box((0, 0, 0.6), (1.2, 1.2, 1.2)).box((0, -0.3, 0.6), (1.0, 1.4, 1.0), cut=True, m=2)

    # ---- CLOTH
    g = new("garment", "Garment", blend=0.06).box((0, 0, 0.55), (0.8, 0.35, 1.0), m=6, r=0.08)
    g.box((-0.6, 0, 0.85), (0.6, 0.3, 0.32), m=6, rot=(0, 30, 0), r=0.06).box((0.6, 0, 0.85), (0.6, 0.3, 0.32), m=6, rot=(0, -30, 0), r=0.06)
    g.cyl((0, 0, 0.95), (0, 0, 1.15), 0.17, cut=True, m=6)
    fb = new("fabric", "Fabric").tube([(-0.9, 0, 0.3), (-0.45, 0, 0.55), (0, 0, 0.3), (0.45, 0, 0.55), (0.9, 0, 0.3)], [0.12] * 5, m=6)
    for y in (-0.6, -0.3, 0.3, 0.6):
        fb.tube([(-0.9, y, 0.3), (-0.45, y, 0.55), (0, y, 0.3), (0.45, y, 0.55), (0.9, y, 0.3)], [0.12] * 5, m=6)
    pd = new("padding", "Padding").box((0, 0, 0.3), (1.6, 1.6, 0.6), m=6, r=0.28)
    for x in (-0.27, 0.27):
        pd.box((x, 0, 0.6), (0.04, 1.7, 0.2), m=4, paint=True).box((0, x, 0.6), (1.7, 0.04, 0.2), m=4, paint=True)
    pk = new("pocket", "Pocket").box((0, 0, 0.05), (1.4, 1.6, 0.1), m=6).box((0, -0.1, 0.13), (1.0, 1.0, 0.08), m=6, r=0.03)
    pk.box((0, 0.35, 0.2), (1.05, 0.38, 0.06), m=6, r=0.03).cyl((0, 0.3, 0.2), (0, 0.3, 0.28), 0.1, m=5)
    hw = new("hardware", "Hardware").box((-0.2, 0, 0.1), (1.2, 1.0, 0.2), m=5, r=0.04).box((-0.2, 0, 0.1), (0.8, 0.65, 0.4), cut=True, m=5)
    hw.cyl((-0.2, 0, 0.18), (0.25, 0, 0.18), 0.06, m=5).box((0.8, 0, 0.08), (1.0, 0.55, 0.12), m=6)
    wr = new("wear", "Wear").box((0, 0, 0.06), (1.7, 1.5, 0.12), m=6)
    for (c, rr) in [((0.3, 0.2, 0.06), 0.3), ((-0.45, -0.35, 0.06), 0.18), ((0.6, -0.5, 0.06), 0.12)]:
        wr.sphere(c, rr, cut=True, m=1)
    wr.box((0, 0.68, 0.1), (1.7, 0.1, 0.1), m=1, paint=True)
    we = new("wearer", "Wearer", blend=0.08).ell((0, 0, 0.5), (0.55, 0.32, 0.5), m=1).cyl((0, 0, 0.95), (0, 0, 1.15), 0.14, m=1).sphere((0, 0, 1.38), 0.3, m=1)
    we.cyl((0, 0, -0.05), (0, 0, 0.1), 0.35, m=5)
    fu = new("fur", "Fur").sphere((0, 0, 0.5), 0.5, m=5)
    for k in range(14):
        a = k * 2.4
        z = 0.15 + 0.8 * (k / 13)
        rr = (0.25 - (z - 0.5) ** 2) ** 0.5 + 0.02
        p = (rr * math.cos(a), rr * math.sin(a), z)
        tip = (p[0] * 1.9, p[1] * 1.9, 0.5 + (z - 0.5) * 1.9)
        fu.cone(p, 0.09, tip, 0.0, m=5)

    # ---- SURFACE
    mt = new("material", "Material").sphere((0, 0, 0.75), 0.6, m=2).cyl((0, 0, 0), (0, 0, 0.2), 0.5, m=4)
    mt.sphere((-0.18, -0.25, 1.05), 0.14, m=1, paint=True)
    pa = new("paint", "Paint").cyl((-0.8, 0.5, 1.1), (0.0, -0.05, 0.55), 0.09, m=5).cone((0.0, -0.05, 0.55), 0.14, (0.25, -0.25, 0.4), 0.13, m=0)
    pa.cone((0.25, -0.25, 0.4), 0.13, (0.55, -0.45, 0.15), 0.02, m=2).ell((0.75, -0.55, 0.03), (0.4, 0.25, 0.03), m=2)
    new("facematerial", "Face material").box((0, 0, 0.55), (1.1, 1.1, 1.1)).box((0, 0, 1.12), (1.2, 1.2, 0.1), m=2, paint=True)
    fl = new("fleshmaterial", "Flesh", blend=0.25).ell((0, 0, 0.45), (0.85, 0.65, 0.45), m=3).sphere((0.4, 0.3, 0.75), 0.3, m=3).sphere((-0.35, -0.2, 0.75), 0.25, m=3)
    fl.sphere((0.1, -0.35, 0.85), 0.1, m=2, paint=True)
    sk = new("skin", "Skin").cap((-0.6, 0, 0.4), (0.6, 0, 0.4), 0.4, m=3)
    for (x, y) in [(-0.4, -0.3), (0.0, -0.38), (0.35, -0.28), (-0.15, 0.2)]:
        sk.sphere((x, y, 0.7), 0.06, m=2, paint=True)
    gl = new("glow", "Glow").sphere((0, 0, 0.9), 0.5, m=7).cyl((0, 0, 0.2), (0, 0, 0.5), 0.24, m=5)
    for z in (0.28, 0.36, 0.44):
        gl.torus((0, 0, z), 0.24, 0.035, m=5)
    uv = new("uvproject", "UV project")
    for (x, y) in [(0, 0), (-0.55, 0), (0.55, 0), (0, 0.55), (0, -0.55), (0, -1.1)]:
        uv.box((x + 0.25, y + 0.25, 0.04), (0.5, 0.5, 0.08), m=1)
        uv.box((x + 0.12, y + 0.12, 0.08), (0.25, 0.25, 0.06), m=4, paint=True)
        uv.box((x + 0.37, y + 0.37, 0.08), (0.25, 0.25, 0.06), m=4, paint=True)

    # ---- RIG
    bn = new("bind", "Bind").cap((-0.7, -0.3, 0.3), (0.7, 0.3, 0.3), 0.1, m=1)
    for (x, y) in [(-0.75, -0.32), (0.75, 0.32)]:
        bn.sphere((x, y + 0.1, 0.3), 0.15, m=1).sphere((x, y - 0.1, 0.3), 0.15, m=1)
    bn.torus((0, 0, 0.3), 0.18, 0.05, m=2, rot=(0, 90, 23)).ell((0, 0, 0.3), (0.95, 0.55, 0.4), m=3, paint=True)
    bn.cyl((0, 0, 0.3), (0, 0, 1.0), 0.04, m=2).sphere((0, 0, 1.05), 0.1, m=2)
    k1 = new("shapekey", "Shape key").box((0, 0, 0.08), (1.9, 0.3, 0.16), m=4, r=0.04).box((0.25, 0, 0.3), (0.3, 0.45, 0.35), m=5, r=0.05)
    k1.sphere((-0.8, 0.5, 0.25), 0.22, m=3).ell((0.8, 0.5, 0.25), (0.3, 0.18, 0.18), m=3)
    k2 = new("shapekeyend", "Key end").box((0, 0, 0.08), (1.9, 0.3, 0.16), m=4, r=0.04).box((0.75, 0, 0.3), (0.3, 0.45, 0.35), m=5, r=0.05)
    k2.box((0.98, 0, 0.35), (0.1, 0.55, 0.6), m=2)
    co = new("correctiveshapekey", "Corrective", blend=0.12).cap((-0.8, 0, 0.3), (0, 0, 0.3), 0.28, m=3).cap((0, 0, 0.3), (0.5, 0, 0.95), 0.25, m=3)
    co.sphere((0, 0, 0.3), 0.32, m=2, paint=True)

    # ---- TWEAK: changing what is already there
    ws = new("set", "Set").cyl((-0.85, -0.45, 0.12), (0.25, 0.15, 0.12), 0.12, m=0).cyl((0.5, 0.3, 0), (0.5, 0.3, 0.24), 0.36, m=0)
    ws.box((0.72, 0.42, 0.12), (0.45, 0.28, 0.4), cut=True, rot=(0, 0, 28), m=0).cyl((0.5, 0.3, -0.1), (0.5, 0.3, 0.4), 0.14, cut=True, m=0)
    rmv = new("remove", "Remove").cone((0, 0, 0), 0.5, (0, 0, 1.0), 0.6).cyl((0, 0, 0.12), (0, 0, 1.2), 0.5, cut=True, m=0)
    rmv.cyl((0.1, 0, 1.15), (0.1, 0, 1.25), 0.68, m=4, rot=None).box((0.1, 0, 1.33), (0.3, 0.08, 0.1), m=4)
    for a in (0, 60, 120, 180, 240, 300):
        rmv.box((0.56 * math.cos(math.radians(a)), 0.56 * math.sin(math.radians(a)), 0.55), (0.06, 0.06, 0.8), m=4, paint=True)
    rd = new("remedy", "Remedy").box((0, 0, 0.35), (1.3, 0.9, 0.7), m=1, r=0.06)
    rd.box((0, -0.46, 0.4), (0.5, 0.06, 0.14), m=2, paint=True).box((0, -0.46, 0.4), (0.14, 0.06, 0.5), m=2, paint=True)
    rd.box((0, 0, 0.75), (0.5, 0.12, 0.1), m=4)
    fx = new("fix", "Fix mesh").box((0, 0, 0.5), (1.2, 1.2, 1.0), r=0.03).box((0.15, -0.15, 1.0), (0.7, 0.7, 0.3), cut=True, m=0)
    fx.box((0.15, -0.15, 1.02), (0.85, 0.85, 0.08), m=2)
    for (x, y) in [(-0.22, -0.52), (0.52, -0.52), (-0.22, 0.22), (0.52, 0.22)]:
        fx.sphere((x, y, 1.08), 0.08, m=5)

    # ---- CHECK: reading the model back
    ms = new("measure", "Measure").box((0, 0, 0.1), (2.0, 0.5, 0.2), m=5, r=0.03)
    for k in range(9):
        ms.box((-0.8 + k * 0.2, -0.12, 0.21), (0.05, 0.26 if k % 2 else 0.4, 0.04), m=4)
    ms.box((-0.95, 0.2, 0.45), (0.1, 0.1, 0.7), m=2).box((0.95, 0.2, 0.45), (0.1, 0.1, 0.7), m=2)
    ds = new("distance", "Distance").sphere((-0.75, 0, 0.35), 0.35).sphere((0.75, 0, 0.35), 0.35)
    ds.arrow((0, 0, 0.95), (-0.75, 0, 0.95), rad=0.06).arrow((0, 0, 0.95), (0.75, 0, 0.95), rad=0.06)
    sy = new("symmetry", "Symmetry").ell((0, 0, 0.75), (0.6, 0.5, 0.75), m=1)
    sy.sphere((-0.24, -0.42, 0.95), 0.12, m=4).sphere((0.24, -0.42, 0.95), 0.12, m=4).box((0, -0.5, 0.75), (0.08, 0.3, 1.7), m=2, paint=True)
    cl = new("clearance", "Clearance").box((-0.6, 0, 0.5), (0.7, 1.2, 1.0), r=0.03).box((0.6, 0, 0.5), (0.7, 1.2, 1.0), r=0.03)
    cl.box((0, 0, 0.5), (0.5, 0.5, 0.12), m=2).box((-0.22, 0, 0.5), (0.06, 0.6, 0.4), m=2).box((0.22, 0, 0.5), (0.06, 0.6, 0.4), m=2)
    tp = new("topology", "Topology").box((0, 0, 0.05), (1.8, 1.8, 0.1), m=1)
    for k in range(4):
        v = -0.75 + k * 0.5
        tp.box((v, 0, 0.12), (0.06, 1.56, 0.06), m=4).box((0, v, 0.12), (1.56, 0.06, 0.06), m=4)
    tp.sphere((0.25, -0.25, 0.18), 0.13, m=2)
    se = new("section", "Section").cyl((0, 0, 0), (0, 0, 1.2), 0.75).cyl((0, 0, -0.1), (0, 0, 1.3), 0.4, cut=True, m=0)
    se.box((0, -0.6, 0.6), (1.8, 1.2, 1.6), cut=True, m=0).box((0, 0, 0.6), (1.8, 0.06, 1.4), m=2, paint=True)
    fe = new("flex", "Flex", blend=0.15).cap((-0.8, 0, 0.3), (0.05, 0, 0.3), 0.26, m=3).cap((0.05, 0, 0.3), (0.05, 0, 1.2), 0.24, m=3)
    fe.arrow((0.7, 0, 0.35), (0.6, 0, 1.15), rad=0.07)
    mc = new("match", "Match").box((-0.55, 0.3, 0.75), (0.9, 0.08, 1.3), m=1).box((-0.55, 0.25, 0.75), (0.5, 0.04, 0.8), m=4)
    mc.box((0.55, 0, 0.4), (0.5, 0.5, 0.8)).box((0, -0.3, 0.55), (0.3, 0.1, 0.08), m=2).box((0, -0.3, 0.75), (0.3, 0.1, 0.08), m=2)
    dc = new("describe", "Describe").box((0, 0, 0.05), (1.4, 1.8, 0.1), m=1)
    for k in range(5):
        dc.box((-0.08 if k % 2 else 0, 0.6 - k * 0.3, 0.12), (1.0 if k % 2 else 1.15, 0.1, 0.05), m=4)
    dc.cyl((0.55, -0.75, 0.15), (0.95, 0.4, 0.8), 0.08, m=2)

    # ---- OUTPUT: pictures and files out of the model
    rn = new("render", "Render").box((0, 0, 0.45), (1.5, 0.9, 0.9), r=0.08).cyl((0, -0.4, 0.45), (0, -0.75, 0.45), 0.34, m=4)
    rn.cyl((0, -0.74, 0.45), (0, -0.78, 0.45), 0.22, m=7).box((-0.45, 0, 1.0), (0.4, 0.5, 0.2), m=0).sphere((0.5, -0.4, 0.8), 0.1, m=2)
    tn = new("turnaround", "Turnaround").cyl((0, 0, 0), (0, 0, 0.15), 0.85, m=5).box((0, 0, 0.6), (0.5, 0.5, 0.9), r=0.05)
    tn.torus((0, 0, 0.25), 0.95, 0.06, m=2).cone((0, -0.95, 0.25), 0.16, (0.4, -0.95, 0.25), 0.0, m=2)
    sv = new("save", "Save").box((0, 0, 0.1), (1.6, 1.6, 0.2), m=4, r=0.04).box((0, -0.3, 0.22), (1.0, 0.9, 0.06), m=1)
    sv.box((0.1, 0.55, 0.22), (0.8, 0.5, 0.06), m=0).box((0.25, 0.55, 0.26), (0.18, 0.32, 0.04), m=4)
    xp = new("export", "Export").box((0, 0, 0.35), (1.4, 1.4, 0.7), m=5, r=0.03).box((0, 0, 0.75), (1.1, 1.1, 0.6), cut=True, m=5)
    xp.box((0, 0, 0.55), (0.6, 0.6, 0.6)).arrow((0, 0, 0.9), (0, 0, 1.75), rad=0.1)
    bk = new("bake", "Bake").box((-0.55, 0, 0.35), (0.7, 0.7, 0.7), r=0.03).box((0.6, 0, 0.04), (0.9, 0.9, 0.08), m=1)
    for (x, y) in [(0.38, -0.22), (0.82, 0.22)]:
        bk.box((x, y, 0.09), (0.44, 0.44, 0.05), m=4, paint=True)
    bk.arrow((-0.15, 0, 0.8), (0.55, 0, 0.45), rad=0.07)
    pm = new("paintmaps", "Paint maps")
    for k, mm in enumerate((4, 5, 2)):
        pm.box((-0.3 + k * 0.3, 0.3 - k * 0.3, 0.06 + k * 0.14), (1.1, 1.1, 0.1), m=1)
        pm.box((-0.3 + k * 0.3, 0.3 - k * 0.3, 0.12 + k * 0.14), (0.8, 0.8, 0.04), m=mm)
    pv = new("pivot", "Pivot").cone((0, 0, 0), 0.4, (0, 0, 0.55), 0.06, m=5).box((0, 0, 0.66), (2.0, 0.3, 0.14), rot=(0, -14, 0))
    pv.sphere((0, 0, 0.66), 0.17, m=2)
    rb = new("rebuild", "Rebuild").box((0, 0, 0.45), (0.8, 0.8, 0.8), r=0.03).torus((0, 0, 0.45), 0.85, 0.07, m=2, rot=(70, 0, 0))
    rb.cone((0.85, 0, 0.45), 0.18, (0.85, 0, 0.0), 0.0, m=2).cone((-0.85, 0, 0.45), 0.18, (-0.85, 0, 0.9), 0.0, m=2)
    redo({i.key: i for i in I})
    return I


def redo(by):
    """Judge redesigns (judge/round-3/report.md): these replace the first designs above. The render camera
    looks from (6,-8,6.5), so screen-right is U = (0.8, 0.6) in plan; long things lie along U to read flat."""
    def re(key, blend=0.03):
        i = by[key]
        i.shapes, i.blend = [], blend
        return i

    def arrow(i, a, b, rad=0.09, m=2, head=0.42):
        """a shaft with a long, pointed head (the squat helper head reads as a ball at 60 px)"""
        d = [b[j] - a[j] for j in range(3)]
        n = sum(x * x for x in d) ** 0.5
        neck = [b[j] - d[j] / n * head for j in range(3)]
        i.cyl(a, neck, rad, m)
        return i.cone(neck, rad * 2.4, b, 0.0, m)

    def along(t, z=0.0, side=0.0):
        """a point t along screen-right U, side along the screen-depth normal"""
        return (0.8 * t + 0.6 * side, 0.6 * t - 0.8 * side, z)

    # FORM
    b = re("blob", blend=0.32).sphere((-0.5, 0, 0.45), 0.48, m=3).sphere((0.45, 0.1, 0.42), 0.42, m=3).sphere((-0.05, -0.15, 1.0), 0.36, m=3)
    b.ell((-0.05, -0.05, 0.5), (0.22, 0.5, 0.18), m=2, paint=True)
    s = re("sketch").box((0, 0, 0.05), (2.2, 1.7, 0.1), m=1)
    for (c, sz) in [((-0.35, -0.45, 0.12), (1.1, 0.12, 0.05)), ((-0.35, 0.35, 0.12), (1.1, 0.12, 0.05)),
                    ((-0.9, -0.05, 0.12), (0.12, 0.92, 0.05)), ((0.2, -0.05, 0.12), (0.12, 0.92, 0.05))]:
        s.box(c, sz, m=4)
    s.cyl((0.45, -0.5, 0.25), (1.05, 0.25, 1.05), 0.17, m=5).cone((0.45, -0.5, 0.25), 0.17, (0.3, -0.68, 0.1), 0.0, m=2)
    pr = re("profile")
    ol = [along(t, z) for t, z in [(-0.85, 0.05), (0.85, 0.05), (0.85, 0.6), (-0.15, 0.6), (-0.15, 1.55), (-0.85, 1.55), (-0.85, 0.05)]]
    pr.tube(ol, [0.12] * 7, m=2)
    for t, z in [(-0.5, 0.33), (0.35, 0.33), (-0.5, 1.05)]:
        pr.box(along(t, z, 0.15), (0.12, 0.12, 0.12), m=4, rot=(0, 0, 37))
    pts = [along(-1.15 + k * 0.144, 0.75 + 0.6 * math.sin(k * 0.4)) for k in range(17)]
    sp = re("spline").tube(pts, [0.11] * 17, m=2)
    for k in (0, 8, 16):
        sp.sphere(pts[k], 0.2, m=5)
    h0, h1 = along(-0.35, 1.25), along(0.45, 1.45)
    sp.cyl(h0, h1, 0.04, m=4).sphere(h0, 0.12, m=4).sphere(h1, 0.12, m=4)
    for key, file_t, cube_t, a0, a1 in (("import", -0.85, 0.75, -0.5, 0.35), ("export", 0.85, -0.75, -0.35, 0.5)):
        d = re(key)
        fc = along(file_t, 0.65)
        d.box(fc, (0.62, 0.62, 0.9), m=1, rot=(0, 0, 37)).box(along(file_t + 0.32, 1.1), (0.4, 0.4, 0.4), cut=True, m=1, rot=(0, 45, 37))
        for k in range(3):
            d.box(along(file_t, 0.82 - k * 0.2, -0.33), (0.42, 0.05, 0.07), m=4, rot=(0, 0, 37))
        d.box(along(cube_t, 0.4), (0.8, 0.8, 0.8))
        arrow(d, along(a0, 0.55, -0.45), along(a1, 0.55, -0.45), rad=0.11)
    bo = re("bodyfrombones", blend=0.06).cap(along(-0.85, 0.45), along(0.85, 0.45), 0.13, m=1)
    for t in (-0.95, 0.95):
        bo.sphere(along(t, 0.45, 0.17), 0.24, m=1).sphere(along(t, 0.45, -0.17), 0.24, m=1)
    bo.ell(along(0, 0.5), (0.55, 0.45, 0.42), m=3, rot=(0, 0, 37))
    arrow(bo, along(0, 0.95, -0.2), along(0, 1.55, -0.2), rad=0.07)

    # BUILD
    rail = [along(-1.1 + 2.2 * k / 10, 0.05, -0.55 * math.sin(math.pi * k / 10)) for k in range(11)]
    sw = re("sweep").tube([(x, y, 0.4) for x, y, _ in rail[:8]], [0.32] * 8)
    sw.tube(rail, [0.07] * 11, m=2).torus((rail[0][0], rail[0][1], 0.4), 0.36, 0.07, m=2, rot=(0, 90, 37))
    lo = re("loft", blend=0.15).box((0, 0, 0.1), (1.3, 1.3, 0.2), r=0.03).box((0, 0, 0.45), (1.1, 1.1, 0.25), r=0.25)
    lo.box((0, 0, 0.8), (0.95, 0.95, 0.25), r=0.42).cyl((0, 0, 0.95), (0, 0, 1.25), 0.45)
    lo.box((0, 0, 0.06), (1.4, 1.4, 0.13), m=2, paint=True).cyl((0, 0, 1.15), (0, 0, 1.35), 0.6, m=2, paint=True)
    cp = re("circularpattern").torus((0, 0, 0.05), 0.85, 0.08, m=2).cone(along(0.05, 0.05, 0.86), 0.2, along(-0.4, 0.05, 0.78), 0.0, m=2)
    for k in range(6):
        a = k * math.pi / 3 + 0.25
        cp.cyl((0.85 * math.cos(a), 0.85 * math.sin(a), 0), (0.85 * math.cos(a), 0.85 * math.sin(a), 0.55), 0.26)

    # REFINE
    f = re("fillet").box((0, 0.35, 0.6), (1.5, 0.8, 1.2)).box((0, -0.35, 0.3), (1.5, 0.7, 0.6))
    f.cyl((-0.75, -0.05, 0.6), (0.75, -0.05, 0.6), 0.6, m=2)
    d = re("draft").box((0, 0, 0.55), (1.5, 1.5, 1.1))
    for rot, c in (((-20, 0, 0), (0, -1.05, 0.55)), ((20, 0, 0), (0, 1.05, 0.55)), ((0, -20, 0), (1.05, 0, 0.55)), ((0, 20, 0), (-1.05, 0, 0.55))):
        d.box(c, (2.6, 0.8, 2.4) if rot[0] else (0.8, 2.6, 2.4), cut=True, rot=rot, m=2)
    arrow(d, (0, 0, 1.15), (0, 0, 1.8), rad=0.08, m=4)
    t = re("trim").box((0, 0, 0.3), (1.4, 1.4, 0.6)).box((0, 0, 0.64), (2.0, 2.0, 0.05), m=2)
    t.box((0.2, 0.15, 1.25), (1.4, 1.4, 0.5), rot=(0, -14, 0))

    # DEFORM
    sc = re("sculpt", blend=0.08).ell((0, 0, 0.35), (1.0, 0.8, 0.35), m=3).sphere((0, -0.1, 0.75), 0.36, cut=True, m=3)
    sc.torus((0, -0.1, 0.52), 0.36, 0.07, m=3)
    sc.torus((0.05, -0.1, 0.62), 0.24, 0.06, m=2, rot=(30, 0, 37)).cyl((0.2, -0.25, 0.8), (0.75, -0.75, 1.55), 0.12, m=5)
    tf = re("transform").box((0, 0, 0.3), (0.5, 0.5, 0.5))
    arrow(tf, along(0.3, 0.3), along(1.3, 0.3), rad=0.1, m=2)
    arrow(tf, along(-0.3, 0.3, 0.0), along(-1.25, 0.3, 0.0), rad=0.1, m=7)
    arrow(tf, (0, 0, 0.55), (0, 0, 1.6), rad=0.1, m=6)
    bd = re("simpledeform").tube([along(-0.8, 0.12), along(-0.72, 0.75), along(-0.3, 1.1), along(0.3, 1.1), along(0.72, 0.75), along(0.8, 0.12)], [0.22] * 6)
    arc = [along(1.0 * math.cos(math.radians(a)), 0.55 + 1.0 * math.sin(math.radians(a)), -0.35) for a in (150, 120, 90, 60)]
    bd.tube(arc, [0.06] * 4, m=2).cone(arc[-1], 0.16, along(1.0 * math.cos(math.radians(38)), 0.55 + 1.0 * math.sin(math.radians(38)), -0.35), 0.0, m=2)
    cpts = [along(-1.15 + 2.3 * k / 16, 0.06, 0.55 * math.sin(2 * math.pi * k / 16)) for k in range(17)]
    cd = re("curvedeform").tube(cpts, [0.1] * 17, m=2).sphere(cpts[0], 0.17, m=7).sphere(cpts[-1], 0.17, m=7)
    cd.tube([(x, y, 0.36) for x, y, _ in cpts[3:14]], [0.26] * 11)
    cd.cyl(along(-0.7, 1.35), along(0.7, 1.35), 0.11, m=1)
    # MESH
    e = re("edit:bevel").box((0, 0, 0.55), (1.4, 1.4, 1.1))
    e.box((0, -0.85, 1.05), (1.6, 0.5, 0.8), cut=True, rot=(25, 0, 0), m=2).box((0, -0.85, 1.25), (1.6, 0.8, 0.5), cut=True, rot=(65, 0, 0), m=2)
    for x in (-0.7, 0.7):
        e.box((x, 0, 0.55), (0.05, 1.42, 0.05), m=4).box((x, -0.7, 0.4), (0.05, 0.05, 0.85), m=4)
    for x in (-0.25, 0.25):
        e.box((x, -0.71, 0.4), (0.04, 0.03, 0.8), m=4)
    e = re("edit:symmetrize").ell((0, 0, 0.55), (0.95, 0.55, 0.5))
    for x in (-0.55, 0.55):
        e.sphere((x, -0.15, 0.95), 0.25)
    e.box((0, 0, 0.6), (0.06, 1.4, 1.4), m=2)
    arrow(e, (-0.6, -0.5, 1.4), (0.6, -0.5, 1.4), rad=0.07, m=2, head=0.3)
    e = re("edit:dissolve").box((0, 0, 0.6), (1.5, 1.5, 1.2))
    for c, sz in [((0, -0.72, 1.22), (1.5, 0.1, 0.06)), ((0, 0.72, 1.22), (1.5, 0.1, 0.06)), ((-0.72, 0, 1.22), (0.1, 1.5, 0.06)),
                  ((0.72, 0, 1.22), (0.1, 1.5, 0.06)), ((0, 0, 1.22), (1.5, 0.1, 0.06))]:
        e.box(c, sz, m=1)
    for k in range(4):
        e.box((0, -0.55 + k * 0.37, 1.23), (0.12, 0.17, 0.08), m=2)
    e = re("edit:delete").box((0, 0.1, 0.5), (1.0, 1.0, 1.0)).box((0.4, -0.3, 0.9), (0.6, 0.6, 0.6), cut=True)
    for c in [(0.1, -0.4, 1.0), (0.5, 0.0, 1.0), (0.5, -0.4, 0.6)]:
        e.sphere(c, 0.09, m=7)
    e.box((0, -0.62, 0.55), (1.5, 0.12, 0.24), m=2, rot=(0, 45, 0)).box((0, -0.62, 0.55), (1.5, 0.12, 0.24), m=2, rot=(0, -45, 0))

    # CLOTH
    pk = re("pocket").box((0, 0, 0.05), (1.6, 1.4, 0.1), m=6).box((0, -0.05, 0.2), (1.1, 1.0, 0.22), m=6, r=0.04)
    pk.box((0, 0.3, 0.4), (1.2, 0.45, 0.12), m=4, r=0.04).sphere((0, 0.15, 0.48), 0.13, m=5)
    for k in range(6):
        pk.box((-0.5 + k * 0.2, -0.57, 0.32), (0.12, 0.05, 0.04), m=2)
    for y in (-0.45, -0.25, -0.05):
        pk.box((-0.57, y, 0.32), (0.05, 0.12, 0.04), m=2).box((0.57, y, 0.32), (0.05, 0.12, 0.04), m=2)
    wr = re("wear").box((0, 0, 0.06), (2.0, 1.7, 0.12), m=6)
    for (c, rr) in [((0.15, -0.1, 0.06), 0.42), ((0.5, 0.15, 0.06), 0.3), ((-0.15, 0.2, 0.06), 0.25)]:
        wr.sphere(c, rr, cut=True)
    for a in range(0, 360, 45):
        r0, c = 0.5, (0.2, 0.05)
        wr.cone((c[0] + (r0 + 0.12) * math.cos(math.radians(a)), c[1] + (r0 + 0.08) * math.sin(math.radians(a)), 0.1), 0.05,
                (c[0] + (r0 - 0.15) * math.cos(math.radians(a)), c[1] + (r0 - 0.18) * math.sin(math.radians(a)), 0.1), 0.0, m=2)
    fu = re("fur").box((0, 0, 0.06), (1.7, 1.4, 0.12), m=3)
    for i in range(4):
        for j in range(3):
            x, y = -0.6 + i * 0.4, -0.4 + j * 0.4
            for dx, dy in ((0, 0), (0.1, 0.08), (-0.08, 0.1)):
                fu.cone((x + dx, y + dy, 0.1), 0.09, (x + dx + 0.2, y + dy - 0.1, 0.85 + 0.1 * ((i + j) % 2)), 0.0, m=5)

    # SURFACE
    fl = re("fleshmaterial").box((0, 0, 0.22), (1.7, 1.4, 0.44), m=2).box((0, 0, 0.52), (1.7, 1.4, 0.16), m=1).box((0, 0, 0.72), (1.7, 1.4, 0.24), m=3)
    fl.box((0.5, -0.45, 0.62), (0.9, 0.7, 0.6), cut=True, m=2)
    sk = re("skin").ell((0, 0, 0.0), (1.05, 0.9, 0.6), m=3).box((0, 0, -0.5), (2.4, 2.4, 1.0), cut=True, m=3)
    for (x, y, rr) in [(-0.55, -0.35, 0.1), (-0.2, -0.5, 0.08), (0.3, -0.35, 0.11), (-0.4, 0.1, 0.09), (0.05, -0.05, 0.1),
                       (0.45, 0.15, 0.08), (-0.15, 0.4, 0.09), (0.3, 0.5, 0.07)]:
        sk.sphere((x, y, 0.6 * (1 - (x / 1.05) ** 2 - (y / 0.9) ** 2) ** 0.5), rr, m=8, paint=True)
    sk.sphere((0.6, -0.15, 0.6 * (1 - (0.6 / 1.05) ** 2 - (0.15 / 0.9) ** 2) ** 0.5), 0.09, m=2, paint=True)
    # RIG
    k = re("shapekey").cyl(along(-0.75, 0.3), along(0.75, 0.3), 0.14, m=1).box(along(0.0, 0.55), (0.42, 0.42, 0.7), m=2, r=0.08, rot=(0, 0, 37))
    k.box(along(-1.05, 0.5), (0.7, 0.7, 0.7), rot=(0, 0, 37)).sphere(along(1.05, 0.5), 0.48, m=3)
    k = re("shapekeyend").cyl(along(-0.9, 0.15), along(0.35, 0.15), 0.12, m=4).box(along(0.15, 0.38), (0.3, 0.3, 0.42), m=5, r=0.06, rot=(0, 0, 37))
    k.box(along(0.45, 0.4), (0.18, 0.75, 0.75), m=2, rot=(0, 0, 37)).sphere(along(0.45, 1.2), 0.42, m=3).torus(along(0.45, 1.2), 0.5, 0.06, m=2, rot=(90, 0, 37))

    # TWEAK
    st = re("set").cyl((0, 0, 0), (0, 0, 0.12), 1.05, m=4).cyl((0, 0, 0.12), (0, 0, 0.7), 0.72)
    st.box(along(0.0, 0.72, -0.34), (0.16, 0.16, 0.08), m=2, rot=(0, 0, 37)).box(along(0.0, 0.73, -0.12), (0.12, 0.42, 0.06), m=2, rot=(0, 0, 37))
    for a in range(0, 360, 30):
        st.box((0.9 * math.cos(math.radians(a)), 0.9 * math.sin(math.radians(a)), 0.14), (0.2, 0.07, 0.05), m=1, rot=(0, 0, a))
    fx = re("fix").box((-0.25, 0.2, 0.55), (1.1, 1.1, 1.1)).box((-0.25, -0.38, 0.55), (0.75, 0.12, 0.3), m=2)
    fx.cyl((0.95, -0.7, 0.1), (0.35, -0.2, 1.05), 0.1, m=5).torus((0.95, -0.7, 0.1), 0.22, 0.09, m=5, rot=(40, 0, 37))
    fx.torus((0.35, -0.2, 1.05), 0.22, 0.09, m=5, rot=(40, 0, 37)).box((0.45, -0.32, 1.2), (0.2, 0.2, 0.35), cut=True, m=5, rot=(0, 0, 37))

    # CHECK
    sy = re("symmetry", blend=0.05).cap((0, -0.5, 0.2), (0, 0.5, 0.2), 0.13, m=4)
    for sx in (-1, 1):
        sy.ell((sx * 0.55, 0.15, 0.2), (0.52, 0.38, 0.08), m=6, rot=(0, 0, sx * 20)).ell((sx * 0.42, -0.38, 0.2), (0.34, 0.27, 0.08), m=6, rot=(0, 0, -sx * 20))
    sy.box((0, 0, 0.35), (0.13, 1.7, 0.12), m=2)
    tp = re("topology").box((0, 0, 0.1), (2.1, 2.1, 0.2))
    for k in range(4):
        v = -0.99 + k * 0.66
        tp.box((v, 0, 0.22), (0.1, 2.08, 0.06), m=1).box((0, v, 0.22), (2.08, 0.1, 0.06), m=1)
    tp.box((-0.33, -0.33, 0.23), (0.1, 0.93, 0.07), m=2, rot=(0, 0, 45)).sphere((-0.33, -0.33, 0.27), 0.17, m=2)
    fe = re("flex").cap(along(-1.0, 0.35), along(0.1, 0.35), 0.24, m=3).cap(along(0.1, 0.35), along(0.35, 1.35), 0.22, m=3).sphere(along(0.1, 0.35), 0.32, m=5)
    arc = [along(0.1 + 0.85 * math.cos(math.radians(a)), 0.35 + 0.85 * math.sin(math.radians(a)), -0.35) for a in (8, 30, 52)]
    fe.tube(arc, [0.07] * 3, m=2).cone(arc[-1], 0.18, along(0.1 + 0.85 * math.cos(math.radians(72)), 0.35 + 0.85 * math.sin(math.radians(72)), -0.35), 0.0, m=2)
    dc = re("describe").box((0, 0, 0.85), (1.6, 0.2, 1.0), m=1, r=0.12).cone((-0.4, 0, 0.4), 0.18, (-0.6, 0, 0.0), 0.0, m=1)
    for k in range(3):
        dc.box((-0.08 if k == 1 else 0, -0.11, 1.1 - k * 0.25), (1.1 if k != 1 else 0.95, 0.04, 0.1), m=4 if k else 2)
    dc.box((0.75, -0.3, 0.2), (0.4, 0.4, 0.4))

    # OUTPUT
    bk = re("bake").box((-0.45, 0, 0.75), (0.75, 0.75, 0.75)).cyl((-0.45, 0, 0.0), (-0.45, 0, 0.12), 0.75, m=4)
    for rr in (0.25, 0.5):
        bk.torus((-0.45, 0, 0.16), rr, 0.07, m=7)
    bk.box((0.8, 0.35, 0.05), (0.85, 0.85, 0.1), m=1)
    for (x, y) in [(0.6, 0.15), (1.0, 0.55)]:
        bk.box((x, y, 0.11), (0.42, 0.42, 0.04), m=2)


GROUPS = ["FORM", "BUILD", "REFINE", "DEFORM", "MESH", "CLOTH", "SURFACE", "RIG", "TWEAK", "CHECK", "OUTPUT"]
ROWS = {"primitive": 0, "blob": 0, "part": 0, "sketch": 0, "profile": 0, "spline": 0, "plane": 0, "import": 0, "bodyfrombones": 0,
        "extrude": 1, "revolve": 1, "loft": 1, "sweep": 1, "boolean": 1, "mirror": 1, "linearpattern": 1, "circularpattern": 1, "shell": 1, "hole": 1,
        "fillet": 2, "chamfer": 2, "draft": 2, "trim": 2, "subdivide": 2, "remesh": 2, "detail": 2, "stroke": 2,
        "sculpt": 3, "transform": 3, "lattice": 3, "simpledeform": 3, "curvedeform": 3, "moveface": 3,
        "meshedit": 4, "deleteface": 4,
        "garment": 5, "fabric": 5, "padding": 5, "pocket": 5, "hardware": 5, "wear": 5, "wearer": 5, "fur": 5,
        "material": 6, "paint": 6, "facematerial": 6, "fleshmaterial": 6, "skin": 6, "glow": 6, "uvproject": 6,
        "bind": 7, "shapekey": 7, "shapekeyend": 7, "correctiveshapekey": 7,
        "set": 8, "remove": 8, "remedy": 8, "fix": 8,
        "measure": 9, "distance": 9, "symmetry": 9, "clearance": 9, "topology": 9, "section": 9, "flex": 9, "match": 9, "describe": 9,
        "render": 10, "turnaround": 10, "save": 10, "export": 10, "bake": 10, "paintmaps": 10, "pivot": 10, "rebuild": 10}


def main():
    only = None
    for arg in sys.argv[1:]:
        if arg.startswith("only="):
            only = set(arg[5:].split(","))   # build just these (headless rework rounds); cells.json keeps all
    out = ["# The tool wheel's icons for the Geppetto chest: one small object per modelling tool, a row per",
           "# category (FORM, BUILD, REFINE, DEFORM, MESH, CLOTH, SURFACE, RIG, TWEAK, CHECK, OUTPUT), cells %g apart. Z up." % CELL,
           "# Written by make_build.py; render_icons.py cuts the export back into cells.",
           "material 0 color=#a9b0ba rough=0.45 metal=0.6", "material 1 color=#e9e3cc rough=0.8",
           "material 2 color=#c8322a rough=0.45", "material 3 color=#d9907c rough=0.6",
           "material 4 color=#1d1a2a rough=0.6", "material 5 color=#c79a3e metal=1 rough=0.3",
           "material 6 color=#3d5a8a rough=0.85", "material 7 color=#ffd25a emit=#ffc840 rough=0.3",
           "material 8 color=#7a4a24 rough=0.7"]
    cells = {}
    row = None
    for icon in icons():
        if icon.row != row:
            row = icon.row
            out.append("echo ---- %s" % GROUPS[row])
        name = "Icon_" + icon.key.replace(":", "_")
        cells[icon.key] = {"label": icon.label, "group": GROUPS[icon.row], "body": name,
                           "cx": icon.col * CELL, "cy": -icon.row * CELL}
        if only and icon.key not in only:
            continue
        out.append("echo %s" % icon.label)
        out.append('add Blob name=%s voxel=0.025 blend=%g cutblend=0.02 shapes="%s"' % (name, icon.blend, "; ".join(icon.shapes)))
    out += ([] if only else ["turnaround out shade=material size=1024 views=quarter name=toolicons", "save toolicons.effigy"]) \
        + ["export out name=toolicons"]
    open(os.path.join(HERE, "build.txt"), "w").write("\n".join(out) + "\n")
    json.dump({"cell": CELL, "icons": cells}, open(os.path.join(HERE, "cells.json"), "w"), indent=1)
    print(len(cells), "icons")


if __name__ == "__main__":
    main()
