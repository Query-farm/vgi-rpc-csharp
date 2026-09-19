using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Server;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// Every header a browser client needs is named in <c>Access-Control-Expose-Headers</c> -- the
/// reference suite's <c>TestCors::test_advertised_capabilities_are_all_exposed</c> and
/// <c>test_failure_path_headers_are_exposed</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two gaps, both invisible to any client that is not a browser. The list itself omitted headers
/// the server advertises (<c>VGI-Max-Request-Bytes</c>, <c>VGI-Max-Upload-Bytes</c>, ...) and
/// the failure-path <c>X-Request-ID</c>. And the list was written only on actual responses: the
/// reference server writes it on the preflight too, which is where the reference suite reads it.
/// </para>
/// <para>
/// The advertised set is taken from the server's own <c>OPTIONS /health</c> rather than
/// hardcoded, as the reference does, so a capability header added later without exposing it
/// fails here.
/// </para>
/// </remarks>
public sealed class CorsExposureTests
{
    private const string Origin = "https://conformance.example";
    private const string Policy = "vgi-rpc-test";

    public interface IPingService
    {
        Task PingAsync();
    }

    private sealed class PingService : IPingService
    {
        public Task PingAsync() => Task.CompletedTask;
    }

    [Fact]
    public void ExposedHeaders_CoverTheCapabilityAndFailureHeaders()
    {
        var exposed = Cors.ExposedHeaders();

        foreach (var header in new[]
                 {
                     "VGI-Max-Request-Bytes", "VGI-Max-Upload-Bytes", "VGI-Max-Externalized-Response-Bytes",
                     "X-Request-ID", "VGI-Auth-Reason", RpcHttpEndpoints.RpcErrorHeader,
                     ProxyProof.ProofRequiredHeader, "VGI-Sticky-Enabled", "VGI-Session",
                 })
        {
            Assert.Contains(header, exposed);
        }
    }

    [Fact]
    public async Task Preflight_ExposesEveryAdvertisedCapabilityHeader()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/PingService/ping");
        preflight.Headers.Add("Origin", Origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var preflightResponse = await http.SendAsync(preflight, TestContext.Current.CancellationToken);
        Assert.True(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"), "the preflight was refused");
        var exposed = Exposed(preflightResponse);

        using var health = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/health"), TestContext.Current.CancellationToken);
        var advertised = health.Headers.Concat(health.Content.Headers)
            .Select(header => header.Key.ToLowerInvariant())
            .Where(name => name.StartsWith("vgi-", StringComparison.Ordinal) || name.StartsWith("x-vgi-", StringComparison.Ordinal))
            .ToHashSet();

        Assert.NotEmpty(advertised);
        Assert.Empty(advertised.Except(exposed));
        foreach (var failureHeader in new[] { "x-request-id", "vgi-auth-reason", "x-vgi-rpc-error" })
        {
            Assert.Contains(failureHeader, exposed);
        }
    }

    private static HashSet<string> Exposed(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Expose-Headers", out var values)
            ? values.SelectMany(value => value.Split(',')).Select(name => name.Trim().ToLowerInvariant()).Where(name => name.Length > 0).ToHashSet()
            : [];

    private static async Task<TestHost> StartHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVgiRpcCors(Policy, [Origin], maxResponseBytes: 65536);
        var app = builder.Build();
        app.UseCors();
        app.UseVgiRpcCorsExtras();
        app.MapVgiRpc(
            new RpcServer(typeof(IPingService), new PingService()),
            maxResponseBytes: 65536,
            corsPolicyName: Policy,
            externalization: new ExternalizationOptions { MaxRequestBytes = 1 << 20, MaxUploadBytes = 1 << 20 });
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
