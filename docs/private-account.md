# Tbox 私有账号服务

在桌面端「账号」点击「使用 GitHub 登录」，在系统浏览器完成认证后返回 Tbox。Windows 和 macOS 登录同一个允许的 GitHub 账号后，设备自动登记。登录和同步属于可选功能，本机工具继续使用原有的本机数据。

## 部署

- 将域名指向 Linux 服务器，开放 HTTPS 和 Caddy 获取证书所需的 HTTP 端口。PostgreSQL 和应用容器只通过内部网络访问。
- 打包前将该 HTTPS 根地址写入 GitHub Actions 变量 `TBOX_ACCOUNT_ORIGIN`。本地打包使用同名环境变量。升级已手动配置过的安装沿用已保存的地址。全新安装若未指定地址，「账号」页会提示服务尚未配置。
- 单独为 Tbox 创建 GitHub OAuth 应用，将回调地址设为 `https://你的域名/v1/auth/github/callback`。将允许登录者的 GitHub 数字用户 ID 写入部署配置。Tbox OAuth 应用独立于 NAV 和发布流程。
- 从 [部署示例](../deploy/cloud/.env.example) 创建本机 `deploy/cloud/.env`，限制文件访问权限。注入 OAuth 应用密钥、独立数据库密码以及 32 字节随机截图加密密钥，以 base64 表示后两项可避免连接字符串的分隔符。例如用 `openssl rand -base64 32` 生成各自独立的值。
- 在 `deploy/cloud` 运行 `docker compose up -d --build`，确认 `/health` 返回 HTTP 200。用同账号的两台设备分别登录，再用非许可账号验证拒绝行为。
- 每次升级前运行 `docker compose exec -T db pg_dump -U tbox -Fc tbox` 并将输出保存到受限备份位置。截图加密密钥分开备份。用隔离数据库执行 `pg_restore`，以相同截图密钥验证账号偏好和样图可读取。留意数据库容量、证书续期和服务错误。

## 同步行为

云端按键保存主题值、失败提示与翻译目标语言。第一台设备绑定后初始化偏好。后续设备收到并应用云端值。正在使用的主窗口继续遵循既有的浅色方案。并发修改同一项时显示冲突提示。API 密钥、剪贴板、路径和权限仅保存在本机。

截图上传为每台设备单独开放。用户明确启用后，已复制和保存的普通截图成品才入队。云端保存选区合成图和有限的索引元数据。单张上限 32 MiB，保留最近 7 天、最多 50 条、合计最多 1 GiB。删除云端项不改动本机原始历史。账号服务持有解密密钥。

「我的设备」移除目标后，服务器立即使其会话失效。在线客户端还会接收撤销事件。本机离线退出会记录待撤销状态并停止本机同步，重连后撤销服务器会话。

## 验证

`TBOX_TEST_POSTGRES` 指向专门用于测试的 PostgreSQL 数据库。运行 `dotnet test tests/Tbox.Cloud.Tests/Tbox.Cloud.Tests.csproj -c Release` 验证设备认证、模拟 GitHub 回调、偏好和截图生命周期。

本阶段的部署包包含账号和同步服务。远控宿主、会话窗口和中继的实现及其重启验收按产品需求跟踪。

## 无人值守组件验证

源代码初查采用 RustDesk 客户端 1.5.3 和开源服务器 1.1.16。客户端源码包含固定的协议子模块，客户端与服务器仓库均提供 AGPL-3.0 许可。发行验收需核对组件改动、对应源码、依赖及构建材料，包含可从网络使用的修改后服务。[客户端源码](https://github.com/rustdesk/rustdesk/tree/95cca3f661f635b3cc85c04935fe8d96d8f432ef) · [服务器源码](https://github.com/rustdesk/rustdesk-server/tree/73523b31cfd25d77dee862e6fc9f5e1fb5e485ef)

上游 Windows 安装器自行注册自动启动服务，运行时还检查上游安装布局。Tbox 签名包需要在应用扩展中声明服务，用随包位置解析服务与子进程路径，并在宿主通过本机授权前保持远控关闭。管理员安装、从重启到登录界面连接、撤销及升级后的服务连续性均需实际运行验证。[上游 Windows 实现](https://github.com/rustdesk/rustdesk/blob/95cca3f661f635b3cc85c04935fe8d96d8f432ef/src/platform/windows.rs) · [MSIX 服务文档](https://learn.microsoft.com/en-us/windows/msix/packaging-tool/convert-an-installer-with-services)
