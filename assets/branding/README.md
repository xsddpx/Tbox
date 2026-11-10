# Tbox 软件图标

`tbox.svg` 是紫晶工具盒 VI 原稿。正方形画板贴齐深色底座四边，圆角外保持透明。配套的 `src/Tbox.Desktop/Assets/AppIcon.png` 是该原稿的 1024×1024 RGBA 渲染图。

运行 `python3 scripts/generate-app-icons.py`（需要 Pillow）可从配套 PNG 生成 Windows 图块、16～256 像素的无底板 Shell 图标、七尺寸程序图标和托盘专用的 16～64 像素图帧。正方形资源直接按尺寸缩放。macOS 菜单栏模板沿用 SVG 中的 `box` 与 `mark` 轮廓，Dock 和 Finder 的 ICNS 由 Mac 打包脚本从彩色 PNG 生成。更新原稿时，应按 SVG 画板同步导出 1024×1024 RGBA PNG，再运行资源生成脚本。
