#!/usr/bin/env python3
"""Placeholder portrait for the Sable Harrow character card.

The card image is a designed title card rather than artwork: dark tiled wall,
one lamp, steam, and the character name. Replace portrait.png with your own
art and re-run `node tools/cards/build-card.mjs --card sable-harrow` to
install it; the metadata lives in card.json and is unaffected.

Usage:
    python3 tools/cards/sable-harrow/make_portrait.py
"""

from __future__ import annotations

import random
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
W, H = 400, 600
SEED = 341034
FONT = "DejaVu Serif, Georgia, serif"


def build_svg() -> str:
    rng = random.Random(SEED)
    p: list[str] = []
    p.append("<svg xmlns=\"http://www.w3.org/2000/svg\" "
             f"width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\">")
    p.append(f"""  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0.3" y2="1">
      <stop offset="0%" stop-color="#0b1a1c"/>
      <stop offset="45%" stop-color="#123030"/>
      <stop offset="100%" stop-color="#060f10"/>
    </linearGradient>
    <radialGradient id="lampglow" cx="26%" cy="30%" r="62%">
      <stop offset="0%" stop-color="#ffc887" stop-opacity="0.70"/>
      <stop offset="40%" stop-color="#b06a2c" stop-opacity="0.22"/>
      <stop offset="100%" stop-color="#000000" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="steam" cx="50%" cy="50%" r="50%">
      <stop offset="0%" stop-color="#e8f4f2" stop-opacity="0.16"/>
      <stop offset="70%" stop-color="#d8ebe8" stop-opacity="0.05"/>
      <stop offset="100%" stop-color="#d8ebe8" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="vignette" cx="50%" cy="45%" r="70%">
      <stop offset="50%" stop-color="#000000" stop-opacity="0"/>
      <stop offset="100%" stop-color="#000000" stop-opacity="0.80"/>
    </radialGradient>
    <linearGradient id="fade" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#060f10" stop-opacity="0"/>
      <stop offset="100%" stop-color="#04090a" stop-opacity="0.92"/>
    </linearGradient>
  </defs>""")

    p.append('  <rect width="400" height="600" fill="url(#bg)"/>')
    p.append('  <g stroke="#1d3d3c" stroke-width="1.2" opacity="0.5">')
    for x in range(0, 401, 50):
        p.append(f'    <line x1="{x}" y1="0" x2="{x}" y2="600"/>')
    for y in range(0, 601, 50):
        p.append(f'    <line x1="0" y1="{y}" x2="400" y2="{y}"/>')
    p.append("  </g>")

    # the lamp
    p.append('  <rect width="400" height="600" fill="url(#lampglow)"/>')
    p.append('  <line x1="104" y1="0" x2="104" y2="150" stroke="#241a10" stroke-width="2.5"/>')
    p.append('  <path d="M78 150 L130 150 L120 182 L88 182 Z" fill="#2e2013"/>')
    p.append('  <path d="M84 154 L124 154 L117 178 L91 178 Z" fill="#ffcf8b"/>')
    p.append('  <circle cx="104" cy="165" r="8" fill="#fff6e0"/>')
    p.append('  <circle cx="104" cy="165" r="34" fill="#ffc078" opacity="0.28"/>')
    p.append('  <path d="M84 182 L124 182 L114 196 L94 196 Z" fill="#241a10"/>')

    # steam
    p.append('  <g>')
    for _ in range(18):
        cx = rng.uniform(20, 380)
        cy = rng.uniform(200, 590)
        rx = rng.uniform(60, 180)
        ry = rng.uniform(30, 85)
        p.append(f'    <ellipse cx="{cx:.0f}" cy="{cy:.0f}" rx="{rx:.0f}" ry="{ry:.0f}" '
                 f'fill="url(#steam)"/>')
    p.append("  </g>")

    # text block
    p.append('  <rect x="0" y="330" width="400" height="270" fill="url(#fade)"/>')
    p.append(f'  <text x="200" y="452" font-family="{FONT}" font-size="46" '
             'letter-spacing="3" fill="#f3e3cc" text-anchor="middle">SABLE</text>')
    p.append(f'  <text x="200" y="500" font-family="{FONT}" font-size="46" '
             'letter-spacing="3" fill="#f3e3cc" text-anchor="middle">HARROW</text>')
    p.append('  <line x1="150" y1="524" x2="250" y2="524" stroke="#c98a45" stroke-width="1.6" '
             'opacity="0.8"/>')
    p.append(f'  <text x="200" y="554" font-family="{FONT}" font-size="17" '
             'letter-spacing="4" fill="#d7b48c" text-anchor="middle" opacity="0.9">'
             'THE STEAMHOUSE</text>')
    p.append(f'  <text x="200" y="580" font-family="{FONT}" font-size="13" '
             'letter-spacing="2" fill="#8fa9a6" text-anchor="middle" opacity="0.75">'
             'BELLWETHER REACH - FAR BANK</text>')

    p.append('  <rect width="400" height="600" fill="url(#vignette)"/>')
    p.append("</svg>")
    return "\n".join(p) + "\n"


def main() -> int:
    svg_path = HERE / "portrait.svg"
    svg_path.write_text(build_svg(), encoding="utf-8")
    png_path = HERE / "portrait.png"
    renderer = shutil.which("rsvg-convert")
    if renderer is None:
        print("rsvg-convert not found; wrote portrait.svg only", file=sys.stderr)
        return 1
    subprocess.run([renderer, "-w", str(W), "-h", str(H), "-o", str(png_path), str(svg_path)],
                   check=True)
    print(f"wrote {svg_path}")
    print(f"wrote {png_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
