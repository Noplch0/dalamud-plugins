using System;
using System.Collections.Generic;
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
    private string search;
    private bool enabled;
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
        this.search = string.Empty;
        this.enabled = config.Enabled;
        this.autoRefresh = config.AutoRefresh;
        this.Size = new Vector2(880, 760);
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
            ImGui.TextWrapped("代理只用于 Dalamud 插件市场：所选仓库的 pluginmaster、所选插件的图片，以及安装/更新/测试版下载。不会接管游戏或其他插件的普通运行时流量。");
            ImGui.Spacing();

            this.DrawProxySection();
            ImGui.Separator();
            this.DrawRepositories(false);
            ImGui.Separator();
            this.DrawPlugins();
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
        var changed = ImGui.Checkbox("启用插件市场代理", ref this.enabled);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("应用后自动刷新所选仓库", ref this.autoRefresh);

        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputTextWithHint(
            "##proxyUrl",
            "http://127.0.0.1:7890 或 socks5://127.0.0.1:1080",
            ref this.proxyUrl,
            256);

        ImGui.SetNextItemWidth(260);
        changed |= ImGui.InputTextWithHint("##username", "用户名（可选）", ref this.username, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        changed |= ImGui.InputText("##password", ref this.password, 128, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        if (ImGui.Button("测试代理"))
            this.StartProxyTest();

        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputTextWithHint("##testUrl", "测试 URL", ref this.testUrl, 512);

        if (changed)
            this.dirty = true;

        if (ImGui.Button("保存并应用"))
            this.SaveAndApply();

        ImGui.SameLine();
        if (ImGui.Button("刷新所选仓库"))
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

    private void DrawRepositories(bool compact)
    {
        var state = this.plugin.GetCatalogState();
        ImGui.TextUnformatted($"仓库（只读发现：{state.Repositories.Count}）");
        ImGui.TextColored(
            state.Refreshing ? WarningColor : MutedColor,
            state.Refreshing ? $"{state.Status} 正在处理…" : state.Status);

        if (!string.IsNullOrEmpty(state.RefreshWarning))
            ImGui.TextColored(ErrorColor, state.RefreshWarning);

        if (!string.IsNullOrEmpty(state.DiscoveryWarning))
            ImGui.TextWrapped(state.DiscoveryWarning);

        if (state.Repositories.Count == 0)
        {
            ImGui.TextColored(WarningColor, "没有发现 Dalamud 仓库。请先在 Dalamud 设置中添加仓库，然后点击重新读取。");
            if (ImGui.Button("重新读取仓库"))
                this.ReloadDiscovery();
            return;
        }

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
        var height = compact ? 120f : 155f;
        if (ImGui.BeginTable("##repositories", 3, tableFlags, new Vector2(0, height), 0))
        {
            ImGui.TableSetupColumn("代理", ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableSetupColumn("仓库 URL", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();

            for (var i = 0; i < state.Repositories.Count; i++)
            {
                var repository = state.Repositories[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = this.plugin.IsRepositorySelected(repository.Url);
                if (ImGui.Checkbox($"##repo{i}", ref selected))
                {
                    this.plugin.SetRepositorySelected(repository.Url, selected);
                    this.dirty = true;
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(repository.Url);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(RepositoryStateLabel(repository.State));
            }

            ImGui.EndTable();
        }

        if (ImGui.Button("重新读取仓库"))
            this.ReloadDiscovery();

        ImGui.SameLine();
        if (ImGui.Button("清除失效仓库选择"))
        {
            this.plugin.ForgetMissingSelections();
            this.dirty = true;
        }


    }

    private void DrawPlugins()
    {
        var state = this.plugin.GetCatalogState();
        ImGui.TextUnformatted($"目标插件（当前清单：{state.Manifests.Count}）");

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##pluginSearch", "搜索名称、InternalName 或仓库", ref this.search, 256);
        ImGui.SameLine();
        if (ImGui.Button("全选当前仓库插件"))
        {
            this.plugin.SetAllPluginsSelected(true);
            this.dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("清空插件选择"))
        {
            this.plugin.SetAllPluginsSelected(false);
            this.dirty = true;
        }

        var selectedRepos = this.plugin.GetSelectedRepositoryUrlSnapshot()
            .Select(NormalizeOrOriginal)
            .ToHashSet(StringComparer.Ordinal);
        var filtered = state.Manifests
            .Where(manifest => selectedRepos.Contains(NormalizeOrOriginal(manifest.RepositoryMasterUrl)))
            .Where(this.MatchesSearch)
            .ToArray();

        if (filtered.Length == 0)
        {
            ImGui.TextColored(
                MutedColor,
                state.Manifests.Count == 0
                    ? "点击“刷新所选仓库”以通过当前代理读取插件清单。"
                    : "没有符合当前仓库和搜索条件的插件。");
            return;
        }

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##plugins", 6, flags, new Vector2(0, 230), 0))
            return;

        ImGui.TableSetupColumn("代理", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("InternalName", ImGuiTableColumnFlags.WidthFixed, 185);
        ImGui.TableSetupColumn("版本", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 85);
        ImGui.TableSetupColumn("仓库", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < filtered.Length; i++)
        {
            var manifest = filtered[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = this.plugin.ArePluginSelected(manifest.InternalName);
            if (ImGui.Checkbox($"##plugin{i}", ref selected))
            {
                this.plugin.SetPluginSelected(manifest.InternalName, selected);
                this.dirty = true;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(manifest.Name);
            if (!string.IsNullOrWhiteSpace(manifest.Punchline))
                ImGui.TextColored(MutedColor, manifest.Punchline);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(manifest.InternalName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(manifest.AssemblyVersion?.ToString() ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(state.InstalledPlugins.Contains(manifest.InternalName) ? "已安装" : "未安装");
            ImGui.TableNextColumn();
            ImGui.TextWrapped(manifest.RepositoryMasterUrl);
        }

        ImGui.EndTable();
    }

    private void DrawActivityLog()
    {
        ImGui.TextUnformatted("最近代理请求");
        if (ImGui.Button("清空日志"))
            this.plugin.RequestLog.Clear();

        var entries = this.plugin.RequestLog.Snapshot().Reverse().Take(50).ToArray();
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##activity", 6, flags, new Vector2(0, 145), 0))
            return;

        ImGui.TableSetupColumn("时间", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("地址", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("耗时", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("结果", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Timestamp.ToString("HH:mm:ss"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(OperationLabel(entry.Operation));
            ImGui.TableNextColumn();
            ImGui.TextWrapped(entry.Path);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.StatusCode > 0 ? entry.StatusCode.ToString() : "ERR");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{entry.ElapsedMilliseconds} ms");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Result);
        }

        ImGui.EndTable();
    }

    private bool MatchesSearch(PluginManifestInfo manifest)
    {
        if (string.IsNullOrWhiteSpace(this.search))
            return true;

        return manifest.Name.Contains(this.search, StringComparison.OrdinalIgnoreCase)
            || manifest.InternalName.Contains(this.search, StringComparison.OrdinalIgnoreCase)
            || manifest.RepositoryMasterUrl.Contains(this.search, StringComparison.OrdinalIgnoreCase);
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
            else
            {
                this.statusMessage = string.Empty;
                this.testMessage = this.plugin.ConfigurationError;
            }
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

        _ = this.plugin.RefreshCatalogAsync();
    }

    private void ReloadDiscovery()
    {
        this.plugin.ReloadRepositoryDiscovery();
        this.statusMessage = "已重新读取 Dalamud 仓库列表。";
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
            var result = await this.plugin.TestProxyAsync(settings).ConfigureAwait(false);
            this.testMessage = result.Message;
            Volatile.Write(ref this.testInProgress, 0);
        });
    }

    private void CopyBuffersToConfig()
    {
        this.config.Enabled = this.enabled;
        this.config.ProxyUrl = this.proxyUrl.Trim();
        this.config.Username = this.username.Trim();
        this.config.TestUrl = string.IsNullOrWhiteSpace(this.testUrl) ? "https://github.com/" : this.testUrl.Trim();
        this.config.AutoRefresh = this.autoRefresh;
    }

    private void SyncBuffersFromConfig()
    {
        this.proxyUrl = this.config.ProxyUrl;
        this.username = this.config.Username;
        this.password = this.plugin.RuntimePassword;
        this.testUrl = this.config.TestUrl;
        this.enabled = this.config.Enabled;
        this.autoRefresh = this.config.AutoRefresh;
    }
    private static string NormalizeOrOriginal(string value)
        => UriUtilities.TryNormalizeAbsolute(value, out var normalized) ? normalized : value.Trim();

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
        MarketplaceOperation.RepositoryMaster => "仓库清单",
        MarketplaceOperation.PluginDownload => "插件下载",
        MarketplaceOperation.PluginAsset => "插件图片",
        _ => operation.ToString(),
    };

    public void Dispose()
    {
    }
}

