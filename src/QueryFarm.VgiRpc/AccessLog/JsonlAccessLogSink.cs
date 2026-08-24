using System.Text.Json;

namespace QueryFarm.VgiRpc.AccessLog;

/// <summary>
/// Appends one JSON line per <see cref="AccessLogRecord"/> to a file — the wire format
/// vgi_rpc/access_log.schema.json (canonical Python repo) describes. Thread-safe: concurrent
/// connections may log at once.
/// </summary>
public sealed class JsonlAccessLogSink : IAccessLogSink, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public bool IncludeRequestData { get; }

    /// <param name="path">File to append JSONL records to.</param>
    /// <param name="debug">
    /// The <c>--access-log-debug</c> gate — see <see cref="IAccessLogSink.IncludeRequestData"/>.
    /// </param>
    public JsonlAccessLogSink(string path, bool debug = false)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        IncludeRequestData = debug;
    }

    public void Write(AccessLogRecord record)
    {
        var fields = new Dictionary<string, object?>
        {
            ["timestamp"] = record.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["level"] = "INFO",
            ["logger"] = "vgi_rpc.access",
            ["message"] = $"{record.Method} {record.Status}",
            ["server_id"] = record.ServerId,
            ["protocol"] = record.Protocol,
            ["protocol_hash"] = record.ProtocolHash,
            ["method"] = record.Method,
            ["method_type"] = record.MethodType,
            ["principal"] = record.Principal,
            ["auth_domain"] = record.AuthDomain,
            ["authenticated"] = record.Authenticated,
            ["remote_addr"] = record.RemoteAddr,
            ["duration_ms"] = record.DurationMs,
            ["status"] = record.Status,
            ["error_type"] = record.ErrorType,
        };

        if (record.ErrorMessage is not null)
        {
            fields["error_message"] = record.ErrorMessage;
        }

        if (record.ServerVersion is not null)
        {
            fields["server_version"] = record.ServerVersion;
        }

        if (record.RequestId is not null)
        {
            fields["request_id"] = record.RequestId;
        }

        if (record.StreamId is not null)
        {
            fields["stream_id"] = record.StreamId;
        }

        if (record.RequestData is not null)
        {
            fields["request_data"] = record.RequestData;
        }

        if (record.Truncated is not null)
        {
            fields["truncated"] = record.Truncated;
        }

        if (record.OriginalRequestBytes is not null)
        {
            fields["original_request_bytes"] = record.OriginalRequestBytes;
        }

        var json = JsonSerializer.Serialize(fields);
        lock (_lock)
        {
            _writer.WriteLine(json);
        }
    }

    public void Dispose() => _writer.Dispose();
}
