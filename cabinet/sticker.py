"""The send-off's last frame (a finished model pressed flat onto a card, square on, no grid or ground
shadow) cut out of the viewport as a die-cut sticker: the model on a white border with a thin dark edge.
The cabinet files it in the tools drawer's MODELING folder; it's only a picture of the model, the
model itself is untouched."""
import glob
import os

import numpy as np

BORDER = 7          # px of white round the model
EDGE = 2            # px of dark edge round the white
PAPER = (250, 246, 236)
INK = (38, 30, 22)


def disc(m, r):
    """m grown by r px in every direction (m is padded by more than r, so rolling never wraps)"""
    out = m.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dx * dx + dy * dy <= r * r + r:
                out |= np.roll(np.roll(m, dy, 0), dx, 1)
    return out


def cut(rgbx, w, h, tol=10):
    """rgbx: the frame's w*h*4 bytes. Returns (rgba array, (x, y, w, h) of it in the frame) or None.
    The backdrop is flat, so whatever differs from the corners is the model."""
    a = np.frombuffer(rgbx, np.uint8, w * h * 4).reshape(h, w, 4)[..., :3]
    bg = np.median(np.stack([a[0, 0], a[0, -1], a[-1, 0], a[-1, -1]]), 0)
    m = np.abs(a.astype(np.int16) - bg).max(-1) > tol
    ys, xs = np.where(m)
    if len(ys) < 16:
        return None
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    pad = BORDER + EDGE + 2
    mm = np.pad(m[y0:y1, x0:x1], pad)
    rgb = np.pad(a[y0:y1, x0:x1], ((pad, pad), (pad, pad), (0, 0)))
    paper = disc(mm, BORDER)
    edge = disc(paper, EDGE)
    out = np.zeros(mm.shape + (4,), np.uint8)
    out[edge] = INK + (255,)
    out[paper] = PAPER + (255,)
    out[mm, :3] = rgb[mm]
    return out, (int(x0 - pad), int(y0 - pad), int(out.shape[1]), int(out.shape[0]))


def filed(runs):
    """every run's sticker, newest first: what's in the MODELING folder"""
    fs = glob.glob(os.path.join(runs, "*", "sticker.png"))
    return sorted(fs, key=os.path.getmtime, reverse=True)
