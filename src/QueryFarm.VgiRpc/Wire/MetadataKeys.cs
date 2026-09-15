namespace QueryFarm.VgiRpc.Wire;

/// <summary>
/// The <c>vgi_rpc.*</c> custom_metadata key namespace, mirroring
/// <c>vgi_rpc/metadata.py</c> in the canonical Python repo exactly (key strings must match
/// byte-for-byte across every language port — this is the actual cross-language contract).
/// </summary>
public static class MetadataKeys
{
    public const string Method = "vgi_rpc.method";

    /// <summary>Names the protocol a request addresses -- the routing key.</summary>
    /// <remarks>
    /// <para>
    /// Dispatch resolves the pair <c>(protocol, method)</c>: a server hosts one or more protocols
    /// and method names may collide across them, which is what lets protocols be authored
    /// independently. Required on every request, including against a server hosting exactly one
    /// protocol -- an exemption would let an intermediary that rebuilds a request and drops the
    /// field land silently on whichever protocol happened to be first, rather than being told.
    /// </para>
    /// <para>
    /// The major version is part of the protocol name (<c>vgi_rpc.Reflection.v1</c>), so an
    /// incompatible major is a routing failure rather than a parse failure, and v1 and v2 can be
    /// served side by side while clients migrate.
    /// </para>
    /// </remarks>
    public const string Protocol = "vgi_rpc.protocol";
    public const string StreamState = "vgi_rpc.stream_state#b64";
    public const string CallState = "vgi_rpc.call_state#b64";
    public const string Cancel = "vgi_rpc.cancel";
    public const string LogLevel = "vgi_rpc.log_level";
    public const string LogMessage = "vgi_rpc.log_message";
    public const string LogExtra = "vgi_rpc.log_extra";
    public const string ErrorKind = "vgi_rpc.error_kind";
    public const string RequestVersion = "vgi_rpc.request_version";
    public const string CurrentRequestVersion = "1";
    public const string ServerId = "vgi_rpc.server_id";
    public const string RequestId = "vgi_rpc.request_id";
    public const string Location = "vgi_rpc.location";
    public const string LocationSha256 = "vgi_rpc.location.sha256";
    public const string LocationFetchMs = "vgi_rpc.location.fetch_ms";
    public const string LocationSource = "vgi_rpc.location.source";
    public const string ShmOffset = "vgi_rpc.shm_offset";
    public const string ShmLength = "vgi_rpc.shm_length";
    public const string ShmSource = "vgi_rpc.shm_source";
    public const string ShmSegmentName = "vgi_rpc.shm_segment_name";
    public const string ShmSegmentSize = "vgi_rpc.shm_segment_size";
    public const string TransportShm = "vgi_rpc.transport.shm";
    public const string ProtocolName = "vgi_rpc.protocol_name";
    public const string DescribeVersion = "vgi_rpc.describe_version";
    public const string ProtocolHash = "vgi_rpc.protocol_hash";
    public const string ProtocolVersion = "vgi_rpc.protocol_version";

    /// <summary>Stable error_kind tokens for the built-in exception types. Open set — never treat as a closed enum.</summary>
    public static class ErrorKinds
    {
        public const string MethodNotImplemented = "method_not_implemented";
        public const string ProtocolVersionMismatch = "protocol_version_mismatch";

        // Protocol routing (WIRE_PROTOCOL.md 3.1). Three distinct answers a client depends on
        // being able to tell apart: no routing key at all, a routing key naming a protocol this
        // server does not host, and a hosted protocol that lacks the method
        // (MethodNotImplemented above -- the documented capability-probe signal).
        public const string ProtocolNotSpecified = "protocol_not_specified";
        public const string ProtocolNotSupported = "protocol_not_supported";
        public const string SessionLost = "session_lost";
        public const string ServerDraining = "server_draining";

        // vgi_rpc.Identity.v1. These five are the whole definitive-vs-transient signal a caller
        // gets: as protocol methods (rather than the HTTP JSON route they replace) every handler
        // exception surfaces the same way, so the status code no longer carries the distinction.
        // See QueryFarm.VgiRpc.Identity.IdentityErrors.
        public const string IntrospectionRefused = "introspection_refused";
        public const string TokenUnresolved = "token_unresolved";
        public const string StaleAuth = "stale_auth";
        public const string GrantRefused = "grant_refused";
        public const string IdentityUnavailable = "identity_unavailable";
    }
}
