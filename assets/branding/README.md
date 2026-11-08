# Tbox 软件图标

`tbox.svg` 是用户确认的紫晶 T 原稿。它在透明画布上使用深色圆角底座和紫色切面。配套的 `src/Tbox.App/Assets/AppIcon.png` 保留确认稿的 1024×1024 渲染像素。

运行 `python3 scripts/generate-app-icons.py`（需要 Pillow）可从配套 PNG 生成 Windows 图块、七尺寸 ICO 和从 SVG 的晶体轮廓生成 macOS 菜单栏模板。Mac 打包脚本从同一彩色 PNG 生成 Dock 和 Finder 使用的 ICNS。更新原稿时，应同步从 SVG 导出新的透明 1024×1024 PNG，再运行资源生成脚本。
