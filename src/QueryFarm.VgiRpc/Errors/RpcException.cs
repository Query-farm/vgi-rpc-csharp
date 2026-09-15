namespace QueryFarm.VgiRpc.Errors;

/// <summary>
/// The exception a client raises for any error a server reports on the wire. Mirrors Python's
/// <c>RpcError</c> (named <c>RpcException</c> here per C# convention: exception types end in
/// "Exception", not "Error"). <see cref="ErrorKind"/> is an open string set, never a closed
/// enum — new kinds may appear on the wire that this port's version doesn't know about yet.
/// </summary>
public class RpcException : Exception
{
    public string ErrorType { get; }
    public string ErrorMessage { get; }
    public string RemoteTraceback { get; }
    public string RequestId { get; }
    public string? ErrorKind { get; }

    public RpcException(
        string errorType,
        string errorMessage,
        string remoteTraceback = "",
        string requestId = "",
        string? errorKind = null)
        : base($"{errorType}: {errorMessage}")
    {
        ErrorType = errorType;
        ErrorMessage = errorMessage;
        RemoteTraceback = remoteTraceback;
        RequestId = requestId;
        ErrorKind = errorKind;
    }
}

/// <summary>Base for version-mismatch errors (request/protocol version). Mirrors Python's <c>VersionError</c>.</summary>
public class VersionException : RpcException
{
    public VersionException(string errorType, string message, string? errorKind = null)
        : base(errorType, message, errorKind: errorKind)
    {
    }
}

/// <summary>The client and server declared incompatible <c>protocol_version</c> major.minor values.</summary>
public sealed class ProtocolVersionException : VersionException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.ProtocolVersionMismatch;

    public ProtocolVersionException(string message)
        : base(nameof(ProtocolVersionException), message, ErrorKindConst)
    {
    }
}

/// <summary>The server has no method registered under the requested name.</summary>
public sealed class MethodNotImplementedException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.MethodNotImplemented;

    public MethodNotImplementedException(string message)
        : base(nameof(MethodNotImplementedException), message, errorKind: ErrorKindConst)
    {
    }
}

/// <summary>A sticky-session call referenced a session that no longer exists (evicted/expired).
/// Named "...Exception" per C# convention (see this file's own class doc comment), but its wire
/// <see cref="RpcException.ErrorType"/> is the literal string <c>"SessionLostError"</c> — unlike
/// <see cref="ProtocolVersionException"/>/<see cref="MethodNotImplementedException"/> above (whose
/// wire type is this port's own class name), this one is part of the closed cross-language error
/// vocabulary every port's sticky-session implementation is expected to spell identically on the
/// wire (mirrors Python's own <c>SessionLostError</c> class name — confirmed against the Rust
/// port, which hardcodes the same literal string despite its own internal type being named
/// differently). See <c>docs/sticky-sessions-spec.md</c> §6 and docs/roadmap.md M10.</summary>
public sealed class SessionLostException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.SessionLost;

    public SessionLostException(string message)
        : base("SessionLostError", message, errorKind: ErrorKindConst)
    {
    }
}

/// <summary>The incoming request's declared payload size exceeds what this runtime's array/buffer
/// types can represent (~2^31 bytes — no managed <c>byte[]</c>/reader buffer on any .NET runtime
/// can hold more). Thrown server-side by <see cref="Wire.WireReader"/> after it has already
/// drained the oversized body off the wire, so the connection stays usable for the next call —
/// see <c>docs/roadmap.md</c> M17 and the mandatory conformance test
/// <c>large_payload.echo_binary_over_int32_max</c>, whose reference explicitly sanctions exactly
/// this kind of typed refusal. No dedicated <c>error_kind</c> wire token exists for this case in
/// the shared cross-language vocabulary (see this file's own class doc comment on
/// <see cref="RpcException.ErrorKind"/> being an open set) — it surfaces as a plain application-level error,
/// the same way a user's own thrown exception would.</summary>
public sealed class PayloadTooLargeException : RpcException
{
    public long DeclaredBodyLength { get; }

    public PayloadTooLargeException(long declaredBodyLength)
        : base(
            nameof(PayloadTooLargeException),
            $"Request payload is {declaredBodyLength} bytes, which exceeds the maximum size this runtime can represent ({int.MaxValue} bytes).")
    {
        DeclaredBodyLength = declaredBodyLength;
    }
}

/// <summary>The server is shutting down and is no longer accepting new sticky sessions/calls. See
/// <see cref="SessionLostException"/>'s doc comment — same "wire type is a fixed cross-language
/// string, not this class's own C# name" reasoning applies here (Python: <c>ServerDrainingError</c>).</summary>
public sealed class ServerDrainingException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.ServerDraining;

    public ServerDrainingException(string message)
        : base("ServerDrainingError", message, errorKind: ErrorKindConst)
    {
    }
}

/// <summary>The request named no protocol — no <c>vgi_rpc.protocol</c> routing key.
/// <para>
/// Required on every request, including against a server hosting exactly one protocol. An
/// exemption would cost the property that makes routing safe: an intermediary that rebuilds a
/// request and drops the field gets a loud rejection instead of silently landing on whichever
/// protocol happened to be registered first.
/// </para>
/// <para>
/// Named "...Exception" per C# convention (see this file's own doc comment), but its wire
/// <see cref="RpcException.ErrorType"/> is the literal <c>"ProtocolNotSpecifiedError"</c> —
/// same reasoning as <see cref="SessionLostException"/>: routing failures are part of the closed
/// cross-language vocabulary every port spells identically.
/// </para></summary>
public sealed class ProtocolNotSpecifiedException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.ProtocolNotSpecified;

    public ProtocolNotSpecifiedException(string message)
        : base("ProtocolNotSpecifiedError", message, errorKind: ErrorKindConst)
    {
    }
}

/// <summary>The named protocol is not hosted by this server.
/// <para>
/// Deliberately distinct from <see cref="MethodNotImplementedException"/>: "I do not speak that
/// protocol" and "I speak it but not that method" are different answers, and a client probing for
/// an optional protocol has to tell them apart. Maps to HTTP 404, matching unknown-method — gRPC
/// likewise answers UNIMPLEMENTED for both. Also the answer when the two carriers of the protocol
/// disagree (there, HTTP 400: the request is malformed rather than unroutable).
/// </para>
/// <para>Wire <see cref="RpcException.ErrorType"/> is the literal
/// <c>"ProtocolNotSupportedError"</c> — see <see cref="ProtocolNotSpecifiedException"/>.</para>
/// </summary>
public sealed class ProtocolNotSupportedException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.ProtocolNotSupported;

    public ProtocolNotSupportedException(string message)
        : base("ProtocolNotSupportedError", message, errorKind: ErrorKindConst)
    {
    }
}
