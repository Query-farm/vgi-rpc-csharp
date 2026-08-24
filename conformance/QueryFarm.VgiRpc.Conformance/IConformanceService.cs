using QueryFarm.VgiRpc.Conformance.Types;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;

namespace QueryFarm.VgiRpc.Conformance;

/// <summary>
/// A C# port of <c>vgi_rpc.conformance._protocol.ConformanceService</c> — method names,
/// parameter names, and wire type shapes must match the Python reference exactly (that IS the
/// conformance contract; see docs/roadmap.md M2). This first pass covers the unary methods
/// reachable with the current <c>ValueCodec</c> (scalars, string/bytes, enum, list/nested-list,
/// map, optional, struct-of-scalars, client logging). Streaming methods, the wide-Arrow-type
/// methods (temporal/decimal/large_string/fixed_size_binary), <c>AllTypes</c>/<c>WideTypes</c>/
/// <c>ContainerWideTypes</c>/<c>DeepNested</c>/<c>EmbeddedArrow</c> (which need
/// list-of-struct/list-of-decimal support <c>ValueCodec</c> doesn't have yet), and HTTP/SHM/
/// cancellation-specific methods are deferred — see the TODOs below and docs/roadmap.md.
/// </summary>
public interface IConformanceService
{
    // -- Scalar echo -----------------------------------------------------

    Task<string> EchoStringAsync(string value);

    Task<byte[]> EchoBytesAsync(byte[] data);

    Task<long> EchoIntAsync(long value);

    Task<double> EchoFloatAsync(double value);

    Task<bool> EchoBoolAsync(bool value);

    // -- Void --------------------------------------------------------------

    Task VoidNoopAsync();

    Task VoidWithParamAsync(long value);

    // -- Complex type echo ---------------------------------------------------

    Task<Status> EchoEnumAsync(Status status);

    Task<List<string>> EchoListAsync(List<string> values);

    Task<Dictionary<string, long>> EchoDictAsync(Dictionary<string, long> mapping);

    Task<List<List<long>>> EchoNestedListAsync(List<List<long>> matrix);

    // -- Optional/nullable -------------------------------------------------

    Task<string?> EchoOptionalStringAsync(string? value);

    Task<long?> EchoOptionalIntAsync(long? value);

    // -- Dataclass round-trip ------------------------------------------------

    Task<Point> EchoPointAsync(Point point);

    Task<BoundingBox> EchoBoundingBoxAsync(BoundingBox box);

    Task<string> InspectPointAsync(Point point);

    // -- Annotated types -----------------------------------------------------

    Task<int> EchoInt32Async(int value);

    Task<float> EchoFloat32Async(float value);

    // -- Multi-param & defaults ---------------------------------------------

    Task<double> AddFloatsAsync(double a, double b);

    Task<string> ConcatenateAsync(string prefix, string suffix, string separator);

    Task<string> WithDefaultsAsync(long required, string optionalStr, long optionalInt);

    // -- Error propagation -----------------------------------------------------

    Task<string> RaiseValueErrorAsync(string message);

    Task<string> RaiseRuntimeErrorAsync(string message);

    Task<string> RaiseTypeErrorAsync(string message);

    // -- Client-directed logging ----------------------------------------------

    Task<string> EchoWithInfoLogAsync(string value, ICallContext? ctx = null);

    Task<string> EchoWithMultiLogsAsync(string value, ICallContext? ctx = null);

    Task<string> EchoWithLogExtrasAsync(string value, ICallContext? ctx = null);

    Task<string> EchoWithAllLogLevelsAsync(string value, ICallContext? ctx = null);

    // -- Producer streams ------------------------------------------------------

    // Declared as Task<RpcStream<StreamState>> (the base state type) rather than each
    // method's own concrete state type — mirrors Python's Protocol declaring `Stream[StreamState]`
    // loosely while each impl method's concrete return narrows it; C# needs one shared
    // interface-level type since (unlike Python duck typing) it enforces exact signature
    // matching between interface and implementation, and RpcStream<TState> isn't covariant.
    Task<RpcStream<StreamState>> ProduceNAsync(long count);

    Task<RpcStream<StreamState>> ProduceEmptyAsync();

    Task<RpcStream<StreamState>> ProduceSingleAsync();

    Task<RpcStream<StreamState>> ProduceWithLogsAsync(long count);

    Task<RpcStream<StreamState>> ProduceErrorMidStreamAsync(long emitBeforeError);

    // -- Exchange streams --------------------------------------------------

    Task<RpcStream<StreamState>> ExchangeScaleAsync(double factor);

    Task<RpcStream<StreamState>> ExchangeAccumulateAsync();

    Task<RpcStream<StreamState>> ExchangeWithLogsAsync();

    Task<RpcStream<StreamState>> ExchangeErrorOnNthAsync(long failOn);

    // -- Cancellation ---------------------------------------------------------

    Task<RpcStream<StreamState>> CancellableProducerAsync();

    Task<RpcStream<StreamState>> CancellableExchangeAsync();

    Task<List<long>> CancelProbeCountersAsync();

    Task ResetCancelProbeAsync();

    // -- Stream headers -------------------------------------------------------

    Task<RpcStream<StreamState>> ProduceWithHeaderAsync(long count);

    Task<RpcStream<StreamState>> ProduceWithHeaderAndLogsAsync(long count, ICallContext? ctx = null);

    Task<RpcStream<StreamState>> ExchangeWithHeaderAsync(double factor);

    // TODO (later milestones — see docs/roadmap.md):
    //   - oversized_unary (HTTP response-cap conformance)
    //   - echo_all_types / echo_all_types_with_nulls / echo_wide_types /
    //     echo_container_wide_types / echo_embedded_arrow / echo_deep_nested /
    //     echo_dict_encoded_string (need list-of-struct + wide Arrow type support)
    //   - echo_int8/int16/uint8/uint16/uint32/uint64/date/timestamp/timestamp_utc/time/
    //     duration/decimal/large_string/large_binary/fixed_binary (wide Arrow types)
    //   - produce_large_batches / *_header / rich_header_* / dynamic_schema_producer / cancel_*
    //   - exchange_* (Milestone 3, continued)
}
