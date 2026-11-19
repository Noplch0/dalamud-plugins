using DalamudProxy.Core;

namespace DalamudProxy.Tests;

public sealed class SecretAndLogTests
{
    [Fact]
    public void Dpapi_RoundTripsPassword()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var encrypted = DpapiSecretStore.Protect("s3cret!中文");
        Assert.NotEqual("s3cret!中文", encrypted);
        Assert.Equal("s3cret!中文", DpapiSecretStore.Unprotect(encrypted));
    }

    [Fact]
    public void RequestLog_KeepsMostRecentEntries()
    {
        var log = new RequestLog();
        for (var i = 0; i < 250; i++)
        {
            log.Add(new RequestLogEntry(
                DateTimeOffset.UnixEpoch.AddSeconds(i),
                MarketplaceOperation.PluginDownload,
                "example.com",
                $"https://example.com/{i}",
                true,
                200,
                i,
                "ok"));
        }

        var entries = log.Snapshot();
        Assert.Equal(200, entries.Count);
        Assert.Equal(50, entries[0].ElapsedMilliseconds);
        Assert.Equal(249, entries[^1].ElapsedMilliseconds);
    }
}
