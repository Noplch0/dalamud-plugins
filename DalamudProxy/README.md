# Dalamud Proxy

适用于国服 FFXIV（Dalamud API 15）的插件市场代理插件。

本目录是 [dalamud-plugins](https://github.com/Noplch0/dalamud-plugins) 仓库中的一个独立插件项目。

它只让 **Dalamud 插件市场相关请求** 走代理：

- 所选仓库的 `pluginmaster.json` 拉取与刷新
- 所选插件的 `IconUrl`、`ImageUrls`
- 所选插件的安装、更新和测试版 ZIP 下载
- 代理失败时直接报错，不会静默回退直连

不会代理游戏网络、其他插件的普通运行时 HTTP 请求，也不会修改 Windows 系统代理。

## 功能

- 单一 HTTP/HTTPS/SOCKS4/SOCKS5 上游代理
- 可选用户名与密码
- 密码使用当前 Windows 用户的 DPAPI 加密后写入 Dalamud 插件配置
- 只读发现 Dalamud 已配置的仓库，不在插件内增删仓库
- 按仓库与插件 `InternalName` 选择代理范围
- 插件加载后自动刷新所选仓库，补上启动阶段因代理插件尚未加载导致的失败
- 请求日志脱敏，不记录查询参数和认证信息
- `/dalamudproxy` 打开设置窗口

## 环境要求

- Windows
- 国服 XIVLauncher / Dalamud，API 15
- 构建需要 .NET 10 SDK
- 默认支持 `XIVLauncherCN` 或国际服 `XIVLauncher` 的 Dalamud 开发目录

项目会优先使用：

1. 已设置的 `DALAMUD_HOME`
2. `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`
3. `%APPDATA%\XIVLauncher\addon\Hooks\dev`

## 构建与测试

在本插件目录运行：

```powershell
pwsh ./scripts/build-release.ps1
```

脚本会执行：

1. `dotnet restore`
2. `dotnet test`
3. Release 构建
4. 将 DalamudPackager 生成的 `latest.zip` 复制为 `dist/DalamudProxy.zip`
5. 验证 ZIP 必需文件
6. 生成 `dist/DalamudProxy.zip.sha256`

如果自动探测路径不符合本机情况：

```powershell
$env:DALAMUD_HOME = 'C:\path\to\XIVLauncherCN\addon\Hooks\dev'
pwsh ./scripts/build-release.ps1
```

跳过测试：

```powershell
pwsh ./scripts/build-release.ps1 -SkipTests
```

## 本地安装

1. 运行构建脚本。
2. 将 `dist/DalamudProxy.zip` 解压到固定目录。
3. 在 Dalamud 设置的 **Experimental / 开发插件** 中添加解压后 `DalamudProxy.dll` 的完整路径。
4. 在插件安装器中启用 `Dalamud Proxy`。
5. 使用 `/dalamudproxy` 打开设置。

不要只移动 `DalamudProxy.dll`；发布包还包含：

- `DalamudProxy.Core.dll`
- `System.Security.Cryptography.ProtectedData.dll`

## 使用

1. 填写代理地址，例如：
   - `http://127.0.0.1:7890`
   - `socks5://127.0.0.1:1080`
2. 如有认证，填写用户名和密码。
3. 点击 **测试代理**，确认能访问测试 URL。
4. 在仓库表中勾选需要代理的仓库。
5. 点击 **保存并应用** 或 **刷新所选仓库**。
6. 在插件表中搜索并勾选需要代理安装/更新的插件。
7. 点击 **打开插件安装器**，继续安装或更新插件。

自动刷新只会处理 Dalamud 当前已存在且已勾选的仓库。若第三方仓库依赖主仓库已成功加载，请同时勾选主仓库并一起刷新。

## 行为边界

### 会代理

- `Dalamud.Plugin.Internal.Types.PluginRepository` 发起的所选仓库 pluginmaster 请求
- `Dalamud.Plugin.Internal.PluginManager` 发起的所选插件下载请求
- `Dalamud.Interface.Internal.Windows.PluginImageCache` 发起的所选插件图片请求

### 不会代理

- 游戏服务器连接
- 其他插件的普通 HTTP API 请求
- 未选择仓库或未选择插件的市场请求
- 原始 TCP/UDP、游戏协议或非 HTTP(S) 流量
- Windows 其他进程的网络流量

即使某个其他插件运行时访问了与下载链接完全相同的 URL，只要调用来源不是 Dalamud 插件管理器/仓库/图片缓存，也不会被接管。

## 故障排查

### 没有发现仓库

先在 Dalamud 本体中添加仓库，然后在设置窗口点击 **重新读取仓库**。插件不会写入 Dalamud 仓库配置。

### 刷新失败

- 先确认代理测试成功
- 确认代理支持目标协议
- 检查代理是否允许访问对应仓库域名
- 若 Dalamud 内部刷新 API 不可用，插件会尝试自行通过同一代理抓取 pluginmaster；此时目标列表可更新，但 Dalamud 内置插件列表可能仍需手动点击刷新
- 代理失败不会自动直连

### Harmony 或拦截器不可用

设置窗口会显示错误。当前版本直接使用 Dalamud 自带的 `0Harmony 2.4.2`；发布 ZIP 不应再包含自己的 `0Harmony.dll`。如仍失败，请查看 Dalamud 日志中的完整异常。

## GitHub 发布

插件使用聚合仓库 `Noplch0/dalamud-plugins`：

```text
https://github.com/Noplch0/dalamud-plugins
```

发布约定：

- 根目录 `../repo.json` 维护全部插件的聚合清单
- 本插件的 Release Tag 使用 `DalamudProxy-v0.1.0.1`
- Release 资产必须命名为 `DalamudProxy.zip`
- 下载地址：

```text
https://github.com/Noplch0/dalamud-plugins/releases/download/DalamudProxy-v0.1.0.1/DalamudProxy.zip
```

版本号需要同时与以下位置保持一致：

- `src/DalamudProxy/DalamudProxy.csproj`
- 根目录 `../repo.json`
- Git Tag，例如 `DalamudProxy-v0.1.0.1`

## 许可证

MIT

