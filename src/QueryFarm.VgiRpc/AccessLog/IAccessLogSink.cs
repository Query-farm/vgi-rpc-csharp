namespace QueryFarm.VgiRpc.AccessLog;

/// <summary>Receives one <see cref="AccessLogRecord"/> per completed RPC call.</summary>
public interface IAccessLogSink
{
    /// <summary>
    /// When <see langword="true"/>, unary calls' access-log records carry the full base64
    /// <see cref="AccessLogRecord.RequestData"/> payload; when <see langword="false"/> (the
    /// default posture), the record instead carries <see cref="AccessLogRecord.Truncated"/> =
    /// "payload_omitted" + <see cref="AccessLogRecord.OriginalRequestBytes"/>. Mirrors Python's
    /// "only at DEBUG" gate on the <c>vgi_rpc.access</c> logger — the CLI-facing knob is
    /// <c>--access-log-debug</c>.
    /// </summary>
    bool IncludeRequestData { get; }

    void Write(AccessLogRecord record);
}
