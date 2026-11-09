# Tbox 项目交接与当前进度

> [!IMPORTANT]
> 本文件是 v1.0 时点的历史交接快照，不再承担当前需求清单职责。当前产品需求、历史修改要求和迁移语义以 [`product-requirements.md`](product-requirements.md) 为准，发布版本以 [`release-history.md`](release-history.md) 为准。

> 快照时间：2026-07-13 13:55（Asia/Shanghai）
>
> 工作目录：`C:\Users\lihongbin.SOULAPP\Documents\Tbox`
>
> 用途：切换 GPT/Codex 账号后，在同一本地仓库继续工作。后续若状态发生变化，请更新本文件顶部时间和相关章节。

## 一眼结论

| 项目 | 当前状态 |
| --- | --- |
| 产品状态 | 已完成并打包 Tbox v1.0；已有“文件夹跳转”和“截图与贴图”两个可用模块 |
| 当前分支 | `main` |
| 当前提交 | `30fea2c feat: release Tbox v1.0`，本地标签 `v1.0` |
| 远端差异 | 本地 `main` 比 `origin/main` 领先 2 个提交；这两个提交和 `v1.0` 标签尚未进入远端 |
| 源码版本 | `Version=1.0.0`；Assembly、File 和 MSIX Manifest 均为 `1.0.0.0` |
| 最新产物 | `artifacts/Tbox_1.0.0.0_x64.msix`，70,548,183 字节 |
| 最新验证 | 本次交接复核：Release 构建 0 警告、0 错误；21/21 个单元测试通过 |
| 当前阻塞 | 没有代码阻塞；下一项产品目标尚未由用户指定 |

## 新账号接手时先做什么

1. 先完整读取根目录 `AGENTS.md`，并按其中要求读取全局 `🎭AGENT须知.md`；不要把全局文件中的本机敏感信息复制进仓库。
2. 阅读本文件、根目录 `README.md` 和 `docs/release-history.md`。
3. 在仓库根目录执行：

   ```powershell
   git status --short --branch
   git log --oneline --decorate -5
   git diff
   ```

4. 先保护本地尚未推送的两个提交和 `v1.0` 标签，不要直接清理、重置或覆盖工作区。是否推送由用户确认。
5. 继续开发前先让用户明确下一个产品目标；仓库里目前没有正式的 backlog、Issue 清单或未完成任务说明。

## 项目定位与技术栈

Tbox 是面向 Windows 11 x64 的模块化效率工具，使用 C#、WinUI 3、Windows App SDK 和 Win2D。各项目的目标框架是 .NET 8，命令行 SDK 由 `global.json` 固定为 `9.0.313`。

解决方案 `Tbox.sln` 包含四个项目：

- `src/Tbox.Core`：不依赖 UI 的设置、缓存、截图坐标、标注模型和缩放策略。
- `src/Tbox.App`：WinUI 主程序、窗口、托盘、全局快捷键、Windows/COM 互操作。
- `tests/Tbox.Core.Tests`：xUnit 单元测试。
- `tests/Tbox.IntegrationHarness`：真实 Explorer 和标准 Windows 文件对话框集成压测。

## 当前已经实现的产品内容

### 1. 文件夹跳转

- 跟踪最近使用的文件资源管理器目录。
- 在标准打开、保存和文件夹选择对话框前台时，按 `Ctrl+G` 跳转到该目录。
- 在触发快捷键前主动刷新最近的 Explorer 窗口，减少路径陈旧问题。
- 支持本地化地址栏结果校验和失败重试。
- 遇到“主页”“此电脑”等非文件系统 Shell 位置或无效目录时，回退到用户主目录。
- 当前只支持标准 `#32770` 文件对话框；Office、Adobe 等自定义对话框不在 v1.0 范围内。
- 快捷键当前固定为 `Ctrl+G`。数据模型已有热键设置，但界面注明的“快捷键录制器”尚未实现。

关键文件：

- `src/Tbox.App/FolderJumpModule.cs`
- `src/Tbox.App/ExplorerFolderTracker.cs`
- `src/Tbox.App/FileDialogNavigator.cs`
- `src/Tbox.Core/FolderTargetPolicy.cs`

### 2. 截图、标注与贴图

- `F6`：捕获鼠标所在显示器并框选截图；松开后保留选区和操作条。
- `F7`：读取剪贴板图像并创建新贴图。
- `F8`：统一隐藏或在原位置恢复所有贴图。
- 支持矩形、椭圆、直线、折线、箭头、画笔、直线高亮、马赛克、文字和对象橡皮擦。
- 支持颜色与粗细调整、撤销/重做，以及 `Shift` 正方形、圆形和 45° 方向约束。
- 最终图像可复制、另存为或固定；选框和工具栏不会写入输出。
- 贴图支持置顶、拖动、按指针锚点滚轮缩放、`Ctrl+C` 复制、右键另存/关闭和 `Esc` 关闭。
- 截图固定时保留原选区位置和尺寸；剪贴板贴图默认显示在鼠标附近。
- 默认缓存目录为 `%LOCALAPPDATA%\Tbox\cache\screenshots`，可从设置页更换或打开。
- 缓存按 30 天、200 个文件、512 MB 三重上限清理；正在显示的贴图文件受到保护。更换目录不会迁移旧缓存。

关键文件：

- `src/Tbox.App/ScreenPasteModule.cs`
- `src/Tbox.App/ScreenCaptureService.cs`
- `src/Tbox.App/CaptureOverlayWindow.cs`
- `src/Tbox.App/CaptureAnnotationController.cs`
- `src/Tbox.App/CaptureAnnotationRenderer.cs`
- `src/Tbox.App/PinnedImageWindow.cs`
- `src/Tbox.Core/CaptureAnnotations.cs`
- `src/Tbox.Core/CaptureCache.cs`

### 3. 应用底座

- 单实例运行；再次启动会打开主窗口。
- 关闭主窗口只是隐藏，继续驻留托盘，只有托盘菜单“退出”才完全结束。
- 支持模块开关、开机启动和缓存目录设置。
- 设置以 JSON 原子写入 `%LOCALAPPDATA%\Tbox\settings.json`。
- 常规日志位于 `%LOCALAPPDATA%\Tbox\tbox.log`，超过 2 MB 后轮换到 `tbox.previous.log`。
- 启动失败详情写入 `%LOCALAPPDATA%\Tbox\startup-error.log`。
- 打包版开机启动使用 `StartupTask`，未打包版使用当前用户的 Run 注册表项。

关键文件：

- `src/Tbox.App/App.xaml.cs`
- `src/Tbox.App/MainWindow.xaml` / `MainWindow.xaml.cs`
- `src/Tbox.App/SingleInstanceCoordinator.cs`
- `src/Tbox.App/TrayIconService.cs`
- `src/Tbox.App/StartupManager.cs`
- `src/Tbox.Core/AppSettings.cs`
- `src/Tbox.Core/JsonSettingsStore.cs`

## 已完成里程碑与 Git 状态

最近工作的顺序如下：

1. `96def36`（当前 `origin/main`）：完善截图与贴图工作流。
2. `567ef44`（仅本地）：稳定文件对话框导航，生成并记录 `0.3.4.0` 包。
3. `30fea2c`（仅本地，当前 `HEAD`）：完成截图标注、发布脚本加固及 v1.0 发布；本地标签为 `v1.0`。

交接时的分支关系：

```text
origin/main  96def36
                \
local main       567ef44 -- 30fea2c (HEAD, tag: v1.0)
```

在创建本文件前，`git status` 还把 `docs/release-history.md` 显示为未暂存修改，但 `git diff` 为空，索引与工作区 blob 哈希相同；这更像行尾或 stat 状态异常，不是已发现的内容变更。新账号应再次复核，不要擅自覆盖该文件。本交接文件本身是本轮新增文件，除非用户另行要求，尚未提交。

## 构建、测试与验证状态

### 本次交接实际复核

2026-07-13 本轮已执行：

```powershell
dotnet build Tbox.sln -c Release -p:Platform=x64 --nologo -m:1
dotnet test Tbox.sln -c Release -p:Platform=x64 --no-build --nologo
```

结果：

- Release 构建成功：0 个警告，0 个错误。
- xUnit 单元测试：21 通过，0 失败，0 跳过。
- 测试覆盖设置默认值与原子保存、截图坐标和裁剪、缓存边界与清理、贴图缩放、全部标注命令、撤销/重做、对象擦除、Shift 几何约束和命中测试。

注意：若对单独的测试项目使用 `-p:Platform=x64 --no-build`，可能因项目输出路径与解决方案配置映射不同而提示找不到测试 DLL。交接复核和发布脚本使用的是上面的解决方案级命令。

### 集成验证

`tests/Tbox.IntegrationHarness` 会真实启动 Explorer 和 Windows 标准打开对话框，验证：

- Explorer 初始目录跟踪及目录变化刷新。
- 标准文件对话框真实跳转和本地化地址栏结果。
- 截图标注最终渲染。

本轮交接没有再次运行会操作桌面窗口的集成压测。最近的 `1.0.0.0` 发布记录由发布脚本生成；该脚本在写入历史和发布产物前会依次运行 Release 构建、单元测试、20 次集成压测、签名和验签。仓库没有保留独立测试报告，因此需要完全重新验证时仍应手动运行集成 Harness。

## 当前版本与发布产物

版本声明当前一致：

- `src/Tbox.App/Tbox.App.csproj`
  - `Version=1.0.0`
  - `AssemblyVersion=1.0.0.0`
  - `FileVersion=1.0.0.0`
- `src/Tbox.App/Package.appxmanifest`
  - `Identity Version=1.0.0.0`

最新 MSIX：

```text
文件：artifacts/Tbox_1.0.0.0_x64.msix
时间：2026-07-13 13:36:11 +08:00
大小：70,548,183 字节
SHA-256：71697A05F0A3BCB9C8074740D91580B20DF652B6E8104E9D1F6D347FD3FDDF84
```

哈希已在本次交接中重新计算，并与 `docs/release-history.md` 一致；MSIX 签名验签成功。开发签名没有可信时间戳，仅适合内部测试。

`artifacts/` 整体被 `.gitignore` 忽略，因此 MSIX、开发证书和历史包不会随 Git 推送或新克隆迁移。如果只是切换 GPT 账号并继续使用同一 Windows 工作目录，这些文件仍在；如果同时切换 Windows 用户或机器，需要单独迁移合适的产物，并重新配置含私钥的代码签名证书。

## 发布规则：下一次不能再使用 1.0.0.0

`1.0.0.0` 已存在于发布历史和 `artifacts/`，禁止覆盖。下一次默认补丁版本应为：

- `Tbox.App.csproj`：`Version=1.0.1`
- `Tbox.App.csproj`：`AssemblyVersion=1.0.1.0`
- `Tbox.App.csproj`：`FileVersion=1.0.1.0`
- `Package.appxmanifest`：`Identity Version=1.0.1.0`

然后运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 -IntegrationAttempts 20
```

脚本会拒绝版本不一致、历史中已有版本、同名包已存在、二进制过期、测试失败或签名失败，并只在完整成功后追加 `docs/release-history.md`。不要手工伪造发布历史。

## 已知边界、文档差异和候选待办

### 已知产品边界

- 仅支持 Windows 11 x64。
- 仅承诺支持 Windows 标准文件对话框；Office、Adobe 等自定义对话框不在 v1.0 范围内。
- 虚拟 Shell 位置回退到用户主目录。
- 当前没有 CI，真实 Explorer/文件对话框验证依赖本机交互式 Windows 环境。

### 文档/配置需要后续统一

1. 根目录 `AGENTS.md` 的前半部分仍称项目是“尚未提交业务代码的初始脚手架”，并使用 npm 命令举例，已经与当前 .NET/WinUI 项目不符；其中版本、打包、安全和提交规则仍然有效。后续应在用户确认后更新其项目结构和命令部分。
2. `README.md` 写的是“.NET 8 SDK”，但 `global.json` 实际固定 SDK `9.0.313`；项目目标框架仍是 `net8.0`。当前机器使用 `dotnet 9.0.313` 构建成功，后续应统一文档或 SDK 固定策略。

### 已发现但尚未实现的产品项

- 快捷键录制器：UI 已明确标注为后续能力；`Ctrl+G`、`F6`、`F7`、`F8` 目前固定。
- `AppSettings.ShowFailureNotifications` 已存在，但当前没有对应的设置界面/行为开关。
- 除上述明确线索外，没有正式 backlog；不要自行假定下一功能的优先级。

## 常用命令

```powershell
# 还原、构建、测试
dotnet restore
dotnet build Tbox.sln -c Release -p:Platform=x64
dotnet test Tbox.sln -c Release -p:Platform=x64 --no-build

# 本地运行
dotnet run --project src/Tbox.App/Tbox.App.csproj

# 真实 Windows 集成压测（会操作 Explorer 和文件对话框）
dotnet run --project tests/Tbox.IntegrationHarness/Tbox.IntegrationHarness.csproj -- 20

# 查看本地与远端差异
git status --short --branch
git log --oneline --decorate origin/main..HEAD
git tag --list
```

## 建议的接续顺序

1. 复核 `git status`、空 diff 的 `release-history.md` 状态和本交接文件。
2. 在用户确认后提交本交接文档，并把本地两个业务提交及 `v1.0` 标签安全同步到远端。
3. 让用户确定下一项产品工作；不要把候选待办自动视为已授权需求。
4. 若继续开发，按行为变更补充测试，并重新运行 Release 构建和单元测试。
5. 若涉及 Explorer、文件对话框或截图最终渲染，运行集成 Harness。
6. 若生成新 MSIX，先升级到从未使用过的新版本；默认从 `1.0.1` / `1.0.1.0` 开始。
