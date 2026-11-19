using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudProxy.Core;

namespace DalamudProxy.Windows;

internal sealed class ConfigWindow : Window, IDisposable
{
    private static readonly Vector4 WarningColor = new(1f, 0.58f, 0.20f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.28f, 0.24f, 1f);
    private static readonly Vector4 SuccessColor = new(0.38f, 0.88f, 0.48f, 1f);
    private static readonly Vector4 MutedColor = new(0.70f, 0.70f, 0.70f, 1f);

    private readonly Plugin plugin;
    private readonly Configuration config;

    private string proxyUrl;
    private string username;
    private string password;
    private string testUrl;
    private bool enabled;
    private bool repositoryReads;
    private bool pluginDownloads;
    private bool autoRefresh;
    private bool dirty;
    private string statusMessage = string.Empty;
    private string testMessage = string.Empty;
    private int testInProgress;

    public ConfigWindow(Plugin plugin)
        : base("Dalamud Proxy###DalamudProxyConfig")
    {
        this.plugin = plugin;
        this.config = plugin.Configuration;
        this.proxyUrl = config.ProxyUrl;
        this.username = config.Username;
        this.password = plugin.RuntimePassword;
        this.testUrl = config.TestUrl;
        this.enabled = config.Enabled;
        this.repositoryReads = config.ProxyRepositoryReads;
        this.pluginDownloads = config.ProxyPluginDownloads;
        this.autoRefresh = config.AutoRefresh;
        this.Size = new Vector2(820, 650);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        this.WindowName = "Dalamud Proxy###DalamudProxyConfig";
        var contentVisible = ImGui.BeginChild(
            "##DalamudProxyScroll",
            new Vector2(0, 0),
            false,
            ImGuiWindowFlags.None);

        if (contentVisible)
        {
            ImGui.TextWrapped("代理只在两种 Dalamud 操作中生效：读取插件仓库（pluginmaster）以及安装/更新插件包。不会代理插件图片、其他插件运行时请求、游戏网络或 Windows 系统流量。");
            ImGui.Spacing();

            this.DrawProxySection();
            ImGui.Separator();
            this.DrawRepositories();
            ImGui.Separator();
            this.DrawActivityLog();

            if (!string.IsNullOrEmpty(this.statusMessage))
                ImGui.TextColored(SuccessColor, this.statusMessage);

            if (!string.IsNullOrEmpty(this.plugin.ConfigurationError))
                ImGui.TextColored(ErrorColor, this.plugin.ConfigurationError);

            if (!this.plugin.Interceptor.Installed && !string.IsNullOrEmpty(this.plugin.Interceptor.InstallationError))
                ImGui.TextColored(ErrorColor, this.plugin.Interceptor.InstallationError);
        }

        ImGui.EndChild();
    }

    private void DrawProxySection()
    {
        ImGui.TextUnformatted("代理设置");
        var changed = ImGui.Checkbox("启用代理", ref this.enabled);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("应用后自动刷新所有插件仓库", ref this.autoRefresh);

        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputTextWithHint(
            "##proxyUrl",
            "http://127.0.0.1:7890 或 socks5://127.0.0.1:1080",
            ref this.proxyUrl,
            256);

        ImGui.SetNextItemWidth(240);
        changed |= ImGui.InputTextWithHint("##username", "用户名（可选）", ref this.username, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        changed |= ImGui.InputText("##password", ref this.password, 128, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        if (ImGui.Button("测试代理"))
            this.StartProxyTest();

        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputTextWithHint("##testUrl", "测试 URL", ref this.testUrl, 512);

        if (changed)
            this.dirty = true;

        ImGui.Spacing();
        ImGui.TextUnformatted("生效范围");
        changed = ImGui.Checkbox("读取插件仓库链接 / pluginmaster", ref this.repositoryReads);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("安装、更新和测试版插件下载", ref this.pluginDownloads);
        if (changed)
            this.dirty = true;

        ImGui.TextColored(MutedColor, "只匹配 Dalamud 的 PluginRepository 和 PluginManager.DownloadPluginAsync，不会按 URL 或插件名称猜测。");

        if (ImGui.Button("保存并应用"))
            this.SaveAndApply();

        ImGui.SameLine();
        if (ImGui.Button("刷新所有插件仓库"))
            this.SaveAndRefresh();

        ImGui.SameLine();
        if (ImGui.Button("打开插件安装器"))
        {
            if (this.dirty && !this.SaveAndApply())
                return;
            this.plugin.OpenPluginInstaller();
        }

        if (Volatile.Read(ref this.testInProgress) != 0)
            ImGui.TextColored(WarningColor, "正在测试代理…");
        else if (!string.IsNullOrEmpty(this.testMessage))
            ImGui.TextWrapped(this.testMessage);

        if (this.dirty)
            ImGui.TextColored(WarningColor, "设置有未保存的修改。");
    }

    private void DrawRepositories()
    {
        var state = this.plugin.GetViewState();
        ImGui.TextUnformatted($"Dalamud 插件仓库（{state.Repositories.Count}）");
        ImGui.TextColored(
            state.Refreshing ? WarningColor : MutedColor,
            state.Refreshing ? $"{state.Status} 正在处理…" : state.Status);

        if (!string.IsNullOrEmpty(state.DiscoveryWarning))
            ImGui.TextColored(WarningColor, state.DiscoveryWarning);

        if (!string.IsNullOrEmpty(state.RefreshWarning))
            ImGui.TextColored(ErrorColor, state.RefreshWarning);

        if (state.Repositories.Count == 0)
        {
            ImGui.TextColored(WarningColor, "没有发现插件仓库。请先在 Dalamud 设置中添加仓库。");
        }
        else if (ImGui.BeginTable(
                     "##repositories",
                     2,
                     ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                     new Vector2(0, 150),
                     0))
        {
            ImGui.TableSetupColumn("仓库 URL", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableHeadersRow();

            foreach (var repository in state.Repositories)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(repository.Url);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(RepositoryStateLabel(repository.State));
            }

            ImGui.EndTable();
        }

        if (ImGui.Button("重新读取仓库列表"))
        {
            this.plugin.ReloadRepositoryDiscovery();
            this.statusMessage = "已重新读取 Dalamud 仓库列表。";
        }
    }

    private void DrawActivityLog()
    {
        ImGui.TextUnformatted("最近代理请求");
        if (ImGui.Button("清空日志"))
            this.plugin.RequestLog.Clear();

        var entries = this.plugin.RequestLog.Snapshot().Reverse().Take(50).ToArray();
        if (!ImGui.BeginTable(
                "##activity",
                6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 150),
                0))
        {
            return;
        }

        ImGui.TableSetupColumn("时间", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("地址", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("耗时", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("结果", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Timestamp.ToString("HH:mm:ss"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(OperationLabel(entry.Operation));
            ImGui.TableNextColumn(); ImGui.TextWrapped(entry.Path);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.StatusCode > 0 ? entry.StatusCode.ToString() : "ERR");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{entry.ElapsedMilliseconds} ms");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Result);
        }

        ImGui.EndTable();
    }

    private bool SaveAndApply(bool scheduleRefresh = true)
    {
        this.CopyBuffersToConfig();
        try
        {
            this.plugin.SaveAndApplyConfiguration(this.password, scheduleRefresh);
            this.SyncBuffersFromConfig();
            this.dirty = false;

            if (string.IsNullOrEmpty(this.plugin.ConfigurationError))
            {
                this.statusMessage = $"设置已保存：{DateTime.Now:HH:mm:ss}";
                this.testMessage = string.Empty;
                return true;
            }

            this.statusMessage = string.Empty;
            this.testMessage = this.plugin.ConfigurationError;
            return false;
        }
        catch (Exception ex)
        {
            this.statusMessage = string.Empty;
            this.testMessage = $"保存失败：{ex.Message}";
            return false;
        }
    }

    private void SaveAndRefresh()
    {
        if (this.dirty && !this.SaveAndApply(scheduleRefresh: false))
            return;

        _ = this.plugin.RefreshRepositoriesAsync();
    }

    private void StartProxyTest()
    {
        if (Interlocked.CompareExchange(ref this.testInProgress, 1, 0) != 0)
            return;

        var settings = new ProxySettings(
            this.enabled,
            this.proxyUrl.Trim(),
            string.IsNullOrWhiteSpace(this.username) ? null : this.username.Trim(),
            string.IsNullOrEmpty(this.password) ? null : this.password,
            this.testUrl.Trim());

        if (!UriUtilities.TryCreateProxyEndpoint(
                settings.ProxyUrl,
                settings.Username,
                settings.Password,
                out _,
                out var error))
        {
            Volatile.Write(ref this.testInProgress, 0);
            this.testMessage = error;
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await this.plugin.TestProxyAsync(settings).ConfigureAwait(false);
                this.testMessage = result.Message;
            }
            finally
            {
                Volatile.Write(ref this.testInProgress, 0);
            }
        });
    }

    private void CopyBuffersToConfig()
    {
        this.config.Enabled = this.enabled;
        this.config.ProxyRepositoryReads = this.repositoryReads;
        this.config.ProxyPluginDownloads = this.pluginDownloads;
        this.config.AutoRefresh = this.autoRefresh;
        this.config.ProxyUrl = this.proxyUrl.Trim();
        this.config.Username = this.username.Trim();
        this.config.TestUrl = string.IsNullOrWhiteSpace(this.testUrl) ? "https://github.com/" : this.testUrl.Trim();
    }

    private void SyncBuffersFromConfig()
    {
        this.proxyUrl = this.config.ProxyUrl;
        this.username = this.config.Username;
        this.password = this.plugin.RuntimePassword;
        this.testUrl = this.config.TestUrl;
        this.enabled = this.config.Enabled;
        this.repositoryReads = this.config.ProxyRepositoryReads;
        this.pluginDownloads = this.config.ProxyPluginDownloads;
        this.autoRefresh = this.config.AutoRefresh;
    }

    private static string RepositoryStateLabel(string state) => state switch
    {
        "Success" => "正常",
        "InProgress" => "刷新中",
        "Fail" => "失败",
        "Unknown" => "未刷新",
        "配置读取" => "配置读取",
        _ => state,
    };

    private static string OperationLabel(MarketplaceOperation operation) => operation switch
    {
        MarketplaceOperation.RepositoryMaster => "仓库读取",
        MarketplaceOperation.PluginDownload => "插件下载",
        _ => operation.ToString(),
    };

    public void Dispose()
    {
    }
}
