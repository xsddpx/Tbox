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

录屏依赖：LibVLCSharp / LibVLCSharp.Avalonia 3.10.2 为 LGPL-2.1-or-later（随附许可）；Windows ScreenRecorderLib 6.6.0 为 MIT（随附许可）。Windows 使用 VideoLAN.LibVLC.Windows 3.0.24，Mac 媒体运行库来自 VideoLAN VLC 3.0.24 官方分发。LibVLC 核心为 LGPL；插件有各自许可，不能将整个运行库一概称为 LGPL。最终安装包必须携带组件许可、来源及与分发二进制相对应的源代码获取材料。媒体分发材料仍在准备，当前不是完成版安装包。
