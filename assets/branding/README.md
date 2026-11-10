# Tbox 软件图标

`tbox.svg` 是用户确认的紫晶工具盒原稿：透明画布上的深色圆角底座，等轴测紫色立方体，左侧面印白色 T。配套的 `src/Tbox.Desktop/Assets/AppIcon.png` 是该原稿的 1024×1024 透明渲染。

运行 `python3 scripts/generate-app-icons.py`（需要 Pillow）可从配套 PNG 生成 Windows 图块和七尺寸 ICO，并用 SVG 中 `box` 与 `mark` 两条直线轮廓生成镂空 T 的 macOS 菜单栏模板。Mac 打包脚本从同一彩色 PNG 生成 Dock 和 Finder 使用的 ICNS。更新原稿时，应同步从 SVG 导出新的透明 1024×1024 PNG，再运行资源生成脚本。
