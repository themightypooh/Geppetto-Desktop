"""Cut the saved Effigy material render into UI layers; never creates geometry."""

from pathlib import Path

from PIL import Image


HERE = Path(__file__).resolve().parent
SOURCE = HERE / "out/hanging_menu_silver_centered_side.png"
SCALE = 0.42


def main():
    src = Image.open(SOURCE).convert("RGB")
    background = [src.getpixel((8, y)) for y in range(src.height)]

    def cut(name, box, metal):
        left, top, right, bottom = box
        output = Image.new("RGBA", (right - left, bottom - top))
        for y in range(top, bottom):
            for x in range(left, right):
                color = src.getpixel((x, y))
                difference = max(abs(color[i] - background[y][i]) for i in range(3))
                alpha = max(0, min(255, round((difference - 16) * 255 / 24)))
                if metal == "chain" and color[0] > color[1] + 18 and color[1] > color[2] + 25:
                    alpha = 0
                if metal == "hardware" and color[0] < 80:
                    alpha = 0
                output.putpixel((x - left, y - top), (*color, alpha))
        output.resize(
            (round(output.width * SCALE), round(output.height * SCALE)),
            Image.Resampling.LANCZOS,
        ).save(HERE / f"{name}.png")

    # Fixed upper mounts, moving chain spans, and lower mounts each have different motion.
    cut("upper_left", (86, 109, 119, 165), "hardware")
    cut("upper_right", (392, 109, 425, 165), "hardware")
    cut("chain_left", (86, 157, 119, 238), "chain")
    cut("chain_right", (392, 157, 425, 238), "chain")
    cut("lower_left", (86, 233, 119, 280), "hardware")
    cut("lower_right", (392, 233, 425, 280), "hardware")

    # A clean strip from the model's center makes the plaque grow with its contents.
    # The model's untouched side edges keep the actual corner/rim lighting.
    plaque = Image.new("RGBA", (150, 57))
    for py in range(plaque.height):
        sy = 254 + round(py * 134 / 56)
        for px in range(plaque.width):
            sx = 80 + px if px < 5 else 280 if px < 145 else 280 + (px - 145)
            if px >= 145:
                sx = 426 + (px - 145)
            color = src.getpixel((sx, sy))
            difference = max(abs(color[i] - background[sy][i]) for i in range(3))
            alpha = max(0, min(255, round((difference - 16) * 255 / 24)))
            plaque.putpixel((px, py), (*color, alpha))
    plaque.save(HERE / "plaque.png")


if __name__ == "__main__":
    main()
