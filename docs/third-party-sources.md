# 第三方源码与构建材料

每个公开版本的发布正文都链接对应的 `Tbox_third-party-sources.tar.gz`。归档包含 FFmpeg 8.1.2、libwebp 1.6.0、VLC 3.0.24、LibVLCSharp 3.10.2、RustDesk 1.5.3 的上游源码和对应构建材料。`source-manifest.json` 记录来源、固定提交和 SHA-256，并保留 VLC 归档输入的上游 SHA-512。Git 源码归档由清单中的完整提交生成。

VLC contrib 材料也涵盖可选组件。VLC 核心使用 LGPL，部分插件及依赖使用 GPL 或其他许可。各组件归档中的 COPYING、LICENSE、NOTICE、PATENTS 等文件保留原许可和版权声明，`licenses/` 提供应用随附的许可副本。

## 图像导出程序

Windows 与 Mac 的 FFmpeg/libwebp 源码未作修改，配置选项保存在 `scripts/prepare-windows-media.sh` 和 `scripts/prepare-macos-media.sh`。Windows 构建需要 Linux、MinGW-w64、nasm、pkg-config、make；Mac 构建需要 Apple Silicon macOS、Xcode Command Line Tools 和 pkg-config。

Mac 媒体编译显式固定 `MACOSX_DEPLOYMENT_TARGET=26.0`，与应用的最低系统版本一致。配置变化时清理旧目标文件，并用随附的 `verify-macos-deployment-target.py --deployment-target 26.0` 检查媒体运行库的 Mach-O 部署元数据。

将 `upstream/ffmpeg-8.1.2.tar.xz` 与 `upstream/libwebp-1.6.0.tar.gz` 放到 `TEMP/windows-media-build/sources/` 后，在归档根目录运行 `bash scripts/prepare-windows-media.sh`。脚本校验输入，输出独立运行的 LGPL FFmpeg 及构建记录；未启用 GPL 或非自由组件。Mac 将 `upstream/` 的三个归档复制到 `TEMP/media-sources/`，运行 `bash scripts/prepare-macos-media.sh`；该脚本还会下载并校验 VideoLAN 官方 Mac 分发包，以复制所选 LibVLC 动态库和插件。

## 回放运行库

解压 `upstream/vlc-3.0.24.tar.xz` 后，`contrib/src/` 包含各依赖的版本、补丁和构建配方，`extras/package/win32/` 与 `extras/package/macosx/` 包含平台打包脚本。构建说明以该版本的 README、INSTALL 和这些脚本为准。`vlc-contrib/` 保存配方声明的原始归档和头文件，可按文件名复制到 VLC 的 `contrib/tarballs/`；Git 来源在 `git/`，完整提交及压缩包根目录记录在清单中。若离线重建 Git 依赖，应按配方要求调整归档根目录和文件名，并重新压缩为对应 `.tar.xz`；源代码内容保持原样。通用编译器和系统 SDK 由使用者安装。

Windows 使用 VideoLAN.LibVLC.Windows 3.0.24 的官方 NuGet 重分发；Mac 使用 VideoLAN VLC 3.0.24 arm64 官方分发。Tbox 未修改这些原生库源码。LibVLCSharp 及 Avalonia 绑定来自清单中同一提交，其项目文件和构建说明位于对应 Git 归档内。

运行库采用独立 FFmpeg 程序和动态 LibVLC/LibVLCSharp，允许按相应开源许可替换组件及为调试修改而进行必要的逆向工程。自行修改 Mac 安装内容会改变应用签名，需要自行重签名；不要把修改后的包当作官方更新包。

## 远程桌面引擎

`git/` 内的 RustDesk 客户端和 `hbb_common` 归档分别对应固定提交。将协议归档内容放入客户端的 `libs/hbb_common/`，按 `scripts/prepare-remote-engine.sh` 应用 `src/Tbox.Remote/rustdesk/` 下的补丁并放入 Tbox 授权库。脚本也能直接检出同样的两个提交。

`.github/workflows/remote-viewers.yml` 记录 Flutter 绑定生成、vcpkg 依赖和平台构建步骤。引擎中的 `RustDesk-source.txt` 列出客户端、协议和 Tbox 补丁的源码提交。远控组件保留 RustDesk 的原许可证。

## 发布核对

发布任务逐项验证源码归档及 Git 提交。源材料缺失或哈希不一致时停止发布。归档的 SHA-256 由 GitHub 附件元数据提供，发布任务核对匹配后才会使用其下载链接。依赖升级时同步更新 `assets/third-party/media-sources.json`、构建配方、组件声明和本说明。
