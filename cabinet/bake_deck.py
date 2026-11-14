#!/usr/bin/env python3
"""Bake the pane pace deck (LV1, 2026-11-13): the little machinist's control plate that slides
up out of a pane while a model builds in it. Same rules as bake.py (one art px = S screen px,
fixed ramps, 4x4 Bayer dither, nearest-neighbour upscale, no smoothing) and the same parts
language as the Timeline keys: brushed aluminium plate on chrome screws, black bakelite keys,
jewel lamps, a recessed fader slot with a ribbed bakelite cap.

Writes ONLY these new files (never runs bake.main(), so no existing art changes):
  deck_plate.png     the faceplate (360 x 72 + shadow)
  deck_slot.png      the fader's recessed slot with engraved ticks (128 x 16)
  deck_cap.png       the fader cap, ribbed bakelite with a chrome line (16 x 28); deck_cap_down.png pressed
  deck_glow.png      an amber halo that blooms around the lamp when a step lands
  deck_lcd.png       the recessed readout window (60 x 24)

Run: python3 bake_deck.py
"""
import numpy as np
from bake import (S, ALU, CHROME, RUBBER, TAPE, YELLOW, hexes, dither, noise, save, solid,
                  checker_shadow, over, rounded, screw, stamp)

PLATE_W, PLATE_H = 180, 36       # art px
SLOT_W, SLOT_H = 64, 8
CAP_W, CAP_H = 8, 14
LCD_W, LCD_H = 30, 12


def deck_plate():
    W, H = PLATE_W, PLATE_H
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    m = rounded(W, H, 3)
    brushed = noise(H, W, 1.5, 19) * 0.22 + noise(H, W, 30, 23) * 0.15
    sh = 4.5 + brushed - (yy / H) * 0.9
    sh[yy == 1] += 1.7
    sh[xx == 1] += 1.0
    sh[yy == H - 2] -= 1.9
    sh[xx == W - 2] -= 1.2
    # an engraved rule under the top label row, short of the screws
    rule = (yy == 13) & (xx >= 8) & (xx <= W - 9)
    sh[rule] -= 1.8
    sh[(yy == 14) & (xx >= 8) & (xx <= W - 9)] += 0.9
    rgb = dither(sh, ALU)
    rgb[m & ((xx == 0) | (yy == 0) | (xx == W - 1) | (yy == H - 1))] = ALU[0]
    body = m.copy()
    sc, scm = screw(1, cross=False)
    for x, y in ((2, 2), (W - 5, 2), (2, H - 5), (W - 5, H - 5)):
        stamp(rgb, body, sc, scm, x, y)
    CW, CH = W + 2, H + 2
    out = np.zeros((CH, CW, 4), np.uint8)
    cm = np.zeros((CH, CW), bool)
    cm[:H, :W] = m
    out = over(out, checker_shadow(cm, 2, 2, 170))
    out[:H, :W][m] = solid(rgb)[m]
    save("deck_plate.png", out)


def deck_slot():
    W, H = SLOT_W, SLOT_H
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    out = np.zeros((H, W, 4), np.uint8)
    # engraved tick marks above and below the groove: every 8 px, a long one at the ends and middle
    ticks = np.zeros((H, W), bool)
    for x in range(2, W - 1, 6):
        big = x in (2, W - 2 - ((W - 4) % 6)) or abs(x - W // 2) < 3
        ticks[0:2 if big else 1, x] = True
        ticks[H - (2 if big else 1):H, x] = True
    tick_rgb = dither(np.full((H, W), 0.6, np.float32), ALU)
    out[ticks] = solid(tick_rgb)[ticks]
    # the groove itself: a dark recessed channel with a chrome lip
    g = (yy >= 3) & (yy <= H - 4) & (xx >= 1) & (xx <= W - 2)
    sh = np.full((H, W), 0.8, np.float32) + noise(H, W, 3, 31) * 0.2
    sh[yy == 3] = 0.2
    rgb = dither(sh, TAPE)
    lip_lo = (yy == H - 3) & (xx >= 1) & (xx <= W - 2)
    rgb[lip_lo] = CHROME[6]
    lip_end = ((xx == 0) | (xx == W - 1)) & (yy >= 3) & (yy <= H - 3)
    rgb[lip_end] = CHROME[3]
    mask = g | lip_lo | lip_end
    out[mask] = solid(rgb)[mask]
    save("deck_slot.png", out)


def deck_cap(pressed):
    W, H = CAP_W, CAP_H
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    m = rounded(W, H, 1)
    sh = np.full((H, W), 3.0 if not pressed else 2.0) + noise(H, W, 4, 41) * 0.15
    ribs = (yy.astype(int) % 2 == 0) & (yy > 1) & (yy < H - 2)
    sh[ribs] += 0.9
    if not pressed:
        sh[(xx == 1) | (yy == 1)] += 2.0
        sh[(xx == W - 2) | (yy == H - 2)] -= 1.6
    else:
        sh[(xx == 1) | (yy == 1)] -= 0.8
    rgb = dither(sh, RUBBER)
    rgb[m & ((xx == 0) | (yy == 0) | (xx == W - 1) | (yy == H - 1))] = CHROME[2]
    line = (yy == H // 2) & (xx >= 1) & (xx <= W - 2)
    rgb[line] = CHROME[8] if not pressed else CHROME[6]
    rgb[(yy == H // 2 + 1) & (xx >= 1) & (xx <= W - 2)] = CHROME[3]
    out = np.zeros((H, W, 4), np.uint8)
    out[m] = solid(rgb)[m]
    save("deck_cap_down.png" if pressed else "deck_cap.png", out)


def deck_glow():
    r = 9
    d = 2 * r + 1
    yy, xx = np.mgrid[0:d, 0:d] - r
    dist = np.hypot(xx, yy)
    a = np.clip(1 - dist / (r + 0.5), 0, 1) ** 1.6
    # dithered alpha: the halo is drawn in solid amber dots, thinning out with distance
    keep = a > np.indices((d, d)).sum(0) % 2 * 0.25 + (BAYER_D(d) * 0.75)
    rgb = dither(np.clip(3.5 + a * 3, 0, 6), YELLOW)
    out = np.zeros((d, d, 4), np.uint8)
    out[keep] = np.dstack([rgb, np.full((d, d), 210, np.uint8)])[keep]
    save("deck_glow.png", out)


def BAYER_D(d):
    from bake import bayer
    return bayer(d, d)


def deck_lcd():
    W, H = LCD_W, LCD_H
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    m = rounded(W, H, 1)
    sh = 5.0 - (yy / H) * 1.2 + noise(H, W, 1.5, 51) * 0.2
    sh[yy == 0] -= 2.0
    sh[yy == H - 1] += 1.2
    rgb = dither(sh, ALU)
    field = (xx >= 2) & (xx < W - 2) & (yy >= 2) & (yy < H - 2)
    rgb[field] = dither(1.0 + noise(H, W, 3, 52) * 0.25, TAPE)[field]
    rgb[(yy == 2) & field] = TAPE[0]
    out = np.zeros((H, W, 4), np.uint8)
    out[m] = solid(rgb)[m]
    save("deck_lcd.png", out)


def main():
    deck_plate()
    deck_slot()
    deck_cap(False)
    deck_cap(True)
    deck_glow()
    deck_lcd()
    print("baked deck_plate/slot/cap/cap_down/glow/lcd at S=%d" % S)


if __name__ == "__main__":
    main()
