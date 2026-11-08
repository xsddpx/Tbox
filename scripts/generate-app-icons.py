#!/usr/bin/env python3
"""Generate platform icon sizes from the approved SVG and its raster export."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets/branding/tbox.svg"
DESTINATION = ROOT / "src/Tbox.App/Assets"
MENU_ICON = ROOT / "src/Tbox.Desktop/Platform/macos/menu-bar-icon.png"
NS = "{http://www.w3.org/2000/svg}"


def menu_icon(svg):
    # macOS tints the template alpha mask. Use only the approved crystal outline
    # so the dark app tile does not become a solid block in the menu bar.
    crystal = svg.find(f".//{NS}path[@id='crystal']")
    outline = crystal.get("d", "") if crystal is not None else ""
    if not re.fullmatch(r"M\d+ \d+(?:L\d+ \d+){2,}Z", outline):
        raise ValueError("图标原稿须提供由直线组成的晶体轮廓。")
    points = [(int(x), int(y)) for x, y in re.findall(r"(\d+) (\d+)", outline)]
    xs, ys = zip(*points)
    center_x = (min(xs) + max(xs)) / 2
    center_y = (min(ys) + max(ys)) / 2
    scale = 224 / max(max(xs) - min(xs), max(ys) - min(ys))
    mask = Image.new("RGBA", (256, 256))
    ImageDraw.Draw(mask).polygon(
        [(128 + (x - center_x) * scale, 128 + (y - center_y) * scale) for x, y in points],
        fill=(0, 0, 0, 255),
    )
    return mask.resize((64, 64), Image.Resampling.LANCZOS)


def main():
    svg = ET.parse(SOURCE).getroot()
    if svg.get("viewBox") != "0 0 1024 1024":
        raise ValueError("图标原稿画布须为 1024×1024。")
    template = menu_icon(svg)
    # The full-size export preserves the SVG gradients and drop shadows exactly
    # as reviewed. Pillow only resizes those pixels for the platform assets.
    with Image.open(DESTINATION / "AppIcon.png") as source:
        if source.size != (1024, 1024) or source.mode != "RGBA" or source.getchannel("A").getextrema() != (0, 255):
            raise ValueError("AppIcon.png 须为具有透明边缘的 1024×1024 已确认渲染图。")

        def render(size):
            return source.resize((size, size), Image.Resampling.LANCZOS)

        for name, size in {"StoreLogo.png": 50, "Square44x44Logo.png": 44, "Square150x150Logo.png": 150}.items():
            render(size).save(DESTINATION / name, optimize=True)
        wide = Image.new("RGBA", (310, 150))
        wide.alpha_composite(render(150), (80, 0))
        wide.save(DESTINATION / "Wide310x150Logo.png", optimize=True)
        sizes = (16, 24, 32, 48, 64, 128, 256)
        render(256).save(
            DESTINATION / "Tbox.ico", sizes=[(size, size) for size in sizes],
            append_images=[render(size) for size in sizes[:-1]],
        )
    template.save(MENU_ICON, optimize=True)
    print("Generated Windows PNG/ICO and macOS menu bar template.")


if __name__ == "__main__":
    main()
