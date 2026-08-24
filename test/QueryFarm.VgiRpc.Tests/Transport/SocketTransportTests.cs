using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tests.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Transport;

/// <summary>
/// Milestone 4: unary calls over a real Unix domain socket (not just the in-process
/// <see cref="PipeTransport"/>). See docs/roadmap.md for the known streaming-over-socket gap
/// this deliberately doesn't cover yet.
/// </summary>
public sealed class SocketTransportTests
{
    [Fact]
    public async Task UnaryCall_RoundTrips_OverUnixSocket()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vgi-rpc-test-{Guid.NewGuid():n}.sock");
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        using var cts = new CancellationTokenSource();

        var serveTask = SocketTransport.ServeUnixAsync(path, (transport, ct) => server.ServeAsync(transport, ct), cts.Token);

        // Wait for the socket file to exist rather than a fixed delay.
        for (var i = 0; i < 50 && !File.Exists(path); i++)
        {
            await Task.Delay(20);
        }

        using var clientTransport = (SocketTransport)await SocketTransport.ConnectUnixAsync(path);
        var connection = new RpcConnection<IGreeter>(clientTransport);
        var client = connection.CreateProxy();

        var result = await client.EchoStringAsync("hello-over-unix-socket");

        Assert.Equal("hello-over-unix-socket", result);

        cts.Cancel();
        File.Delete(path);
    }
}
