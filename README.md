# Dalamud Plugins

Noplch0 的 Dalamud 插件集合。本仓库同时作为自定义插件仓库使用。

## 插件仓库地址

在 Dalamud 的插件仓库设置中添加：

```text
https://raw.githubusercontent.com/Noplch0/dalamud-plugins/main/repo.json
```

## 插件列表

| 插件 | 版本 | 命令 | 说明 |
| --- | --- | --- | --- |
| [Dalamud Proxy](./DalamudProxy/) | 0.1.0.1 | `/dalamudproxy` | 按仓库和插件范围代理国服 Dalamud 插件市场的列表、图片及安装下载。 |

## 仓库结构

```text
dalamud-plugins/
├── README.md
├── repo.json
└── DalamudProxy/
    ├── DalamudProxy.sln
    ├── README.md
    ├── src/
    ├── tests/
    └── scripts/
```

- 根目录只存放仓库说明和聚合后的 `repo.json`
- 每个一级目录都是一个可以独立构建、测试和发布的插件项目
- 每个插件拥有自己的 README、许可证、源码、测试和构建脚本

## 安装插件

1. 打开 Dalamud 设置。
2. 在第三方插件仓库中添加上面的 `repo.json` URL。
3. 打开插件安装器并刷新仓库。
4. 搜索并安装需要的插件。

## 构建插件

进入对应插件目录后执行该插件自己的构建脚本，例如：

```powershell
cd .\DalamudProxy
pwsh ./scripts/build-release.ps1
```

发布多个插件时使用独立标签，避免版本相互覆盖：

```text
<InternalName>-v<AssemblyVersion>
```

例如：

```text
DalamudProxy-v0.1.0.1
```

每个插件的发布 ZIP 文件名应与根目录 `repo.json` 中的下载地址保持一致。

## 新增插件

1. 在仓库根目录创建一个新的插件目录。
2. 在该目录内保留完整、独立的插件工程。
3. 为新插件生成 Release ZIP。
4. 在根目录 `repo.json` 中增加对应条目。
5. 使用 `<InternalName>-v<AssemblyVersion>` 格式创建独立 Release。

## 许可证

各插件目录内的许可证适用于对应插件。
