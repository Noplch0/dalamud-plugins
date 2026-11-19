namespace DalamudProxy.Core;

public enum MarketplaceOperation
{
    RepositoryMaster,
    PluginDownload,
    PluginAsset,
}

public sealed record ProxySettings(
    bool Enabled,
    string ProxyUrl,
    string? Username,
    string? Password,
    string TestUrl)
{
    public static ProxySettings Disabled { get; } = new(false, string.Empty, null, null, "https://github.com/");

    public override string ToString()
        => $"ProxySettings {{ Enabled = {this.Enabled}, ProxyUrl = {this.ProxyUrl}, Username = {this.Username}, Password = ***, TestUrl = {this.TestUrl} }}";
}

public sealed record PluginManifestInfo(
    string RepositoryMasterUrl,
    string InternalName,
    string Name,
    string? Author,
    string? Punchline,
    Version? AssemblyVersion,
    string? DownloadLinkInstall,
    string? DownloadLinkUpdate,
    string? DownloadLinkTesting,
    string? IconUrl,
    IReadOnlyList<string> ImageUrls);

public sealed record RequestLogEntry(
    DateTimeOffset Timestamp,
    MarketplaceOperation Operation,
    string Host,
    string Path,
    bool Proxied,
    int StatusCode,
    long ElapsedMilliseconds,
    string Result);
