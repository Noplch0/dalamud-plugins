using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudProxy.Core;
using HarmonyLib;

namespace DalamudProxy;

internal sealed class HttpInterceptor : IDisposable
{
    private const string HarmonyId = "DalamudProxy.HttpInterceptor";
    internal static readonly HttpRequestOptionsKey<bool> ManagedRequestKey = new("DalamudProxy.ManagedRequest");

    private static volatile HttpInterceptor? active;

    private readonly Harmony harmony = new(HarmonyId);
    private readonly IPluginLog log;
    private readonly RequestLog requestLog;
    private readonly string dalamudVersion;

    private ProxyRuleSet rules = ProxyRuleSet.Empty;
    private ProxyTransport? transport;
    private string? blockedReason;
    private int enabled;
    private bool disposed;

    public HttpInterceptor(IPluginLog log, RequestLog requestLog)
    {
        this.log = log;
        this.requestLog = requestLog;
        this.dalamudVersion = typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    public bool Installed { get; private set; }

    public string? InstallationError { get; private set; }

    public void Install()
    {
        active = this;
        var sendAsync = AccessTools.DeclaredMethod(
            typeof(SocketsHttpHandler),
            "SendAsync",
            new[] { typeof(HttpRequestMessage), typeof(CancellationToken) });
        var send = AccessTools.DeclaredMethod(
            typeof(SocketsHttpHandler),
            "Send",
            new[] { typeof(HttpRequestMessage), typeof(CancellationToken) });

        if (sendAsync is null && send is null)
        {
            this.InstallationError = "当前 .NET 运行时中未找到 SocketsHttpHandler.SendAsync/Send。";
            this.log.Error("DalamudProxy: {0}", this.InstallationError);
            active = null;
            return;
        }

        try
        {
            if (sendAsync is not null)
            {
                this.harmony.Patch(
                    sendAsync,
                    prefix: new HarmonyMethod(typeof(HttpInterceptor), nameof(SendAsyncPrefix)));
            }

            if (send is not null)
            {
                this.harmony.Patch(
                    send,
                    prefix: new HarmonyMethod(typeof(HttpInterceptor), nameof(SendPrefix)));
            }

            this.Installed = true;
            this.log.Information(
                "DalamudProxy: HTTP 拦截器已安装。async={Async}, sync={Sync}",
                sendAsync is not null,
                send is not null);
        }
        catch (Exception ex)
        {
            active = null;
            this.InstallationError = $"安装 Harmony 补丁失败：{ex.Message}";
            this.log.Error(ex, "DalamudProxy: 安装 HTTP 拦截补丁失败。");
            try
            {
                this.harmony.UnpatchAll(HarmonyId);
            }
            catch
            {
            }
        }
    }

    public void Configure(ProxySettings settings, ProxyRuleSet rules)
    {
        Volatile.Write(ref this.rules, rules);
        this.blockedReason = null;
        var newTransport = settings.Enabled ? new ProxyTransport(settings, this.dalamudVersion) : null;
        var previous = Interlocked.Exchange(ref this.transport, newTransport);
        Volatile.Write(ref this.enabled, settings.Enabled && newTransport is not null ? 1 : 0);
        previous?.Dispose();

        if (settings.Enabled)
        {
            this.log.Information(
                "DalamudProxy: 规则已更新，仓库={Repos}，下载={Downloads}，资源={Assets}",
                rules.RepositoryCount,
                rules.PluginDownloadCount,
                rules.PluginAssetCount);
        }
    }

    public void Block(ProxyRuleSet rules, string reason)
    {
        Volatile.Write(ref this.rules, rules);
        this.blockedReason = reason;
        Interlocked.Exchange(ref this.transport, null)?.Dispose();
        Volatile.Write(ref this.enabled, rules.IsEmpty ? 0 : 1);
    }

    public void Disable()
    {
        Volatile.Write(ref this.enabled, 0);
        this.blockedReason = null;
        Interlocked.Exchange(ref this.transport, null)?.Dispose();
    }

    public static bool SendAsyncPrefix(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        ref Task<HttpResponseMessage> __result)
    {
        var self = active;
        if (self is null || !self.TryBeginRoute(request, out var operation, out var selectedTransport))
            return true;

        if (selectedTransport is null)
        {
            __result = Task.FromException<HttpResponseMessage>(
                new InvalidOperationException(self.blockedReason ?? "代理配置不可用，已阻止直连。"));
            self.WriteLog(operation, request.RequestUri, 0, 0, "blocked", proxied: false);
            return false;
        }

        __result = self.RouteAsync(selectedTransport, operation, request, cancellationToken);
        return false;
    }

    public static bool SendPrefix(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        ref HttpResponseMessage __result)
    {
        var self = active;
        if (self is null || !self.TryBeginRoute(request, out var operation, out var selectedTransport))
            return true;

        if (selectedTransport is null)
        {
            self.WriteLog(operation, request.RequestUri, 0, 0, "blocked", proxied: false);
            throw new InvalidOperationException(self.blockedReason ?? "代理配置不可用，已阻止直连。");
        }

        __result = self.RouteSync(selectedTransport, operation, request, cancellationToken);
        return false;
    }

    private bool TryBeginRoute(HttpRequestMessage request, out MarketplaceOperation operation, out ProxyTransport selectedTransport)
    {
        operation = default;
        selectedTransport = null!;

        if (this.disposed || Volatile.Read(ref this.enabled) == 0)
            return false;

        if (request.RequestUri is null
            || request.RequestUri.Scheme is not ("http" or "https")
            || (request.Options.TryGetValue(ManagedRequestKey, out var managed) && managed))
        {
            return false;
        }

        var currentRules = Volatile.Read(ref this.rules);
        if (!currentRules.TryGetOperation(request.RequestUri, out operation))
            return false;

        var stack = new StackTrace(2, false);
        var typeNames = stack.GetFrames()
            .Select(static frame => frame.GetMethod()?.DeclaringType?.FullName);
        if (!currentRules.IsOperationAllowed(operation, typeNames))
            return false;

        request.Options.Set(ManagedRequestKey, true);
        selectedTransport = Volatile.Read(ref this.transport)!;
        return true;
    }

    private async Task<HttpResponseMessage> RouteAsync(
        ProxyTransport selectedTransport,
        MarketplaceOperation operation,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var statusCode = 0;
        var result = "ok";
        try
        {
            var response = await selectedTransport
                .SendAsync(request, cancellationToken, HttpCompletionOption.ResponseContentRead)
                .ConfigureAwait(false);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                result = "http-error";
            return response;
        }
        catch (Exception ex)
        {
            result = ex.GetType().Name;
            throw;
        }
        finally
        {
            this.WriteLog(operation, request.RequestUri, statusCode, stopwatch.ElapsedMilliseconds, result);
        }
    }

    private HttpResponseMessage RouteSync(
        ProxyTransport selectedTransport,
        MarketplaceOperation operation,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var statusCode = 0;
        var result = "ok";
        try
        {
            var response = selectedTransport.Send(request, cancellationToken);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                result = "http-error";
            return response;
        }
        catch (Exception ex)
        {
            result = ex.GetType().Name;
            throw;
        }
        finally
        {
            this.WriteLog(operation, request.RequestUri, statusCode, stopwatch.ElapsedMilliseconds, result);
        }
    }

    private void WriteLog(
        MarketplaceOperation operation,
        Uri? requestUri,
        int statusCode,
        long elapsedMilliseconds,
        string result,
        bool proxied = true)
    {
        try
        {
            this.requestLog.Add(new RequestLogEntry(
                DateTimeOffset.Now,
                operation,
                requestUri?.Host ?? string.Empty,
                requestUri is null ? string.Empty : UriUtilities.SanitizeForLog(requestUri),
                proxied,
                statusCode,
                elapsedMilliseconds,
                result));
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "DalamudProxy: 写入请求日志失败。");
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        active = null;
        this.Disable();

        try
        {
            this.harmony.UnpatchAll(HarmonyId);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "DalamudProxy: 卸载 Harmony 补丁失败。");
        }
    }
}
