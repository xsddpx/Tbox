# 跨平台逐项验收表

当前共用桌面测试为 56 项（UI 2.0 移除 6 项旧搜索用例，增加 2 项浅深配色对比度用例，Windows 升级增加 7 项设置迁移测试）；保留剪贴板回退、受限重试、超长文字画布及模块热键回滚验证。提交 `b74fa61` 的 [两平台 CI](https://github.com/xsddpx/Tbox/actions/runs/42377475447) 已通过 95 核心 + 36 桌面测试及实际媒体解码/导出。文件夹适配与热键模块开关在提交 `15572d0` 的双平台 CI 通过（95 核心 + 39 桌面）；仍待实机文件面板验收。

媒体集成证据（2026-11-07，本机 Apple Silicon）：`Tbox.MediaSmoke` 对库内合成视频实际解码 36 次输出回调；GIF 导出 24 帧、2000 ms；WebP 导出 20 个合并帧、1999 ms，循环且不放大 320×240；MP4 复制哈希相同。此检查已接入 CI；不验证实际系统采集、音频、原生视频窗口或安装授权。

2026-11-07 重复弹窗修复：系统日志明确报告旧 `cdhash` 授权与当前证书身份不匹配。1.1.1 移除截图入口的自动授权请求，实机点击截图和快捷键均只显示应用内说明；三个新增测试覆盖重复截图/激活、显式请求一次、已授权不再请求。系统设置中仅重新登记 Tbox 后，正常启动的 1.1.1 进程通过 IPC 验证 `screenPermission=true`、`captureVerified=true`，单屏 3024×1964；未保存像素或识别文本。安装脚本修正签名要求的文字参数格式，防止将相同证书误判为不兼容。

连续升级验证详见 [屏幕权限升级记录](macos-permission-upgrade-verification.md)：1.1.1 → 1.1.2 → 1.1.3 两次覆盖安装均保留授权、实际取帧成功，1.1.3 截图界面也已实际打开并取消。

连续升级第一轮：1.1.1 → 1.1.2 在固定安装位置覆盖安装，二进制 SHA-256 不同、指定签名要求相同。由系统正常打开后，两版均通过权限检查与 3024×1964 实际取帧；升级过程未再次申请/登记屏幕权限。1.1.2 开发 ZIP 已通过 95 核心 + 42 桌面测试、离线 OCR、裁剪和实际媒体导出验证。

设置迁移（1.1.4）：四页共用导航、标题区搜索、宽屏固定/窄屏浮层导航、设置行折叠、串行保存、失败通知与缓存不可写回退已实现。53 项桌面测试覆盖所有页面在 1280/900/520 宽度及来回切换下的控件边界、并发设置写入、启动状态待批准/失败恢复与关闭通知。Mac 实际中文搜索“启动”进入常规页、点击截图页与已有屏幕权限显示通过。开机启动的注册、重新登录、系统拒绝及跨升级行为尚未实机验收；当前本地包返回系统尚无启动项登记。Windows 目标构建通过，实机界面仍待验收。

UI 2.0 重构（1.1.5，2026-11-07；提交 `fc5cf01` 的 [Windows/macOS CI](https://github.com/xsddpx/Tbox/actions/runs/42382116528) 全部通过，含两端实际媒体解码/导出）：按最新要求移除应用搜索与搜索路由，四页共用现代主题令牌、线性图标、快捷操作卡片和滑动开关；OCR 工作区与任务浮层复用对应表面。49 项桌面测试、95 项核心测试和 Windows 目标构建通过。四页在浅色/深色、1280/900/520 宽度反复切换后均无可见搜索输入及操作控件横向越界，生成 24 张共用控件渲染预览；文字及图标对比度通过。预览在本地 `TEMP/ui-2.0-preview/`，属于本轮验收产物。

Mac 1.1.5 已覆盖安装至固定位置，独立启动后实际导航常规与截图页通过；选择合成中英文图片完成离线 OCR，结果两行可编辑，展开原图成功。没有改动用户主题、启动项或模块偏好。权限探测通过（应用进程 48101，`screenPermission=true`、`captureVerified=true`，3024×1964），没有再次申请权限。打包同时通过离线 OCR、像素裁剪、实际媒体解码和 GIF/WebP/MP4 导出检查。Windows 实际窗口、各任务浮层完整交互与安装验收仍未完成，不能把交叉编译视为全平台交付。

Windows 共用交付流程（1.1.6，2026-11-07）：新增共用入口 MSIX manifest、版本与载荷检查、持久证书签名和安装验证脚本；沿用原包身份与启动项。7 项设置导入用例、56 项桌面与95 项核心测试通过；包策略测试验证合法配置并拒绝版本不一致、旧程序入口、身份变更、已登记版本、已有包和缺失载荷。PowerShell 语法检查与 Windows 目标编译通过。Windows CI 负责实际发布、媒体验证与 MakeAppx 打包/解包，结果继续登记；当前没有 Windows 原签名证书或实机，正式签名与安装阶段尚未验证。

Mac 1.1.6 已生成唯一 ZIP、覆盖安装并独立启动（进程 53695）。界面显示正确版本，屏幕授权沿用且 3024×1964 实际取帧成功；没有新增授权。打包的 OCR、像素裁剪与媒体导出验证通过。

冻结基线：`0f88802717d6a2a6ad86e117c20ec35ead91f96c`（Windows 1.0.45）。原产品要求可通过 `git show 0f88802:docs/product-requirements.md` 复核。表中的原平台证据是源码定位，不代替迁移后的实机复验。

2026-11-07：共用桌面工程 32 项测试、原核心 95 项测试通过；已运行本机离线中英文 OCR、原始像素裁剪及单显示器取帧（命令行验证；不代表独立启动后的应用授权）。Windows 目标交叉编译通过；提交 `7e6c1a3` 的 [Windows/macOS CI](https://github.com/xsddpx/Tbox/actions/runs/42372131729) 均通过（95 核心 + 20 桌面测试）；新增长截图及独立进程权限探测后本机桌面测试增至 25 项；[长截图提交的双平台 CI](https://github.com/xsddpx/Tbox/actions/runs/42372899931) 通过。Mac 另有拒绝临时签名和错误标识的两项脚本测试。持久签名已完成首次钥匙串确认；旧临时签名登记迁移完成，屏幕权限连续两次升级保留已在本机验收，见 [验证记录](macos-permission-upgrade-verification.md)。混合 DPI 多显示器、Intel Mac、Windows 共用界面均尚未实机验证。截图全流程仍在授权与交互复验中。

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
| APP-003 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs / Platform/*StartupRegistration.cs | 设置已迁移，布局/串行写入/异常状态自动测试通过；1.1.5 已移除搜索，Mac 页面导航与图片 OCR 通过；启动注册及其他交互待实机验收 |
| APP-004 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-005 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs / Platform/*StartupRegistration.cs | 设置已迁移，布局/串行写入/异常状态自动测试通过；1.1.5 已移除搜索，Mac 页面导航与图片 OCR 通过；启动注册及其他交互待实机验收 |
| APP-006 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-007 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-008 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs / Platform/*StartupRegistration.cs | 设置已迁移，布局/串行写入/异常状态自动测试通过；1.1.5 已移除搜索，Mac 页面导航与图片 OCR 通过；启动注册及其他交互待实机验收 |
| APP-009 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | 待逐项复验 |
| APP-010 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs / Platform/*StartupRegistration.cs | 设置已迁移，布局/串行写入/异常状态自动测试通过；1.1.5 已移除搜索，Mac 页面导航与图片 OCR 通过；启动注册及其他交互待实机验收 |
| APP-011 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs / Platform/*StartupRegistration.cs | 设置已迁移，布局/串行写入/异常状态自动测试通过；1.1.5 已移除搜索，Mac 页面导航与图片 OCR 通过；启动注册及其他交互待实机验收 |
| APP-012 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs / Platform/*StartupRegistration.cs | 设置已迁移，布局/串行写入/异常状态自动测试通过；1.1.5 已移除搜索，Mac 页面导航与图片 OCR 通过；启动注册及其他交互待实机验收 |
| UI-001 | 已实现 | TboxUi.cs / MainWindow.xaml（旧外观已被本轮需求覆盖） | Views/DesignTokens.cs / Views/Ui.cs / Views/MainWindow.cs | 1.1.5 共用令牌、浅深布局及对比度测试通过；Mac 导航通过；Windows 实机待验收 |
| UI-002 | 已实现 | TboxUi.cs / MainWindow.xaml（旧外观已被本轮需求覆盖） | Views/DesignTokens.cs / Views/Ui.cs / Views/MainWindow.cs | 1.1.5 共用令牌、浅深布局及对比度测试通过；Mac 导航通过；Windows 实机待验收 |
| UI-003 | 已实现 | TboxUi.cs / MainWindow.xaml（旧外观已被本轮需求覆盖） | Views/DesignTokens.cs / Views/Ui.cs / Views/MainWindow.cs | 1.1.5 共用令牌、浅深布局及对比度测试通过；Mac 导航通过；Windows 实机待验收 |
| UI-004 | 已实现 | TboxUi.cs / MainWindow.xaml（旧外观已被本轮需求覆盖） | Views/DesignTokens.cs / Views/Ui.cs / Views/MainWindow.cs | 1.1.5 共用令牌、浅深布局及对比度测试通过；Mac 导航通过；Windows 实机待验收 |
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
