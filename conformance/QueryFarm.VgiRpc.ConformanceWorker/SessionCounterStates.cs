using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Conformance.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>The sticky-session state bound by <c>open_counter</c> — mirrors Python's
/// <c>_StickyCounter</c> dataclass. A plain mutable counter; nothing about it is HTTP- or
/// sticky-specific, which is the point (any handle-bearing object works the same way).</summary>
public sealed class StickyCounter(long value)
{
    public long Value { get; set; } = value;
}

/// <summary>Output schema shared by <c>stream_session_counter</c>/<c>exchange_session_counter</c>
/// — mirrors Python's <c>_SESSION_COUNTER_OUTPUT_SCHEMA</c> (<c>{value: int64}</c>).</summary>
public static class SessionCounterSchemas
{
    public static readonly Schema Output = new([new Field("value", Int64Type.Default, nullable: false)], metadata: null);

    public static readonly Schema ExchangeInput = new([new Field("by", Int64Type.Default, nullable: false)], metadata: null);
}

/// <summary>
/// Producer stream that emits the sticky-session counter <c>count</c> times, one increment per
/// batch — mirrors Python's <c>SessionCounterProducerState</c>. Each <see cref="ProduceAsync"/>
/// call resolves the counter via <c>ctx.Session</c>; since every producer turn is its own HTTP
/// request (see docs/roadmap.md M10), this proves the session survives the multi-request shape of
/// producer streams, not just unary calls.
/// </summary>
public sealed class SessionCounterProducerState(long count) : ProducerState
{
    private long _current;

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (_current >= count)
        {
            output.Finish();
            return Task.CompletedTask;
        }

        if (ctx?.Session is not StickyCounter counter)
        {
            throw new RuntimeError("no sticky counter bound to this request");
        }

        counter.Value++;
        output.Emit(ValueCodec.BuildRow(SessionCounterSchemas.Output, [counter.Value]));
        _current++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Exchange stream that adds each input batch's <c>by</c> column to the sticky session counter —
/// mirrors Python's <c>SessionCounterExchangeState</c>. Every exchange turn is its own HTTP
/// request, so the sticky middleware rebinds the same <see cref="StickyCounter"/> via
/// <c>ctx.Session</c> on each call.
/// </summary>
public sealed class SessionCounterExchangeState : ExchangeState
{
    public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (ctx?.Session is not StickyCounter counter)
        {
            throw new RuntimeError("no sticky counter bound to this request");
        }

        var byColumn = (Int64Array)input.Batch.Column(0);
        var sum = byColumn.Values.ToArray().Sum();
        counter.Value += sum;
        output.Emit(ValueCodec.BuildRow(SessionCounterSchemas.Output, [counter.Value]));
        return Task.CompletedTask;
    }
}
