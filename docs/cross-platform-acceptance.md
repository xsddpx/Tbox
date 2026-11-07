# 跨平台逐项验收表

## 当前公开交付：1.1.15

2026-11-08 03:48:46 +0800 已公开 Windows 11 x64 与 macOS 14+ Apple Silicon 安装包。[正式流程 42407512233](https://github.com/xsddpx/Tbox/actions/runs/42407512233) 全部通过；源码保持私有。公开包已匿名完整下载，大小、SHA-256 与发布清单一致，Mac 深度签名和当前安装版指定签名要求一致，44 个原生二进制满足最低 macOS 14。Windows 实际安装、OCR、取帧、录屏及三格式导出通过；文件夹导航的八次原生检查在串行干净构建后通过。详见 [首发验收记录](releases/v1.1.15-verification.md)。

本机 Mac 仍为 1.1.13，电脑锁屏，应用内升级与升级后权限保留待用户解锁；Windows 旧设备升级、Mac 文件夹跳转/长截图等未验收范围继续保留。新界面候选尚未开始，按 UI-010 在上线及更新验收后提供。

## 历史验证记录

1.1.16 开发修复（尚未交付）：共用 OCR 窗口重新识别时恢复最小化状态；展开原图占可用内容区 35%，折叠后把空间归还编辑器。新增 7 项真实共用控件的 headless 回归，覆盖 460×360、800×600、1200×900 三种尺寸及控件边界、单窗口复用和置顶状态、旧任务取消与迟到结果、关闭取消和复制编辑后的全文。以上回归通过，不等同于两平台实机、多屏来源定位或真实离线识别验收。Mac 当前锁屏；GitHub 在 runner 启动前以账户付款/消费额度原因拒绝运行，旧版在线升级测试已保留在 `codex/test-windows-updates`，等待外部恢复。

1.1.13 两平台 CI 与 Windows 安装验收全部通过：[运行 42402478448](https://github.com/xsddpx/Tbox/actions/runs/42402478448)，应用提交 `aec6414`。Windows 持久签名 MSIX 已实际安装，系统 OCR、像素裁剪、界面渲染、单显示器实际取帧通过；320×240/10 FPS 录屏的解码时长为 3.9 秒，74 次播放回调中红蓝各 37、暂停绿色 0，GIF/WebP 导出和 MP4 原样保存通过，未测试系统声音。下载的 108744413 字节 MSIX 与云端 SHA-256 一致，已登记发布历史；结果在 `TEMP/windows-installed/1.1.13/verification/`。Mac 实际文件夹跳转仍待用户完成更新后的前台热键复验，该轮尚未公开 Release。

Windows 共用文件夹导航已通过 [CI 42402478448](https://github.com/xsddpx/Tbox/actions/runs/42402478448)：前台 Explorer 跟踪两处含中文、空格且末级名称相同的路径，四次真实打开/保存面板均显示各自唯一目录标记，耗时 1473/165/842/155 ms；普通文本窗口保持原内容，模块停止及恢复通过。结果在 `TEMP/folder-smoke-ci/42402478448/result.json`。此前三次失败来自夹具窗口未完成加载/前台切换，修正夹具后通过，没有改动 Windows 生产导航逻辑。该验证已加入正式 Release 的签名打包前置检查；不覆盖全局热键、Explorer 多标签切换或旧用户设备升级。

1.1.13 Mac 文件面板识别调整已打包并固定路径覆盖安装：新增 7 项纯原生归属回归，验证远程面板与宿主窗口分离、不同提供者拒绝及父级循环边界；95 核心、111 桌面和 34 发布检查通过，包内 OCR、像素裁剪、媒体播放与三格式导出通过。进程 37490 的屏幕权限和 3024×1964 实际取帧通过，证据在 `TEMP/install-verification/1.1.13/permission.json`。辅助功能授权在 1.1.12 → 1.1.13 后仍被应用识别，没有重新申请；实际目录导航继续复验，不能以这些回归代替真实面板通过。Windows 第三轮 42401986309 仍未通过夹具的文件面板前台等待，尚未调用导航；继续补充宿主窗口焦点和失败窗口元数据。

2026-11-08 文件夹集成验收：新增 `tests/Tbox.FolderSmoke`，仅允许一次性 GitHub Windows runner 执行，调用共用平台工厂和正式文件夹适配层，验证两处同名目录下四次真实打开/保存跳转、唯一内容标记、普通输入窗口保护及模块停止/恢复。已通过本机 Windows 目标编译；首轮 CI 42401130718 在目录跟踪处超时。夹具补充目录加载完成后的实际离开/返回窗口动作后，第二轮 42401625717 的 Explorer 跟踪成功，但在文件面板创建尚未完成时尝试聚焦失败；继续改为等待系统模态面板可见并取得前台，再调用导航，原生复验待记录。

Mac 1.1.12 辅助功能已由用户认证，系统开关开启，重启后的 Tbox 识别授权。用户在打开图片面板执行真实 Control+G 后，Tbox 返回“请先打开系统标准文件选择或保存面板”，没有到达测试目标目录；Finder 跟踪与面板识别未通过，不能当作授权失效再反复申请。AX 状态与截图保存在 `TEMP/integration-acceptance/1.1.12/`，系统日志确认当前持久身份的辅助功能授权已允许。此次观察还不足以区分前台切换时序与面板识别代码问题。

1.1.12 软件图标更新：采用用户指定的白色透明倒品字 SVG，生成两平台共用 PNG、七种尺寸 ICO 和 Mac ICNS，Mac 菜单栏使用模板着色。Mac 唯一 ZIP 已签名、固定路径覆盖安装并独立启动；安装后 ICNS 的 1024×1024 像素与共用 PNG 完全一致。进程 17616 保留屏幕授权并实际取得 3024×1964 帧，证据在 `TEMP/install-verification/1.1.12/permission.json`；小图预览在 `TEMP/icon-delivery/1.1.12/icon-preview.png`。[两平台 CI 42399093145](https://github.com/xsddpx/Tbox/actions/runs/42399093145) 全部通过，Windows 1.1.12.0 持久签名 MSIX 已实际安装并通过 OCR、截图、原生录屏及三格式保存。下载包的四项 PNG 图块逐字节匹配；EXE 内嵌的 16/24/32/48/64/128/256 像素图标全部与新 ICO 相符，结果在 `TEMP/windows-installed/1.1.12/package/artifacts/icon-verification.json`。安装包哈希已核对并登记，正式公开发布仍待全部迁移验收完成。

1.1.11 Windows 原生录屏检查已通过：[两平台 CI 42398378401](https://github.com/xsddpx/Tbox/actions/runs/42398378401) 全部成功，签名 MSIX 在 Windows 2025 runner 实际安装，OCR、截图、界面、录屏及三格式导出通过。独立用 AVFoundation 解码实际母版：320×240、H.264、10 FPS、3.9 秒、38 帧，其中红/蓝各 19 帧、绿色 0 帧；GIF 47 帧/3910 ms，WebP 2 帧/3916 ms，两者无限循环且颜色与录制阶段一致，MP4 原样保存。LibVLC 的 74 次回调包含重复呈现，不当作源视频编码帧数。证据在 `TEMP/windows-installed/1.1.11/verification/`，安装包哈希已登记。仍未验证 Windows 系统声音、全部帧率、完整 UI 与旧用户设备升级。

1.1.11 增加 Windows 安装版实际录屏验收：320×240、10 FPS 静音录制，红/蓝画面在暂停前后各录制约 2 秒，暂停期间的绿色必须排除，解码时长不得包含暂停；随后导出实际母版的 GIF、WebP 和 MP4。9 项判定回归覆盖黑屏、静帧、缺失片段、暂停画面泄漏和错误时长。当前本机 95 核心、111 桌面、34 发布/材料检查及 Mac 打包检查通过；Mac 已固定路径覆盖安装并独立启动；进程 14018 的权限探测显示 `screenPermission=true`、`captureVerified=true`，3024×1964，未重新申请授权，证据在 `TEMP/install-verification/1.1.11/permission.json`。Windows 云端实际录制已通过，结果见上。此诊断不覆盖系统声音、完整 UI 或旧电脑升级。

1.1.10 两平台 CI 已全部通过：[运行 42396331339](https://github.com/xsddpx/Tbox/actions/runs/42396331339)。Windows 使用固定源码运行库的签名 MSIX 已在 Windows 2025 runner 实际安装、启动并取得屏幕帧；报告 `success=true`、`nativeOcr=true`、`pixelCrop=true`、`uiRendered=true`、`captureVerified=true`、`displayCount=1`。包内媒体验证实际解码 36 帧，GIF 24 帧/2000 ms、WebP 20 帧/1999 ms、MP4 复制一致。下载到本机的 110706316 字节安装包与云端登记哈希一致，已写入发布历史；证据在 `TEMP/windows-installed/1.1.10/`。仍未完成旧 Windows 用户设备升级、全部交互及公开在线更新验收。

1.1.10 依赖替换验证（2026-11-08）：Windows 动画导出改为与 Mac 相同的固定 FFmpeg 8.1.2 / libwebp 1.6.0 源码构建；[独立 Windows 运行验证](https://github.com/xsddpx/Tbox/actions/runs/42394403213) 已通过实际启动、GIF/WebP 导出及播放。新增输入源码、构建脚本、二进制哈希和载荷校验，25 项发布/依赖检查、95 项核心及 102 项桌面测试通过。Mac 1.1.10 唯一 ZIP 已记录哈希，固定路径覆盖安装并正常独立启动；进程 562 的权限探测显示 `screenPermission=true`、`captureVerified=true`，3024×1964，未重新申请权限。证据在 `TEMP/install-verification/1.1.10/permission.json`。Windows 签名安装验收结果见上；第三方材料已通过 [独立 CI](https://github.com/xsddpx/Tbox/actions/runs/42397291104)，当前发布/依赖检查增至 34 项，正式公开发布仍待完成。

1.1.10 长截图实机复验尚未获得有效结果：尝试滚动 Safari 测试页及 Chrome 公开文档时，实际屏幕截图仍是 Codex 前台，未取得目标页面序列；随后检测到窗口被操作并结束该次验收。此记录不证明拼接通过或失败。临时浏览器标签页与 Finder 窗口已清理，不把后台页面滚动等同于真实屏幕采集验证。

当前验证汇总（2026-11-08）：1.1.9 的 [Windows/macOS CI](https://github.com/xsddpx/Tbox/actions/runs/42393228787) 全部通过，含两端 95 核心、102 共用桌面、20 发布事务检查、实际媒体解码/导出和 Windows MSIX 载荷检查。Windows 1.1.9.0 在 Windows 2025 runner 以持久证书签名、实际安装并按包身份启动，系统 OCR、像素裁剪、界面渲染及实际屏幕取帧通过，`captureVerified=true`、`displayCount=1`。签名安装包已下载并核对 SHA-256，登记在发布历史；原始报告在本地 `TEMP/windows-installed/1.1.9/verification/`。Windows 1.1.8 检查没有执行取帧，并非已证实无显示器。旧 Windows 用户电脑覆盖升级与其余完整交互仍待验收。

Mac 1.1.8 真实跨应用剪贴板验收：从贴图复制，关闭来源贴图，再在 Chrome 使用系统粘贴；得到 160×100 PNG，四个采样点 RGBA 与原图完全一致，常驻进程保持运行，未产生新崩溃日志。证据为本地 `TEMP/clipboard-real-test/1.1.8/result.json` 与 `result.png`。这补齐了此前被前台操作变化中止的验收。

Mac 1.1.8 真实动态有声录屏：通过截图选区进入录屏，选 25 FPS、启用系统声音，完成开始、暂停、继续、停止、原生回放和 MP4 保存。输出 400×250、22.88 秒、572 帧；六个时间点图像各不相同，AAC 48 kHz 双声道的解码波形非静音，主频 439.45 Hz 与 440 Hz 测试音相符。暂停时界面计时保持，保存后进程仍运行。证据为本地 `TEMP/recording-real-test/1.1.8/analysis.json`、`dynamic-audio-25fps.mp4` 和 `dynamic-preview.jpg`；该检查不证明严格音画同步。实际 WebP 导出、重录、多显示器及 Windows 系统采集仍待验收。保存后发现从 Finder 重新打开无法恢复主窗口，另行修复，不能把进程存活当作完整交互通过。

Mac 重新打开修复（1.1.9）：关闭主窗口使其隐藏后，通过 Finder 的“打开”恢复同一进程的主窗口；最小化后重新打开亦恢复。6 项回归覆盖隐藏、最小化、普通激活和退出中的排队事件。覆盖安装后进程 88185 的屏幕授权、3024×1964 取帧均通过，未重新申请权限。唯一安装包哈希已登记；界面及独立进程证据在本地 `TEMP/reopen-real-test/1.1.9/`。Windows 原生取帧检查已接入安装验收，将单独记录实际结果。

Mac 重录与动画导出（1.1.9，2026-11-08）：真实录屏回放点击“重新录制”，确认放弃后回到同一选区的待录制控制条；再次录制默认为 MP4，改选 WebP，通过系统保存面板导出成功。结果 400×250、8791 ms、18 个不同画面、无限循环，未放大；母版关闭后从 Finder 重新打开恢复主窗口，进程仍为 88185，屏幕授权和取帧正常。证据在本地 `TEMP/recording-real-test/1.1.9/analysis.json`、`dynamic-rerecorded.webp`、`after-save-reopened.png`。导出取消、错误重试及 Windows 真实录制仍待验收。

历史阶段共用桌面测试为 56 项（UI 2.0 移除 6 项旧搜索用例，增加 2 项浅深配色对比度用例，Windows 升级增加 7 项设置迁移测试）；保留剪贴板回退、受限重试、超长文字画布及模块热键回滚验证。提交 `b74fa61` 的 [两平台 CI](https://github.com/xsddpx/Tbox/actions/runs/42377475447) 已通过 95 核心 + 36 桌面测试及实际媒体解码/导出。文件夹适配与热键模块开关在提交 `15572d0` 的双平台 CI 通过（95 核心 + 39 桌面）；仍待实机文件面板验收。

媒体集成证据（2026-11-07，本机 Apple Silicon）：`Tbox.MediaSmoke` 对库内合成视频实际解码 36 次输出回调；GIF 导出 24 帧、2000 ms；WebP 导出 20 个合并帧、1999 ms，循环且不放大 320×240；MP4 复制哈希相同。此检查已接入 CI；不验证实际系统采集、音频、原生视频窗口或安装授权。

2026-11-07 重复弹窗修复：系统日志明确报告旧 `cdhash` 授权与当前证书身份不匹配。1.1.1 移除截图入口的自动授权请求，实机点击截图和快捷键均只显示应用内说明；三个新增测试覆盖重复截图/激活、显式请求一次、已授权不再请求。系统设置中仅重新登记 Tbox 后，正常启动的 1.1.1 进程通过 IPC 验证 `screenPermission=true`、`captureVerified=true`，单屏 3024×1964；未保存像素或识别文本。安装脚本修正签名要求的文字参数格式，防止将相同证书误判为不兼容。

连续升级验证详见 [屏幕权限升级记录](macos-permission-upgrade-verification.md)：1.1.1 → 1.1.2 → 1.1.3 两次覆盖安装均保留授权、实际取帧成功，1.1.3 截图界面也已实际打开并取消。

连续升级第一轮：1.1.1 → 1.1.2 在固定安装位置覆盖安装，二进制 SHA-256 不同、指定签名要求相同。由系统正常打开后，两版均通过权限检查与 3024×1964 实际取帧；升级过程未再次申请/登记屏幕权限。1.1.2 开发 ZIP 已通过 95 核心 + 42 桌面测试、离线 OCR、裁剪和实际媒体导出验证。

设置迁移（1.1.4）：四页共用导航、标题区搜索、宽屏固定/窄屏浮层导航、设置行折叠、串行保存、失败通知与缓存不可写回退已实现。53 项桌面测试覆盖所有页面在 1280/900/520 宽度及来回切换下的控件边界、并发设置写入、启动状态待批准/失败恢复与关闭通知。Mac 实际中文搜索“启动”进入常规页、点击截图页与已有屏幕权限显示通过。开机启动的注册、重新登录、系统拒绝及跨升级行为尚未实机验收；当前本地包返回系统尚无启动项登记。Windows 目标构建通过，实机界面仍待验收。

UI 2.0 重构（1.1.5，2026-11-07；提交 `fc5cf01` 的 [Windows/macOS CI](https://github.com/xsddpx/Tbox/actions/runs/42382116528) 全部通过，含两端实际媒体解码/导出）：按最新要求移除应用搜索与搜索路由，四页共用现代主题令牌、线性图标、快捷操作卡片和滑动开关；OCR 工作区与任务浮层复用对应表面。49 项桌面测试、95 项核心测试和 Windows 目标构建通过。四页在浅色/深色、1280/900/520 宽度反复切换后均无可见搜索输入及操作控件横向越界，生成 24 张共用控件渲染预览；文字及图标对比度通过。预览在本地 `TEMP/ui-2.0-preview/`，属于本轮验收产物。

Mac 1.1.5 已覆盖安装至固定位置，独立启动后实际导航常规与截图页通过；选择合成中英文图片完成离线 OCR，结果两行可编辑，展开原图成功。没有改动用户主题、启动项或模块偏好。权限探测通过（应用进程 48101，`screenPermission=true`、`captureVerified=true`，3024×1964），没有再次申请权限。打包同时通过离线 OCR、像素裁剪、实际媒体解码和 GIF/WebP/MP4 导出检查。Windows 实际窗口、各任务浮层完整交互与安装验收仍未完成，不能把交叉编译视为全平台交付。

Windows 共用交付流程（1.1.6，2026-11-07）：新增共用入口 MSIX manifest、版本与载荷检查、持久证书签名和安装验证脚本；沿用原包身份与启动项。7 项设置导入用例、56 项桌面与95 项核心测试通过；包策略测试验证合法配置并拒绝版本不一致、旧程序入口、身份变更、已登记版本、已有包和缺失载荷。PowerShell 语法检查与 Windows 目标编译通过。提交 `33c14db` 的 [双平台 CI](https://github.com/xsddpx/Tbox/actions/runs/42383182814) 已通过。Windows 已实际执行自包含发布、包内媒体验证、MakeAppx 打包/解包和载荷检查；未签名验证包 SHA-256 为 `B3CEA961C39B8B219C5F0D42990518D1FC3E801B9343C3EB5AE93C6374FBA4C5`。报告明确 `signed=false`、`installed=false`，本地记录在 `TEMP/windows-ci-33c14db/`；当前没有 Windows 原签名证书或实机，正式签名与安装阶段尚未验证。

Mac 1.1.6 已生成唯一 ZIP、覆盖安装并独立启动（进程 53695）。界面显示正确版本，屏幕授权沿用且 3024×1964 实际取帧成功；没有新增授权。打包的 OCR、像素裁剪与媒体导出验证通过。

冻结基线：`0f88802717d6a2a6ad86e117c20ec35ead91f96c`（Windows 1.0.45）。原产品要求可通过 `git show 0f88802:docs/product-requirements.md` 复核。表中的原平台证据是源码定位，不代替迁移后的实机复验。

2026-11-07：共用桌面工程 32 项测试、原核心 95 项测试通过；已运行本机离线中英文 OCR、原始像素裁剪及单显示器取帧（命令行验证；不代表独立启动后的应用授权）。Windows 目标交叉编译通过；提交 `7e6c1a3` 的 [Windows/macOS CI](https://github.com/xsddpx/Tbox/actions/runs/42372131729) 均通过（95 核心 + 20 桌面测试）；新增长截图及独立进程权限探测后本机桌面测试增至 25 项；[长截图提交的双平台 CI](https://github.com/xsddpx/Tbox/actions/runs/42372899931) 通过。Mac 另有拒绝临时签名和错误标识的两项脚本测试。持久签名已完成首次钥匙串确认；旧临时签名登记迁移完成，屏幕权限连续两次升级保留已在本机验收，见 [验证记录](macos-permission-upgrade-verification.md)。混合 DPI 多显示器、Intel Mac、Windows 共用界面均尚未实机验证。截图全流程仍在授权与交互复验中。

| ID | 原状态 | 原平台源码证据（src/Tbox.App，另注除外） | 共用/目标平台入口（src/Tbox.Desktop，另注除外） | 迁移验收 |
| --- | --- | --- | --- | --- |
| FJ-001 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/*FolderNavigation.cs / Platform/macos/TboxFolders.swift | Windows 云端真实两目录跟踪通过，多标签仍待复验；Mac 待实机复验 |
| FJ-002 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/*FolderNavigation.cs / Platform/macos/TboxFolders.swift | Windows 四次实际打开/保存导航通过，全局热键未覆盖；Mac 标准面板复验中 |
| FJ-003 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/*FolderNavigation.cs / Platform/macos/TboxFolders.swift | Windows 复用原重试；Mac 不稳定标题保留旧路径；待实机复验 |
| FJ-004 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Core/FolderTargetPolicy.cs | 两端共用目录校验和主目录回退；待实机复验 |
| FJ-005 | 已实现 | FolderJumpModule.cs / ExplorerFolderTracker.cs / FileDialogNavigator.cs | Platform/*FolderNavigation.cs / Platform/macos/TboxFolders.swift | Windows 中文同名目录与唯一内容标记验证通过；Mac 面板属性仍需实际验证 |
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
| OCR-001 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 共用贴图入口的原像素、位置、显隐与标注保持回归通过；真实交互待验收 |
| OCR-002 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 原图合成隔离及贴图 OCR 输入像素回归通过；完整截图流程待验收 |
| OCR-003 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Views/OcrWindowSource.cs | 1.1.16 已接来源工作区与中心，四档缩放、负坐标、小工作区及贴图置顶回归通过；真实多屏待验收 |
| OCR-004 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 三种尺寸的原图 35% 布局、折叠归还空间和复制当前全文回归通过；实机待验收 |
| OCR-005 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 单窗口复用、最小化恢复、旧任务取消与迟到结果、关闭取消回归通过；实机待验收 |
| OCR-006 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| OCR-007 | 已实现 | TextRecognition.cs / OcrResultWindow.cs | Views/OcrWindow.cs / Platform/MacPlatformServices.cs / Platform/windows/WindowsPlatformServices.cs | 待逐项复验 |
| REC-001 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | Mac 1.1.8/1.1.9 从截图选区进入独立控制、停止回放通过；Windows 1.1.11 安装版实际静音采集、停止解码通过，完整界面待验收 |
| REC-002 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | Mac 默认 10 FPS、切换 25 FPS、暂停冻结/继续/停止通过；Windows 1.1.11 的 10 FPS 暂停画面排除及有效时长通过；边框排除、其他帧率和完整界面待复验 |
| REC-003 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | Mac 25 FPS、400×250 动态 H.264 与系统 AAC 测试音通过；音频降级、指针及 Windows 待验收 |
| REC-004 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | Mac 动态回放、元数据、暂停与有效时长通过；Windows 1.1.11 实采母版的包内原生解码通过；完整回放界面及快捷键待验收 |
| REC-005 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | Mac 重录后默认 MP4、切换 GIF/WebP 通过；Windows 实际格式交互待验收 |
| REC-006 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | Mac 实际 GIF、WebP、MP4 保存已验证；MP4 有声，WebP 循环且不放大；Windows 1.1.11 的 320×240 实采三格式导出通过；大尺寸及完整格式交互待验收 |
| REC-007 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | 自动化文件保护/取消测试通过；实际转换保存通过；实际取消、失败恢复待验收 |
| REC-008 | 已实现 | ScreenRegionRecorder.cs / RecordingControlWindow.cs / RecordingReviewWindow.cs | Views/RecordingControlWindow.cs / Views/RecordingReviewWindow.cs / Models/RecordingOperation.cs / Platform/*RegionRecorder.cs | Mac 放弃确认、重录、保存后进程存活且重新打开主窗通过；实际异常路径及 Windows 待验收 |
| PIN-001 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-002 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs / Models/ClipboardReader.cs / DesktopController.cs | 格式回退、占用三轮上限、恢复及超长文字画布测试通过；实际剪贴板交互待复验 |
| PIN-003 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-004 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-005 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | Mac 图片复制后关闭来源，跨应用粘贴尺寸及像素一致、未崩溃；标注提交与其他菜单交互待复验 |
| PIN-006 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-007 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| PIN-008 | 已实现 | PinnedImageWindow.cs / ScreenPasteModule.cs | Views/PinnedWindow.cs | 待逐项复验 |
| APP-001 | 已实现 | MainWindow.xaml.cs / App.xaml.cs / StartupManager.cs | App.cs / DesktopController.cs / Views/MainWindow.cs | Mac 1.1.9 隐藏与最小化后从 Finder 重新打开恢复既有主窗口、进程不变；Windows 实际重复启动待验收 |
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
| UI-009 | 新增 | 用户指定 v5 倒品字 SVG | assets/branding/tbox.svg / scripts/generate-app-icons.py / App.cs | 1.1.12 两平台新图标安装验证通过；Mac ICNS 像素一致、正常启动及权限保留，Windows 安装图块与 EXE 七尺寸图标完全一致 |
| REL-001 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-002 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-003 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-004 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-005 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |
| REL-006 | 已实现 | scripts/package-msix.ps1 / docs/release-history.md | scripts/build-macos.sh / scripts/package-macos.sh | 待逐项复验 |

录屏实现进展（2026-11-07）：Windows 链接原 `ScreenRegionRecorder`，Mac 使用 ScreenCaptureKit + AVAssetWriter；共用操作负责声音失败降级、暂停时钟、等待原生释放再清理。回放使用 LibVLCSharp 的 Avalonia 组件，GIF/WebP 使用外部 FFmpeg 进程。32 项桌面测试、95 项核心测试和两目标编译已通过；Mac 媒体组件打包与真实回放仍在验证，不能视为完成发布。

Mac 实际静音录制首轮（1.1.6，2026-11-07）：正常启动的安装版从截图选区进入录屏控制，默认 10 FPS、系统声音关闭；开始、暂停状态、继续、停止进入回放均通过。原生回放时间从 00:00 推进到 00:07。选择 GIF 并通过原生保存面板导出到本地 `TEMP/recording-real-test/1.1.6/recording-silent.gif`；结果尺寸 300×200、94 帧、7830 ms、无限循环。该小区域画面静止，因此只验证静态画面时长保持，不能证明动态内容或音画同步。导出后回放关闭，录屏会话目录为空。实际 WebP/MP4 导出、系统声音、重录、不同帧率、动态内容与多屏仍待后续实机验收；现有合成媒体 CI 不替代这些检查。
