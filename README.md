# Tbox

Tbox 是一个面向 Windows 11 的模块化效率工具底座，采用 C#、.NET 8 和 WinUI 3。

首个模块“文件夹跳转”会记住最近使用的文件资源管理器目录；当标准文件打开/保存对话框位于前台时，按 `Ctrl+G` 可直接跳转到该目录。

第二个模块“截图与贴图”提供接近 Snipaste 的轻量截图工作流：按 `F6` 后所有显示器会作为同一个截图会话同时进入截图状态，可在任意显示器框选，但全局同一时刻只保留一个选区；松开后选区和纯图标操作条会保留，可选择“贴图固定”或“复制”，也可单击鼠标中键快速贴图；固定后贴图会留在原选区。按 `F7` 可将剪贴板图像创建为新贴图，按 `F8` 可整体隐藏或在原位置重新显示全部贴图。

截图选区支持矩形、椭圆、箭头、自动递增序号、画笔、马赛克、文字和对象橡皮擦，可调整颜色与粗细，并支持撤销/重做。序号工具默认大小为 `22`，每次单击会依次放置 `1`、`2`、`3`……，复用当前颜色和大小设置。`Shift` 可约束正方形、圆形和箭头的 45° 方向，`B`/`T` 选择画笔/文字，`Tab` 切换矩形与椭圆，`1`/`2` 调整粗细；文字编辑使用 `Ctrl+Enter` 提交。`Ctrl+C`、`Ctrl+S`、`Ctrl+T` 分别复制、保存和固定最终合成图，选框与工具栏不会写入输出。

产品行为、历史修改要求和跨平台迁移语义统一维护在 [`docs/product-requirements.md`](docs/product-requirements.md)。

贴图支持拖动、鼠标滚轮沿指针位置缩放、`Ctrl+C` 复制图像、右键“复制图像 / 图像另存为… / 关闭”和 `Esc` 关闭。默认缓存位于 `%LOCALAPPDATA%\Tbox\cache\screenshots`；可在“截图与贴图”设置页查看、更换或打开缓存文件夹。更换目录只影响后续截图，不会移动已有缓存。缓存按 30 天、200 个文件和 512 MB 三重上限自动清理，当前仍在显示的贴图文件会被保留。

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

- 目标平台为 Windows 11 x64。
- 支持 Windows 标准文件打开、保存及文件夹选择对话框。
- Office、Adobe 等自定义文件对话框不在首版范围。
- 遇到“主页”“此电脑”等非文件系统 Shell 位置时，回退到用户主目录。

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
