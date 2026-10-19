"""Five original 64px pixel icons: palette-limited wood + marionette.

No gradient, vector stroke, antialias, or image library. Every mark is a
palette pixel; the timber's transition uses a 4x4 ordered dither. Run:
    python3 specs/pixel_icon_gen.py
"""
from collections import Counter
from pathlib import Path
import struct
import zlib

OUT = Path(__file__).with_name("icon-pixels")
OUT.mkdir(exist_ok=True)

# A compact, MagicWB-inspired palette; original artwork, not a traced icon.
P = {
    "clear": (0, 0, 0, 0),
    "ink": (23, 28, 36, 255),
    "shade": (65, 43, 39, 255),
    "wood0": (105, 60, 42, 255),
    "wood1": (151, 91, 52, 255),
    "wood2": (196, 129, 69, 255),
    "wood3": (231, 174, 98, 255),
    "shine": (252, 218, 146, 255),
    "cord": (220, 184, 115, 255),
    "ivory": (242, 226, 184, 255),
    "blue0": (47, 82, 91, 255),
    "blue1": (102, 153, 155, 255),
}
BAYER = ((0, 8, 2, 10), (12, 4, 14, 6), (3, 11, 1, 9), (15, 7, 13, 5))


class Pixels:
    def __init__(self):
        self.data = [[P["clear"] for _ in range(64)] for _ in range(64)]

    def put(self, x, y, colour):
        if 0 <= x < 64 and 0 <= y < 64:
            self.data[y][x] = P[colour]

    def box(self, x0, y0, x1, y1, colour):
        for y in range(y0, y1):
            for x in range(x0, x1):
                self.put(x, y, colour)

    def line(self, x0, y0, x1, y1, colour, width=1):
        dx, dy = abs(x1 - x0), abs(y1 - y0)
        sx, sy = (1 if x0 < x1 else -1), (1 if y0 < y1 else -1)
        error = dx - dy
        while True:
            for yy in range(y0 - (width - 1) // 2, y0 + width // 2 + 1):
                for xx in range(x0 - (width - 1) // 2, x0 + width // 2 + 1):
                    self.put(xx, yy, colour)
            if x0 == x1 and y0 == y1:
                break
            twice = 2 * error
            if twice > -dy:
                error -= dy
                x0 += sx
            if twice < dx:
                error += dx
                y0 += sy

    def poly(self, points, colour, alternate=None, density=0):
        x0 = max(0, min(x for x, _ in points))
        x1 = min(63, max(x for x, _ in points))
        y0 = max(0, min(y for _, y in points))
        y1 = min(63, max(y for _, y in points))
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                inside = False
                j = len(points) - 1
                for i in range(len(points)):
                    xi, yi = points[i]
                    xj, yj = points[j]
                    if (yi > y + .5) != (yj > y + .5) and x + .5 < (xj - xi) * (y + .5 - yi) / (yj - yi) + xi:
                        inside = not inside
                    j = i
                if inside:
                    pick = alternate if alternate and BAYER[y % 4][x % 4] < density else colour
                    self.put(x, y, pick)

    def outline(self, points, colour="ink"):
        for i in range(len(points)):
            a, b = points[i], points[(i + 1) % len(points)]
            self.line(*a, *b, colour)


def puppet(p):
    # Identical actor on every plate: all five studies compare the control,
    # rather than sneaking different characters into the perspective test.
    p.poly([(29, 31), (35, 31), (38, 34), (38, 39),
            (35, 42), (29, 42), (26, 39), (26, 34)], "ink")
    p.poly([(29, 33), (35, 33), (37, 35), (37, 38),
            (35, 40), (29, 40), (27, 38), (27, 35)], "ivory")
    p.box(29, 35, 31, 37, "ink")
    p.box(34, 35, 36, 37, "ink")
    p.box(30, 39, 35, 40, "wood1")
    p.poly([(28, 42), (36, 42), (39, 52), (35, 56), (29, 56), (25, 52)], "ink")
    p.poly([(29, 43), (35, 43), (37, 51), (34, 54), (30, 54), (27, 51)], "blue0", "blue1", 5)
    p.box(30, 44, 34, 46, "blue1")
    p.box(31, 48, 34, 50, "wood3")
    for a, b in (((27, 44), (21, 48)), ((37, 44), (43, 48)), ((30, 55), (28, 60)), ((34, 55), (36, 60))):
        p.line(*a, *b, "ink", 4)
        p.line(*a, *b, "ivory", 2)
    p.box(18, 47, 22, 51, "ink")
    p.box(19, 47, 21, 49, "wood3")
    p.box(42, 47, 46, 51, "ink")
    p.box(43, 47, 45, 49, "wood3")


def timber(p, pose):
    # Hand-shaped blades from the Nielsen/Slavík controls: tips lift, the
    # center falls slightly, and the grip has a waisted, rounded profile.
    # A polygon at this resolution IS a deliberate staircase of pixels.
    a, b = pose["rocker"]
    ac, bc = a[1], b[1]
    rocker = [(a[0], ac + 1), (a[0] + 2, ac - 1), (a[0] + 5, ac),
              (b[0] - 5, bc), (b[0] - 2, bc - 1), (b[0] + 1, bc + 1),
              (b[0], bc + 4), (a[0] + 1, ac + 4)]
    p.poly(rocker, "wood1")
    p.outline(rocker)
    p.line(a[0] + 3, ac, b[0] - 3, bc, "wood3")
    p.line(a[0] + 4, ac + 3, a[0] + 8, ac + 3, "wood0")
    p.line(b[0] - 9, bc + 3, b[0] - 5, bc + 3, "wood0")
    p.put(a[0] + 2, ac + 1, "shine")
    p.put(b[0] - 1, bc + 1, "shine")

    # Main transverse bar: a carved wing shape rather than a squared plank.
    # The undercut and end-grain are darker, but the upper face is flat
    # palette colours with an ordered pixel transition (not a gradient).
    left, right = pose["left"], pose["right"]
    ly, ry = left[1], right[1]
    mid = (ly + ry) // 2
    raised = max(1, pose["depth"] - 1)
    outline = [(left[0], ly + 2), (left[0] + 1, ly - 1),
               (left[0] + 3, ly - 2), (left[0] + 6, ly),
               (left[0] + 13, ly + 2), (31, mid + 3),
               (right[0] - 13, ry + 2), (right[0] - 6, ry),
               (right[0] - 3, ry - 2), (right[0] - 1, ry - 1),
               (right[0], ry + 2), (right[0] - 2, ry + raised + 4),
               (right[0] - 6, ry + raised + 5), (31, mid + raised + 6),
               (left[0] + 6, ly + raised + 5), (left[0] + 2, ly + raised + 4)]
    p.poly(outline, "wood1")
    p.outline(outline)
    # A bright stepped crown follows the bow, leaving the carved side dark.
    crown = outline[1:10] + [(right[0] - 3, ry + raised),
                              (31, mid + raised + 2),
                              (left[0] + 3, ly + raised)]
    p.poly(crown, "wood3")
    # Short, directional grain clusters. They follow the bowed timber rather
    # than filling it with an all-over checkerboard. A few isolated dither
    # pixels bridge the lit face and underside, as in hand-pixelled logs.
    p.line(left[0] + 8, ly + raised + 2, left[0] + 15, ly + raised + 3, "wood1")
    p.line(left[0] + 18, ly + raised + 4, left[0] + 22, ly + raised + 4, "wood2")
    p.line(right[0] - 20, ry + raised + 4, right[0] - 14, ry + raised + 3, "wood1")
    p.line(right[0] - 11, ry + raised + 2, right[0] - 7, ry + raised + 2, "wood2")
    p.line(left[0] + 6, ly + raised, left[0] + 11, ly + raised + 1, "wood1")
    p.line(left[0] + 13, ly + raised + 1, left[0] + 17, ly + raised + 2, "wood1")
    p.line(right[0] - 16, ry + raised + 1, right[0] - 9, ry + raised, "wood1")
    p.line(right[0] - 26, ry + raised + 3, right[0] - 23, ry + raised + 3, "wood2")
    p.line(left[0] + 7, ly + raised + 4, left[0] + 13, ly + raised + 5, "wood0")
    p.line(right[0] - 15, ry + raised + 5, right[0] - 8, ry + raised + 4, "wood0")
    for x, y in ((left[0] + 12, ly + raised + 1), (left[0] + 17, ly + raised + 3),
                 (right[0] - 17, ry + raised + 3), (right[0] - 10, ry + raised + 1)):
        if BAYER[y % 4][x % 4] < 10:
            p.put(x, y, "wood2")
    # Cut end grain: light oval rim and a dark pith at each rounded tip.
    for x, y in ((left[0] + 3, ly + raised), (right[0] - 3, ry + raised)):
        p.put(x, y - 1, "shine")
        p.put(x - 1, y, "wood3")
        p.put(x + 1, y, "wood3")
        p.put(x, y + 1, "wood2")
        p.put(x, y, "wood0")
    # Asymmetric knots like a small eye in the grain, away from the join.
    kx, ky = left[0] + 19, ly + raised + 2
    p.put(kx - 1, ky, "wood2")
    p.put(kx, ky - 1, "wood2")
    p.put(kx, ky, "wood0")
    p.put(kx + 1, ky, "wood2")
    p.put(kx + 2, ky + 1, "wood1")

    # Waisted longitudinal handle with softly rounded pixel ends.
    hx, hy = pose["handle"]
    grip = [(hx - 2, hy - 3), (hx + 2, hy - 3), (hx + 4, hy - 1),
            (hx + 4, hy + 4), (hx + 3, hy + 7), (hx + 4, hy + 11),
            (hx + 3, hy + 15), (hx + 1, hy + 17), (hx - 2, hy + 17),
            (hx - 4, hy + 15), (hx - 4, hy + 11), (hx - 3, hy + 7),
            (hx - 4, hy + 4), (hx - 4, hy - 1)]
    p.poly(grip, "wood2")
    p.outline(grip)
    p.box(hx - 2, hy - 1, hx + 2, hy + 1, "wood3")
    p.box(hx - 2, hy + 14, hx + 2, hy + 16, "wood0")
    p.line(hx - 2, hy + 3, hx - 2, hy + 7, "wood3")
    p.line(hx + 2, hy + 8, hx + 2, hy + 12, "wood1")
    p.box(hx - 1, hy + 5, hx + 1, hy + 7, "wood0")
    p.put(hx - 1, hy + 5, "shine")

    # Tiny eyelets at the hand strings, plus the rocker pivot.
    p.put(left[0] + 2, ly + pose["depth"] + pose["face"] - 1, "shine")
    p.put(right[0] - 4, ry + pose["depth"] + pose["face"] - 1, "shine")
    p.box(hx - 1, pose["rocker"][0][1] + 1, hx + 1, pose["rocker"][0][1] + 3, "shade")


POSES = {
    "front": {"left": (10, 16), "right": (55, 16), "rocker": ((22, 5), (42, 5)), "depth": 3, "face": 5, "handle": (32, 7), "label": "front / level"},
    "forward": {"left": (12, 14), "right": (52, 14), "rocker": ((19, 4), (45, 4)), "depth": 8, "face": 4, "handle": (32, 4), "label": "nose down / top face"},
    "back": {"left": (9, 14), "right": (56, 14), "rocker": ((26, 7), (38, 7)), "depth": 1, "face": 8, "handle": (32, 11), "label": "nose up / underside"},
    "left": {"left": (10, 12), "right": (51, 20), "rocker": ((22, 4), (39, 8)), "depth": 4, "face": 5, "handle": (29, 8), "label": "yaw left / right end recedes"},
    "right": {"left": (13, 20), "right": (54, 12), "rocker": ((25, 8), (42, 4)), "depth": 4, "face": 5, "handle": (35, 8), "label": "yaw right / left end recedes"},
}


def png(path, data):
    height, width = len(data), len(data[0])
    rows = b"".join(b"\0" + b"".join(bytes(pixel) for pixel in row) for row in data)
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def reduce(data, factor):
    # Palette-preserving block vote; avoid blur/resampling/extra colours.
    result = []
    for y in range(0, 64, factor):
        row = []
        for x in range(0, 64, factor):
            block = [data[yy][xx] for yy in range(y, y + factor) for xx in range(x, x + factor)]
            row.append(Counter(block).most_common(1)[0][0])
        result.append(row)
    return result


def tiny(pose):
    """A separately drawn 16px fallback: no disappearing 1px strings."""
    a = [[P["clear"] for _ in range(16)] for _ in range(16)]
    def mark(x, y, colour):
        if 0 <= x < 16 and 0 <= y < 16:
            a[y][x] = P[colour]
    left_y = 3 if pose["left"][1] <= 13 else 4
    right_y = 3 if pose["right"][1] <= 13 else 4
    if pose["left"][1] < pose["right"][1] - 4:
        right_y = 5
    if pose["right"][1] < pose["left"][1] - 4:
        left_y = 5
    for x in range(2, 14):
        y = round(left_y + (right_y - left_y) * (x - 2) / 11)
        mark(x, y, "wood3")
        mark(x, y + 1, "wood1")
    for y in range(2, 7):
        mark(7, y, "wood2")
        mark(8, y, "wood1")
    for y in range(left_y + 2, 11):
        mark(3, y, "cord")
    for y in range(right_y + 2, 11):
        mark(12, y, "cord")
    for y in range(6, 9):
        mark(8, y, "cord")
    for x in (7, 8):
        for y in (8, 9):
            mark(x, y, "ivory")
    for x in (6, 7, 8, 9):
        for y in (10, 11, 12):
            mark(x, y, "blue0")
    mark(7, 11, "wood3")
    for x in (4, 5, 10, 11):
        mark(x, 11, "ivory")
    for x in (3, 12):
        mark(x, 10, "wood3")
    mark(6, 13, "ivory")
    mark(9, 13, "ivory")
    return a


for name, pose in POSES.items():
    image = Pixels()
    # strings behind wood and hands; shadow pixel next to the gold thread.
    lx, ly = pose["left"]
    rx, ry = pose["right"]
    for x0, y0, x1, y1 in ((lx + 3, ly + pose["depth"] + pose["face"], 20, 47),
                            (rx - 4, ry + pose["depth"] + pose["face"], 44, 47),
                            (pose["handle"][0] - 4, pose["handle"][1] + 17, 28, 34),
                            (pose["handle"][0] + 4, pose["handle"][1] + 17, 36, 34)):
        image.line(x0 + 1, y0, x1 + 1, y1, "shade")
        image.line(x0, y0, x1, y1, "cord")
    puppet(image)
    timber(image, pose)
    for size, factor in ((64, 1), (32, 2), (16, 4)):
        png(OUT / f"control-{name}-{size}.png", image.data if factor == 1 else tiny(pose) if size == 16 else reduce(image.data, factor))
    print(name, pose["label"])
