# 跨平台实现与验收

## 目标与基线

2026-11-07：最终目标是 Windows 与 macOS 对齐当前全部已实现功能。先交付可用的 Mac 基础版，再补齐全部行为。产品要求只在 `product-requirements.md` 维护，本文记录技术选择及验证证据，不改变功能语义。

原平台基线为开始迁移时的 `origin/main`，Windows 1.0.45。`Tbox.App` 保留为现有 WinUI 实现和行为参照。迁移完成前，不能用新工程可编译替代 Windows 功能回归。

## 共用实现

- `Tbox.Core`：继续复用标注命令、撤销/重做、区域映射、拖动控制点、贴图缩放/裁剪、长截图拼接、录屏参数、缓存与设置规则。
- `Tbox.Desktop`：Avalonia 11.3.22 管理的跨平台界面、输入交互、截图会话、贴图窗口、结果窗、菜单、剪贴板、文件选择器和图像渲染。平台代码不得复制这些工作流。
- `Platform/IPlatformServices.cs`：截图原始像素、识别、系统快捷键及特殊桌面窗口行为的边界。图像使用 PNG；桌面位置使用左上原点的系统桌面单位（Mac 为点、Windows 为物理像素），适配层提供 DIP 换算，输出图像始终使用原始像素坐标。
- `Platform/macos`：macOS 自身提供的 ScreenCaptureKit、Vision、全局快捷键以及跨 Space 浮窗适配。桥接库与主应用同进程，不单独保存整屏图像或识别文字。
- `Platform/windows`：Win32 原始像素捕获、窗口位置与 F6/F7/F8；系统 OCR 使用 Windows.Media.Ocr，仍要求 MSIX 包身份和已安装语言。共用工程在 Windows 自动选择 Windows TFM，也可在 Mac 以 `-p:TboxWindows=true` 编译检查。
- `tests/Tbox.Core.Tests`、`tests/Tbox.Desktop.Tests`：两平台共用规则和实际渲染测试；系统权限及真实桌面集成另做本机验证。

采用 Avalonia 是为了延续 C# 核心并共用桌面交互，不引入第二套 SwiftUI 或 WinUI 业务界面。已有 .NET 8 和 SDK 9 构建基线暂不同时升级，框架升级应作为独立变更。

## 平台依赖选择

| 能力 | 共用部分 | 必要的平台适配 |
| --- | --- | --- |
| 设置、主页、结果窗、菜单栏/托盘 | Avalonia | 平台窗口惯例 |
| 剪贴板图片/文件/文字、复制、文件选择器 | Avalonia | 使用框架后端 |
| 截图选区、标注、贴图缩放和裁剪 | C# + 既有 Core 规则 | 原始像素捕获、特殊置顶行为 |
| OCR | 请求生命周期、原图、编辑、行号、复制 | macOS Vision；Windows 系统 OCR |
| 长截图 | 稳定帧与双向拼接、预览 | 实时取帧 |
| 录屏 | 状态机、时间、参数、导出工作流 | 屏幕与系统音频捕获、编码器 |
| 文件夹跳转 | 最近可信目录、校验、失败反馈 | Finder / Explorer 与各自标准文件对话框 |

评估过 SharpHook：它可跨平台处理全局键盘事件，但 macOS 需要辅助功能权限。现阶段只需注册三个全局快捷键，用小范围系统注册接口即可；不为统一依赖额外监听全部键盘事件。未来快捷键录制器仍应复用共用模型。

## 权限与发布

屏幕捕获只在用户触发截图时申请屏幕录制权限。已有图片的 OCR 和贴图不依赖此权限。macOS 使用登录钥匙串中持久的 `Tbox Local Development` 自签代码签名身份，固定 Bundle ID `com.xsddpx.tbox` 和安装位置 `~/Applications/Tbox.app`；不使用随二进制变化的 ad hoc 身份，不改变系统根证书信任。私钥不进仓库、不分发。签名探针已验证指定要求为标识符和稳定证书根哈希；实际连续升级后的权限保留仍需验收。此签名不等于 Apple Developer ID 或公证。Windows 版本继续遵守原有 MSIX 签名、测试和覆盖升级流程。

当前迁移工程尚在实现；跨平台代码、编译通过、模拟测试通过、本机实际验收是不同状态，必须分别记录。Intel Mac、混合缩放多屏和 Windows 实机没有证据时标记未验证。

截图检测在打开遮罩前冻结窗口顺序并排除自身进程；界面元素查询使用 Windows MSAA / macOS AX。Mac 仅在切换元素模式时请求辅助功能权限，拒绝时回退窗口检测。查询在后台进行，丢弃过期结果，不得覆盖手动调整或已有标注；窗口列表与元素边界不写入磁盘。

本轮已完成 Windows 目标交叉编译和 Swift 桥接编译。CI 配置覆盖 Windows/macOS 的核心与共用 UI 测试；CI 测试不代替屏幕授权、真实文件对话框、混合 DPI 和安装升级验收。

## 主要官方依据

- [Avalonia 11 平台支持](https://v11.docs.avaloniaui.net/docs/overview/supported-platforms/)
- [Avalonia 剪贴板 API](https://docs.avaloniaui.net/docs/how-to/clipboard-how-to)
- [Apple ScreenCaptureKit](https://developer.apple.com/documentation/screencapturekit)
- [Apple Vision 文字识别](https://developer.apple.com/documentation/vision/recognizing-text-in-images)
- [Apple AX 元素坐标查询](https://developer.apple.com/documentation/applicationservices/1462077-axuielementcopyelementatposition)
- [Windows OCR 的应用身份要求](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr?view=winrt-26100)
- [SharpHook 平台权限约束](https://sharphook.tolik.io/articles/os-constraints.html)
