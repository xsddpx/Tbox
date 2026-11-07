# Tbox

Tbox 是面向 Windows 和 macOS 的桌面工具。当前公开交付为共用 Avalonia 界面的 1.1.15，提供截图、标注、贴图、离线 OCR 和区域录屏。原 WinUI 工程保留为迁移基线，完整功能和交互验收仍在继续。

**[下载最新正式版](https://github.com/xsddpx/Tbox-releases/releases/latest)** · Windows 11 x64 / macOS 14+ Apple Silicon · 无需登录。首次安装见 [安装与权限说明](docs/releases/INSTALL.md)。

## 跨平台迁移

`src/Tbox.Desktop` 共用设置、截图交互、贴图、剪贴板与 OCR 结果界面，并直接复用 `Tbox.Core`。截图取帧、系统 OCR 等能力通过平台适配层接入。共用版已完成双平台签名构建和运行验证，并提供公开安装包；未验收范围以 [活需求文档](docs/product-requirements.md) 和 [技术与验收记录](docs/cross-platform-architecture.md) 为准。

Mac 开发需要 `global.json` 指定的 .NET SDK（9.0.313）、.NET 8 运行时，以及 Apple Command Line Tools。应用包自带运行时，使用者无需安装 .NET。

```bash
dotnet test tests/Tbox.Core.Tests/Tbox.Core.Tests.csproj -c Release
dotnet test tests/Tbox.Desktop.Tests/Tbox.Desktop.Tests.csproj -c Release
bash scripts/build-macos.sh osx-arm64
bash scripts/package-macos.sh osx-arm64
bash scripts/install-macos.sh
```

若 SDK 不在 PATH，可通过 `TBOX_DOTNET=/完整路径/dotnet` 指定。首次开发运行 `bash scripts/setup-macos-signing.sh` 创建持久本地签名身份；后续必须复用登录钥匙串中的证书。构建产物在 `TEMP/build/osx-arm64/Tbox.app`；正式生成的开发包在 `artifacts/`，禁止覆盖同版本。打包会运行测试、构建并签名验签，再实际启动应用验证离线 OCR 与像素裁剪，最后记录哈希。当前为本机开发证书签名，没有 Apple 公证。

固定安装位置为 `~/Applications/Tbox.app`。安装脚本在覆盖前检查新包是否满足旧包的签名身份，不主动清空权限；普通构建禁止退回 ad hoc 签名。私钥保存在登录钥匙串，不进 Git、不随应用分发。首次从旧临时签名迁移需要重新登记一次 Tbox 权限；之后通过兼容签名保留系统识别的应用身份。

升级验证需先正常打开已安装的 Tbox，再执行 `~/Applications/Tbox.app/Contents/MacOS/Tbox.Desktop --permission-probe`；它查询正在运行的应用进程，只报告权限及取帧尺寸，不保存屏幕图像。本机已通过 1.1.1 → 1.1.2 → 1.1.3 两次覆盖升级验证，屏幕授权及实际取帧均保留，见 [验证记录](docs/macos-permission-upgrade-verification.md)。1.1.13 已识别本机辅助功能授权，Mac 实际文件面板导航仍待复验。1.1.15 公开包已验证签名与当前安装身份一致；本机锁屏，应用内升级及升级后权限验证待解锁完成。

Mac 全局快捷键为 `Control+Shift+1` 截图、`Control+Shift+2` 剪贴板贴图、`Control+Shift+3` 全部贴图显隐。图片复制、保存和固定使用 `Command+C/S/T`，`Esc` 取消截图或关闭贴图。第一次截图需要在系统设置中允许 Tbox 屏幕录制。缺少权限时截图只显示应用内提示；只有点击授权按钮才请求一次，再次点击打开系统设置。已有图片的识别和剪贴板贴图无需屏幕录制权限。

共用工程已接入 Windows 截图、系统 OCR、窗口/元素检测和 F6/F7/F8，Mac 可执行 `dotnet build src/Tbox.Desktop/Tbox.Desktop.csproj -c Release -p:TboxWindows=true` 检查 Windows 目标。Windows 自动使用此目标；系统 OCR 仍需 MSIX 应用身份。1.1.15 的 Windows MSIX 已在托管 Windows 环境完成签名、安装、OCR、取帧、录屏及三格式导出检查；旧用户设备升级、系统声音和全部桌面交互仍待验收。

Windows 共用版使用 `scripts/package-desktop-windows.ps1`，与旧 WinUI 打包脚本分开。它保留原 MSIX 的 `Tbox` 包身份、发布者和 `App`/启动项标识，入口改为 `Tbox.Desktop.exe`；应用自带 .NET 运行时，首次启动会在新版设置不存在时导入旧偏好，旧设置与缓存文件保留。Windows 开发机需 Windows 11 x64、Windows SDK、.NET SDK，及原有持久签名证书；脚本不生成证书或自动修改系统信任。

```powershell
# CI/开发机：发布自包含二进制，验证版本、运行库、媒体导出与 MSIX 结构。
# 仅在 TEMP 生成未签名验证包，不作为用户下载包。
.\scripts\package-desktop-windows.ps1 -VerifyOnly

# Windows 实机：复用已有证书签名、安装、运行系统 OCR/界面验证，通过后记录版本和哈希。
.\scripts\package-desktop-windows.ps1 -CertificateThumbprint '<现有证书指纹>'

# 安装已经签名且在当前电脑受信任的共用版本；可通过 -DependencyPath 传入官方 VCLibs 框架包。
.\scripts\install-desktop-windows.ps1 -PackagePath '.\artifacts\Tbox_<版本>_win-x64.msix'
```

签名阶段需证书主题与 manifest 发布者一致；MSIX 实机安装需要受信任签名及所声明的 VCLibs 框架，详见 [Microsoft 签名说明](https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing)。当前 Mac 环境没有 Windows 原签名证书或 Windows 实机，因此新脚本的签名/安装阶段仍待验证；CI 包结构通过不能替代这一步。

Mac 媒体构建另需 Xcode Command Line Tools、`make` 与 `pkg-config`（`brew install pkgconf`）。`scripts/prepare-macos-media.sh` 校验固定下载哈希，从源码构建 FFmpeg 8.1.2 + libwebp 1.6.0，并准备 VLC 3.0.24 的本地 MP4 回放插件。首次构建需要联网，后续复用 `TEMP/` 中的依赖。

共用媒体检查使用合成视频，不采集屏幕：

```sh
dotnet run --project tests/Tbox.MediaSmoke/Tbox.MediaSmoke.csproj -c Release -- assets/media-test/fixture.mp4 TEMP/media-smoke TEMP/media-runtime/FFmpeg/bin/ffmpeg TEMP/media-runtime/VLC
```

检查实际 H.264 解码、GIF 12 FPS/循环时长、WebP 循环动画/尺寸及 MP4 复制一致性。Windows CI 使用同一测试及固定源码构建的 FFmpeg。1.1.15 已发布在独立公开仓库 [Tbox-releases](https://github.com/xsddpx/Tbox-releases/releases/latest)，两个安装包均经匿名实际下载和 SHA-256 校验；源码仓库保持私有。应用内“常规 → 检查更新”使用同一公开渠道，真实在线升级仍待本机解锁及旧 Windows 设备验收，详见 [更新机制](docs/online-updates.md)。

## Windows 完整版

首个模块“文件夹跳转”会记住最近使用的文件资源管理器目录；多标签窗口以当前选中的标签为准。当标准文件打开/保存对话框位于前台时，按 `Ctrl+G` 可直接跳转到该目录。

第二个模块“截图与贴图”提供接近 Snipaste 的轻量截图工作流：按 `F6` 后所有显示器会作为同一个截图会话同时进入截图状态，并按显示器原始物理像素清晰呈现冻结画面；可在任意显示器框选，但全局同一时刻只保留一个选区。松开后选区和纯图标操作条会保留，四边和四角控制点可继续拖动调整范围，已有标注会保留；随后可选择“贴图固定”或“复制”，也可单击鼠标中键快速贴图。固定后贴图会留在原选区。按 `F7` 可将剪贴板中的图片像素、复制的图片文件或纯文本创建为新贴图；文本会保留显式换行并自动折行。按 `F8` 可整体隐藏或在原位置重新显示全部贴图。

截图工具栏另外提供长截图和区域录屏入口。长截图退出冻结画面后，捕获选区显示黄色截图式细线与八个方形控制柄并立即开始捕获，由用户在框内手动向上或向下滚动；右侧实时预览使用中性窗口外框，持续显示整张拼接图、当前视口和滚动方向。长截图使用与截图工具栏一致的深色单行图标控制条，提供尺寸、贴图、保存、全屏、取消和复制，且不使用自动滚动。Tbox 会在画面稳定后识别双向重叠区域并从顶部或底部扩展，对白底文字和固定页头保留匹配容错；只移动鼠标、滚动条、资源管理器整行/文件卡片悬停变色等集中在少数纵向区域的变化不会增加长截图高度。完成后可预览、识别文字、保存、复制或贴图；当前只支持单显示器内的纵向拼接。

区域录屏使用独立的精简界面：从等待开始，捕获选区即显示红色截图式细线与八个方形控制柄；深色控制条在开始前显示时间、帧率、系统声音、开始和取消，录制后自动收紧为时间、暂停、停止和取消，暂停时以黄色状态及继续图标明确提示。普通截图、长截图和录屏的选区分别使用蓝色、黄色、红色的同款边框样式，不使用系统白边或圆形控制点。开始前可选择 5/10/15/25/60 FPS，默认 10 FPS；录制包含鼠标指针，系统声音默认关闭且不录制麦克风。停止后进入深色视频预览：标题区显示分辨率、时长和系统声音状态，底部按播放/音量与格式/导出分组，默认保存 MP4，也可选择 GIF 或动画 WebP；GIF/WebP 不包含声音，导出进度、取消和失败都在当前窗口反馈。关闭或重新录制会先安全卸载回放媒体，再清理临时文件。

截图启动后会检测指针下的窗口，单击即可采用检测区域，也可拖动自由框选；`Tab` 在窗口检测与界面元素检测之间切换，界面元素不可识别时回退到窗口，`R` 恢复本次运行中上一次成功截图的区域。截图选区支持矩形、椭圆、箭头、自动递增序号、画笔、马赛克、文字和对象橡皮擦，可调整颜色与粗细，并支持撤销/重做。序号工具默认大小为 `22`，每次单击会依次放置 `1`、`2`、`3`……，复用当前颜色和大小设置。`Shift` 可约束正方形、圆形和箭头的 45° 方向，`B`/`T` 选择画笔/文字，`1`/`2` 调整粗细，空格显示或隐藏标注工具条；文字编辑使用 `Ctrl+Enter` 提交。`Ctrl+C`、`Ctrl+S`、`Ctrl+T` 分别复制、保存和固定最终合成图，选框与工具栏不会写入输出。

截图工具栏、贴图工具栏和贴图右键菜单的“文字识别”都会使用原始图像像素，不包含 Tbox 标注。全局只保留一个结果窗口；再次识别会取消旧任务并在同一窗口替换内容，从贴图触发时结果窗口会自动置顶并移到最前。结果显示为可编辑、自动换行的文本，并提供逻辑行号、复制全部、原图展开和窗口置顶；识别成功后自动聚焦并全选。首版使用 Windows 随系统安装的离线 OCR 和用户首选语言，因此需通过已安装的 Tbox MSIX 运行；缺少对应语言时会在结果窗口中给出处理提示。识别文字不写入历史。

产品行为、历史修改要求和跨平台迁移语义统一维护在 [`docs/product-requirements.md`](docs/product-requirements.md)。

设置、主页、文字识别和结果回放统一迁移到 Tbox UI 2.0 共用浅深主题（移除搜索）；截图、贴图、长截图和录屏浮层固定使用共享的高对比深色工具条。全产品复用 `28/16/14/12` 四级字号、`4/8/12/16/24/32` 间距序列、统一圆角、图标度量及交互状态，详细规范见 [`docs/ui-design-system.md`](docs/ui-design-system.md)。

贴图支持拖动、鼠标滚轮沿指针位置缩放、`Ctrl+C` 复制图像，以及通过空格或右键“显示工具条”进入标注；工具条紧邻贴图显示在图像外部，空间不足时自动切换到贴图上方，不遮挡图像内容。在贴图上按住鼠标右键拖动可框选并裁剪当前可见区域，短按右键仍打开菜单；裁剪不会改写原图，可通过“恢复完整贴图”撤销，复制、另存为和文字识别只处理当前可见区域。贴图和截图共用同一套单行工具条尺寸与顺序，贴图末尾以完成对号替代固定贴图按钮且不显示横向滚动条；工具条按所在显示器 DPI 换算完整窗口尺寸，在 100%/125%/150% 缩放下均保留右侧 OCR、保存、复制和完成按钮。点击对号或在工具条显示时按 `Esc` 会提交有效文字和折线、放弃未完成手势并隐藏工具条；工具条隐藏时按 `Esc` 关闭贴图。复制和另存为会合成当前标注。右键菜单同时提供“恢复完整贴图 / 复制图像 / 图像另存为… / 关闭”。默认缓存位于 Windows 用户“图片”文件夹下的 `screenshots`；可在“截图与贴图”设置页查看、更换或打开缓存文件夹。更换目录只影响后续截图，不会移动已有缓存。缓存按 30 天、200 个文件和 512 MB 三重上限自动清理，当前仍在显示的贴图文件会被保留。录屏临时文件单独位于 `%LOCALAPPDATA%\Tbox\cache\recordings`，保存或放弃后自动删除。

## 开发

要求：

- Windows 11 x64
- .NET 8 SDK
- Windows 11 SDK 10.0.26100
- Visual Studio 2022 Build Tools（MSBuild 与 VSTest）

```powershell
dotnet restore
$vsPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
$vstest = Join-Path $vsPath "Common7\IDE\Extensions\TestPlatform\vstest.console.exe"
& $msbuild .\Tbox.sln /restore /t:Build /p:Configuration=Release /p:Platform=x64 /m:1
& $vstest .\tests\Tbox.Core.Tests\bin\Release\net8.0\Tbox.Core.Tests.dll /Platform:x64
& .\src\Tbox.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\Tbox.App.exe
```

WinUI 3 项目使用 Visual Studio 构建链，避免 Windows App SDK 的独立 XAML 编译器在复杂响应式页面上异常退出。发布脚本会自动发现本机最新的 Build Tools。

真实文件对话框与资源管理器集成压测：

```powershell
& .\tests\Tbox.IntegrationHarness\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\Tbox.IntegrationHarness.exe 20
```

只验证多显示器枚举与逐屏像素捕获：

```powershell
& .\tests\Tbox.IntegrationHarness\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\Tbox.IntegrationHarness.exe --capture-only
```

关闭设置窗口后应用会继续驻留系统托盘；双击托盘图标可重新打开设置，通过托盘菜单的“退出”才会完全结束应用。

## 项目结构

- `src/Tbox.Core`：模块契约、设置和可测试的业务规则
- `src/Tbox.App`：WinUI 底座及 Windows 互操作
- `tests/Tbox.Core.Tests`：核心单元测试
- `tests/Tbox.IntegrationHarness`：真实 Windows 文件对话框与资源管理器集成压测

## 当前边界

- 目标平台为 Windows 11 x64；系统 OCR 需在已安装的 MSIX 中运行并依赖 Windows 已安装的识别语言。
- 支持 Windows 标准文件打开、保存及文件夹选择对话框。
- Office、Adobe 等自定义文件对话框不在首版范围。
- 遇到“主页”“此电脑”等非文件系统 Shell 位置时，回退到用户主目录。
- OCR 首版不包含搜索、翻译、自动去换行、识别历史或图文双向校对。

## 安装开发版 MSIX

`artifacts/` 中的包使用 Tbox 开发证书签名。首次在另一台电脑安装时：

1. 以管理员 PowerShell 将开发证书安装到“本地计算机 → 受信任人”：

   ```powershell
   Import-Certificate -FilePath .\artifacts\Tbox-Development.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

2. 双击最新的 `Tbox_*.msix` 完成安装。

该证书只用于内部开发测试，不应导入“受信任的根证书颁发机构”；不再测试时可从“受信任人”删除。公开发布时应替换为正式代码签名证书或 Microsoft Store 签名，并为签名添加可信时间戳。

## 生成 MSIX

每个可交付包必须使用从未打包过的新版本。打包前同步更新：

- `src/Tbox.App/Tbox.App.csproj` 中的 `Version`、`AssemblyVersion`、`FileVersion`
- `src/Tbox.App/Package.appxmanifest` 中的 `Identity Version`

例如 v1.0 使用 `Version=1.0.0`，其余三处使用 `1.0.0.0`。确认当前用户证书库中存在与 manifest `Publisher` 匹配、包含私钥且具备 Code Signing 用途的证书后，运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 -IntegrationAttempts 20
```

脚本会串行执行 Release 构建、单元测试、真实 Explorer/文件对话框集成测试、打包、签名和验签；成功后才会生成 `artifacts/Tbox_<版本>_x64.msix`，并将时间和 SHA-256 追加到 `docs/release-history.md`。脚本会检测验证期间的历史文件并发修改，并在发布失败时回滚包和历史记录；版本不一致、二进制过期、版本已发布、包已存在或任一验证失败都会终止发布。

## GitHub 自动发布

在源码仓库发布版本后，GitHub Actions 自动构建 Windows MSIX 与 Apple Silicon Mac ZIP，全部校验通过后上传并公开到 [Tbox-releases](https://github.com/xsddpx/Tbox-releases/releases)。签名凭据、版本准备、失败恢复和当前启用状态见 [自动发布说明](docs/automatic-releases.md)。
