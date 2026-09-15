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

    /// <summary>A hosted identity method round-trips over a real transport.</summary>
    /// <remarks>
    /// The guards are unit-tested against <see cref="IdentityImpl"/> directly; this is the other
    /// half -- that a request naming <c>vgi_rpc.Identity.v1</c> actually reaches them, and that
    /// the answer comes back as this framework's ordinary single <c>result</c> binary column.
    /// </remarks>
    [Fact]
    public async Task IssueGrantDispatchesOverTheWire()
    {
        var server = Server(new IdentityImpl(mintGrant: IdentityTestDoubles.Minter, maxAuthAge: 900.0));
        var info = server.MethodsForProtocol(IdentityProtocol.ProtocolName)!["issue_grant"];

        // The pipe transport carries no authenticated principal, which is exactly why the
        // freshness guard refuses: subprocess and unix transports fail closed for free.
        var response = await CallAsync(server, info, ["reports", new List<string> { "read" }, 3600L]);

        Assert.Equal("EXCEPTION", response.GetMetadata(MetadataKeys.LogLevel));
        Assert.Equal("stale_auth", response.GetMetadata(MetadataKeys.ErrorKind));
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
    private static async Task<AnnotatedBatch> CallAsync(RpcServer server, RpcMethodInfo info, object?[] args)
    {
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
