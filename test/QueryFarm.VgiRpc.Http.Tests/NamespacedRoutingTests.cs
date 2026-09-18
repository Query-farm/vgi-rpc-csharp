using System.Net;
using System.Net.Http.Headers;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// The HTTP transport's protocol routing: <c>POST {prefix}/{protocol}/{method}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Before these routes existed the HTTP dispatcher routed on <c>{method}</c> alone against the
/// application protocol's method table, so the two co-hosted framework protocols were reachable
/// on every transport except the one where they matter most. <c>vgi_rpc.Identity.v1</c> makes
/// that concrete: its freshness guard requires an <c>auth_time</c> claim, which arrives only on
/// an OIDC/JWT credential — i.e. over HTTP — so <c>issue_grant</c> was reachable only on the
/// transports where it must refuse by design. <see cref="IssueGrant_SucceedsOverHttp"/> is the
/// test that closes that loop.
/// </para>
/// <para>
/// The rest is the routing contract of WIRE_PROTOCOL.md §3.1: metadata is canonical, the path is
/// a required faithful projection of it, disagreement is refused, a percent sign in the protocol
/// segment is refused without being decoded, and an unhosted protocol is 404.
/// </para>
/// </remarks>
public sealed class NamespacedRoutingTests
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

    // ---------------------------------------------------------------- routing, all three

    [Fact]
    public async Task ApplicationProtocol_RoutesUnderItsOwnNamespace()
    {
        await using var host = await StartHostAsync();
        await using var client = Client(host, AppProtocol);

        using var response = (await client.CallUnaryAsync(
            "echo", StringRow("value", "hi"), cancellationToken: TestContext.Current.CancellationToken)).Batch;

        Assert.Equal("hi", ((StringArray)response.Column(0)).GetString(0));
    }

    /// <summary>Reflection is reachable over HTTP, and lists every protocol this worker hosts.</summary>
    /// <remarks>
    /// Introspection is not a special path: it is <c>{prefix}/vgi_rpc.Reflection.v1/list_protocols</c>,
    /// reached exactly the way an application method is.
    /// </remarks>
    [Fact]
    public async Task Reflection_IsReachableOverHttpAndDescribesEveryHostedProtocol()
    {
        await using var host = await StartHostAsync(identity: FullIdentity());
        await using var client = Client(host, ReflectionProtocol.ProtocolName);

        using var listing = (await client.CallUnaryAsync(
            ReflectionProtocol.ListProtocolsMethod,
            new RecordBatch(new Schema([], null), [], 1),
            cancellationToken: TestContext.Current.CancellationToken)).Batch;

        // The framework's convention for a structured return: serialized bytes in one `result`
        // binary column. Asserting the names are in there is enough — the payload's own shape is
        // covered by the reflection tests.
        Assert.Equal("result", listing.Schema.FieldsList[0].Name);
        var payload = System.Text.Encoding.UTF8.GetString(((BinaryArray)listing.Column(0)).GetBytes(0));
        Assert.Contains(AppProtocol, payload, StringComparison.Ordinal);
        Assert.Contains(ReflectionProtocol.ProtocolName, payload, StringComparison.Ordinal);
        Assert.Contains(IdentityProtocol.ProtocolName, payload, StringComparison.Ordinal);

        using var described = (await client.CallUnaryAsync(
            ReflectionProtocol.DescribeMethod,
            StringRow("protocol", AppProtocol),
            cancellationToken: TestContext.Current.CancellationToken)).Batch;

        Assert.Contains(
            "echo",
            System.Text.Encoding.UTF8.GetString(((BinaryArray)described.Column(0)).GetBytes(0)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>issue_grant</c> succeeds over HTTP when the request's authentication carries an
    /// <c>auth_time</c> — the one transport where it can.
    /// </summary>
    [Fact]
    public async Task IssueGrant_SucceedsOverHttp()
    {
        await using var host = await StartHostAsync(
            identity: FullIdentity(),
            authenticate: RecentlyAuthenticated("alice", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var client = Client(host, IdentityProtocol.ProtocolName);

        var info = ServiceRegistry.GetMethods(typeof(IIdentityProtocol))[IdentityProtocol.IssueGrantMethod];
        using var parameters = ValueCodec.BuildRow(
            info.ParamsSchema, ["reports", new List<string> { "read" }, 3600L]);

        var response = await client.CallUnaryAsync(
            IdentityProtocol.IssueGrantMethod, parameters,
            cancellationToken: TestContext.Current.CancellationToken);
        using var batch = response.Batch;

        Assert.Null(response.GetMetadata(MetadataKeys.LogLevel));
        var grant = (IssuedGrant)ValueCodec.ExtractRow(batch, [typeof(IssuedGrant)])[0]!;

        // The subject is the caller, never a parameter.
        Assert.Equal("grant-for-alice", grant.Token);
    }

    /// <summary>The same call over HTTP without an <c>auth_time</c> still refuses — the route is
    /// what changed, not the guard.</summary>
    [Fact]
    public async Task IssueGrant_StillRefusesWithoutAuthTime()
    {
        await using var host = await StartHostAsync(
            identity: FullIdentity(),
            authenticate: RecentlyAuthenticated("alice", authTime: null));
        await using var client = Client(host, IdentityProtocol.ProtocolName);

        var info = ServiceRegistry.GetMethods(typeof(IIdentityProtocol))[IdentityProtocol.IssueGrantMethod];
        using var parameters = ValueCodec.BuildRow(
            info.ParamsSchema, ["reports", new List<string> { "read" }, 3600L]);

        var error = await Assert.ThrowsAsync<RpcException>(() => client.CallUnaryAsync(
            IdentityProtocol.IssueGrantMethod, parameters,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MetadataKeys.ErrorKinds.StaleAuth, error.ErrorKind);
    }

    /// <summary>Introspection's allowlist is enforced over HTTP exactly as on any transport.</summary>
    [Fact]
    public async Task IntrospectToken_IsReachableOverHttpAndKeepsItsAllowlist()
    {
        await using var host = await StartHostAsync(
            identity: FullIdentity(),
            authenticate: RecentlyAuthenticated("proxy", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var client = Client(host, IdentityProtocol.ProtocolName);

        var response = await client.CallUnaryAsync(
            IdentityProtocol.IntrospectTokenMethod, StringRow("token", "good"),
            cancellationToken: TestContext.Current.CancellationToken);
        using var batch = response.Batch;
        var identity = (TokenIdentity)ValueCodec.ExtractRow(batch, [typeof(TokenIdentity)])[0]!;

        Assert.Equal("bob", identity.Principal);
    }

    [Fact]
    public async Task IntrospectToken_RefusesACallerOutsideTheAllowlist()
    {
        await using var host = await StartHostAsync(
            identity: FullIdentity(),
            authenticate: RecentlyAuthenticated("mallory", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var client = Client(host, IdentityProtocol.ProtocolName);

        var error = await Assert.ThrowsAsync<RpcException>(() => client.CallUnaryAsync(
            IdentityProtocol.IntrospectTokenMethod, StringRow("token", "good"),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MetadataKeys.ErrorKinds.IntrospectionRefused, error.ErrorKind);
    }

    // ---------------------------------------------------------------- routing refusals

    /// <summary>An unhosted protocol is 404 — the same answer as an unknown method, and the one
    /// every proxy, WAF and load balancer understands without an Arrow parser.</summary>
    [Fact]
    public async Task UnknownProtocol_Is404()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, "/vgi_rpc.Identity.v1/issue_grant", "issue_grant", "vgi_rpc.Identity.v1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MetadataKeys.ErrorKinds.ProtocolNotSupported, await ErrorKindAsync(response));
    }

    /// <summary>A hosted protocol that lacks the method is a <em>different</em> 404: a client
    /// probing for an optional method has to tell the two apart.</summary>
    [Fact]
    public async Task HostedProtocolWithoutTheMethod_Is404WithADifferentKind()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, $"/{AppProtocol}/nope", "nope", AppProtocol);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MetadataKeys.ErrorKinds.MethodNotImplemented, await ErrorKindAsync(response));
    }

    /// <summary>The path and the metadata must name the same protocol.</summary>
    /// <remarks>
    /// Unspecified, this is the Content-Length/Transfer-Encoding shape: the edge applies policy to
    /// one protocol while the worker dispatches another.
    /// </remarks>
    [Fact]
    public async Task PathAndMetadataDisagreement_IsRejected()
    {
        await using var host = await StartHostAsync(identity: FullIdentity());
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(
            http, $"/{AppProtocol}/echo", "echo", declaredProtocol: ReflectionProtocol.ProtocolName);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MetadataKeys.ErrorKinds.ProtocolNotSupported, await ErrorKindAsync(response));
    }

    /// <summary>A request carrying no routing key at all is routed on the path.</summary>
    /// <remarks>
    /// <para>
    /// Pinned as a behaviour, not an omission. The shared cross-language conformance harness
    /// requires it — <c>_adversarial_http.py</c>'s recovery probe expects 200 for exactly this
    /// request — and the reference server's <c>check_protocol_agreement</c> is documented as a
    /// no-op when the key is absent.
    /// </para>
    /// <para>
    /// What it gives up: an intermediary that rewrites the <i>path</i> cannot reach inside the
    /// Arrow body to match it, so a present key is what would make such a rewrite detectable.
    /// Without one, this request is routed on the projection alone. The requirement belongs on
    /// the raw transports, where metadata is the only carrier; here the path already resolved the
    /// binding.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AbsentProtocolMetadata_IsRoutedOnThePath()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, $"/{AppProtocol}/echo", "echo", declaredProtocol: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(RpcHttpEndpoints.RpcErrorHeader));
    }

    /// <summary>A percent sign in the protocol segment is refused without being decoded.</summary>
    /// <remarks>
    /// <c>%45cho</c> decodes to the hosted protocol's own name, so a dispatcher that compared the
    /// decoded value would route this happily while the edge in front of it saw a different
    /// string. The name charset never requires encoding, so there is nothing to lose by refusing.
    /// </remarks>
    [Theory]
    [InlineData("/%45cho/echo")]
    [InlineData("/Ech%6f/echo")]
    [InlineData("/%45cho/echo/init")]
    public async Task PercentInTheProtocolSegment_IsRejectedWithoutDecoding(string path)
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, path, "echo", AppProtocol);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MetadataKeys.ErrorKinds.ProtocolNotSpecified, await ErrorKindAsync(response));
    }

    /// <summary>A percent sign anywhere else in the path is not this rule's business.</summary>
    [Fact]
    public async Task PercentInTheMethodSegment_IsNotRejectedByTheProtocolRule()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        // %65cho decodes to "echo" — the method half is ordinary path decoding, and the answer
        // is an ordinary successful dispatch rather than the routing refusal above.
        using var response = await PostAsync(http, $"/{AppProtocol}/%65cho", "echo", AppProtocol);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A path segment that cannot be a protocol name is refused before it is looked up,
    /// so a request-supplied string never reaches an error message or a log field.</summary>
    [Fact]
    public async Task AProtocolSegmentThatIsNotAName_Is404()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(http, "/9not-a-name/echo", "echo", AppProtocol);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MetadataKeys.ErrorKinds.ProtocolNotSupported, await ErrorKindAsync(response));
    }

    /// <summary>Framework protocols are unary-only: a stream endpoint on one is the wrong
    /// endpoint for an existing method, not a missing method.</summary>
    [Fact]
    public async Task StreamInitOnAFrameworkProtocol_IsRefused()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var response = await PostAsync(
            http,
            $"/{ReflectionProtocol.ProtocolName}/{ReflectionProtocol.ListProtocolsMethod}/init",
            ReflectionProtocol.ListProtocolsMethod,
            ReflectionProtocol.ProtocolName);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A continuation token minted under one protocol cannot be opened under another.
    /// </summary>
    /// <remarks>
    /// This is how the <c>/exchange</c> and cancel paths stay on the protocol their stream
    /// started on. The protocol is bound into the token's AEAD associated data rather than
    /// compared in the dispatcher, so a cross-protocol continuation fails the tag check and is
    /// refused exactly as an invalid token is — with no comparison to get wrong, and nothing to
    /// forget on a path that skipped it. Edge visibility on continuations matters more than on
    /// anything else: for a stream, most requests are continuations.
    /// </remarks>
    [Fact]
    public void AContinuationTokenIsBoundToItsProtocol()
    {
        var key = new byte[32];
        var identity = new AuthIdentity("oidc", "alice");
        var payload = new byte[] { 1, 2, 3, 4 };

        var token = Crypto.Seal(payload, key, StickySessions.ComputeCallAad(identity, AppProtocol));

        Assert.Equal(payload, Crypto.Open(token, key, StickySessions.ComputeCallAad(identity, AppProtocol)));
        Assert.ThrowsAny<Exception>(
            () => Crypto.Open(token, key, StickySessions.ComputeCallAad(identity, IdentityProtocol.ProtocolName)));
    }

    // ---------------------------------------------------------------- reserved endpoints

    /// <summary>The reserved framework endpoints stay flat — they belong to the server, not to
    /// any one protocol, and the namespaced route must not swallow them.</summary>
    [Fact]
    public async Task ReservedEndpointsAreNotNamespaced()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var health = await http.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var options = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/health"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
    }

    /// <summary>
    /// The pre-0.46 <c>POST {prefix}/__introspect_token__</c> JSON route is retired
    /// (IDENTITY_V1_SPEC §8), even on a worker that does introspect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two introspection surfaces means two sets of guards to keep identical, and the second had
    /// already drifted: it kept a rate limiter after the protocol dropped one. So the route is not
    /// merely disabled — the old "always mounted, answers <c>404 not_enabled</c>" shape is gone
    /// too. The request below is answered exactly as a path that was never routed is, which is
    /// what goes red if any of it is restored; and the capability header that advertised the
    /// route is not emitted (a client learns whether a worker introspects from reflection).
    /// </para>
    /// <para>
    /// The caller is an allowlisted introspector presenting a credential the worker resolves, so
    /// a route that still worked would have answered with the principal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheRetiredIntrospectionRouteIsNotServed()
    {
        await using var host = await StartHostAsync(
            identity: FullIdentity(),
            authenticate: RecentlyAuthenticated("proxy", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        async Task<(HttpStatusCode Status, string Body)> PostJsonAsync(string path)
        {
            using var content = new StringContent("{\"token\":\"good\"}", System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(path, content, TestContext.Current.CancellationToken);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var retired = await PostJsonAsync("/__introspect_token__");
        var neverRouted = await PostJsonAsync("/__no_such_route__");

        Assert.Equal(HttpStatusCode.NotFound, retired.Status);
        Assert.Equal(neverRouted, retired);
        Assert.DoesNotContain("bob", retired.Body, StringComparison.Ordinal);

        using var options = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/health"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        Assert.False(options.Headers.Contains("VGI-Token-Introspection"));
    }

    // ---------------------------------------------------------------- helpers

    private static IdentityImpl FullIdentity() => new(
        resolveToken: token => token == "good" ? new TokenIdentity("bob", "ci-key") : null,
        mintGrant: (principal, purpose, scopes, ttl) => new IssuedGrant(
            $"grant-for-{principal}", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl, "g1"),
        introspectPrincipals: ["proxy"]);

    /// <summary>
    /// An <c>authenticate</c> delegate that publishes a full <see cref="AuthContext"/>, claims
    /// included. <paramref name="authTime"/> is what an OIDC credential carries and a peer
    /// identity does not — the whole reason identity needs an HTTP route.
    /// </summary>
    private static RpcHttpEndpoints.AuthenticateDelegate RecentlyAuthenticated(string principal, long? authTime) =>
        context =>
        {
            var claims = new Dictionary<string, object?>();
            if (authTime is { } value)
            {
                claims["auth_time"] = (double)value;
            }

            PeerIdentityAuthentication.SetAuth(context, new AuthContext("oidc", true, principal, claims));
            return Task.CompletedTask;
        };

    private static HttpRpcClient Client(TestHost host, string protocol) =>
        new(host.Address, new HttpRpcClientOptions { Protocol = protocol });

    private static RecordBatch StringRow(string field, string value) =>
        new(new Schema([new Field(field, StringType.Default, false)], null),
            [new StringArray.Builder().Append(value).Build()],
            1);

    /// <summary>Posts a hand-built request so a test can pick the path and the routing metadata
    /// independently — which is the whole point of the disagreement cases.</summary>
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

        using var request = new HttpRequestMessage(HttpMethod.Post, RawUri(http.BaseAddress!, path))
        {
            Content = new ByteArrayContent(buffer.ToArray()),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ArrowContentType);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds a request URI that reaches the wire byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Do not simplify this back to a plain string path.</b> <see cref="Uri"/> canonicalises a
    /// percent-escaped <em>unreserved</em> character — <c>%45</c> to <c>E</c> — client-side, so
    /// the very requests the <c>%</c> rule exists to refuse never leave as written. Measured, not
    /// theorised: with an ordinary path string the three
    /// <see cref="PercentInTheProtocolSegment_IsRejectedWithoutDecoding"/> cases passed against a
    /// server whose check was never reached, because the escape was already gone by the time the
    /// bytes hit the socket. Reverting this makes those assertions vacuous rather than red.
    /// </para>
    /// <para>
    /// Any port whose HTTP client library normalises request targets has the same trap.
    /// </para>
    /// </remarks>
    private static Uri RawUri(Uri baseAddress, string path)
    {
        var options = new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true };
        Assert.True(Uri.TryCreate(baseAddress.GetLeftPart(UriPartial.Authority) + path, in options, out var uri));
        return uri!;
    }

    /// <summary>Reads <c>vgi_rpc.error_kind</c> off a refusal's Arrow body.</summary>
    private static async Task<string?> ErrorKindAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var stream = new MemoryStream(bytes);
        using var reader = new WireReader(stream);
        await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
        var batch = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(batch);
        using (batch!.Batch)
        {
            Assert.Equal("EXCEPTION", batch.GetMetadata(MetadataKeys.LogLevel));
            return batch.GetMetadata(MetadataKeys.ErrorKind);
        }
    }

    private static async Task<TestHost> StartHostAsync(
        IdentityImpl? identity = null,
        RpcHttpEndpoints.AuthenticateDelegate? authenticate = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(
            new RpcServer(typeof(IEcho), new Echo(), identity: identity),
            authenticate: authenticate);
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
