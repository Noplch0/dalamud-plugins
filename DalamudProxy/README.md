# Dalamud Proxy

适用于国服 FFXIV（Dalamud API 15）的插件仓库与安装下载代理。

本目录是 [dalamud-plugins](https://github.com/Noplch0/dalamud-plugins) 仓库中的一个独立插件项目。

## 生效范围

启用后只代理以下 Dalamud 操作：

- 插件仓库读取：`PluginRepository` 获取 `pluginmaster.json`
- 插件安装、更新和测试版下载：`PluginManager.DownloadPluginAsync`

不会代理：

- 插件图标和截图
- 其他插件的普通运行时 HTTP 请求
- 游戏网络
- Windows 系统代理或其他进程流量

## 功能

- 单一 HTTP/HTTPS/SOCKS4/SOCKS5 上游代理
- 可选用户名与密码
- 密码使用当前 Windows 用户的 DPAPI 加密
- 可分别启用“仓库读取”和“插件下载”两个作用域
- 请求通过独立代理客户端重新发送，不依赖系统代理
- 请求日志脱敏，不记录查询参数和认证信息
- `/dalamudproxy` 打开设置窗口

## 环境要求

- Windows
- 国服 XIVLauncher / Dalamud，API 15
- 构建需要 .NET 10 SDK
- 使用 Dalamud 自带的 `0Harmony 2.4.2`，插件包不再附带 Harmony

构建时按以下顺序寻找 Dalamud：

1. `DALAMUD_HOME`
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
4. 生成 `dist/DalamudProxy.zip`
5. 验证 ZIP 不包含 `0Harmony.dll`
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
3. 在 Dalamud 的 **Experimental / 开发插件** 中添加 `DalamudProxy.dll` 的完整路径。
4. 在插件安装器中启用 `Dalamud Proxy`。
5. 使用 `/dalamudproxy` 打开设置。

发布包包含：

- `DalamudProxy.dll`
- `DalamudProxy.Core.dll`
- `DalamudProxy.json`
- `System.Security.Cryptography.ProtectedData.dll`

## 使用

1. 填写代理地址，例如：
   - `http://127.0.0.1:7890`
   - `socks5://127.0.0.1:1080`
2. 如有认证，填写用户名和密码。
3. 选择生效范围：
   - 读取插件仓库链接 / pluginmaster
   - 安装、更新和测试版插件下载
4. 点击 **测试代理**。
5. 点击 **保存并应用**。
6. 点击 **刷新所有插件仓库**，或直接打开插件安装器。

只要启用对应范围，仓库读取和插件下载会自动走插件配置的代理，不需要另外勾选具体插件或仓库。

## 排查

### 插件仓库读取失败

- 确认已启用“读取插件仓库链接”
- 确认代理测试成功
- 确认代理可以访问该仓库域名
- 代理失败时请求会失败，不会静默回退直连

### 插件安装失败

- 确认已启用“安装、更新和测试版插件下载”
- 在设置窗口日志中应出现“插件下载”记录
- 若日志没有记录，检查 Harmony 拦截器是否成功安装
- 若状态为 `blocked`，说明代理配置无效，请求被主动阻止以防直连

### Harmony 或拦截器不可用

设置窗口会显示错误。当前版本直接使用 Dalamud 自带的 `0Harmony 2.4.2`；发布 ZIP 不应包含自己的 `0Harmony.dll`。

## GitHub 发布

插件使用聚合仓库：

```text
https://github.com/Noplch0/dalamud-plugins
```

发布约定：

- 根目录 `../repo.json` 维护全部插件清单
- Release Tag：`DalamudProxy-v0.1.0.2`
- Release 资产名：`DalamudProxy.zip`
- 下载地址：

```text
https://github.com/Noplch0/dalamud-plugins/releases/download/DalamudProxy-v0.1.0.2/DalamudProxy.zip
```

版本号需要同时与以下位置保持一致：

- `src/DalamudProxy/DalamudProxy.csproj`
- 根目录 `../repo.json`
- Git Tag

## 许可证

MIT
