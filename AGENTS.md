# AGENTS.md

## 仓库定位

本仓库是 Noplch0 维护的 Dalamud 插件集合，同时作为公开的自定义插件仓库。

本仓库**不是单插件仓库**。仓库会长期存放多个插件，每个插件必须位于自己的独立顶层目录中，拥有自己的工程、源码、测试、构建脚本、README 和许可证。

当前插件：

- `DalamudProxy/`：国服 Dalamud 插件仓库读取与安装下载代理

## 根目录与插件边界

根目录只允许出现：

- `AGENTS.md`
- `README.md`
- `repo.json`
- `.gitattributes`
- 每个插件一个独立顶层目录
- Git 元数据目录 `.git`

禁止在根目录放置某个插件的源码、解决方案、构建产物或共享测试项目。

例如：

```text
dalamud-plugins/
├── AGENTS.md
├── README.md
├── repo.json
├── .gitattributes
├── DalamudProxy/
│   ├── DalamudProxy.sln
│   ├── README.md
│   ├── LICENSE
│   ├── Directory.Build.props
│   ├── scripts/
│   ├── src/
│   └── tests/
└── NextPlugin/
    └── ...
```

### 独立插件要求

- 每个一级目录必须是一个可独立构建、测试和发布的插件项目。
- 一个插件的源码、测试、脚本和资源必须留在该插件目录内。
- 不允许一个插件直接引用另一个插件的源码或工程。
- 不允许把插件工程放在根目录的 `src/`、`tests/`、`plugins/` 等公共目录中。
- 修改某个插件时，不得顺手重构或格式化其他插件。
- 新增插件时，必须同时更新根目录 `README.md` 和 `repo.json`。
- 插件的文件夹名应稳定、可读，并优先与 `InternalName` 一致。

## `repo.json` 约束

- 根目录 `repo.json` 是全部插件的聚合清单。
- `repo.json` 顶层**必须是 JSON 数组**，即使当前只有一个插件也必须保留 `[ ... ]`。
- 每个对象对应一个插件。
- 不得发布单个 JSON 对象作为插件仓库清单。
- `InternalName` 必须唯一。
- `AssemblyVersion`、Dalamud API 等级、下载链接必须与插件实际 Release 一致。
- `DownloadLinkInstall`、`DownloadLinkUpdate`、`DownloadLinkTesting` 必须指向真实存在的 ZIP。
- 修改 `repo.json` 后必须验证它可被 Dalamud 的 `List<RemotePluginManifest>` 反序列化。

## 版本与发布

- 发布标签格式：`<InternalName>-v<AssemblyVersion>`。
- 当前示例：`DalamudProxy-v0.1.0.2`。
- Release 资产名必须与插件名及 `repo.json` 链接一致，例如 `DalamudProxy.zip`。
- 每个插件使用独立 Tag，避免多个插件版本相互覆盖。
- 发布前必须同步以下版本来源：
  - 插件 `.csproj`
  - 根目录 `repo.json`
  - Git Tag
  - 插件 README 中的下载与标签说明
- 不得复用已发布 Tag，也不得静默替换已发布 Release 资产；修复必须发布新版本。

## 构建与测试

- 使用 .NET 10 SDK。
- 目标 Dalamud API 为 15，重点兼容国服 XIVLauncherCN。
- 构建时优先使用 `DALAMUD_HOME`；否则优先查找：
  - `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`
  - `%APPDATA%\XIVLauncher\addon\Hooks\dev`
- 在每个插件目录内执行构建和测试。
- 每个插件的 Release 脚本必须至少完成：
  - restore
  - test
  - Release build
  - ZIP 内容校验
  - SHA256 生成
- 修改行为时必须同步增加或更新测试，不能只验证“能编译”。
- 发布前至少运行该插件完整的 `scripts/build-release.ps1`。
- `bin/`、`obj/`、`dist/`、测试结果、日志和本地配置不得提交。

## Dalamud 与 Harmony 约束

- 使用 Dalamud 提供的 `0Harmony 2.4.2`。
- 禁止在插件包内重复携带 `0Harmony.dll`。
- 禁止把另一份 Harmony 手动加载到 Default ALC 来绕过程序集解析。
- 新增 Harmony 补丁时必须验证插件位于可卸载 AssemblyLoadContext 时仍可安装补丁。
- 卸载插件时必须只移除本插件注册的 Harmony ID。
- 不得使用未隔离的全局补丁处理与插件目标无关的请求。

## DalamudProxy 当前功能约束

`DalamudProxy` 只允许代理以下两种 Dalamud 操作：

- `Dalamud.Plugin.Internal.Types.PluginRepository` 的插件仓库读取
- `Dalamud.Plugin.Internal.PluginManager.DownloadPluginAsync` 的安装、更新和测试版下载

禁止代理：

- 插件图标或截图
- 其他插件的普通运行时 HTTP 请求
- 游戏网络
- Windows 系统代理
- 与插件仓库或安装下载无关的 Dalamud 请求

### HTTP 转发要求

- 不得把外层 `HttpClient` 已标记发送过的同一个 `HttpRequestMessage` 直接交给第二个 `HttpClient`。
- 重新发送前必须使用 `HttpRequestCloner` 克隆请求。
- 只处理无正文的 `GET` 请求。
- 必须通过 `ManagedRequestKey` 防止代理客户端递归拦截。
- 代理失败时不得静默回退直连；启用范围内的请求应明确失败并写入脱敏日志。
- 代理配置使用单一活动上游代理，支持 HTTP、HTTPS、SOCKS4、SOCKS4a、SOCKS5。
- 密码必须使用当前 Windows 用户的 DPAPI 加密保存，不得明文写入配置文件或日志。

## 代码与文档风格

- 面向用户的 UI、README 和发布说明使用简体中文。
- 对外发布文本应明确说明代理范围和已知限制。
- 日志不得记录密码、Authorization、查询参数中的 Token 或完整敏感 URL。
- 保持现有命名和可读性；不要为小改动进行无关的大规模重写。
- 仓库文本文件统一使用 LF，遵守根目录 `.gitattributes`。

## Git 操作

- 开始修改前检查 `git status`，不得覆盖或回滚其他未提交改动。
- 提交应聚焦于单个插件或单个仓库级变更，避免混合多个插件的无关修改。
- 不得提交密钥、令牌、账号数据、用户目录绝对路径或运行时日志。
- 推送前必须确认构建、测试和发布校验通过。
- 除非用户明确要求，不得强制推送、重写远端历史或覆盖现有 Release。

