using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Conformance.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>Output schema shared by every producer state below — mirrors Python's <c>_COUNTER_SCHEMA</c>.</summary>
public static class ConformanceStreamSchemas
{
    public static readonly Schema Counter = new(
        [new Field("index", Int64Type.Default, nullable: false), new Field("value", Int64Type.Default, nullable: false)],
        metadata: null);

    public static RecordBatch CounterRow(long index) =>
        ValueCodec.BuildRow(Counter, [index, index * 10]);
}

/// <summary>Produces <c>count</c> {index, value} batches, mirroring Python's <c>CounterState</c>.</summary>
public sealed class CounterState(long count) : ProducerState
{
    private long _current;

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (_current >= count)
        {
            output.Finish();
            return Task.CompletedTask;
        }

        output.Emit(ConformanceStreamSchemas.CounterRow(_current));
        _current++;
        return Task.CompletedTask;
    }
}

/// <summary>Emits one large {index, value} batch of <c>rowsPerBatch</c> rows, then finishes on
/// the next turn. Used by HTTP-only conformance tests to deliberately overshoot the operator-
/// configured response cap in a single producer iteration — the single-batch shape ensures the
/// overshoot happens before any continuation-token boundary. Mirrors <c>OversizedBatchState</c>.</summary>
public sealed class OversizedProducerState(long rowsPerBatch) : ProducerState
{
    private bool _emitted;

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (_emitted)
        {
            output.Finish();
            return Task.CompletedTask;
        }

        var indexBuilder = new Int64Array.Builder();
        var valueBuilder = new Int64Array.Builder();
        for (var i = 0L; i < rowsPerBatch; i++)
        {
            indexBuilder.Append(i);
            valueBuilder.Append(i * 10);
        }

        output.Emit(new RecordBatch(ConformanceStreamSchemas.Counter, [indexBuilder.Build(), valueBuilder.Build()], checked((int)rowsPerBatch)));
        _emitted = true;
        return Task.CompletedTask;
    }
}

/// <summary>Finishes immediately — zero batches. Mirrors <c>EmptyProducerState</c>.</summary>
public sealed class EmptyProducerState : ProducerState
{
    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        output.Finish();
        return Task.CompletedTask;
    }
}

/// <summary>Emits exactly one batch, then finishes. Mirrors <c>SingleProducerState</c>.</summary>
public sealed class SingleProducerState : ProducerState
{
    private bool _emitted;

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (_emitted)
        {
            output.Finish();
            return Task.CompletedTask;
        }

        _emitted = true;
        output.Emit(ConformanceStreamSchemas.CounterRow(0));
        return Task.CompletedTask;
    }
}

/// <summary>Emits an INFO log before each batch. Mirrors <c>LoggingProducerState</c>.</summary>
public sealed class LoggingProducerState(long count) : ProducerState
{
    private long _current;

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (_current >= count)
        {
            output.Finish();
            return Task.CompletedTask;
        }

        output.ClientLog(VgiLogLevel.Info, $"producing batch {_current}");
        output.Emit(ConformanceStreamSchemas.CounterRow(_current));
        _current++;
        return Task.CompletedTask;
    }
}

/// <summary>Output schema for <c>produce_tick_metadata</c> — mirrors the inline schema
/// (<c>{index: int64, seen: string}</c>) Python's <c>_impl.produce_tick_metadata</c> builds.</summary>
public static class TickMetadataSchemas
{
    public static readonly Schema Output = new(
        [new Field("index", Int64Type.Default, nullable: false), new Field("seen", StringType.Default, nullable: false)],
        metadata: null);
}

/// <summary>
/// Reports the application metadata attached to each producer tick — implements
/// <see cref="StreamState.ProcessAsync"/> directly (rather than <see cref="ProducerState"/>)
/// since it needs the input batch's custom_metadata, which <see cref="ProducerState"/> discards.
/// Mirrors Python's <c>TickMetadataState</c>.
/// </summary>
public sealed class TickMetadataState(long count) : StreamState
{
    // Matches Python's vgi.conformance.tick — see _test_tick_metadata in the canonical repo's
    // conformance test runner.
    private const string TickMetadataKey = "vgi.conformance.tick";

    private long _current;

    public override Task ProcessAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        var seen = input.GetMetadata(TickMetadataKey) ?? "";
        output.Emit(ValueCodec.BuildRow(TickMetadataSchemas.Output, [_current, seen]));
        _current++;
        if (_current >= count)
        {
            output.Finish();
        }

        return Task.CompletedTask;
    }
}

/// <summary>Raises after emitting <c>emitBeforeError</c> batches. Mirrors <c>ErrorAfterNState</c>.</summary>
public sealed class ErrorAfterNState(long emitBeforeError) : ProducerState
{
    private long _current;

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (_current >= emitBeforeError)
        {
            throw new RuntimeError($"intentional error after {emitBeforeError} batches");
        }

        output.Emit(ConformanceStreamSchemas.CounterRow(_current));
        _current++;
        return Task.CompletedTask;
    }
}
