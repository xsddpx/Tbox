# 跨平台逐项验收表

冻结基线：`0f88802717d6a2a6ad86e117c20ec35ead91f96c`（Windows 1.0.45）。原产品要求可通过 `git show 0f88802:docs/product-requirements.md` 复核。表中的原平台证据是源码定位，不代替迁移后的实机复验。

2026-11-07：共用桌面工程 20 项测试、原核心 95 项测试通过；已运行本机离线中英文 OCR、原始像素裁剪及单显示器取帧（命令行验证；不代表独立启动后的应用授权）。Windows 目标交叉编译通过；持久签名正在等待首次钥匙串确认，连续升级权限保留未验收。混合 DPI 多显示器、Intel Mac、Windows 共用界面均尚未实机验证。截图全流程仍在授权与交互复验中。

| ID | 原状态 | 原平台源码证据（src/Tbox.App，另注除外） | 共用/目标平台入口（src/Tbox.Desktop，另注除外） | 迁移验收 |
| --- | --- | --- | --- | --- |
| FJ-001 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | 待迁移 | 待实现 |
| FJ-002 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | 待迁移 | 待实现 |
| FJ-003 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | 待迁移 | 待实现 |
| FJ-004 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | 待迁移 | 待实现 |
| FJ-005 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | 待迁移 | 待实现 |
| FJ-006 | 待实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | 待迁移 | 原基线待实现；不因迁移新增 |
| FJ-007 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | 待迁移 | 待实现 |
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
| CAP-013 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | 待迁移（长截图/录屏） | 待实现 |
| CAP-014 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | 待迁移（长截图/录屏） | 待实现 |
| CAP-015 | 已实现 | CaptureOverlayWindow.cs / ScreenCaptureService.cs / LongCaptureWindow.cs | 待迁移（长截图/录屏） | 待实现 |
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
| REC-001 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | 待迁移 | 待实现 |
| REC-002 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | 待迁移 | 待实现 |
| REC-003 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | 待迁移 | 待实现 |
| REC-004 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | 待迁移 | 待实现 |
| REC-005 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | 待迁移 | 待实现 |
| REC-006 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | 待迁移 | 待实现 |
| REC-007 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | 待迁移 | 待实现 |
| REC-008 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | 待迁移 | 待实现 |
| PIN-001 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-002 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
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
