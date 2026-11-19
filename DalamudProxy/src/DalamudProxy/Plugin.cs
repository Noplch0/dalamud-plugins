using System;
using System.Collections.Generic;
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

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    private readonly object stateLock = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly WindowSystem windowSystem = new("DalamudProxy");
    private readonly RequestLog requestLog = new();
    private readonly HttpInterceptor interceptor;
    private readonly DalamudRepositoryService repositoryService;
    private readonly ConfigWindow configWindow;

    private IReadOnlyList<DalamudRepositoryInfo> repositories = Array.Empty<DalamudRepositoryInfo>();
    private string? discoveryWarning;
    private string? refreshWarning;
    private string status = "尚未刷新插件仓库";
    private bool refreshing;
    private string runtimePassword = string.Empty;
    private string? configurationError;
    private bool disposed;

    public Plugin()
    {
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

        this.DiscoverRepositories();
        this.ApplyProxyConfiguration(showError: false, scheduleRefresh: false);

        if (this.Configuration.Enabled && this.Configuration.AutoRefresh)
            this.ScheduleInitialRefresh();
    }

    internal Configuration Configuration { get; }

    internal RequestLog RequestLog => this.requestLog;

    internal HttpInterceptor Interceptor => this.interceptor;

    internal string RuntimePassword => this.runtimePassword;

    internal string? ConfigurationError
    {
        get
        {
            lock (this.stateLock)
                return this.configurationError;
        }
    }

    internal ProxyViewState GetViewState()
    {
        lock (this.stateLock)
        {
            return new ProxyViewState(
                this.repositories,
                this.discoveryWarning,
                this.refreshWarning,
                this.status,
                this.refreshing);
        }
    }

    internal void SaveAndApplyConfiguration(string password, bool scheduleRefresh = true)
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

    internal async Task RefreshRepositoriesAsync()
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
            this.SetRefreshing(true, "正在刷新所有插件仓库…");
            this.DiscoverRepositories();

            if (!this.TryCreateProxySettings(out _, out var error))
            {
                lock (this.stateLock)
                {
                    this.refreshWarning = error;
                    this.status = "代理配置无效，已取消刷新";
                }

                return;
            }

            var result = await this.repositoryService
                .RefreshAllRepositoriesAsync(this.lifetime.Token)
                .ConfigureAwait(false);

            lock (this.stateLock)
            {
                this.refreshWarning = result.Warning;
                this.status = result.Success
                    ? "插件仓库已由 Dalamud 刷新"
                    : "Dalamud 仓库刷新失败";
            }

            this.DiscoverRepositories();
            Log.Information("DalamudProxy: 插件仓库刷新完成，success={Success}", result.Success);
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (this.stateLock)
            {
                this.refreshWarning = ex.Message;
                this.status = "插件仓库刷新失败";
            }

            Log.Error(ex, "DalamudProxy: 刷新插件仓库失败。");
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
            using var response = await transport.ProbeAsync(testUri, TimeSpan.FromSeconds(15), cancellation.Token).ConfigureAwait(false);
            return new ProxyTestResult(
                response.IsSuccessStatusCode,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} · {UriUtilities.SanitizeForLog(transport.ProxyUri)}");
        }
        catch (Exception ex)
        {
            return new ProxyTestResult(false, ex.GetBaseException().Message);
        }
    }

    internal void ReloadRepositoryDiscovery() => this.DiscoverRepositories();

    internal void OpenPluginInstaller()
        => PluginInterface.OpenPluginInstallerTo(Dalamud.Interface.PluginInstallerOpenKind.AllPlugins);

    private void NormalizeConfiguration()
    {
        if (this.Configuration.Version < 2)
        {
            this.Configuration.Version = 2;
            this.Configuration.ProxyRepositoryReads = true;
            this.Configuration.ProxyPluginDownloads = true;
        }
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

    private void ApplyProxyConfiguration(bool showError, bool scheduleRefresh = true)
    {
        if (!this.interceptor.Installed)
        {
            lock (this.stateLock)
                this.configurationError = this.interceptor.InstallationError;
            return;
        }

        try
        {
            var scope = this.BuildScope();
            if (!this.TryCreateProxySettings(out var settings, out var error))
            {
                lock (this.stateLock)
                    this.configurationError = error;
                this.interceptor.Block(scope, error ?? "代理配置无效。");
                return;
            }

            lock (this.stateLock)
                this.configurationError = null;
            this.interceptor.Configure(settings, scope);

            if (scheduleRefresh && this.Configuration.Enabled && this.Configuration.AutoRefresh)
                _ = this.RefreshRepositoriesAsync();
        }
        catch (Exception ex)
        {
            lock (this.stateLock)
                this.configurationError = ex.Message;
            this.interceptor.Block(this.BuildScope(), ex.Message);
            Log.Error(ex, "DalamudProxy: 应用代理配置失败。");
        }
    }

    private InterceptionScope BuildScope()
        => new(this.Configuration.ProxyRepositoryReads, this.Configuration.ProxyPluginDownloads);

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
            .GroupBy(static repository => repository.Url, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static repository => repository.Url, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (this.stateLock)
        {
            this.repositories = discovered;
            this.discoveryWarning = result.Warning;
        }
    }

    private void ScheduleInitialRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), this.lifetime.Token).ConfigureAwait(false);
                await this.RefreshRepositoriesAsync().ConfigureAwait(false);
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

    private void SetRefreshing(bool value, string? newStatus)
    {
        lock (this.stateLock)
        {
            this.refreshing = value;
            if (newStatus is not null)
                this.status = newStatus;
        }
    }

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

internal sealed record ProxyViewState(
    IReadOnlyList<DalamudRepositoryInfo> Repositories,
    string? DiscoveryWarning,
    string? RefreshWarning,
    string Status,
    bool Refreshing);

internal sealed record ProxyTestResult(bool Success, string Message);
