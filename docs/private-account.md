# Tbox 私有账号服务

在桌面端「账号」点击「使用 GitHub 登录」，在系统浏览器完成认证后返回 Tbox。Windows 和 macOS 登录同一个允许的 GitHub 账号后，设备自动登记。登录和同步属于可选功能，本机工具继续使用原有的本机数据。

## 部署

- 将域名指向 Linux 服务器，开放 HTTPS 和 Caddy 获取证书所需的 HTTP 端口。PostgreSQL 和应用容器只通过内部网络访问。
- 桌面包默认使用 `https://tbox.xsddpx.com/`。面向独立部署的打包可用 GitHub Actions 变量或同名环境变量 `TBOX_ACCOUNT_ORIGIN` 指定 HTTPS 根地址。升级已手动配置过的安装沿用已保存的地址。
- 单独为 Tbox 创建 GitHub OAuth 应用，将回调地址设为 `https://你的域名/v1/auth/github/callback`。将允许登录者的 GitHub 数字用户 ID 写入部署配置。Tbox OAuth 应用独立于 NAV 和发布流程。
- 从 [部署示例](../deploy/cloud/.env.example) 创建本机 `deploy/cloud/.env`，限制文件访问权限。注入 OAuth 应用密钥、独立数据库密码以及 32 字节随机截图加密密钥，以 base64 表示后两项可避免连接字符串的分隔符。例如用 `openssl rand -base64 32` 生成各自独立的值。
- 在 `deploy/cloud` 运行 `docker compose up -d --build`，确认 `/health` 返回 HTTP 200。用同账号的两台设备分别登录，再用非许可账号验证拒绝行为。
- 每次升级前运行 `docker compose exec -T db pg_dump -U tbox -Fc tbox` 并将输出保存到受限备份位置。截图加密密钥分开备份。用隔离数据库执行 `pg_restore`，以相同截图密钥验证账号偏好和样图可读取。留意数据库容量、证书续期和服务错误。

### 复用现有 Caddy

Tbox 的域名为 `tbox.xsddpx.com`，独立 GitHub OAuth 应用使用精确回调 `https://tbox.xsddpx.com/v1/auth/github/callback`。
如果 Linux 主机已有 Caddy 监听公网端口，在 `deploy/cloud` 运行以下命令。Tbox 使用独立 Compose 项目，账号服务只监听主机回环接口。

```sh
docker compose -p tbox --env-file .env -f compose.yaml -f compose.shared-proxy.yaml up -d --build
curl -fsS http://127.0.0.1:18984/health
```

在现有 Caddy 中新增该域名的站点块。验证 Caddy 配置后重载，用公网上的 HTTPS 地址检查 `/health`。

```caddyfile
tbox.xsddpx.com {
    log {
        output discard
    }
    header Strict-Transport-Security "max-age=31536000"
    reverse_proxy 127.0.0.1:18984
}
```

目前 NAV 所在 VPS 复用系统 Caddy，并以 [systemd 单元](../deploy/cloud/tbox-cloud.service)运行自包含的 Tbox 云服务。应用通过本机 PostgreSQL 16 的独立数据库保存账号与同步数据，两项服务只监听回环接口。配置分别保存至 `/etc/tbox/cloud.env` 和 `/etc/tbox/oauth.env`，仅 root 可读。部署产物位于 `/opt/tbox/releases/<源码提交>`，`/opt/tbox/current` 指向当前版本。升级前使用 `sudo -u postgres pg_dump -Fc tbox` 备份数据库，并分开备份受限配置。

## 同步行为

云端按键保存主题值、失败提示与翻译目标语言。第一台设备绑定后初始化偏好。后续设备收到并应用云端值。正在使用的主窗口继续遵循既有的浅色方案。并发修改同一项时显示冲突提示。API 密钥、剪贴板、路径和权限仅保存在本机。

截图上传为每台设备单独开放。用户明确启用后，已复制和保存的普通截图成品才入队。云端保存选区合成图和有限的索引元数据。单张上限 32 MiB，保留最近 7 天、最多 50 条、合计最多 1 GiB。删除云端项不改动本机原始历史。账号服务持有解密密钥。

「我的设备」移除目标后，服务器立即使其会话失效。在线客户端还会接收撤销事件。本机离线退出会记录待撤销状态并停止本机同步，重连后撤销服务器会话。

## 验证

`TBOX_TEST_POSTGRES` 指向专门用于测试的 PostgreSQL 数据库。运行 `dotnet test tests/Tbox.Cloud.Tests/Tbox.Cloud.Tests.csproj -c Release` 验证设备认证、模拟 GitHub 回调、偏好和截图生命周期。

## 远控授权服务

账号服务已支持就绪设备列表、宿主登记、连接票据和最多 30 秒的会话续期。登记须以被控设备的本机密钥签名。宿主使用专用凭据，账号令牌仅用于账号端。宿主为每次连接生成独立挑战，控制端签名绑定宿主、票据和挑战。票据在验证后只能兑换一次。关闭宿主、退出或移除任一相关设备后，服务端立即作废对应授权。

存储迁移自动创建宿主和会话表，现有账号与设备不需要重新登记。宿主需要在真正能接收会话时报告就绪，在会话期间停止使用过期、撤销或无法续期的授权。桌面宿主、会话窗口、中继和重启验收按产品需求跟踪。

## 无人值守组件验证

源代码初查采用 RustDesk 客户端 1.5.3 和开源服务器 1.1.16。客户端源码包含固定的协议子模块，客户端与服务器仓库均提供 AGPL-3.0 许可。发行验收需核对组件改动、对应源码、依赖及构建材料，包含可从网络使用的修改后服务。[客户端源码](https://github.com/rustdesk/rustdesk/tree/95cca3f661f635b3cc85c04935fe8d96d8f432ef) · [服务器源码](https://github.com/rustdesk/rustdesk-server/tree/73523b31cfd25d77dee862e6fc9f5e1fb5e485ef)

上游 Windows 安装器自行注册自动启动服务，运行时还检查上游安装布局。Tbox 签名包需要在应用扩展中声明服务，用随包位置解析服务与子进程路径，并在宿主通过本机授权前保持远控关闭。管理员安装、从重启到登录界面连接、撤销及升级后的服务连续性均需实际运行验证。[上游 Windows 实现](https://github.com/rustdesk/rustdesk/blob/95cca3f661f635b3cc85c04935fe8d96d8f432ef/src/platform/windows.rs) · [MSIX 服务文档](https://learn.microsoft.com/en-us/windows/msix/packaging-tool/convert-an-installer-with-services)

2026-11-21，GitHub Windows 2025 运行器安装并覆盖升级独立签名夹具 `Tbox.RemoteServiceFixture`。两个版本的包系列一致，自动服务以 LocalSystem 启动并达到 Running。夹具验证 MSIX 服务注册与覆盖升级，RustDesk 宿主接入、Windows 重启至登录界面后的连接及双机操作仍待验收。[Windows MSIX 服务验证](https://github.com/xsddpx/Tbox/actions/runs/44871636834/job/136747239256)

### RustDesk 握手验证

`cargo test --locked --manifest-path src/Tbox.Remote/Cargo.toml` 验证独立握手逻辑。`bash scripts/prepare-remote-engine.sh` 检出固定 RustDesk 客户端和协议子模块，并将 Tbox 补丁应用到 `TEMP/` 的独立目录。控制端在已加密的流上比对服务器登记的被控端公钥。被控端生成新挑战，并调用账号服务兑换票据。引擎只处理桌面画面和键鼠消息。宿主使用单独的凭据报告就绪、兑换及续期。开发入口通过继承管道与 Tbox 交换登记信息和挑战签名。

当前补丁已通过固定源码应用检查。Tbox 账号「我的设备」已接入启动、授权和握手。桌面应用只在安装包包含引擎并配置对应账号的中继时显示功能入口。打包配置 `TBOX_REMOTE_RENDEZVOUS` 和 `TBOX_REMOTE_RENDEZVOUS_KEY` 分别指定自建会合地址和公钥。完整引擎构建、Windows 签名服务注册、Mac 授权和中继部署按 REM-001 验收后进入安装包。

定制客户端通过 macOS 原生类型检查。固定版本的 `hbbs` 和 `hbbr` 已在本机分别编译，使用新建测试密钥启动并接受回环 TCP 连接。此结果验证启动和监听，画面经中继传输仍需双端验收。[macOS 原生检查](https://github.com/xsddpx/Tbox/actions/runs/44890546117/job/136805192496)

完整窗口构建保留 RustDesk 许可和准确的对应源码提交。Mac 和 Windows 打包器分别从 `TBOX_REMOTE_MACOS_VIEWER_ZIP` 或 `TBOX_REMOTE_WINDOWS_VIEWER_ZIP` 取得组件，并核对对应的 `TBOX_REMOTE_MACOS_VIEWER_SHA256` 或 `TBOX_REMOTE_WINDOWS_VIEWER_SHA256`。Mac 组件以 `com.xsddpx.tbox.remote` 嵌入并使用应用的持久签名身份。Windows 组件随 MSIX 封装并覆盖验签。无人值守 Windows 服务另按 REM-001 验收。
