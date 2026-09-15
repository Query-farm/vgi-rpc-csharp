using System.Collections.Generic;
using System.Threading.Tasks;
using Apache.Arrow;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

/// <summary>
/// A stale <c>__describe__</c> caller is told where introspection went.
/// </summary>
/// <remarks>
/// <para>
/// This server never answered <c>__describe__</c>, which is the correct end state -- but "no
/// such method" is the same answer a server built without introspection gives, and the two need
/// opposite fixes: update the client, or reconfigure the server. The C++ port spent real time on
/// the first while reading an error that described the second.
/// </para>
/// <para>
/// So the refusal names its replacement and both of that protocol's entry points, which makes a
/// stale client fixable from the error text alone. Only this one reserved name is special-cased;
/// every other keeps the plain capability answer, which is what a client probing for an optional
/// method needs in order to tell "absent" from "refused".
/// </para>
/// </remarks>
public class DescribeRetirementTests
{
    private static RpcServer Server() => new(typeof(IGreeter), new Greeter());

    /// <summary>The refusal carries the protocol and both entry points, in the order asked.</summary>
    [Fact]
    public async Task TheRefusalNamesTheReplacement()
    {
        var message = await RefusalFor("__describe__");

        Assert.Contains("retired", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ReflectionProtocol.ProtocolName, message, System.StringComparison.Ordinal);
        Assert.Contains(ReflectionProtocol.ListProtocolsMethod, message, System.StringComparison.Ordinal);
        Assert.Contains(ReflectionProtocol.DescribeMethod, message, System.StringComparison.Ordinal);
        Assert.True(
            message.IndexOf(ReflectionProtocol.ListProtocolsMethod, System.StringComparison.Ordinal)
                < message.IndexOf($"'{ReflectionProtocol.DescribeMethod}'", System.StringComparison.Ordinal),
            $"the two entry points are asked in order -- list_protocols, then describe: {message}");
    }

    /// <summary>
    /// Refused as "no such method", not as a routing failure: the caller did nothing wrong with
    /// routing, and a client that reads this as "name a protocol" retries forever.
    /// </summary>
    [Fact]
    public async Task TheRefusalIsACapabilityAnswer()
    {
        var (_, kind) = await ResponseFor("__describe__", protocol: null);
        Assert.Equal(MetadataKeys.ErrorKinds.MethodNotImplemented, kind);
    }

    /// <summary>
    /// Answered without naming a protocol and without agreeing about versions -- this is what a
    /// stale or mismatched client calls to find out what is wrong, so gating it on either would
    /// withhold the diagnosis exactly when it is needed.
    /// </summary>
    [Fact]
    public async Task TheRefusalSurvivesAVersionMismatch()
    {
        var server = new RpcServer(typeof(IGreeter), new Greeter(), expectedProtocolVersion: "9.0.0");
        var (message, kind) = await ResponseFor("__describe__", protocol: null, server: server);

        Assert.Equal(MetadataKeys.ErrorKinds.MethodNotImplemented, kind);
        Assert.Contains(ReflectionProtocol.ProtocolName, message, System.StringComparison.Ordinal);
    }

    /// <summary>Only describe is special-cased; the rest keep the plain capability answer.</summary>
    [Fact]
    public async Task AnotherReservedNameKeepsTheGenericAnswer()
    {
        var message = await RefusalFor("__not_a_thing__");

        Assert.DoesNotContain("retired", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReflectionProtocol.ProtocolName, message, System.StringComparison.Ordinal);
    }

    /// <summary>An ordinary unknown method is untouched too.</summary>
    [Fact]
    public async Task AnOrdinaryUnknownMethodKeepsTheGenericAnswer()
    {
        var (message, kind) = await ResponseFor("no_such_method", protocol: "Greeter");

        Assert.Equal(MetadataKeys.ErrorKinds.MethodNotImplemented, kind);
        Assert.DoesNotContain("retired", message, System.StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RefusalFor(string method) =>
        (await ResponseFor(method, protocol: null)).Message;

    private static async Task<(string Message, string? Kind)> ResponseFor(
        string method, string? protocol, RpcServer? server = null)
    {
        server ??= Server();
        var (client, serverTransport) = PipeTransport.CreatePair();
        var serveTask = server.ServeOneAsync(serverTransport);

        var schema = new Schema.Builder().Build();
        var metadata = new Dictionary<string, string>
        {
            [MetadataKeys.Method] = method,
            [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
        };
        if (protocol is not null)
        {
            metadata[MetadataKeys.Protocol] = protocol;
        }

        await using (var writer = new WireWriter(client.Output, schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(schema), metadata));
        }

        using var reader = new WireReader(client.Input);
        await reader.ReadSchemaAsync();
        var response = await reader.ReadNextAsync();
        await serveTask;

        Assert.NotNull(response);
        return (response!.GetMetadata(MetadataKeys.LogMessage) ?? "", response.GetMetadata(MetadataKeys.ErrorKind));
    }
}
