using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Apache.Arrow;
using QueryFarm.VgiRpc.AccessLog;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tests.Identity;
using QueryFarm.VgiRpc.Tests.Server;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.AccessLog;

/// <summary>
/// An access record names the protocol that owns the dispatched method -- and carries
/// <em>that</em> protocol's digest.
/// </summary>
/// <remarks>
/// <para>
/// <c>access-log-spec.md</c> §3 makes <c>protocol</c> the owning protocol's wire name, "not a
/// server-wide default", and <c>protocol_hash</c> "the registry key when decoding archived
/// records". The two must agree, and the canonical Python implementation shipped a version where
/// they did not: the name was per-binding while the hash was the server's primary at every emit
/// site, so a reflection record named one protocol and carried another's digest. That is worse
/// than either field being wrong alone -- the record is well-formed, passes the schema, groups
/// plausibly on a dashboard, and decodes against the wrong description.
/// </para>
/// <para>
/// Nothing catches it except a call to a <em>secondary</em> protocol: for an application method
/// the primary <em>is</em> the owning binding. So these tests drive reflection and identity, not
/// just the application, and assert the digest against what reflection itself reports for that
/// protocol rather than against "not the primary" -- the weaker assertion passes for a port that
/// logs some third wrong value.
/// </para>
/// </remarks>
public class PerBindingIdentityTests
{
    private sealed class CapturingSink : IAccessLogSink
    {
        public ConcurrentQueue<AccessLogRecord> Records { get; } = new();

        public bool IncludeRequestData => false;

        public void Write(AccessLogRecord record) => Records.Enqueue(record);
    }

    private static RpcServer Server(CapturingSink sink) =>
        new(typeof(IGreeter), new Greeter(), accessLog: sink,
            identity: new IdentityImpl(mintGrant: IdentityTestDoubles.Minter));

    /// <summary>
    /// The precondition that makes everything below meaningful: if the three bindings hashed
    /// alike, logging the primary everywhere would be both invisible and harmless.
    /// </summary>
    [Fact]
    public void TheHostedBindingsDoNotShareADigest()
    {
        var server = Server(new CapturingSink());
        var digests = server.HostedProtocols.Select(server.ProtocolHashFor).ToList();

        Assert.Equal(3, digests.Count);
        Assert.Equal(digests.Count, digests.Distinct().Count());
    }

    /// <summary>
    /// The application protocol's access digest is the canonical one -- the same value
    /// reflection reports, and the same value every other port computes.
    /// </summary>
    /// <remarks>
    /// This port used to log a port-local digest here (SHA-256 over a StringBuilder of Arrow
    /// <c>TypeId</c>s) while reflection reported the canonical one. Both are 64 lowercase hex
    /// characters and both pass the schema, so the divergence was invisible -- but a consumer
    /// keying its registry on <c>protocol_hash</c> builds that registry from
    /// <c>list_protocols</c>/<c>describe</c>, and the port-local digest is a key in no registry
    /// at all.
    /// </remarks>
    [Fact]
    public void ThePrimaryDigestIsTheOneReflectionReports()
    {
        var server = Server(new CapturingSink());
        Assert.Equal(
            ReflectionProtocol.BindingHash(server.ProtocolName, server.MethodsForProtocol(server.ProtocolName)!),
            server.ProtocolHash);
    }

    /// <summary>An application call is labelled with the application binding.</summary>
    [Fact]
    public async Task AnApplicationCallCarriesTheApplicationBinding()
    {
        var sink = new CapturingSink();
        var server = Server(sink);
        var info = server.Methods["echo_string"];

        await CallAsync(server, info.WireName, server.ProtocolName, info.ParamsSchema, ["hi"]);

        var record = Assert.Single(sink.Records);
        AssertIdentity(server, record, server.ProtocolName, "echo_string");
    }

    /// <summary>
    /// A reflection call is labelled with reflection -- on this transport at all, and with its
    /// own digest.
    /// </summary>
    /// <remarks>
    /// "At all" is half the point: HTTP already emitted a record for a reflection call while the
    /// serve loop emitted none, and two transports disagreeing about whether a call happened
    /// leaves a hole in an audit trail that reads as quiet traffic rather than as a gap.
    /// </remarks>
    [Fact]
    public async Task AReflectionCallCarriesReflectionsOwnBinding()
    {
        var sink = new CapturingSink();
        var server = Server(sink);

        await CallAsync(
            server, ReflectionProtocol.ListProtocolsMethod, ReflectionProtocol.ProtocolName,
            new Schema.Builder().Build(), []);

        var record = Assert.Single(sink.Records);
        AssertIdentity(server, record, ReflectionProtocol.ProtocolName, ReflectionProtocol.ListProtocolsMethod);
    }

    /// <summary>An identity call is labelled with identity.</summary>
    [Fact]
    public async Task AnIdentityCallCarriesIdentitysOwnBinding()
    {
        var sink = new CapturingSink();
        var server = Server(sink);
        var info = server.MethodsForProtocol(IdentityProtocol.ProtocolName)!["issue_grant"];

        // Refused (the pipe carries no authenticated principal) -- which is the interesting
        // case: a refusal is exactly the record an auditor of credential issuance reads, so it
        // must be filed under the protocol that refused.
        await CallAsync(
            server, info.WireName, IdentityProtocol.ProtocolName, info.ParamsSchema,
            ["reports", new List<string> { "read" }, 3600L]);

        var record = Assert.Single(sink.Records);
        Assert.Equal("error", record.Status);
        AssertIdentity(server, record, IdentityProtocol.ProtocolName, "issue_grant");
    }

    /// <summary>
    /// The three bindings' records, seen together: each names its own protocol and no two share
    /// a digest.
    /// </summary>
    /// <remarks>
    /// Written as one server's log rather than three isolated assertions because that is the
    /// artefact a consumer actually reads -- one stream in which the protocol field has to be a
    /// usable partition key.
    /// </remarks>
    [Fact]
    public async Task OneServersLogPartitionsCleanlyByProtocol()
    {
        var sink = new CapturingSink();
        var server = Server(sink);
        var echo = server.Methods["echo_string"];
        var grant = server.MethodsForProtocol(IdentityProtocol.ProtocolName)!["issue_grant"];

        await CallAsync(server, echo.WireName, server.ProtocolName, echo.ParamsSchema, ["hi"]);
        await CallAsync(
            server, ReflectionProtocol.ListProtocolsMethod, ReflectionProtocol.ProtocolName,
            new Schema.Builder().Build(), []);
        await CallAsync(
            server, grant.WireName, IdentityProtocol.ProtocolName, grant.ParamsSchema,
            ["reports", new List<string> { "read" }, 3600L]);

        var records = sink.Records.ToList();
        Assert.Equal(
            new[] { server.ProtocolName, ReflectionProtocol.ProtocolName, IdentityProtocol.ProtocolName },
            records.Select(r => r.Protocol).ToArray());
        Assert.Equal(3, records.Select(r => r.ProtocolHash).Distinct().Count());
        foreach (var record in records)
        {
            Assert.Equal(server.ProtocolHashFor(record.Protocol), record.ProtocolHash);
        }
    }

    /// <summary>
    /// A record's digest is the digest of the protocol it names -- asserted positively, against
    /// what reflection reports, not merely as "different from the primary".
    /// </summary>
    private static void AssertIdentity(RpcServer server, AccessLogRecord record, string protocol, string method)
    {
        Assert.Equal(method, record.Method);
        Assert.Equal(protocol, record.Protocol);
        Assert.Equal(server.ProtocolHashFor(protocol), record.ProtocolHash);
        if (protocol != server.ProtocolName)
        {
            Assert.NotEqual(server.ProtocolHash, record.ProtocolHash);
        }
    }

    /// <summary>Drives one unary call over a real pipe and waits for the serve loop to finish.</summary>
    private static async Task CallAsync(
        RpcServer server, string method, string protocol, Schema paramsSchema, object?[] args)
    {
        var (client, serverTransport) = PipeTransport.CreatePair();
        var serveTask = server.ServeOneAsync(serverTransport);

        var request = ValueCodec.BuildRow(paramsSchema, args);
        var metadata = new Dictionary<string, string>
        {
            [MetadataKeys.Method] = method,
            [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
            [MetadataKeys.Protocol] = protocol,
        };
        await using (var writer = new WireWriter(client.Output, paramsSchema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(request, metadata));
        }

        using var reader = new WireReader(client.Input);
        await reader.ReadSchemaAsync();
        while (await reader.ReadNextAsync() is { } batch)
        {
            batch.Batch.Dispose();
        }

        await serveTask;
    }
}
