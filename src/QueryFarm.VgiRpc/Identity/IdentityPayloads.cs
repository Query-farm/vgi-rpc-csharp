namespace QueryFarm.VgiRpc.Identity;

/// <summary>The identity an opaque credential authenticates as.</summary>
/// <remarks>
/// <para>
/// <b>Never carries claims.</b> A pass-through claims field would let a worker choose its
/// caller's tenant routing, its row scope, and its policy branch, and the asker derives
/// everything it needs from the principal alone. Do not add one.
/// </para>
/// <para>
/// A plain class with a parameterless constructor and settable properties, not a positional
/// record: the wire codec reconstructs a dataclass-equivalent through
/// <c>Activator.CreateInstance</c> plus property sets. Declaration order IS the inner schema's
/// field order, so reordering these properties changes the bytes on the wire.
/// </para>
/// </remarks>
public sealed class TokenIdentity
{
    /// <summary>Builds an empty instance -- the constructor the wire codec uses.</summary>
    public TokenIdentity()
    {
    }

    /// <param name="principal">See <see cref="Principal"/>.</param>
    /// <param name="tokenName">See <see cref="TokenName"/>.</param>
    /// <param name="ttlSeconds">See <see cref="TtlSeconds"/>.</param>
    public TokenIdentity(string principal, string tokenName = "", long ttlSeconds = 300)
    {
        Principal = principal;
        TokenName = tokenName;
        TtlSeconds = ttlSeconds;
    }

    /// <summary>
    /// The canonical principal, in the exact form the worker itself would derive -- so an asker
    /// that normalises differently does not authorize as one identity while the worker serves
    /// another. Required by the contract: there is no sensible default for "who is this".
    /// </summary>
    public string Principal { get; set; } = "";

    /// <summary>Human-readable name for the credential, for audit trails. Never the credential.</summary>
    public string TokenName { get; set; } = "";

    /// <summary>
    /// How long the answer may be cached. The caller does the caching. Treat it as an
    /// authorization window: for any path the asker serves without re-presenting the credential
    /// it is exactly that, and therefore also the revocation lag.
    /// </summary>
    public long TtlSeconds { get; set; } = 300;
}

/// <summary>A standing delegation credential.</summary>
/// <remarks>
/// OAuth cannot express durable delegation: it fuses the grant, the credential and the session
/// into one refresh token, so an IdP shortening session lifetime shortens the grant. This is the
/// durable record -- minted while the user is present, presented later by unattended automation
/// as an ordinary bearer.
/// </remarks>
public sealed class IssuedGrant
{
    /// <summary>Builds an empty instance -- the constructor the wire codec uses.</summary>
    public IssuedGrant()
    {
    }

    /// <param name="token">See <see cref="Token"/>.</param>
    /// <param name="expiresAt">See <see cref="ExpiresAt"/>.</param>
    /// <param name="grantId">See <see cref="GrantId"/>.</param>
    public IssuedGrant(string token, double expiresAt, string grantId = "")
    {
        Token = token;
        ExpiresAt = expiresAt;
        GrantId = grantId;
    }

    /// <summary>
    /// The credential. <b>Opaque to the framework</b> -- the worker owns the format entirely (a
    /// sealed envelope, a database row, or a credential brokered from the IdP are all equally
    /// valid and equally invisible here). Never parsed, never logged.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// Unix timestamp after which the worker will stop honouring the grant. Required
    /// <em>because</em> the framework cannot enforce it: the real lifetime lives inside the
    /// opaque token, so this is a declaration rather than an enforcement. A worker that must
    /// state a lifetime has thought about one.
    /// </summary>
    public double ExpiresAt { get; set; }

    /// <summary>
    /// Correlation handle for the audit trail. Not a credential and not secret -- it is what ties
    /// a mint record to later use.
    /// </summary>
    public string GrantId { get; set; } = "";
}
