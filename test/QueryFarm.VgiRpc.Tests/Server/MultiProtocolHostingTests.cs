using QueryFarm.VgiRpc.Attributes;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

[ProtocolName("test.Second.v1")]
public interface ISecondProtocol
{
    /// <summary>Collides by name and signature with <see cref="IGreeter.EchoStringAsync"/>.</summary>
    Task<string> EchoStringAsync(string value);

    Task FailAsync();
}

public sealed class SecondProtocol : ISecondProtocol
{
    public Task<string> EchoStringAsync(string value) => Task.FromResult("second:" + value);

    public Task FailAsync() =>
        throw new StatusException("nope", ErrorCodes.Unavailable, "second_down", [new RetryInfo(4)]);
}

[ProtocolName("test.Third.v1")]
public interface IThirdProtocol
{
    Task<long> CountAsync();
}

public sealed class ThirdProtocol : IThirdProtocol
{
    public Task<long> CountAsync() => Task.FromResult(3L);
}

[ProtocolName("vgi_rpc.Reflection.v1")]
public interface IShadowsReflection
{
    Task<string> ListProtocolsAsync();
}

[ProtocolName("test.Second.v1")]
public interface IAlsoSecond
{
    Task PingAsync();
}

/// <summary>WIRE_PROTOCOL.md §3.1, "Hosting several application protocols".</summary>
public class MultiProtocolHostingTests
{
    private static RpcServer Server(string? primaryVersion = null) =>
        new(typeof(IGreeter), new Greeter(), expectedProtocolVersion: primaryVersion,
            additionalProtocols:
            [
                new HostedProtocol(typeof(ISecondProtocol), new SecondProtocol()),
                HostedProtocol.For<IThirdProtocol>(new ThirdProtocol()),
            ]);

    private static async Task<T> CallAsync<TContract, T>(
        RpcServer server, Func<TContract, Task<T>> call, string? version = null)
        where TContract : class
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var serveTask = server.ServeOneAsync(serverTransport);
        var proxy = new RpcConnection<TContract>(
            clientTransport,
            new RpcClientOptions { Protocol = WireNaming.ForProtocol(typeof(TContract)), ProtocolVersion = version })
            .CreateProxy();
        try
        {
            return await call(proxy);
        }
        finally
        {
            await serveTask;
        }
    }

    /// <summary>Application protocols in registration order, primary first; framework after.</summary>
    [Fact]
    public void ListedInRegistrationOrder()
    {
        var server = Server();
        Assert.Equal(["Greeter", "test.Second.v1", "test.Third.v1", "vgi_rpc.Reflection.v1"], server.HostedProtocols);
        Assert.Equal(["Greeter", "test.Second.v1", "test.Third.v1"], server.ApplicationProtocols);
        Assert.NotNull(server.MethodsForProtocol("test.Second.v1"));
        Assert.NotEqual(server.ProtocolHashFor("Greeter"), server.ProtocolHashFor("test.Second.v1"));
    }

    /// <summary>Registration order, not name order: a server that sorts its listing passes a
    /// registration that happens to be alphabetical, so this one is not.</summary>
    [Fact]
    public void ListedInRegistrationOrderNotSorted()
    {
        var server = new RpcServer(
            typeof(IThirdProtocol), new ThirdProtocol(),
            additionalProtocols: [HostedProtocol.For<IGreeter>(new Greeter()), HostedProtocol.For<ISecondProtocol>(new SecondProtocol())]);
        Assert.Equal(["test.Third.v1", "Greeter", "test.Second.v1"], server.ApplicationProtocols);
    }

    /// <summary>The same method name resolves by (protocol, method).</summary>
    [Fact]
    public async Task RoutesByPair()
    {
        var server = Server();
        Assert.Equal("ping", await CallAsync<IGreeter, string>(server, p => p.EchoStringAsync("ping")));
        Assert.Equal("second:ping", await CallAsync<ISecondProtocol, string>(server, p => p.EchoStringAsync("ping")));
        Assert.Equal(3L, await CallAsync<IThirdProtocol, long>(server, p => p.CountAsync()));
    }

    /// <summary>The version gate is per binding: a secondary declaring none is not refused
    /// because the primary declares one.</summary>
    [Fact]
    public async Task TheVersionGateBelongsToTheBinding()
    {
        var server = Server(primaryVersion: "2.0.0");
        Assert.Equal("second:x", await CallAsync<ISecondProtocol, string>(server, p => p.EchoStringAsync("x")));

        var refused = await Assert.ThrowsAsync<ProtocolVersionException>(
            () => CallAsync<IGreeter, string>(server, p => p.EchoStringAsync("x"), version: "1.0.0"));
        Assert.Equal("FAILED_PRECONDITION", refused.ErrorCode);
        Assert.Contains(refused.GetPreconditionFailure()!.Violations, v => v.Subject == "Greeter");
        Assert.Equal("x", await CallAsync<IGreeter, string>(server, p => p.EchoStringAsync("x"), version: "2.0.0"));
    }

    /// <summary>A secondary's errors carry its code, kind and details over the byte stream.</summary>
    [Fact]
    public async Task ASecondaryErrorCarriesTheModel()
    {
        var server = Server();
        var error = await Assert.ThrowsAsync<RpcException>(
            () => CallAsync<ISecondProtocol, int>(server, async p => { await p.FailAsync(); return 0; }));
        Assert.Equal(("UNAVAILABLE", "second_down"), (error.ErrorCode, error.ErrorKind));
        Assert.Equal(4, error.GetRetryInfo()!.RetryDelaySeconds);
        // Tracebacks are included by default, on every transport.
        Assert.NotEqual("", error.RemoteTraceback);
    }

    /// <summary>The explicit setting wins over the transport default.</summary>
    [Fact]
    public async Task TracebacksCanBeTurnedOff()
    {
        var server = new RpcServer(
            typeof(IGreeter), new Greeter(),
            additionalProtocols: [new HostedProtocol(typeof(ISecondProtocol), new SecondProtocol())])
        { IncludeTracebacks = false };
        var error = await Assert.ThrowsAsync<RpcException>(
            () => CallAsync<ISecondProtocol, int>(server, async p => { await p.FailAsync(); return 0; }));
        Assert.Equal("", error.RemoteTraceback);
        Assert.Equal("UNAVAILABLE", error.ErrorCode);
    }

    [Fact]
    public async Task AnUnhostedProtocolIsUnimplemented()
    {
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        var error = await Assert.ThrowsAsync<RpcException>(
            () => CallAsync<ISecondProtocol, string>(server, p => p.EchoStringAsync("x")));
        Assert.Equal(("protocol_not_supported", "UNIMPLEMENTED"), (error.ErrorKind, error.ErrorCode));
    }

    /// <summary>The reserved prefix is refused for an additional protocol, not only the primary.</summary>
    [Fact]
    public void TheReservedPrefixIsRefusedForEveryRegisteredProtocol()
    {
        var error = Assert.Throws<ArgumentException>(() => new RpcServer(
            typeof(IGreeter), new Greeter(),
            additionalProtocols: [new HostedProtocol(typeof(IShadowsReflection), new Shadow())]));
        Assert.Contains("reserved", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Names are unique: a second registration is an error, not last-writer-wins.</summary>
    [Fact]
    public void ADuplicateNameIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new RpcServer(
            typeof(IGreeter), new Greeter(),
            additionalProtocols:
            [
                new HostedProtocol(typeof(ISecondProtocol), new SecondProtocol()),
                new HostedProtocol(typeof(IAlsoSecond), new AlsoSecond()),
            ]));
        Assert.Throws<ArgumentException>(() => new RpcServer(
            typeof(IGreeter), new Greeter(),
            additionalProtocols: [new HostedProtocol(typeof(IGreeter), new Greeter())]));
    }

    [Fact]
    public void AnImplementationMustImplementItsInterface() =>
        Assert.Throws<ArgumentException>(() => new RpcServer(
            typeof(IGreeter), new Greeter(),
            additionalProtocols: [new HostedProtocol(typeof(ISecondProtocol), new ThirdProtocol())]));

    [Fact]
    public void AMalformedVersionIsRefusedAtConstruction() =>
        Assert.Throws<ArgumentException>(() => new RpcServer(
            typeof(IGreeter), new Greeter(),
            additionalProtocols: [new HostedProtocol(typeof(ISecondProtocol), new SecondProtocol(), "v2")]));

    /// <summary>Tracebacks are included on a network transport too: one server-wide setting, no
    /// per-transport default.</summary>
    [Fact]
    public async Task TracebacksAreIncludedOnTcpByDefault()
    {
        var server = Server();
        using var cts = new CancellationTokenSource();
        int port = 0;
        var bound = new TaskCompletionSource();
        var serveTask = SocketTransport.ServeTcpAsync(
            "127.0.0.1", 0, (t, ct) => server.ServeAsync(t, ct), cts.Token,
            onBound: p => { port = p; bound.SetResult(); });
        await bound.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var clientTransport = (SocketTransport)await SocketTransport.ConnectTcpAsync("127.0.0.1", port);
        var proxy = new RpcConnection<ISecondProtocol>(
            clientTransport, new RpcClientOptions { Protocol = "test.Second.v1" }).CreateProxy();
        var error = await Assert.ThrowsAsync<RpcException>(() => proxy.FailAsync());
        Assert.NotEqual("", error.RemoteTraceback);
        cts.Cancel();
    }

    /// <summary>The protocol set is sealed at construction: changing the list a server was built
    /// from changes nothing it hosts, before or after serving starts, and there is no API to add
    /// one later.</summary>
    [Fact]
    public async Task TheProtocolSetIsSealedAtConstruction()
    {
        var extra = new List<HostedProtocol> { new(typeof(ISecondProtocol), new SecondProtocol()) };
        var server = new RpcServer(typeof(IGreeter), new Greeter(), additionalProtocols: extra);
        var before = server.HostedProtocols.ToList();
        extra.Add(HostedProtocol.For<IThirdProtocol>(new ThirdProtocol()));
        Assert.Equal(before, server.HostedProtocols);

        Assert.Equal("x", await CallAsync<IGreeter, string>(server, p => p.EchoStringAsync("x")));
        extra.Clear();
        Assert.Equal(before, server.HostedProtocols);
        Assert.Null(server.MethodsForProtocol("test.Third.v1"));
        Assert.DoesNotContain(
            typeof(RpcServer).GetMethods(),
            m => m.IsPublic && (m.Name.StartsWith("Add", StringComparison.Ordinal) || m.Name.StartsWith("Register", StringComparison.Ordinal)));
    }

    /// <summary>Cancelling an idle serve loop is a shutdown, not an exception: a worker whose
    /// SIGTERM handler cancels the token must exit cleanly rather than abort.</summary>
    [Fact]
    public async Task CancellingAnIdleServeLoopReturnsNormally()
    {
        var (_, serverTransport) = PipeTransport.CreatePair();
        using var cts = new CancellationTokenSource();
        var serve = new RpcServer(typeof(IGreeter), new Greeter()).ServeAsync(serverTransport, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(serve.IsCompletedSuccessfully);
    }

    private sealed class Shadow : IShadowsReflection
    {
        public Task<string> ListProtocolsAsync() => Task.FromResult("");
    }

    private sealed class AlsoSecond : IAlsoSecond
    {
        public Task PingAsync() => Task.CompletedTask;
    }
}
