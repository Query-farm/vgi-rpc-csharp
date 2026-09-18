using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Identity;

/// <summary>
/// The refusals and failures <c>vgi_rpc.Identity.v1</c> can produce.
/// </summary>
/// <remarks>
/// <para>
/// Each one carries a stable <see cref="RpcException.ErrorKind"/>, and that is load bearing
/// rather than decorative. These used to be a bespoke HTTP JSON route (this port still has it:
/// <c>QueryFarm.VgiRpc.Http.TokenIntrospection</c>) whose callers classified
/// definitive-vs-transient on the HTTP status -- 404 meant "that credential is unknown", 503
/// meant "I could not find out". As protocol methods every handler exception surfaces the same
/// way, so <c>error_kind</c> is now the <em>only</em> signal a caller has. A caller that
/// negative-caches a transient failure locks out valid users; one that retries a definitive
/// rejection hammers the worker.
/// </para>
/// <para>
/// The wire <see cref="RpcException.ErrorType"/> of each is the canonical Python class name
/// (<c>IntrospectionRefusedError</c>, not this port's <c>...Exception</c>), following
/// <see cref="SessionLostException"/>'s precedent: an error every port is expected to spell
/// identically on the wire is part of the cross-language vocabulary, not of C#'s naming
/// convention.
/// </para>
/// </remarks>
public class IdentityRefusedException : RpcException
{
    /// <param name="errorType">The wire error type -- the canonical Python class name.</param>
    /// <param name="message">Operator-facing text. Must never contain the credential.</param>
    /// <param name="errorKind">The stable wire token a caller classifies on.</param>
    protected IdentityRefusedException(string errorType, string message, string errorKind)
        : base(errorType, message, errorKind: errorKind)
    {
    }
}

/// <summary>The caller may not introspect -- it is not on the allowlist.</summary>
/// <remarks>
/// <para>
/// Definitive: a caller may cache this. Authentication is not the same capability as
/// introspection -- a deployment where any valid credential may introspect lets any user test
/// guesses of any other user's credential at unlimited rate, and resolve a stolen one to its
/// owner.
/// </para>
/// <para>
/// <b>Never a throttle.</b> Because a caller may cache it, a throttled introspection reported as
/// this negative-caches valid credentials -- which is how the retired per-caller limit locked
/// users out. Anything transient is <see cref="IdentityUnavailableException"/>.
/// </para>
/// </remarks>
public sealed class IntrospectionRefusedException : IdentityRefusedException
{
    /// <summary>The stable wire token for this refusal.</summary>
    public const string ErrorKindConst = MetadataKeys.ErrorKinds.IntrospectionRefused;

    /// <param name="message">Why the caller was refused. Never names the subject credential.</param>
    public IntrospectionRefusedException(string message)
        : base("IntrospectionRefusedError", message, ErrorKindConst)
    {
    }
}

/// <summary>The subject credential did not resolve.</summary>
/// <remarks>
/// Definitive, and deliberately uniform: unknown, expired and malformed are one answer, because
/// reporting which would confirm that a guessed credential exists. This is the invalid-argument
/// member of the taxonomy -- Python spells it as a <c>ValueError</c> subclass for exactly that
/// classification -- and it is sealed so nothing transient can ever arrive wearing it.
/// </remarks>
public sealed class TokenUnresolvedException : RpcException
{
    /// <summary>The stable wire token for this rejection.</summary>
    public const string ErrorKindConst = MetadataKeys.ErrorKinds.TokenUnresolved;

    /// <param name="message">Always the same uniform text; see the class remarks.</param>
    public TokenUnresolvedException(string message)
        : base("TokenUnresolvedError", message, errorKind: ErrorKindConst)
    {
    }
}

/// <summary>The caller has not authenticated recently enough to mint a grant.</summary>
/// <remarks>
/// Definitive but <em>actionable</em>, unlike the introspection rejections: this is always about
/// the caller themselves, so naming the reason leaks nothing and is the only way a console
/// learns to re-prompt.
/// </remarks>
public sealed class StaleAuthException : IdentityRefusedException
{
    /// <summary>The stable wire token for this refusal.</summary>
    public const string ErrorKindConst = MetadataKeys.ErrorKinds.StaleAuth;

    /// <param name="message">Names the reason -- see the class remarks on why that is safe here.</param>
    public StaleAuthException(string message)
        : base("StaleAuthError", message, ErrorKindConst)
    {
    }
}

/// <summary>The worker declined to mint this grant.</summary>
/// <remarks>Definitive. The worker holds the policy; the framework only asked.</remarks>
public sealed class GrantRefusedException : IdentityRefusedException
{
    /// <summary>The stable wire token for this refusal.</summary>
    public const string ErrorKindConst = MetadataKeys.ErrorKinds.GrantRefused;

    /// <param name="message">Why the mint was refused.</param>
    public GrantRefusedException(string message)
        : base("GrantRefusedError", message, ErrorKindConst)
    {
    }
}

/// <summary>The answer is not <em>knowable</em> -- a backing store is down, a 5xx upstream.</summary>
/// <remarks>
/// <para>
/// Transient, and distinct from a definitive rejection: a caller that negative-caches "unknown"
/// must not cache this. Cache an outage and a worker restart takes the fleet down for the
/// cache's lifetime; retry a rejection and the worker is hammered.
/// </para>
/// <para>
/// Deliberately <em>not</em> a subclass of <see cref="TokenUnresolvedException"/> (the
/// invalid-argument member) nor of <see cref="IdentityRefusedException"/> (the
/// permission-denied member), and deliberately not a CLR <see cref="ArgumentException"/>. Python
/// keeps it off <c>ValueError</c> because <c>chain_authenticate</c> advances to the next
/// authenticator on <c>ValueError</c>, so a sidecar outage raised as one reads as "not my
/// credential, try the next" and becomes a 401 from the end of the chain -- restarting every
/// session in the fleet over a thirty-second blip. This port's equivalent hazard is
/// <c>QueryFarm.VgiRpc.Http.AuthFailure</c>: a peer-identity chain advances past
/// <c>AuthReason.MissingCredential</c>, and any other exception a custom authenticate delegate
/// throws is flattened to a bare 401. So this type is not an <c>AuthFailure</c> either, and an
/// authenticate delegate that calls a resolver must let this propagate rather than wrap it.
/// </para>
/// </remarks>
public sealed class IdentityUnavailableException : RpcException
{
    /// <summary>The stable wire token for this transient failure.</summary>
    public const string ErrorKindConst = MetadataKeys.ErrorKinds.IdentityUnavailable;

    /// <param name="detail">Operator-facing text. Must not contain the credential.</param>
    /// <param name="retryAfterSeconds">How long the caller should wait. Keep it short -- a hint
    /// to retry, not a backoff schedule.</param>
    public IdentityUnavailableException(string detail = "", int retryAfterSeconds = 5)
        : base(
            "IdentityUnavailableError",
            string.IsNullOrEmpty(detail) ? "identity lookup unavailable" : detail,
            errorKind: ErrorKindConst)
    {
        Detail = detail;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>Operator-facing text. Must not contain the credential.</summary>
    public string Detail { get; }

    /// <summary>Seconds the caller should wait before retrying.</summary>
    public int RetryAfterSeconds { get; }
}
