using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudProxy.Core;
using DalamudProxy.Windows;

namespace DalamudProxy;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dalamudproxy";
    private static readonly StringComparer UrlComparer = StringComparer.Ordinal;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;


    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;
    private readonly object catalogLock = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly WindowSystem windowSystem = new("DalamudProxy");
    private readonly RequestLog requestLog = new();
    private readonly HttpInterceptor interceptor;
    private readonly DalamudRepositoryService repositoryService;
    private readonly ConfigWindow configWindow;

    private IReadOnlyList<DalamudRepositoryInfo> repositories = Array.Empty<DalamudRepositoryInfo>();
    private IReadOnlyList<PluginManifestInfo> manifests = Array.Empty<PluginManifestInfo>();
    private IReadOnlySet<string> installedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string? discoveryWarning;
    private string? refreshWarning;
    private string catalogStatus = "尚未刷新";
    private bool refreshing;
    private string runtimePassword = string.Empty;
    private string? configurationError;
    private bool disposed;

    public Plugin()
    {
        HarmonyLoader.EnsureLoaded(PluginInterface.AssemblyLocation.DirectoryName!);

        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.NormalizeConfiguration();
        this.runtimePassword = this.DecryptPassword();

        this.repositoryService = new DalamudRepositoryService(PluginInterface, Framework, Log);
        this.interceptor = new HttpInterceptor(Log, this.requestLog);
        this.interceptor.Install();

        this.configWindow = new ConfigWindow(this);
        this.windowSystem.AddWindow(this.configWindow);

        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleConfigUi;
        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开 Dalamud Proxy 设置。",
        });

        this.ReloadInstalledPluginNames();
        this.DiscoverRepositories();
        this.ApplyProxyConfiguration(showError: false);

        if (this.Configuration.Enabled && this.Configuration.AutoRefresh)
            this.ScheduleInitialRefresh();
        _ = this.RunCatalogSyncLoopAsync();
    }


    public Configuration Configuration { get; }

    public RequestLog RequestLog => this.requestLog;

    public string RuntimePassword => this.runtimePassword;

    internal HttpInterceptor Interceptor => this.interceptor;

    public string? ConfigurationError
    {
        get
        {
            lock (this.catalogLock)
                return this.configurationError;
        }
    }

    internal CatalogViewState GetCatalogState()
    {
        lock (this.catalogLock)
        {
            return new CatalogViewState(
                this.repositories.ToArray(),
                this.manifests.ToArray(),
                this.installedPlugins,
                this.discoveryWarning,
                this.refreshWarning,
                this.catalogStatus,
                this.refreshing);
        }
    }

    public IReadOnlyList<string> SelectedRepositoryUrls
    {
        get
        {
            lock (this.catalogLock)
                return this.Configuration.SelectedRepositoryUrls.ToArray();
        }
    }

    public IReadOnlyList<string> SelectedPluginInternalNames
    {
        get
        {
            lock (this.catalogLock)
                return this.Configuration.SelectedPluginInternalNames.ToArray();
        }
    }

    public IReadOnlyList<string> GetSelectedRepositoryUrlSnapshot() => this.SelectedRepositoryUrls;

    public bool IsRepositorySelected(string url)
    {
        var normalized = NormalizeOrOriginal(url);
        lock (this.catalogLock)
            return this.Configuration.SelectedRepositoryUrls.Contains(normalized, UrlComparer);
    }

    public bool ArePluginSelected(string internalName)
    {
        lock (this.catalogLock)
            return this.Configuration.SelectedPluginInternalNames.Contains(internalName, StringComparer.OrdinalIgnoreCase);
    }

    public void SetRepositorySelected(string url, bool selected)
    {
        var normalized = NormalizeOrOriginal(url);
        lock (this.catalogLock)
        {
            if (selected)
            {
                if (!this.Configuration.SelectedRepositoryUrls.Contains(normalized, UrlComparer))
                    this.Configuration.SelectedRepositoryUrls.Add(normalized);
            }
            else
            {
                this.Configuration.SelectedRepositoryUrls.RemoveAll(
                    item => string.Equals(NormalizeOrOriginal(item), normalized, StringComparison.Ordinal));
            }
        }
    }

    public void SetPluginSelected(string internalName, bool selected)
    {
        lock (this.catalogLock)
        {
            if (selected)
            {
                if (!this.Configuration.SelectedPluginInternalNames.Contains(internalName, StringComparer.OrdinalIgnoreCase))
                    this.Configuration.SelectedPluginInternalNames.Add(internalName);
            }
            else
            {
                this.Configuration.SelectedPluginInternalNames.RemoveAll(
                    item => string.Equals(item, internalName, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public void SetAllPluginsSelected(bool selected)
    {
        var state = this.GetCatalogState();
        var selectedRepos = new HashSet<string>(
            this.GetSelectedRepositoryUrlSnapshot().Select(NormalizeOrOriginal),
            UrlComparer);

        if (!selected)
        {
            lock (this.catalogLock)
                this.Configuration.SelectedPluginInternalNames.Clear();
            return;
        }

        var relevant = state.Manifests
            .Where(manifest => selectedRepos.Contains(NormalizeOrOriginal(manifest.RepositoryMasterUrl)))
            .Select(static manifest => manifest.InternalName)
            .ToArray();

        foreach (var name in relevant)
            this.SetPluginSelected(name, true);
    }

    public void SaveAndApplyConfiguration(string password, bool scheduleRefresh = true)
    {
        this.runtimePassword = password ?? string.Empty;
        this.ExtractInlineProxyCredentials();
        this.Configuration.ProtectedPassword = string.IsNullOrEmpty(this.runtimePassword)
            ? string.Empty
            : DpapiSecretStore.Protect(this.runtimePassword);
        this.Configuration.Username = this.Configuration.Username?.Trim() ?? string.Empty;
        this.Configuration.ProxyUrl = this.Configuration.ProxyUrl?.Trim() ?? string.Empty;
        this.Configuration.TestUrl = string.IsNullOrWhiteSpace(this.Configuration.TestUrl)
            ? "https://github.com/"
            : this.Configuration.TestUrl.Trim();
        this.NormalizeConfiguration();
        this.Configuration.Save();

        this.DiscoverRepositories();
        this.ApplyProxyConfiguration(showError: true, scheduleRefresh);
    }

    public void RebuildRules()
    {
        var rules = this.BuildRuleSet();
        if (!this.interceptor.Installed)
            return;

        if (this.TryCreateProxySettings(out var settings, out var error))
        {
            this.interceptor.Configure(settings, rules);
            return;
        }

        if (this.Configuration.Enabled)
            this.interceptor.Block(rules, error ?? "代理配置无效。");
    }

    public async Task RefreshCatalogAsync()
    {
        try
        {
            await this.refreshGate.WaitAsync(this.lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
            return;
        }
        try
        {
            this.SetRefreshing(true, "正在读取仓库并刷新插件清单…");
            this.DiscoverRepositories();
            var selected = this.GetDiscoveredSelectedRepositoryUrls();
            if (!this.TryCreateProxySettings(out var settings, out var proxyError))
            {
                lock (this.catalogLock)
                {
                    this.refreshWarning = proxyError;
                    this.catalogStatus = "代理配置无效，已取消刷新";
                }

                return;
            }

            if (selected.Count == 0)
            {
                lock (this.catalogLock)
                {
                    this.manifests = Array.Empty<PluginManifestInfo>();
                    this.refreshWarning = proxyError;
                    this.catalogStatus = "请先勾选至少一个仓库";
                }

                this.RebuildRules();
                return;
            }

            var result = await this.repositoryService
                .RefreshSelectedAsync(selected, settings, this.lifetime.Token)
                .ConfigureAwait(false);

            lock (this.catalogLock)
            {
                this.manifests = result.Plugins;
                this.refreshWarning = result.Warning ?? proxyError;
                var managerText = result.ManagerRefreshSucceeded ? "Dalamud 列表已刷新" : "仅本地目标清单已刷新";
                this.catalogStatus = $"{managerText}，共 {result.Plugins.Count} 个插件";
            }

            this.DiscoverRepositories();
            this.ReloadInstalledPluginNames();
            this.RebuildRules();
            Log.Information("DalamudProxy: 市场清单刷新完成，共 {Count} 个插件。", result.Plugins.Count);
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (this.catalogLock)
            {
                this.refreshWarning = ex.Message;
                this.catalogStatus = "刷新失败";
            }

            Log.Error(ex, "DalamudProxy: 刷新插件市场失败。");
            this.RebuildRules();
        }
        finally
        {
            this.SetRefreshing(false, null);
            this.refreshGate.Release();
        }
    }

    internal async Task<ProxyTestResult> TestProxyAsync(ProxySettings settings)
    {
        if (!settings.Enabled)
            return new ProxyTestResult(false, "代理未启用。");

        if (!Uri.TryCreate(settings.TestUrl, UriKind.Absolute, out var testUri)
            || testUri.Scheme is not ("http" or "https"))
            return new ProxyTestResult(false, "测试 URL 必须是 http/https 绝对地址。");

        try
        {
            using var transport = new ProxyTransport(
                settings,
                typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString(3));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await transport.ProbeAsync(testUri, TimeSpan.FromSeconds(15), cancellation.Token)
                .ConfigureAwait(false);
            return new ProxyTestResult(
                response.IsSuccessStatusCode,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} · {UriUtilities.SanitizeForLog(transport.ProxyUri)}");
        }
        catch (Exception ex)
        {
            return new ProxyTestResult(false, ex.GetBaseException().Message);
        }
    }

    public ProxySettings BuildCurrentProxySettings(string password)
        => new(
            this.Configuration.Enabled,
            this.Configuration.ProxyUrl,
            string.IsNullOrWhiteSpace(this.Configuration.Username) ? null : this.Configuration.Username,
            string.IsNullOrEmpty(password) ? null : password,
            this.Configuration.TestUrl);

    public void ReloadRepositoryDiscovery() => this.DiscoverRepositories();

    public void OpenPluginInstaller()
        => PluginInterface.OpenPluginInstallerTo(Dalamud.Interface.PluginInstallerOpenKind.AllPlugins);

    public void ForgetMissingSelections()
    {
        var discovered = this.GetCatalogState().Repositories
            .Select(static repository => NormalizeOrOriginal(repository.Url))
            .ToHashSet(UrlComparer);
        lock (this.catalogLock)
        {
            this.Configuration.SelectedRepositoryUrls.RemoveAll(
                url => !discovered.Contains(NormalizeOrOriginal(url)));
            this.Configuration.Save();
        }

        this.RebuildRules();
    }

    private void NormalizeConfiguration()
    {
        lock (this.catalogLock)
        {
            this.Configuration.Version = 1;
            this.Configuration.SelectedRepositoryUrls = this.Configuration.SelectedRepositoryUrls
                .Where(static url => !string.IsNullOrWhiteSpace(url))
                .Select(NormalizeOrOriginal)
                .Distinct(UrlComparer)
                .ToList();
            this.Configuration.SelectedPluginInternalNames = this.Configuration.SelectedPluginInternalNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void ExtractInlineProxyCredentials()
    {
        if (!Uri.TryCreate(this.Configuration.ProxyUrl, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.UserInfo))
        {
            return;
        }

        var parts = uri.UserInfo.Split(':', 2);
        if (!string.IsNullOrWhiteSpace(parts[0]))
            this.Configuration.Username = Uri.UnescapeDataString(parts[0]);
        if (parts.Length > 1)
            this.runtimePassword = Uri.UnescapeDataString(parts[1]);

        var clean = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        this.Configuration.ProxyUrl = clean.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped);
    }

    private string DecryptPassword()
    {
        try
        {
            return DpapiSecretStore.Unprotect(this.Configuration.ProtectedPassword);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DalamudProxy: 已保存的代理密码无法解密，请重新输入。");
            return string.Empty;
        }
    }

    private ProxyRuleSet BuildRuleSet()
    {
        lock (this.catalogLock)
        {
            return ProxyRuleSet.Build(
                this.Configuration.SelectedRepositoryUrls,
                this.manifests,
                this.Configuration.SelectedPluginInternalNames);
        }
    }

    private void ApplyProxyConfiguration(bool showError, bool scheduleRefresh = true)
    {
        if (!this.interceptor.Installed)
        {
            lock (this.catalogLock)
                this.configurationError = this.interceptor.InstallationError;
            return;
        }

        try
        {
            if (!this.TryCreateProxySettings(out var settings, out var error))
            {
                lock (this.catalogLock)
                    this.configurationError = error;
                this.interceptor.Block(this.BuildRuleSet(), error ?? "代理配置无效。");
                return;
            }

            lock (this.catalogLock)
                this.configurationError = null;

            var rules = this.BuildRuleSet();
            this.interceptor.Configure(settings, rules);

            if (scheduleRefresh && this.Configuration.Enabled && this.Configuration.AutoRefresh)
                _ = this.RefreshCatalogAsync();
        }
        catch (Exception ex)
        {
            lock (this.catalogLock)
                this.configurationError = ex.Message;
            this.interceptor.Block(this.BuildRuleSet(), ex.Message);
            Log.Error(ex, "DalamudProxy: 应用代理配置失败。");
        }
    }

    private bool TryCreateProxySettings(out ProxySettings settings, out string? error)
    {
        error = null;
        settings = new ProxySettings(
            this.Configuration.Enabled,
            this.Configuration.ProxyUrl,
            string.IsNullOrWhiteSpace(this.Configuration.Username) ? null : this.Configuration.Username,
            string.IsNullOrEmpty(this.runtimePassword) ? null : this.runtimePassword,
            this.Configuration.TestUrl);

        if (!this.Configuration.Enabled)
            return true;

        if (!UriUtilities.TryCreateProxyEndpoint(
                settings.ProxyUrl,
                settings.Username,
                settings.Password,
                out _,
                out var parseError))
        {
            error = parseError;
            return false;
        }

        return true;
    }

    private void DiscoverRepositories()
    {
        var result = this.repositoryService.Discover();
        var discovered = result.Repositories
            .GroupBy(static repository => NormalizeOrOriginal(repository.Url), UrlComparer)
            .Select(static group => group.First())
            .OrderBy(static repository => repository.Url, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (this.catalogLock)
        {
            this.repositories = discovered;
            this.discoveryWarning = result.Warning;
        }
    }

    private IReadOnlyList<string> GetDiscoveredSelectedRepositoryUrls()
    {
        var discovered = this.GetCatalogState().Repositories
            .Select(static repository => NormalizeOrOriginal(repository.Url))
            .ToHashSet(UrlComparer);
        return this.GetSelectedRepositoryUrlSnapshot()
            .Select(NormalizeOrOriginal)
            .Where(discovered.Contains)
            .Distinct(UrlComparer)
            .ToArray();
    }

    private void ReloadInstalledPluginNames()
    {
        var names = PluginInterface.InstalledPlugins
            .Select(static plugin => plugin.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (this.catalogLock)
            this.installedPlugins = names;
    }

    private void ScheduleInitialRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), this.lifetime.Token).ConfigureAwait(false);
                await this.RefreshCatalogAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DalamudProxy: 初始自动刷新失败。");
            }
        });
    }

    private async Task RunCatalogSyncLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(45));
        try
        {
            while (await timer.WaitForNextTickAsync(this.lifetime.Token).ConfigureAwait(false))
            {
                var selected = this.GetDiscoveredSelectedRepositoryUrls();
                if (selected.Count == 0)
                    continue;

                var current = this.repositoryService.ReadSelectedManifests(selected);
                if (current.Count == 0)
                    continue;

                var previous = this.GetCatalogState().Manifests;
                if (string.Equals(CatalogFingerprint(previous), CatalogFingerprint(current), StringComparison.Ordinal))
                    continue;

                lock (this.catalogLock)
                {
                    this.manifests = current;
                    this.catalogStatus = "已同步 Dalamud 当前插件清单";
                }

                this.RebuildRules();
            }
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DalamudProxy: 后台同步插件清单失败。");
        }
    }

    private static string CatalogFingerprint(IEnumerable<PluginManifestInfo> manifests)
    {
        return string.Join(
            "\n",
            manifests
                .OrderBy(static item => item.RepositoryMasterUrl, StringComparer.Ordinal)
                .ThenBy(static item => item.InternalName, StringComparer.OrdinalIgnoreCase)
                .Select(static item => string.Join(
                    "|",
                    item.RepositoryMasterUrl,
                    item.InternalName,
                    item.DownloadLinkInstall,
                    item.DownloadLinkUpdate,
                    item.DownloadLinkTesting,
                    item.IconUrl,
                    string.Join(",", item.ImageUrls))));
    }

    private void SetRefreshing(bool value, string? status)
    {
        lock (this.catalogLock)
        {
            this.refreshing = value;
            if (status is not null)
                this.catalogStatus = status;
        }
    }

    private static string NormalizeOrOriginal(string value)
        => UriUtilities.TryNormalizeAbsolute(value, out var normalized) ? normalized : value.Trim();

    private void OnCommand(string command, string args) => this.ToggleConfigUi();

    private void ToggleConfigUi() => this.configWindow.Toggle();

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleConfigUi;
        this.lifetime.Cancel();
        this.windowSystem.RemoveAllWindows();
        this.configWindow.Dispose();
        this.interceptor.Dispose();
        this.refreshGate.Dispose();
        this.lifetime.Dispose();
        this.Configuration.Save();
    }
}

internal sealed record CatalogViewState(
    IReadOnlyList<DalamudRepositoryInfo> Repositories,
    IReadOnlyList<PluginManifestInfo> Manifests,
    IReadOnlySet<string> InstalledPlugins,
    string? DiscoveryWarning,
    string? RefreshWarning,
    string Status,
    bool Refreshing);

internal sealed record ProxyTestResult(bool Success, string Message);







