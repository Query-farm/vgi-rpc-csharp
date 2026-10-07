namespace QueryFarm.VgiRpc.AccessLog;

/// <summary>
/// One record on the <c>vgi_rpc.access</c> logger — see the canonical Python repo's
/// docs/access-log-spec.md and vgi_rpc/access_log.schema.json for the full cross-language
/// contract. Covers the schema's <c>required</c> fields plus the handful of optional ones this
/// port currently has data for; auth/session/sticky fields land alongside their own milestones.
/// </summary>
public sealed record AccessLogRecord(
    DateTimeOffset Timestamp,
    string ServerId,
    string Protocol,
    string ProtocolHash,
    string Method,
    string MethodType, // "unary" | "stream"
    string Status, // "ok" | "error"
    double DurationMs,
    string ErrorType = "",
    string? ErrorMessage = null,
    string Principal = "",
    string AuthDomain = "",
    bool Authenticated = false,
    string RemoteAddr = "",
    string? ServerVersion = null,
    string? RequestId = null,
    // Required by access_log.schema.json whenever MethodType is "stream" — a per-call
    // correlation id (32 lowercase hex chars), matching Python's uuid.uuid4().hex.
    string? StreamId = null,
    // The request's shape -- parameter names and Arrow types, and the row count -- on unary and
    // stream-init records. Never a value: see RequestShape.
    IReadOnlyList<AccessLogRequestField>? RequestFields = null,
    long? RequestRows = null,
    // "payload_omitted" on unary records. Transitional: the released vgi-rpc 0.50.0 schema
    // requires request_data on a unary record unless it is marked truncated, and the newer
    // schema accepts the marker as legacy. Remove once CI validates against vgi-rpc >= 0.50.1.
    string? Truncated = null,
    // Sizes of the HTTP stream state tokens received / returned on a turn. The tokens themselves
    // are never logged: they are replayable.
    long? RequestStateBytes = null,
    long? ResponseStateBytes = null,
    // The canonical code (WIRE_PROTOCOL.md §8) on status "error" records -- what an operator
    // alerts on. Null on success, where the schema forbids it.
    string? ErrorCode = null);
