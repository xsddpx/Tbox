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

在 `Shared desktop checks` 同时选择 `build_remote_viewer` 和所需平台的签名验证选项，并填写 `remote_rendezvous` 和 `remote_rendezvous_key`，可生成带引擎的签名候选。签名任务从同一次运行下载引擎，按构建任务输出的 SHA-256 核对。候选包保留为工作流产物。

2026-11-21，VPS 使用生产数据库结构和合成设备记录复测远控授权迁移。候选进程在隔离的 PostgreSQL 数据库和回环端口从架构 2 迁移到 3，保留已有设备及宿主，宿主在绑定传输公钥前保持不可用。候选 `/health` 返回 200，未认证的设备列表请求返回 401。

同日，Tbox 验收站点更新至 `6546fc2` 的服务端。切换前已备份并回读数据库，切换后公网 `/health` 返回 200，数据库为架构 3，现有 Windows 和 Mac 会话仍有效。

固定版会合和中继已在 VPS 启动。Mac 经现有 Stash 的 Tailscale 路由完成会合和双向中继协议探测。Mac 经代理访问公网时使用另一个出口，测试服务加入实际出口后，通过 `tbox.xsddpx.com` 收到 TCP 会合响应和 UDP 设备探测响应，并确认中继拒绝错误密钥、双向传送合成数据。VPS 日志确认请求配对。服务使用独立受限账号，仅允许本地、Tailscale 和验收出口接入，双端桌面会话仍待验收。

包含 `--tbox-service` 启动参数的两个签名 MSIX 夹具通过原位覆盖、进程参数、LocalSystem 身份和 Running 状态检查。此结果覆盖包服务的启动契约。[服务夹具复测](https://github.com/xsddpx/Tbox/actions/runs/44896274398/job/136822500770)

带远控引擎的 Windows 包注册 `TboxRemote` 服务，启动后等待本机授权。管理员确认后，桌面端与授权程序通过只限本机进程的管道交换专用宿主凭据。系统服务从活动控制台会话生成传输身份，账号服务接受登记后才保存在受保护的系统目录中。服务重启时复用既有授权，客户端设备私钥留在 Tbox 原凭据库中。服务收到明确撤销后清除对应授权，退出账号时先关闭属于当前设备的本机宿主。安装验证会检查服务身份和启动参数，隔离 Windows 运行器还会检查默认关闭、准备控制台宿主、取消后退出三个状态。Windows 登录界面、授权保持与双端画面操作仍需实机验收。

Windows 1.1.131 的签名包在 Windows 2025 运行器完成安装，`TboxRemote` 以 LocalSystem 进入 Running。测试等待控制台进程发布身份超时。后续候选在受保护的本机目录记录准备阶段和 Windows 错误编号。测试失败时保存阶段和进程会话编号，不读取授权状态文件或宿主凭据。[签名服务验证](https://github.com/xsddpx/Tbox/actions/runs/44901112084/job/136843710588)

Windows 1.1.132 诊断将超时定位到工作进程启动，控制台会话存在，服务未能拉起工作进程。后续服务使用自己的 LocalSystem 令牌，将随包工作进程启动到活动控制台会话，避免依赖打开系统登录进程。待确认状态继续只准备身份，成功登记并落盘本机授权后才开始接受连接。[诊断构建](https://github.com/xsddpx/Tbox/actions/runs/44904858801/job/136855969514)

Windows 1.1.134 完成 MSIX 签名、安装和 LocalSystem 服务启动。服务读取待确认授权并复制自身令牌，系统拒绝设置目标控制台会话，返回 Windows 错误 5。微软文档要求跨会话分配具备 `SeTcbPrivilege`，签名运行器中拒绝的具体原因仍待核查。工作进程没有启动，未发布身份。失败运行未保留可交付 MSIX。[Windows 1.1.134](https://github.com/xsddpx/Tbox/actions/runs/44908576376/job/136866701993) · [Windows TokenSessionId 文档](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_information_class)

Mac 1.1.134 的签名引擎取得屏幕录制及辅助功能权限后，两次冷启动返回相同的有效传输身份。用设备页授权后，Windows 11 上的 1.1.130 显示 Mac 可远程控制，独立窗口收到 Mac 测试画面。点击和键入均未触发 Mac 测试窗口的回执。关闭 Mac 被控后，Windows 在 2.3 秒内进入重连。反向测试中，Mac 能看到 Windows 就绪并打开控制窗口，随后显示无法通过注册服务器建立连接。Windows 1.1.130 没有安装无人值守服务。

Windows 2025 的独立签名 MSIX 服务夹具已在首次安装和覆盖安装后读取自身有效令牌。两次均为 LocalSystem、Running，`SeTcbPrivilege` 均为 enabled。该结果排除了签名 MSIX 服务一律缺少此权限的推测；1.1.134 产品进程发生错误 5 时的令牌尚无直接测量。[权限夹具](https://github.com/xsddpx/Tbox/actions/runs/44917972136/job/136887569157)

Mac 1.1.135 的签名宿主在等待父进程发送登记消息期间能完成 AppKit 启动。Windows 11 上的 Tbox 1.1.130 通过设备页连接后，Mac 本机测试窗口收到点击和键入的 `734182`。宿主进程的连接对端是 Windows 来宾的局域网地址。使用对应设备的强制中继选项后，控制窗口返回 10054。反向测试中，仅允许 Mac 虚拟网卡访问 Windows 程序的临时防火墙规则未使会话成功，随后已移除，原规则核对一致。Mac 1.1.134 的被控开关在签名升级至 1.1.135 后需要重新开启。

2026-11-21 深夜，在两端被控均关闭时，通过安装包使用的公网域名测试了三个 TCP 端口。Mac 默认路径和 Windows 11 来宾都能建立 TCP 连接，但在 21115、21116 上均未收到有效的会合协议响应。21120 上的两条测试连接提交配对请求后，第一段数据未抵达接收端。账号站点同时返回 HTTP 200。

同一台 Mac 指定物理网卡访问公网 IPv4 时，21115 返回正确响应，21116 和 21120 连接超时。经现有 Stash 访问 VPS 的 Tailscale 地址时，两个会合端口都返回正确响应，中继也原样传回两个方向各自的随机数据。Windows 来宾直接连接该私网地址时三个端口均超时。公网失败原因仍需用服务端日志核对，真实桌面的强制中继验收仍未通过。

2026-11-22，Windows 11 的 Tbox 1.1.130 与 Mac 1.1.135 双向同步了失败提示和翻译目标语言。仅阻断来宾 Tbox 的 HTTPS 后，Windows 报告账号服务暂不可用并保留基于修订 3 的待同步改动。Mac 在线更新到修订 4 再恢复原值，服务器记录为修订 5。恢复来宾联网后，Windows 显示采用服务器较新设置，修订变为 5、待传项为 0。两项偏好均已恢复，临时规则已移除。

2026-11-22 再次经默认公网路径测试。Mac 在 00:54 收到 21115、21116 的有效会合响应，21120 双向传回合成测试数据。Windows 11 来宾在 01:10 建立了 TCP 连接，两个会合端口均未返回有效响应，中继首段数据未传回。设备页显示双方已就绪后，Mac 控制 Windows 的普通会话仍在会合阶段失败，强制中继报 Broken pipe。Windows 控制 Mac 强制中继报 10054。已恢复两端针对测试设备的中继选项并关闭被控。

Mac 仅移除了本次验收使用的 Windows 11 虚拟机。Windows 端没有手动刷新即显示「登录已失效或设备已移除，请重新登录」。点击「使用 GitHub 登录」后，原浏览器会话完成认证，Mac 设备列表重新显示这台 Windows 设备，Windows 被控保持关闭。

Windows 离线退出后显示「本机已退出，联网后会撤销服务器会话」。Mac 的已登录设备列表仍有这台虚拟机。删除临时的应用级出站规则并确认规则已不存在后，来宾自动提交撤销，Mac 刷新后该设备从列表消失。通过原 GitHub 会话重新登录后，Mac 再次显示 Windows 在线，Windows 被控仍然关闭。
