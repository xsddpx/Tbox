# Tbox 软件图标

`tbox.svg` 是用户确认的紫晶工具盒原稿：透明画布上的深色圆角底座，等轴测紫色立方体，左侧面印白色 T。配套的 `src/Tbox.Desktop/Assets/AppIcon.png` 是该原稿的 1024×1024 透明渲染。

运行 `python3 scripts/generate-app-icons.py`（需要 Pillow）可从配套 PNG 生成 Windows 图块、16～256 像素的无底板 Shell 图标和七尺寸程序图标。托盘使用单独的 `WindowsTrayIcon.ico`，按深色底座边缘裁切并生成 16～64 像素的图帧。macOS 菜单栏模板沿用 SVG 中的 `box` 与 `mark` 轮廓，Dock 和 Finder 的 ICNS 由 Mac 打包脚本从彩色 PNG 生成。更新原稿时，应同步导出透明的 1024×1024 PNG，再运行资源生成脚本。
