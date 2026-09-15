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
        public const string SessionLost = "session_lost";
        public const string ServerDraining = "server_draining";
    }
}
