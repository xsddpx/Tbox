# Tbox

Tbox 是一个面向 Windows 11 的模块化效率工具底座，采用 C#、.NET 8 和 WinUI 3。

首个模块“文件夹跳转”会记住最近使用的文件资源管理器目录；当标准文件打开/保存对话框位于前台时，按 `Ctrl+G` 可直接跳转到该目录。

## 开发

要求：

- Windows 11 x64
- .NET 8 SDK
- Windows 11 SDK 10.0.26100

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Tbox.App/Tbox.App.csproj
```

真实文件对话框与资源管理器集成压测：

```powershell
dotnet run --project tests/Tbox.IntegrationHarness/Tbox.IntegrationHarness.csproj -- 20
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

1. 将 `Tbox-Development.cer` 安装到“本地计算机 → 受信任的根证书颁发机构”。
2. 双击最新的 `Tbox_*.msix` 完成安装。

该证书只用于开发测试；公开发布时应替换为正式代码签名证书或 Microsoft Store 签名。
