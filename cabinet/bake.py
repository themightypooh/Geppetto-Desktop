#!/usr/bin/env python3
"""Bake the Geppetto tool-chest art: offline, once. Writes art/*.png and art/geom.js.

A machinist's roller chest in red enamelled sheet steel: rolled edges, chrome trim, rivets,
pressed louvres, ball-bearing drawer slides, black ribbed rubber drawer liners, full-width
extruded chrome bar pulls, stamped aluminium label plates and Dymo label tape.

Everything is drawn at "art" resolution (one art pixel = S screen pixels) from small fixed ramps
with 4x4 ordered (Bayer) dithering, Amiga style, then upscaled nearest-neighbour so QML shows it
1:1 with no smoothing.

  frame.png        the chest body the viewport sits in (hole in the middle, drop shadow)
  drawer_*.png     the three drawers seen from above: enamel lip, zinc walls, rubber liner,
                   chrome slides on the sides and a chrome bar pull on the front
  bar.png          the lid / title bar with a piano hinge
  btn_*.png        black enamel push-buttons in a chrome bezel
  plate_h/v.png    aluminium label plates (text drawn by QML); nameplate.png
  key*.png         black bakelite keys for the Timeline drawer, lamp_*.png jewel lamps
  sheet*.png       inspection-sheet tiles, dymo.png tape tile, rule.png steel rule tile

Run: python3 bake.py   (needs numpy + Pillow)
Colour schemes: python3 bake.py --schemes [name ...]  -> art/schemes/<name>/ (enamel pieces only)
"""
import json
import os
import numpy as np
from PIL import Image

S = 2                                  # screen px per art px
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "art")


# ---- palette ---------------------------------------------------------------------------
def hexes(*hs):
    return np.array([[int(h[i:i + 2], 16) for i in (1, 3, 5)] for h in hs], np.uint8)

RED = hexes("#120304", "#2a0608", "#47090c", "#650d10", "#841414", "#a31c19",
            "#bf2a20", "#d6402b", "#e65f3e", "#f2875c", "#fbb68a")
CHROME = hexes("#0b0d11", "#1d2128", "#343a44", "#4f5763", "#6d7684", "#8f99a6",
               "#b3bcc7", "#d3dae2", "#eef2f6", "#ffffff")
ZINC = hexes("#16181b", "#25282c", "#363a3f", "#4a4f55", "#60666c", "#787e84", "#92989d")
RUBBER = hexes("#060607", "#0d0e10", "#16171a", "#202226", "#2c2f34", "#3b3f45", "#4e535a")
ALU = hexes("#3a3d41", "#5c6066", "#7f848a", "#a2a7ac", "#c2c6ca", "#dcdfe2", "#f0f2f4")
SHEET = hexes("#5d6655", "#8e9a83", "#b5c0a8", "#cdd6c0", "#dfe6d3", "#edf1e4")
GRID = np.array([150, 176, 156], np.uint8)
GRID_MAJOR = np.array([116, 150, 128], np.uint8)
TAPE = hexes("#050506", "#0e0f12", "#17191d", "#22252a")

BAYER = np.array([[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]) / 16.0 + 1 / 32

YELLOW = hexes("#5c4004", "#7d5806", "#9d7209", "#bc8c0d", "#d8a615", "#eec029", "#fbdc66")
WOOD = hexes("#5e3c1c", "#835829", "#a5753e", "#c09158", "#d6ae7c", "#e7c9a4")
GRAPHITE = hexes("#101116", "#1d1f27", "#2c2f38", "#3d414c")


# ---- colour schemes: the chest's enamel in other colours ---------------------------------
# name: (target colour, hue shift in degrees, ink colour for text) or None for the stock red.
# The targets are well-known brand colours (cola red, streaming green, auction-site purple...),
# each turned a few degrees off its brand so it reads as "that kind of colour" without being
# the brand. The RED ramp is re-tinted to the target's hue, saturation and brightness so the
# shading (sheen, orange-peel, dark outline) stays the same and only the colour changes.
SCHEMES = {
    "red":      None,
    "cola":     ("#F40009", +6),     # Coca-Cola
    "rosso":    ("#D40000", -8),     # Ferrari rosso corsa
    "cinema":   ("#E50914", +10),    # Netflix
    "hazard":   ("#F96302", -6),     # Home Depot
    "fanta":    ("#FF8300", +8),     # Fanta
    "fries":    ("#FFC72C", +6),     # McDonald's golden arches
    "lemon":    ("#FFDA1A", -6),     # IKEA yellow
    "lime":     ("#1DB954", +10),    # Spotify
    "deere":    ("#367C2B", +8),     # John Deere
    "pine":     ("#00704A", -8),     # Starbucks
    "robin":    ("#0ABAB5", +8),     # Tiffany blue
    "sky":      ("#1DA1F2", +8),     # Twitter
    "azure":    ("#1877F2", -8),     # Facebook
    "cobalt":   ("#0051BA", +6),     # IKEA blue
    "twitch":   ("#9146FF", -8),     # Twitch
    "grape":    ("#482683", +8),     # Cadbury
    "magenta":  ("#E20074", +6),     # T-Mobile
    "barbie":   ("#E0218A", -8),     # Barbie
    "graphite": ("#5a6068", 0),
    "black":    ("#2a2c30", 0),
    "cream":    ("#f3e6c4", 0),
}
RED_MID = 6                             # the RED ramp's "body" tone: the target colour lands on this step


def scheme_tint(name):
    """(hue, sat x, val x, ink) for a scheme: the RED ramp's body tone moved onto the target colour"""
    import colorsys
    if SCHEMES[name] is None:
        return None, 1.0, 1.0, "#b3261c"
    hexc, shift = SCHEMES[name]
    r, g, b = (int(hexc[i:i + 2], 16) / 255.0 for i in (1, 3, 5))
    th, ts, tv = colorsys.rgb_to_hsv(r, g, b)
    rh, rs, rv = colorsys.rgb_to_hsv(*(RED[RED_MID].astype(np.float32) / 255.0))
    hue = (th * 360.0 + shift) % 360.0
    ink = colorsys.hsv_to_rgb(hue / 360.0, min(1.0, ts * 1.05), tv * 0.72)      # text in the scheme's colour: darker
    return hue, ts / max(rs, 1e-3), tv / max(rv, 1e-3), "#%02x%02x%02x" % tuple(int(c * 255) for c in ink)


def tinted(ramp, hue, sat, val):
    """the same ramp in another colour: hue swapped, saturation and value scaled, lightness steps kept"""
    import colorsys
    out = []
    for r, g, b in ramp.astype(np.float32) / 255.0:
        h, s, v = colorsys.rgb_to_hsv(r, g, b)
        if hue is not None:
            h = (hue / 360.0) % 1.0
        rr, gg, bb = colorsys.hsv_to_rgb(h, min(1.0, s * sat), min(1.0, v * val))
        out.append([int(round(c * 255)) for c in (rr, gg, bb)])
    return np.array(out, np.uint8)


def bayer(h, w):
    yy, xx = np.mgrid[0:h, 0:w]
    return BAYER[yy % 4, xx % 4]


def dither(shade, ramp):
    """shade: float array in ramp-index units -> RGB via ordered dither between neighbours."""
    shade = np.asarray(shade, np.float32)
    if shade.ndim == 0:
        shade = shade.reshape(1, 1)
    h, w = shade.shape
    t = np.clip(shade, 0, len(ramp) - 1)
    lo = np.floor(t)
    idx = lo + ((t - lo) > bayer(h, w))
    return ramp[np.clip(idx, 0, len(ramp) - 1).astype(int)]


def px(b, ramp):
    return dither(np.array([[b]], np.float32), ramp)[0, 0]


def noise(h, w, cell, seed):
    """smooth value noise in [-1, 1], features about `cell` pixels wide"""
    rng = np.random.default_rng(seed)
    gh, gw = max(2, int(h / cell) + 2), max(2, int(w / cell) + 2)
    g = rng.random((gh, gw)).astype(np.float32)
    im = Image.fromarray((g * 255).astype(np.uint8)).resize(
        (int(gw * cell), int(gh * cell)), Image.BICUBIC)
    return np.asarray(im, np.float32)[:h, :w] / 127.5 - 1


def upscale(rgba):
    return np.repeat(np.repeat(rgba, S, 0), S, 1)


def save(name, rgba):
    os.makedirs(OUT, exist_ok=True)
    Image.fromarray(upscale(rgba), "RGBA").save(os.path.join(OUT, name))


def solid(rgb, m=None):
    h, w = rgb.shape[:2]
    a = np.full((h, w), 255, np.uint8) if m is None else np.where(m, 255, 0).astype(np.uint8)
    return np.dstack([rgb, a])


def checker_shadow(mask, dx, dy, alpha=150):
    """dithered (50% checker) drop shadow of `mask`, pushed by dx, dy"""
    h, w = mask.shape
    sh = np.zeros_like(mask)
    sh[dy:, dx:] = mask[:h - dy, :w - dx]
    yy, xx = np.mgrid[0:h, 0:w]
    sh &= ((yy + xx) % 2 == 0)
    out = np.zeros((h, w, 4), np.uint8)
    out[sh] = (4, 4, 6, alpha)
    return out


def over(dst, src):
    a = src[..., 3:4].astype(np.float32) / 255
    rgb = dst[..., :3] * (1 - a) + src[..., :3] * a
    al = np.maximum(dst[..., 3], src[..., 3])
    return np.dstack([rgb.astype(np.uint8), al.astype(np.uint8)])


def rounded(W, H, r):
    """mask of a W x H rectangle with corners rounded by r"""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32) + 0.5
    m = np.ones((H, W), bool)
    for cx, cy in ((r, r), (W - r, r), (r, H - r), (W - r, H - r)):
        q = ((xx < r) if cx == r else (xx > W - r)) & ((yy < r) if cy == r else (yy > H - r))
        m &= ~(q & (np.hypot(xx - cx, yy - cy) > r))
    return m


def edge_dist(m):
    """chebyshev distance (in px) from each inside pixel to the outside of mask m, capped at 32"""
    d = np.where(m, 32, 0).astype(np.int32)
    cur = m.copy()
    for i in range(1, 33):
        sh = cur.copy()
        sh[1:, :] &= cur[:-1, :]
        sh[:-1, :] &= cur[1:, :]
        sh[:, 1:] &= cur[:, :-1]
        sh[:, :-1] &= cur[:, 1:]
        sh[0, :] = sh[-1, :] = False
        sh[:, 0] = sh[:, -1] = False
        d[cur & ~sh] = np.minimum(d[cur & ~sh], i - 1)
        cur = sh
        if not cur.any():
            break
    return d


def enamel(H, W, seed, base=6.0):
    """glossy red enamel: a soft vertical sheen and a little orange-peel"""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    sh = base + noise(H, W, 40, seed) * 0.35 + noise(H, W, 3, seed + 1) * 0.18
    sh += 0.6 * np.exp(-((yy / max(H, 1) - 0.18) ** 2) / 0.01)   # a sheen band near the top
    return sh


def screw(r=2, cross=True):
    """a chrome pan-head screw: (rgb, mask)"""
    d = 2 * r + 1
    yy, xx = np.mgrid[0:d, 0:d] - r
    dist = np.hypot(xx, yy)
    m = dist <= r + 0.35
    sh = 6.0 - (xx + yy) / max(r, 1) * 1.6 - (dist / (r + .5)) ** 2 * 1.4
    sh[(xx < 0) & (yy < 0) & (dist < r * 0.7)] += 1.5
    sh[dist > r - 0.5] -= 2.4
    rgb = dither(sh, CHROME)
    if cross and r >= 2:
        rgb[r, 1:d - 1] = CHROME[1]
        rgb[1:d - 1, r] = CHROME[1]
    return rgb, m


def stamp(canvas_rgb, canvas_m, rgb, m, x, y):
    h, w = m.shape
    sub = canvas_rgb[y:y + h, x:x + w]
    sub[m[:sub.shape[0], :sub.shape[1]]] = rgb[:sub.shape[0], :sub.shape[1]][m[:sub.shape[0], :sub.shape[1]]]
    canvas_m[y:y + h, x:x + w] |= m[:sub.shape[0], :sub.shape[1]]


# ---- drawers ---------------------------------------------------------------------------
P = 12          # how far the bar pull sticks out past the front edge (art px)
SH = 3          # drop-shadow offset (art px)


LINING = "wood"   # default drawer interior: "wood" (oak), "walnut" (Pinoch cabinet wood) or "rubber"
PINOCH_WOOD = hexes("#160b06", "#28140b", "#3b1f11", "#512b16", "#6a391c", "#844823",
                    "#9d5a2c", "#b56f38", "#cb8848", "#dfa560", "#efc282", "#f8dca8")
LININGS = ("wood", "walnut", "rubber")


def _wood_interior(rgb, H, W, xx, yy, xc, yc, u, v, s, k, vx, vy, x0, y0, x1, y1,
                   floor, wall, inside, wl, wr, wt, wb, slide_h, seed, ramp=None):
    ramp = WOOD if ramp is None else ramp
    n = len(ramp) - 1
    sc = n / 5.0
    rng = np.random.default_rng(seed + 9)
    fx0, fy0 = vx + (x0 - vx) * k, vy + (y0 - vy) * k
    fx1, fy1 = vx + (x1 - vx) * k, vy + (y1 - vy) * k
    # -- floor: planks running along the slide axis, so the boards point at the pull
    along = xx if slide_h else yy
    across = (yy - fy0) if slide_h else (xx - fx0)
    PW = 13
    plank = np.floor(across / PW).astype(int)
    inplank = np.mod(across, PW)
    tone = (rng.random(64) - 0.5) * 1.3
    f = 2.2 + tone[np.clip(plank, 0, 63)]
    # grain: thin streaks along each board, wobbling slowly so they read as growth lines
    wob = np.sin(along * 0.045 + plank * 1.7) * 1.6 + np.sin(along * 0.013 + plank) * 2.5
    g = np.sin((inplank + wob) * 1.9 + plank * 2.3)
    f += np.where(g > 0.78, 1.2, np.where(g < -0.84, -1.0, 0.0))
    f += noise(H, W, 6, seed + 3) * 0.12
    # the seam between boards: a dark gap with a lit edge on the next board
    f = np.where(inplank < 1, f - 2.2, f)
    f = np.where((inplank >= 1) & (inplank < 2), f + 0.5, f)
    # butt joints where boards end, staggered per plank
    off = (rng.random(64) * 120).astype(int)
    joint = np.mod(along + off[np.clip(plank, 0, 63)], 150) < 1
    f = np.where(joint, f - 1.8, f)
    fu = np.clip((xc - fx0) / max(fx1 - fx0, 1), 0, 1)
    fv = np.clip((yc - fy0) / max(fy1 - fy0, 1), 0, 1)
    f += 0.5 * np.exp(-((fu - 0.3) ** 2 + (fv - 0.25) ** 2) / 0.25) - 0.2
    def ao(d, r):
        return np.clip(1 - d / r, 0, 1) ** 1.6
    f -= ao(xc - fx0, 11) * 2.2 + ao(yc - fy0, 11) * 2.0 + ao(fx1 - xc, 7) * 1.0 + ao(fy1 - yc, 7) * 0.9
    rgb[floor] = dither(np.clip(f * sc, 0, n), ramp)[floor]
    # -- walls: solid wood sides, grain running along each side, lit from the top-left
    depth = np.clip((s - k) / (1 - k), 0, 1)
    wsh = np.zeros((H, W), np.float32)
    for m, base, horiz_grain in ((wr, 3.9, False), (wb, 3.5, True), (wt, 1.7, True), (wl, 1.4, False)):
        a = xx if horiz_grain else yy
        d = depth * 9.0
        gg = np.sin((d + np.sin(a * 0.05 + seed) * 1.4) * 2.1)
        val = base - (1 - depth) ** 2 * 1.2 + np.where(gg > 0.85, 0.6, np.where(gg < -0.9, -0.5, 0.0))
        val = val + noise(H, W, 4, seed + 4) * 0.12
        wsh[m] = val[m]
    wsh[wall & (depth < 0.16)] -= 1.1                      # contact shadow where the side meets the floor
    wsh[wall & (depth > 0.86)] += 1.0                      # the sanded top edge of the side board catches light
    gap = np.abs(u - v) * np.minimum(x1 - x0, y1 - y0)
    wsh[wall & (gap < 0.9)] -= 1.5                         # the corner joint
    wsh[wall & (gap >= 0.9) & (gap < 1.9)] += 0.5
    rgb[wall] = dither(np.clip(wsh * sc, 0, n), ramp)[wall]
    rgb[inside & (s > k) & (s < k + 0.02)] = ramp[0]



def bake_drawer(name, W, H, front, vp, k=0.86, seed=1, lining=None):
    lining = LINING if lining is None else lining
    lip = {"l": 4, "r": 4, "t": 4, "b": 4}
    lip[front] = 11
    x0, y0, x1, y1 = lip["l"], lip["t"], W - lip["r"], H - lip["b"]
    vx = x0 + vp[0] * (x1 - x0)
    vy = y0 + vp[1] * (y1 - y0)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xc, yc = xx + 0.5, yy + 0.5
    inside = (xc > x0) & (xc < x1) & (yc > y0) & (yc < y1)
    u = np.where(xc >= vx, (xc - vx) / max(x1 - vx, 1e-3), (vx - xc) / max(vx - x0, 1e-3))
    v = np.where(yc >= vy, (yc - vy) / max(y1 - vy, 1e-3), (vy - yc) / max(vy - y0, 1e-3))
    s = np.maximum(u, v)
    floor = inside & (s <= k)
    wall = inside & ~floor
    horiz = u >= v
    wl = wall & horiz & (xc < vx)
    wr = wall & horiz & (xc >= vx)
    wt = wall & ~horiz & (yc < vy)
    wb = wall & ~horiz & (yc >= vy)
    slide_h = front in ("l", "r")
    rgb = np.zeros((H, W, 3), np.uint8)

    fx0, fy0 = vx + (x0 - vx) * k, vy + (y0 - vy) * k
    fx1, fy1 = vx + (x1 - vx) * k, vy + (y1 - vy) * k
    if lining in ("wood", "walnut"):
        _wood_interior(rgb, H, W, xx, yy, xc, yc, u, v, s, k, vx, vy, x0, y0, x1, y1,
                       floor, wall, inside, wl, wr, wt, wb, slide_h, seed,
                       PINOCH_WOOD if lining == "walnut" else WOOD)
        rng = np.random.default_rng(seed + 5)
    else:
        # -- floor: black ribbed rubber liner, ribs running across the slide axis
        fx0, fy0 = vx + (x0 - vx) * k, vy + (y0 - vy) * k
        fx1, fy1 = vx + (x1 - vx) * k, vy + (y1 - vy) * k
        across = (xx - fx0) if slide_h else (yy - fy0)
        rib = np.mod(across, 4)
        f = 3.1 + noise(H, W, 30, seed) * 0.08
        # each rib: a lit crown, a flank, a flat and a dark groove
        f = f + np.choose(rib.astype(int), [1.9, 0.8, 0.0, -1.3])
        # a soft pool of light from the top-left, falling off toward the far corner
        fu = np.clip((xc - fx0) / max(fx1 - fx0, 1), 0, 1)
        fv = np.clip((yc - fy0) / max(fy1 - fy0, 1), 0, 1)
        f += 0.45 * np.exp(-((fu - 0.3) ** 2 + (fv - 0.25) ** 2) / 0.25) - 0.2
        # ambient occlusion where the liner meets each wall: deepest under the lit (top/left) walls
        def ao(d, r):
            return np.clip(1 - d / r, 0, 1) ** 1.6
        f -= ao(xc - fx0, 11) * 2.6 + ao(yc - fy0, 11) * 2.4 + ao(fx1 - xc, 7) * 1.2 + ao(fy1 - yc, 7) * 1.1
        rgb[floor] = dither(f, RUBBER)[floor]
    
        # -- walls: brushed zinc-plated steel, lit from the top-left, grain running along each wall
        depth = np.clip((s - k) / (1 - k), 0, 1)             # 0 where the wall meets the floor, 1 at the lip
        rng = np.random.default_rng(seed + 5)
        grain_rows = (rng.random((H, 1)) - 0.5) * 0.7          # streaks along x (top and bottom walls)
        grain_cols = (rng.random((1, W)) - 0.5) * 0.7          # streaks along y (left and right walls)
        wsh = np.zeros((H, W), np.float32)
        for m, base, grain in ((wr, 5.4, grain_cols), (wb, 4.8, grain_rows), (wt, 2.3, grain_rows), (wl, 1.9, grain_cols)):
            val = base - (1 - depth) ** 2 * 1.7 + np.broadcast_to(grain, (H, W)) + noise(H, W, 2, seed + 4) * 0.15
            wsh[m] = val[m]
        wsh[wall & (depth < 0.16)] -= 1.3                      # contact shadow down where the wall meets the liner
        wsh[wall & (depth > 0.88)] += 1.4                      # the box's rolled top edge catches the light
        # slide rails pressed into the two walls that run along the slide axis: a lit ridge over a dark groove
        rail_walls = (wt | wb) if slide_h else (wl | wr)
        wsh[rail_walls & (np.abs(depth - 0.52) < 0.035)] += 2.2
        wsh[rail_walls & (np.abs(depth - 0.44) < 0.035)] -= 1.8
        # the corner seams: a dark fold with a thin highlight beside it
        gap = np.abs(u - v) * np.minimum(x1 - x0, y1 - y0)
        wsh[wall & (gap < 0.9)] -= 1.6
        wsh[wall & (gap >= 0.9) & (gap < 1.9)] += 0.8
        rgb[wall] = dither(wsh, ZINC)[wall]
        rgb[inside & (s > k) & (s < k + 0.02)] = ZINC[0]

    # -- the enamel lip around the top, and the drawer front
    rim = ~inside
    esh = enamel(H, W, seed + 7, 6.2)
    fb = {"l": xx, "r": W - 1 - xx, "t": yy, "b": H - 1 - yy}[front]
    front_board = fb < lip[front]
    # the front board catches more light toward its top edge, the side lips are a touch darker
    esh[front_board] += 0.5 - (yy[front_board] / H) * 0.8
    esh[rim & ~front_board] -= 0.4
    # rolled edge: highlight on the outer top/left, shade on the bottom/right
    esh[(xx == 1) | (yy == 1)] += 2.6
    esh[(xx == 2) | (yy == 2)] += 1.1
    esh[(xx == W - 2) | (yy == H - 2)] -= 2.6
    esh[(xx == W - 3) | (yy == H - 3)] -= 0.9
    # where the lip folds down into the box
    lipline = rim & ((np.abs(xc - x0) < 1) | (np.abs(xc - x1) < 1) | (np.abs(yc - y0) < 1) | (np.abs(yc - y1) < 1))
    lipline &= (xc > x0 - 1) & (xc < x1 + 1) & (yc > y0 - 1) & (yc < y1 + 1)
    esh[lipline] = 1.4
    # the shut-line between the front board and the box behind it
    esh[front_board & (fb == lip[front] - 1)] -= 1.8
    rgb[rim] = dither(esh, RED)[rim]
    # a chrome pinstripe inlaid across the front, like a real tool-chest drawer: one row of
    # metal over a dark groove, thin so the enamel still carries the colour
    along = yy if slide_h else xx
    span = (H if slide_h else W)
    run = (along >= 4) & (along < span - 4)                 # it stops short of the corners
    groove = front_board & run & (fb == 7)
    trim = front_board & run & (fb == 6)
    tsh = 5.8 + np.sin(along * 0.11) * 0.5 + (rng.random((H, W)) - 0.5) * 0.3
    rgb[groove] = dither(np.full((H, W), 1.2, np.float32), RED)[groove]
    rgb[trim] = dither(tsh, ALU)[trim]
    outline = (xx == 0) | (yy == 0) | (xx == W - 1) | (yy == H - 1)
    rgb[outline] = RED[0]

    # -- ball-bearing slides along the two sides parallel to the slide axis (on the lip)
    if slide_h:
        for ry in (1, H - 3):
            for x in range(3, W - 3):
                rgb[ry, x] = px(7.2 if ry == 1 else 5.0, CHROME)
                rgb[ry + 1, x] = px(4.0, CHROME)
                if x % 6 == 0:
                    rgb[ry, x] = CHROME[9]
    else:
        for rx in (1, W - 3):
            for y in range(3, H - 3):
                rgb[y, rx] = px(7.2 if rx == 1 else 5.0, CHROME)
                rgb[y, rx + 1] = px(4.0, CHROME)
                if y % 6 == 0:
                    rgb[y, rx] = CHROME[9]
    body = np.ones((H, W), bool)

    # -- canvas with room for the pull and the drop shadow
    pad = {"l": 0, "r": 0, "t": 0, "b": 0}
    pad[front] = P
    CW, CH = W + pad["l"] + pad["r"] + SH, H + pad["t"] + pad["b"] + SH
    crgb = np.zeros((CH, CW, 3), np.uint8)
    cm = np.zeros((CH, CW), bool)
    bx, by = pad["l"], pad["t"]
    crgb[by:by + H, bx:bx + W] = rgb
    cm[by:by + H, bx:bx + W] = body

    # screws on the drawer front, at each end of the bead
    sc, scm = screw(1, cross=False)
    span_len = (H if slide_h else W)
    for t_ in (6, span_len - 9):
        if slide_h:
            fxp = bx + (1 if front == "l" else W - 4)
            stamp(crgb, cm, sc, scm, fxp, by + t_)
        else:
            fyp = by + (1 if front == "t" else H - 4)
            stamp(crgb, cm, sc, scm, bx + t_, fyp)

    # -- full-width extruded chrome bar pull: two end brackets + a long D-section bar
    L0, L1 = int(span_len * 0.16), int(span_len * 0.84)

    def put(along, out, b):
        """along: position along the front edge; out: px outward from the front face (1..P)"""
        if front == "l":
            x, y = bx - out, by + along
        elif front == "r":
            x, y = bx + W - 1 + out, by + along
        elif front == "t":
            x, y = bx + along, by - out
        else:
            x, y = bx + along, by + H - 1 + out
        crgb[y, x] = CHROME[0] if b < 0 else px(b, CHROME)
        cm[y, x] = True

    for end in (L0, L1):                                 # brackets
        for o in range(1, P - 3):
            for t, b in ((-3, -1), (-2, 6.8), (-1, 5.6), (0, 4.4), (1, 3.4), (2, 2.4), (3, -1)):
                put(end + t, o, b)
        put(end - 1, 2, 9.0)                             # the foot's screw head: a lit dot over a dark one
        put(end, 2, 1.0)
        put(end, P - 7, 1.4)                             # where the bracket meets the bar: a little shadow
    prof = ((P - 6, -1), (P - 5, 8.8), (P - 4, 7.4), (P - 3, 5.6), (P - 2, 3.8), (P - 1, 2.2), (P, -1))
    for t_ in range(L0 - 5, L1 + 6):                    # the bar
        endcap = t_ in (L0 - 5, L1 + 5)
        for o, b in prof:
            bb = -1 if endcap else b
            if slide_h and front == "r" or front == "b":
                pass
            put(t_, o, bb)
        # a long specular streak
        if L0 + 4 < t_ < L1 - 4 and (t_ // 3) % 5 != 0:
            put(t_, P - 5, 9.0)

    out = np.zeros((CH, CW, 4), np.uint8)
    out = over(out, checker_shadow(cm, SH, SH))
    body_rgba = np.zeros((CH, CW, 4), np.uint8)
    body_rgba[cm] = solid(crgb)[cm]
    out = over(out, body_rgba)
    save(name, out)
    return {
        "w": CW * S, "h": CH * S,
        "bodyX": bx * S, "bodyY": by * S, "bodyW": W * S, "bodyH": H * S,
        "floorX": int(np.ceil(fx0)) * S, "floorY": int(np.ceil(fy0)) * S,
        "floorW": int(np.floor(fx1) - np.ceil(fx0)) * S, "floorH": int(np.floor(fy1) - np.ceil(fy0)) * S,
    }


# ---- the chest body --------------------------------------------------------------------
def bake_frame(RW, RH, T, seed=40):
    W, H = RW + 2 * T, RH + 2 * T
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    shape = rounded(W, H, 4)
    hole = (xx >= T) & (xx < W - T) & (yy >= T) & (yy < H - T)
    d_out = np.minimum(np.minimum(xx, W - 1 - xx), np.minimum(yy, H - 1 - yy))
    d_in = np.maximum(np.maximum(T - 1 - xx, xx - (W - T)), np.maximum(T - 1 - yy, yy - (H - T)))
    sh = enamel(H, W, seed, 6.0)
    # rolled outer edge
    sh[d_out == 1] += 2.6
    sh[d_out == 2] += 1.2
    sh[(d_out <= 2) & ((xx > W - 4) | (yy > H - 4))] -= 4.0
    # a pressed panel line part-way in
    sh[d_out == 6] -= 2.0
    sh[d_out == 7] += 1.2
    rgb = dither(sh, RED)
    # chrome bezel around the viewport, stepping down into it (lit top-left)
    bez = ~hole & (d_in >= 0) & (d_in < 5)
    toward_light = (yy < T + 0.5 * RH) & (xx < T + 0.5 * RW)
    top_or_left = (np.minimum(yy - (T - 5), xx - (T - 5)) < np.minimum((H - T + 4) - yy, (W - T + 4) - xx))
    csh = np.where(top_or_left, 3.0 + d_in * 0.4, 7.4 - d_in * 0.4) + noise(H, W, 2, seed + 3) * 0.3
    csh[d_in == 4] = np.where(top_or_left, 8.4, 3.0)[d_in == 4]   # outer chrome lip
    csh[d_in == 0] = 0.5                                           # rubber gasket at the glass
    rgb[bez] = dither(csh, CHROME)[bez]
    rgb[~hole & (d_in == 5)] = RED[1]
    # pressed louvres on the bottom rail either side of the label plate
    for side in (0, 1):
        for i in range(6):
            lx = (T + 14 + i * 9) if side == 0 else (W - T - 14 - 6 - i * 9)
            ly = H - T + 6
            for j in range(6):
                rgb[ly, lx + j] = RED[1]
                rgb[ly + 1, lx + j] = px(2.6, RED)
                rgb[ly + 2, lx + j] = px(8.4, RED)
    rgb[shape & (d_out == 0)] = RED[0]
    rgb[(xx == 0) | (yy == 0) | (xx == W - 1) | (yy == H - 1)] = RED[0]
    body = shape & ~hole
    # chrome screws in the corners and along the rails
    sc, scm = screw(2)
    pts = [(9, 9), (W - 14, 9), (9, H - 14), (W - 14, H - 14)]
    for i in range(1, 5):
        pts += [(int(W * i / 5) - 2, 9), (int(W * i / 5) - 2, H - 14)]
    for i in range(1, 3):
        if abs(int(H * i / 3) - H / 2) > 50:
            pts += [(9, int(H * i / 3) - 2), (W - 14, int(H * i / 3) - 2)]
    for (sx, sy) in pts:
        stamp(rgb, body, sc, scm, sx, sy)

    CW, CH = W + SH + 2, H + SH + 2
    out = np.zeros((CH, CW, 4), np.uint8)
    cm = np.zeros((CH, CW), bool)
    cm[0:H, 0:W] = shape
    out = over(out, checker_shadow(cm, SH + 1, SH + 1, 160))
    room_sh = np.zeros((CH, CW), bool)              # the bezel casts a shadow onto the image
    room_sh[T:T + 2, T:W - T] = True
    room_sh[T:H - T, T:T + 2] = True
    yy2, xx2 = np.mgrid[0:CH, 0:CW]
    room_sh &= ((yy2 + xx2) % 2 == 0)
    out[room_sh] = (0, 0, 0, 150)
    out[0:H, 0:W][body] = solid(rgb)[body]
    out[0:H, 0:W][hole & ~room_sh[0:H, 0:W]] = 0
    save("frame.png", out)
    return {"w": CW * S, "h": CH * S, "frameW": W * S, "frameH": H * S, "border": T * S}


# ---- the lid: title bar with a piano hinge ----------------------------------------------
def bake_bar(W, H, seed=60):
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    m = rounded(W, H, 3)
    sh = enamel(H, W, seed, 6.4)
    sh[yy == 1] += 3.0
    sh[yy == 2] += 1.6
    sh[yy == 3] += 0.6
    sh[(xx <= 2)] += 1.2
    sh[(xx >= W - 3)] -= 2.2
    sh[yy == H - 10] -= 2.4                    # the lid's bottom fold
    sh[yy == H - 11] += 1.0
    rgb = dither(sh, RED)
    # piano hinge: chrome knuckles along the bottom
    for y in range(H - 8, H - 2):
        for x in range(3, W - 3):
            knuckle = (x // 8) % 2
            t = (y - (H - 8)) / 5.0
            b = 7.6 - abs(t - 0.3) * 6.0 - knuckle * 0.8
            rgb[y, x] = px(b, CHROME)
            if x % 8 == 0:
                rgb[y, x] = CHROME[1]
    rgb[H - 2, 3:W - 3] = CHROME[1]
    rgb[m & ((xx == 0) | (yy == 0) | (xx == W - 1) | (yy == H - 1))] = RED[0]
    CW, CH = W + SH, H + SH
    out = np.zeros((CH, CW, 4), np.uint8)
    cm = np.zeros((CH, CW), bool)
    cm[:H, :W] = m
    out = over(out, checker_shadow(cm, SH, SH, 160))
    out[:H, :W][m] = solid(rgb)[m]
    save("bar.png", out)
    return {"w": CW * S, "h": CH * S, "barW": W * S, "barH": H * S, "faceY": 4 * S, "faceH": (H - 14) * S}


GLYPHS = {
    "close": ["X...X", ".X.X.", "..X..", ".X.X.", "X...X"],
    "min":   [".....", ".....", ".....", ".....", "XXXXX"],
    "shut":  ["XXXXX", "X...X", "XXXXX", "X...X", "XXXXX"],   # a stack of drawers: shut them all
}


def bake_button(name, glyph, pressed):
    """a black enamel push-button in a chrome bezel, 13 x 13 art px"""
    N = 13
    yy, xx = np.mgrid[0:N, 0:N].astype(np.float32)
    rgb = np.zeros((N, N, 3), np.uint8)
    bez = (xx < 2) | (yy < 2) | (xx > N - 3) | (yy > N - 3)
    bsh = np.where((xx < 2) | (yy < 2), 7.8, 3.6)
    bsh[(xx == 1) & (yy > N - 3)] = 5
    rgb[bez] = dither(bsh, CHROME)[bez]
    cap = ~bez
    if pressed:
        csh = np.full((N, N), 1.6) + (xx + yy) * 0.04
        csh[(xx == 2) | (yy == 2)] = 0.3
    else:
        csh = np.full((N, N), 2.6) - (xx + yy) * 0.04
        csh[(xx == 2) | (yy == 2)] = 5.4
        csh[(xx == N - 3) | (yy == N - 3)] = 0.6
    rgb[cap] = dither(csh, RUBBER)[cap]
    o = 4 + (1 if pressed else 0)
    for j, row in enumerate(GLYPHS[glyph]):
        for i, c in enumerate(row):
            if c == "X" and o + j < N - 2 and o + i < N - 2:
                rgb[o + j, o + i] = (226, 222, 210)
    rgb[(xx == 0) | (yy == 0) | (xx == N - 1) | (yy == N - 1)] = CHROME[0]
    m = np.ones((N, N), bool)
    for (x, y) in ((0, 0), (N - 1, 0), (0, N - 1), (N - 1, N - 1)):
        m[y, x] = False
    out = np.zeros((N, N, 4), np.uint8)
    out[m] = solid(rgb)[m]
    save(name, out)
    return {"w": N * S, "h": N * S}


# ---- aluminium label plates ------------------------------------------------------------
def bake_plate(name, W, H):
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    horiz = W >= H
    brushed = noise(H, W, 1.5, 9) * 0.25 if horiz else noise(H, W, 1.5, 9).T[:H, :W] * 0.25 if False else noise(H, W, 1.5, 9) * 0.25
    sh = 4.6 + brushed - (xx / W) * 0.4 - (yy / H) * 0.4
    sh[(xx == 1) | (yy == 1)] += 1.6
    sh[(xx == W - 2) | (yy == H - 2)] -= 1.8
    # a stamped (raised) border line
    ix0, iy0 = (6, 2) if horiz else (2, 6)
    ix1, iy1 = W - ix0, H - iy0
    border = ((xx == ix0 - 1) | (xx == ix1) ) & (yy >= iy0 - 1) & (yy <= iy1) | ((yy == iy0 - 1) | (yy == iy1)) & (xx >= ix0 - 1) & (xx <= ix1)
    sh[border] += 1.0
    rgb = dither(sh, ALU)
    rgb[(xx == 0) | (yy == 0) | (xx == W - 1) | (yy == H - 1)] = ALU[0]
    body = np.ones((H, W), bool)
    sc, scm = screw(1, cross=False)
    if horiz:
        stamp(rgb, body, sc, scm, 2, H // 2 - 1)
        stamp(rgb, body, sc, scm, W - 5, H // 2 - 1)
    else:
        stamp(rgb, body, sc, scm, W // 2 - 1, 2)
        stamp(rgb, body, sc, scm, W // 2 - 1, H - 5)
    m = np.ones((H, W), bool)
    for (x, y) in ((0, 0), (W - 1, 0), (0, H - 1), (W - 1, H - 1)):
        m[y, x] = False
    out = np.zeros((H, W, 4), np.uint8)
    out[m] = solid(rgb)[m]
    save(name, out)
    return {"w": W * S, "h": H * S}


# ---- timeline keys and lamps -----------------------------------------------------------
def bake_key(name, W, H, pressed=False):
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    rgb = np.zeros((H, W, 3), np.uint8)
    sh = np.full((H, W), 2.4 if not pressed else 1.6) + noise(H, W, 6, 3) * 0.2
    if not pressed:
        sh[(xx <= 1) | (yy <= 1)] += 2.6
        sh[(xx >= W - 2) | (yy >= H - 2)] -= 1.8
    else:
        sh[(xx <= 1) | (yy <= 1)] -= 1.0
    rgb[:] = dither(sh, RUBBER)
    rgb[(xx == 0) | (yy == 0) | (xx == W - 1) | (yy == H - 1)] = CHROME[2]
    m = np.ones((H, W), bool)
    for (x, y) in ((0, 0), (W - 1, 0), (0, H - 1), (W - 1, H - 1)):
        m[y, x] = False
    CW, CH = W + 2, H + 2
    out = np.zeros((CH, CW, 4), np.uint8)
    cm = np.zeros((CH, CW), bool)
    cm[:H, :W] = m
    if not pressed:
        out = over(out, checker_shadow(cm, 2, 2, 170))
    out[:H, :W][m] = solid(rgb)[m]
    save(name, out)
    return {"w": CW * S, "h": CH * S}


def bake_lamp(name, ramp_hexes):
    ramp = hexes(*ramp_hexes)
    r = 3
    d = 2 * r + 1
    yy, xx = np.mgrid[0:d, 0:d] - r
    dist = np.hypot(xx, yy)
    m = dist <= r + 0.3
    sh = 3.0 - (xx + yy) * 0.35 - (dist / (r + .5)) ** 2 * 1.0
    sh[(xx == -1) & (yy == -1)] = 6
    sh[(xx == -1) & (yy == -2)] = 5
    rgb = dither(sh, ramp)
    rgb[m & (dist > r - 0.7)] = CHROME[5]
    out = np.zeros((d, d, 4), np.uint8)
    out[m] = solid(rgb)[m]
    save(name, out)


# ---- tiles -----------------------------------------------------------------------------
def bake_tiles():
    # inspection sheet: pale green engineering paper, a 9-px grid with a heavier line every 3
    W, H = 27, 27
    yy, xx = np.mgrid[0:H, 0:W]
    rgb = dither(4.3 + noise(H, W, 5, 7) * 0.3, SHEET)
    g = (xx % 9 == 8) | (yy % 9 == 8)
    rgb[g] = GRID
    rgb[(xx == W - 1) | (yy == H - 1)] = GRID_MAJOR
    save("sheet.png", solid(rgb))
    save("sheet_plain.png", solid(dither(4.3 + noise(9, 27, 5, 8) * 0.3, SHEET)))
    # Dymo tape: black, faintly ribbed lengthwise, 12 px tall
    W, H = 16, 12
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    sh = 1.6 + noise(H, W, 4, 5) * 0.4
    sh[yy == 0] = 0; sh[yy == H - 1] = 0
    sh[yy == 1] += 1.2
    save("dymo.png", solid(dither(sh, TAPE)))
    # polished steel rule for the timeline track
    W, H = 32, 12
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    sh = 6.2 + noise(H, W, 1.5, 11) * 0.3 - yy * 0.18
    sh[yy == 0] = 8.6
    sh[yy == H - 1] = 2.0
    save("rule.png", solid(dither(sh, CHROME)))
    t = np.zeros((2, 2, 4), np.uint8)
    t[0, 0] = t[1, 1] = (4, 4, 6, 160)
    save("shadow.png", t)


def bake_nameplate(W, H):
    """a cast aluminium badge with a black recessed field, for the name on the lid"""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    m = rounded(W, H, 2)
    sh = 5.0 - (yy / H) * 1.4 + noise(H, W, 1.5, 4) * 0.2
    sh[(yy == 1)] += 1.4
    sh[(yy == H - 2)] -= 1.6
    rgb = dither(sh, ALU)
    field = (xx >= 3) & (xx < W - 3) & (yy >= 3) & (yy < H - 3)
    rgb[field] = dither(1.4 + noise(H, W, 3, 2) * 0.3, TAPE)[field]
    rgb[(yy == 3) & field] = TAPE[0]
    rgb[m & ((xx == 0) | (yy == 0) | (xx == W - 1) | (yy == H - 1))] = ALU[0]
    out = np.zeros((H, W, 4), np.uint8)
    out[m] = solid(rgb)[m]
    save("nameplate.png", out)
    return {"w": W * S, "h": H * S}


# ---- the drawing pencil: Geppetto's 2D tool ------------------------------------------------
def bake_pencil(name="pencil.png"):
    """A school pencil lying on its side, on a square canvas so QML can tumble it in place.

    32 art px long, a 6 px hexagonal barrel: red rubber, chrome ferrule, yellow barrel,
    sharpened cedar and a graphite point. A 1 px outline keeps it legible on the dark mat.
    """
    C = 44                      # canvas (art px), square: the pencil spins about its middle
    L = 40                      # pencil length
    T = 7                       # barrel thickness
    x0 = (C - L) // 2
    cy = C // 2
    rgb = np.zeros((C, C, 3), np.uint8)
    m = np.zeros((C, C), bool)

    def band(x, rows, ramp, shades):
        """a vertical band of `rows` at column x, shaded down the rows"""
        for i, r in enumerate(rows):
            rgb[r, x] = px(shades[min(i, len(shades) - 1)], ramp)
            m[r, x] = True

    # -- the barrel: a hexagonal pencil seen from the side, lit from the top
    barrel_shades = (5.6, 4.6, 3.6, 2.8, 2.0, 1.3, 0.8)
    for i in range(7, 33):
        band(x0 + i, range(cy - 3, cy + 4), YELLOW, barrel_shades)
    # the seam between the two visible facets
    rgb[cy, x0 + 7:x0 + 33] = px(1.6, YELLOW)

    # -- the sharpened cedar, tapering to the point
    for i, half in ((33, 3), (34, 3), (35, 2), (36, 2), (37, 1), (38, 1)):
        rows = range(cy - half, cy + half + 1) if half < 3 else range(cy - 3, cy + 4)
        band(x0 + i, rows, WOOD, (4.6, 3.6, 2.6, 1.8, 1.2, 0.8, 0.5))
    # -- the graphite point
    band(x0 + 39, range(cy - 1, cy + 2), GRAPHITE, (2.4, 1.4, 0.6))

    # -- the chrome ferrule, crimped
    for i in range(4, 7):
        band(x0 + i, range(cy - 3, cy + 4), CHROME, (7.2, 5.4, 3.4, 2.2, 1.2, 0.7, 0.5))
    # -- the rubber: the cabinet's red enamel, rounded off at the end
    for i in range(4):
        rows = range(cy - 1, cy + 2) if i == 0 else range(cy - 3, cy + 4)
        band(x0 + i, rows, RED, (6.4, 5.2, 4.2, 3.2, 2.2, 1.4, 0.9))

    # -- a 1 px outline so the pencil reads against the black mat
    grown = np.zeros_like(m)
    grown[1:, :] |= m[:-1, :]
    grown[:-1, :] |= m[1:, :]
    grown[:, 1:] |= m[:, :-1]
    grown[:, :-1] |= m[:, 1:]
    outline = grown & ~m
    rgb[outline] = (14, 13, 8)

    out = np.zeros((C, C, 4), np.uint8)
    out[m] = solid(rgb)[m]
    out[outline] = (14, 13, 8, 255)
    save(name, out)
    return {"w": C * S, "h": C * S, "len": L * S, "thick": T * S}


def bake_pencil_shadow(name="pencil_shadow.png"):
    """the dithered shadow the pencil drops on the mat: a 50% checker of near-black"""
    W, H = 14, 5
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32) + 0.5
    ellipse = ((xx - W / 2) / (W / 2 - 0.6)) ** 2 + ((yy - H / 2) / (H / 2 - 0.4)) ** 2 <= 1.0
    checker = ((yy + xx).astype(int) % 2) == 0
    out = np.zeros((H, W, 4), np.uint8)
    hit = ellipse & checker
    out[hit] = (4, 4, 6, 150)
    save(name, out)
    return {"w": W * S, "h": H * S}


def bake_tool_tray(name="tool_tray.png", W=114, H=60):
    """the tray the drawing pencil rolls on: a pressed steel recess with a rubber mat"""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    rgb = np.zeros((H, W, 3), np.uint8)
    outer = rounded(W, H, 3)

    # -- the lip: brushed zinc, lit from the top left
    lip = 4.8 + noise(H, W, 2, 21) * 0.3 - (yy / H) * 0.6
    lip[(yy == 0) | (xx == 0)] += 2.2
    lip[(yy == H - 1) | (xx == W - 1)] -= 2.2
    rgb[outer] = dither(lip, ZINC)[outer]

    # -- the mat: black ribbed rubber, recessed into the tray
    inner = (xx >= 5) & (xx < W - 5) & (yy >= 5) & (yy < H - 5)
    rib = np.mod(yy - 5, 4)
    f = 3.5 + noise(H, W, 30, 23) * 0.3
    f = np.where(rib == 0, f + 1.5, np.where(rib == 1, f + 0.6, np.where(rib == 3, f - 1.2, f)))
    f -= np.clip(1 - (yy - 5) / 8, 0, 1) * 1.8          # the far wall drops a shadow
    f -= np.clip(1 - (xx - 5) / 6, 0, 1) * 1.2
    rgb[inner] = dither(f, RUBBER)[inner]

    # -- the fold from the lip into the recess, and a chrome hairline along it
    fold = outer & ~inner & ((xx < 5) | (yy < 5))
    rgb[fold] = dither(lip - 1.6, ZINC)[fold]
    hair = inner & ((yy == 5) | (xx == 5))
    rgb[hair] = CHROME[6]
    rgb[(yy == 0) | (xx == 0) | (yy == H - 1) | (xx == W - 1)] = ZINC[0]

    out = np.zeros((H, W, 4), np.uint8)
    out[outer] = solid(rgb)[outer]
    out = over(out, checker_shadow(outer, 1, 1, 120))
    save(name, out)
    return {"w": W * S, "h": H * S}


# ---- the 3D pencil: rendered, not drawn ------------------------------------------------------
PENCIL_FRAMES = 24
KRAFT = hexes("#3e2a12", "#5f4220", "#80602f", "#a07e44", "#bd9c5e", "#d6b97e", "#ecd6a2")
ERASER = hexes("#2a0a0a", "#4d1414", "#78201e", "#a0302a", "#c4483c", "#de6e5c", "#f2a08c")
LACQUER = hexes("#2e1f02", "#5c4004", "#7d5806", "#9d7209", "#bc8c0d", "#d8a615", "#eec029", "#fbdc66", "#fff2b0")


def bake_pencil3d(name="pencil3d"):
    """A hexagonal pencil lying on the mat, ray-cast in 3D and rolled about its long axis.

    It lies along the screen's y axis, point up, and is lit from the top left like the rest of
    the chest. Each of the PENCIL_FRAMES frames rolls it 360/PENCIL_FRAMES degrees, so QML can
    roll it as it moves across its axis: the facets, the stamped band and the sharpened scallops
    turn with it. The silhouette and the drop shadow stay put, as a real pencil's would.
    """
    L = 58                                  # length (art px)
    RC = 4.6                                # hexagon circumradius
    A = RC * np.cos(np.pi / 6)              # its inradius
    half = 7
    W, H = 2 * half + 1, L
    CW, CH = W + 3, H + 3                   # room for the outline and the shadow
    s = (L - 0.5 - np.arange(H, dtype=np.float32))[:, None] * np.ones((1, W), np.float32)
    u = (np.arange(W, dtype=np.float32) - half)[None, :] * np.ones((H, 1), np.float32)
    # sections, from the eraser end (s = 0) to the point (s = L)
    E0, F0, B0, C0, G0 = 0, 6, 12, 45, 54   # eraser, ferrule, barrel, cone, graphite
    r = np.full((H, W), 99.0, np.float32)
    r = np.where(s < F0, RC * 0.92, r)
    cap = s < 1.6
    r = np.where(cap, RC * 0.92 * np.sqrt(np.clip(1 - ((1.6 - s) / 2.0) ** 2, 0.05, 1)), r)
    r = np.where((s >= F0) & (s < B0), RC * 1.03, r)
    cone = s >= C0
    rc = RC * 1.02 - (s - C0) / (L - C0) * (RC * 1.02 - 0.35)
    r = np.where(cone, rc, r)
    slope = (RC * 1.02 - 0.35) / (L - C0)  # how fast the cone narrows: tilts its normals forward
    hexed = (s >= B0)                        # barrel and cone are clipped by the hexagon
    L3 = np.array([-0.75, -0.35, 0.55]); L3 /= np.linalg.norm(L3)
    Hv = L3 + np.array([0, 0, 1.0]); Hv /= np.linalg.norm(Hv)
    meta = {}
    for f in range(PENCIL_FRAMES):
        th = f / PENCIL_FRAMES * 2 * np.pi
        # ---- the circle: the top of a round section at u
        inc = np.abs(u) < r
        zc = np.sqrt(np.clip(r * r - u * u, 0, None))
        # ---- the hexagon: six half-planes in the (x, z) cross-section
        ztop = np.full((H, W), 99.0, np.float32)
        zlow = np.full((H, W), -99.0, np.float32)
        face = np.full((H, W), -1, np.int32)
        inh = np.ones((H, W), bool)
        for i in range(6):
            a = th + i * np.pi / 3
            nx, nz = np.cos(a), np.sin(a)
            if nz > 1e-4:
                z = (A - u * nx) / nz
                face = np.where(z < ztop, i, face)
                ztop = np.minimum(ztop, z)
            elif nz < -1e-4:
                zlow = np.maximum(zlow, (A - u * nx) / nz)
            else:
                inh &= u * nx <= A
        inh &= ztop >= zlow
        # ---- which surface the eye meets
        use_hex = hexed & inh & (ztop <= zc)
        m = np.where(hexed, inh & inc & (np.minimum(ztop, zc) >= zlow), inc)
        z = np.where(use_hex, ztop, zc)
        fa = th + face * np.pi / 3
        nxs = np.where(use_hex, np.cos(fa), u / np.maximum(r, 1e-3))
        nzs = np.where(use_hex, np.sin(fa), zc / np.maximum(r, 1e-3))
        nys = np.where(cone & ~use_hex, -slope, 0.0)               # the cone leans toward the point
        nys = np.where(cap & ~hexed, (1.6 - s) / 2.0 * -1.0, nys)  # the eraser end rounds off
        n = np.stack([nxs, nys, nzs], -1)
        n /= np.linalg.norm(n, axis=-1, keepdims=True) + 1e-6
        dif = np.clip(n @ L3, 0, 1)
        spec = np.clip(n @ Hv, 0, 1)
        rgb = np.zeros((H, W, 3), np.uint8)

        def paint(sel, ramp, base, gain, shine, power):
            sh = base + dif * gain + shine * spec ** power
            rgb[sel] = dither(sh, ramp)[sel]

        lead = cone & (r < 1.15)
        wood = cone & ~use_hex & ~lead
        lac = hexed & use_hex
        ferrule = (s >= F0) & (s < B0)
        eraser = s < F0
        paint(lac, LACQUER, 0.9, 5.6, 2.2, 40)
        paint(wood, WOOD, 0.2, 4.4, 0.4, 8)
        paint(lead, GRAPHITE, 0.0, 2.6, 2.0, 16)
        paint(ferrule, CHROME, 0.6, 5.4, 4.0, 30)
        paint(eraser, ERASER, 0.6, 4.6, 0.8, 10)
        # the ferrule's crimped rings
        for ring in (F0 + 1, B0 - 2):
            sel = ferrule & (np.floor(s) == ring)
            rgb[sel] = dither(0.4 + dif * 3.0, CHROME)[sel]
        # the facet edges catch a hairline of light; the seam on the far side stays dark
        # where two facets meet, a hairline: darker on the shadow side
        seam = lac & np.zeros((H, W), bool)
        seam[:, 1:] |= lac[:, 1:] & lac[:, :-1] & (face[:, 1:] != face[:, :-1])
        rgb[seam] = dither(1.2 + dif * 2.6, LACQUER)[seam]
        # the stamped band on one facet: gold letters on a black stripe, turning with it
        band = lac & (face == 0) & (s > 22) & (s < 40)
        if band.any():
            rgb[band] = dither(0.8 + dif * 1.6, TAPE)[band]
            si = np.floor(s).astype(int)
            gold = band & (si % 3 == 1) & (si != 31)
            rgb[gold] = dither(4.0 + dif * 2.4, YELLOW)[gold]
        # ---- outline and shadow, on a canvas with room for both
        out = np.zeros((CH, CW, 4), np.uint8)
        cm = np.zeros((CH, CW), bool)
        cm[1:H + 1, 1:W + 1] = m
        out = over(out, checker_shadow(cm, 2, 2, 165))
        grown = cm.copy()
        grown[1:, :] |= cm[:-1, :]; grown[:-1, :] |= cm[1:, :]
        grown[:, 1:] |= cm[:, :-1]; grown[:, :-1] |= cm[:, 1:]
        out[grown & ~cm] = (14, 13, 8, 255)
        body = np.zeros((CH, CW, 3), np.uint8)
        body[1:H + 1, 1:W + 1] = rgb
        out[cm] = np.dstack([body, np.full((CH, CW), 255, np.uint8)])[cm]
        save("%s_%02d.png" % (name, f), out)
        if f == 0:
            cols = np.where(m.any(0))[0]
            meta = {"w": CW * S, "h": CH * S, "bodyW": int(cols[-1] - cols[0] + 1) * S,
                    "bodyH": H * S, "frames": PENCIL_FRAMES, "radius": round(float(A * S), 2)}
    return meta


# ---- models from Effigy: rendered there, repainted here ----------------------------------
# model/<name>/build.txt is the model (run through geppetto-live); model/render_frames.py renders
# its exported mesh top down in flat ID colours. Here each frame is shrunk to art pixels and every
# part is repainted from the rendered light in a chest ramp with the same Bayer dither as the rest.
MODEL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "model")
PARCH = hexes("#4a3a1c", "#6e5a30", "#93804f", "#b4a26e", "#cfbf8c", "#e3d6a8", "#f2e9c8", "#fbf6e2")
WALNUT = hexes("#1e1006", "#33200d", "#4d3216", "#6a4720", "#8a5f2d", "#a87a40", "#c79a5c")
INK = hexes("#0c0a10", "#1d1a2a", "#2e2a3e", "#45405a")
BRASS = hexes("#2a1a06", "#4a3210", "#6e4c16", "#94681e", "#b8892e", "#d6ad4e", "#f0d68a", "#fff4c8")
WAX = hexes("#1a0204", "#33060a", "#520b10", "#741218", "#961d22", "#b8383a")
# ID class -> (ramp, base, gain): flat light v in 0..1 lands at base + v * gain on the ramp
SCROLL_PARTS = {1: (PARCH, 1.4, 6.0), 2: (WALNUT, 0.6, 6.0), 3: (RED, 2.0, 7.6), 4: (PARCH, 0.6, 4.2),
                5: (INK, 0.2, 2.4), 6: (BRASS, 1.0, 6.9), 7: (WAX, 0.4, 5.4)}
# ID class -> the hue it is rendered in (render_frames.py IDS): paper, wood, ribbon, aged paper, ink, brass, wax
ID_HUES = {1: 120, 2: 240, 3: 0, 4: 180, 5: 60, 6: 300, 7: 30}
# thin details win a shrunk pixel with less cover, so a line of ink or a brass ring survives
ID_WEIGHT = {5: 2.2, 6: 1.6, 7: 1.6, 3: 1.2}


def id_classes(rgb):
    """flat ID render -> class per pixel (0 backdrop, else the nearest ID hue) and the light on it
    (its brightest channel)"""
    f = rgb.astype(np.float32) / 255
    r, g, b = f[..., 0], f[..., 1], f[..., 2]
    v = f.max(-1)
    sat = v - f.min(-1)
    hue = np.degrees(np.arctan2(np.sqrt(3) * (g - b), 2 * r - g - b)) % 360
    k = np.zeros(r.shape, np.int32)
    best = np.full(r.shape, 999.0)
    for c, h in ID_HUES.items():
        d = np.abs((hue - h + 180) % 360 - 180)
        win = d < best
        best = np.where(win, d, best)
        k = np.where(win, c, k)
    k[(sat < 0.12) | (sat < v * 0.35)] = 0
    return k, v


def shrink_ids(k, v, tw, th):
    """area-average each class to tw x th: the class with most cover wins, with its mean light"""
    best = np.zeros((th, tw), np.float32)
    cls = np.zeros((th, tw), np.int32)
    lit = np.zeros((th, tw), np.float32)
    cover = np.zeros((th, tw), np.float32)
    for c in ID_HUES:
        w = (k == c).astype(np.float32)
        ws = np.asarray(Image.fromarray(w, "F").resize((tw, th), Image.BOX))
        vs = np.asarray(Image.fromarray(w * v, "F").resize((tw, th), Image.BOX))
        cover += ws
        win = ws * ID_WEIGHT.get(c, 1.0) > best
        ws = ws * ID_WEIGHT.get(c, 1.0)
        best = np.where(win, ws, best)
        cls = np.where(win, c, cls)
        lit = np.where(win, vs * ID_WEIGHT.get(c, 1.0) / np.maximum(ws, 1e-6), lit)
    cls[cover < 0.5] = 0
    return cls, lit


def light_range(ids):
    """per class, the light from its darkest to its brightest (2nd..99.5th percentile, all frames):
    the render lights a part from above, so its own range is narrow; stretch it to the whole ramp"""
    rng = {}
    for c in ID_HUES:
        vs = np.concatenate([v[k == c] for k, v in ids])
        if len(vs):
            lo, hi = np.percentile(vs, [2, 99.5])
            rng[c] = (float(lo), float(max(hi, lo + 1e-3)))
    return rng


def paint_ids(cls, lit, parts, rng):
    rgb = np.zeros(cls.shape + (3,), np.uint8)
    for c, (ramp, base, gain) in parts.items():
        sel = cls == c
        if sel.any():
            lo, hi = rng.get(c, (0.0, 1.0))
            t = np.clip((lit - lo) / (hi - lo), 0, 1)
            rgb[sel] = dither(base + t * gain, ramp)[sel]
    return rgb


def outlined(rgb, m, shadow=True):
    """a 1 px dark ring and the chest's checker drop shadow, on a canvas with room for both"""
    H, W = m.shape
    CH, CW = H + 4, W + 4
    out = np.zeros((CH, CW, 4), np.uint8)
    cm = np.zeros((CH, CW), bool)
    cm[1:H + 1, 1:W + 1] = m
    if shadow:
        out = over(out, checker_shadow(cm, 2, 2, 165))
    grown = cm.copy()
    grown[1:, :] |= cm[:-1, :]; grown[:-1, :] |= cm[1:, :]
    grown[:, 1:] |= cm[:, :-1]; grown[:, :-1] |= cm[:, 1:]
    out[grown & ~cm] = (14, 10, 6, 255)
    body = np.zeros((CH, CW, 3), np.uint8)
    body[1:H + 1, 1:W + 1] = rgb
    out[cm] = np.dstack([body, np.full((CH, CW), 255, np.uint8)])[cm]
    return out


def model_frames(name, part):
    d = os.path.join(MODEL, name, "frames", part)
    fs = sorted(f for f in os.listdir(d) if f.endswith(".png")) if os.path.isdir(d) else []
    # the long axis (Y) renders up the frame: it lies along the drawer's depth, like the pencil
    return [np.asarray(Image.open(os.path.join(d, f)).convert("RGB")) for f in fs]


def bake_scroll3d(name="scroll3d", L=84):
    """The chat scroll (model/scroll), rolling about its long axis, L art px long (upright)."""
    frames = model_frames("scroll", "roll")
    if not frames:
        return None
    ids = [id_classes(f) for f in frames]
    rng = light_range(ids)
    on = np.zeros(ids[0][0].shape, bool)
    for k, _ in ids:
        on |= k > 0
    ys, xs = np.where(on)
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    H = L
    W = max(1, int(round((x1 - x0) * L / (y1 - y0))))
    meta = None
    for i, (k, v) in enumerate(ids):
        cls, lit = shrink_ids(k[y0:y1, x0:x1], v[y0:y1, x0:x1], W, H)
        save("%s_%02d.png" % (name, i), outlined(paint_ids(cls, lit, SCROLL_PARTS, rng), cls > 0))
        if meta is None:
            cols = np.where((cls == 1).sum(0) > H * 0.3)[0]      # the paper roll's own width
            meta = {"w": (W + 4) * S, "h": (H + 4) * S, "bodyW": W * S, "bodyH": H * S,
                    "frames": len(frames), "radius": round(float((cols[-1] - cols[0] + 1) / 2 * S), 2)}
    return meta


# the chat note: a sheet of paper (model/paperball), slot 0 rendered green
PAPER_PARTS = {1: (PARCH, 1.2, 6.6)}


def bake_paperball(name="paperball", sheetH=180, ballL=34):
    """The chat as a crumpled sheet of paper (model/paperball, poses unfold_NN from its build).
    <name>.png: the ball lying in the drawer, ballL art px across. <name>_unfold_NN.png: the ball
    opening out flat, every frame on one canvas at one scale, the flat sheet sheetH art px tall."""
    frames = model_frames("paperball", "unfold")
    if not frames:
        return None
    ids = [id_classes(f) for f in frames]
    rng = light_range(ids)
    on = np.zeros(ids[0][0].shape, bool)
    for k, _ in ids:
        on |= k > 0
    ys, xs = np.where(on)
    cy, cx = (ys.min() + ys.max() + 1) / 2, (xs.min() + xs.max() + 1) / 2
    fy, fx = np.where(ids[-1][0] > 0)                       # the flat sheet sets the scale
    sc = sheetH / (fy.max() + 1 - fy.min())
    hh, hw = max(cy - ys.min(), ys.max() + 1 - cy), max(cx - xs.min(), xs.max() + 1 - cx)
    H, W = int(np.ceil(2 * hh * sc / 2)) * 2, int(np.ceil(2 * hw * sc / 2)) * 2
    box = (int(round(cy - H / sc / 2)), int(round(cy + H / sc / 2)), int(round(cx - W / sc / 2)), int(round(cx + W / sc / 2)))
    meta = {"w": (W + 4) * S, "h": (H + 4) * S, "frames": len(frames)}
    for i, (k, v) in enumerate(ids):
        cls, lit = shrink_ids(k[box[0]:box[1], box[2]:box[3]], v[box[0]:box[1], box[2]:box[3]], W, H)
        save("%s_unfold_%02d.png" % (name, i), outlined(paint_ids(cls, lit, PAPER_PARTS, rng), cls > 0))
        if i == 0:                                          # how big the ball is on the canvas
            meta["firstW"] = int((cls > 0).any(0).sum()) * S
        if i == len(ids) - 1:                               # where the flat sheet lies on the canvas
            ry, rx = np.where(cls > 0)
            meta.update(sheetX=int(rx.min() + 1) * S, sheetY=int(ry.min() + 1) * S,
                        sheetW=int(rx.max() + 1 - rx.min()) * S, sheetH=int(ry.max() + 1 - ry.min()) * S)
    # the ball in the drawer: the first pose on its own, cropped tight
    k, v = ids[0]
    ys, xs = np.where(k > 0)
    k, v = k[ys.min():ys.max() + 1, xs.min():xs.max() + 1], v[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    bw = max(1, int(round(k.shape[1] * ballL / k.shape[0])))
    cls, lit = shrink_ids(k, v, bw, ballL)
    save(name + ".png", outlined(paint_ids(cls, lit, PAPER_PARTS, rng), cls > 0))
    meta["ball"] = {"w": (bw + 4) * S, "h": (ballL + 4) * S, "bodyW": bw * S, "bodyH": ballL * S,
                    "radius": round(min(bw, ballL) / 2 * S, 2)}
    return meta


# the MODELING folder (model/modelfolder): kraft leaves, the fold, the label and its ink line; the
# cover is slots 5/6 (classes 6, 7) so it bakes as its own layer over whatever is filed in it
MANILA = hexes("#3e2a12", "#5f4220", "#80602f", "#a07e44", "#bd9c5e", "#d6b97e", "#ecd6a2", "#f7ead0")
MFOLDER_PARTS = {1: (MANILA, 1.6, 5.4), 2: (MANILA, 0.6, 2.6), 4: (PARCH, 3.0, 4.6), 5: (INK, 0.2, 1.6),
                 6: (MANILA, 1.6, 5.4), 7: (MANILA, 0.6, 2.6)}


def bake_modelfolder(name="modelfolder", W=100):
    """The MODELING folder lying shut in the tools drawer (model/modelfolder), W art px wide, in two
    layers: <name>_back.png the back leaf with its tab, <name>_cover.png the front cover, so a finished
    model's card can slide down between them and stay filed there, peeking out over the cover."""
    frames = model_frames(name, "open")
    if not frames:
        return None
    k, v = id_classes(frames[0])
    rng = light_range([(k, v)])
    ys, xs = np.where(k > 0)
    k, v = k[ys.min():ys.max() + 1, xs.min():xs.max() + 1], v[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    H = max(1, int(round(k.shape[0] * W / k.shape[1])))
    cls, lit = shrink_ids(k, v, W, H)
    cover = (cls == 6) | (cls == 7)
    back = (cls > 0) & ~cover
    save(name + "_back.png", outlined(paint_ids(np.where(back, cls, 0), lit, MFOLDER_PARTS, rng), back))
    save(name + "_cover.png", outlined(paint_ids(np.where(cover, cls, 0), lit, MFOLDER_PARTS, rng), cover, shadow=False))
    ry, rx = np.where(cover)                        # the cover: cards tuck in behind its top edge
    return {"w": (W + 4) * S, "h": (H + 4) * S, "bodyW": W * S, "bodyH": H * S,
            "pocketX": int(rx.min() + 1) * S, "pocketY": int(ry.min() + 1) * S,
            "pocketW": int(rx.max() + 1 - rx.min()) * S, "pocketH": int(ry.max() + 1 - ry.min()) * S}


STEEL = hexes("#14161a", "#262a30", "#3a4048", "#535a64", "#6f7782", "#8e96a0", "#b3bac2", "#dde2e7")
# the live build stopwatch: enamel dial, red sweep hand, ink track, brass case (slots 0, 2, 4, 5)
WATCH_PARTS = {1: (PARCH, 3.0, 4.8), 3: (RED, 2.0, 7.6), 5: (INK, 0.2, 2.4), 6: (BRASS, 1.0, 6.9)}
# the modeling chisel: walnut handle, steel blade, ink stamp, brass ferrule (slots 1, 3, 4, 5)
CHISEL_PARTS = {2: (WALNUT, 0.6, 6.0), 4: (STEEL, 1.0, 6.9), 5: (INK, 0.2, 2.4), 6: (BRASS, 1.0, 6.9)}


def bake_stopwatch(name="stopwatch.png", L=30):
    """The live build stopwatch (model/stopwatch) seen from above, crown up, L art px tall."""
    frames = model_frames("stopwatch", "face")
    if not frames:
        return None
    k, v = id_classes(frames[0])
    ys, xs = np.where(k > 0)
    k, v = k[ys.min():ys.max() + 1, xs.min():xs.max() + 1], v[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    W = max(1, int(round(k.shape[1] * L / k.shape[0])))
    cls, lit = shrink_ids(k, v, W, L)
    save(name, outlined(paint_ids(cls, lit, WATCH_PARTS, light_range([(k, v)])), cls > 0))
    return {"w": (W + 4) * S, "h": (L + 4) * S, "bodyW": W * S, "bodyH": L * S}


# the rolled drawing: green engineering paper, its grid, red twine, blue ink (slots 0, 1, 2, 4)
GRIDPAPER = hexes("#2e3a2a", "#4a5a44", "#6c7d63", "#90a286", "#b4c4a8", "#d2dec4", "#e9f0de", "#f7fbf0")
GRIDLINE = hexes("#3a5a46", "#56806a", "#79a48c", "#9bc2a8")
DRAWROLL_PARTS = {1: (GRIDPAPER, 2.0, 5.8), 2: (GRIDLINE, 0.6, 3.0), 3: (RED, 2.0, 7.6), 5: (INK, 0.4, 2.6)}


def bake_chisel3d(name="chisel3d", L=60, model="chisel", parts=None, widthCls=2):
    """A prop that rolls about its long axis (model/<model>), L art px long (upright): the modeling
    chisel by default, and the rolled drawing."""
    parts = parts or CHISEL_PARTS
    frames = model_frames(model, "roll")
    if not frames:
        return None
    ids = [id_classes(f) for f in frames]
    rng = light_range(ids)
    on = np.zeros(ids[0][0].shape, bool)
    for k, _ in ids:
        on |= k > 0
    ys, xs = np.where(on)
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    H = L
    W = max(1, int(round((x1 - x0) * L / (y1 - y0))))
    meta = None
    for i, (k, v) in enumerate(ids):
        cls, lit = shrink_ids(k[y0:y1, x0:x1], v[y0:y1, x0:x1], W, H)
        save("%s_%02d.png" % (name, i), outlined(paint_ids(cls, lit, parts, rng), cls > 0))
        if meta is None:
            cols = np.where((cls == widthCls).sum(0) > 0)[0]    # the round part's width
            meta = {"w": (W + 4) * S, "h": (H + 4) * S, "bodyW": W * S, "bodyH": H * S,
                    "frames": len(frames), "radius": round(float((cols[-1] - cols[0] + 1) / 2 * S), 2) if len(cols) else 6}
    return meta


# the drawing pencil (model/pencil): lacquer, sharpened wood, eraser, ferrule, graphite, gold stamp
PENCIL_PARTS = {1: (LACQUER, 1.2, 7.4), 2: (WOOD, 0.4, 5.2), 3: (ERASER, 1.0, 5.8), 4: (STEEL, 1.4, 6.4),
                5: (GRAPHITE, 0.2, 3.4), 6: (BRASS, 1.6, 6.0)}


def bake_turning(name, model, L, parts, widthCls):
    """A rod-like prop (model/<model>) that lies any way in a drawer and rolls across itself: every
    roll frame at every heading (render_frames.py turn=), so the light stays top left however it
    lies. L art px long. Frames are <name>_HH_RR.png on one square canvas, the prop's middle at its
    centre; QML picks HH from its heading and RR from how far it has rolled."""
    d = os.path.join(MODEL, model, "frames", "turn")
    names = sorted(f for f in os.listdir(d) if f.endswith(".png")) if os.path.isdir(d) else []
    if not names:
        return None
    turns = len({f[:2] for f in names})
    rolls = len(names) // turns
    ids = {f[:5]: id_classes(np.asarray(Image.open(os.path.join(d, f)).convert("RGB"))) for f in names}
    rng = light_range(list(ids.values()))
    # its length at heading 0 (point up) sets the scale for every heading
    on = np.zeros(ids["00_00"][0].shape, bool)
    for r in range(rolls):
        on |= ids["00_%02d" % r][0] > 0
    ys, xs = np.where(on)
    span = ys.max() + 1 - ys.min()
    N = L + 2 + (L % 2)                         # square canvas: the prop turns inside it
    side = span * N / L
    cy, cx = on.shape[0] / 2, on.shape[1] / 2
    box = (int(round(cy - side / 2)), int(round(cy + side / 2)), int(round(cx - side / 2)), int(round(cx + side / 2)))
    meta = None
    for tag, (k, v) in ids.items():
        kk, vv = k[box[0]:box[1], box[2]:box[3]], v[box[0]:box[1], box[2]:box[3]]
        cls, lit = shrink_ids(kk, vv, N, N)
        save("%s_%s.png" % (name, tag), outlined(paint_ids(cls, lit, parts, rng), cls > 0))
        if tag == "00_00":
            cols = np.where((cls == widthCls).sum(0) > 0)[0]     # how thick its round part is
            rad = (cols[-1] - cols[0] + 1) / 2 if len(cols) else 3
            meta = {"w": (N + 4) * S, "h": (N + 4) * S, "bodyW": N * S, "bodyH": N * S, "frames": rolls,
                    "turns": turns, "radius": round(float(rad * S), 2), "halfLen": round(float((L / 2 - rad) * S), 2)}
    return meta


def bake_scroll_rod(name="scroll_rod.png", L=272):
    """The scroll's turned rod alone, upright, L art px long: the two sides of the open scroll."""
    frames = model_frames("scroll", "rod")
    if not frames:
        return None
    k, v = id_classes(frames[0])
    ys, xs = np.where(k > 0)
    k, v = k[ys.min():ys.max() + 1, xs.min():xs.max() + 1], v[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    W = max(1, int(round(k.shape[1] * L / k.shape[0])))
    cls, lit = shrink_ids(k, v, W, L)
    save(name, outlined(paint_ids(cls, lit, SCROLL_PARTS, light_range([(k, v)])), cls > 0))
    # where the plain shaft runs between the collars, so QML can hang the paper on it
    rows = np.where((cls > 0).sum(1) <= max(2, W * 0.6))[0]
    mid = rows[(rows > L * 0.1) & (rows < L * 0.9)]
    return {"w": (W + 4) * S, "h": (L + 4) * S, "shaftY": int(mid.min() + 1) * S if len(mid) else 0,
            "shaftH": int(mid.max() - mid.min() + 1) * S if len(mid) else L * S}


def bake_parchment(name="parchment.png", W=64, H=64):
    """a tile of the scroll's paper: its colour with a faint fibre grain, to tile under the chat"""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    sh = 5.2 + noise(H, W, 9, 91) * 0.35 + noise(H, W, 3, 92) * 0.18
    sh += np.sin(yy * 0.9 + noise(H, W, 6, 93) * 3) * 0.08          # long fibres across the sheet
    save(name, solid(dither(sh, PARCH)))
    return {"w": W * S, "h": H * S}


def bake_folder(name="folder.png", W=52, H=38):
    """a kraft file folder with a sheet sticking out of it: the button for the drawings folder"""
    out = np.zeros((H + 3, W + 3, 4), np.uint8)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    TAB_W, TAB_H = 18, 5
    # the back leaf, with its tab
    back = rounded(W, H, 2) & ((yy >= TAB_H) | ((xx >= 3) & (xx < 3 + TAB_W)))
    back &= ~((yy < 2) & ((xx < 4) | (xx >= 2 + TAB_W)))
    # the drawing sticking out, tilted a little: green engineering paper
    sheet = (yy >= 3) & (yy < H - 6) & (xx >= 8 + (yy - 3) * 0.12) & (xx < W - 5 + (yy - 3) * 0.12)
    # the front leaf, cut lower
    front = rounded(W, H, 2) & (yy >= 11)
    m = back | sheet | front
    rgb = np.zeros((H, W, 3), np.uint8)
    rgb[back] = dither(2.6 - yy * 0.02 + noise(H, W, 4, 71) * 0.25, KRAFT)[back]
    sp = dither(4.2 + noise(H, W, 5, 72) * 0.3 - (yy - 3) * 0.02, SHEET)
    sp[((xx.astype(int) - 8) % 5 == 4) | (yy.astype(int) % 5 == 2)] = GRID
    rgb[sheet] = sp[sheet]
    # a pencil line on the sheet
    sk = sheet & (np.abs((yy - 6) - np.sin(xx * 0.35) * 1.6) < 0.5) & (xx > 11) & (xx < W - 9)
    rgb[sk] = GRAPHITE[1]
    fr = 4.4 - (yy - 11) * 0.06 + noise(H, W, 6, 73) * 0.3
    fr[(yy == 11)] += 1.4                                   # the folded top edge
    fr[(xx == 0)] += 0.8
    fr[(yy == H - 1) | (xx == W - 1)] -= 1.6
    rgb[front] = dither(fr, KRAFT)[front]
    rgb[front & (yy == 12)] = KRAFT[2]
    rgb[back & ~front & ~sheet & (yy < TAB_H + 1)] = dither(3.6 - yy * 0.1, KRAFT)[back & ~front & ~sheet & (yy < TAB_H + 1)]
    cm = np.zeros((H + 3, W + 3), bool)
    cm[:H, :W] = m
    out = over(out, checker_shadow(cm, 2, 2, 165))
    body = np.zeros((H + 3, W + 3, 3), np.uint8)
    body[:H, :W] = rgb
    out[cm] = np.dstack([body, np.full((H + 3, W + 3), 255, np.uint8)])[cm]
    # a dark line round the edge so it reads on the black mat
    ring = cm & ~(np.roll(cm, 1, 0) & np.roll(cm, -1, 0) & np.roll(cm, 1, 1) & np.roll(cm, -1, 1))
    out[ring] = (40, 26, 10, 255)
    save(name, out)
    return {"w": (W + 3) * S, "h": (H + 3) * S, "bodyW": W * S, "bodyH": H * S, "labelY": 16 * S}


# ---- layout ----------------------------------------------------------------------------
def tone_ramp(hexc, n=8, lo=0.22, hi=1.45):
    """a leather ramp from one colour: deep shadow to a worn, warmer highlight"""
    c = np.array([int(hexc[i:i + 2], 16) for i in (1, 3, 5)], np.float32)
    out = []
    for i in range(n):
        f = lo + (hi - lo) * i / (n - 1)
        rgb = c * f + (np.array([40, 26, 8], np.float32) * max(0.0, f - 1.0))   # highlights warm toward gilt
        out.append(np.clip(rgb, 0, 255))
    return np.array(out, np.uint8)


# the run books (model/booknook), left to right as built: spine widths (in), leather and lettering piece
BOOK_WIDTHS = [1.20, 1.55, 1.35, 1.70, 1.15, 1.45, 1.80, 1.30, 1.60]
BOOK_COLOURS = [("#a8794a", "#8e1a1a"), ("#6e1c1c", "#1d2c44"), ("#1e2a44", "#8e1a1a"),
                ("#1f3b2a", "#2a1a10"), ("#d6cdb6", "#8e1a1a"), ("#6b3f24", "#1f4a33"),
                ("#2a211c", "#1d2c44"), ("#4a1a26", "#2a1a10"), ("#7a5a2a", "#8e1a1a")]


BOOK_THICK = 1.6      # spines drawn thicker than built, so a run's title fits on two lines


def bake_books(name="book", H=118):      # 118 + its outline fits the research drawer's 250 px scroll width
    """The run books: each one lying flat spine out, H art px long, book_<i>.png, with where its
    lettering piece is (the run's title is lettered over it live)"""
    frames = model_frames("booknook", "spines")
    if not frames:
        return None
    k, v = id_classes(frames[0])
    rng = light_range([(k, v)])
    ys, xs = np.where(k > 0)
    y0, y1 = ys.min(), ys.max() + 1
    # their bands touch, so cut at the gaps they were built with (spine widths, 0.06 in apart)
    edges, at = [], 0.0
    for w in BOOK_WIDTHS:
        edges.append((at - 0.03, at + w + 0.03)); at += w + 0.06
    span = at - 0.06
    px_in = (xs.max() + 1 - xs.min()) / (span + 0.03)        # the bands stand 0.015 proud each side
    runs = [(int(round(xs.min() + (a + 0.015) * px_in)), int(round(xs.min() + (b + 0.015) * px_in))) for a, b in edges]
    runs = [(max(0, a), min(k.shape[1], b)) for a, b in runs]
    books = []
    for i, (x0, x1) in enumerate(runs[:len(BOOK_COLOURS)]):
        bk, bv = k[y0:y1, x0:x1].copy(), v[y0:y1, x0:x1]
        W = max(1, int(round((x1 - x0) * H / (y1 - y0) * BOOK_THICK)))
        cls, lit = shrink_ids(bk, bv, W, H)
        # laid flat in a pile, spine out: turned a quarter, head to the left (the light turns with it)
        cls, lit = np.ascontiguousarray(np.rot90(cls)), np.ascontiguousarray(np.rot90(lit))
        leather, label = BOOK_COLOURS[i]
        # the drawer is dim: leather stays in its lower tones, the gilt is old and dull, not lamp-lit
        parts = {1: (tone_ramp(leather, 8, 0.18, 1.15), 0.6, 5.2), 3: (tone_ramp(label, 6, 0.3, 1.1), 0.4, 3.8),
                 6: (BRASS, 1.0, 4.4)}
        save("%s_%d.png" % (name, i), outlined(paint_ids(cls, lit, parts, rng), cls > 0))
        lys, lxs = np.where(cls == 3)
        books.append({"w": (H + 4) * S, "h": (W + 4) * S, "bodyW": H * S, "bodyH": W * S,
                      "labelX": int(lxs.min() + 1) * S, "labelY": int(lys.min() + 1) * S,
                      "labelW": int(lxs.max() + 1 - lxs.min()) * S, "labelH": int(lys.max() + 1 - lys.min()) * S})
    return books


# the clear eraser (model/eraser): pink pencil rubber, blue ink rubber, a cream stripe on the pink slant
def bake_eraser(name="eraser.png", L=18):     # small: it lies in the gap over the pile
    """The research drawer's clear button: the two-tone eraser seen from above, lying long, L art px long."""
    frames = model_frames("eraser", "top")
    if not frames:
        return None
    k, v = id_classes(frames[0])
    ys, xs = np.where(k > 0)
    k, v = k[ys.min():ys.max() + 1, xs.min():xs.max() + 1], v[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    H = max(1, int(round(k.shape[0] * L / k.shape[1])))
    cls, lit = shrink_ids(k, v, L, H)
    # dim drawer: the rubber stays matte, no lamp-lit highlight
    parts = {1: (tone_ramp("#c8695c", 8, 0.3, 1.1), 0.8, 5.6), 2: (tone_ramp("#5f86a8", 8, 0.3, 1.1), 0.8, 5.6),
             4: (tone_ramp("#e6dcc4", 6, 0.4, 1.0), 0.6, 4.0)}
    save(name, outlined(paint_ids(cls, lit, parts, light_range([(k, v)])), cls > 0))
    return {"w": (L + 4) * S, "h": (H + 4) * S, "bodyW": L * S, "bodyH": H * S}


# the open rotary rolodex (model/rolodex): cream cards, dark cradle, blue index tabs, ink, black knobs
# (slots 0, 1, 2, 3, 4). The card wheel is rendered apart from the cradle so only the cards turn.
PLASTIC = hexes("#100e0c", "#1e1814", "#342820", "#4c3c30", "#685444", "#847060", "#a49280", "#c8beb2")
TAB = hexes("#163038", "#245868", "#387888", "#5898a8", "#7eb4c0", "#a6d0d8", "#d0e8ec", "#f0f8f8")
ROLO_PARTS = {1: (PARCH, 1.6, 6.2), 2: (PLASTIC, 0.4, 5.4), 3: (TAB, 1.2, 6.4), 4: (INK, 0.2, 2.2),
              5: (PLASTIC, 0.2, 4.6)}


def _rolo_crop(ids):
    """one crop for every pass, so the cradle and the turning cards stay on the same pixels"""
    on = np.zeros(ids[0][0].shape, bool)
    for k, _ in ids:
        on |= k > 0
    ys, xs = np.where(on)
    return ys.min(), ys.max() + 1, xs.min(), xs.max() + 1


def _rolo_face(cls):
    """where the front card's face sits: the paper block on the right of the wheel (the spindle runs
    up the frame, the cards flip sideways, and the one lying flat on the right is the one you read)"""
    paper = cls == 1
    if not paper.any():
        return None
    cols = paper.sum(0)
    band = cols > max(4, cols.max() * 0.35)
    xs = np.where(band)[0]
    splits = np.where(np.diff(xs) > 2)[0]
    groups = np.split(xs, splits + 1) if len(xs) else []
    if not groups:
        return None
    face = max(groups, key=lambda g: g.mean())
    x0, x1 = int(face.min()), int(face.max()) + 1
    rows = np.where(paper[:, x0:x1].sum(1) > 0)[0]
    if len(rows) == 0:
        return None
    return int(rows.min()), int(rows.max()) + 1, x0, x1


def bake_rolodex(name="rolodex", W=110, H=132):
    """The files as an expanding accordion file, fanned open in the drawer: nineteen cream
    dividers with blue tabs, hinged from a spine you can see at the bottom middle, the fan
    blown up until its tips bleed off the top and its sides off the edges, the file's own
    dark covers filling the bottom corners -- no wood anywhere, and no backdrop either:
    every pixel is some part of the file. A flip (3 frames, 12 to a full turn) takes the
    front divider over to the back of the fan while the right-hand side closes one slot
    toward the middle, so the fan leafs forever. Fully opaque in every frame."""
    import math

    from PIL import ImageDraw

    frames_n, cards = 36, 12
    px, py = W / 2.0, H - 14.0             # the spine hinge, inside the frame
    div_w, div_l = 26.0, 172.0            # divider width and hinge-to-tip length
    step = 8.5                            # degrees between dividers: 19 of them span 153
    tab_r, tab_l = 7.0, 16.0
    ink_c = tuple(int(c) for c in INK[1])
    edge_c = (14, 10, 6)

    def tone(k, drop=0):                  # face tone by slot: the front face pops, the
        idx = [6, 5, 4, 4, 3, 3, 2, 2, 2, 2][min(9, abs(k))]   # rest fall off into shade
        return tuple(int(c) for c in PARCH[max(1, idx - drop)])

    def divider_poly(a_deg, tab_rad):
        a = math.radians(a_deg)
        ca, sa = math.cos(a), math.sin(a)

        def pt(x, y):                     # local: x across, y up from the hinge
            return (px + x * ca - y * sa, py - x * sa - y * ca)

        body = [pt(-div_w / 2, -6), pt(div_w / 2, -6), pt(div_w / 2, div_l), pt(-div_w / 2, div_l)]
        # the tab rides the trailing edge, like the real file: a bump out of the side,
        # stepped up and down the fan by slot so the arc is ringed with tabs
        tab = [pt(div_w / 2, tab_rad - tab_l / 2), pt(div_w / 2 + tab_r, tab_rad - tab_l / 2),
               pt(div_w / 2 + tab_r, tab_rad + tab_l / 2), pt(div_w / 2, tab_rad + tab_l / 2)]
        return body, tab, pt

    def draw_frame(img, flip_ph):
        """flip_ph: None at rest, else 0..1 through the flip of the front divider."""
        d = ImageDraw.Draw(img, "RGBA")
        ease = flip_ph * flip_ph * (3 - 2 * flip_ph) if flip_ph is not None else 0.0
        # the covers the fan sits between, splayed to the bottom corners
        d.polygon([(px, py), (0, H), (0, H - 46), (px - 40, py + 2)],
                  fill=tuple(int(c) for c in PLASTIC[2]) + (255,), outline=edge_c + (255,))
        d.polygon([(px, py), (W, H), (W, H - 46), (px + 40, py + 2)],
                  fill=tuple(int(c) for c in PLASTIC[2]) + (255,), outline=edge_c + (255,))
        # (angle, is_flipper) for the 19 slots, left to right; slot 9 stands at the front
        slots = []
        for p in range(19):
            a = (p - 9) * step
            if flip_ph is not None:
                if p == 9:
                    a = 9 * step * ease
                elif p > 9:
                    a -= step * ease
            slots.append((a, p == 9 and flip_ph is not None))
        order = sorted(range(19), key=lambda i: -abs(slots[i][0]))
        if flip_ph is not None:
            fi = next(i for i, s in enumerate(slots) if s[1])
            order.remove(fi)
            if flip_ph < 0.5:
                order.append(fi)          # still on top, being leafed over
            else:
                order.insert(0, fi)       # tucking in behind the fan
        for i in order:
            a, is_flip = slots[i]
            tab_rad = 55.0 + (i % 5) * 20.0     # tabs step up the fan by slot
            body, tab, pt = divider_poly(a, tab_rad)
            sh = [(x + 2, y + 3) for x, y in body]
            d.polygon(sh, fill=(20, 14, 8, 70))
            kk = int(round(a / step))
            d.polygon(body, fill=tone(kk) + (255,), outline=edge_c + (255,))
            band = [pt(div_w / 2 - 3, -6), pt(div_w / 2, -6), pt(div_w / 2, div_l), pt(div_w / 2 - 3, div_l)]
            d.polygon(band, fill=tone(kk, 1) + (255,))
            d.line([pt(div_w / 2, -6), pt(div_w / 2, div_l)], fill=edge_c + (255,), width=1)
            d.polygon(tab, fill=tuple(int(c) for c in TAB[4]) + (255,), outline=edge_c + (255,))
            if a == 0.0 or is_flip:
                # the front face carries the writing: three ink lines near the top
                for j, (off, ln) in enumerate([(-9, 17), (-6, 12), (-11, 14)]):
                    r0 = 100.0 + j * 8.0
                    d.line([pt(off, r0), pt(off + ln, r0)], fill=ink_c + (255,), width=2)
        # the spine rivet the whole fan turns on
        d.ellipse([px - 5, py - 5, px + 5, py + 5],
                  fill=tuple(int(c) for c in PLASTIC[0]) + (255,), outline=edge_c + (255,))
        d.ellipse([px - 2, py - 2, px + 2, py + 2], fill=tuple(int(c) for c in BRASS[3]) + (255,))

    for t in range(frames_n):
        ph = (t % 3) / 3.0
        img = Image.new("RGBA", (W, H), (0, 0, 0, 255))
        arr = np.asarray(img).copy()
        # the pleated gusset: folds converging on the hinge, for any sliver left over
        yy, xx = np.mgrid[0:H, 0:W]
        ang = np.degrees(np.arctan2(xx - px, py - yy))
        fold = ((ang + 66) // 6).astype(int) % 2
        gus = np.where(fold[..., None] == 0, np.array(PLASTIC[2], np.uint8), np.array(PLASTIC[1], np.uint8))
        deep = np.clip(1.0 - (yy / H) * 0.25, 0.7, 1.0)[..., None]
        arr[..., :3] = (gus.astype(np.float32) * deep).astype(np.uint8)
        img = Image.fromarray(arr, "RGBA")
        draw_frame(img, None if t % 3 == 0 else ph)
        save("%s_%02d.png" % (name, t), np.asarray(img))
    return {"w": W * S, "h": H * S, "bodyW": W * S, "bodyH": H * S,
            "frames": frames_n, "cards": cards}


def bake_minidrawer_front(name="minidrawer_front.png", W=120, H=28):
    """The front of a mini drawer that lives inside a big one (the MODELS drawer in the tools
    tray): an oak board the width of the MODELING folder, a stamped aluminium plate across the
    top for its name, and a chrome bar pull low in the middle — the handle you grab. Drawn flat
    like the wavegoo cabinet fronts, so the plate's lettering (QML, on top) stays crisp while the
    box behind it carries the depth. A dithered shadow strip rides under it."""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    # oak board: planks running across, grain streaking along them
    plank = np.floor(yy / 9).astype(int)
    rng = np.random.default_rng(71)
    tone = (rng.random(8) - 0.5) * 1.1
    sh = 4.6 + tone[np.clip(plank, 0, 7)]
    sh += np.where(np.sin(xx * 0.11 + plank * 2.1) > 0.86, 0.8, 0.0)
    sh += noise(H, W, 5, 72) * 0.15
    sh = np.where(np.mod(yy, 9) < 1, sh - 2.0, sh)                    # plank seams
    sh += (1 - yy / H) * 0.7                                          # lit from above
    m = np.ones((H, W), bool)
    sh[(xx == 0) | (yy == 0)] += 1.6
    sh[(xx == W - 1) | (yy == H - 1)] -= 2.2
    rgb = dither(np.clip(sh, 0, len(WOOD) - 1), WOOD)
    body = solid(rgb, m)
    # corner screws
    for sx, sy in ((3, 3), (W - 6, 3), (3, H - 6), (W - 6, H - 6)):
        scr, scm = screw(2, cross=False)
        sub = body[sy:sy + 5, sx:sx + 5]
        sub[scm[:sub.shape[0], :sub.shape[1]]] = np.dstack([scr, np.full(scr.shape[:2], 255, np.uint8)])[:sub.shape[0], :sub.shape[1]][scm[:sub.shape[0], :sub.shape[1]]]
    # the aluminium plate, stamped across the top middle: QML letters it
    pw, ph, px0, py0 = 64, 12, (W - 64) // 2, 3
    psh = 5.6 + noise(ph, pw, 3, 73) * 0.2
    psh[0, :] += 1.6; psh[-1, :] -= 1.8; psh[:, 0] += 1.2; psh[:, -1] -= 1.4
    body[py0:py0 + ph, px0:px0 + pw, :3] = dither(np.clip(psh, 0, len(ALU) - 1), ALU)
    for sx in (px0 + 2, px0 + pw - 4):
        scr, scm = screw(1, cross=False)
        sub = body[py0 + 4:py0 + 7, sx:sx + 3]
        sub[scm[:sub.shape[0], :sub.shape[1]]] = np.dstack([scr, np.full(scr.shape[:2], 255, np.uint8)])[:sub.shape[0], :sub.shape[1]][scm[:sub.shape[0], :sub.shape[1]]]
    # the chrome bar pull, low and central: two feet and a bar with a specular streak
    bx0, bx1, byy = W // 2 - 30, W // 2 + 30, H - 9
    for fx in (bx0, bx1 - 3):
        body[byy - 2:byy + 5, fx:fx + 3, :3] = dither(np.array([[3.2, 5.4, 3.2]] * 7, np.float32), CHROME)
    bar = np.zeros((4, bx1 - bx0), np.float32)
    bar[0, :] = 2.6; bar[1, :] = 8.8; bar[2, :] = 6.4; bar[3, :] = 1.4
    body[byy:byy + 4, bx0:bx1, :3] = dither(bar, CHROME)
    body[byy + 1, bx0 + 3:bx1 - 3, :3] = CHROME[9]                     # the long streak
    # dithered shadow under the board, on a canvas 3px taller
    pad_m = np.zeros((H + 3, W), bool); pad_m[:H] = m
    body_pad = np.zeros((H + 3, W, 4), np.uint8); body_pad[:H] = body
    out = np.zeros((H + 3, W, 4), np.uint8)
    out = over(out, checker_shadow(pad_m, 0, 3))
    out = over(out, body_pad)
    save(name, out)
    return {"w": W * S, "h": (H + 3) * S, "plateX": px0 * S, "plateY": py0 * S,
            "plateW": pw * S, "plateH": ph * S}


def main():
    VIEW_W, VIEW_H = 512, 288             # art px: the viewport is 1024 x 576 on screen
    T = 20
    geom = {"S": S}
    geom["frame"] = bake_frame(VIEW_W, VIEW_H, T)
    geom["view"] = {"x": T * S, "y": T * S, "w": VIEW_W * S, "h": VIEW_H * S}
    geom["research"] = bake_drawer("drawer_research.png", 164, 250, "l", (0.94, 0.40), seed=11)
    geom["tools"] = bake_drawer("drawer_tools.png", 164, 250, "r", (0.06, 0.40), seed=23)
    geom["timeline"] = bake_drawer("drawer_timeline.png", 500, 112, "b", (0.5, 0.05), k=0.88, seed=37)
    FW = VIEW_W + 2 * T
    geom["bar"] = bake_bar(FW + 10, 30)
    for gname in GLYPHS:
        geom["button"] = bake_button("btn_%s.png" % gname, gname, False)
        bake_button("btn_%s_down.png" % gname, gname, True)
    geom["plateH"] = bake_plate("plate_h.png", 76, 13)
    geom["plateV"] = bake_plate("plate_v.png", 13, 84)
    geom["nameplate"] = bake_nameplate(84, 15)
    geom["key"] = bake_key("key.png", 40, 16)
    bake_key("key_down.png", 40, 16, pressed=True)
    bake_lamp("lamp_on.png", ("#2a1500", "#5a2c00", "#8f4a00", "#c87800", "#f2a91c", "#ffd970", "#ffffff"))
    bake_lamp("lamp_off.png", ("#0b0c0e", "#16181b", "#222429", "#2e3137", "#3c4047", "#565b63", "#80858c"))
    geom["pencil"] = bake_pencil("pencil.png")
    geom["pencilShadow"] = bake_pencil_shadow("pencil_shadow.png")
    geom["toolTray"] = bake_tool_tray("tool_tray.png")
    geom["pencil3d"] = bake_turning("pencil3d", "pencil", 58, PENCIL_PARTS, 1)
    geom["folder"] = bake_folder("folder.png")
    geom["scroll3d"] = bake_scroll3d("scroll3d")
    geom["chisel3d"] = bake_turning("chisel3d", "chisel", 60, CHISEL_PARTS, 2)
    geom["modelfolder"] = bake_modelfolder("modelfolder")
    geom["minidrawerBox"] = bake_drawer("minidrawer_box.png", 120, 118, "b", (0.5, 0.38), k=0.80, seed=43)
    geom["minidrawerFront"] = bake_minidrawer_front()
    geom["books"] = bake_books()
    geom["eraser"] = bake_eraser()
    geom["scrollRod"] = bake_scroll_rod("scroll_rod.png")
    geom["parchment"] = bake_parchment("parchment.png")
    geom["paperball"] = bake_paperball("paperball")
    geom["paperballRoll"] = bake_turning("paperball_roll", "paperball", 34, PAPER_PARTS, 1)
    geom["rolodex"] = bake_rolodex()
    geom["keyS"] = bake_key("key_s.png", 20, 16)
    bake_key("key_s_down.png", 20, 16, pressed=True)
    bake_tiles()
    geom["pull"] = P * S
    geom["shadow"] = SH * S
    with open(os.path.join(OUT, "geom.js"), "w") as f:
        f.write("// generated by bake.py, do not edit\n.pragma library\nvar G = ")
        f.write(json.dumps(geom, indent=1))
        f.write(";\n")
    print(json.dumps(geom))



ENAMEL_FILES = ["frame.png", "bar.png", "drawer_research.png", "drawer_tools.png", "drawer_timeline.png",
                "modelfolder_back.png", "modelfolder_cover.png", "minidrawer_box.png"]


def bake_scheme(name):
    """Bake the chest's enamel pieces in one colour scheme into art/schemes/<name>/ (same geometry
    as the stock bake, so geom.js is shared). "red" is the stock art and needs no folder."""
    global RED, OUT
    hue, sat, val, ink = scheme_tint(name)
    stock_red, stock_out = RED, OUT
    RED = tinted(stock_red, hue, sat, val)
    OUT = os.path.join(stock_out, "schemes", name)
    os.makedirs(OUT, exist_ok=True)
    try:
        VIEW_W, VIEW_H, T = 512, 288, 20
        bake_frame(VIEW_W, VIEW_H, T)
        bake_drawer("drawer_research.png", 164, 250, "l", (0.94, 0.40), seed=11)
        bake_drawer("drawer_tools.png", 164, 250, "r", (0.06, 0.40), seed=23)
        bake_drawer("drawer_timeline.png", 500, 112, "b", (0.5, 0.05), k=0.88, seed=37)
        bake_bar(VIEW_W + 2 * T + 10, 30)
        bake_modelfolder("modelfolder")
        bake_drawer("minidrawer_box.png", 120, 118, "b", (0.5, 0.38), k=0.80, seed=43)
    finally:
        RED, OUT = stock_red, stock_out
    return ink


def bake_schemes(names=None):
    """every scheme but the stock red, plus art/schemes/schemes.js: the list and each one's ink colour"""
    names = names or [n for n in SCHEMES if n != "red"]
    inks = {n: scheme_tint(n)[3] for n in SCHEMES}
    for n in names:
        print("scheme", n)
        bake_scheme(n)
    os.makedirs(os.path.join(OUT, "schemes"), exist_ok=True)
    with open(os.path.join(OUT, "schemes", "schemes.js"), "w") as f:
        f.write("// generated by bake.py --schemes, do not edit\n.pragma library\n")
        f.write("var NAMES = " + json.dumps(list(SCHEMES)) + ";\n")
        f.write("var INK = " + json.dumps(inks) + ";\n")
        f.write("var FILES = " + json.dumps(ENAMEL_FILES) + ";\n")


if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1 and sys.argv[1] == "--schemes":
        bake_schemes(sys.argv[2:])
    else:
        main()
