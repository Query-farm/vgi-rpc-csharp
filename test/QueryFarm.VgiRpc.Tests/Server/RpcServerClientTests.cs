using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

public enum Status
{
    Pending,
    Active,
    Closed,
}

public sealed class PointRecord
{
    public double X { get; set; }
    public double Y { get; set; }
}

public interface IGreeter
{
    Task<string> EchoStringAsync(string value);

    Task<int> AddAsync(int a, int b);

    Task PingAsync();

    Task<string?> EchoOptionalAsync(string? value);

    Task<List<long>> EchoListAsync(List<long> values);

    Task ThrowAsync();

    Task<Status> EchoStatusAsync(Status value);

    Task<Dictionary<string, long>> EchoMapAsync(Dictionary<string, long> value);

    Task<string> EchoWithLogAsync(string value, ICallContext? ctx = null);

    Task<List<PointRecord>> EchoPointsAsync(List<PointRecord> points);

    Task<LargeBytesBuffer> EchoLargeBytesAsync(LargeBytesBuffer value);
}

public sealed class Greeter : IGreeter
{
    public LargeBytesBuffer? LastLargeBytesArgument { get; private set; }

    public Task<string> EchoStringAsync(string value) => Task.FromResult(value);

    public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

    public Task PingAsync() => Task.CompletedTask;

    public Task<string?> EchoOptionalAsync(string? value) => Task.FromResult(value);

    public Task<List<long>> EchoListAsync(List<long> values) => Task.FromResult(values);

    public Task ThrowAsync() => throw new InvalidOperationException("boom");

    public Task<Status> EchoStatusAsync(Status value) => Task.FromResult(value);

    public Task<Dictionary<string, long>> EchoMapAsync(Dictionary<string, long> value) => Task.FromResult(value);

    public Task<string> EchoWithLogAsync(string value, ICallContext? ctx = null)
    {
        ctx!.EmitLog(VgiLogLevel.Info, "processing", new Dictionary<string, object?> { ["value"] = value });
        return Task.FromResult(value);
    }

    public Task<List<PointRecord>> EchoPointsAsync(List<PointRecord> points) => Task.FromResult(points);

    public Task<LargeBytesBuffer> EchoLargeBytesAsync(LargeBytesBuffer value)
    {
        LastLargeBytesArgument = value;
        return Task.FromResult(value);
    }
}

/// <summary>
/// Milestone 1 exit criteria: a full unary RPC round-trip (reflection-based schema derivation,
/// <see cref="RpcServer"/> dispatch, <see cref="DispatchProxy"/>-based client) over the
/// in-process pipe transport.
/// </summary>
public sealed class RpcServerClientTests
{
    private static (RpcServer Server, IGreeter Client, IRpcTransport ServerTransport) Setup()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        var connection = new RpcConnection<IGreeter>(clientTransport);
        return (server, connection.CreateProxy(), serverTransport);
    }

    [Fact]
    public async Task EchoString_RoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoStringAsync("hello");

        Assert.Equal("hello", result);
        Assert.True(await serveTask);
    }

    [Fact]
    public async Task Add_RoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.AddAsync(2, 3);

        Assert.Equal(5, result);
        await serveTask;
    }

    [Fact]
    public async Task VoidMethod_RoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        await client.PingAsync();

        await serveTask;
    }

    [Fact]
    public async Task OptionalString_NullRoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoOptionalAsync(null);

        Assert.Null(result);
        await serveTask;
    }

    [Fact]
    public async Task List_RoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoListAsync([1, 2, 3]);

        Assert.Equal([1L, 2L, 3L], result);
        await serveTask;
    }

    [Fact]
    public async Task ServerException_PropagatesAsRpcException()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var exception = await Assert.ThrowsAsync<RpcException>(() => client.ThrowAsync());

        Assert.Equal("InvalidOperationException", exception.ErrorType);
        Assert.Equal("boom", exception.ErrorMessage);
        await serveTask;
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotImplemented()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        var serveTask = server.ServeOneAsync(serverTransport);

        // A client typed for a *different* interface that shares no methods, forcing the
        // unknown-method path end to end. It has to address the hosted protocol explicitly to
        // get there: a request naming IOther's own protocol is refused one step earlier, as
        // not-hosted, which is the whole point of the pair of tests below.
        var connection = new RpcConnection<IOther>(
            clientTransport,
            new RpcClientOptions { Protocol = WireNaming.ForProtocol(typeof(IGreeter)) });
        var otherClient = connection.CreateProxy();

        var exception = await Assert.ThrowsAsync<MethodNotImplementedException>(() => otherClient.DoSomethingAsync());
        Assert.Equal(MethodNotImplementedException.ErrorKindConst, exception.ErrorKind);
        await serveTask;
    }

    /// <summary>"I do not speak that protocol" is a different answer from "I speak it but not
    /// that method", and a client probing for an optional protocol depends on the difference.</summary>
    [Fact]
    public async Task UnhostedProtocol_ReturnsProtocolNotSupported()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        var serveTask = server.ServeOneAsync(serverTransport);

        // No explicit Protocol, so the connection addresses IOther's own name -- which this
        // server does not host.
        var otherClient = new RpcConnection<IOther>(clientTransport).CreateProxy();

        var exception = await Assert.ThrowsAsync<RpcException>(() => otherClient.DoSomethingAsync());
        Assert.Equal("protocol_not_supported", exception.ErrorKind);
        Assert.Contains("Other", exception.ErrorMessage, StringComparison.Ordinal);
        await serveTask;
    }

    /// <summary>
    /// On a byte-stream transport the routing key is the protocol's only carrier, so a request
    /// without one is refused rather than landed on whichever protocol happens to be first.
    /// </summary>
    /// <remarks>
    /// The deliberate asymmetry with HTTP, where the path segment has already resolved the
    /// binding and an absent key is accepted. Written at the wire level because no client this
    /// port ships can produce such a request any more — which is the point: the loud rejection is
    /// for the intermediary that rebuilds a request and drops the field.
    /// </remarks>
    [Fact]
    public async Task AbsentRoutingKey_IsRefused()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        var serveTask = server.ServeOneAsync(serverTransport);

        var info = ServiceRegistry.GetMethods(typeof(IGreeter))["echo_string"];
        using var parameters = ValueCodec.BuildRow(info.ParamsSchema, ["hi"]);
        await using (var writer = new WireWriter(clientTransport.Output, info.ParamsSchema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(
                parameters,
                new Dictionary<string, string>
                {
                    [MetadataKeys.Method] = info.WireName,
                    [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
                }));
        }

        using var reader = new WireReader(clientTransport.Input);
        await reader.ReadSchemaAsync();
        var response = await reader.ReadNextAsync();
        Assert.NotNull(response);
        using (response!.Batch)
        {
            Assert.Equal("EXCEPTION", response.GetMetadata(MetadataKeys.LogLevel));
            Assert.Equal("protocol_not_specified", response.GetMetadata(MetadataKeys.ErrorKind));
        }

        await serveTask;
    }

    public interface IOther
    {
        Task DoSomethingAsync();
    }

    [Fact]
    public async Task Enum_RoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoStatusAsync(Status.Active);

        Assert.Equal(Status.Active, result);
        await serveTask;
    }

    [Fact]
    public async Task Map_RoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoMapAsync(new Dictionary<string, long> { ["a"] = 1, ["b"] = 2 });

        Assert.Equal(new Dictionary<string, long> { ["a"] = 1, ["b"] = 2 }, result);
        await serveTask;
    }

    [Fact]
    public async Task ListOfStruct_RoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoPointsAsync([
            new PointRecord { X = 1.0, Y = 2.0 },
            new PointRecord { X = 3.0, Y = 4.0 },
        ]);

        Assert.Equal(2, result.Count);
        Assert.Equal(1.0, result[0].X);
        Assert.Equal(2.0, result[0].Y);
        Assert.Equal(3.0, result[1].X);
        Assert.Equal(4.0, result[1].Y);
        await serveTask;
    }

    [Fact]
    public async Task EmptyListOfStruct_RoundTrips()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoPointsAsync([]);

        Assert.Empty(result);
        await serveTask;
    }

    [Fact]
    public async Task CallContext_EmitLog_DoesNotBreakTheCall()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoWithLogAsync("hi");

        Assert.Equal("hi", result);
        await serveTask;
    }

    [Fact]
    public async Task LargeBytesBuffer_RoundTrips_AndServerArgumentIsReleased()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var service = new Greeter();
        var server = new RpcServer(typeof(IGreeter), service);
        var client = new RpcConnection<IGreeter>(clientTransport).CreateProxy();
        var serveTask = server.ServeOneAsync(serverTransport);
        using var input = new LargeBytesBuffer(new byte[] { 1, 2, 3, 4 });

        using var result = await client.EchoLargeBytesAsync(input);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.ToArray());
        Assert.Equal(4, input.Length);
        Assert.True(await serveTask);
        Assert.NotNull(service.LastLargeBytesArgument);
        Assert.Throws<ObjectDisposedException>(() => service.LastLargeBytesArgument!.ToArray());
    }
}
