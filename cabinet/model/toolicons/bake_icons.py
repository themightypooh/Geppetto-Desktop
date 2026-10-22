#!/usr/bin/env python3
"""Bake the tool wheel's icons into the chest's pixel art: each flat ID render from render_icons.py
(frames/<key>.png) is shrunk to L art px, repainted part by part in bake.py's dithered ramps from the
rendered light, ringed in a dark outline with the checker drop shadow, and saved 2x to
cabinet/art/tools/<key>.png. Also writes sheet.png, the whole set on the wheel's steel.

  python3 bake_icons.py [L=26] [only=blob,hole]
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
CABINET = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, CABINET)
import bake  # noqa: E402

ART = os.path.join(CABINET, "art", "tools")
bake.ID_HUES[8] = 270                  # the eighth slot (glow) renders violet
bake.ID_HUES[9] = 150                  # the ninth (brown: freckles, fur) sea-green
FLESH = bake.hexes("#2a0e08", "#4e1e14", "#7a3424", "#a04e3a", "#c46c54", "#de9078", "#f0b6a0", "#fbd8c8")
CLOTH = bake.hexes("#0a1020", "#142038", "#1e3256", "#2c4878", "#3d5f98", "#5a7eb6", "#86a6d2")
BROWN = bake.hexes("#1e0f06", "#3a1f0e", "#5a3318", "#7a4a24", "#9a6434", "#b8804a")
GLOW = bake.hexes("#7d5806", "#bc8c0d", "#eec029", "#fbdc66", "#fff2b0", "#fffbe6")
# ID class (render_icons.py IDS) -> (ramp, base, gain)
PARTS = {1: (bake.STEEL, 0.6, 5.6), 4: (bake.PARCH, 1.6, 6.0), 3: (bake.RED, 2.4, 7.2), 7: (FLESH, 1.0, 6.4),
         5: (bake.INK, 0.2, 2.6), 6: (bake.BRASS, 1.0, 6.9), 2: (CLOTH, 0.6, 5.8), 8: (GLOW, 1.8, 3.6), 9: (BROWN, 0.8, 5.0)}


def main():
    opts = dict(a.split("=", 1) for a in sys.argv[1:])
    L = int(opts.get("L", "26"))
    only = set(opts["only"].split(",")) if "only" in opts else None
    cells = json.load(open(os.path.join(HERE, "cells.json")))["icons"]
    os.makedirs(ART, exist_ok=True)
    done = []
    for key, c in cells.items():
        if only and key not in only:
            continue
        name = key.replace(":", "_")
        src = os.path.join(HERE, "frames", name + ".png")
        if not os.path.exists(src):
            continue
        k, v = bake.id_classes(np.asarray(Image.open(src).convert("RGB")))
        ys, xs = np.where(k > 0)
        y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
        # square around the icon so they all fill their tile alike
        side = max(y1 - y0, x1 - x0)
        cy, cx = (y0 + y1) // 2, (x0 + x1) // 2
        pad = lambda a: np.pad(a, side, mode="constant")
        kk, vv = pad(k), pad(v)
        y0, x0 = cy - side // 2 + side, cx - side // 2 + side
        kk, vv = kk[y0:y0 + side, x0:x0 + side], vv[y0:y0 + side, x0:x0 + side]
        cls, lit = bake.shrink_ids(kk, vv, L, L)
        rgba = bake.outlined(bake.paint_ids(cls, lit, PARTS, bake.light_range([(kk, vv)])), cls > 0)
        out = os.path.join(ART, name + ".png")
        Image.fromarray(bake.upscale(rgba), "RGBA").save(out)
        done.append((c["label"], out))
        print(key, flush=True)

    cols, cs = 10, (L + 4) * bake.S * 2
    rows = (len(done) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cs, rows * (cs + 16)), "#2b2f36")
    d = ImageDraw.Draw(sheet)
    for n, (label, png) in enumerate(done):
        im = Image.open(png)
        im = im.resize((im.width * 2, im.height * 2), Image.NEAREST)
        x, y = (n % cols) * cs, (n // cols) * (cs + 16)
        sheet.paste(im, (x, y), im)
        d.text((x + 4, y + cs + 2), label, fill="#c9ccd0")
    sheet.save(os.path.join(HERE, "sheet.png"))


if __name__ == "__main__":
    main()
