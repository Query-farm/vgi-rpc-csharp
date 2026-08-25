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

    Task<Point?> EchoOptionalPointAsync(Point? point);

    // Python declares these two as `Annotated[int | None, ArrowType(pa.int32())]` and
    // `Annotated[int, ArrowType(pa.int32(), nullable=False)] | None` respectively — testing
    // Optional-vs-Annotated resolution order that has no distinct C# equivalent (a nullable CLR
    // value type already maps to a nullable Arrow field regardless of where "the" nullability
    // marker sits), so both are simply nullable-int32 echoes on the wire.
    Task<int?> EchoAnnotatedOptionalIntAsync(int? value);

    Task<int?> EchoOuterOptionalNonNullAsync(int? value);

    // -- Dataclass round-trip ------------------------------------------------

    Task<Point> EchoPointAsync(Point point);

    Task<BoundingBox> EchoBoundingBoxAsync(BoundingBox box);

    Task<string> InspectPointAsync(Point point);

    Task<AllTypes> EchoAllTypesAsync(AllTypes data);

    // -- Annotated types -----------------------------------------------------

    Task<int> EchoInt32Async(int value);

    Task<float> EchoFloat32Async(float value);

    // -- Wide Arrow types (M2, continued) -------------------------------------

    Task<sbyte> EchoInt8Async(sbyte value);

    Task<short> EchoInt16Async(short value);

    Task<byte> EchoUint8Async(byte value);

    Task<ushort> EchoUint16Async(ushort value);

    Task<uint> EchoUint32Async(uint value);

    Task<ulong> EchoUint64Async(ulong value);

    Task<DateOnly> EchoDateAsync(DateOnly value);

    /// <summary>A naive (no-offset) timestamp — <see cref="DateTime"/>, matching Python's naive
    /// <c>datetime.datetime</c> (<c>pa.timestamp("us")</c>, no tz).</summary>
    Task<DateTime> EchoTimestampAsync(DateTime value);

    /// <summary>A UTC-tagged timestamp — <see cref="DateTimeOffset"/>, matching
    /// <c>pa.timestamp("us", tz="UTC")</c>.</summary>
    Task<DateTimeOffset> EchoTimestampUtcAsync(DateTimeOffset value);

    Task<TimeOnly> EchoTimeAsync(TimeOnly value);

    Task<TimeSpan> EchoDurationAsync(TimeSpan value);

    Task<decimal> EchoDecimalAsync(decimal value);

    // -- HTTP response-cap conformance (M7) -----------------------------------

    /// <summary>Returns a bytes payload of approximately <paramref name="targetBytes"/> bytes —
    /// used by HTTP-only conformance tests to deliberately overshoot the operator-configured
    /// <c>max_response_bytes</c> so strict-fail behavior can be verified.</summary>
    Task<byte[]> OversizedUnaryAsync(long targetBytes);

    // -- External storage (M13) ------------------------------------------------

    /// <summary>Echoes <paramref name="value"/> — the same shape as <see cref="EchoStringAsync"/>,
    /// but wire-named <c>echo_large_string</c> to match the canonical Python method conformance
    /// tests drive with genuinely large payloads to trigger server-response externalization. The
    /// reference declares this over <c>pa.large_string()</c> (64-bit offsets); this port has no
    /// attribute-based Arrow-type-width override yet (see docs/roadmap.md), so it reuses the
    /// default <c>Utf8Type</c> (32-bit offsets) — functionally equivalent for every payload size
    /// the external-storage conformance suite actually exercises (tens of KB).</summary>
    Task<string> EchoLargeStringAsync(string value);

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

    Task<RpcStream<StreamState>> ProduceTickMetadataAsync(long count);

    Task<RpcStream<StreamState>> ProduceEmptyAsync();

    Task<RpcStream<StreamState>> ProduceSingleAsync();

    Task<RpcStream<StreamState>> ProduceWithLogsAsync(long count);

    Task<RpcStream<StreamState>> ProduceErrorMidStreamAsync(long emitBeforeError);

    /// <summary>Emits one batch of <paramref name="rowsPerBatch"/> {index, value} rows, then
    /// finishes — used by HTTP-only conformance tests (M13's <c>TestExternalizedResponseCap</c>
    /// and M7's <c>TestHttpResponseCapSoftWire</c>) to deliberately overshoot the operator-
    /// configured response cap for a single producer turn. The single-batch shape ensures the
    /// overshoot happens before any continuation-token boundary.</summary>
    Task<RpcStream<StreamState>> ProduceOversizedBatchAsync(long rowsPerBatch);

    // -- Exchange streams --------------------------------------------------

    Task<RpcStream<StreamState>> ExchangeScaleAsync(double factor);

    Task<RpcStream<StreamState>> ExchangeAccumulateAsync();

    Task<RpcStream<StreamState>> ExchangeWithLogsAsync();

    Task<RpcStream<StreamState>> ExchangeErrorOnNthAsync(long failOn);

    Task<RpcStream<StreamState>> ExchangeCastCompatibleAsync();

    /// <summary>Companion to <see cref="OversizedUnaryAsync"/> for the lockstep exchange path —
    /// emits <paramref name="rowsPerBatch"/> rows for any input, sized to overshoot the response cap.</summary>
    Task<RpcStream<StreamState>> ExchangeOversizedAsync(long rowsPerBatch);

    // -- Cancellation ---------------------------------------------------------

    Task<RpcStream<StreamState>> CancellableProducerAsync();

    Task<RpcStream<StreamState>> CancellableExchangeAsync();

    Task<List<long>> CancelProbeCountersAsync();

    Task ResetCancelProbeAsync();

    // -- Stream headers -------------------------------------------------------

    Task<RpcStream<StreamState>> ProduceWithHeaderAsync(long count);

    Task<RpcStream<StreamState>> ProduceWithHeaderAndLogsAsync(long count, ICallContext? ctx = null);

    Task<RpcStream<StreamState>> ExchangeWithHeaderAsync(double factor);

    Task<RpcStream<StreamState>> ProduceWithRichHeaderAsync(long seed, long count);

    Task<RpcStream<StreamState>> ExchangeWithRichHeaderAsync(long seed, double factor);

    Task<RpcStream<StreamState>> ProduceDynamicSchemaAsync(long seed, long count, bool includeStrings, bool includeFloats);

    // -- Sticky Sessions (HTTP-only; capability-gated tests — see docs/roadmap.md M10) ---------
    //
    // Wire-protocol exercise for the sticky-session feature. The three unary methods together
    // prove the open / resume / close lifecycle: open_counter registers a server-side counter via
    // ctx.OpenSession, increment_counter mutates it through the session token, close_counter
    // tears it down. Servers without sticky support throw InvalidOperationException when
    // ctx.OpenSession is invoked (ICallContext's default implementation — see
    // src/QueryFarm.VgiRpc/Server/ICallContext.cs), and the capability-gated TestSticky
    // conformance group skips them entirely.

    /// <summary>Opens a sticky session holding a counter; returns its initial value.</summary>
    Task<long> OpenCounterAsync(long initial, ICallContext? ctx = null);

    /// <summary>Increments the sticky session's counter; returns the post-increment value.
    /// Requires a session opened by <see cref="OpenCounterAsync"/>; raises a plain
    /// <c>RuntimeError</c> if no session is bound.</summary>
    Task<long> IncrementCounterAsync(long by, ICallContext? ctx = null);

    /// <summary>Closes the sticky session; returns the counter's final value before close.</summary>
    Task<long> CloseCounterAsync(ICallContext? ctx = null);

    // -- Sticky Sessions — Streaming (HTTP-only; capability-gated tests) -----------------------
    //
    // Producer + exchange streams that resume the sticky-session counter opened by
    // OpenCounterAsync. Each iteration is its own HTTP request, so these exercise the sticky
    // middleware on every turn — proving the session contract holds across the multi-request
    // shape of streaming RPCs, not just unary calls.

    /// <summary>Emits <paramref name="count"/> increments of the sticky session counter via a
    /// producer stream. Each emitted batch carries the post-increment value of the counter bound
    /// via <see cref="ICallContext.Session"/>.</summary>
    Task<RpcStream<StreamState>> StreamSessionCounterAsync(long count);

    /// <summary>Exchange stream adding each input <c>by</c> column to the sticky session counter.
    /// Each turn emits a single one-row batch with the post-update counter value.</summary>
    Task<RpcStream<StreamState>> ExchangeSessionCounterAsync();

    // TODO (later milestones — see docs/roadmap.md):
    //   - echo_wide_types / echo_container_wide_types / echo_embedded_arrow / echo_deep_nested /
    //     pack_nested_containers / echo_dict_encoded_string / echo_status_list (need an
    //     embedded-RecordBatch-as-field mechanism, a Rust/HashSet-style set type, and a
    //     dictionary-encoding attribute override — bigger, separate work; see docs/roadmap.md)
    //   - echo_large_binary/echo_fixed_binary (need an attribute-based Arrow type override —
    //     byte[] already means the default-width binary; unlike the int8..uint64/date/timestamp/
    //     time/duration/decimal widths above, there's no distinct CLR type to hang the wider
    //     variant off of; echo_large_string is ported above reusing plain Utf8Type)
    //   - produce_large_batches / produce_error_on_init
    //   - large_payload.* (2GiB+ transport-level gap — M4)
}
