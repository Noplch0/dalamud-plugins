using DalamudProxy.Core;

namespace DalamudProxy.Tests;

public sealed class UriUtilitiesTests
{
    [Theory]
    [InlineData("HTTPS://Example.COM:443/a/b?q=1#frag", "https://example.com/a/b?q=1")]
    [InlineData("http://example.com:80", "http://example.com/")]
    [InlineData("http://example.com:8080/path", "http://example.com:8080/path")]
    public void Normalize_ProducesStableUrl(string input, string expected)
    {
        Assert.True(UriUtilities.TryNormalizeAbsolute(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7890")]
    [InlineData("https://proxy.example:443")]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("socks4a://127.0.0.1:1080")]
    public void ProxyParser_AcceptsSupportedSchemes(string value)
    {
        Assert.True(UriUtilities.TryCreateProxyEndpoint(value, null, null, out var endpoint, out var error), error);
        Assert.Equal(value.Split(':')[0], endpoint.Uri.Scheme);
    }

    [Fact]
    public void ProxyParser_ExtractsAndStripsEmbeddedCredentials()
    {
        Assert.True(
            UriUtilities.TryCreateProxyEndpoint(
                "socks5://alice:secret@127.0.0.1:1080",
                null,
                null,
                out var endpoint,
                out var error),
            error);

        Assert.Empty(endpoint.Uri.UserInfo);
        Assert.NotNull(endpoint.Credentials);
        Assert.Equal("socks5://127.0.0.1:1080/", endpoint.Uri.AbsoluteUri);
    }
    [Theory]
    [InlineData("")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("http:///missing-host")]
    [InlineData("not-a-uri")]
    public void ProxyParser_RejectsInvalidValues(string value)
    {
        Assert.False(UriUtilities.TryCreateProxyEndpoint(value, null, null, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void SanitizeForLog_DropsQueryAndUserInfo()
    {
        var uri = new Uri("https://user:pass@example.com:8443/a/b?token=secret#fragment");
        Assert.Equal("https://example.com:8443/a/b", UriUtilities.SanitizeForLog(uri));
    }
}

