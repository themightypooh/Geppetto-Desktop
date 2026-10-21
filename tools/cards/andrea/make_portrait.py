#!/usr/bin/env python3
"""Placeholder portrait for the Andrea character card.

The card image is a plain title card rather than artwork: a warm hotel-room
wash, a few out-of-focus string lights, and the character name. Replace
portrait.png with your own picture and re-run
`node tools/cards/build-card.mjs --card andrea` to install it; the metadata
lives in card.json and is unaffected.

Usage:
    python3 tools/cards/andrea/make_portrait.py
"""

from __future__ import annotations

import random
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
W, H = 400, 600
SEED = 18082026
FONT = "DejaVu Serif, Georgia, serif"


def build_svg() -> str:
    rng = random.Random(SEED)
    p: list[str] = []
    p.append("<svg xmlns=\"http://www.w3.org/2000/svg\" "
             f"width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\">")
    p.append(f"""  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0.25" y2="1">
      <stop offset="0%" stop-color="#241521"/>
      <stop offset="45%" stop-color="#3b2029"/>
      <stop offset="100%" stop-color="#130c11"/>
    </linearGradient>
    <radialGradient id="warm" cx="50%" cy="24%" r="60%">
      <stop offset="0%" stop-color="#ffcf8e" stop-opacity="0.34"/>
      <stop offset="45%" stop-color="#c9803a" stop-opacity="0.13"/>
      <stop offset="100%" stop-color="#000000" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="bulb" cx="50%" cy="50%" r="50%">
      <stop offset="0%" stop-color="#fff4d8"/>
      <stop offset="32%" stop-color="#ffd48a" stop-opacity="0.8"/>
      <stop offset="100%" stop-color="#ff9d3c" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="fade" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#130c11" stop-opacity="0"/>
      <stop offset="100%" stop-color="#0d070b" stop-opacity="0.94"/>
    </linearGradient>
    <radialGradient id="vignette" cx="50%" cy="44%" r="72%">
      <stop offset="48%" stop-color="#000000" stop-opacity="0"/>
      <stop offset="100%" stop-color="#000000" stop-opacity="0.82"/>
    </radialGradient>
  </defs>""")

    p.append('  <rect width="400" height="600" fill="url(#bg)"/>')
    p.append('  <rect width="400" height="600" fill="url(#warm)"/>')

    # out-of-focus room lights
    for _ in range(14):
        bx = rng.uniform(10, 390)
        by = rng.uniform(30, 280)
        r = rng.uniform(20, 52)
        p.append(f'    <circle cx="{bx:.0f}" cy="{by:.0f}" r="{r:.0f}" fill="#ffbb66" '
                 f'opacity="{rng.uniform(0.03, 0.08):.2f}"/>')

    # two swags of string lights
    def swag(y0: float, y1: float, sag: float, count: int) -> None:
        p.append(f'    <path d="M-10 {y0:.0f} Q 200 {y0 + sag:.0f}, 410 {y1:.0f}" '
                 f'fill="none" stroke="#2a1c18" stroke-width="1.2" opacity="0.8"/>')
        for i in range(count):
            t = (i + 0.5) / count
            x = (1 - t) ** 2 * -10 + 2 * (1 - t) * t * 200 + t ** 2 * 410
            y = (1 - t) ** 2 * y0 + 2 * (1 - t) * t * (y0 + sag) + t ** 2 * y1
            r = rng.uniform(4.5, 6.5)
            p.append(f'    <circle cx="{x:.0f}" cy="{y + 6:.0f}" r="{r * 3.4:.0f}" fill="url(#bulb)" opacity="0.45"/>')
            p.append(f'    <circle cx="{x:.0f}" cy="{y + 6:.0f}" r="{r:.1f}" fill="#fff2d4"/>')
            p.append(f'    <circle cx="{x:.0f}" cy="{y + 6:.0f}" r="{r * 0.45:.1f}" fill="#ffffff"/>')

    p.append('  <g>')
    swag(60, 100, 70, 9)
    swag(26, 46, 42, 7)
    p.append('  </g>')

    # text block
    p.append('  <rect x="0" y="320" width="400" height="280" fill="url(#fade)"/>')
    p.append(f'  <text x="200" y="450" font-family="{FONT}" font-size="48" '
             'letter-spacing="6" fill="#f4e4cf" text-anchor="middle">ANDREA</text>')
    p.append('  <line x1="156" y1="474" x2="244" y2="474" stroke="#c98a45" stroke-width="1.6" '
             'opacity="0.8"/>')
    p.append(f'  <text x="200" y="506" font-family="{FONT}" font-size="16" '
             'letter-spacing="4" fill="#d7b48c" text-anchor="middle" opacity="0.9">'
             'A WEDDING, 2AM</text>')
    p.append(f'  <text x="200" y="534" font-family="{FONT}" font-size="13" '
             'letter-spacing="2" fill="#8fa9a6" text-anchor="middle" opacity="0.75">'
             'EIGHT MONTHS LATER</text>')

    p.append('  <rect width="400" height="600" fill="url(#vignette)"/>')
    p.append("</svg>")
    return "\n".join(p) + "\n"


def main() -> int:
    svg_path = HERE / "portrait.svg"
    svg_path.write_text(build_svg(), encoding="utf-8")

    png_path = HERE / "portrait.png"
    renderer = shutil.which("rsvg-convert") or shutil.which("inkscape")
    if renderer is None:
        print("rsvg-convert not found; wrote portrait.svg only", file=sys.stderr)
        return 1

    if renderer.endswith("rsvg-convert"):
        cmd = [renderer, "-w", str(W), "-h", str(H), "-o", str(png_path), str(svg_path)]
    else:
        cmd = [renderer, "-w", str(W), "-h", str(H), "-export-type", "png",
               "-o", str(png_path), str(svg_path)]
    subprocess.run(cmd, check=True)

    print(f"wrote {svg_path}")
    print(f"wrote {png_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
