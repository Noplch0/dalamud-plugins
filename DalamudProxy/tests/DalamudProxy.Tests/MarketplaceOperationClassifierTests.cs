using DalamudProxy.Core;

namespace DalamudProxy.Tests;

public sealed class MarketplaceOperationClassifierTests
{
    [Fact]
    public void Classify_RepositoryRead()
    {
        var frames = new[]
        {
            new StackFrameInfo("Dalamud.Plugin.Internal.Types.PluginRepository+<GetPluginMaster>d__32", "MoveNext"),
        };

        var operation = MarketplaceOperationClassifier.Classify(frames, InterceptionScope.All);
        Assert.Equal(MarketplaceOperation.RepositoryMaster, operation);
    }

    [Fact]
    public void Classify_PluginDownloadOnlyWhenDownloadMethodIsPresent()
    {
        var downloadFrames = new[]
        {
            new StackFrameInfo("Dalamud.Plugin.Internal.PluginManager+<DownloadPluginAsync>d__97", "MoveNext"),
        };
        var unrelatedManagerFrames = new[]
        {
            new StackFrameInfo("Dalamud.Plugin.Internal.PluginManager+<ReloadReposInternalAsync>d__101", "MoveNext"),
        };

        Assert.Equal(
            MarketplaceOperation.PluginDownload,
            MarketplaceOperationClassifier.Classify(downloadFrames, InterceptionScope.All));
        Assert.Null(MarketplaceOperationClassifier.Classify(unrelatedManagerFrames, InterceptionScope.All));
    }

    [Fact]
    public void Classify_RespectsConfiguredScope()
    {
        var frames = new[]
        {
            new StackFrameInfo("Dalamud.Plugin.Internal.PluginManager+<DownloadPluginAsync>d__97", "MoveNext"),
            new StackFrameInfo("Dalamud.Plugin.Internal.Types.PluginRepository+<GetPluginMaster>d__32", "MoveNext"),
        };

        Assert.Equal(
            MarketplaceOperation.PluginDownload,
            MarketplaceOperationClassifier.Classify(frames, new InterceptionScope(false, true)));
        Assert.Equal(
            MarketplaceOperation.RepositoryMaster,
            MarketplaceOperationClassifier.Classify(frames, new InterceptionScope(true, false)));
        Assert.Null(MarketplaceOperationClassifier.Classify(frames, new InterceptionScope(false, false)));
    }

    [Fact]
    public void PluginImageCacheIsNotInScope()
    {
        var frames = new[]
        {
            new StackFrameInfo("Dalamud.Interface.Internal.Windows.PluginImageCache+<>c", "<DownloadPluginIconAsync>b__0"),
        };

        Assert.Null(MarketplaceOperationClassifier.Classify(frames, InterceptionScope.All));
    }
}
