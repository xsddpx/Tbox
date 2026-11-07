# 自动发布 Windows 与 Mac 安装包

## 流程与范围

2026-11-08 首发安排：用户明确要求上线，首发版本顺延为 1.1.15。1.1.14 已打包 Mac，但 Windows 干净环境编译冲突导致整轮未公开；已打包版本不复用。Windows 文件面板门槛现由同一脚本串行构建并独立运行，普通 CI 在任何其他 .NET 构建前验证它。[1.1.15 正式发布流程](https://github.com/xsddpx/Tbox/actions/runs/42407512233) 已全部成功，7 项附件已公开，两个安装包已匿名下载校验。首发不代表全部迁移完成；本机锁屏，应用内升级验收待解锁。

`.github/workflows/release.yml` 在私有源码仓库 **发布 Release**（`release.published`）时启动。它只接受已合入 `main` 的提交和 `v<主>.<次>.<补丁>` 标签。创建草稿、普通推送不会发布软件。

1. 检查证书配置、源码仍为私有、下载仓库仍为公开、所有版本文件一致、版本未打包、公开版本说明存在。
2. Windows 2025 x64 与 macOS 15 Apple Silicon 并行运行现有测试、媒体检查和打包脚本。Windows 签名、验签、安装 MSIX，并以包身份启动 OCR/裁剪/界面检查；Mac 复用持久签名，验签后运行 OCR/裁剪/界面与媒体检查。
3. 汇总 Windows MSIX、Mac `.app` ZIP、Windows 公钥证书、第三方源码归档与中文安装说明，生成 `SHA256SUMS`、`release-manifest.json`。不发布未签名验证包、日志、Tbox 私有业务源码或签名私钥。
4. 在公开的 [Tbox-releases](https://github.com/xsddpx/Tbox-releases/releases) 创建草稿，上传全部文件；逐一核对 GitHub 返回的大小、上传状态和 SHA-256。任一平台失败或文件校验失败时，不公开此版本。
5. 将实际打包时间及哈希提交到源码仓库的 `codex/release-record-vX.Y.Z` 分支，尝试创建发布历史 PR。记录成功推送后才公开 Release。公开版本说明来自 `docs/releases/vX.Y.Z.md`，不会复制私有 Release 的正文。
6. 保留源 Release 的预发布标记；预发布不会成为应用更新源的正式最新版。正式版本公开后，两平台更新器可匿名发现它。

当前覆盖 Windows 11 x64、macOS 14+ Apple Silicon。Intel Mac 尚未接入。当前证书属于持久开发签名，流程不等于 Apple 公证或商业可信签名；安装限制见 [公开安装说明](releases/INSTALL.md)。完整功能与用户设备升级验收仍遵循活需求文档。

## 首次配置

在**私有源码仓库**的 [Actions Secrets and variables](https://github.com/xsddpx/Tbox/settings/secrets/actions) 配置下表。不得把私钥上传到公开仓库或提交 Git。

| 类型 | 名称 | 内容 |
| --- | --- | --- |
| Secret | `TBOX_WINDOWS_PFX_BASE64` | 已固定的 Windows 签名证书及私钥的 PFX，Base64 编码；发布者必须是 `CN=Tbox Development` |
| Secret | `TBOX_WINDOWS_PFX_PASSWORD` | PFX 密码，使用非空密码 |
| Secret | `TBOX_MAC_P12_BASE64` | 当前持久 Mac 签名身份的 P12，仅导出 Tbox 这一个身份；Base64 编码 |
| Secret | `TBOX_MAC_P12_PASSWORD` | P12 密码，使用非空密码 |
| Secret | `TBOX_RELEASE_TOKEN` | 仅授权 `xsddpx/Tbox-releases`、Repository Contents **Read and write** 的 fine-grained token；下载用户不需要该令牌 |
| Variable | `TBOX_WINDOWS_CERT_SHA1` | 已固定的 Windows 签名证书的 40 位指纹，无空格/冒号 |
| Variable | `TBOX_MAC_CERT_SHA1` | 原 Mac 签名证书的 40 位指纹，无空格/冒号 |
| Variable | `TBOX_MAC_SIGN_IDENTITY` | 可选；默认 `Tbox Local Development` |

2026-11-08：用户确认没有原 Windows PFX；本机及项目相关下载目录未找到可恢复副本，已一次性建立 Windows 持久开发签名身份并存入 Actions Secrets。Windows 指纹为 `10859D3FDA689CC8CCC92143C79DA6CCFCDA5D92`。发布者仍为 `CN=Tbox Development`，旧 Windows 用户需首次信任新的公钥证书；原电脑覆盖升级尚未验证，不能承诺无提示升级。以后不得逐轮生成新证书。

Windows 加密 PFX、原 Mac 身份导出的加密 P12 均备份在本机 `~/Library/Application Support/Tbox/Signing/`，目录权限 0700、私钥包权限 0600；密码保存在登录钥匙串。仅导出匹配 Tbox 指纹的 Mac 身份，没有导出其他钥匙串身份。四个证书 Secrets 和两平台指纹变量已配置；公开发布令牌也已配置。用户先要求 10 年，GitHub 自定义期限上限为 1 年，经用户再次选择后改为不设到期时间，可随时撤销。令牌 `Tbox Release Publisher` 仅授权 `xsddpx/Tbox-releases` 的 Contents Read and write 与必要的 Metadata Read-only，没有账号权限；真实 API 验证可访问公开下载仓库、无法访问私有源码仓库。令牌仅保存到私有仓库的 `TBOX_RELEASE_TOKEN` Secret，临时明文文件已删除。管理入口为 https://github.com/settings/personal-access-tokens/22557510 。

当前本机 Mac 指纹为 `FA0F60B5D7C8EE3DCFE356A0D564F8F0B1BE2EE6`。只传同一个身份，不重新生成证书。不能把本机 `gh` 的广泛账号令牌直接保存为发布 Secret。

Windows 包装脚本只在一次性的 GitHub 托管 runner 导入指定证书，并把对应叶证书放入本地计算机“受信任人”（LocalMachine\TrustedPeople），结束后删除。Mac 使用一次性钥匙串，结束后删除；不清空用户设备授权、不修改系统根证书信任。

`GITHUB_TOKEN` 只用于源码仓库的读取与发布记录分支。跨仓库上传使用单独的受限令牌；这是因为内置令牌的仓库范围有限，见 [GitHub 官方认证说明](https://docs.github.com/en/actions/tutorials/authenticate-with-github_token)。

当前仓库不允许 Actions 创建 PR。流程会保留已推送的发布记录分支并输出提醒，公开发布仍可继续。若希望自动创建记录 PR，可在 Actions → General → Workflow permissions 开启 “Allow GitHub Actions to create and approve pull requests”；不需要开启默认全仓库写权限。流程不会自行修改此设置或自动合并记录 PR。

## 每次发布

1. 同步提升 `Tbox.Desktop.csproj` 的 Version/AssemblyVersion/FileVersion、Mac `Info.plist`、Windows `Package.appxmanifest`。使用从未本地或 CI 打包过的新版本，不能重新打包 1.1.8。
2. 完成功能、安装、升级及第三方依赖分发验收。添加 `docs/releases/vX.Y.Z.md` 的中文版本说明，写清已验证范围与限制。将变更合入 main。
3. 在 [源码仓库 Releases](https://github.com/xsddpx/Tbox/releases) 选择这个 main 提交，创建对应标签并点击 **Publish release**。也可以用 `gh release create vX.Y.Z --repo xsddpx/Tbox --target main --notes-file docs/releases/vX.Y.Z.md`。发布预览时加 `--prerelease`；标签本身仍使用三段版本。
4. 查看 [Publish desktop installers](https://github.com/xsddpx/Tbox/actions/workflows/release.yml)。成功后，公开仓库的对应 Release 包含两个安装包、Windows 公钥证书、第三方源码归档、说明和校验清单；原私有 Release 仅承担触发入口。
5. 合并自动生成的发布记录 PR（或提示中的记录分支），再验收真实在线更新。不要通过移动旧标签、覆盖旧包或删除已有正式 Release 来处理问题。

打包与上传耗用已有 Actions 配额；本次没有变更计费或开通付费 runner。

## 失败与重试

构建失败时不会运行发布任务，修复后可重跑失败任务；已经成功的另一个平台产物保留 7 天。上传/记录失败可能留下公开仓库草稿；自动流程拒绝覆盖或接管它，需要检查失败日志与资产后处理，或发布新的版本。已经公开的同版本不会被重写。

若 GitHub 返回的资产还没有 SHA-256、大小不符或缺失平台，任务会失败并保持草稿。不能通过跳过检查强行公开。发布记录推送成功但最终公开请求失败时，记录保留，供核对恢复。

## 验证与当前状态

38 项离线回归测试覆盖标签注入/版本范围、版本与包身份不一致、缺说明、复用版本、漏包、额外私钥文件、篡改文件、实际打包时间、上传损坏、草稿/正式切换、预发布保留、审计失败、固定依赖、跨系统换行及源码材料完整性。运行：

```sh
python3 -m unittest discover -s tests/release -v
```

现有双平台 CI 也运行这些测试，提交 `46a8da0` 的 [双平台检查](https://github.com/xsddpx/Tbox/actions/runs/42389041205) 全部通过。临时变量 `TBOX_VERIFY_SIGNING=true` 可在当前迁移分支的推送中启用证书导入、实际签名、验签和指纹核对：Windows 使用独立包身份的 MSIX 夹具，Mac 签名隔离的系统可执行文件副本，不生成 Tbox 安装包，PR 不获得证书；验证完成后关闭该变量。当前代码已合入 main，1.1.15 真实签名、安装/运行、完整附件校验、审计分支记录和公开发布均通过；源提交为 `08bf212`，公开时间为 2026-11-08 03:48:46 +0800。详见 [首发验收记录](releases/v1.1.15-verification.md)。公开环境的旧版应用内升级仍待验收。

事件与 runner 的选择依据 [GitHub Release 事件](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#release)、[托管 runner](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)；上传校验使用 [Release assets API](https://docs.github.com/en/rest/releases/assets) 的 `size`、`state`、`digest`。

## 历史验证记录

以下记录描述各轮当时状态；当前正式发布状态见上。

首次云端签名检查发现并修正两处集成差异：Mac `codesign` 的证书导出前缀需使用等号参数；Windows MSIX 需使用本地计算机的 TrustedPeople 信任，并以独立 MSIX 夹具验证包签名策略，不用普通 PE 文件代替。Windows 信任位置依据 [Microsoft 包签名证书说明](https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing)，从未将开发证书加入根证书颁发机构。修复后的 [完整 CI](https://github.com/xsddpx/Tbox/actions/runs/42390103544) 已通过：两平台 95 项核心、96 项桌面和 20 项发布事务测试、媒体验证、Windows MSIX 结构检查，以及两平台实际证书导入、签名、验签和指纹核对。Windows 签名夹具使用独立的 `Tbox.SigningFixture` 包身份，未安装为 Tbox、未作为下载产物发布。验证后已将 `TBOX_VERIFY_SIGNING` 关闭。

2026-11-08 真实 Windows 安装检查通过：[运行 42390887997](https://github.com/xsddpx/Tbox/actions/runs/42390887997) 生成唯一的 1.1.8.0 签名 MSIX，在 Windows 2025 runner 实际安装、按包身份启动并通过系统 OCR、像素裁剪及界面检查；产物哈希已登记。检查结束后 `TBOX_VERIFY_INSTALLER` 已关闭。尚未执行公开 Release 事务，也未验证旧用户电脑覆盖升级。

安装验证支持显式 `-VerifyCapture`：只有实际取得至少一个显示器原始帧才通过，当前迁移分支的可选 Windows 安装作业已启用。普通 CI 的 `-VerifyOnly` 仅在隔离临时目录校验未签名载荷，可重复检查已登记版本；交付模式仍严格拒绝版本或文件重复。两端开发包记录按各自章节插入，避免混入另一平台表格。

Windows 安装及正式发布作业还启用 `-VerifyRecording`：安装版通过原生采集器录制 320×240、10 FPS 静音画面，暂停前后分别显示红色和蓝色，暂停期间显示绿色。使用包内 LibVLC 解码，要求两段有效画面均存在、绿色未进入输出、时长已扣除暂停；再使用实录母版导出 GIF、WebP 并校验 MP4 原样保存。失败保留诊断视频与报告，不登记为成功包。该项不证明系统声音、所有帧率或用户完整交互已通过。

2026-11-08 的 [1.1.9 检查](https://github.com/xsddpx/Tbox/actions/runs/42393228787) 全部通过；Windows 实际签名安装后额外取帧成功（1 个显示器），安装产物下载复核哈希一致并登记。临时安装验收开关已再次关闭，避免重复打包同版。正式 Release 事务尚未运行。

公开分发准备：旧 Windows FFmpeg 为 `n8.1.2-22-g94138f6973`，不能只用基础 8.1.2 归档代表对应源码。新增独立 Windows 导出运行库构建检查（`windows-media.yml`），使用固定 FFmpeg 8.1.2、libwebp 1.6.0 源码与最小配置；记录来源哈希、构建脚本哈希、二进制哈希及系统 DLL 导入，生成对应源码归档，再在 Windows 上实际验证 GIF/WebP 导出。后续 1.1.10 已接入共用工程，并通过实际 Windows 签名安装、启动及取帧检查。

固定源码 Windows 导出运行库已在 [独立构建及 Windows 实测](https://github.com/xsddpx/Tbox/actions/runs/42394403213) 通过，输入为 FFmpeg 8.1.2、libwebp 1.6.0，构建结果约 6.5 MB，只依赖系统 DLL。1.1.10 共用工程从 `TEMP/media-runtime/windows` 载入该运行库，不再复制旧 WinUI 的 FFmpeg 目录。

正常双平台 CI 和 Release 均调用 `windows-media.yml` 构建并传递该产物；构建缓存按完整脚本哈希隔离，消费前核对源码、配方和二进制哈希。Windows 打包需要 Python 3，并拒绝缺失许可、旧 DLL 混入或文件被改写。若在本地 Windows 开发，从对应成功 CI 下载 `windows-media-runtime` 到 `TEMP/media-runtime/windows`，运行 `python scripts/verify-windows-media.py` 后再构建；也可在 Linux 执行 `bash scripts/prepare-windows-media.sh` 自行构建。对应源码归档作为同次 CI 的 `windows-media-source` 保存；正式公开发布由独立源码材料任务汇总 VLC、其 contrib 输入及 LibVLCSharp 固定提交。

第三方源码发布门槛：`third-party-sources.yml` 读取版本化清单，校验 128 个归档/头文件输入及 7 个 Git 固定提交材料；其中包括 125 个 VLC contrib 输入（排除两个未分发的 WiX 安装工具）、3 个顶层源码归档。版本必须与共用项目和构建配方一致，源码归档必须匹配原始哈希。它将许可、上游源码、补丁/配方和构建说明汇成 `Tbox_third-party-sources.tar.gz`。发布汇总缺少该归档、清单变更或哈希不符时拒绝继续；公共校验清单覆盖源码包。具体范围及重建方式见 [第三方材料说明](third-party-sources.md)。[独立 CI](https://github.com/xsddpx/Tbox/actions/runs/42397291104) 已通过，135 项输入生成的归档为 700588719 字节；云端与本机 SHA-256 均为 `b6b5d388281f9c8a4d447075dc2ea4f00a079b877fcbc5ed51a69e97dd5b541c`。独立逐项读取归档再次核对全部输入通过。

1.1.10 实际安装包离线发布演练通过：使用已验收的 Mac ZIP、Windows 签名 MSIX、公钥证书及上述源码包，生成完整的 7 个公开资产和校验清单；原始打包时间与哈希均与发布历史相符。演练只在 `TEMP/release-rehearsal/1.1.10/` 生成文件，没有创建 GitHub Release 或触发客户端升级；已打包版本仍不可再次打包发布。
