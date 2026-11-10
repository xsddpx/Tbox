# 跨平台实现与验收

## 目标与基线

2026-11-07：最终目标是 Windows 与 macOS 对齐当前全部已实现功能。先交付可用的 Mac 基础版，再补齐全部行为。产品要求只在 `product-requirements.md` 维护，本文记录技术选择及验证证据，不改变功能语义。

原平台基线为开始迁移时的 Windows 1.0.45（提交 `0f88802`，可用 `git show` 查看原 WinUI 实现）。共用版已取代原 WinUI 工程，后者已从仓库删除；Windows 适配沿用的资源管理器跟踪、文件对话框导航和录制器源码已移入 `Platform/windows`。

## 共用实现

- `Tbox.Core`：继续复用标注命令、撤销/重做、区域映射、拖动控制点、贴图缩放/裁剪、长截图拼接、录屏参数、缓存与设置规则。
- `Tbox.Desktop`：Avalonia 11.3.22 管理的跨平台界面、输入交互、截图会话、贴图窗口、结果窗、菜单、剪贴板、文件选择器和图像渲染。平台代码不得复制这些工作流。
- `Platform/IPlatformServices.cs`：截图原始像素、识别、系统快捷键及特殊桌面窗口行为的边界。图像使用 PNG；桌面位置使用左上原点的系统桌面单位（Mac 为点、Windows 为物理像素），适配层提供 DIP 换算，输出图像始终使用原始像素坐标。
- `Platform/macos`：macOS 自身提供的 ScreenCaptureKit、Vision、全局快捷键以及跨 Space 浮窗适配。桥接库与主应用同进程，不单独保存整屏图像或识别文字。
- `Platform/windows`：Win32 原始像素捕获、窗口位置与全局快捷键（默认 F6/F7/F8/Ctrl+G）；系统 OCR 使用 Windows.Media.Ocr，仍要求 MSIX 包身份和已安装语言。共用工程在 Windows 自动选择 Windows TFM，也可在 Mac 以 `-p:TboxWindows=true` 编译检查。
- `tests/Tbox.Core.Tests`、`tests/Tbox.Desktop.Tests`：两平台共用规则和实际渲染测试；系统权限及真实桌面集成另做本机验证。

采用 Avalonia 是为了延续 C# 核心并共用桌面交互，不另写 SwiftUI 或 WinUI 业务界面。已有 .NET 8 和 SDK 9 构建基线暂不同时升级，框架升级应作为独立变更。

## 平台依赖选择

| 能力 | 共用部分 | 必要的平台适配 |
| --- | --- | --- |
| 主窗口、结果窗、菜单栏/托盘 | Avalonia | 平台窗口惯例 |
| 剪贴板图片/文件/文字、复制、文件选择器 | Avalonia | 使用框架后端 |
| 截图选区、标注、贴图缩放和裁剪 | C# + 既有 Core 规则 | 原始像素捕获、特殊置顶行为 |
| OCR | 请求生命周期、原图、编辑、行号、复制 | macOS Vision；Windows 系统 OCR |
| 长截图 | 稳定帧与双向拼接、预览 | 实时取帧 |
| 录屏 | 状态机、时间、参数、导出工作流 | 屏幕与系统音频捕获、编码器 |
| 文件夹跳转 | 最近可信目录、校验、失败反馈 | Finder / Explorer 与各自标准文件对话框 |

评估过 SharpHook：它可跨平台处理全局键盘事件，但 macOS 需要辅助功能权限。全局快捷键仍用小范围系统注册接口（Windows `RegisterHotKey`、Mac Carbon `RegisterEventHotKey`），不额外监听全部键盘事件。组合键模型、默认值、校验与原生键码在 `Tbox.Core/ShortcutPolicy.cs`；`IPlatformServices.RegisterHotkey/UnregisterHotkey` 按动作逐项注册，录制器只在主窗口内读取按键。macOS 使用独占注册以检测系统报告的独占冲突，Carbon 仍可能接受其他进程以共享方式注册的组合。

## 权限与发布

屏幕捕获缺少授权时只显示说明；仅显式授权按钮可以申请屏幕录制权限，每次运行最多一次。已有图片的 OCR 和贴图不依赖此权限。macOS 使用登录钥匙串中持久的 `Tbox Local Development` 自签代码签名身份，固定 Bundle ID `com.xsddpx.tbox` 和安装位置 `~/Applications/Tbox.app`；不使用随二进制变化的 ad hoc 身份，不改变系统根证书信任。私钥不进仓库、不分发。签名探针已验证指定要求为标识符和稳定证书根哈希；本机连续升级后的屏幕权限已验证，见 `macos-permission-upgrade-verification.md`。此签名不等于 Apple Developer ID 或公证。Windows 版本使用持久证书签名 MSIX，并按相同身份覆盖升级。

跨平台代码、编译通过、模拟测试通过、本机实际验收是不同状态，必须分别记录。Intel Mac、混合缩放多屏和 Windows 实机没有证据时标记未验证。

权限升级验收：先从 Finder 或正常应用启动方式打开固定安装位置的 Tbox，再运行 `~/Applications/Tbox.app/Contents/MacOS/Tbox.Desktop --permission-probe`。命令通过仅当前用户可用的命名管道查询已运行应用，由该应用进程检查权限并在内存中取帧；输出只有进程、路径、版本、授权状态和图像尺寸。禁止以直接启动测试进程继承到的终端/Codex 授权代替此结果。每次覆盖升级前后保存探测结果、应用二进制哈希与签名要求，至少比较两个不同构建；探测不会请求或重置授权，不保存屏幕图像。

截图检测在打开遮罩前冻结窗口顺序并排除自身进程；界面元素查询使用 Windows MSAA / macOS AX。Mac 仅在切换元素模式时请求辅助功能权限，拒绝时回退窗口检测。查询在后台进行，丢弃过期结果，不得覆盖手动调整或已有标注；窗口列表与元素边界不写入磁盘。

长截图共用 `ScrollCaptureStitcher` 与稳定帧缓冲；macOS 区域采集通过 ScreenCaptureKit 排除当前应用，Windows 控制窗设置捕获排除并让区域边框穿透鼠标。预览与输出采用同一套 Avalonia 界面。真实连续滚动、音视频录制仍未验收。

录屏新增 `IRegionRecorder` 小型平台边界：Windows 沿用原 `ScreenRegionRecorder`，Mac 以 ScreenCaptureKit 取屏幕/系统输出、AVAssetWriter 编码 H.264。`RecordingOperation` 统一准备、音频降级、暂停计时、取消与文件所有权转移；取消等待原生写入退出，不提前删除母版。回放使用 LibVLCSharp.Avalonia；首次媒体初始化放到后台，关闭按停止、卸载视图、关闭窗口、释放媒体、删除会话的顺序执行。GIF/WebP 在应用临时目录转换，成功后使用目标目录的临时文件原子替换，取消不会破坏用户已有文件。

文件夹跳转通过 `IFolderNavigation` 接入共用模块开关和 Ctrl+G。Windows 沿用原 `ExplorerFolderTracker`、`ExplorerShellViewResolver`、`FileDialogNavigator`，避免重写已验证的活动标签逻辑。Mac 使用辅助功能接口观察 Finder 的选中窗口。窗口未提供目录地址时，从同一窗口的图标或列表视图读取文件 URL，要求父目录一致并与窗口标题相符。无法核实的目录保留上一次可信记录。系统文件面板可能以宿主应用的辅助功能节点呈现。Tbox 用实际 AppKit 服务控件核实面板来源，仅向该服务进程发送导航按键。确认“前往文件夹”子面板获得焦点并选中完整目标路径后才提交，再检查原面板已显示目标目录。失败仅在当前状态区反馈。模块设置只在热键注册完整成功后保存，失败会重新注册上一组热键。

CI 覆盖 Windows/macOS 的核心与共用 UI 测试；CI 测试不代替屏幕授权、真实文件对话框、混合 DPI 和安装升级验收。

## 主要官方依据

- [Avalonia 11 平台支持](https://v11.docs.avaloniaui.net/docs/overview/supported-platforms/)
- [Avalonia 剪贴板 API](https://docs.avaloniaui.net/docs/how-to/clipboard-how-to)
- [Apple ScreenCaptureKit](https://developer.apple.com/documentation/screencapturekit)
- [Apple Vision 文字识别](https://developer.apple.com/documentation/vision/recognizing-text-in-images)
- [Apple AX 元素坐标查询](https://developer.apple.com/documentation/applicationservices/1462077-axuielementcopyelementatposition)
- [Windows OCR 的应用身份要求](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr?view=winrt-26100)
- [SharpHook 平台权限约束](https://sharphook.tolik.io/articles/os-constraints.html)

## Windows 共用版打包与设置升级

`Platform/windows/Package.appxmanifest` 保留原包名、发布者、App ID 和 StartupTask ID，改为共用桌面入口。`package-desktop-windows.ps1` 在 Windows 使用自包含发布与 SDK MakeAppx（不跳过语义校验），检查版本、原生运行库和许可文件，并针对实际发布目录做媒体解码/导出后解包再次检查。正式模式要求显式提供原有证书指纹，签名验证后调用安装脚本；安装脚本校验包身份、已有发布者及签名，再覆盖安装并通过包身份运行离线 OCR、像素裁剪与界面渲染检查。只有通过后才登记最终包和哈希。

CI 的 `-VerifyOnly` 不签名、不导入证书、不安装、不发布完成版；仅保留验证 JSON 报告。默认正式模式不会悄悄降级成未签名包。安装、启动项、屏幕集成与升级保留数据仍需 Windows 实机验证。

`SettingsMigration` 仅在 Windows 正常启动且 `desktop-settings.json` 缺失时尝试导入 `settings.json`。校验有效 JSON 后通过同目录临时文件和不覆盖重命名落盘，保留旧文件原始字节；已有新版设置优先，损坏/无权限的旧配置不删除且返回非致命状态。系统开机启动状态单独读取，不把旧配置字段当作系统授权。

官方机制：[MakeAppx 打包和解包](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool)、[包身份下运行验证进程](https://learn.microsoft.com/en-us/powershell/module/appx/invoke-commandindesktoppackage?view=windowsserver2025-ps)。

## 本地开发与打包

在仓库根目录运行命令。使用 `global.json` 固定的 .NET SDK、.NET 8 运行时和 Python 3。交付安装包自带 .NET 运行时。

```sh
dotnet test tests/Tbox.Core.Tests/Tbox.Core.Tests.csproj -c Release
dotnet test tests/Tbox.Desktop.Tests/Tbox.Desktop.Tests.csproj -c Release
python3 -m unittest discover -s tests/release -v
```

运行桌面测试时设置 `TBOX_UI_PREVIEWS=<目录>` 可导出真实控件的渲染预览。`tests/Tbox.FolderSmoke` 和 `tests/Tbox.MediaSmoke` 分别用于 Windows 文件对话框集成检查与媒体解码、导出检查。构建及验证的中间产物放在 Git 忽略的 `TEMP/`。

### macOS

需要 Apple Command Line Tools、`make` 和 `pkg-config`（`brew install pkgconf`）。首次构建会校验并下载固定版本的媒体源码，构建 FFmpeg 与 libwebp 并准备 VLC 回放插件。后续构建复用 `TEMP/` 中的媒体依赖。SDK 不在 PATH 时可设置 `TBOX_DOTNET=/完整路径/dotnet`。

```bash
bash scripts/setup-macos-signing.sh   # 仅首次配置持久本地签名身份
bash scripts/build-macos.sh osx-arm64
bash scripts/package-macos.sh osx-arm64
bash scripts/install-macos.sh
```

构建产物为 `TEMP/build/osx-arm64/Tbox.app`。打包脚本运行测试、构建、签名和验签，启动应用验证离线 OCR 与像素裁剪，再在 `artifacts/` 生成 ZIP 并登记哈希。安装脚本在 `~/Applications/Tbox.app` 覆盖已有应用前校验签名要求。升级后的权限检查见 [权限升级验证](macos-permission-upgrade-verification.md)。

媒体检查使用合成视频。准备好媒体运行库后运行以下命令。

```sh
dotnet run --project tests/Tbox.MediaSmoke/Tbox.MediaSmoke.csproj -c Release -- assets/media-test/fixture.mp4 TEMP/media-smoke TEMP/media-runtime/FFmpeg/bin/ffmpeg TEMP/media-runtime/VLC
```

### Windows

需要 Windows 11 x64 与 Windows SDK 10.0.26100。将 CI 的 `windows-media-runtime` 产物解压到 `TEMP/media-runtime/windows` 并运行 `python scripts/verify-windows-media.py` 校验。也可在 Linux 上通过 `bash scripts/prepare-windows-media.sh` 从固定源码构建。Windows 上默认选择 Windows 目标。在 Mac 上准备好同一套 Windows 媒体依赖后，可用 `dotnet build src/Tbox.Desktop/Tbox.Desktop.csproj -c Release -p:TboxWindows=true` 检查 Windows 目标编译。

```powershell
# 验证版本、运行库、媒体导出及 MSIX 结构
.\scripts\package-desktop-windows.ps1 -VerifyOnly

# 复用已有持久证书签名、安装并验证
.\scripts\package-desktop-windows.ps1 -CertificateThumbprint '<证书指纹>'

# 安装受信任的签名包，可用 -DependencyPath 提供 VCLibs 框架包
.\scripts\install-desktop-windows.ps1 -PackagePath '.\artifacts\Tbox_<版本>_win-x64.msix'

# 标准文件对话框集成检查在 GitHub 托管 Windows runner 上执行
.\scripts\test-windows-folders.ps1

# 使用合成视频验证解码和导出
dotnet run --project tests/Tbox.MediaSmoke/Tbox.MediaSmoke.csproj -c Release -- assets/media-test/fixture.mp4 TEMP/media-smoke TEMP/media-runtime/windows/bin/ffmpeg.exe
```

首次安装需要信任证书时，按 [安装与权限说明](releases/INSTALL.md#windows) 处理。

### 正式发布

同步更新项目文件、Windows 包清单与 macOS Info.plist 的版本号，每个交付包使用从未打包过的新版本。代码合入主分支后，通过推送版本标签或手动运行发布工作流触发双平台构建、签名和验收。具体流程见 [自动发布](automatic-releases.md)，包的打包时间和 SHA-256 见 [发布历史](release-history.md)。
