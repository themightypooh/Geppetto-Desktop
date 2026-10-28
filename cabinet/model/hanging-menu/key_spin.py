import sys
from pathlib import Path
from PIL import Image

src_dir = Path(sys.argv[1])
dst_dir = Path(sys.argv[2])
dst_dir.mkdir(parents=True, exist_ok=True)
# turn=24: front face is camera at -Y = index 18, back face is +Y = index 6
order = [18, 19, 20, 21, 22, 23, 0, 1, 2, 3, 4, 5, 6]
for out_i, turn_i in enumerate(order):
    src = Image.open(src_dir / ("plaque_turn%02d.png" % turn_i)).convert("RGB")
    w, h = src.size
    bg = [src.getpixel((4, y)) for y in range(h)]
    out = Image.new("RGBA", (w, h))
    px = src.load()
    op = out.load()
    for y in range(h):
        b = bg[y]
        for x in range(w):
            c = px[x, y]
            diff = max(abs(c[i] - b[i]) for i in range(3))
            a = max(0, min(255, round((diff - 16) * 255 / 24)))
            op[x, y] = (*c, a)
    bbox = out.getchannel("A").getbbox()
    print("frame", out_i, "turn", turn_i, "bbox", bbox)
    if bbox:
        out.crop(bbox).save(dst_dir / ("spin_%02d.png" % out_i))
    else:
        out.save(dst_dir / ("spin_%02d.png" % out_i))
