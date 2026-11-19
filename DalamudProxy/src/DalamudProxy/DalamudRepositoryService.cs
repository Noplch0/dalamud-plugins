using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudProxy.Core;

namespace DalamudProxy;

internal sealed record DalamudRepositoryInfo(string Url, string State, object? RuntimeObject);

internal sealed record RepositoryDiscoveryResult(IReadOnlyList<DalamudRepositoryInfo> Repositories, string? Warning);

internal sealed record CatalogRefreshResult(
    IReadOnlyList<PluginManifestInfo> Plugins,
    bool ManagerRefreshSucceeded,
    string? Warning);

internal sealed class DalamudRepositoryService
{
    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly Type? managerType;
    private readonly Type? repositoryType;

    public DalamudRepositoryService(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
        var assembly = typeof(IDalamudPluginInterface).Assembly;
        this.managerType = assembly.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: false);
        this.repositoryType = assembly.GetType("Dalamud.Plugin.Internal.Types.PluginRepository", throwOnError: false);
    }

    public RepositoryDiscoveryResult Discover()
    {
        var runtime = this.TryGetRuntimeRepositories(out var managerError);
        if (runtime.Count > 0)
            return new RepositoryDiscoveryResult(runtime, managerError);

        var fallback = DalamudConfigurationFallback.ReadRepositoryUrls(this.log);
        if (fallback.Count == 0)
            return new RepositoryDiscoveryResult(Array.Empty<DalamudRepositoryInfo>(), managerError ?? "未发现 Dalamud 仓库配置。");

        return new RepositoryDiscoveryResult(
            fallback.Select(static url => new DalamudRepositoryInfo(url, "配置读取", null)).ToArray(),
            managerError is null ? "内部服务不可用，当前从 dalamudConfig.json 只读发现仓库。" : managerError);
    }

    public IReadOnlyList<PluginManifestInfo> ReadSelectedManifests(IReadOnlyCollection<string> selectedRepositoryUrls)
    {
        var selected = selectedRepositoryUrls
            .Select(static url => UriUtilities.TryNormalizeAbsolute(url, out var normalized) ? normalized : url)
            .ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
        var manifests = this.ReadManifestsFromRuntime(selected, warnings);
        return manifests;
    }

    public async Task<CatalogRefreshResult> RefreshSelectedAsync(
        IReadOnlyCollection<string> selectedRepositoryUrls,
        ProxySettings proxySettings,
        CancellationToken cancellationToken)
    {
        var selected = new HashSet<string>(
            selectedRepositoryUrls.Select(NormalizeOrOriginal),
            StringComparer.Ordinal);
        if (selected.Count == 0)
            return new CatalogRefreshResult(Array.Empty<PluginManifestInfo>(), true, null);

        var warnings = new List<string>();
        var managerRefreshed = true;
        var repositories = this.TryGetRuntimeRepositories(out var managerError);

        if (repositories.Count == 0)
        {
            managerRefreshed = false;
            if (managerError is not null)
                warnings.Add(managerError);
        }
        else
        {
            foreach (var repository in repositories.Where(r => selected.Contains(NormalizeOrOriginal(r.Url))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await this.ReloadRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    managerRefreshed = false;
                    warnings.Add($"刷新 {repository.Url} 失败：{Unwrap(ex).Message}");
                    this.log.Warning(ex, "DalamudProxy: 通过 Dalamud 刷新仓库失败 {Url}", repository.Url);
                }
            }
        }

        var manifests = this.ReadManifestsFromRuntime(selected, warnings);
        var foundRepositories = new HashSet<string>(
            manifests.Select(static manifest => NormalizeOrOriginal(manifest.RepositoryMasterUrl)),
            StringComparer.Ordinal);

        using var fallbackTransport = this.CreateCatalogTransport(proxySettings);
        foreach (var repositoryUrl in selected)
        {
            if (foundRepositories.Contains(repositoryUrl))
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var parsed = await this.FetchMasterAsync(repositoryUrl, fallbackTransport, cancellationToken)
                    .ConfigureAwait(false);
                manifests.AddRange(parsed);
                foundRepositories.Add(repositoryUrl);
            }
            catch (Exception ex)
            {
                warnings.Add($"抓取 {repositoryUrl} 的 pluginmaster 失败：{Unwrap(ex).Message}");
                this.log.Warning(ex, "DalamudProxy: 自行抓取 pluginmaster 失败 {Url}", repositoryUrl);
            }
        }

        var deduplicated = manifests
            .GroupBy(
                static item => $"{NormalizeOrOriginal(item.RepositoryMasterUrl)}|{item.InternalName}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new CatalogRefreshResult(
            deduplicated,
            managerRefreshed,
            warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings));
    }

    private List<DalamudRepositoryInfo> TryGetRuntimeRepositories(out string? error)
    {
        error = null;
        var result = new List<DalamudRepositoryInfo>();
        var manager = this.GetManager(out error);
        if (manager is null)
            return result;

        var repositories = GetProperty(manager, "Repos")?.GetValue(manager) as IEnumerable;
        if (repositories is null)
        {
            error = "无法读取 PluginManager.Repos。";
            return result;
        }

        foreach (var repository in repositories)
        {
            var url = GetString(repository, "PluginMasterUrl");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var state = GetProperty(repository, "State")?.GetValue(repository)?.ToString() ?? "未知";
            result.Add(new DalamudRepositoryInfo(url, state, repository));
        }

        return result;
    }

    private object? GetManager(out string? error)
    {
        error = null;
        if (this.managerType is null)
        {
            error = "当前 Dalamud 中不存在 PluginManager 类型。";
            return null;
        }

        try
        {
            var service = ((IServiceProvider)this.pluginInterface).GetService(this.managerType);
            if (service is not null)
                return service;
        }
        catch (Exception ex)
        {
            error = $"读取 Dalamud PluginManager 服务失败：{ex.Message}";
        }

        try
        {
            var create = typeof(IDalamudPluginInterface).GetMethod(
                "Create",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(object[]) },
                modifiers: null);
            if (create is not null)
            {
                var typed = create.MakeGenericMethod(this.managerType);
                return typed.Invoke(this.pluginInterface, new object?[] { Array.Empty<object>() });
            }
        }
        catch (Exception ex)
        {
            error ??= $"解析 Dalamud PluginManager 失败：{Unwrap(ex).Message}";
        }

        error ??= "无法取得 Dalamud PluginManager；将仅使用配置文件发现仓库。";
        return null;
    }

    private async Task ReloadRepositoryAsync(DalamudRepositoryInfo repository, CancellationToken cancellationToken)
    {
        if (repository.RuntimeObject is null || this.repositoryType is null)
            throw new InvalidOperationException("仓库运行时对象不可用。");

        var manager = this.GetManager(out var managerError);
        if (manager is null)
            throw new InvalidOperationException(managerError ?? "PluginManager 不可用。");

        var method = manager.GetType().GetMethod(
            "ReloadRepoAsync",
            InstanceFlags,
            binder: null,
            types: new[] { this.repositoryType },
            modifiers: null);
        if (method is null)
            throw new MissingMethodException(manager.GetType().FullName, "ReloadRepoAsync");

        async Task InvokeOnCurrentThread()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method.Invoke(manager, new[] { repository.RuntimeObject }) is Task task)
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                throw new InvalidOperationException("ReloadRepoAsync 未返回 Task。");
        }

        if (this.framework.IsInFrameworkUpdateThread)
        {
            await InvokeOnCurrentThread().ConfigureAwait(false);
        }
        else
        {
            await this.framework.Run(InvokeOnCurrentThread, cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private List<PluginManifestInfo> ReadManifestsFromRuntime(ISet<string> selected, ICollection<string> warnings)
    {
        var result = new List<PluginManifestInfo>();
        var repositories = this.TryGetRuntimeRepositories(out var error);
        if (repositories.Count == 0)
        {
            if (error is not null)
                warnings.Add(error);
            return result;
        }

        foreach (var repository in repositories.Where(r => selected.Contains(NormalizeOrOriginal(r.Url))))
        {
            if (repository.RuntimeObject is null)
                continue;

            try
            {
                var master = GetProperty(repository.RuntimeObject, "PluginMaster")?.GetValue(repository.RuntimeObject) as IEnumerable;
                if (master is null)
                    continue;

                foreach (var manifest in master)
                {
                    var parsed = ReadManifest(repository.Url, manifest);
                    if (parsed is not null)
                        result.Add(parsed);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"读取 {repository.Url} 的运行时插件清单失败：{Unwrap(ex).Message}");
            }
        }

        return result;
    }

    private ProxyTransport? CreateCatalogTransport(ProxySettings settings)
        => settings.Enabled ? new ProxyTransport(settings, this.dalamudVersion()) : null;

    private string dalamudVersion()
        => typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private async Task<IReadOnlyList<PluginManifestInfo>> FetchMasterAsync(
        string repositoryUrl,
        ProxyTransport? transport,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, repositoryUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        HttpResponseMessage response;
        string json;
        if (transport is not null)
        {
            response = await transport.SendAsync(request, cancellationToken, HttpCompletionOption.ResponseContentRead)
                .ConfigureAwait(false);
        }
        else
        {
            using var client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Dalamud/{this.dalamudVersion()}");
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        return PluginMasterParser.Parse(json, repositoryUrl);
    }

    private static PluginManifestInfo? ReadManifest(string repositoryUrl, object manifest)
    {
        var internalName = GetString(manifest, "InternalName");
        if (string.IsNullOrWhiteSpace(internalName))
            return null;

        var imageUrls = new List<string>();
        if (GetProperty(manifest, "ImageUrls")?.GetValue(manifest) is IEnumerable images)
        {
            foreach (var image in images)
            {
                if (image is string value && !string.IsNullOrWhiteSpace(value))
                    imageUrls.Add(value);
            }
        }

        Version? version = null;
        if (GetProperty(manifest, "AssemblyVersion")?.GetValue(manifest) is Version parsedVersion)
            version = parsedVersion;

        return new PluginManifestInfo(
            repositoryUrl,
            internalName,
            GetString(manifest, "Name") ?? internalName,
            GetString(manifest, "Author"),
            GetString(manifest, "Punchline"),
            version,
            GetString(manifest, "DownloadLinkInstall"),
            GetString(manifest, "DownloadLinkUpdate"),
            GetString(manifest, "DownloadLinkTesting"),
            GetString(manifest, "IconUrl"),
            imageUrls);
    }

    private static PropertyInfo? GetProperty(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName, InstanceFlags);

    private static string? GetString(object instance, string propertyName)
        => GetProperty(instance, propertyName)?.GetValue(instance) as string;

    private static string NormalizeOrOriginal(string url)
        => UriUtilities.TryNormalizeAbsolute(url, out var normalized) ? normalized : url;

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null })
            exception = exception.InnerException;

        return exception;
    }
}
