using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.AccessLog;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// The HTTP dispatcher's access records name the protocol that owns the dispatched method, and
/// carry that protocol's digest.
/// </summary>
/// <remarks>
/// <para>
/// The same rule the serve loop follows (see <c>PerBindingIdentityTests</c> in the core suite),
/// asserted separately because HTTP dispatches outside that loop and builds its own record —
/// which is exactly how a port comes to have one transport right and the other wrong. The
/// canonical Python implementation had seven emit sites across two HTTP dispatchers, a stream
/// resource and three raw-transport paths; a missed one reintroduces this silently.
/// </para>
/// <para>
/// Only a call to a <em>secondary</em> protocol can catch it: for an application method the
/// primary <em>is</em> the owning binding, so a server that labels everything with its primary
/// passes every other assertion in this file.
/// </para>
/// </remarks>
public sealed class AccessLogIdentityTests
{
    private const string AppProtocol = "Echo";
    private const string ArrowContentType = "application/vnd.apache.arrow.stream";

    public interface IEcho
    {
        Task<string> EchoAsync(string value);
    }

    private sealed class Echo : IEcho
    {
        public Task<string> EchoAsync(string value) => Task.FromResult(value);
    }

    private sealed class CapturingSink : IAccessLogSink
    {
        public ConcurrentQueue<AccessLogRecord> Records { get; } = new();

        public bool IncludeRequestData => false;

        public void Write(AccessLogRecord record) => Records.Enqueue(record);
    }

    /// <summary>A reflection call over HTTP is filed under reflection, with reflection's digest.</summary>
    [Fact]
    public async Task AReflectionCallCarriesReflectionsOwnBinding()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(
            http,
            $"/{ReflectionProtocol.ProtocolName}/{ReflectionProtocol.ListProtocolsMethod}",
            ReflectionProtocol.ListProtocolsMethod,
            ReflectionProtocol.ProtocolName);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = Single(host, ReflectionProtocol.ListProtocolsMethod);
        Assert.Equal(ReflectionProtocol.ProtocolName, record.Protocol);
        Assert.Equal(host.Server.ProtocolHashFor(ReflectionProtocol.ProtocolName), record.ProtocolHash);
        Assert.NotEqual(host.Server.ProtocolHash, record.ProtocolHash);
    }

    /// <summary>An identity call over HTTP is filed under identity.</summary>
    [Fact]
    public async Task AnIdentityCallCarriesIdentitysOwnBinding()
    {
        await using var host = await StartHostAsync(identity: new IdentityImpl(
            mintGrant: (principal, _, _, ttl) => new IssuedGrant(
                $"grant-for-{principal}", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl, "g1")));
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        // Refused -- the request carries no authenticated principal, so the freshness guard
        // fires. Which is the interesting case: a refusal is exactly the record an auditor of
        // credential issuance reads, so it has to be filed under the protocol that refused.
        using var response = await PostAsync(
            http, $"/{IdentityProtocol.ProtocolName}/{IdentityProtocol.IssueGrantMethod}",
            IdentityProtocol.IssueGrantMethod, IdentityProtocol.ProtocolName);

        var record = Single(host, IdentityProtocol.IssueGrantMethod);
        Assert.Equal("error", record.Status);
        Assert.Equal(IdentityProtocol.ProtocolName, record.Protocol);
        Assert.Equal(host.Server.ProtocolHashFor(IdentityProtocol.ProtocolName), record.ProtocolHash);
        Assert.NotEqual(host.Server.ProtocolHash, record.ProtocolHash);
    }

    /// <summary>
    /// An application call carries the application binding's <em>canonical</em> digest — the one
    /// reflection reports for it, and the one every other port computes.
    /// </summary>
    /// <remarks>
    /// This port used to log a port-local digest for the primary while reflection reported the
    /// canonical one. Both are 64 lowercase hex characters, so the divergence passed every
    /// format check; a consumer keying its registry on <c>protocol_hash</c> simply found no
    /// entry, because a registry is built from what <c>describe</c> reports.
    /// </remarks>
    [Fact]
    public async Task AnApplicationCallCarriesTheCanonicalPrimaryDigest()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, $"/{AppProtocol}/echo", "echo", AppProtocol);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = Single(host, "echo");
        Assert.Equal(AppProtocol, record.Protocol);
        Assert.Equal(
            ReflectionProtocol.BindingHash(AppProtocol, host.Server.MethodsForProtocol(AppProtocol)!),
            record.ProtocolHash);
    }

    /// <summary>
    /// A request naming a protocol this server does not host is logged against the primary — and
    /// with the primary's digest, so the pair still agrees.
    /// </summary>
    /// <remarks>
    /// The record deliberately does not echo the requested name: a message goes to one caller
    /// while a log field is shared, retained and assumed to be of bounded cardinality. This
    /// pins the fallback's <em>pair</em>, which is the half that can silently rot — the name
    /// falling back while the hash does not, or the reverse.
    /// </remarks>
    [Fact]
    public async Task AnUnhostedProtocolFallsBackToThePrimaryOnBothFields()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, "/some.Other.v1/echo", "echo", "some.Other.v1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var record = Single(host, "echo");
        Assert.Equal(AppProtocol, record.Protocol);
        Assert.Equal(host.Server.ProtocolHash, record.ProtocolHash);
    }

    /// <summary>
    /// A stale <c>__describe__</c> caller is told where introspection went, on this transport too.
    /// </summary>
    [Fact]
    public async Task DescribeIsRefusedWithItsReplacement()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, $"/{AppProtocol}/__describe__", "__describe__", AppProtocol);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var message = await MessageAsync(response);
        Assert.Contains("retired", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ReflectionProtocol.ProtocolName, message, StringComparison.Ordinal);
        Assert.Contains(ReflectionProtocol.ListProtocolsMethod, message, StringComparison.Ordinal);
        Assert.Contains(ReflectionProtocol.DescribeMethod, message, StringComparison.Ordinal);
    }

    /// <summary>Every other unknown method keeps the plain capability answer.</summary>
    [Fact]
    public async Task AnotherUnknownMethodKeepsTheGenericAnswer()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, $"/{AppProtocol}/__not_a_thing__", "__not_a_thing__", AppProtocol);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("retired", await MessageAsync(response), StringComparison.OrdinalIgnoreCase);
    }

    private static AccessLogRecord Single(TestHost host, string method)
    {
        var matching = host.Sink.Records.Where(r => r.Method == method).ToList();
        Assert.Single(matching);
        return matching[0];
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var stream = new MemoryStream(bytes);
        using var reader = new WireReader(stream);
        await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
        var batch = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(batch);
        using (batch!.Batch)
        {
            return batch.GetMetadata(MetadataKeys.LogMessage) ?? "";
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(
        System.Net.Http.HttpClient http, string path, string method, string? declaredProtocol)
    {
        var schema = new Schema([new Field("value", StringType.Default, false)], null);
        var metadata = new Dictionary<string, string>
        {
            [MetadataKeys.Method] = method,
            [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
        };
        if (declaredProtocol is not null)
        {
            metadata[MetadataKeys.Protocol] = declaredProtocol;
        }

        using var buffer = new MemoryStream();
        await using (var writer = new WireWriter(buffer, schema))
        {
            using var batch = new RecordBatch(schema, [new StringArray.Builder().Append("hi").Build()], 1);
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, metadata));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(http.BaseAddress!, path))
        {
            Content = new ByteArrayContent(buffer.ToArray()),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ArrowContentType);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<TestHost> StartHostAsync(IdentityImpl? identity = null)
    {
        var sink = new CapturingSink();
        var server = new RpcServer(typeof(IEcho), new Echo(), accessLog: sink, identity: identity);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(server);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHost(app, new Uri(address), server, sink);
    }

    private sealed class TestHost(WebApplication app, Uri address, RpcServer server, CapturingSink sink)
        : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public RpcServer Server { get; } = server;

        public CapturingSink Sink { get; } = sink;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
