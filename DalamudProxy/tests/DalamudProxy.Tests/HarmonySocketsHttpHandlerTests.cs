using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace DalamudProxy.Tests;

[CollectionDefinition("global-http-patch", DisableParallelization = true)]
public sealed class GlobalHttpPatchCollection
{
}

[Collection("global-http-patch")]
public sealed class HarmonySocketsHttpHandlerTests
{
    private const string HarmonyId = "DalamudProxy.Tests.SocketsHttpHandler";

    [Fact]
    public async Task PrefixCanReplaceSocketsHttpHandlerSendAsync()
    {
        var harmony = new Harmony(HarmonyId);
        var method = AccessTools.DeclaredMethod(
            typeof(SocketsHttpHandler),
            "SendAsync",
            new[] { typeof(HttpRequestMessage), typeof(CancellationToken) });
        Assert.NotNull(method);

        try
        {
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(HarmonySocketsHttpHandlerTests), nameof(Prefix)));

            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync("http://harmony.invalid/test");

            Assert.Equal((HttpStatusCode)299, response.StatusCode);
            Assert.Equal("patched", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            harmony.UnpatchAll(HarmonyId);
        }
    }

    public static bool Prefix(HttpRequestMessage request, ref Task<HttpResponseMessage> __result)
    {
        if (!string.Equals(request.RequestUri?.Host, "harmony.invalid", StringComparison.OrdinalIgnoreCase))
            return true;

        __result = Task.FromResult(new HttpResponseMessage((HttpStatusCode)299)
        {
            Content = new StringContent("patched"),
        });
        return false;
    }
}
