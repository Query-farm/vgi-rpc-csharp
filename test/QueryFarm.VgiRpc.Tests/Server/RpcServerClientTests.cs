using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

public enum Status
{
    Pending,
    Active,
    Closed,
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
}

public sealed class Greeter : IGreeter
{
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

        // Deliberately connect a client typed for a *different* interface that shares no
        // methods, to force an unknown-method error path end-to-end.
        var connection = new RpcConnection<IOther>(clientTransport);
        var otherClient = connection.CreateProxy();

        var exception = await Assert.ThrowsAsync<MethodNotImplementedException>(() => otherClient.DoSomethingAsync());
        Assert.Equal(MethodNotImplementedException.ErrorKindConst, exception.ErrorKind);
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
    public async Task CallContext_EmitLog_DoesNotBreakTheCall()
    {
        var (server, client, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoWithLogAsync("hi");

        Assert.Equal("hi", result);
        await serveTask;
    }
}
