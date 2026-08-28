using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tests.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Client;

public sealed class RpcClientSharedMemoryTests
{
    [Fact]
    public async Task Unary_NegotiatesAndRoundTripsLargePayloadThroughSharedMemory()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var implementation = new Greeter();
        var server = new RpcServer(typeof(IGreeter), implementation);
        var serveTask = Task.Run(async () =>
        {
            Assert.True(await server.ServeOneAsync(serverTransport, TestContext.Current.CancellationToken));
            Assert.True(await server.ServeOneAsync(serverTransport, TestContext.Current.CancellationToken));
        }, TestContext.Current.CancellationToken);
        await using var client = new RpcClient(
            clientTransport,
            new RpcClientOptions { SharedMemorySize = 4 * 1024 * 1024 });
        var method = new RpcMethodInfo(typeof(IGreeter).GetMethod(nameof(IGreeter.EchoLargeBytesAsync))!);
        using var value = new LargeBytesBuffer(Enumerable.Range(0, 512 * 1024).Select(index => (byte)index).ToArray());
        using var request = ValueCodec.BuildRow(method.ParamsSchema, [value]);

        var response = await client.CallUnaryAsync(method.WireName, request, cancellationToken: TestContext.Current.CancellationToken);
        using (response.Batch)
        using (var result = (LargeBytesBuffer)ValueCodec.ExtractRow(response.Batch, [typeof(LargeBytesBuffer)])[0]!)
        {
            Assert.Equal(value.Length, result.Length);
            Assert.Equal(value.ToArray(), result.ToArray());
        }

        await client.DisposeAsync();
        await serveTask;
    }
}
