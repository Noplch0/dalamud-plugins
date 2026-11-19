namespace DalamudProxy.Core;

public static class MarketplaceOperationClassifier
{
    private static readonly IReadOnlyDictionary<MarketplaceOperation, string> TypePrefixes =
        new Dictionary<MarketplaceOperation, string>
        {
            [MarketplaceOperation.RepositoryMaster] = "Dalamud.Plugin.Internal.Types.PluginRepository",
            [MarketplaceOperation.PluginDownload] = "Dalamud.Plugin.Internal.PluginManager",
            [MarketplaceOperation.PluginAsset] = "Dalamud.Interface.Internal.Windows.PluginImageCache",
        };

    public static bool Matches(MarketplaceOperation operation, string? typeFullName)
    {
        if (string.IsNullOrEmpty(typeFullName) || !TypePrefixes.TryGetValue(operation, out var prefix))
            return false;

        return typeFullName.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static bool MatchesAny(MarketplaceOperation operation, IEnumerable<string?> typeFullNames)
        => typeFullNames.Any(name => Matches(operation, name));
}
