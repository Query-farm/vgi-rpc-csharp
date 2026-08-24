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
    // Required by access_log.schema.json whenever MethodType is "unary", unless Truncated is
    // set instead: a base64-encoded, self-contained Arrow IPC stream of the request batch (only
    // populated when the sink's IAccessLogSink.IncludeRequestData is true — the --access-log-debug
    // gate, mirroring Python's DEBUG-only capture of the same field).
    string? RequestData = null,
    // Set instead of RequestData at INFO (non-debug) level: "payload_omitted", paired with
    // OriginalRequestBytes so the schema's "unary requires request_data unless truncated"
    // invariant still holds without paying to base64-encode a payload nobody asked to see.
    string? Truncated = null,
    long? OriginalRequestBytes = null);
