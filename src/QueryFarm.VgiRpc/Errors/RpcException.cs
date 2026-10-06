using System.Text.Json;

namespace QueryFarm.VgiRpc.Errors;

/// <summary>
/// The exception a client raises for any error a server reports on the wire. Mirrors Python's
/// <c>RpcError</c> (named <c>RpcException</c> here per C# convention: exception types end in
/// "Exception", not "Error"). <see cref="ErrorKind"/> is an open string set, never a closed
/// enum — new kinds may appear on the wire that this port's version doesn't know about yet.
/// </summary>
public class RpcException : Exception, IRpcErrorModel
{
    public string ErrorType { get; }
    public string ErrorMessage { get; }
    public string RemoteTraceback { get; }
    public string RequestId { get; }
    public string? ErrorKind { get; init; }

    /// <summary>The canonical code (WIRE_PROTOCOL.md §8), e.g. <c>"UNAVAILABLE"</c>.</summary>
    /// <remarks>
    /// On a server-side exception: the code this error is reported with (<c>""</c> means
    /// unclassified, which goes on the wire as <c>UNKNOWN</c>). On a client-decoded error: the
    /// code the server sent, verbatim, or <c>""</c> when it sent none (a server older than the
    /// error model) -- <c>""</c> and <c>"UNKNOWN"</c> are different answers. See
    /// <see cref="CanonicalCode"/> for the value to switch on.
    /// </remarks>
    public string ErrorCode { get; init; }

    /// <summary>The typed details, as JSON objects, in wire order -- unknown types included. The
    /// typed accessors (<see cref="GetRetryInfo"/>, ...) skip the ones this library does not
    /// know.</summary>
    public IReadOnlyList<JsonElement> ErrorDetails { get; init; }

    public RpcException(
        string errorType,
        string errorMessage,
        string remoteTraceback = "",
        string requestId = "",
        string? errorKind = null,
        string errorCode = "",
        IReadOnlyList<JsonElement>? errorDetails = null)
        : base($"{errorType}: {errorMessage}")
    {
        ErrorType = errorType;
        ErrorMessage = errorMessage;
        RemoteTraceback = remoteTraceback;
        RequestId = requestId;
        ErrorKind = errorKind;
        ErrorCode = errorCode;
        ErrorDetails = errorDetails ?? [];
    }

    /// <summary><see cref="ErrorCode"/> read for handling: <c>UNKNOWN</c> when absent or
    /// unrecognised.</summary>
    public string CanonicalCode => ErrorCodes.Parse(ErrorCode);

    /// <summary>Whether retrying this call is warranted, by the rule in WIRE_PROTOCOL.md §8:
    /// <c>UNAVAILABLE</c> always, <c>RESOURCE_EXHAUSTED</c> only with <see cref="RetryInfo"/>.
    /// When <see cref="GetRetryInfo"/> is present a retry waits at least that long.</summary>
    /// <remarks>A classification only. No client in this library retries an RPC error
    /// automatically: a method may not be idempotent, so retrying is the caller's decision.</remarks>
    public bool IsRetryable() => ErrorModel.IsRetryable(ErrorCode, ErrorDetails);

    /// <summary>The catalog details this library understands, in wire order; unknown types
    /// skipped.</summary>
    public IReadOnlyList<ErrorDetail> Details() => ErrorModel.Typed(ErrorDetails);

    private T? Detail<T>() where T : ErrorDetail => Details().OfType<T>().FirstOrDefault();

    /// <summary>The <c>vgi_rpc.ErrorInfo</c> detail, if present.</summary>
    public ErrorInfo? GetErrorInfo() => Detail<ErrorInfo>();

    /// <summary>The <c>vgi_rpc.RetryInfo</c> detail, if present.</summary>
    public RetryInfo? GetRetryInfo() => Detail<RetryInfo>();

    /// <summary>The <c>vgi_rpc.BadRequest</c> detail, if present.</summary>
    public BadRequest? GetBadRequest() => Detail<BadRequest>();

    /// <summary>The <c>vgi_rpc.PreconditionFailure</c> detail, if present.</summary>
    public PreconditionFailure? GetPreconditionFailure() => Detail<PreconditionFailure>();

    /// <summary>The <c>vgi_rpc.QuotaFailure</c> detail, if present.</summary>
    public QuotaFailure? GetQuotaFailure() => Detail<QuotaFailure>();

    /// <summary>The <c>vgi_rpc.ResourceInfo</c> detail, if present.</summary>
    public ResourceInfo? GetResourceInfo() => Detail<ResourceInfo>();

    /// <summary>The <c>vgi_rpc.Help</c> detail, if present.</summary>
    public Help? GetHelp() => Detail<Help>();

    /// <summary>The <c>vgi_rpc.LocalizedMessage</c> detail, if present.</summary>
    public LocalizedMessage? GetLocalizedMessage() => Detail<LocalizedMessage>();
}

/// <summary>An application error carrying the full error model -- the code, reason and details
/// a client sees. Mirrors the reference's <c>StatusError</c>.</summary>
/// <example>
/// <code>
/// throw new StatusException("report is being rebuilt", ErrorCodes.Unavailable,
///     kind: "report_rebuilding", details: [new RetryInfo(30)]);
/// </code>
/// </example>
/// <remarks>
/// The details are validated eagerly, so a rule violation fails in the code that made it rather
/// than being silently dropped on the way out. Any <see cref="RpcException"/> subclass may
/// instead pass <c>errorCode</c>/<c>errorDetails</c> to the base constructor; this is the
/// convenience for when a dedicated class would add nothing.
/// </remarks>
public sealed class StatusException : RpcException
{
    /// <param name="message">Developer-facing text.</param>
    /// <param name="code">One of the sixteen <see cref="ErrorCodes"/>.</param>
    /// <param name="kind">The reason a client branches on, unique within the raising protocol;
    /// <see langword="null"/> or empty for none.</param>
    /// <param name="details">Catalog details (or protocol-defined objects under the protocol's own
    /// name), at most one of each type.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is not canonical, or
    /// <paramref name="details"/> breaks a catalog rule.</exception>
    public StatusException(string message, string code, string? kind = null, IEnumerable<ErrorDetail>? details = null)
        : this(message, code, kind, details is null ? [] : ErrorModel.ToJson(details))
    {
    }

    /// <inheritdoc cref="StatusException(string, string, string?, IEnumerable{ErrorDetail}?)"/>
    public StatusException(string message, string code, string? kind, IReadOnlyList<JsonElement> details)
        : base("StatusError", message, errorKind: string.IsNullOrEmpty(kind) ? null : kind,
            errorCode: ErrorCodes.IsCanonical(code) ? code : throw new ArgumentException($"'{code}' is not a canonical error code", nameof(code)),
            errorDetails: ValidatedDetails(details))
    {
    }

    private static IReadOnlyList<JsonElement> ValidatedDetails(IReadOnlyList<JsonElement> details)
    {
        ErrorModel.Validate(details);
        return details;
    }
}

/// <summary>An authenticator -- or an identity hook calling the same store -- could not answer.
/// Not a rejection.</summary>
/// <remarks>
/// <para>
/// "The credential is bad" and "I could not find out whether the credential is bad" are
/// different answers. Thrown from an HTTP authenticate delegate it becomes <c>503</c> with
/// <c>Retry-After</c>, never a 401. Thrown from a <c>vgi_rpc.Identity.v1</c> hook
/// (<see cref="Identity.IdentityImpl.TokenResolver"/> / <see cref="Identity.IdentityImpl.GrantMinter"/>)
/// the framework translates it to <c>identity_unavailable</c> carrying the same retry hint
/// (WIRE_PROTOCOL.md §16), on every transport -- which is why it lives in the core package rather
/// than the HTTP one. This is this port's equivalent of the reference's
/// <c>AuthUnavailableError</c>; <see cref="Identity.PeerIdentityUnavailableException"/> derives
/// from it.
/// </para>
/// <para>
/// Deliberately neither an <see cref="ArgumentException"/> nor an <c>AuthFailure</c>: a
/// peer-identity chain advances past "not my credential", so an outage reported as one would be
/// read as "try the next authenticator" and end as a 401 -- a thirty-second blip becoming a
/// fleet-wide re-login. Raise it for transport failures, timeouts and 5xx from a remote
/// authority, never for a credential the authority answered about.
/// </para>
/// </remarks>
public class AuthUnavailableException : Exception, IRpcErrorModel
{
    /// <param name="detail">Operator-facing text. Must not contain the credential.</param>
    /// <param name="retryAfterSeconds">Seconds to advertise -- in <c>Retry-After</c> over HTTP and
    /// as <see cref="RetryInfo"/> on the wire. Keep it short: a hint, not a backoff schedule.</param>
    public AuthUnavailableException(string detail = "", int retryAfterSeconds = 5)
        : base(string.IsNullOrEmpty(detail) ? "authentication service unavailable" : detail)
    {
        Detail = detail;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>Operator-facing text, possibly empty. Must not contain the credential.</summary>
    public string Detail { get; }

    /// <summary>Seconds the caller should wait before retrying.</summary>
    public int RetryAfterSeconds { get; }

    /// <summary>Always <c>UNAVAILABLE</c>.</summary>
    public string ErrorCode => ErrorCodes.Unavailable;

    /// <summary>None of its own: translated to <c>identity_unavailable</c> by the identity
    /// protocol, unclassified elsewhere.</summary>
    public string? ErrorKind => null;

    /// <summary>The retry hint, as the one detail this error carries.</summary>
    public IReadOnlyList<JsonElement> ErrorDetails => [new RetryInfo(RetryAfterSeconds).ToJson()];
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
        ErrorCode = ErrorCodes.FailedPrecondition;
    }

    /// <param name="message">The directional, human-readable explanation.</param>
    /// <param name="protocol">The protocol whose version gate refused the call -- named in a
    /// <see cref="PreconditionFailure"/>, because with several bindings "Server: 2.0.0" alone does
    /// not say which.</param>
    /// <param name="clientVersion">What the client declared, or <see langword="null"/>.</param>
    /// <param name="serverVersion">What the server's binding declares.</param>
    public ProtocolVersionException(string message, string protocol, string? clientVersion, string serverVersion)
        : this(message)
    {
        ErrorDetails =
        [
            new PreconditionFailure(
            [
                new PreconditionViolation(
                    "protocol_version", protocol,
                    $"client declares {(string.IsNullOrEmpty(clientVersion) ? "<none>" : clientVersion)}, "
                    + $"server requires {serverVersion}; major and minor must match"),
            ]).ToJson(),
        ];
    }
}

/// <summary>The server has no method registered under the requested name.</summary>
public sealed class MethodNotImplementedException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.MethodNotImplemented;

    public MethodNotImplementedException(string message)
        : base(nameof(MethodNotImplementedException), message, errorKind: ErrorKindConst)
    {
        ErrorCode = ErrorCodes.Unimplemented;
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
        ErrorCode = ErrorCodes.Aborted;
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

    /// <param name="message">Human-readable explanation.</param>
    /// <param name="retryAfterSeconds">Seconds before a retry -- which a load balancer will usually
    /// route to a worker that is not draining. Sent as <see cref="RetryInfo"/>.</param>
    public ServerDrainingException(string message, double retryAfterSeconds = 1.0)
        : base("ServerDrainingError", message, errorKind: ErrorKindConst,
            errorCode: ErrorCodes.Unavailable, errorDetails: [new RetryInfo(retryAfterSeconds).ToJson()])
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
        ErrorCode = ErrorCodes.InvalidArgument;
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
        ErrorCode = ErrorCodes.Unimplemented;
    }
}
