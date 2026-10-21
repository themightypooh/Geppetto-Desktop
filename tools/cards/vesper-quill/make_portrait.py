#!/usr/bin/env python3
"""Generate the original portrait used by the Vesper Quill character card.

Writes portrait.svg (vector source, deterministic) and portrait.png (400x600,
the image SillyTavern shows for the card). No network access, no third-party
assets: every shape here is generated from a fixed seed.

Usage:
    python3 tools/cards/vesper-quill/make_portrait.py
"""

from __future__ import annotations

import random
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
W, H = 400, 600
SEED = 20261020


def lamp_glow(cx: float, cy: float, r: float, inner: str, outer: str, op: float) -> str:
    return f"""  <radialGradient id="glow{cx:.0f}_{cy:.0f}" cx="50%" cy="50%" r="50%">
    <stop offset="0%" stop-color="{inner}" stop-opacity="{op}"/>
    <stop offset="38%" stop-color="{inner}" stop-opacity="{op * 0.45:.3f}"/>
    <stop offset="100%" stop-color="{outer}" stop-opacity="0"/>
  </radialGradient>"""


def build_svg() -> str:
    rng = random.Random(SEED)
    parts: list[str] = []

    # ---------------------------------------------------------------- defs
    defs = [
        """  <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0%" stop-color="#071018"/>
    <stop offset="42%" stop-color="#102435"/>
    <stop offset="72%" stop-color="#1b3b4e"/>
    <stop offset="100%" stop-color="#0a1a24"/>
  </linearGradient>""",
        """  <linearGradient id="water" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0%" stop-color="#12293a"/>
    <stop offset="30%" stop-color="#081521"/>
    <stop offset="100%" stop-color="#040b11"/>
  </linearGradient>""",
        """  <radialGradient id="horizon" cx="72%" cy="50%" r="60%">
    <stop offset="0%" stop-color="#6d8fa0" stop-opacity="0.55"/>
    <stop offset="100%" stop-color="#6d8fa0" stop-opacity="0"/>
  </radialGradient>""",
        """  <radialGradient id="rim" cx="78%" cy="22%" r="85%">
    <stop offset="0%" stop-color="#ffd79a"/>
    <stop offset="55%" stop-color="#d9924a"/>
    <stop offset="100%" stop-color="#7a4a26"/>
  </radialGradient>""",
        """  <radialGradient id="vignette" cx="50%" cy="46%" r="72%">
    <stop offset="55%" stop-color="#000000" stop-opacity="0"/>
    <stop offset="100%" stop-color="#000000" stop-opacity="0.72"/>
  </radialGradient>""",
        """  <radialGradient id="flame" cx="50%" cy="50%" r="50%">
    <stop offset="0%" stop-color="#fffaf0"/>
    <stop offset="45%" stop-color="#ffd489"/>
    <stop offset="100%" stop-color="#ff9d3c"/>
  </radialGradient>""",
        lamp_glow(322, 168, 240, "#ffb861", "#5a2f10", 0.95),
        lamp_glow(302, 392, 150, "#ffc478", "#4a2508", 0.85),
        lamp_glow(200, 300, 300, "#2c5a70", "#08131c", 0.5),
        # the bust, kept in <defs> so it is only drawn through <use>
        """  <g id="fig">
    <path d="M16 600 C24 512 54 442 100 406 C126 382 150 366 170 356 L206 352
             C240 358 268 374 292 398 C334 434 360 508 370 600 Z"/>
    <path d="M168 274 L204 274 L208 320 L164 320 Z"/>
    <path d="M146 316 C176 306 200 306 228 316 L236 344 C204 360 170 360 138 344 Z"/>
    <ellipse cx="188" cy="240" rx="35" ry="43"/>
    <ellipse cx="176" cy="226" rx="44" ry="48"/>
    <circle cx="140" cy="206" r="16"/>
    <circle cx="158" cy="184" r="17"/>
    <circle cx="190" cy="182" r="16"/>
    <circle cx="214" cy="198" r="16"/>
    <circle cx="220" cy="226" r="14"/>
    <circle cx="132" cy="242" r="15"/>
    <circle cx="116" cy="262" r="21"/>
    <circle cx="130" cy="288" r="15"/>
    <path d="M294 432 C314 414 320 390 314 370 L292 378 C296 394 290 408 276 420 Z"/>
  </g>""",
    ]
    parts.append("<svg xmlns=\"http://www.w3.org/2000/svg\" "
                 "xmlns:xlink=\"http://www.w3.org/1999/xlink\" "
                 f"width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\">")
    parts.append("  <defs>")
    parts.extend(defs)
    parts.append("  </defs>")

    # ------------------------------------------------------------ backdrop
    parts.append('  <rect width="400" height="600" fill="url(#sky)"/>')
    parts.append('  <ellipse cx="290" cy="300" rx="330" ry="260" fill="url(#horizon)"/>')

    # distant rain haze
    parts.append('  <rect width="400" height="600" fill="#9fc4dd" opacity="0.05"/>')

    # -------------------------------------------------------- city skyline
    sky = ['  <g fill="#07121b">']
    x = -12
    while x < W + 12:
        bw = rng.randint(26, 54)
        bh = rng.randint(34, 96)
        top = 352 - bh
        sky.append(f'    <rect x="{x}" y="{top:.1f}" width="{bw}" height="{bh + 70:.1f}"/>')
        # roof
        if rng.random() < 0.45:
            rw = rng.randint(8, 18)
            rx = x + rng.randint(3, max(4, bw - rw - 3))
            sky.append(f'    <rect x="{rx}" y="{top - rng.randint(8, 22):.1f}" '
                       f'width="{rw}" height="26"/>')
        # chimney
        if rng.random() < 0.5:
            cx = x + rng.randint(4, max(5, bw - 10))
            sky.append(f'    <rect x="{cx}" y="{top - 16:.1f}" width="6" height="18"/>')
        x += bw + rng.randint(2, 9)
    # far-bank tower with the wrong bell
    sky.append('    <rect x="288" y="216" width="34" height="146"/>')
    sky.append('    <rect x="296" y="200" width="18" height="18"/>')
    sky.append('    <path d="M296 200 L305 186 L314 200 Z"/>')
    sky.append("  </g>")
    # a few lit windows
    for _ in range(26):
        wx = rng.randint(4, 396)
        wy = rng.randint(250, 350)
        ww = rng.choice([3, 3, 4, 5])
        sky.append(f'  <rect x="{wx}" y="{wy}" width="{ww}" height="{ww + 1}" '
                   f'fill="#ffcf85" opacity="{rng.uniform(0.25, 0.8):.2f}"/>')
    parts.extend(sky)

    # ------------------------------------------------------------- the canal
    parts.append('  <rect x="0" y="404" width="400" height="196" fill="url(#water)"/>')
    for _ in range(46):
        wy = rng.randint(412, 596)
        wx = rng.randint(-10, 400)
        ww = rng.randint(16, 120)
        warm = rng.random() < 0.35
        col = "#ffbe74" if warm else "#5d8ea8"
        parts.append(f'  <rect x="{wx}" y="{wy}" width="{ww}" height="1.6" '
                     f'fill="{col}" opacity="{rng.uniform(0.05, 0.3):.2f}"/>')

    # --------------------------------------------------------- rain (back)
    rain_back = ['  <g stroke="#b9d7ef" stroke-linecap="round" opacity="0.5">']
    for _ in range(70):
        rx = rng.uniform(-60, 420)
        ry = rng.uniform(-40, 520)
        ln = rng.uniform(24, 64)
        rain_back.append(f'    <line x1="{rx:.1f}" y1="{ry:.1f}" '
                         f'x2="{rx - ln * 0.22:.1f}" y2="{ry + ln:.1f}" '
                         f'stroke-width="{rng.uniform(0.6, 1.2):.2f}" '
                         f'opacity="{rng.uniform(0.12, 0.4):.2f}"/>')
    rain_back.append("  </g>")
    parts.extend(rain_back)

    # ------------------------------------------------------ the street lamp
    parts.append('  <circle cx="322" cy="168" r="230" fill="url(#glow322_168)"/>')
    parts.append('  <g stroke="#1a1108" fill="#120c06">')
    parts.append('    <line x1="322" y1="60" x2="322" y2="132" stroke-width="3"/>')
    parts.append('    <path d="M306 132 L338 132 L332 146 L312 146 Z"/>')
    parts.append("  </g>")
    parts.append('  <path d="M312 146 L332 146 L336 182 L308 182 Z" fill="#1c1409"/>')
    parts.append('  <path d="M315 150 L329 150 L332 178 L312 178 Z" fill="url(#flame)"/>')
    parts.append('  <circle cx="322" cy="164" r="5" fill="#fff6e2" opacity="0.95"/>')
    parts.append('  <path d="M306 182 L338 182 L330 192 L314 192 Z" fill="#120c06"/>')

    # ----------------------------------------------------------- the figure
    # rim light: copies of the bust offset toward the street lamp
    parts.append('  <use xlink:href="#fig" href="#fig" fill="url(#rim)" '
                 'opacity="0.30" transform="translate(16,-3)"/>')
    parts.append('  <use xlink:href="#fig" href="#fig" fill="url(#rim)" '
                 'opacity="0.55" transform="translate(9,-2)"/>')
    parts.append('  <use xlink:href="#fig" href="#fig" fill="url(#rim)" '
                 'opacity="0.95" transform="translate(3,-1)"/>')
    parts.append('  <use xlink:href="#fig" href="#fig" fill="#04090e"/>')
    # the brass pin holding her hair
    parts.append('  <ellipse cx="114" cy="258" rx="7" ry="4.5" fill="#e8b878" '
                 'opacity="0.9" transform="rotate(-24 114 258)"/>')

    # her own hand-lamp, low and warm
    parts.append('  <circle cx="302" cy="392" r="150" fill="url(#glow302_392)"/>')
    parts.append('  <line x1="302" y1="352" x2="302" y2="372" stroke="#241a10" stroke-width="2.4"/>')
    parts.append('  <path d="M290 372 L314 372 L311 382 L293 382 Z" fill="#2a1e12"/>')
    parts.append('  <path d="M293 382 L311 382 L314 408 L290 408 Z" fill="#22180e"/>')
    parts.append('  <path d="M296 386 L308 386 L310 404 L294 404 Z" fill="url(#flame)"/>')
    parts.append('  <circle cx="302" cy="395" r="3.4" fill="#fff7e6"/>')
    parts.append('  <path d="M288 408 L316 408 L309 416 L295 416 Z" fill="#22180e"/>')

    # ---------------------------------------------------------- rain (front)
    rain_front = ['  <g stroke="#cfe6fa" stroke-linecap="round">']
    for _ in range(55):
        rx = rng.uniform(-40, 430)
        ry = rng.uniform(-60, 560)
        ln = rng.uniform(30, 86)
        rain_front.append(f'    <line x1="{rx:.1f}" y1="{ry:.1f}" '
                          f'x2="{rx - ln * 0.24:.1f}" y2="{ry + ln:.1f}" '
                          f'stroke-width="{rng.uniform(0.7, 1.5):.2f}" '
                          f'opacity="{rng.uniform(0.08, 0.3):.2f}"/>')
    rain_front.append("  </g>")
    parts.extend(rain_front)

    # a few bright streaks catching the lamp light
    for _ in range(14):
        rx = rng.uniform(280, 400)
        ry = rng.uniform(0, 500)
        ln = rng.uniform(30, 70)
        parts.append(f'  <line x1="{rx:.1f}" y1="{ry:.1f}" x2="{rx - ln * 0.24:.1f}" '
                     f'y2="{ry + ln:.1f}" stroke="#ffd79a" stroke-width="1" '
                     f'opacity="{rng.uniform(0.12, 0.34):.2f}"/>')

    # ------------------------------------------------------------- vignette
    parts.append('  <rect width="400" height="600" fill="url(#vignette)"/>')
    parts.append("</svg>")
    return "\n".join(parts) + "\n"


def main() -> int:
    svg = build_svg()
    svg_path = HERE / "portrait.svg"
    svg_path.write_text(svg, encoding="utf-8")

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

    # A touch of grain so the flat vector gradients do not read as plastic.
    try:
        from PIL import Image, ImageFilter
        import random as _r

        img = Image.open(png_path).convert("RGB")
        noise = Image.new("L", (W, H))
        px = noise.load()
        rnd = _r.Random(SEED)
        for yy in range(H):
            for xx in range(W):
                px[xx, yy] = rnd.randint(0, 255)
        noise = noise.filter(ImageFilter.GaussianBlur(0.4))
        grain = Image.merge("RGB", (noise, noise, noise)).point(lambda v: 128 + (v - 128) // 7)
        img = Image.blend(img, grain, 0.05)
        img.save(png_path)
    except Exception as exc:  # grain is optional
        print(f"grain pass skipped: {exc}", file=sys.stderr)

    print(f"wrote {svg_path}")
    print(f"wrote {png_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
