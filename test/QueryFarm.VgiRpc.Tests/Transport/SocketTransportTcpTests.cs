using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tests.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Transport;

public sealed class SocketTransportTcpTests
{
    [Fact]
    public async Task UnaryCallRoundTripsOverTcp()
    {
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        using var cancellation = new CancellationTokenSource();
        var boundPort = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serveTask = SocketTransport.ServeTcpAsync(
            "127.0.0.1",
            0,
            (transport, token) => server.ServeAsync(transport, token),
            cancellation.Token,
            port => boundPort.TrySetResult(port));

        using var clientTransport = (SocketTransport)await SocketTransport.ConnectTcpAsync(
            "127.0.0.1",
            await boundPort.Task);
        var client = new RpcConnection<IGreeter>(clientTransport).CreateProxy();

        Assert.Equal("hello-over-tcp", await client.EchoStringAsync("hello-over-tcp"));

        cancellation.Cancel();
        await serveTask;
    }
}
