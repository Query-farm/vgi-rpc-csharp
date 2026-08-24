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
    /// and structured extra data (exception type/message, truncated stack trace, up to
    /// <see cref="MaxStackFrames"/> frames, and — for <see cref="RpcException"/>-derived
    /// exceptions carrying a class-level <c>ErrorKind</c> — that stable token). Mirrors
    /// Python's <c>Message.from_exception</c>.
    /// </summary>
    public static LogMessage FromException(Exception exception)
    {
        var formattedTrace = exception.ToString();
        if (formattedTrace.Length > MaxTracebackChars)
        {
            formattedTrace = formattedTrace[..MaxTracebackChars] + "\n… <traceback truncated>";
        }

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
            ["traceback"] = formattedTrace,
        };

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

        if (exception is RpcException { ErrorKind: { } kind })
        {
            extra["error_kind"] = kind;
        }

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
        }

        return result;
    }
}
