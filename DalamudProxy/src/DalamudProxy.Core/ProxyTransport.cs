using System.Net;
using System.Net.Http.Headers;

namespace DalamudProxy.Core;

public sealed class ProxyTransport : IDisposable
{
    private readonly HttpClient client;
    private readonly ProxyEndpoint endpoint;
    private int references = 1;
    private int disposed;

    public ProxyTransport(ProxySettings settings, string? userAgentProductVersion = null)
    {
        if (!UriUtilities.TryCreateProxyEndpoint(
                settings.ProxyUrl,
                settings.Username,
                settings.Password,
                out var parsedEndpoint,
                out var error))
            throw new ArgumentException(error, nameof(settings));

        this.endpoint = parsedEndpoint;
        var handler = new SocketsHttpHandler
        {
            Proxy = this.endpoint.CreateWebProxy(),
            UseProxy = true,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        this.client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (!string.IsNullOrWhiteSpace(userAgentProductVersion))
        {
            this.client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Dalamud", userAgentProductVersion));
        }
    }

    public Uri ProxyUri => this.endpoint.Uri;

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        using var lease = this.Acquire();
        return await this.client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
    }

    public HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = this.Acquire();
        return this.client.Send(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public async Task<HttpResponseMessage> ProbeAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await this.SendAsync(request, timeoutSource.Token, HttpCompletionOption.ResponseContentRead)
            .ConfigureAwait(false);
    }

    public void Dispose() => this.ReleaseReference();

    private ProxyTransportLease Acquire()
    {
        while (true)
        {
            if (Volatile.Read(ref this.disposed) != 0)
                throw new ObjectDisposedException(nameof(ProxyTransport));

            var current = Volatile.Read(ref this.references);
            if (current <= 0)
                throw new ObjectDisposedException(nameof(ProxyTransport));

            if (Interlocked.CompareExchange(ref this.references, current + 1, current) == current)
                return new ProxyTransportLease(this);
        }
    }

    private void ReleaseReference()
    {
        if (Interlocked.Decrement(ref this.references) != 0)
            return;

        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.client.Dispose();
    }

    private sealed class ProxyTransportLease(ProxyTransport owner) : IDisposable
    {
        private ProxyTransport? owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref this.owner, null)?.ReleaseReference();
        }
    }
}
