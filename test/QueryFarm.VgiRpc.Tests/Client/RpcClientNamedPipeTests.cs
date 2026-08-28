using System.IO.Pipes;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Client;

public sealed class RpcClientNamedPipeTests
{
    public interface IEchoService
    {
        Task<string> EchoAsync(string value);
    }

    private sealed class EchoService : IEchoService
    {
        public Task<string> EchoAsync(string value) => Task.FromResult(value);
    }

    [Fact]
    public async Task ConnectNamedPipe_RoundTripsTypedUnary()
    {
        var pipeName = $"vgi-rpc-{Guid.NewGuid():n}";
        await using var serverPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var accept = serverPipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await using var client = await RpcClient.ConnectNamedPipeAsync(
            pipeName,
            cancellationToken: TestContext.Current.CancellationToken);
        await accept;

        var server = new RpcServer(typeof(IEchoService), new EchoService());
        var serve = server.ServeOneAsync(
            new StreamTransport(serverPipe),
            TestContext.Current.CancellationToken);
        var proxy = client.CreateProxy<IEchoService>();

        Assert.Equal("named-pipe", await proxy.EchoAsync("named-pipe"));
        Assert.True(await serve);
    }

    private sealed class StreamTransport(Stream stream) : IRpcTransport
    {
        public Stream Input => stream;

        public Stream Output => stream;
    }
}
