using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using DalamudProxy.Core;

namespace DalamudProxy.Tests;

public sealed class FakeHttpProxy : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task acceptTask;
    private readonly TaskCompletionSource<string> requestLine = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeHttpProxy()
    {
        this.listener = new TcpListener(IPAddress.Loopback, 0);
        this.listener.Start();
        this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
        this.acceptTask = this.AcceptAsync();
    }

    public int Port { get; }

    public Task<string> RequestLine => this.requestLine.Task;

    private async Task AcceptAsync()
    {
        try
        {
            using var client = await this.listener.AcceptTcpClientAsync(this.cancellation.Token);
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            using var memory = new MemoryStream();
            while (memory.Length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, this.cancellation.Token);
                if (read == 0)
                    break;

                memory.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(memory.ToArray());
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var firstLine = text.Split("\r\n", StringSplitOptions.None)[0];
                    this.requestLine.TrySetResult(firstLine);
                    break;
                }
            }

            const string response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), this.cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            this.requestLine.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.cancellation.Cancel();
        this.listener.Stop();
        try
        {
            await this.acceptTask;
        }
        catch
        {
        }

        this.cancellation.Dispose();
    }
}

public sealed class ProxyTransportIntegrationTests
{
    [Fact]
    public async Task SendAsync_UsesConfiguredHttpProxy()
    {
        await using var proxy = new FakeHttpProxy();
        var settings = new ProxySettings(
            true,
            $"http://127.0.0.1:{proxy.Port}",
            null,
            null,
            "https://github.com/");
        using var transport = new ProxyTransport(settings, "15.0.3.9");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://plugin-master.invalid/repo.json");

        using var response = await transport.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OK", await response.Content.ReadAsStringAsync());
        var line = await proxy.RequestLine.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("GET http://plugin-master.invalid/repo.json HTTP/1.1", line);
    }
}
