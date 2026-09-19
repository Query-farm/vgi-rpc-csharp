using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Apache.Arrow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// A require-mode proxy-proof worker's 401 says that a proxy is involved -- the reference suite's
/// <c>TestUnauthorized::test_proxy_note_when_proxy_required</c>.
/// </summary>
/// <remarks>
/// The reference derives its proxy note from the proof header whenever proxy proof is required.
/// This port only ever used an operator-supplied hint, so a proof-gated worker configured the
/// obvious way refused an unproxied request with a bare <c>proxy_required</c>: no
/// <c>VGI-Auth-Proxy-Required</c> header and no <c>proxy_hint</c>, exactly the "is it the
/// credential or the proxy?" ambiguity the note exists to remove.
/// </remarks>
public sealed class ProxyProofUnauthorizedTests
{
    public interface IPingService
    {
        Task PingAsync();
    }

    private sealed class PingService : IPingService
    {
        public Task PingAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task RequireModeRefusal_CarriesTheProxyNote()
    {
        await using var host = await StartHostAsync(proxyHint: null);
        using var response = await PostPingAsync(host.Address);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("proxy_required", Header(response, "VGI-Auth-Reason"));
        Assert.Equal("true", Header(response, "VGI-Auth-Proxy-Required"));
        var hint = await ProxyHintAsync(response);
        Assert.Contains(ProxyProof.ProofHeader, hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorHint_StillWins()
    {
        await using var host = await StartHostAsync(proxyHint: "Route through the edge.");
        using var response = await PostPingAsync(host.Address);

        Assert.Equal("true", Header(response, "VGI-Auth-Proxy-Required"));
        Assert.Equal("Route through the edge.", await ProxyHintAsync(response));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static async Task<string?> ProxyHintAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.TryGetProperty("proxy_hint", out var hint) ? hint.GetString() : null;
    }

    private static async Task<HttpResponseMessage> PostPingAsync(Uri address)
    {
        var protocol = WireNaming.ForProtocol(typeof(IPingService));
        using var buffer = new MemoryStream();
        var schema = new Schema([], null);
        await using (var writer = new WireWriter(buffer, schema))
        {
            using var batch = new RecordBatch(schema, [], 1);
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, new Dictionary<string, string>
            {
                [MetadataKeys.Method] = "ping",
                [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
                [MetadataKeys.Protocol] = protocol,
            }));
        }

        using var http = new System.Net.Http.HttpClient { BaseAddress = address };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{protocol}/ping")
        {
            Content = new ByteArrayContent(buffer.ToArray()),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/vnd.apache.arrow.stream");
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<TestHost> StartHostAsync(string? proxyHint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var gate = ProxyProof.CreateGate(new ProxyProofConfig(
            ProxyProofMode.Require,
            "conformance-origin",
            new Dictionary<string, ProxyProofSecret> { ["kid"] = new(Enumerable.Repeat((byte)0x11, 32).ToArray(), "kid") },
            30,
            enableReplayCache: true));
        app.MapVgiRpc(
            new RpcServer(typeof(IPingService), new PingService()),
            authenticate: ProxyProof.RequireAll(gate, null),
            proxyHint: proxyHint,
            proxyProofRequired: true);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHost(app, new Uri(address));
    }

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
