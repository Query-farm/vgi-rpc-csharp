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

    public Task<Point> EchoPointAsync(Point point) => Task.FromResult(point);

    public Task<BoundingBox> EchoBoundingBoxAsync(BoundingBox box) => Task.FromResult(box);

    public Task<string> InspectPointAsync(Point point) =>
        Task.FromResult($"Point({point.X.ToString(CultureInfo.InvariantCulture)}, {point.Y.ToString(CultureInfo.InvariantCulture)})");

    public Task<int> EchoInt32Async(int value) => Task.FromResult(value);

    public Task<float> EchoFloat32Async(float value) => Task.FromResult(value);

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
}
