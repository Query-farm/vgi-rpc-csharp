using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Client;

public sealed class RpcClientStreamingTests
{
    private static readonly Schema s_valuesSchema = new([new Field("value", Int64Type.Default, false)], null);

    public interface IStreamingService
    {
        Task<RpcStream<CountingProducer>> CountAsync(long count);

        Task<RpcStream<DoublingExchange>> DoubleAsync();

        Task<RpcStream<CancelAwareProducer>> UntilCancelledAsync();
    }

    public interface IStreamingClient
    {
        Task<IRpcProducerSession> CountAsync(long count, CancellationToken cancellationToken = default);

        Task<RpcExchangeSession<ValueInput>> DoubleAsync(CancellationToken cancellationToken = default);
    }

    public sealed class ValueInput
    {
        public long Value { get; set; }
    }

    private sealed class StreamingService : IStreamingService
    {
        public CancelAwareProducer? LastCancelAwareProducer { get; private set; }

        public Task<RpcStream<CountingProducer>> CountAsync(long count) =>
            Task.FromResult(new RpcStream<CountingProducer>(s_valuesSchema, new CountingProducer(count)));

        public Task<RpcStream<DoublingExchange>> DoubleAsync() =>
            Task.FromResult(new RpcStream<DoublingExchange>(s_valuesSchema, new DoublingExchange(), s_valuesSchema));

        public Task<RpcStream<CancelAwareProducer>> UntilCancelledAsync()
        {
            LastCancelAwareProducer = new CancelAwareProducer();
            return Task.FromResult(new RpcStream<CancelAwareProducer>(s_valuesSchema, LastCancelAwareProducer));
        }
    }

    public sealed class CountingProducer(long count) : ProducerState
    {
        private long _next;

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            if (_next >= count)
            {
                output.Finish();
                return Task.CompletedTask;
            }

            output.Emit(Batch(_next++));
            return Task.CompletedTask;
        }
    }

    public sealed class DoublingExchange : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            var value = ((Int64Array)input.Batch.Column(0)).GetValue(0)!.Value;
            output.Emit(Batch(value * 2));
            return Task.CompletedTask;
        }
    }

    public sealed class CancelAwareProducer : ProducerState
    {
        public bool Cancelled { get; private set; }

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            output.Emit(Batch(1));
            return Task.CompletedTask;
        }

        public override void OnCancel(ICallContext? ctx) => Cancelled = true;
    }

    [Fact]
    public async Task Producer_ReadsUntilServerFinishes_AndReleasesConnection()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IStreamingService), new StreamingService());
        await using var client = new RpcClient(clientTransport);
        using var parameters = ValueRow("count", 2);
        var serveTask = server.ServeOneAsync(serverTransport);

        await using (var stream = await client.OpenProducerAsync("count", parameters))
        {
            using var first = (await stream.ReadNextAsync())!.Batch;
            using var second = (await stream.ReadNextAsync())!.Batch;
            Assert.Equal(0, ((Int64Array)first.Column(0)).GetValue(0));
            Assert.Equal(1, ((Int64Array)second.Column(0)).GetValue(0));
            Assert.Null(await stream.ReadNextAsync());
        }

        Assert.True(await serveTask);
    }

    [Fact]
    public async Task Exchange_RoundTripsRawBatches()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IStreamingService), new StreamingService());
        await using var client = new RpcClient(clientTransport);
        using var parameters = new RecordBatch(new Schema([], null), [], 1);
        var serveTask = server.ServeOneAsync(serverTransport);

        await using (var stream = await client.OpenExchangeAsync("double", parameters, s_valuesSchema))
        {
            using var input = Batch(21);
            using var response = (await stream.ExchangeAsync(input))!.Batch;
            Assert.Equal(42, ((Int64Array)response.Column(0)).GetValue(0));
        }

        Assert.True(await serveTask);
    }

    [Fact]
    public async Task Cancel_CompletesBothDirections_AndNotifiesServerState()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var implementation = new StreamingService();
        var server = new RpcServer(typeof(IStreamingService), implementation);
        await using var client = new RpcClient(clientTransport);
        using var parameters = new RecordBatch(new Schema([], null), [], 1);
        var serveTask = server.ServeOneAsync(serverTransport);

        await using (var stream = await client.OpenProducerAsync("until_cancelled", parameters))
        {
            await stream.CancelAsync();
        }

        Assert.True(await serveTask);
        Assert.True(implementation.LastCancelAwareProducer!.Cancelled);
    }

    [Fact]
    public async Task Dispose_ClosesGracefullyWithoutNotifyingCancellation()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var implementation = new StreamingService();
        var server = new RpcServer(typeof(IStreamingService), implementation);
        await using var client = new RpcClient(clientTransport);
        using var parameters = new RecordBatch(new Schema([], null), [], 1);
        var serveTask = server.ServeOneAsync(serverTransport);

        await using (var stream = await client.OpenProducerAsync("until_cancelled", parameters))
        {
            using var first = (await stream.ReadNextAsync())!.Batch;
        }

        Assert.True(await serveTask);
        Assert.False(implementation.LastCancelAwareProducer!.Cancelled);
    }

    [Fact]
    public async Task TypedProxy_InfersProducerAndExchangeFromReturnTypes()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IStreamingService), new StreamingService());
        await using var client = new RpcClient(clientTransport);
        var proxy = client.CreateProxy<IStreamingClient>();

        var producerServeTask = server.ServeOneAsync(serverTransport);
        await using (var producer = await proxy.CountAsync(1, TestContext.Current.CancellationToken))
        {
            using var item = (await producer.ReadNextAsync())!.Batch;
            Assert.Equal(0, ((Int64Array)item.Column(0)).GetValue(0));
            Assert.Null(await producer.ReadNextAsync());
        }

        Assert.True(await producerServeTask);

        var exchangeServeTask = server.ServeOneAsync(serverTransport);
        await using (var exchange = await proxy.DoubleAsync(TestContext.Current.CancellationToken))
        {
            using var response = (await exchange.ExchangeAsync(new ValueInput { Value = 9 }))!.Batch;
            Assert.Equal(18, ((Int64Array)response.Column(0)).GetValue(0));
        }

        Assert.True(await exchangeServeTask);
    }

    private static RecordBatch ValueRow(string name, long value)
    {
        var schema = new Schema([new Field(name, Int64Type.Default, false)], null);
        return new RecordBatch(schema, [new Int64Array.Builder().Append(value).Build()], 1);
    }

    private static RecordBatch Batch(long value) =>
        new(s_valuesSchema, [new Int64Array.Builder().Append(value).Build()], 1);
}
