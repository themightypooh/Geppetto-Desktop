#!/usr/bin/env python3
"""Writes build.txt: a sheet of paper crumpled into a ball, then unfolded flat in steps.
The sheet is a grid of thin facets fused in one Effigy Blob. Each step wraps the grid round a sphere
whose radius grows (ball -> flat) and jitters each grid corner off the surface (facets share corners, so they meet in creases); the jitter fades as it opens."""
import math, random
W, H, NX, NY = 5.2, 4.6, 13, 12
R0 = 0.68                  # tight enough that even the edge midpoints wrap past the top: a closed ball
STEPS = [round((i / 15) ** 1.7, 4) for i in range(16)]   # more steps while it is still a ball
rnd = random.Random(7)
vjit = [[rnd.uniform(-1, 1) for _ in range(NY + 1)] for _ in range(NX + 1)]

def rot(axis, a):
    x, y, z = axis; c, s = math.cos(a), math.sin(a); t = 1 - c
    return [[c + x*x*t, x*y*t - z*s, x*z*t + y*s], [y*x*t + z*s, c + y*y*t, y*z*t - x*s], [z*x*t - y*s, z*y*t + x*s, c + z*z*t]]

def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

def euler_xyz(m):  # m = Rz(c) Ry(b) Rx(a), as Effigy's Xform.FromEulerXyz
    b = -math.asin(max(-1, min(1, m[2][0])))
    return math.degrees(math.atan2(m[2][1], m[2][2])), math.degrees(b), math.degrees(math.atan2(m[1][0], m[0][0]))

def sheet_point(x, y, t, k, R, noise):
    r = math.hypot(x, y); f = math.atan2(y, x); th = r / R
    cf, sf, ct, st = math.cos(f), math.sin(f), math.cos(th), math.sin(th)
    n = [-st*cf, -st*sf, ct]
    # creases mostly dent in: a corner pushed out reads as a spike from above
    d = (noise if noise < 0 else 0.35 * noise) * (0.22*k + 0.012)
    return [R*st*cf + n[0]*d, R*st*sf + n[1]*d, R*(1 - ct) + n[2]*d]

def shapes(t):
    k = (1 - t) ** 1.5
    R = R0 / max(1e-4, (1 - t)) ** 1.6
    P = [[sheet_point(-W/2 + W*i/NX, -H/2 + H*j/NY, t, k, R, vjit[i][j]) for j in range(NY + 1)] for i in range(NX + 1)]
    out = []
    for i in range(NX):
        for j in range(NY):
            a, b, c, d = P[i][j], P[i+1][j], P[i][j+1], P[i+1][j+1]
            ctr = [(a[q] + b[q] + c[q] + d[q]) / 4 for q in range(3)]
            u = [(b[q] + d[q] - a[q] - c[q]) / 2 for q in range(3)]
            v = [(c[q] + d[q] - a[q] - b[q]) / 2 for q in range(3)]
            lu = math.sqrt(sum(x*x for x in u)); lv = math.sqrt(sum(x*x for x in v))
            n = [u[1]*v[2] - u[2]*v[1], u[2]*v[0] - u[0]*v[2], u[0]*v[1] - u[1]*v[0]]
            ln = math.sqrt(sum(x*x for x in n)); n = [x/ln for x in n]
            tu = [x/lu for x in u]
            tv = [n[1]*tu[2] - n[2]*tu[1], n[2]*tu[0] - n[0]*tu[2], n[0]*tu[1] - n[1]*tu[0]]
            m = [[tu[q], tv[q], n[q]] for q in range(3)]
            ex, ey, ez = euler_xyz(m)
            out.append("box %.3f,%.3f,%.3f %.3f,%.3f,0.07 r=0.03 rot=%.1f,%.1f,%.1f mat=0"
                       % (ctr[0], ctr[1], ctr[2] + 0.03, lu*1.03, lv*1.03, ex, ey, ez))
    return "; ".join(out)

L = ["material 0 color=#ece2c8 rough=0.9",
     "echo a sheet crumpled into a ball",
     'add Blob name=Paper voxel=0.018 blend=0.08 shapes="%s"' % shapes(0),
     "describe", "turnaround out shade=material size=512 name=paperball_crumpled", "export poses name=unfold_00"]
for n, t in enumerate(STEPS[1:], 1):
    L += ["echo unfolding %d%%" % round(t*100), 'set Paper shapes="%s"' % shapes(t), "export poses name=unfold_%02d" % n]
L += ["turnaround out shade=material size=512 name=paperball_open", "save paperball.effigy"]
import sys
if "ball" in sys.argv:                      # just the crumpled ball, to check its look
    L = L[:5] + ["save paperball_ball.effigy"]
open("build.txt", "w").write("\n".join(L) + "\n")
