# Tbox

Tbox 是面向 Windows 和 macOS 的桌面效率工具，提供文件夹跳转、截图、标注、贴图、长截图、区域录屏和离线文字识别。两平台共用同一个 Avalonia 应用 `src/Tbox.Desktop`。

**[下载最新正式版](https://github.com/xsddpx/Tbox/releases/latest)** · Windows 11 x64 / macOS 14+ Apple Silicon · 无需登录。首次安装见 [安装与权限说明](docs/releases/INSTALL.md)。

## 功能

Tbox 启动后只驻留系统托盘（Mac 为菜单栏），不弹出窗口。单击托盘图标打开主窗口：左侧侧栏切换截图、文件夹跳转、通用和关于；截图页集中截图、贴出剪贴板、长截图、录屏、识别文字和最近 4 张截图，单击缩略图打开图片预览。普通截图保留主窗口原有的显示状态；单独启动长截图或录屏时主窗口先隐藏。关闭主窗口只是隐藏，快捷键照常可用。右键托盘图标的原生菜单提供“打开 Tbox”和“退出”；只有“退出”才会完全结束应用。

默认全局快捷键：

| 动作 | Windows | Mac |
| --- | --- | --- |
| 截图 | `F6` | `Control+Shift+1` |
| 贴出剪贴板 | `F7` | `Control+Shift+2` |
| 显示/隐藏全部贴图 | `F8` | `Control+Shift+3` |
| 文件夹跳转 | `Ctrl+G` | `Control+G` |

截图页和文件夹跳转页的快捷键行可单击键帽后按下新组合修改，`Esc` 取消；被其他应用占用或与其他 Tbox 快捷键重复时保留原快捷键并在该行说明原因，修改过的项可“恢复默认”。下文以 Windows 修饰键书写，Mac 上 `Ctrl+C/S/T` 对应 `Command`。

- **文件夹跳转**：记住最近使用的资源管理器或访达目录，多标签窗口以当前选中的标签为准。标准打开、保存或文件夹选择对话框位于前台时，按跳转快捷键直接切到该目录。Mac 需要辅助功能权限。
- **截图**：所有显示器作为同一个截图会话冻结画面，任意显示器框选，全局只保留一个选区。顶部提示条显示 `R` / `Tab` / `Esc` 键帽：单击采用指针下检测到的窗口，`Tab` 在窗口与界面元素检测之间切换，`R` 恢复本次运行中上一次成功截图的区域。选区四边和四角可继续拖动调整。
- **标注**：选区下方两行工具条，第 1 行为矩形、椭圆、箭头、直线、画笔、高亮、马赛克、文字、序号、橡皮擦，以及颜色、粗细和撤销/重做；第 2 行为长截图、录屏，贴图、识别文字、保存、复制，以及取消。序号默认大小 `22`，每次单击依次放置 `1`、`2`、`3`……。`Shift` 约束正方形、圆形和 45° 方向，`B`/`T` 选择画笔/文字，`1`/`2` 调整粗细，空格显示或隐藏标注工具条，文字用 `Ctrl+Enter` 提交。`Ctrl+C`（或 `Enter`）、`Ctrl+S`、`Ctrl+T` 分别复制、保存和固定最终合成图，选框与工具栏不会写入输出。
- **贴图**：支持拖动、滚轮沿指针位置缩放和 `Ctrl+C` 复制。贴出剪贴板可使用图片像素、复制的图片文件或纯文本。单击选中贴图后按空格展开工具条，第一排与截图共用全部标注工具，第二排为识别文字、保存、复制和完成。按住右键拖动可裁剪可见区域，短按右键打开菜单（恢复完整贴图 / 复制图像 / 图像另存为… / 关闭）。工具条隐藏时按 `Esc` 关闭贴图。
- **长截图**：黄色选区，由用户在框内手动上下滚动；右侧实时预览显示整张拼接图和“已拼接 N 屏”。完成后可预览、识别文字、保存、复制或贴图，只支持单显示器内纵向拼接。
- **区域录屏**：红色选区，开始前可选 5/10/15/25/60 FPS（默认 10）和系统声音（默认关闭，不录麦克风）。`Enter` 开始或停止，空格暂停，`Esc` 取消。停止后进入录屏预览，可导出 MP4 / GIF / WebP，GIF/WebP 不含声音。
- **文字识别**：截图、贴图和长截图的识别只处理原始像素，不含 Tbox 标注。全局只有一个结果窗口，再次识别会替换内容；结果可编辑，带逻辑行号、复制全部和原图展开。Windows 使用系统 OCR，需要从已安装的 MSIX 运行并安装对应识别语言；Mac 使用 Vision。识别文字不写入历史。
- **缓存**：截图默认保存在用户“图片”文件夹下的 `screenshots`，可在截图页更换。缓存按 30 天、200 个文件和 512 MB 三重上限自动清理，仍在显示的贴图文件会保留。
- **在线更新**：在主窗口“关于”页检查、下载并安装新正式版，见 [在线更新](docs/online-updates.md)。

当前不支持 Office、Adobe 等自定义文件对话框；遇到“此电脑”等非文件系统位置时回退到用户主目录。OCR 不含搜索、翻译、自动去换行或识别历史。

## 文档

- [产品需求](docs/product-requirements.md)：产品行为、交互规则和验收标准的唯一活文档。
- [产品设计参考](docs/product-references.md)：交互、布局和内容方案优先参考 PixPin、Listary 的成熟设计。
- [UI 3.0 设计规范](docs/ui-design-system.md)：主窗口、结果窗口与任务工具条的视觉和交互规则。
- [跨平台架构](docs/cross-platform-architecture.md)、[逐项验收记录](docs/cross-platform-acceptance.md)：技术选择与验证证据。
- [自动发布](docs/automatic-releases.md)、[发布历史](docs/release-history.md)、[版本说明](docs/releases/)、[第三方源码](docs/third-party-sources.md)。

## 项目结构

- `src/Tbox.Core`：平台无关的产品规则（标注、坐标、缓存、设置、快捷键策略等）。
- `src/Tbox.Desktop`：共用 Avalonia 应用；`Platform/` 为平台适配，`Platform/windows` 与 `Platform/macos` 只放系统能力差异。
- `tests/Tbox.Core.Tests`、`tests/Tbox.Desktop.Tests`：两平台共用的单元与渲染测试。
- `tests/Tbox.FolderSmoke`、`tests/Tbox.MediaSmoke`：Windows 文件对话框集成检查与媒体解码/导出检查。
- `tests/macos`、`tests/windows`、`tests/release`：原生桥接、打包策略和发布脚本测试。
- `scripts/`：构建、打包、安装和发布脚本；`assets/`：品牌原稿、媒体夹具和第三方依赖清单。
- `TEMP/`：Git 忽略的临时数据与中间产物。

## 开发

需要 `global.json` 指定的 .NET SDK 和 .NET 8 运行时。应用包自带运行时，使用者无需安装 .NET。

```sh
dotnet test tests/Tbox.Core.Tests/Tbox.Core.Tests.csproj -c Release
dotnet test tests/Tbox.Desktop.Tests/Tbox.Desktop.Tests.csproj -c Release
```

设置 `TBOX_UI_PREVIEWS=<目录>` 运行桌面测试会输出真实控件的渲染预览。

### macOS

另需 Apple Command Line Tools；媒体构建还需 `make` 与 `pkg-config`（`brew install pkgconf`）。

```bash
bash scripts/setup-macos-signing.sh   # 仅首次：创建持久本地签名身份
bash scripts/build-macos.sh osx-arm64
bash scripts/package-macos.sh osx-arm64
bash scripts/install-macos.sh
```

若 SDK 不在 PATH，可通过 `TBOX_DOTNET=/完整路径/dotnet` 指定。`scripts/prepare-macos-media.sh` 校验固定下载哈希，从源码构建 FFmpeg 8.1.2 + libwebp 1.6.0，并准备 VLC 3.0.24 的本地 MP4 回放插件；首次需要联网，之后复用 `TEMP/`。构建产物在 `TEMP/build/osx-arm64/Tbox.app`，开发包在 `artifacts/`，禁止覆盖同版本。打包会运行测试、构建并签名验签，再实际启动应用验证离线 OCR 与像素裁剪，最后记录哈希。

固定安装位置为 `~/Applications/Tbox.app`。签名身份保存在登录钥匙串，私钥不进 Git；安装脚本在覆盖前检查新包满足旧包的签名要求，不主动清空权限，普通构建禁止退回 ad hoc 签名。当前为本机开发证书签名，没有 Apple 公证。升级后先正常打开 Tbox，再运行 `~/Applications/Tbox.app/Contents/MacOS/Tbox.Desktop --permission-probe` 查询正在运行的应用的权限和取帧尺寸，见 [权限升级验证](docs/macos-permission-upgrade-verification.md)。

### Windows

需要 Windows 11 x64、Windows 11 SDK 10.0.26100 和 .NET SDK。Windows 下自动选择 Windows 目标；在 Mac 上可用 `dotnet build src/Tbox.Desktop/Tbox.Desktop.csproj -c Release -p:TboxWindows=true` 检查编译。导出运行库由 `scripts/prepare-windows-media.sh` 从固定源码构建，放在 `TEMP/media-runtime/windows`。

```powershell
# 发布自包含二进制，验证版本、运行库、媒体导出与 MSIX 结构；只在 TEMP 生成未签名验证包。
.\scripts\package-desktop-windows.ps1 -VerifyOnly

# 复用已有持久证书签名、安装并运行系统 OCR/界面验证，通过后记录版本和哈希。
.\scripts\package-desktop-windows.ps1 -CertificateThumbprint '<证书指纹>'

# 安装已签名且在当前电脑受信任的包；可通过 -DependencyPath 传入 VCLibs 框架包。
.\scripts\install-desktop-windows.ps1 -PackagePath '.\artifacts\Tbox_<版本>_win-x64.msix'

# 真实资源管理器与标准文件对话框集成检查（仅在 GitHub 托管 runner 上执行）。
.\scripts\test-windows-folders.ps1
```

MSIX 保留 `Tbox` 包身份、发布者和 `App`/启动项标识，入口为 `Tbox.Desktop.exe`；首次启动在新版设置不存在时导入旧偏好，旧设置与缓存文件保留。脚本不生成证书，也不修改系统信任。开发证书只应导入“本地计算机 → 受信任人”，不要导入根证书颁发机构。

媒体检查使用合成视频，不采集屏幕：

```sh
dotnet run --project tests/Tbox.MediaSmoke/Tbox.MediaSmoke.csproj -c Release -- assets/media-test/fixture.mp4 TEMP/media-smoke TEMP/media-runtime/FFmpeg/bin/ffmpeg TEMP/media-runtime/VLC
```

## 发布

版本号同步写在 `src/Tbox.Desktop/Tbox.Desktop.csproj`、`Platform/windows/Package.appxmanifest` 和 `Platform/macos/Info.plist`，每个交付包使用从未打包过的新版本。新版本合入 main 后，由 GitHub Actions 构建、签名并验证 Windows MSIX 和 Apple Silicon Mac ZIP，两端均通过后在同一仓库公开为最新正式版。交付以未登录访问者能下载两份安装包并核对校验值为准。配置与验收规则见 [自动发布](docs/automatic-releases.md)。
