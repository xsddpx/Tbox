# Repository Guidelines

## 项目结构与模块组织

Windows 与 macOS 共用同一个桌面应用。根目录仅放置项目级配置和说明文档；临时下载、验证数据和中间产物放在 Git 忽略的 `TEMP/`。

- `src/`：应用或库的源代码
- `tests/`：自动化测试，目录结构与 `src/` 对应
- `assets/`：图片、测试夹具、模板等静态资源
- `docs/`：架构说明、技术决策和贡献文档

`src/Tbox.Core` 保存共用产品规则；`src/Tbox.Desktop` 是共用 Avalonia 应用，平台差异限定在 `Platform/`。新功能优先共用，不复制两套交互逻辑。

代码优先按功能或业务领域组织，避免建立职责模糊的通用工具目录。

## 构建、测试与开发命令

使用 `global.json` 固定的 .NET SDK。完整构建、打包流程见 README；常用命令：

- `dotnet test tests/Tbox.Core.Tests/Tbox.Core.Tests.csproj -c Release`
- `dotnet test tests/Tbox.Desktop.Tests/Tbox.Desktop.Tests.csproj -c Release`
- `bash scripts/build-macos.sh osx-arm64`
- `bash scripts/package-macos.sh osx-arm64`
- `.\scripts\package-desktop-windows.ps1 -VerifyOnly`

除非部署流程明确要求，否则不要提交自动生成的构建产物。

## 编码风格与命名约定

采用所选语言的主流格式化和检查工具，将配置文件纳入版本控制，并在提交拉取请求前运行。除非工具另有要求，统一使用空格缩进。类型和组件使用 `PascalCase`，变量和函数使用 `camelCase`，文档及资源文件使用 `kebab-case`。功能提交中不要混入无关的格式化修改。

## 测试规范

每项行为变更和缺陷修复都应补充测试。测试可统一放在 `tests/`，若框架更适合就近放置，也可与源码同目录。测试名称应描述可观察行为，例如 `rejectsExpiredToken`。测试必须可重复执行，不应依赖个人凭据或状态易变的外部服务。

## 需求文档维护

- `docs/product-requirements.md` 是产品需求、交互规则、迁移语义和历史修改要求的唯一活文档。
- 每次收到新增、变更或撤销需求时，必须在同一轮代码提交中更新对应需求条目、验收标准、状态和文末变更记录；仅修改代码或只写提交信息不算完成。
- 实现细节可以留在源码或架构文档中，但产品行为必须使用与 Windows API 无关的语言描述，便于迁移到其他平台。

## 提交与拉取请求规范

分支、提交和 PR 标题以执行者软件名开头，例如分支 `claude/fix-描述`、提交 `[claude] fix: 修复描述`，类型沿用 Conventional Commits。

拉取请求应说明目的、概括主要改动并列出验证方式。关联相关 Issue；涉及界面变化时附上截图。每个拉取请求只聚焦一个主题，并明确说明配置变更、数据迁移和后续工作。

## 版本与打包规范

Windows 交付签名 MSIX，Mac 交付签名 `.app` 的 ZIP。没有对应平台实机验证时必须明确记录，不能虚构构建或安装结果。

Mac 必须复用登录钥匙串中的持久签名身份，保持 Bundle ID 和安装位置稳定，并验证覆盖升级后权限仍可使用。不得每轮生成新证书、改用 ad hoc 签名，或自动清空已有权限。

- 每个交付包都使用一个从未打包过的新版本号；默认递增补丁版本，禁止覆盖同版本安装包。
- 每轮应用代码修改完成后，必须生成新版本安装包，并覆盖升级安装到当前电脑；仅完成源码修改不算交付完成。
- 打包前同步更新 `src/Tbox.Desktop/Tbox.Desktop.csproj` 的 `Version`、`AssemblyVersion`、`FileVersion`，`Platform/windows/Package.appxmanifest` 的包版本，以及 `Platform/macos/Info.plist` 的版本。
- 每次成功打包后，将版本、打包时间和 SHA-256 记录到 `docs/release-history.md`，并随源码提交。
- 打包脚本必须拒绝版本不一致、Release 二进制版本过期或目标版本安装包已存在的情况。

## 安全与配置

禁止提交密码、访问令牌或本地环境文件。使用 `.env.example` 等脱敏示例说明必需变量，并将敏感的本地文件加入 `.gitignore`。
