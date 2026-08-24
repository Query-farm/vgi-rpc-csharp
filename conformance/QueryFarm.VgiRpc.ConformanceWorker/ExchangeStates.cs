using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Conformance.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.ConformanceWorker;

public static class ExchangeSchemas
{
    public static readonly Schema Scale = new([new Field("value", DoubleType.Default, nullable: false)], metadata: null);

    public static readonly Schema Accumulate = new(
        [new Field("running_sum", DoubleType.Default, nullable: false), new Field("exchange_count", Int64Type.Default, nullable: false)],
        metadata: null);
}

/// <summary>Multiplies the input "value" column by <c>factor</c>. Mirrors <c>ScaleExchangeState</c>.</summary>
public sealed class ScaleExchangeState(double factor) : ExchangeState
{
    public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        var values = (DoubleArray)input.Batch.Column(0);
        var scaledArray = new DoubleArray.Builder().AppendRange(values.Values.ToArray().Select(v => v * factor)).Build();
        output.Emit(new RecordBatch(ExchangeSchemas.Scale, [scaledArray], values.Length));
        return Task.CompletedTask;
    }
}

/// <summary>Running sum + exchange count across exchanges. Mirrors <c>AccumulatingExchangeState</c>.</summary>
public sealed class AccumulatingExchangeState : ExchangeState
{
    private double _runningSum;
    private long _exchangeCount;

    public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        var values = (DoubleArray)input.Batch.Column(0);
        _runningSum += values.Values.ToArray().Sum();
        _exchangeCount++;
        output.Emit(ValueCodec.BuildRow(ExchangeSchemas.Accumulate, [_runningSum, _exchangeCount]));
        return Task.CompletedTask;
    }
}

/// <summary>INFO + DEBUG log per exchange, then echoes input. Mirrors <c>LoggingExchangeState</c>.</summary>
public sealed class LoggingExchangeState : ExchangeState
{
    public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        output.ClientLog(VgiLogLevel.Info, "exchange processing");
        output.ClientLog(VgiLogLevel.Debug, "exchange debug");
        output.Emit(input.Batch);
        return Task.CompletedTask;
    }
}

/// <summary>Emits <c>rowsPerBatch</c> {index, value} rows for any input, regardless of input
/// size — sized by the caller to deliberately overshoot the operator-configured response cap, so
/// HTTP strict-fail behaviour (see docs/roadmap.md M7) can be verified. Mirrors
/// <c>OversizedExchangeState</c>.</summary>
public sealed class OversizedExchangeState(long rowsPerBatch) : ExchangeState
{
    public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        var indexBuilder = new Int64Array.Builder();
        var valueBuilder = new Int64Array.Builder();
        for (var i = 0L; i < rowsPerBatch; i++)
        {
            indexBuilder.Append(i);
            valueBuilder.Append(i * 10);
        }

        output.Emit(new RecordBatch(ConformanceStreamSchemas.Counter, [indexBuilder.Build(), valueBuilder.Build()], checked((int)rowsPerBatch)));
        return Task.CompletedTask;
    }
}

/// <summary>Raises on the Nth exchange (1-indexed). Mirrors <c>FailOnExchangeNState</c>.</summary>
public sealed class FailOnExchangeNState(long failOn) : ExchangeState
{
    private long _exchangeCount;

    public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        _exchangeCount++;
        if (_exchangeCount >= failOn)
        {
            throw new RuntimeError($"intentional error on exchange {_exchangeCount}");
        }

        output.Emit(input.Batch);
        return Task.CompletedTask;
    }
}
