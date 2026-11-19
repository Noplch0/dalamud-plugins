using System.Net;

namespace DalamudProxy.Core;

public static class UriUtilities
{
    private static readonly HashSet<string> SupportedProxySchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "socks4",
        "socks4a",
        "socks5",
    };

    public static bool TryNormalizeAbsolute(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
            return false;

        normalized = Normalize(uri);
        return normalized.Length > 0;
    }

    public static bool TryNormalize(string? value, Uri? baseUri, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (baseUri is null || !Uri.TryCreate(baseUri, value.Trim(), out uri)))
            return false;

        normalized = Normalize(uri);
        return normalized.Length > 0;
    }

    public static string Normalize(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            return string.Empty;

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost,
        };

        if (uri.IsDefaultPort)
            builder.Port = -1;

        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    public static bool TryCreateProxyEndpoint(string? url, string? username, string? password, out ProxyEndpoint endpoint, out string error)
    {
        endpoint = null!;
        error = string.Empty;

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            error = "代理地址不是有效的绝对 URI。";
            return false;
        }

        if (!SupportedProxySchemes.Contains(uri.Scheme))
        {
            error = "代理协议仅支持 http、https、socks4、socks4a、socks5。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535)
        {
            error = "代理地址必须包含主机和有效端口。";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            if (string.IsNullOrWhiteSpace(username))
                username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1 && string.IsNullOrEmpty(password))
                password = Uri.UnescapeDataString(parts[1]);

            var cleanBuilder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            uri = cleanBuilder.Uri;
        }

        ICredentials? credentials = string.IsNullOrWhiteSpace(username)
            ? null
            : new NetworkCredential(username, password ?? string.Empty);

        endpoint = new ProxyEndpoint(uri, credentials);
        return true;
    }

    public static string SanitizeForLog(Uri uri)
    {
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }
}

public sealed record ProxyEndpoint(Uri Uri, ICredentials? Credentials)
{
    public WebProxy CreateWebProxy() => new(Uri)
    {
        BypassProxyOnLocal = false,
        Credentials = Credentials,
    };
}

