using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;

// This worker still serves the retired POST /__introspect_token__ JSON route (--introspect), whose
// own payload type is also called TokenIdentity. Alias rather than drop a using: the protocol's
// type is the one this file means everywhere, and leaving the name ambiguous would let a future
// edit silently resolve to the route's.
using TokenIdentity = QueryFarm.VgiRpc.Identity.TokenIdentity;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>
/// The fixed deployment policy the shared <c>vgi_rpc.Identity.v1</c> conformance group requires,
/// as specified normatively by <c>IDENTITY_CONFORMANCE_FIXTURE.md</c> (reference implementation:
/// <c>vgi_rpc.conformance.identity_fixture</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a test fixture and must never be deployed.</b> The authentication it relies on is
/// two request headers (see <see cref="Authenticate"/>) and is therefore trivially spoofable by
/// anyone who can reach the port. It exists because <c>vgi_rpc.Identity.v1</c> is almost entirely
/// guards, every guard reads deployment policy, and six language ports cannot each stand up an
/// identity provider to produce a deterministic authenticated caller with an <c>auth_time</c>
/// claim.
/// </para>
/// <para>
/// Two rules shape every value below, and both matter more than they look.
/// </para>
/// <para>
/// <b>The resolver resolves almost everything.</b> Identity's rejections are deliberately uniform
/// -- unknown, expired, malformed and over-long are one answer -- so an over-long credential is
/// <em>also</em> an unknown one, and a guard test probing with a credential the resolver does not
/// know cannot distinguish "the cap refused it" from "the cap let it through and the resolver
/// refused it". Delete the cap and such a test stays green. With a resolver that answers for
/// whatever it is handed, a rejection can only have come from a guard -- and a guard that fails to
/// fire produces a <em>success</em>, which uniformity cannot disguise.
/// </para>
/// <para>
/// <b>Both hooks are pure functions of their arguments.</b> No clock, no counter, no shared state:
/// this worker must answer identically on the first call and the thousandth, and on a runtime that
/// dispatches the two methods on different threads (which .NET does). The one value that would
/// otherwise need a clock -- a grant's <c>expires_at</c> -- is <see cref="GrantExpiresAt"/>, a
/// constant, so the wire value can be asserted exactly rather than within a tolerance.
/// </para>
/// </remarks>
public static class ConformanceIdentity
{
    /// <summary>Header naming the authenticated principal; absent means <em>unauthenticated</em>.</summary>
    /// <remarks>
    /// Not "anonymous but authenticated": health checks and capability probes must keep working
    /// without it, and the group relies on its absence to test fail-closed behaviour. Already the
    /// convention the sticky-session fixture uses (<c>--sticky-auth</c>), reused rather than
    /// invented.
    /// </remarks>
    public const string PrincipalHeader = "X-Conformance-Principal";

    /// <summary>Header carrying the <c>auth_time</c> claim, <b>verbatim as a string, unparsed</b>.</summary>
    /// <remarks>
    /// Verbatim is load bearing. A fixture that parses the header and drops it when parsing fails
    /// collapses "the credential carries an unusable <c>auth_time</c>" into "the credential carries
    /// no <c>auth_time</c>". Both answer <c>stale_auth</c>, so the test stays green while the
    /// property it names goes untested. <see cref="IdentityGuards.CheckFreshness"/> is what parses;
    /// this fixture only transports.
    /// </remarks>
    public const string AuthTimeHeader = "X-Conformance-Auth-Time";

    /// <summary>The single principal on the introspector allowlist.</summary>
    /// <remarks>
    /// Exactly one, so that "on the list" and "authenticated but not on the list" are both
    /// reachable -- the second is what shows that holding <em>a</em> credential is not the same
    /// capability as introspecting somebody else's.
    /// </remarks>
    public const string IntrospectorPrincipal = "conformance-introspector";

    /// <summary>An authenticated principal deliberately <em>off</em> the allowlist.</summary>
    public const string OutsiderPrincipal = "conformance-outsider";

    /// <summary>How recently a caller must have authenticated to mint -- the documented default.</summary>
    public const double MaxAuthAge = 900.0;

    /// <summary>Introspections allowed per caller per second.</summary>
    /// <remarks>
    /// Deliberately far above the framework default of 20. Nearly every case in the shared group is
    /// an introspection, and a production-tuned limiter would fire mid-group with every resulting
    /// failure reading as the wrong guard. The limiter is not asserted there at all; it is covered
    /// port-locally in <c>IdentityRateLimiterTests</c>, where its refusal is distinguishable by
    /// message and so cannot be tested vacuously.
    /// </remarks>
    public const int IntrospectRateLimit = 100_000;

    /// <summary>The identity every resolvable credential maps to.</summary>
    public const string SubjectPrincipal = "subject@conformance.example";

    /// <summary>The <c>token_name</c> the catch-all rule reports.</summary>
    public const string SubjectTokenName = "conformance-subject";

    /// <summary>The <c>ttl_seconds</c> the catch-all rule reports.</summary>
    public const long SubjectTtl = 300;

    /// <summary>The one credential this policy reports as <b>unknown</b>.</summary>
    /// <remarks>
    /// Everything the resolver has no other rule for resolves, so a rejection of anything else can
    /// only have come from a guard.
    /// </remarks>
    public const string UnknownToken = "conformance-unknown-token";

    /// <summary>The credential this policy reports as <em>unknowable</em> rather than unknown.</summary>
    /// <remarks>
    /// A caller may negative-cache "unknown"; caching an outage locks out a valid user for as long
    /// as the cache holds, so the two must not share an answer.
    /// </remarks>
    public const string UnavailableToken = "conformance-unavailable-token";

    /// <summary>Resolves with <c>ttl_seconds = 0</c>, which must survive the wire as zero.</summary>
    /// <remarks>
    /// A resolver naming zero is saying <em>do not cache this</em>. This port is exactly where that
    /// is at risk: <c>default(long)</c> is 0, so "the hook set zero" and "the field was omitted"
    /// look alike, and the tempting normalisation of <c>&lt;= 0</c> up to the 300 default silently
    /// converts a revocation signal into five more minutes of access.
    /// </remarks>
    public const string ZeroTtlToken = "conformance-zero-ttl-token";

    /// <summary>Resolves to an identity built with <b>only</b> the principal supplied.</summary>
    /// <remarks>
    /// So <c>token_name</c> and <c>ttl_seconds</c> land on their documented defaults through
    /// <see cref="TokenIdentity(string, string, long)"/>'s optional parameters. Passing <c>""</c>
    /// and <c>300</c> explicitly here would test the wrong thing: a default is for an omitted
    /// field, never a coercion applied to a value a hook actually set.
    /// </remarks>
    public const string MinimalToken = "conformance-minimal-token";

    /// <summary>Two leading and two trailing ASCII spaces (U+0020) around a resolvable credential.</summary>
    /// <remarks>
    /// The shape test runs on the trimmed credential while the resolver receives the untrimmed
    /// original, and this is the only probe that can see the difference: a port that trims once, up
    /// front, and resolves the result passes every other case in the group, because the padded
    /// credential still resolves -- just via the catch-all rule, reporting
    /// <see cref="SubjectTokenName"/> instead of <see cref="PaddedProbeName"/>.
    /// </remarks>
    public const string PaddedProbeToken = "  conformance-padded-probe  ";

    /// <summary>The distinguishable <c>token_name</c> <see cref="PaddedProbeToken"/> resolves to.</summary>
    public const string PaddedProbeName = "conformance-padded";

    /// <summary>Prefix of a minted grant's token; the caller's principal is appended.</summary>
    /// <remarks>
    /// Which is how "the subject is the caller, never a parameter" becomes observable over the
    /// wire: two callers making identical requests get two different tokens.
    /// </remarks>
    public const string GrantTokenPrefix = "conformance-grant-for:";

    /// <summary>Separator between the principal and the echoed scopes in a grant token.</summary>
    public const string ScopeSeparator = "|";

    /// <summary>2030-01-01T00:00:00Z -- fixed rather than <c>now + ttl</c>.</summary>
    /// <remarks>
    /// A constant can be asserted exactly, which also pins the float64 round trip. Nothing is lost:
    /// <c>expires_at</c> is a declaration rather than an enforcement, since the real lifetime lives
    /// inside the opaque token.
    /// </remarks>
    public const double GrantExpiresAt = 1893456000.0;

    /// <summary>Correlation handle a full grant carries.</summary>
    public const string GrantId = "conformance-grant-id";

    /// <summary>The purpose this policy refuses, so <c>grant_refused</c> reaches the wire.</summary>
    public const string RefusedPurpose = "conformance-refused";

    /// <summary>The purpose that mints a grant built without a <c>grant_id</c>.</summary>
    /// <remarks>Omitted, not <c>""</c> -- so the field's documented default is what is observed.</remarks>
    public const string MinimalPurpose = "conformance-minimal";

    /// <summary>Resolves a credential under the fixed conformance policy.</summary>
    /// <param name="token">The credential, exactly as the caller sent it -- untrimmed.</param>
    /// <returns>The identity, or <see langword="null"/> for the one credential this policy calls
    /// unknown.</returns>
    /// <exception cref="IdentityUnavailableException">For <see cref="UnavailableToken"/>, standing
    /// in for a backing store that cannot be reached.</exception>
    /// <remarks>
    /// The catch-all arm is the point, not laziness: see the type's remarks on why a resolver that
    /// answers for anything is what makes the guard assertions non-vacuous.
    /// </remarks>
    public static TokenIdentity? ResolveToken(string token) => token switch
    {
        UnavailableToken => throw new IdentityUnavailableException("conformance: mapping store unreachable"),
        UnknownToken => null,
        ZeroTtlToken => new TokenIdentity(SubjectPrincipal, SubjectTokenName, ttlSeconds: 0),
        MinimalToken => new TokenIdentity(SubjectPrincipal),
        PaddedProbeToken => new TokenIdentity(SubjectPrincipal, PaddedProbeName, SubjectTtl),
        _ => new TokenIdentity(SubjectPrincipal, SubjectTokenName, SubjectTtl),
    };

    /// <summary>Mints a grant under the fixed conformance policy.</summary>
    /// <param name="principal">The caller's authenticated principal, supplied by the framework.
    /// There is no subject parameter and must not be one.</param>
    /// <param name="purpose">Why the grant is wanted; two values carry policy meaning.</param>
    /// <param name="scopes">What the grant may do -- echoed into the token so the list's round trip
    /// is observable in the response, empty list included.</param>
    /// <param name="ttlSeconds">Deliberately ignored: it is a <em>request</em>, the returned
    /// <c>expires_at</c> is authoritative, and honouring it would need a clock and make the value
    /// unassertable.</param>
    /// <returns>The minted grant.</returns>
    /// <exception cref="GrantRefusedException">For <see cref="RefusedPurpose"/>.</exception>
    public static IssuedGrant MintGrant(string principal, string purpose, List<string> scopes, long ttlSeconds)
    {
        _ = ttlSeconds;
        if (purpose == RefusedPurpose)
        {
            throw new GrantRefusedException("conformance: this purpose is refused");
        }

        var token = GrantTokenPrefix + principal + ScopeSeparator + string.Join(",", scopes);
        return purpose == MinimalPurpose
            ? new IssuedGrant(token, GrantExpiresAt)
            : new IssuedGrant(token, GrantExpiresAt, GrantId);
    }

    /// <summary>Builds the <see cref="IdentityImpl"/> for one of the two fixture configurations.</summary>
    /// <param name="mint">Whether to configure the mint hook. <see langword="false"/> is the
    /// <c>introspect-only</c> worker, whose <c>issue_grant</c> is then not hosted <em>at all</em> --
    /// absent rather than routed-and-refusing, and its <c>protocol_hash</c> narrows with it.</param>
    /// <returns>The configured implementation.</returns>
    public static IdentityImpl Build(bool mint) => new(
        resolveToken: ResolveToken,
        mintGrant: mint ? MintGrant : null,
        introspectPrincipals: [IntrospectorPrincipal],
        introspectRateLimit: IntrospectRateLimit,
        maxAuthAge: MaxAuthAge);

    /// <summary>
    /// Derives the caller's identity from <see cref="PrincipalHeader"/> and
    /// <see cref="AuthTimeHeader"/>. <b>Trivially spoofable; never deploy this.</b>
    /// </summary>
    /// <param name="context">The request being authenticated.</param>
    /// <returns>A completed task -- there is nothing asynchronous to do.</returns>
    /// <remarks>
    /// <para>
    /// Never throws. An absent <see cref="PrincipalHeader"/> leaves the request
    /// <em>unauthenticated</em> rather than rejected, which is what keeps <c>GET /health</c>, the
    /// <c>OPTIONS</c> capability probe and <c>vgi_rpc.Reflection.v1</c> reachable -- the group reads
    /// the hosted protocol list and both method descriptions without presenting anything -- and it
    /// is also the case the fail-closed assertions probe.
    /// </para>
    /// <para>
    /// <see cref="PeerIdentityAuthentication.SetAuth"/> rather than
    /// <see cref="AuthIdentity.SetOn"/>, because only the former carries <em>claims</em>, and
    /// <c>auth_time</c> is a claim: an authenticate delegate that could publish nothing but a
    /// principal could never let <c>issue_grant</c> succeed, which would quietly reduce the whole
    /// freshness section to a worker that refuses every mint.
    /// </para>
    /// </remarks>
    public static Task Authenticate(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.Request.Headers[PrincipalHeader].ToString();
        if (string.IsNullOrEmpty(principal))
        {
            return Task.CompletedTask;
        }

        var claims = new Dictionary<string, object?>(StringComparer.Ordinal);
        var authTime = context.Request.Headers[AuthTimeHeader].ToString();
        if (authTime.Length > 0)
        {
            // Verbatim and unparsed -- see AuthTimeHeader's remarks. CheckFreshness is what
            // decides whether this is a timestamp; collapsing "unusable" into "absent" here is
            // exactly the fixture bug that makes the unparseable case untestable.
            claims["auth_time"] = authTime;
        }

        PeerIdentityAuthentication.SetAuth(
            context, new AuthContext("conformance", authenticated: true, principal, claims));
        return Task.CompletedTask;
    }
}
