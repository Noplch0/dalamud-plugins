using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudProxy.Core;

namespace DalamudProxy;

internal sealed record DalamudRepositoryInfo(string Url, string State, object? RuntimeObject);

internal sealed record RepositoryDiscoveryResult(IReadOnlyList<DalamudRepositoryInfo> Repositories, string? Warning);

internal sealed record RepositoryRefreshResult(bool Success, string? Warning);

internal sealed class DalamudRepositoryService
{
    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Type? managerType;

    public DalamudRepositoryService(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
        this.managerType = typeof(IDalamudPluginInterface).Assembly.GetType(
            "Dalamud.Plugin.Internal.PluginManager",
            throwOnError: false);
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

    public async Task<RepositoryRefreshResult> RefreshAllRepositoriesAsync(CancellationToken cancellationToken)
    {
        var manager = this.GetManager(out var managerError);
        if (manager is null)
            return new RepositoryRefreshResult(false, managerError ?? "无法取得 Dalamud PluginManager。");

        var method = manager.GetType().GetMethod(
            "ReloadAllReposAsync",
            InstanceFlags,
            binder: null,
            types: new[] { typeof(bool) },
            modifiers: null);
        if (method is null)
            return new RepositoryRefreshResult(false, "当前 Dalamud 中不存在 ReloadAllReposAsync(Boolean)。");

        async Task InvokeOnCurrentThread()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method.Invoke(manager, new object[] { true }) is not Task task)
                throw new InvalidOperationException("ReloadAllReposAsync 未返回 Task。");

            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (this.framework.IsInFrameworkUpdateThread)
        {
            await InvokeOnCurrentThread().ConfigureAwait(false);
            return new RepositoryRefreshResult(true, null);
        }

        await this.framework.Run(InvokeOnCurrentThread, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return new RepositoryRefreshResult(true, null);
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

    private static PropertyInfo? GetProperty(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName, InstanceFlags);

    private static string? GetString(object instance, string propertyName)
        => GetProperty(instance, propertyName)?.GetValue(instance) as string;

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null })
            exception = exception.InnerException;

        return exception;
    }
}

