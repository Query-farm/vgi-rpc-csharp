using System.Text.Json;
using Apache.Arrow;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Client;

public sealed class IrohEndpointTests
{
    private const string Id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ParsesCanonicalRawAndHttpEndpoints()
    {
        var raw = IrohEndpoint.Parse($"iroh://{Id}");
        Assert.Equal("iroh", raw.Scheme);
        Assert.Equal(32, raw.EndpointIdBytes.Length);
        Assert.Equal(IrohEndpoint.ArrowMuxAlpn, raw.Alpn);

        var http = IrohEndpoint.Parse($"httpi://{Id}/api/v1");
        Assert.Equal("/api/v1", http.BasePath);
        Assert.Equal(IrohEndpoint.HttpAlpn, http.Alpn);
    }

    [Theory]
    [InlineData("iroh://0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("iroh://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/")]
    [InlineData("iroh://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef:443")]
    [InlineData("httpi://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/a//b")]
    [InlineData("httpi://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/a/../b")]
    [InlineData("httpi://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef?x=1")]
    public void RejectsNonCanonicalEndpoints(string value) => Assert.Throws<IrohUriException>(() => IrohEndpoint.Parse(value));

    [Fact]
    public void PassesCanonicalFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "iroh_transport_vectors.json")));
        var root = document.RootElement;
        Assert.Equal(IrohEndpoint.ArrowMuxAlpn, root.GetProperty("alpns").GetProperty("iroh").GetString());
        Assert.Equal(IrohEndpoint.HttpAlpn, root.GetProperty("alpns").GetProperty("httpi").GetString());
        foreach (var vector in root.GetProperty("uri_cases").EnumerateArray())
        {
            var uri = vector.GetProperty("uri").GetString()!;
            if (!vector.GetProperty("valid").GetBoolean())
            {
                var error = Assert.Throws<IrohUriException>(() => IrohEndpoint.Parse(uri));
                Assert.Equal(IrohErrorStage.Parse, error.Stage);
                Assert.Equal(IrohErrorCategory.InvalidInput, error.Category);
                Assert.Equal(IrohDispatchCertainty.NotSent, error.DispatchCertainty);
                continue;
            }
            var endpoint = IrohEndpoint.Parse(uri);
            Assert.Equal(vector.GetProperty("scheme").GetString(), endpoint.Scheme);
            Assert.Equal(vector.GetProperty("base_path").GetString(), endpoint.BasePath);
        }
        foreach (var vector in root.GetProperty("error_cases").EnumerateArray())
        {
            Assert.True(Enum.TryParse<IrohErrorStage>(SnakeToPascal(vector.GetProperty("stage").GetString()!), out _));
            Assert.True(Enum.TryParse<IrohErrorCategory>(SnakeToPascal(vector.GetProperty("category").GetString()!), out _));
            Assert.True(Enum.TryParse<IrohDispatchCertainty>(SnakeToPascal(
                vector.GetProperty("dispatch_certainty").GetString()!), out _));
        }
    }

    private static string SnakeToPascal(string value) => string.Concat(value.Split('_')
        .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    [Fact]
    public async Task DispatchesRawEndpointThroughExplicitProvider()
    {
        var transport = new FakeTransport();
        var provider = new FakeProvider(transport);
        await using var client = await RpcClient.ConnectIrohAsync($"iroh://{Id}", provider);
        Assert.Same(transport, client.Transport);
        Assert.Equal(IrohEndpoint.ArrowMuxAlpn, provider.Endpoint!.Alpn);
        var unsupported = await Assert.ThrowsAsync<IrohTransportException>(() =>
            RpcClient.ConnectIrohAsync($"httpi://{Id}", provider));
        Assert.Equal(IrohErrorCategory.Unsupported, unsupported.Category);
        Assert.Equal(IrohDispatchCertainty.NotSent, unsupported.DispatchCertainty);
    }

    [Fact]
    public void NativeProviderHasANonThrowingCapabilityProbe()
    {
        var available = NativeIrohTransportProvider.IsAvailable();
        if (Environment.GetEnvironmentVariable("VGI_RPC_EXPECT_IROH_NATIVE") == "1") Assert.True(available);
    }

    [Fact]
    public async Task NativeCAbiConnectsToLiveArrowMuxWorkerWhenConfigured()
    {
        var endpoint = Environment.GetEnvironmentVariable("VGI_RPC_IROH_TEST_ENDPOINT");
        if (endpoint is null) return;
        Assert.True(NativeIrohTransportProvider.IsAvailable());
        var localId = NativeIrohTransportProvider.Shared.GetLocalEndpointId();
        Assert.Equal(localId, NativeIrohTransportProvider.Shared.GetLocalEndpointId(
            new IrohConnectOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(7),
                IoTimeout = TimeSpan.FromSeconds(19),
                NoRelay = true,
            }));
        var configuredSecret = Enumerable.Repeat((byte)7, 32).ToArray();
        using var configuredProvider = new NativeIrohTransportProvider();
        var configuredId = configuredProvider.GetLocalEndpointId(
            new IrohConnectOptions { SecretKey = configuredSecret });
        Assert.NotEqual(localId, configuredId);
        Assert.Equal(configuredId, configuredProvider.GetLocalEndpointId(new IrohConnectOptions
        {
            SecretKey = configuredSecret,
            ConnectTimeout = TimeSpan.FromSeconds(11),
            NoRelay = true,
        }));
        System.Array.Clear(configuredSecret);
        using var parameters = new RecordBatch(new Schema([], null), [], 1);
        await using var client = await RpcClient.ConnectIrohAsync($"iroh://{endpoint}");
        var response = await client.CallUnaryAsync("__describe__", parameters);
        Assert.True(response.Batch.ColumnCount > 0);
        response.Batch.Dispose();
    }

    private sealed class FakeProvider(IRpcTransport transport) : IIrohTransportProvider
    {
        public IrohEndpoint? Endpoint { get; private set; }
        public ValueTask<IRpcTransport> OpenArrowMuxAsync(IrohEndpoint endpoint, IrohConnectOptions options,
            CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            return ValueTask.FromResult(transport);
        }
    }

    private sealed class FakeTransport : IRpcTransport
    {
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream();
    }
}
