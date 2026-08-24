using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Conformance.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;

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
