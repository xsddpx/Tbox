# 共用 Windows 文件夹导航集成检查

此程序仅在一次性的 GitHub 托管 Windows runner 上执行；同时检查 `GITHUB_ACTIONS` 和 `RUNNER_ENVIRONMENT`，拒绝在日常使用的电脑启动。它会打开自己的 Explorer 窗口和系统文件面板，并改变测试窗口焦点。

```powershell
dotnet run --project tests/Tbox.FolderSmoke/Tbox.FolderSmoke.csproj -c Release -- TEMP/folder-smoke
```

结果目录必须不存在。程序经 `WindowsPlatformServices.CreateFolderNavigation()` 调用共用应用实际使用的适配层，不使用旧 WinUI 测试入口或直接注入最近目录。

检查范围：

- 观察前台 Explorer 的两个文件系统目录，目录名相同、父路径不同，含中文和空格。
- 每个目标分别从标准打开、保存面板跳转；必须显示目标内唯一标记目录，避免仅检查地址栏末级名称导致假通过。
- 普通窗口必须被拒绝，输入框内容不能改变。
- 停止模块后停止目录跟踪，重新启用后恢复。

`result.json` 记录成功状态、四次导航类型和时长；失败时保留已完成步骤及错误。测试结束关闭自身窗口和所创建的 Explorer 窗口。CI 保存 JSON，失败会阻止后续打包。

这项检查不覆盖全局快捷键投递、Explorer 多标签切换、用户原 Windows 设备升级、自定义文件面板或 Mac 系统集成。本机 Mac 上的 Windows 目标编译仅检查构建，不能作为原生执行通过的证据。
