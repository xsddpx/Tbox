#!/usr/bin/env python3
"""从已确认的平面 SVG 生成 Windows/macOS 共用图标；依赖 Pillow。"""
from pathlib import Path
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets/branding/tbox.svg"
DESTINATION = ROOT / "src/Tbox.App/Assets"
NS = "{http://www.w3.org/2000/svg}"


def render(size):
    root = ET.parse(SOURCE).getroot()
    if root.attrib.get("viewBox") != "0 0 256 256":
        raise ValueError("图标画布必须为 256×256。")
    # This deliberately supports only the approved flat rectangles. A different
    # design must update its renderer instead of silently dropping SVG features.
    group = root.find(f"{NS}g")
    if group is None or group.attrib != {"fill": "#FFFFFF"} or len(group) != 3:
        raise ValueError("需要已确认的三个白色方块图标。")
    scale = size * 4 / 256
    image = Image.new("RGBA", (size * 4, size * 4), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    for rect in group:
        if rect.tag != f"{NS}rect" or set(rect.attrib) != {"x", "y", "width", "height"}:
            raise ValueError("不支持的 SVG 图形；请同步更新渲染器。")
        x, y, width, height = (float(rect.attrib[key]) for key in ("x", "y", "width", "height"))
        draw.rectangle((round(x * scale), round(y * scale), round((x + width) * scale) - 1,
                        round((y + height) * scale) - 1), fill=(255, 255, 255, 255))
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main():
    DESTINATION.mkdir(parents=True, exist_ok=True)
    for name, size in {"AppIcon.png": 1024, "StoreLogo.png": 50,
                       "Square44x44Logo.png": 44, "Square150x150Logo.png": 150}.items():
        render(size).save(DESTINATION / name, optimize=True)
    wide = Image.new("RGBA", (310, 150), (0, 0, 0, 0))
    wide.alpha_composite(render(150), (80, 0))
    wide.save(DESTINATION / "Wide310x150Logo.png", optimize=True)
    render(256).save(DESTINATION / "Tbox.ico", sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)],
                     append_images=[render(s) for s in (16, 24, 32, 48, 64, 128)])
    print(f"Generated Windows PNG/ICO and macOS source icon from {SOURCE.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
