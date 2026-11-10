# Tbox 安装与升级

所有平台的正式安装文件统一从 [xsddpx/Tbox Releases](https://github.com/xsddpx/Tbox/releases) 下载。GitHub 自动显示的两个 `Source code` 压缩包不是安装包。

- Windows：Windows 11 x64，下载 `Tbox_<版本>.0_win-x64.msix`。
- Mac：macOS 14 或以上的 Apple Silicon（M 系列），下载 `Tbox_<版本>_osx-arm64.zip`，解压后将 Tbox.app 放到 `~/Applications` 或 `/Applications`。Intel Mac 暂无安装包。

先阅读对应版本说明。`SHA256SUMS` 和 `release-manifest.json` 列出下载文件的大小与 SHA-256；包内附第三方许可。`Tbox_third-party-sources.tar.gz` 提供第三方组件的对应源码、许可和构建材料，普通安装无需下载它。Windows 可用 `Get-FileHash <文件> -Algorithm SHA256`，Mac 可用 `shasum -a 256 <文件>` 检查下载完整性。

## Windows

关闭正在进行的截图/录屏后双击 MSIX 安装。既有用户应保持原包身份和证书，以便覆盖升级并保留设置。

当前项目采用开发者持久签名。首次安装若系统不信任签名，需要核对来源后将配套 `Tbox-Development.cer` 以管理员身份导入本地计算机的“受信任人”（LocalMachine\TrustedPeople）；不要导入根证书颁发机构。双击安装包，按系统提示安装所需的 Microsoft VCLibs 框架。普通用户只需公钥证书，不应索取或导入发布者的 PFX 私钥。

## Mac

当前流程复用持久开发证书，尚未提供 Apple Developer ID 公证，首次打开可能被系统拦截。核实下载来源后，通过系统“隐私与安全性”中的正常打开入口完成确认，不要关闭 Gatekeeper。

截图、长截图和录屏首次使用需要屏幕录制权限；文件夹跳转需要辅助功能权限。授权后按系统提示重新打开 Tbox。后续升级保持原安装位置及签名身份；不要反复删除应用授权。macOS 自身仍可能要求重新确认。

## 应用内升级

在主窗口「关于」页检查并下载更新，然后点击安装。验证通过后，Tbox 会结束当前任务并退出。Mac 安装后自动打开，Windows 由系统安装程序完成升级，再手动打开 Tbox。应用内更新无需 GitHub 账号，只提供正式版本。
