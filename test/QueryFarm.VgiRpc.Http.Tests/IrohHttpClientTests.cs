using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class IrohHttpClientTests
{
    private const string Id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ConnectIrohCarriesTheExistingHttpClientOverHttpi()
    {
        var provider = new FakeProvider(Id);
        await using var client = HttpRpcClient.ConnectIroh($"httpi://{Id}/vgi",
            new IrohConnectOptions { RelayUrls = ["https://relay.example.test"] },
            new HttpRpcClientOptions { AcceptedMaxResponseBytes = 64L << 10 }, provider);

        var capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);

        Assert.True(capabilities.AcceptMaxResponseBytesSupport);
        Assert.Equal(64L << 10, capabilities.MaxResponseBytes);
        Assert.Equal("OPTIONS", provider.Request!.Method);
        Assert.Equal("/vgi/health", provider.Request.Path);
        Assert.Equal(256L << 20, provider.Request.MaxResponseBytes);
        Assert.Equal(64L << 10, provider.Request.MaxResponseHeaderBytes);
        Assert.Equal(["https://relay.example.test"], provider.Options!.RelayUrls);
        Assert.Contains(provider.Request.Headers, header =>
            header.Key == "VGI-Accept-Max-Response-Bytes" && header.Value == (64L << 10).ToString());
    }

    [Fact]
    public async Task HandlerRejectsAResponseFromTheWrongEndpoint()
    {
        var endpoint = IrohEndpoint.Parse($"httpi://{Id}");
        using var handler = new IrohHttpMessageHandler(endpoint, provider: new FakeProvider(new string('a', 64)));
        using var http = new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri($"httpi://{Id}/") };

        var error = await Assert.ThrowsAsync<IrohTransportException>(() =>
            http.GetAsync("health", TestContext.Current.CancellationToken));

        Assert.Equal(IrohErrorCategory.Authentication, error.Category);
        Assert.Equal(IrohDispatchCertainty.Sent, error.DispatchCertainty);
    }

    [Fact]
    public async Task NativeHttpiConnectsToLiveBridgeWhenConfigured()
    {
        var endpoint = Environment.GetEnvironmentVariable("VGI_RPC_HTTPI_TEST_ENDPOINT");
        if (endpoint is null) return;
        Assert.True(NativeIrohTransportProvider.IsAvailable());
        var directAddresses = (Environment.GetEnvironmentVariable("VGI_RPC_IROH_TEST_DIRECT_ADDRESSES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await using var client = HttpRpcClient.ConnectIroh($"httpi://{endpoint}",
            new IrohConnectOptions { NoRelay = true, DirectAddresses = directAddresses });

        var capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);

        Assert.True(capabilities.AcceptMaxResponseBytesSupport);
    }

    private sealed class FakeProvider(string remoteEndpointId) : IIrohHttpTransportProvider
    {
        public IrohHttpRequest? Request { get; private set; }
        public IrohConnectOptions? Options { get; private set; }

        public ValueTask<IrohHttpResponse> SendHttpAsync(IrohEndpoint endpoint, IrohHttpRequest request,
            IrohConnectOptions options, CancellationToken cancellationToken = default)
        {
            Request = request;
            Options = options;
            IReadOnlyList<KeyValuePair<string, string>> headers =
            [
                new("VGI-Accept-Max-Response-Bytes-Support", "true"),
                new("VGI-Max-Response-Bytes", (64L << 10).ToString()),
            ];
            return ValueTask.FromResult(new IrohHttpResponse(
                204, headers, new MemoryStream(), remoteEndpointId));
        }
    }
}
