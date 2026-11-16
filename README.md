# Tbox

## 产品目标

打造一个跨平台的桌面工具箱，把各种日常小工具集中在一个应用里，让每个工具都能快速响应、高效执行、稳定运行。

## 当前功能

**[下载最新正式版](https://github.com/xsddpx/Tbox/releases/latest)** · Windows 11 x64（MSIX）与 macOS 14+ Apple Silicon（ZIP）· [安装与权限说明](docs/releases/INSTALL.md)

启动后，Tbox 常驻 Windows 系统托盘或 Mac 菜单栏。单击图标打开主窗口，也可以直接用全局快捷键启动常用工具。

- **文件夹跳转**　系统打开、保存和选择文件夹的对话框旁会显示跳转栏。它最多推荐三个最近使用且仍打开的目录。可按名称或路径搜索、收藏常用目录，也可打开最近跳转过的位置。按快捷键可直达首个推荐目录，跳转后保留保存对话框中已输入的文件名。Mac 需要辅助功能权限。
- **截图与标注**　在任一显示器框选区域，或选择检测到的窗口。支持箭头、画笔、文字、序号和马赛克等标注，完成后可复制、保存或贴到屏幕上。
- **贴图**　把截图或剪贴板中的图片、图片文件和文字固定在屏幕上，可拖动、缩放和裁剪。选中贴图后按空格打开标注工具栏。
- **长截图**　手动纵向滚动时实时拼接和预览，可保存、复制或贴出整张长图。
- **区域录屏**　录制选区时可暂停、继续并选择是否录制系统声音。完成后可预览并导出 MP4、GIF 或 WebP，其中 MP4 支持保留声音。
- **离线文字识别**　提取截图、贴图或长截图中的文字，结果可编辑和复制。Windows 使用系统 OCR，Mac 使用 Vision。
- **截图历史**　截图时按 `,` / `.` 查看近期截图，重启后仍可找回选区并继续标注。可在截图设置中清除历史。
- **模型服务**　在设置中管理 OpenAI 兼容连接、可用模型和默认模型。
- **翻译**　输入或粘贴文字，选择目标语言后主动开始翻译；翻译使用配置的模型服务，并可随时停止。
- **应用内更新**　在主窗口的「关于」页检查、下载并安装正式版更新。详见 [在线更新说明](docs/online-updates.md)。

以下快捷键可在「截图」和「文件夹跳转」页面修改。

| 动作 | Windows | macOS |
| --- | --- | --- |
| 截图 | `Ctrl+Shift+1` | `Cmd+Shift+1` |
| 贴出剪贴板 | `Ctrl+Shift+2` | `Cmd+Shift+2` |
| 显示或隐藏全部贴图 | `Ctrl+Shift+3` | `Cmd+Shift+3` |
| 文件夹跳转 | `Ctrl+G` | `Control+G` |

升级时，已自定义的快捷键会保留。如果 Mac 的 `Cmd+Shift+3` 触发了系统全屏截图，可在「系统设置 → 键盘 → 键盘快捷键 → 截屏」更改系统快捷键，然后重启 Tbox，也可在 Tbox 的截图页选择其他组合。参见 [Apple 截图快捷键说明](https://support.apple.com/zh-cn/guide/mac-help/mh26782/mac)。

[各版本更新说明](https://github.com/xsddpx/Tbox/releases)

[产品需求](docs/product-requirements.md) · [产品设计参考](docs/product-references.md) · [界面设计规范](docs/ui-design-system.md) · [跨平台验收](docs/cross-platform-acceptance.md) · [开发与打包](docs/cross-platform-architecture.md#本地开发与打包) · [发布历史](docs/release-history.md) · [第三方源码](docs/third-party-sources.md)
