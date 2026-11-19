namespace DalamudProxy.Core;

public sealed class ProxyRuleSet
{
    private readonly HashSet<string> repositoryMasterUrls;
    private readonly HashSet<string> pluginDownloadUrls;
    private readonly HashSet<string> pluginAssetUrls;

    private ProxyRuleSet(
        HashSet<string> repositoryMasterUrls,
        HashSet<string> pluginDownloadUrls,
        HashSet<string> pluginAssetUrls)
    {
        this.repositoryMasterUrls = repositoryMasterUrls;
        this.pluginDownloadUrls = pluginDownloadUrls;
        this.pluginAssetUrls = pluginAssetUrls;
    }

    public static ProxyRuleSet Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    public int RepositoryCount => this.repositoryMasterUrls.Count;

    public int PluginDownloadCount => this.pluginDownloadUrls.Count;

    public int PluginAssetCount => this.pluginAssetUrls.Count;

    public bool IsEmpty => this.RepositoryCount == 0 && this.PluginDownloadCount == 0 && this.PluginAssetCount == 0;

    public static ProxyRuleSet Build(
        IEnumerable<string> selectedRepositoryUrls,
        IEnumerable<PluginManifestInfo> manifests,
        IEnumerable<string> selectedPluginInternalNames)
    {
        var selectedPlugins = new HashSet<string>(
            selectedPluginInternalNames.Where(static x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);

        var repositories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repositoryUrl in selectedRepositoryUrls)
        {
            if (UriUtilities.TryNormalizeAbsolute(repositoryUrl, out var normalized))
                repositories.Add(normalized);
        }

        var downloads = new HashSet<string>(StringComparer.Ordinal);
        var assets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var manifest in manifests)
        {
            if (!selectedPlugins.Contains(manifest.InternalName)
                || !UriUtilities.TryNormalizeAbsolute(manifest.RepositoryMasterUrl, out var manifestRepository)
                || !repositories.Contains(manifestRepository))
            {
                continue;
            }

            AddNormalized(downloads, manifest.DownloadLinkInstall);
            AddNormalized(downloads, manifest.DownloadLinkUpdate);
            AddNormalized(downloads, manifest.DownloadLinkTesting);
            AddNormalized(assets, manifest.IconUrl);

            foreach (var imageUrl in manifest.ImageUrls)
                AddNormalized(assets, imageUrl);
        }

        return new ProxyRuleSet(repositories, downloads, assets);
    }

    public bool TryGetOperation(Uri uri, out MarketplaceOperation operation)
    {
        operation = default;
        var normalized = UriUtilities.Normalize(uri);
        if (normalized.Length == 0)
            return false;

        if (this.repositoryMasterUrls.Contains(normalized))
        {
            operation = MarketplaceOperation.RepositoryMaster;
            return true;
        }

        if (this.pluginDownloadUrls.Contains(normalized))
        {
            operation = MarketplaceOperation.PluginDownload;
            return true;
        }

        if (this.pluginAssetUrls.Contains(normalized))
        {
            operation = MarketplaceOperation.PluginAsset;
            return true;
        }

        return false;
    }

    public bool IsOperationAllowed(MarketplaceOperation operation, IEnumerable<string?> stackTypeNames)
        => MarketplaceOperationClassifier.MatchesAny(operation, stackTypeNames);

    private static void AddNormalized(ISet<string> target, string? value)
    {
        if (UriUtilities.TryNormalizeAbsolute(value, out var normalized))
            target.Add(normalized);
    }
}

