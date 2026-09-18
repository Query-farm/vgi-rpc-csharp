using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Identity;

/// <summary>Applies this module's guards, then delegates to worker-supplied hooks.</summary>
/// <remarks>
/// <para>
/// The framework owns the guards and owns none of the policy. It decides who may ask and what
/// shape of credential is refused outright; the worker decides what a credential resolves to and
/// whether a grant is minted. That split is deliberate -- the guards are the part
/// that is identical in every deployment and catastrophic to get wrong, and the policy is the
/// part that is different in every deployment and cannot be guessed.
/// </para>
/// <para>
/// <b>A method whose hook is absent is not registered at all</b> (see
/// <see cref="OfferedMethods"/>), so the protocol a server hosts describes what it actually does.
/// Absent beats routed-and-refusing: it is what keeps a dependency upgrade from growing a
/// credential-to-identity oracle on every existing worker. The per-method "hook absent" refusals
/// below are the belt to that braces -- both exist, because a caller can still reach a method
/// through a path that never consulted the registration (and because a refusal is a better
/// answer than a null-reference crash).
/// </para>
/// <para>
/// <b>Introspection is deliberately not rate limited.</b> The allowlist is the control: the only
/// callers are trusted askers, in practice a proxy. A per-caller limit there bounds only guessing,
/// which is hopeless against a random credential at any rate, and not the real harm of a leaked
/// introspector credential -- resolving a <em>stolen</em> credential to its owner takes one call.
/// What it did do was harm: the asker calls on behalf of everyone who presents a bearer, so a
/// per-caller budget is one budget for every user's login, drainable by unauthenticated junk
/// credentials. Throttling untrusted traffic belongs where it arrives -- at the asker, per client
/// -- and a throttled answer is never <see cref="IntrospectionRefusedException"/>, which a caller
/// may cache as definitive (it would negative-cache valid credentials); it is
/// <see cref="IdentityUnavailableException"/>. There was a limiter here, and its option, until
/// the 2026-09-18 revision of the cross-port spec removed both; this port has no published caller
/// that still passes the option, so it is gone outright rather than kept as a no-op.
/// </para>
/// </remarks>
public sealed class IdentityImpl : IIdentityProtocol
{
    /// <summary>Resolves an opaque credential to an identity.</summary>
    /// <param name="token">The credential to resolve.</param>
    /// <returns>The identity, or <see langword="null"/> when the store answered and the
    /// credential is unknown. Throw <see cref="IdentityUnavailableException"/> for "the answer is
    /// not knowable" -- a caller that negative-caches the first must not cache the second.</returns>
    public delegate TokenIdentity? TokenResolver(string token);

    /// <summary>Mints a standing grant for the calling principal.</summary>
    /// <param name="principal">The caller's authenticated principal -- never a client-supplied
    /// subject; see <see cref="IIdentityProtocol.IssueGrant"/>.</param>
    /// <param name="purpose">Why the grant is being minted.</param>
    /// <param name="scopes">What the grant may do; the worker defines their meaning.</param>
    /// <param name="ttlSeconds">The requested lifetime.</param>
    /// <returns>The minted grant.</returns>
    public delegate IssuedGrant GrantMinter(string principal, string purpose, List<string> scopes, long ttlSeconds);

    private static readonly IReadOnlySet<string> s_noPrincipals = new HashSet<string>(StringComparer.Ordinal);

    private readonly TokenResolver? _resolveToken;
    private readonly GrantMinter? _mintGrant;
    private readonly IReadOnlySet<string> _principals;
    private readonly double _maxAuthAge;

    /// <param name="resolveToken">Resolves an opaque credential; see <see cref="TokenResolver"/>.
    /// Omit it and <c>introspect_token</c> is not hosted.</param>
    /// <param name="mintGrant">Mints a grant; see <see cref="GrantMinter"/>. Omit it and
    /// <c>issue_grant</c> is not hosted.</param>
    /// <param name="introspectPrincipals">Who may call <c>introspect_token</c>. Required whenever
    /// <paramref name="resolveToken"/> is supplied; there is no permissive default.</param>
    /// <param name="maxAuthAge">How recently a caller must have authenticated to mint a grant,
    /// in seconds.</param>
    /// <exception cref="ArgumentException"><paramref name="resolveToken"/> was supplied without an
    /// allowlist. Validated at construction, not at first call: a worker that would refuse every
    /// introspection should fail to start rather than serve traffic until someone tries.</exception>
    public IdentityImpl(
        TokenResolver? resolveToken = null,
        GrantMinter? mintGrant = null,
        IEnumerable<string>? introspectPrincipals = null,
        double maxAuthAge = 900.0)
    {
        _resolveToken = resolveToken;
        _mintGrant = mintGrant;
        _maxAuthAge = maxAuthAge;
        _principals = resolveToken is null
            ? s_noPrincipals
            : IdentityGuards.NormalisePrincipals(introspectPrincipals);
    }

    /// <summary>Returns the methods this deployment can actually answer.</summary>
    /// <remarks>
    /// A method whose hook is absent is not registered, so the protocol a server hosts describes
    /// what it does. A worker that resolves credentials but does not mint grants offers
    /// <c>introspect_token</c> and not <c>issue_grant</c>, and a client learns that from
    /// reflection rather than by calling and reading an error.
    /// </remarks>
    public IReadOnlySet<string> OfferedMethods()
    {
        var offered = new HashSet<string>(StringComparer.Ordinal);
        if (_resolveToken is not null)
        {
            offered.Add(IdentityProtocol.IntrospectTokenMethod);
        }

        if (_mintGrant is not null)
        {
            offered.Add(IdentityProtocol.IssueGrantMethod);
        }

        return offered;
    }

    /// <summary>Resolves <paramref name="token"/>, after checking the caller may ask.</summary>
    /// <param name="token">The subject credential.</param>
    /// <param name="ctx">Framework-injected call context.</param>
    /// <returns>The resolved identity.</returns>
    /// <remarks>
    /// <b>The order of these guards is load bearing.</b> Authorization runs before anything looks
    /// at the subject credential -- before its length is measured, before
    /// its shape is matched, and before the resolver sees it -- because an unauthorized caller
    /// must learn nothing about that credential, including how long looking at it took. Do not
    /// reorder for tidiness: an unauthorized caller presenting an over-long or JWS-shaped token
    /// must still be told only that it is not an introspector.
    /// </remarks>
    public TokenIdentity IntrospectToken(string token, ICallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_resolveToken is null)
        {
            throw new IntrospectionRefusedException("this worker does not resolve credentials");
        }

        IdentityGuards.CheckIntrospector(ctx.Auth, _principals);
        IdentityGuards.RejectJwsShaped(token);

        var identity = _resolveToken(token);
        if (identity is null)
        {
            // Uniform with malformed and expired: reporting which would confirm that a guessed
            // credential exists.
            throw new TokenUnresolvedException("unresolved");
        }

        return identity;
    }

    /// <summary>Mints a grant for the caller, after checking they authenticated recently.</summary>
    /// <param name="purpose">Why the grant is being minted.</param>
    /// <param name="scopes">What the grant may do.</param>
    /// <param name="ttlSeconds">The requested lifetime.</param>
    /// <param name="ctx">Framework-injected call context.</param>
    /// <returns>The minted grant.</returns>
    public IssuedGrant IssueGrant(string purpose, List<string> scopes, long ttlSeconds, ICallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_mintGrant is null)
        {
            throw new GrantRefusedException("this worker does not mint grants");
        }

        IdentityGuards.CheckFreshness(ctx.Auth, _maxAuthAge);

        // The subject is the caller, never a parameter: cross-subject minting is closed by
        // construction rather than by a check that could be forgotten in one of seven ports.
        return _mintGrant(ctx.Auth.Principal ?? "", purpose, scopes, ttlSeconds);
    }
}
