namespace DalamudProxy.Core;

public sealed record StackFrameInfo(string? TypeName, string? MethodName);

public static class MarketplaceOperationClassifier
{
    private const string RepositoryTypePrefix = "Dalamud.Plugin.Internal.Types.PluginRepository";
    private const string PluginManagerTypePrefix = "Dalamud.Plugin.Internal.PluginManager";
    private const string DownloadMethodName = "DownloadPluginAsync";

    public static MarketplaceOperation? Classify(IEnumerable<StackFrameInfo> frames, InterceptionScope scope)
    {
        foreach (var frame in frames)
        {
            if (scope.PluginDownloads && IsPluginDownload(frame))
                return MarketplaceOperation.PluginDownload;

            if (scope.RepositoryReads && IsRepositoryRead(frame))
                return MarketplaceOperation.RepositoryMaster;
        }

        return null;
    }

    public static bool IsRepositoryRead(StackFrameInfo frame)
        => frame.TypeName?.StartsWith(RepositoryTypePrefix, StringComparison.Ordinal) == true;

    public static bool IsPluginDownload(StackFrameInfo frame)
    {
        if (frame.TypeName?.StartsWith(PluginManagerTypePrefix, StringComparison.Ordinal) != true)
            return false;

        return frame.TypeName.Contains(DownloadMethodName, StringComparison.Ordinal)
            || frame.MethodName?.Contains(DownloadMethodName, StringComparison.Ordinal) == true;
    }
}
