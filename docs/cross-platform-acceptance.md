# 跨平台逐项验收表

当前共用桌面测试已增至 39 项，新增剪贴板回退、受限重试、超长文字画布及模块热键回滚。提交 `b74fa61` 的 [两平台 CI](https://github.com/xsddpx/Tbox/actions/runs/42377475447) 已通过 95 核心 + 36 桌面测试及实际媒体解码/导出。其后的文件夹适配与热键模块开关已在本机完成两目标编译，仍待实机文件面板验收。

媒体集成证据（2026-11-07，本机 Apple Silicon）：`Tbox.MediaSmoke` 对库内合成视频实际解码 36 次输出回调；GIF 导出 24 帧、2000 ms；WebP 导出 20 个合并帧、1999 ms，循环且不放大 320×240；MP4 复制哈希相同。此检查已接入 CI；不验证实际系统采集、音频、原生视频窗口或安装授权。

冻结基线：`0f88802717d6a2a6ad86e117c20ec35ead91f96c`（Windows 1.0.45）。原产品要求可通过 `git show 0f88802:docs/product-requirements.md` 复核。表中的原平台证据是源码定位，不代替迁移后的实机复验。

2026-11-07：共用桌面工程 32 项测试、原核心 95 项测试通过；已运行本机离线中英文 OCR、原始像素裁剪及单显示器取帧（命令行验证；不代表独立启动后的应用授权）。Windows 目标交叉编译通过；提交 `7e6c1a3` 的 [Windows/macOS CI](https://github.com/xsddpx/Tbox/actions/runs/42372131729) 均通过（95 核心 + 20 桌面测试）；新增长截图及独立进程权限探测后本机桌面测试增至 25 项；[长截图提交的双平台 CI](https://github.com/xsddpx/Tbox/actions/runs/42372899931) 通过。Mac 另有拒绝临时签名和错误标识的两项脚本测试。持久签名正在等待首次钥匙串确认，连续升级权限保留未验收。混合 DPI 多显示器、Intel Mac、Windows 共用界面均尚未实机验证。截图全流程仍在授权与交互复验中。

| ID | 原状态 | 原平台源码证据（src/Tbox.App，另注除外） | 共用/目标平台入口（src/Tbox.Desktop，另注除外） | 迁移验收 |
| --- | --- | --- | --- | --- |
| FJ-001 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/*FolderNavigation.cs / Platform/macos/TboxFolders.swift | Windows 复用原逻辑；Mac 观察选中 Finder 窗口；待实机复验 |
| FJ-002 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/*FolderNavigation.cs / Platform/macos/TboxFolders.swift | Ctrl+G 已接入，Mac 只允许标准文件面板；待实机复验 |
| FJ-003 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/*FolderNavigation.cs / Platform/macos/TboxFolders.swift | Windows 复用原重试；Mac 不稳定标题保留旧路径；待实机复验 |
| FJ-004 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Core/FolderTargetPolicy.cs | 两端共用目录校验和主目录回退；待实机复验 |
| FJ-005 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/*FolderNavigation.cs / Platform/macos/TboxFolders.swift | 导航结果校验已接入，Mac 面板属性仍需实际验证 |
| FJ-006 | 待实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | 待迁移 | 原基线待实现；不因迁移新增 |
| FJ-007 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/macos/TboxFolders.swift | 已限定系统文件面板提供者；实际识别待验收 |
| CAP-001 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-002 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-003 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-004 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-005 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-006 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-007 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-008 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-009 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-010 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-011 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-012 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/CaptureSession.cs / Views/CaptureWindow.cs | 待逐项复验 |
| CAP-013 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/LongCaptureSession.cs / Models/LongCaptureBuffer.cs / Views/SelectionOutline.cs | 长截图及录屏已接入共用流程；实机滚动与录制仍待验收 |
| CAP-014 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/LongCaptureSession.cs / Models/LongCaptureBuffer.cs / Views/SelectionOutline.cs | 长截图及录屏已接入共用流程；实机滚动与录制仍待验收 |
| CAP-015 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | Views/LongCaptureSession.cs / Models/LongCaptureBuffer.cs / Views/SelectionOutline.cs | 长截图及录屏已接入共用流程；实机滚动与录制仍待验收 |
| ANN-001 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-002 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-003 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-004 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-005 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-006 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-007 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-008 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-009 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-010 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-011 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| ANN-012 | 已实现 | CaptureAnnotationController.cs / CaptureAnnotationRenderer.cs / AnnotationToolbar.cs | Models/AnnotationEditor.cs / Models/AnnotationRenderer.cs / Views/AnnotationToolbar.cs | 模型与渲染测试通过；逐项交互复验待完成 |
| OCR-001 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| OCR-002 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| OCR-003 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| OCR-004 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| OCR-005 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| OCR-006 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| OCR-007 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| REC-001 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 共用流程与双平台后端已接入；7 项计时、取消和文件保护测试通过；实际录制/音频/回放/导出待验收 |
| REC-002 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 共用流程与双平台后端已接入；7 项计时、取消和文件保护测试通过；实际录制/音频/回放/导出待验收 |
| REC-003 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 共用流程与双平台后端已接入；7 项计时、取消和文件保护测试通过；实际录制/音频/回放/导出待验收 |
| REC-004 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 共用流程与双平台后端已接入；7 项计时、取消和文件保护测试通过；实际录制/音频/回放/导出待验收 |
| REC-005 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 共用流程与双平台后端已接入；7 项计时、取消和文件保护测试通过；实际录制/音频/回放/导出待验收 |
| REC-006 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 共用流程与双平台后端已接入；7 项计时、取消和文件保护测试通过；实际录制/音频/回放/导出待验收 |
| REC-007 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 共用流程与双平台后端已接入；7 项计时、取消和文件保护测试通过；实际录制/音频/回放/导出待验收 |
| REC-008 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 共用流程与双平台后端已接入；7 项计时、取消和文件保护测试通过；实际录制/音频/回放/导出待验收 |
| PIN-001 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-002 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs / Models/ClipboardReader.cs / DesktopController.cs | 格式回退、占用三轮上限、恢复及超长文字画布测试通过；实际剪贴板交互待复验 |
| PIN-003 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-004 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-005 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-006 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-007 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-008 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| APP-001 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-002 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-003 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-004 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-005 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-006 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-007 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-008 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-009 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-010 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-011 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-012 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| UI-001 | 已实现 | TboxUi.cs / MainWindow.xaml | Views/Ui.cs | 待逐项复验 |
| UI-002 | 已实现 | TboxUi.cs / MainWindow.xaml | Views/Ui.cs | 待逐项复验 |
| UI-003 | 已实现 | TboxUi.cs / MainWindow.xaml | Views/Ui.cs | 待逐项复验 |
| UI-004 | 已实现 | TboxUi.cs / MainWindow.xaml | Views/Ui.cs | 待逐项复验 |
| UI-005 | 已实现 | TboxUi.cs / MainWindow.xaml | Views/Ui.cs | 待逐项复验 |
| UI-006 | 已实现 | TboxUi.cs / MainWindow.xaml | Views/Ui.cs | 待逐项复验 |
| UI-007 | 已实现 | TboxUi.cs / MainWindow.xaml | Views/Ui.cs | 待逐项复验 |
| UI-008 | 已实现 | TboxUi.cs / MainWindow.xaml | Views/Ui.cs | 待逐项复验 |
| REL-001 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-002 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-003 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-004 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-005 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-006 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |

录屏实现进展（2026-11-07）：Windows 链接原 `ScreenRegionRecorder`，Mac 使用 ScreenCaptureKit + AVAssetWriter；共用操作负责声音失败降级、暂停时钟、等待原生释放再清理。回放使用 LibVLCSharp 的 Avalonia 组件，GIF/WebP 使用外部 FFmpeg 进程。32 项桌面测试、95 项核心测试和两目标编译已通过；Mac 媒体组件打包与真实回放仍在验证，不能视为完成发布。
