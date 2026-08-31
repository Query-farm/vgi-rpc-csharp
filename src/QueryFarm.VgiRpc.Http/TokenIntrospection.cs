using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Token introspection: resolving an opaque bearer credential to a principal — a full port of the
/// canonical Python repo's <c>vgi_rpc.http.server._introspect</c>. See
/// <c>docs/porting-guide.md</c>'s "HTTP token introspection" section (there is no standalone spec
/// doc for this feature) and <c>docs/roadmap.md</c> M12 for what this port implements.
///
/// <para>A reverse proxy terminating the only public listener has to know <b>which principal a
/// credential authenticates as</b> before it can authorize anything — that principal becomes the
/// policy principal, the row-rule literal, the bind parameter of every entitlement query. When
/// the credential is opaque the proxy holds no local copy of it, so it asks the worker.</para>
///
/// <para><b>The response is an identity assertion made by the thing being protected</b>, and the
/// asker acts on it using credentials the worker does not hold. "Trust it as much as you trust
/// the worker" is the wrong frame — it must be trusted <i>more</i>. Every guard below follows
/// from that: never return claims (only <c>principal</c>/<c>token_name</c>/<c>ttl_seconds</c> —
/// see <see cref="TokenIdentity"/>); the route is absent unless a resolver is explicitly
/// supplied; the introspector allowlist has no permissive default; a JWS-shaped subject is
/// rejected without ever reaching the resolver; every rejection is uniform (unknown, expired, and
/// malformed produce byte-identical responses); the credential never appears in a response or
/// (were logging wired through this port yet) a log line.</para>
///
/// <para><b>Not "replay the credential through the worker's own authenticate chain."</b> That is
/// the attractive design and the porting guide documents four concrete reasons it breaks — most
/// of which don't even apply to this port's architecture (no precondition-gate-wrapping-a-chain
/// concept, no OR-combinator to run the wrong audience/issuer set through), but the deepest one
/// does: cookie- and mTLS/IP-derived identity cannot be replayed at all, and a synthesized request
/// would carry the proxy's own address. <see cref="TokenResolver"/> is a narrow
/// <c>token -&gt; principal | null</c> callable instead, exactly like Python's.</para>
/// </summary>
public static class TokenIntrospection
{
    /// <summary>Endpoint path, appended to the app's prefix. Matches the de-facto contract the
    /// existing proxy client already speaks.</summary>
    public const string IntrospectEndpoint = "/__introspect_token__";

    /// <summary>Advertised on <c>OPTIONS {prefix}/health</c> when the route is enabled, so a
    /// proxy can preflight at boot rather than discovering at first login that the worker it
    /// depends on cannot answer.</summary>
    public const string IntrospectEnabledHeader = "VGI-Token-Introspection";

    private const int MaxBodyBytes = 8192;
    private const int MaxTokenChars = 4096;

    // Three dot-separated base64url segments — a JWS. Such a credential is validated locally
    // against a key set and MUST NOT be routed here: doing so sends a bearer token the asker may
    // itself have rejected (expired, wrong audience) to a third party that might accept it.
    private static readonly Regex s_jwsShaped = new(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>Resolves an opaque credential, returning <see langword="null"/> when it does not
    /// resolve. Throw <see cref="AuthUnavailableException"/> when the answer is not knowable — a
    /// backing store that is down is not the same as a credential that is unknown, and a caller
    /// that negative-caches the second must not cache the first.</summary>
    public delegate Task<TokenIdentity?> TokenResolver(string token);

    /// <summary>Returns a lowercase hex SHA-256 digest of <paramref name="token"/>, for
    /// diagnostics. The credential itself must never reach a log, a span, or an error message —
    /// a digest is stable enough to correlate one credential's failures without being the
    /// credential.</summary>
    public static string TokenDigest(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>Validates a non-empty introspector allowlist. Throws when empty — there is no
    /// permissive default: "any authenticated caller" is precisely the configuration that turns
    /// this endpoint into an open oracle, so it cannot be reached by omission.</summary>
    public static IReadOnlySet<string> NormalizePrincipals(IEnumerable<string>? principals)
    {
        var allowed = new HashSet<string>((principals ?? []).Where(p => !string.IsNullOrEmpty(p)));
        if (allowed.Count == 0)
        {
            throw new ArgumentException(
                "introspectPrincipals must name at least one principal. Introspection is a distinct " +
                "capability from authentication: allowing any authenticated caller lets any user " +
                "resolve any other user's credential to its owner.",
                nameof(principals));
        }

        return allowed;
    }

    /// <summary><c>POST {prefix}/__introspect_token__</c> once a resolver is configured and the
    /// caller has already passed the worker's own <c>authenticate</c> gate (run by the caller of
    /// this method — see <see cref="RpcHttpEndpoints.MapVgiRpc"/>). Two rejection axes,
    /// deliberately distinguishable from each other and deliberately uniform within themselves:
    /// 403 (the caller may not introspect) and 404 (the subject credential did not resolve —
    /// unknown, expired, and malformed are one answer, since reporting which would confirm a
    /// guessed credential exists). Both are definitive: a caller may cache them. Anything
    /// transient reaches the caller as 503 with <c>Retry-After</c> instead.</summary>
    public static async Task HandleAsync(HttpContext context, TokenResolver resolver, IReadOnlySet<string> principals, IntrospectionRateLimiter limiter)
    {
        // Caller authorization first: an unauthorized caller must not learn anything about a
        // subject credential, including how long it took.
        var identity = AuthIdentity.GetFrom(context);
        var caller = identity?.Principal ?? "";
        if (identity is null || !identity.Authenticated || !principals.Contains(caller))
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, "not_an_introspector").ConfigureAwait(false);
            return;
        }

        if (!limiter.Allow(caller))
        {
            context.Response.Headers["Retry-After"] = "1";
            await RefuseAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited").ConfigureAwait(false);
            return;
        }

        var token = await ReadTokenAsync(context.Request).ConfigureAwait(false);
        if (token is null)
        {
            // Indistinguishable from an unresolvable credential — a malformed body is not worth
            // a separate signal, and giving one lets a caller probe the parser.
            await RefuseAsync(context, StatusCodes.Status404NotFound, "unresolved").ConfigureAwait(false);
            return;
        }

        if (s_jwsShaped.IsMatch(token))
        {
            // Refused without ever reaching the resolver — see this class's doc comment.
            await RefuseAsync(context, StatusCodes.Status404NotFound, "unresolved").ConfigureAwait(false);
            return;
        }

        TokenIdentity? resolved;
        try
        {
            resolved = await resolver(token).ConfigureAwait(false);
        }
        catch (AuthUnavailableException exc)
        {
            // "I could not find out" is not "it did not resolve". Refusing with 404 here would
            // hand the caller a *definitive* answer for an outage, and a caller that
            // negative-caches definitive answers — the correct thing to do — would cache it.
            // Deliberately not routed through RefuseAsync: that shape is for definitive
            // rejections, and a transient needs Retry-After instead of Cache-Control: no-store.
            context.Response.Headers["Retry-After"] = exc.RetryAfterSeconds.ToString();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "unavailable" }), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (resolved is null)
        {
            await RefuseAsync(context, StatusCodes.Status404NotFound, "unresolved").ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/json";
        context.Response.Headers["Cache-Control"] = "no-store";
        var body = JsonSerializer.Serialize(new { principal = resolved.Principal, token_name = resolved.TokenName, ttl_seconds = resolved.TtlSeconds });
        await context.Response.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Answers <c>404 {"error":"not_enabled"}</c> — the disabled-but-mandatory response
    /// every worker must give regardless of whether it implements introspection (porting guide:
    /// a caller classifying 401/403/404 as definitive and everything else as transient — the
    /// sensible classification — would otherwise read a generic-route 415 as "try again later"
    /// and retry forever against a worker that will never support the feature). Deliberately no
    /// authentication requirement: "this worker does not do introspection" is not a secret.</summary>
    public static Task HandleDisabledAsync(HttpContext context) => RefuseAsync(context, StatusCodes.Status404NotFound, "not_enabled");

    private static async Task RefuseAsync(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error }), context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<string?> ReadTokenAsync(HttpRequest request)
    {
        if (request.ContentLength is { } length && length > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted).ConfigureAwait(false);
        if (buffer.Length > MaxBodyBytes)
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenChars)
            {
                return null;
            }

            return token;
        }
    }
}

/// <summary>
/// The identity an opaque credential authenticates as — the closed response shape the porting
/// guide requires (never claims).
/// </summary>
/// <param name="Principal">The canonical principal, in the exact form the worker itself would
/// derive, so an asker that normalizes differently does not authorize as one identity while the
/// worker serves another.</param>
/// <param name="TokenName">Human-readable name for the credential, for audit trails. Never the
/// credential.</param>
/// <param name="TtlSeconds">How long the answer may be cached. The caller does the caching; this
/// endpoint holds no cache of its own. Treat it as an authorization window — for any path the
/// asker serves without re-presenting the credential, it is exactly that.</param>
public sealed record TokenIdentity(string Principal, string TokenName = "", int TtlSeconds = 300);

/// <summary>
/// Signals that an introspection resolver (or the equivalent for any other authenticator that
/// needs the definitive-vs-transient distinction) could not answer — not a rejection. "The
/// credential is bad" and "I could not find out whether the credential is bad" are different
/// answers: a caller that negative-caches a rejection must not cache an outage.
///
/// <para>Python needs this to be deliberately <i>not</i> a <c>ValueError</c>, because
/// <c>chain_authenticate</c> advances to the next authenticator on <c>ValueError</c> — an outage
/// raised as one would be read as "this credential isn't mine, try the next" and end up as a 401
/// from the end of the chain. This port has no such OR-combinator (see
/// <see cref="ProxyProof"/>'s class doc comment for the same architectural point in M11), so
/// there is nothing here for a plain CLR exception type to be silently swallowed by — but a
/// distinct type is still useful so a resolver's outage can't be mistaken for "did not resolve"
/// at the call site, which is exactly the mistake <c>TokenIntrospection.HandleAsync</c> is
/// written to make impossible.</para>
/// </summary>
public sealed class AuthUnavailableException(string detail = "", int retryAfterSeconds = 5)
    : Exception(string.IsNullOrEmpty(detail) ? "authentication service unavailable" : detail)
{
    /// <summary>Operator-facing text. Must not contain the credential.</summary>
    public string Detail { get; } = detail;

    /// <summary>Seconds to advertise in <c>Retry-After</c>. Keep it short — a hint to retry, not
    /// a backoff schedule.</summary>
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Fixed-window request limiter, keyed by caller — present because the introspection endpoint is
/// a credential→identity oracle even when correctly restricted: an allowlisted caller whose own
/// credential leaks can still test guesses. Rate limiting does not close that, it bounds it.
///
/// <para>Fixed-window rather than a token bucket: a window admits at most twice the configured
/// rate across a boundary, which is a rounding error here, and the state is two integers per
/// caller rather than a float that has to be aged — a direct port of Python's
/// <c>_RateLimiter</c>.</para>
/// </summary>
public sealed class IntrospectionRateLimiter(int perWindow, double windowSeconds = 1.0)
{
    private readonly Dictionary<string, int> _counts = [];
    private readonly Lock _lock = new();
    private double _windowStart;
    private readonly Func<double> _clock = () => Environment.TickCount64 / 1000.0;

    /// <summary>Returns <see langword="true"/> if <paramref name="key"/> may make a request in
    /// the current window.</summary>
    public bool Allow(string key, double? now = null)
    {
        var current = now ?? _clock();
        lock (_lock)
        {
            if (current - _windowStart >= windowSeconds)
            {
                // Whole-map reset rather than per-key ageing: a caller cycling keys cannot grow
                // the map beyond one window's worth.
                _counts.Clear();
                _windowStart = current;
            }

            var count = _counts.GetValueOrDefault(key, 0);
            if (count >= perWindow)
            {
                return false;
            }

            _counts[key] = count + 1;
            return true;
        }
    }
}
