using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Apache.Arrow;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tests.Server;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Identity;

/// <summary>Hosting <c>vgi_rpc.Identity.v1</c> -- absent beats routed-and-refusing.</summary>
public class IdentityServerWiringTests
{
    private static RpcServer Server(IdentityImpl? identity = null) =>
        new(typeof(IGreeter), new Greeter(), identity: identity);

    /// <summary>A dependency upgrade must not grow an oracle on every worker.</summary>
    [Fact]
    public void AbsentByDefault()
    {
        var server = Server();
        Assert.DoesNotContain(IdentityProtocol.ProtocolName, server.HostedProtocols);
        Assert.Null(server.MethodsForProtocol(IdentityProtocol.ProtocolName));
    }

    /// <summary>An instance with no hooks is the same as no instance: nothing to host.</summary>
    [Fact]
    public void AnIdentityWithNoHooksIsNotHostedEither()
    {
        var server = Server(new IdentityImpl());
        Assert.DoesNotContain(IdentityProtocol.ProtocolName, server.HostedProtocols);
    }

    /// <summary>What the server hosts describes what it actually does.</summary>
    /// <remarks>
    /// A worker that resolves credentials but does not mint grants offers one method, and a
    /// client learns that from reflection rather than by calling and reading an error.
    /// </remarks>
    [Fact]
    public void OnlyMethodsWithHooksAreHosted()
    {
        Assert.Equal(
            new[] { "introspect_token" },
            new IdentityImpl(resolveToken: IdentityTestDoubles.Resolver, introspectPrincipals: ["proxy"])
                .OfferedMethods().Order().ToArray());

        Assert.Equal(
            new[] { "issue_grant" },
            new IdentityImpl(mintGrant: IdentityTestDoubles.Minter).OfferedMethods().Order().ToArray());

        Assert.Equal(
            new[] { "introspect_token", "issue_grant" },
            new IdentityImpl(
                resolveToken: IdentityTestDoubles.Resolver,
                mintGrant: IdentityTestDoubles.Minter,
                introspectPrincipals: ["proxy"]).OfferedMethods().Order().ToArray());
    }

    /// <summary>Narrowing the method set narrows the protocol hash with it.</summary>
    [Fact]
    public void HostedBindingCarriesOnlyTheOfferedMethods()
    {
        var server = Server(new IdentityImpl(mintGrant: IdentityTestDoubles.Minter));
        var methods = server.MethodsForProtocol(IdentityProtocol.ProtocolName);

        Assert.NotNull(methods);
        Assert.Equal(new[] { "issue_grant" }, methods!.Keys.Order().ToArray());
        Assert.Equal(
            "c71b12f453310139b6b6a445378064661c52711d03ae1e4fba29b8f7976ef4d8",
            ReflectionProtocol.BindingHash(IdentityProtocol.ProtocolName, methods));
    }

    /// <summary>
    /// Registered after reflection, so it appears in reflection's output rather than being
    /// something a client has to know about a priori.
    /// </summary>
    [Fact]
    public void RegisteredAfterReflection()
    {
        var server = Server(new IdentityImpl(mintGrant: IdentityTestDoubles.Minter));
        Assert.Equal(
            ["Greeter", ReflectionProtocol.ProtocolName, IdentityProtocol.ProtocolName],
            server.HostedProtocols);
    }

    /// <summary>A method whose hook is absent is not routed -- it is not there at all.</summary>
    [Fact]
    public async Task AnUnhostedMethodIsNotRouted()
    {
        var server = Server(new IdentityImpl(mintGrant: IdentityTestDoubles.Minter));
        var introspect = ServiceRegistry.GetMethods(typeof(IIdentityProtocol))["introspect_token"];

        var response = await CallAsync(server, introspect, ["anything"]);

        Assert.Equal("EXCEPTION", response.GetMetadata(MetadataKeys.LogLevel));
        Assert.Equal("method_not_implemented", response.GetMetadata(MetadataKeys.ErrorKind));
    }

    /// <summary>Drives one identity call over a real pipe and returns the terminal batch.</summary>
    /// <param name="server">The server to dispatch against.</param>
    /// <param name="info">The method being called.</param>
    /// <param name="args">Its wire arguments, without the framework-injected context.</param>
    /// <param name="peer">The connection identity to install for the duration, or
    /// <see langword="null"/> for an anonymous connection -- what a bare pipe actually carries.</param>
    internal static async Task<AnnotatedBatch> CallAsync(
        RpcServer server, RpcMethodInfo info, object?[] args, PeerConnectionIdentity? peer = null)
    {
        using var scope = peer is null ? null : PeerIdentityScope.Push(peer);
        var (client, serverTransport) = PipeTransport.CreatePair();
        var serveTask = server.ServeOneAsync(serverTransport);

        var request = ValueCodec.BuildRow(info.ParamsSchema, args);
        var metadata = new Dictionary<string, string>
        {
            [MetadataKeys.Method] = info.WireName,
            [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
            [MetadataKeys.Protocol] = IdentityProtocol.ProtocolName,
        };
        await using (var writer = new WireWriter(client.Output, info.ParamsSchema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(request, metadata));
        }

        using var reader = new WireReader(client.Input);
        await reader.ReadSchemaAsync();
        var response = await reader.ReadNextAsync();
        await serveTask;

        Assert.NotNull(response);
        return response!;
    }
}

/// <summary>
/// <c>vgi_rpc.Identity.v1</c> driven end to end over a raw transport, with a real connection
/// identity in front of its guards.
/// </summary>
/// <remarks>
/// <para>
/// This class exists because of a specific bug, in a specific other port, that nothing else could
/// have caught. The Python reference's <see cref="ICallContext"/> injection resolved "does this
/// method want a ctx?" against the <em>primary</em> binding's method set rather than against the
/// binding that owns the dispatched method. Identity is a secondary protocol whose methods both
/// take a ctx -- they need the caller's <see cref="AuthContext"/> to apply their guards -- so they
/// received none, and every call died on a missing argument before any guard ran. The protocol was
/// completely uncallable on every transport, from the day it landed, unnoticed.
/// </para>
/// <para>
/// What hid it was the shape of the tests: all sixty-one of them constructed the implementation
/// directly and handed it a context they had built themselves, so nothing called the protocol end
/// to end. This port's own identity tests had exactly that shape too. It does not have the bug --
/// <see cref="RpcMethodInfo.HasContextParameter"/> is a property of the method, read from the
/// binding's own table in both dispatch paths, not from a per-server map keyed by name -- but
/// "does not have it" is a claim that has to be demonstrated by a call, not by reading the code.
/// </para>
/// <para>
/// Three cases, and the middle one is what makes the other two mean anything: a dispatch path that
/// supplies an <em>empty</em> context refuses everything, which is indistinguishable from a
/// working allowlist unless some caller is also shown to get through.
/// </para>
/// </remarks>
public class IdentityRawTransportDispatchTests
{
    private const string Introspector = "conformance-introspector";

    private static RpcServer Server() => new(
        typeof(IGreeter),
        new Greeter(),
        identity: new IdentityImpl(
            resolveToken: _ => new TokenIdentity("bob", "ci-key", 300),
            mintGrant: IdentityTestDoubles.Minter,
            introspectPrincipals: [Introspector],
            maxAuthAge: 900.0));

    private static PeerConnectionIdentity Connection(string principal) => new(
        new AuthContext("peer", authenticated: true, principal),
        PeerEvidenceSet.Empty,
        new Dictionary<string, object?>());

    /// <summary>An allowlisted connection identity reaches the resolver and gets an answer back.</summary>
    /// <remarks>
    /// The positive case, and the one the reference never had. It proves the ctx actually arrived
    /// carrying the caller: a null context throws before the guard, and an empty one is refused by
    /// it.
    /// </remarks>
    [Fact]
    public async Task AnAllowlistedRawCallerResolvesACredential()
    {
        var server = Server();
        var info = server.MethodsForProtocol(IdentityProtocol.ProtocolName)!["introspect_token"];

        var response = await IdentityServerWiringTests.CallAsync(
            server, info, ["sk_live_abc123"], Connection(Introspector));

        Assert.Null(response.GetMetadata(MetadataKeys.LogLevel));
        var resolved = (TokenIdentity)ValueCodec.ExtractRow(response.Batch, [typeof(TokenIdentity)])[0]!;
        Assert.Equal("bob", resolved.Principal);
        Assert.Equal("ci-key", resolved.TokenName);
        Assert.Equal(300, resolved.TtlSeconds);
    }

    /// <summary>The same transport, a principal off the allowlist, refused.</summary>
    /// <remarks>
    /// Paired with the case above on purpose. Either assertion alone is satisfied by a broken
    /// dispatch path -- one that hands every call an empty context refuses everybody, and one that
    /// ignores the allowlist admits everybody -- so it is the two together that pin the behaviour.
    /// </remarks>
    [Fact]
    public async Task ANonAllowlistedRawCallerIsRefused()
    {
        var server = Server();
        var info = server.MethodsForProtocol(IdentityProtocol.ProtocolName)!["introspect_token"];

        var response = await IdentityServerWiringTests.CallAsync(
            server, info, ["sk_live_abc123"], Connection("conformance-outsider"));

        Assert.Equal("EXCEPTION", response.GetMetadata(MetadataKeys.LogLevel));
        Assert.Equal("introspection_refused", response.GetMetadata(MetadataKeys.ErrorKind));
    }

    /// <summary>An anonymous raw transport cannot mint.</summary>
    /// <remarks>
    /// A bare pipe carries no authenticated principal and therefore no <c>auth_time</c>, so the
    /// freshness guard refuses -- which is how subprocess and unix transports fail closed for free
    /// rather than by a check somebody has to remember to write per transport.
    /// </remarks>
    [Fact]
    public async Task AnAnonymousRawCallerCannotMint()
    {
        var server = Server();
        var info = server.MethodsForProtocol(IdentityProtocol.ProtocolName)!["issue_grant"];

        var response = await IdentityServerWiringTests.CallAsync(
            server, info, ["reports", new List<string> { "read" }, 3600L]);

        Assert.Equal("EXCEPTION", response.GetMetadata(MetadataKeys.LogLevel));
        Assert.Equal("stale_auth", response.GetMetadata(MetadataKeys.ErrorKind));
    }
}
