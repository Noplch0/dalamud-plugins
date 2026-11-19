namespace DalamudProxy.Core;

public enum MarketplaceOperation
{
    RepositoryMaster,
    PluginDownload,
}

public sealed record InterceptionScope(
    bool RepositoryReads,
    bool PluginDownloads)
{
    public static InterceptionScope All { get; } = new(true, true);

    public bool IsEmpty => !this.RepositoryReads && !this.PluginDownloads;
}

public sealed record ProxySettings(
    bool Enabled,
    string ProxyUrl,
    string? Username,
    string? Password,
    string TestUrl)
{
    public override string ToString()
        => $"ProxySettings {{ Enabled = {this.Enabled}, ProxyUrl = {this.ProxyUrl}, Username = {this.Username}, Password = ***, TestUrl = {this.TestUrl} }}";
}

public sealed record RequestLogEntry(
    DateTimeOffset Timestamp,
    MarketplaceOperation Operation,
    string Host,
    string Path,
    bool Proxied,
    int StatusCode,
    long ElapsedMilliseconds,
    string Result);
