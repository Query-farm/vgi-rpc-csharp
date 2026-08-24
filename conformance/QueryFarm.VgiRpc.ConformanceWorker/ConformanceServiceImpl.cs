using System.Globalization;
using QueryFarm.VgiRpc.Conformance;
using QueryFarm.VgiRpc.Conformance.Errors;
using QueryFarm.VgiRpc.Conformance.Types;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>A C# port of <c>vgi_rpc.conformance._impl</c> for the methods <see cref="IConformanceService"/> declares.</summary>
public sealed class ConformanceServiceImpl : IConformanceService
{
    public Task<string> EchoStringAsync(string value) => Task.FromResult(value);

    public Task<byte[]> EchoBytesAsync(byte[] data) => Task.FromResult(data);

    public Task<long> EchoIntAsync(long value) => Task.FromResult(value);

    public Task<double> EchoFloatAsync(double value) => Task.FromResult(value);

    public Task<bool> EchoBoolAsync(bool value) => Task.FromResult(value);

    public Task VoidNoopAsync() => Task.CompletedTask;

    public Task VoidWithParamAsync(long value) => Task.CompletedTask;

    public Task<Status> EchoEnumAsync(Status status) => Task.FromResult(status);

    public Task<List<string>> EchoListAsync(List<string> values) => Task.FromResult(values);

    public Task<Dictionary<string, long>> EchoDictAsync(Dictionary<string, long> mapping) => Task.FromResult(mapping);

    public Task<List<List<long>>> EchoNestedListAsync(List<List<long>> matrix) => Task.FromResult(matrix);

    public Task<string?> EchoOptionalStringAsync(string? value) => Task.FromResult(value);

    public Task<long?> EchoOptionalIntAsync(long? value) => Task.FromResult(value);

    public Task<Point?> EchoOptionalPointAsync(Point? point) => Task.FromResult(point);

    public Task<int?> EchoAnnotatedOptionalIntAsync(int? value) => Task.FromResult(value);

    public Task<int?> EchoOuterOptionalNonNullAsync(int? value) => Task.FromResult(value);

    public Task<Point> EchoPointAsync(Point point) => Task.FromResult(point);

    public Task<BoundingBox> EchoBoundingBoxAsync(BoundingBox box) => Task.FromResult(box);

    public Task<AllTypes> EchoAllTypesAsync(AllTypes data) => Task.FromResult(data);

    public Task<string> InspectPointAsync(Point point) =>
        Task.FromResult($"Point({point.X.ToString(CultureInfo.InvariantCulture)}, {point.Y.ToString(CultureInfo.InvariantCulture)})");

    public Task<int> EchoInt32Async(int value) => Task.FromResult(value);

    public Task<float> EchoFloat32Async(float value) => Task.FromResult(value);

    public Task<sbyte> EchoInt8Async(sbyte value) => Task.FromResult(value);

    public Task<short> EchoInt16Async(short value) => Task.FromResult(value);

    public Task<byte> EchoUint8Async(byte value) => Task.FromResult(value);

    public Task<ushort> EchoUint16Async(ushort value) => Task.FromResult(value);

    public Task<uint> EchoUint32Async(uint value) => Task.FromResult(value);

    public Task<ulong> EchoUint64Async(ulong value) => Task.FromResult(value);

    public Task<DateOnly> EchoDateAsync(DateOnly value) => Task.FromResult(value);

    public Task<DateTime> EchoTimestampAsync(DateTime value) => Task.FromResult(value);

    public Task<DateTimeOffset> EchoTimestampUtcAsync(DateTimeOffset value) => Task.FromResult(value);

    public Task<TimeOnly> EchoTimeAsync(TimeOnly value) => Task.FromResult(value);

    public Task<TimeSpan> EchoDurationAsync(TimeSpan value) => Task.FromResult(value);

    public Task<decimal> EchoDecimalAsync(decimal value) => Task.FromResult(value);

    public Task<byte[]> OversizedUnaryAsync(long targetBytes)
    {
        if (targetBytes < 0)
        {
            throw new ValueError("target_bytes must be non-negative");
        }

        return Task.FromResult(new byte[targetBytes]);
    }

    public Task<double> AddFloatsAsync(double a, double b) => Task.FromResult(a + b);

    public Task<string> ConcatenateAsync(string prefix, string suffix, string separator) =>
        Task.FromResult($"{prefix}{separator}{suffix}");

    public Task<string> WithDefaultsAsync(long required, string optionalStr, long optionalInt) =>
        Task.FromResult($"required={required}, optional_str={optionalStr}, optional_int={optionalInt}");

    public Task<string> RaiseValueErrorAsync(string message) => throw new ValueError(message);

    public Task<string> RaiseRuntimeErrorAsync(string message) => throw new RuntimeError(message);

    public Task<string> RaiseTypeErrorAsync(string message) => throw new TypeError(message);

    public Task<string> EchoWithInfoLogAsync(string value, ICallContext? ctx = null)
    {
        ctx!.EmitLog(VgiLogLevel.Info, value);
        return Task.FromResult(value);
    }

    public Task<string> EchoWithMultiLogsAsync(string value, ICallContext? ctx = null)
    {
        ctx!.EmitLog(VgiLogLevel.Debug, value);
        ctx.EmitLog(VgiLogLevel.Info, value);
        ctx.EmitLog(VgiLogLevel.Warn, value);
        return Task.FromResult(value);
    }

    public Task<string> EchoWithLogExtrasAsync(string value, ICallContext? ctx = null)
    {
        ctx!.EmitLog(VgiLogLevel.Info, value, new Dictionary<string, object?> { ["source"] = "conformance", ["detail"] = value });
        return Task.FromResult(value);
    }

    public Task<string> EchoWithAllLogLevelsAsync(string value, ICallContext? ctx = null)
    {
        ctx!.EmitLog(VgiLogLevel.Trace, value);
        ctx.EmitLog(VgiLogLevel.Debug, value);
        ctx.EmitLog(VgiLogLevel.Info, value);
        ctx.EmitLog(VgiLogLevel.Warn, value);
        ctx.EmitLog(VgiLogLevel.Error, value);
        ctx.EmitLog(VgiLogLevel.Exception, value);
        return Task.FromResult(value);
    }

    public Task<RpcStream<StreamState>> ProduceNAsync(long count) =>
        Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new CounterState(count)));

    public Task<RpcStream<StreamState>> ProduceTickMetadataAsync(long count) =>
        Task.FromResult(new RpcStream<StreamState>(TickMetadataSchemas.Output, new TickMetadataState(count)));

    public Task<RpcStream<StreamState>> ProduceEmptyAsync() =>
        Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new EmptyProducerState()));

    public Task<RpcStream<StreamState>> ProduceSingleAsync() =>
        Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new SingleProducerState()));

    public Task<RpcStream<StreamState>> ProduceWithLogsAsync(long count) =>
        Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new LoggingProducerState(count)));

    public Task<RpcStream<StreamState>> ProduceErrorMidStreamAsync(long emitBeforeError) =>
        Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new ErrorAfterNState(emitBeforeError)));

    public Task<RpcStream<StreamState>> ExchangeScaleAsync(double factor) =>
        Task.FromResult(new RpcStream<StreamState>(ExchangeSchemas.Scale, new ScaleExchangeState(factor), InputSchema: ExchangeSchemas.Scale));

    public Task<RpcStream<StreamState>> ExchangeAccumulateAsync() =>
        Task.FromResult(new RpcStream<StreamState>(ExchangeSchemas.Accumulate, new AccumulatingExchangeState(), InputSchema: ExchangeSchemas.Scale));

    public Task<RpcStream<StreamState>> ExchangeWithLogsAsync() =>
        Task.FromResult(new RpcStream<StreamState>(ExchangeSchemas.Scale, new LoggingExchangeState(), InputSchema: ExchangeSchemas.Scale));

    public Task<RpcStream<StreamState>> ExchangeErrorOnNthAsync(long failOn) =>
        Task.FromResult(new RpcStream<StreamState>(ExchangeSchemas.Scale, new FailOnExchangeNState(failOn), InputSchema: ExchangeSchemas.Scale));

    public Task<RpcStream<StreamState>> ExchangeCastCompatibleAsync() =>
        Task.FromResult(new RpcStream<StreamState>(ExchangeSchemas.Scale, new ScaleExchangeState(1.0), InputSchema: ExchangeSchemas.Scale));

    public Task<RpcStream<StreamState>> ExchangeOversizedAsync(long rowsPerBatch) =>
        Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new OversizedExchangeState(rowsPerBatch), InputSchema: ExchangeSchemas.Scale));

    public Task<RpcStream<StreamState>> CancellableProducerAsync() =>
        Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new CancellableProducerState()));

    public Task<RpcStream<StreamState>> CancellableExchangeAsync() =>
        Task.FromResult(new RpcStream<StreamState>(ExchangeSchemas.Scale, new CancellableExchangeState(), InputSchema: ExchangeSchemas.Scale));

    public Task<List<long>> CancelProbeCountersAsync() => Task.FromResult(CancelProbe.Snapshot());

    public Task ResetCancelProbeAsync()
    {
        CancelProbe.Reset();
        return Task.CompletedTask;
    }

    public Task<RpcStream<StreamState>> ProduceWithHeaderAsync(long count)
    {
        var header = new ConformanceHeader { TotalExpected = count, Description = $"producing {count} batches" };
        return Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new CounterState(count), Header: header));
    }

    public Task<RpcStream<StreamState>> ProduceWithHeaderAndLogsAsync(long count, ICallContext? ctx = null)
    {
        ctx!.EmitLog(VgiLogLevel.Info, "stream init log");
        var header = new ConformanceHeader { TotalExpected = count, Description = $"producing {count} with logs" };
        return Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new CounterState(count), Header: header));
    }

    public Task<RpcStream<StreamState>> ExchangeWithHeaderAsync(double factor)
    {
        // Python's str(float) always shows at least one decimal digit (2.0, not 2) — match
        // that so "2.0" in description-style conformance assertions see what they expect.
        var header = new ConformanceHeader { TotalExpected = 0, Description = $"scaling by {factor.ToString("0.0###############", CultureInfo.InvariantCulture)}" };
        return Task.FromResult(new RpcStream<StreamState>(ExchangeSchemas.Scale, new ScaleExchangeState(factor), InputSchema: ExchangeSchemas.Scale, Header: header));
    }

    public Task<RpcStream<StreamState>> ProduceWithRichHeaderAsync(long seed, long count) =>
        Task.FromResult(new RpcStream<StreamState>(ConformanceStreamSchemas.Counter, new CounterState(count), Header: RichHeaderBuilder.Build(seed)));

    public Task<RpcStream<StreamState>> ExchangeWithRichHeaderAsync(long seed, double factor) =>
        Task.FromResult(new RpcStream<StreamState>(ExchangeSchemas.Scale, new ScaleExchangeState(factor), InputSchema: ExchangeSchemas.Scale, Header: RichHeaderBuilder.Build(seed)));

    public Task<RpcStream<StreamState>> ProduceDynamicSchemaAsync(long seed, long count, bool includeStrings, bool includeFloats)
    {
        var schema = DynamicProducerState.BuildSchema(includeStrings, includeFloats);
        var state = new DynamicProducerState(schema, count, includeStrings, includeFloats);
        return Task.FromResult(new RpcStream<StreamState>(schema, state, Header: RichHeaderBuilder.Build(seed)));
    }
}
