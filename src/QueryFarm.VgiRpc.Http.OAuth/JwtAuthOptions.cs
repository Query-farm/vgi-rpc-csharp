namespace QueryFarm.VgiRpc.Http.OAuth;

/// <summary>
/// Settings for <see cref="JwtAuth.Create(JwtAuthOptions)"/>: the C# analog of the keyword
/// arguments of the reference's <c>vgi_rpc.http.jwt_authenticate</c>, including its opt-in
/// hardening (an algorithm allow-list, a required <c>typ</c>, a required <c>exp</c>, a single
/// audience, a hard JWKS lifetime that fails closed and a rate limit on unknown-<c>kid</c>
/// re-fetches).
/// </summary>
/// <remarks>
/// The defaults keep the reference's defaults: keys are cached for the life of the process, an
/// unknown <c>kid</c> re-fetches immediately, there is no clock skew, and <c>exp</c> is checked
/// when present but not required.
/// </remarks>
public sealed class JwtAuthOptions
{
    /// <summary>Accepted <c>iss</c> values, matched exactly; a token matching any one passes. The
    /// first is used for OIDC discovery when <see cref="JwksUri"/> is not set.</summary>
    public required IReadOnlyList<string> Issuers { get; init; }

    /// <summary>Accepted <c>aud</c> values, matched exactly; a token naming any one passes (see
    /// <see cref="SingleAudience"/>).</summary>
    public required IReadOnlyList<string> Audiences { get; init; }

    /// <summary>Where to fetch the JWKS. <see langword="null"/> discovers it from
    /// <c>{Issuers[0]}/.well-known/openid-configuration</c>. Plain <c>http</c> is allowed, as in
    /// the reference: choosing the URL is the deployment's decision.</summary>
    public string? JwksUri { get; init; }

    /// <summary>The claim published as <c>AuthContext.Principal</c> (default <c>sub</c>).</summary>
    public string PrincipalClaim { get; init; } = "sub";

    /// <summary>The <c>AuthContext.Domain</c> of an accepted token (default <c>jwt</c>).</summary>
    public string Domain { get; init; } = "jwt";

    /// <summary>Allow-list of JWS <c>alg</c> values. <c>none</c> and <c>HS*</c> are refused
    /// here, at construction, and are never accepted from a token whatever this says: an HMAC
    /// algorithm checked against a JWKS lets anyone holding the <em>public</em> key mint tokens.
    /// <see langword="null"/> accepts any asymmetric algorithm the key supports.</summary>
    public IReadOnlyList<string>? Algorithms { get; init; }

    /// <summary>When set (e.g. <c>at+jwt</c> for RFC 9068 access tokens), the JOSE header
    /// <c>typ</c> must be present and match, case-insensitively and with the
    /// <c>application/</c> prefix optional.</summary>
    public string? RequiredTyp { get; init; }

    /// <summary>When true, a token without <c>exp</c> is rejected. An <c>exp</c> that is present
    /// is always checked.</summary>
    public bool RequireExp { get; init; }

    /// <summary>When true, the token's <c>aud</c> must be exactly one value (a string or a
    /// one-element array): a token naming several audiences is refused even if one matches.</summary>
    public bool SingleAudience { get; init; }

    /// <summary>Clock skew allowed on <c>exp</c> and <c>nbf</c> (default none).</summary>
    public TimeSpan Leeway { get; init; } = TimeSpan.Zero;

    /// <summary>Hard upper bound on how long fetched keys are used; a shorter
    /// <c>Cache-Control: max-age</c> on the JWKS response wins. When it passes the keys are
    /// discarded, and if the re-fetch fails verification fails (an
    /// <see cref="QueryFarm.VgiRpc.Errors.AuthUnavailableException"/>, HTTP 503) rather than
    /// using stale keys. <see langword="null"/> caches for the life of the process.</summary>
    public TimeSpan? JwksMaxAge { get; init; }

    /// <summary>Minimum time between JWKS fetches triggered by an unknown <c>kid</c>; within it
    /// such a token is rejected without a fetch. <see langword="null"/> re-fetches on every
    /// unknown <c>kid</c>.</summary>
    public TimeSpan? JwksRefreshMinInterval { get; init; }

    /// <summary>Timeout for each discovery or JWKS request. <see langword="null"/> keeps
    /// <see cref="HttpClient"/>'s default.</summary>
    public TimeSpan? JwksFetchTimeout { get; init; }

    /// <summary>The handler JWKS requests go through -- a test seam, or a deployment's own proxy
    /// settings. <see langword="null"/> uses a default <see cref="HttpClientHandler"/>.</summary>
    public HttpMessageHandler? JwksHttpHandler { get; init; }

    /// <summary>The clock the JWKS cache measures its lifetimes with -- a test seam. Token
    /// lifetimes (<c>exp</c>/<c>nbf</c>) are checked against the system clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
