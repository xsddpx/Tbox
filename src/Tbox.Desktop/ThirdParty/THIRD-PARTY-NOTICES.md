# 跨平台版本第三方依赖

依赖版本以 `Tbox.Desktop.csproj` 和还原产生的 `project.assets.json` 为准。安装包在 `Contents/Resources/Licenses` 携带许可原文及第三方通知。

| 依赖 | 当前版本 | 许可 | 来源 |
| --- | --- | --- | --- |
| Avalonia 与桌面/主题后端 | 11.3.22 | MIT | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp | 2.88.9 | MIT；Skia 等传递许可见通知 | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp | 8.3.1.1 | MIT；HarfBuzz 许可见通知 | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime | 0.11.0 | MIT | NuGet 包元数据 |
| Tmds.DBus.Protocol | 0.21.3 | MIT | NuGet 包元数据；Linux 后端依赖 |
| .NET runtime / System.IO.Pipelines | .NET 8 | MIT 及分发包第三方通知 | https://github.com/dotnet/runtime |

macOS 原生桥接代码为本仓库新写实现，调用系统提供的 AppKit、Vision、ScreenCaptureKit 和 Carbon；不复制其他截图应用的源码或图标。应用图标沿用 Tbox 自有资源。
