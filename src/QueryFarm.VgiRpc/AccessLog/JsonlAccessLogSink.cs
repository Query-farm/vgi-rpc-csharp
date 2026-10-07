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

    /// <param name="path">File to append JSONL records to.</param>
    public JsonlAccessLogSink(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
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

        if (record.Status == "error" && !string.IsNullOrEmpty(record.ErrorCode))
        {
            fields["error_code"] = record.ErrorCode;
        }

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

        if (record.RequestFields is not null)
        {
            fields["request_fields"] = record.RequestFields.Select(f => new Dictionary<string, string> { ["name"] = f.Name, ["type"] = f.Type }).ToList();
            fields["request_rows"] = record.RequestRows ?? 0;
        }

        if (record.Truncated is not null)
        {
            fields["truncated"] = record.Truncated;
        }

        if (record.RequestStateBytes is not null)
        {
            fields["request_state_bytes"] = record.RequestStateBytes;
        }

        if (record.ResponseStateBytes is not null)
        {
            fields["response_state_bytes"] = record.ResponseStateBytes;
        }

        var json = JsonSerializer.Serialize(fields);
        lock (_lock)
        {
            _writer.WriteLine(json);
        }
    }

    public void Dispose() => _writer.Dispose();
}
