using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>Process-wide counters observed by cancel conformance tests. Mirrors <c>_CancelProbe</c>
/// (minus the cross-process shared-file mode, not needed for a single-worker pipe transport).</summary>
public static class CancelProbe
{
    private static long s_produceCalls;
    private static long s_exchangeCalls;
    private static long s_onCancelCalls;

    public static void BumpProduce() => Interlocked.Increment(ref s_produceCalls);

    public static void BumpExchange() => Interlocked.Increment(ref s_exchangeCalls);

    public static void BumpOnCancel() => Interlocked.Increment(ref s_onCancelCalls);

    public static List<long> Snapshot() => [s_produceCalls, s_exchangeCalls, s_onCancelCalls];

    public static void Reset()
    {
        Interlocked.Exchange(ref s_produceCalls, 0);
        Interlocked.Exchange(ref s_exchangeCalls, 0);
        Interlocked.Exchange(ref s_onCancelCalls, 0);
    }
}

/// <summary>Infinite producer that records cancel observations. Mirrors <c>CancellableProducerState</c>.</summary>
public sealed class CancellableProducerState : ProducerState
{
    private long _current;

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        CancelProbe.BumpProduce();
        output.Emit(ConformanceStreamSchemas.CounterRow(_current));
        _current++;
        return Task.CompletedTask;
    }

    public override void OnCancel(ICallContext? ctx) => CancelProbe.BumpOnCancel();
}

/// <summary>Echo exchange that records cancel observations. Mirrors <c>CancellableExchangeState</c>.</summary>
public sealed class CancellableExchangeState : ExchangeState
{
    public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        CancelProbe.BumpExchange();
        output.Emit(input.Batch);
        return Task.CompletedTask;
    }

    public override void OnCancel(ICallContext? ctx) => CancelProbe.BumpOnCancel();
}
