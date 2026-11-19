using DalamudProxy.Core;

namespace DalamudProxy.Tests;

public sealed class ProxyRuleSetTests
{
    private const string Repo = "https://repo.example/pluginmaster.json";

    private static readonly PluginManifestInfo Manifest = new(
        Repo,
        "SamplePlugin",
        "Sample Plugin",
        "Author",
        "Punchline",
        new Version(1, 2, 3, 4),
        "https://downloads.example/sample-install.zip",
        "https://downloads.example/sample-update.zip",
        "https://downloads.example/sample-testing.zip",
        "https://images.example/sample-icon.png",
        new[] { "https://images.example/sample-shot-1.png" });

    [Fact]
    public void Build_UsesSelectedRepositoriesAndPlugins()
    {
        var rules = ProxyRuleSet.Build(
            new[] { Repo, "https://ignored.example/repo.json" },
            new[]
            {
                Manifest,
                Manifest with { InternalName = "NotSelected" },
                Manifest with
                {
                    RepositoryMasterUrl = "https://unselected.example/pluginmaster.json",
                    DownloadLinkInstall = "https://downloads.example/unselected-repo.zip",
                },
            },
            new[] { "SamplePlugin" });

        Assert.True(rules.TryGetOperation(new Uri(Repo), out var repoOperation));
        Assert.Equal(MarketplaceOperation.RepositoryMaster, repoOperation);

        Assert.True(rules.TryGetOperation(new Uri(Manifest.DownloadLinkInstall!), out var downloadOperation));
        Assert.Equal(MarketplaceOperation.PluginDownload, downloadOperation);

        Assert.True(rules.TryGetOperation(new Uri(Manifest.IconUrl!), out var assetOperation));
        Assert.Equal(MarketplaceOperation.PluginAsset, assetOperation);

        Assert.False(rules.TryGetOperation(new Uri("https://downloads.example/not-selected.zip"), out _));
        Assert.False(rules.TryGetOperation(new Uri("https://downloads.example/unselected-repo.zip"), out _));
        Assert.Equal(2, rules.RepositoryCount);
        Assert.Equal(3, rules.PluginDownloadCount);
        Assert.Equal(2, rules.PluginAssetCount);
    }

    [Fact]
    public void OperationClassifier_MatchesOnlyMarketplaceCallers()
    {
        Assert.True(MarketplaceOperationClassifier.Matches(
            MarketplaceOperation.RepositoryMaster,
            "Dalamud.Plugin.Internal.Types.PluginRepository+<GetPluginMaster>d__32"));

        Assert.True(MarketplaceOperationClassifier.Matches(
            MarketplaceOperation.PluginDownload,
            "Dalamud.Plugin.Internal.PluginManager+<DownloadPluginAsync>d__97"));

        Assert.True(MarketplaceOperationClassifier.Matches(
            MarketplaceOperation.PluginAsset,
            "Dalamud.Interface.Internal.Windows.PluginImageCache+<>c__DisplayClass46_0"));

        Assert.False(MarketplaceOperationClassifier.Matches(
            MarketplaceOperation.PluginDownload,
            "SomeOtherPlugin.NetworkClient"));
    }

    [Fact]
    public void EmptyRules_DoNotMatch()
    {
        Assert.True(ProxyRuleSet.Empty.IsEmpty);
        Assert.False(ProxyRuleSet.Empty.TryGetOperation(new Uri(Repo), out _));
    }
}

