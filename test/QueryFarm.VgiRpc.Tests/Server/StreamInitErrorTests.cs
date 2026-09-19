using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

/// <summary>
/// A stream whose constructor throws must leave a byte-stream connection usable: the reference
/// suite's <c>TestExchangeStream::test_error_on_init</c> refuses the open and then calls
/// <c>echo_int</c> on the same connection.
/// </summary>
/// <remarks>
/// A stream request is followed on the wire by a second IPC stream, the client's tick/exchange
/// input. The server used to write the error and return without reading that input, so the next
/// <see cref="RpcServer.ServeOneAsync"/> parsed it as a request and answered the caller's next,
/// unrelated call with "Request batch is missing vgi_rpc.method metadata".
/// </remarks>
public sealed class StreamInitErrorTests
{
    private const string InitErrorMessage = "intentional init error";

    private static readonly Schema s_valuesSchema = new([new Field("value", Int64Type.Default, false)], null);

    public interface IInitErrorService
    {
        Task<long> EchoAsync(long value);

        Task<RpcStream<NeverExchange>> ExchangeErrorOnInitAsync();

        Task<RpcStream<NeverProducer>> ProduceErrorOnInitAsync();
    }

    private sealed class InitErrorService : IInitErrorService
    {
        public Task<long> EchoAsync(long value) => Task.FromResult(value);

        public Task<RpcStream<NeverExchange>> ExchangeErrorOnInitAsync() =>
            throw new InvalidOperationException(InitErrorMessage);

        public Task<RpcStream<NeverProducer>> ProduceErrorOnInitAsync() =>
            throw new InvalidOperationException(InitErrorMessage);
    }

    public sealed class NeverExchange : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken) =>
            throw new TurnRanException();
    }

    public sealed class NeverProducer : ProducerState
    {
        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken) =>
            throw new TurnRanException();
    }

    private sealed class TurnRanException() : Exception("the stream constructor threw; no turn should run");

    [Fact]
    public async Task ExchangeInitError_WithDeclaredInputSchema_LeavesConnectionUsable()
    {
        await AssertNextCallSucceedsAfterAsync(async client =>
        {
            using var parameters = EmptyParameters();
            await using var session = await client.OpenExchangeAsync(
                "exchange_error_on_init", parameters, s_valuesSchema, cancellationToken: TestContext.Current.CancellationToken);
            using var input = Values(1);
            await session.ExchangeAsync(input, cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    /// <summary>The shape the conformance client driver opens: input schema set by the first turn.</summary>
    [Fact]
    public async Task ExchangeInitError_WithLazyInputSchema_LeavesConnectionUsable()
    {
        await AssertNextCallSucceedsAfterAsync(async client =>
        {
            using var parameters = EmptyParameters();
            await using var session = await client.OpenExchangeAsync(
                "exchange_error_on_init", parameters, cancellationToken: TestContext.Current.CancellationToken);
            using var input = Values(1);
            await session.ExchangeAsync(input, cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task ProducerInitError_LeavesConnectionUsable()
    {
        await AssertNextCallSucceedsAfterAsync(async client =>
        {
            using var parameters = EmptyParameters();
            await using var session = await client.OpenProducerAsync(
                "produce_error_on_init", parameters, cancellationToken: TestContext.Current.CancellationToken);
            await session.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    private static async Task AssertNextCallSucceedsAfterAsync(Func<RpcClient, Task> refusedStream)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IInitErrorService), new InitErrorService());
        await using var client = new RpcClient(
            clientTransport,
            new RpcClientOptions { Protocol = WireNaming.ForProtocol(typeof(IInitErrorService)) });

        var streamServe = server.ServeOneAsync(serverTransport, cancellationToken);
        var refusal = await Assert.ThrowsAsync<RpcException>(() => refusedStream(client));
        Assert.Contains(InitErrorMessage, refusal.ErrorMessage, StringComparison.Ordinal);
        Assert.True(await streamServe.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));

        var unaryServe = server.ServeOneAsync(serverTransport, cancellationToken);
        using var request = new RecordBatch(s_valuesSchema, [new Int64Array.Builder().Append(42).Build()], 1);
        var response = await client.CallUnaryAsync("echo", request, cancellationToken: cancellationToken);
        using (response.Batch)
        {
            Assert.Equal(42L, ((Int64Array)response.Batch.Column(0)).GetValue(0));
        }

        Assert.True(await unaryServe.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
    }

    private static RecordBatch EmptyParameters() => new(new Schema([], null), [], 1);

    private static RecordBatch Values(long value) =>
        new(s_valuesSchema, [new Int64Array.Builder().Append(value).Build()], 1);
}
