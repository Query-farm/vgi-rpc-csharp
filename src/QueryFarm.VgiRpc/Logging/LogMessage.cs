using System.Text.Json;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Logging;

/// <summary>
/// A log message emitted during RPC method processing — transmitted to the client as a
/// zero-row batch carrying <see cref="MetadataKeys.LogLevel"/>/<see cref="MetadataKeys.LogMessage"/>/
/// <see cref="MetadataKeys.LogExtra"/> metadata. Mirrors Python's <c>log.Message</c>.
/// </summary>
public sealed class LogMessage
{
    private const int MaxTracebackChars = 16_000;
    private const int MaxStackFrames = 5;

    public VgiLogLevel Level { get; }
    public string Message { get; }
    public IReadOnlyDictionary<string, object?>? Extra { get; }

    public LogMessage(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null)
    {
        Level = level;
        Message = message;
        Extra = extra is { Count: > 0 } ? extra : null;
    }

    public static LogMessage Info(string message) => new(VgiLogLevel.Info, message);
    public static LogMessage Warn(string message) => new(VgiLogLevel.Warn, message);
    public static LogMessage Debug(string message) => new(VgiLogLevel.Debug, message);
    public static LogMessage Trace(string message) => new(VgiLogLevel.Trace, message);

    /// <summary>
    /// Builds a message from an exception: level EXCEPTION, a short "{Type}: {msg}" summary,
    /// and structured extra data -- the exception type and message, the error model's three
    /// layers (WIRE_PROTOCOL.md §8: <c>error_code</c> always, <c>error_kind</c> when declared,
    /// <c>error_details</c> when declared and within the 4 KiB cap -- dropped whole otherwise),
    /// and, when <paramref name="includeTraceback"/>, the truncated stack trace, up to
    /// <see cref="MaxStackFrames"/> frames and the inner exception. Mirrors Python's
    /// <c>Message.from_exception</c>.
    /// </summary>
    /// <param name="exception">The exception to report.</param>
    /// <param name="includeTraceback">Whether to send the traceback, its frames and the chained
    /// <c>cause</c>. Servers omit them on network transports by default: a stack trace names
    /// files, functions and sometimes values, and the caller of a network service is not its
    /// operator. Defaults to <see langword="true"/> for callers outside a server's dispatch.</param>
    public static LogMessage FromException(Exception exception, bool includeTraceback = true)
    {
        // Prefer RpcException.ErrorType over the raw CLR type name when the exception carries
        // one explicitly. Python has no equivalent override — it always uses type(exc).__name__ —
        // because its exception classes are named to already match the cross-language wire
        // vocabulary (SessionLostError, ServerDrainingError, ...). C# convention names the same
        // classes "...Exception" (SessionLostException, ServerDrainingException), so those two
        // spellings diverge unless RpcException-derived types can state their wire name
        // explicitly — which is exactly what the ErrorType constructor parameter is for (see
        // SessionLostException/ServerDrainingException in QueryFarm.VgiRpc.Errors). Plain
        // Exception subclasses (e.g. the conformance worker's ValueError/RuntimeError/TypeError)
        // are unaffected — GetType().Name already matches Python's built-in name for those.
        var wireTypeName = exception is RpcException { ErrorType.Length: > 0 } rpcException ? rpcException.ErrorType : exception.GetType().Name;
        var summary = $"{wireTypeName}: {exception.Message}";

        var extra = new Dictionary<string, object?>
        {
            ["exception_type"] = wireTypeName,
            ["exception_message"] = exception.Message,
            // Code first: it is required on every EXCEPTION batch, so it is set before anything
            // that could be skipped.
            ["error_code"] = ErrorModel.CodeOf(exception),
        };

        if (ErrorModel.KindOf(exception) is { } kind)
        {
            extra["error_kind"] = kind;
        }

        var details = ErrorModel.DetailsOf(exception);
        if (details.Count > 0 && ErrorModel.Encode(details) is not null)
        {
            extra["error_details"] = details;
        }

        if (!includeTraceback)
        {
            return new LogMessage(VgiLogLevel.Exception, summary, extra);
        }

        var formattedTrace = exception.ToString();
        if (formattedTrace.Length > MaxTracebackChars)
        {
            formattedTrace = formattedTrace[..MaxTracebackChars] + "\n… <traceback truncated>";
        }

        extra["traceback"] = formattedTrace;

        if (exception.InnerException is { } inner)
        {
            var innerTrace = inner.ToString();
            if (innerTrace.Length > MaxTracebackChars)
            {
                innerTrace = innerTrace[..MaxTracebackChars] + "\n… <traceback truncated>";
            }

            // .NET has one InnerException chain; Python distinguishes __cause__ (explicit
            // `raise ... from cause`) from __context__ (implicit, caught-during-handling).
            // We surface it under "cause" — the more common intentional case — rather than
            // trying to recover a distinction .NET's exception model doesn't preserve.
            extra["cause"] = innerTrace;
        }

        var frames = new List<Dictionary<string, object?>>();
        var stackTrace = new System.Diagnostics.StackTrace(exception, fNeedFileInfo: true);
        var allFrames = stackTrace.GetFrames() ?? [];
        foreach (var frame in allFrames.TakeLast(MaxStackFrames))
        {
            frames.Add(new Dictionary<string, object?>
            {
                ["file"] = frame.GetFileName(),
                ["line"] = frame.GetFileLineNumber() is > 0 and var line ? line : null,
                ["function"] = frame.GetMethod()?.Name,
                // .NET stack frames don't carry the source line's text the way Python's
                // traceback module does without re-reading/parsing the source file
                // ourselves — left null, which the wire protocol's frame shape allows.
                ["code"] = null,
            });
        }

        extra["frames"] = frames;

        return new LogMessage(VgiLogLevel.Exception, summary, extra);
    }

    /// <summary>
    /// Augments (a copy of) <paramref name="metadata"/> with this message's log_level/
    /// log_message/log_extra keys, hoisting <c>error_kind</c> to its own top-level key when
    /// present (matching Python's <c>Message.add_to_metadata</c>).
    /// </summary>
    public Dictionary<string, string> AddToMetadata(IReadOnlyDictionary<string, string>? metadata = null)
    {
        var result = metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata);
        result[MetadataKeys.LogLevel] = Level.ToWireString();
        result[MetadataKeys.LogMessage] = Message;

        if (Extra is not null)
        {
            result[MetadataKeys.LogExtra] = JsonSerializer.Serialize(Extra);
            if (Extra.TryGetValue("error_kind", out var kind) && kind is string kindString)
            {
                result[MetadataKeys.ErrorKind] = kindString;
            }

            // The other two layers of the error model ride the same way -- only on EXCEPTION: a
            // WARN carrying "error_code" in its extras is an application's free-form log, not a
            // classification.
            if (Level == VgiLogLevel.Exception)
            {
                if (Extra.TryGetValue("error_code", out var code) && code is string codeString)
                {
                    result[MetadataKeys.ErrorCode] = codeString;
                }

                // Same encoder FromException measured the cap with, so the bytes on the wire are
                // the bytes that were checked.
                if (Extra.TryGetValue("error_details", out var details)
                    && details is IReadOnlyList<JsonElement> detailList
                    && ErrorModel.Encode(detailList) is { } encoded)
                {
                    result[MetadataKeys.ErrorDetails] = encoded;
                }
            }
        }

        return result;
    }
}
