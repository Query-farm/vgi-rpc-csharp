using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using QueryFarm.VgiRpc.Hash;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tests.Server;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Reflection;

/// <summary>
/// <c>vgi_rpc.Reflection.v1</c> describes itself, and describes itself the way every other port
/// does.
/// </summary>
/// <remarks>
/// <para>
/// This port used to register reflection's binding without registering its methods into it, so
/// <c>describe("vgi_rpc.Reflection.v1")</c> returned an empty method list and the binding hashed
/// to <c>fafffd66…</c> rather than the reference's <c>3c7db4ca…</c>. A client discovering this
/// server the documented way -- <c>list_protocols</c>, then <c>describe</c> -- was told
/// reflection exists and then told it has no methods, so it could not learn to call the protocol
/// it was already calling.
/// </para>
/// <para>
/// It survived because every port was internally self-consistent: <c>list_protocols</c>
/// advertised the same digest the port's own <c>describe</c> returned, so only a port-to-port
/// comparison could see the disagreement. Hence this file. The digest pin is the cross-port
/// half; <see cref="DescribeReportsReflectionsOwnTwoMethods"/> is the half that would still fail
/// if a future change quietly emptied the table again while keeping some other digest.
/// </para>
/// </remarks>
public class ReflectionSelfDescriptionTests
{
    /// <summary>The reference digest, from the canonical Python implementation.</summary>
    private const string ReferenceDigest =
        "3c7db4cae8cdfc93dc4a76e73b8b759e18e45e6a5811adba4e520366344b919a";

    [Fact]
    public void SelfDescriptionMatchesTheReferenceDigest() =>
        Assert.Equal(
            ReferenceDigest,
            ReflectionProtocol.BindingHash(ReflectionProtocol.ProtocolName, ReflectionProtocol.Methods));

    /// <summary>
    /// The preimage, byte for byte -- so a disagreement names the method, field or type token
    /// this port spells differently instead of just saying some byte somewhere moved.
    /// </summary>
    [Fact]
    public void CanonicalPreimageMatchesTheReference()
    {
        var entries = ReflectionProtocol.Methods.Values.Select(info => new ProtocolHash.HashMethod(
            info.WireName,
            info.Kind == RpcMethodKind.Unary ? "unary" : "stream",
            ReflectionProtocol.UnaryHasReturn(info),
            info.HeaderClrType is not null,
            info.ParamsSchema,
            info.ResultSchema,
            ReflectionProtocol.HeaderSchemaOf(info)));

        Assert.Equal(
            "{\"methods\":[{\"has_header\":false,\"has_return\":true,\"name\":\"describe\","
                + "\"params\":[{\"name\":\"protocol\",\"nullable\":false,\"type\":\"utf8\"}],"
                + "\"result\":[{\"name\":\"result\",\"nullable\":false,\"type\":\"binary\"}],\"type\":\"unary\"},"
                + "{\"has_header\":false,\"has_return\":true,\"name\":\"list_protocols\","
                + "\"params\":[],"
                + "\"result\":[{\"name\":\"result\",\"nullable\":false,\"type\":\"binary\"}],\"type\":\"unary\"}],"
                + "\"protocol\":\"vgi_rpc.Reflection.v1\"}",
            ProtocolHash.CanonicalDescription(ReflectionProtocol.ProtocolName, entries));
    }

    /// <summary>Two methods, both unary, both returning a value, neither carrying a header.</summary>
    [Fact]
    public void TheMethodTableIsTheTwoMethodsItAnswers()
    {
        Assert.Equal(
            new[] { ReflectionProtocol.DescribeMethod, ReflectionProtocol.ListProtocolsMethod },
            ReflectionProtocol.Methods.Keys.Order().ToArray());

        foreach (var info in ReflectionProtocol.Methods.Values)
        {
            Assert.Equal(RpcMethodKind.Unary, info.Kind);
            Assert.True(ReflectionProtocol.UnaryHasReturn(info));
            Assert.Null(info.HeaderClrType);
        }
    }

    /// <summary>
    /// What reflection dispatches and what it describes are one set -- not two that have to be
    /// kept in step.
    /// </summary>
    [Fact]
    public void TheDispatchableNamesAreTheTableItDescribes() =>
        Assert.Equal(
            ReflectionProtocol.Methods.Keys.Order().ToArray(),
            ReflectionProtocol.MethodNames.Order().ToArray());

    /// <summary>The hosted binding is the method table, so the server's digest is the reference's.</summary>
    [Fact]
    public void TheHostedBindingCarriesThoseMethods()
    {
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        var methods = server.MethodsForProtocol(ReflectionProtocol.ProtocolName);

        Assert.NotNull(methods);
        Assert.Equal(
            new[] { ReflectionProtocol.DescribeMethod, ReflectionProtocol.ListProtocolsMethod },
            methods!.Keys.Order().ToArray());
        Assert.Equal(ReferenceDigest, server.ProtocolHashFor(ReflectionProtocol.ProtocolName));
    }

    /// <summary>
    /// The end a client actually sees: ask the running server to describe reflection, over a real
    /// transport, and read the two methods back out of the payload.
    /// </summary>
    /// <remarks>
    /// Driven with the registered <c>describe</c> method's own params schema, which also pins the
    /// table to dispatch: the server reads the <c>protocol</c> argument off the request batch by
    /// name, so a table describing a differently-named parameter than the one dispatch reads
    /// would fail here rather than only in a port-to-port comparison.
    /// </remarks>
    [Fact]
    public async Task DescribeReportsReflectionsOwnTwoMethods()
    {
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        var describe = ReflectionProtocol.Methods[ReflectionProtocol.DescribeMethod];

        using var payload = await DescribeAsync(
            server, describe.ParamsSchema, ReflectionProtocol.ProtocolName);

        Assert.Equal(
            ReflectionProtocol.ProtocolName,
            ((StringArray)payload.Column("protocol")).GetString(0));
        Assert.Equal(ReferenceDigest, ((StringArray)payload.Column("protocol_hash")).GetString(0));

        var methods = (StructArray)((ListArray)payload.Column("methods")).Values;
        var fields = ((Apache.Arrow.Types.StructType)methods.Data.DataType).Fields;
        T Column<T>(string name) where T : IArrowArray =>
            (T)methods.Fields[fields.ToList().FindIndex(f => f.Name == name)];

        var names = Column<StringArray>("name");
        var types = Column<StringArray>("method_type");
        var hasReturn = Column<BooleanArray>("has_return");
        var hasHeader = Column<BooleanArray>("has_header");

        Assert.Equal(2, methods.Length);
        Assert.Equal(
            new[] { ReflectionProtocol.DescribeMethod, ReflectionProtocol.ListProtocolsMethod },
            Enumerable.Range(0, methods.Length).Select(i => names.GetString(i)).ToArray());
        for (var i = 0; i < methods.Length; i++)
        {
            Assert.Equal("unary", types.GetString(i));
            Assert.True(hasReturn.GetValue(i));
            Assert.False(hasHeader.GetValue(i));
        }
    }

    /// <summary>Calls <c>describe</c> over a real pipe and decodes the embedded IPC payload.</summary>
    private static async Task<RecordBatch> DescribeAsync(
        RpcServer server, Schema paramsSchema, string protocol)
    {
        var (client, serverTransport) = PipeTransport.CreatePair();
        var serveTask = server.ServeOneAsync(serverTransport);

        using var request = ValueCodec.BuildRow(paramsSchema, [protocol]);
        var metadata = new Dictionary<string, string>
        {
            [MetadataKeys.Method] = ReflectionProtocol.DescribeMethod,
            [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
            [MetadataKeys.Protocol] = ReflectionProtocol.ProtocolName,
        };
        await using (var writer = new WireWriter(client.Output, paramsSchema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(request, metadata));
        }

        byte[]? serialized = null;
        using (var reader = new WireReader(client.Input))
        {
            await reader.ReadSchemaAsync();
            while (await reader.ReadNextAsync() is { } batch)
            {
                using (batch.Batch)
                {
                    if (batch.Batch.Column("result") is BinaryArray result && result.Length > 0)
                    {
                        serialized = result.GetBytes(0).ToArray();
                    }
                }
            }
        }

        await serveTask;

        Assert.NotNull(serialized);
        using var stream = new System.IO.MemoryStream(serialized!);
        using var ipc = new ArrowStreamReader(stream);
        var described = await ipc.ReadNextRecordBatchAsync();
        Assert.NotNull(described);
        return described!;
    }
}
