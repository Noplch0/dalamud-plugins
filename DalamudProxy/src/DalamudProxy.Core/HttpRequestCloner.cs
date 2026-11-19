using System.Net.Http;

namespace DalamudProxy.Core;

public static class HttpRequestCloner
{
    public static HttpRequestMessage Clone(HttpRequestMessage original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.RequestUri is null)
            throw new ArgumentException("请求 URI 不能为空。", nameof(original));
        if (original.Content is not null)
            throw new NotSupportedException("插件市场代理只支持无正文的 GET 请求。");

        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in original.Options)
        {
            clone.Options.Set(
                new HttpRequestOptionsKey<object?>(option.Key),
                option.Value);
        }

        return clone;
    }
}
