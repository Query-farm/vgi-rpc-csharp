using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Identity;

/// <summary>
/// <c>vgi_rpc.Identity.v1</c> -- resolving a credential, and minting a grant.
/// </summary>
/// <remarks>
/// <para>
/// Identity lives at the RPC layer rather than in any application protocol: a bearer token is
/// not an application concept, the auth primitives it builds on (<see cref="AuthContext"/>, the
/// peer-identity chain) are already here, and implementing it once is the whole point. It was
/// previously an HTTP JSON route, <c>POST {prefix}/__introspect_token__</c> (still present in
/// this port as <c>QueryFarm.VgiRpc.Http.TokenIntrospection</c>), which meant it existed only on
/// one transport and had to be hand-written in every port.
/// </para>
/// <para>
/// Two methods share one module's guards, and they are guarded <em>differently</em> on purpose.
/// <see cref="IntrospectToken"/> answers a question about somebody else's credential and is
/// therefore an oracle that has to be locked down; <see cref="IssueGrant"/> is always about the
/// caller and therefore is not. That asymmetry is the design.
/// </para>
/// <para>
/// The trailing <see cref="ICallContext"/> on each method is framework-injected and is not a wire
/// field, so it does not appear in the protocol hash -- but it is what carries the caller's
/// authentication, which is what every guard here reads.
/// </para>
/// </remarks>
public interface IIdentityProtocol
{
    /// <summary>Resolve an opaque bearer credential to the identity it authenticates as.</summary>
    /// <param name="token">The opaque credential. Never a JWS -- three-segment credentials are
    /// refused before reaching the resolver, because routing one onward would hand a third party
    /// a token the asker may itself have rejected.</param>
    /// <param name="ctx">Framework-injected call context, carrying the caller's authentication.</param>
    /// <returns>The resolved identity.</returns>
    /// <remarks>
    /// <para>
    /// For a reverse proxy that terminates the only public listener and must know <em>which
    /// principal</em> a credential is before it can authorize anything. When the credential is
    /// opaque the proxy holds no local copy and has to ask the worker.
    /// </para>
    /// <para>
    /// The answer is an identity assertion made by the thing being protected, which the asker
    /// then acts on using credentials the worker does not hold -- storage credentials,
    /// entitlement lookups, policy-tier selection. "Trust it as much as you trust the worker" is
    /// the wrong frame: it must be trusted <em>more</em>. Hence the guards: an introspector
    /// allowlist with no permissive default, uniform rejections, and a JWS-shaped subject refused
    /// before the resolver runs. Deliberately not rate limited: the allowlist is the control, and
    /// a per-caller budget on the asker is one budget for every user behind it (see
    /// <see cref="IdentityImpl"/>).
    /// </para>
    /// <para>
    /// Deliberately <em>not</em> "replay the credential through the worker's own authenticate
    /// chain": that would run an independently-configured audience and issuer set, cannot replay
    /// cookie- or mTLS-derived identity, and would silently elevate any address-allowlist member.
    /// </para>
    /// </remarks>
    TokenIdentity IntrospectToken(string token, ICallContext ctx);

    /// <summary>Mint a standing delegation credential for the <em>calling</em> user.</summary>
    /// <param name="purpose">Why the grant is being minted, for the audit trail.</param>
    /// <param name="scopes">What the grant may do. The worker decides what these mean; the
    /// framework neither interprets nor validates them.</param>
    /// <param name="ttlSeconds">Requested lifetime. A request, not an instruction -- the worker
    /// may return a shorter one, and the returned <c>expires_at</c> is authoritative.</param>
    /// <param name="ctx">Framework-injected call context, carrying the caller's authentication.</param>
    /// <returns>The minted grant.</returns>
    /// <remarks>
    /// <para>
    /// OAuth cannot express durable delegation: it fuses the grant, the credential and the
    /// session into one refresh token, so an IdP shortening session lifetime shortens the grant.
    /// This is the durable record -- minted while the user is present, presented later by
    /// unattended automation as an ordinary bearer.
    /// </para>
    /// <para>
    /// <b>There is no subject parameter.</b> The subject is always the caller's authenticated
    /// principal, so cross-subject minting is closed by construction rather than by a check that
    /// could be forgotten in one of seven ports. That is also why this method needs no allowlist
    /// while <see cref="IntrospectToken"/> has one: introspection resolves <em>other people's</em>
    /// credentials, so "any authenticated caller" is an open oracle there; issuance is always
    /// about the caller themselves. Do not add one.
    /// </para>
    /// <para>
    /// The caller must have authenticated recently -- see
    /// <see cref="IdentityGuards.CheckFreshness"/> for why a missing <c>auth_time</c> is what
    /// stops a grant minting another grant.
    /// </para>
    /// </remarks>
    IssuedGrant IssueGrant(string purpose, List<string> scopes, long ttlSeconds, ICallContext ctx);
}

/// <summary>Wire identity of <see cref="IIdentityProtocol"/>, and the narrowing its hosting uses.</summary>
public static class IdentityProtocol
{
    /// <summary>The wire name of the identity protocol.</summary>
    /// <remarks>
    /// Under the reserved <c>vgi_rpc.</c> prefix, like <see cref="ReflectionProtocol.ProtocolName"/>:
    /// framework-owned, so an application cannot register a protocol that impersonates it.
    /// </remarks>
    public const string ProtocolName = "vgi_rpc.Identity.v1";

    /// <summary>Wire name of <see cref="IIdentityProtocol.IntrospectToken"/>.</summary>
    public const string IntrospectTokenMethod = "introspect_token";

    /// <summary>Wire name of <see cref="IIdentityProtocol.IssueGrant"/>.</summary>
    public const string IssueGrantMethod = "issue_grant";

    /// <summary>The methods to host, narrowed to those whose hooks the deployment configured.</summary>
    /// <param name="offered">The subset from <see cref="IdentityImpl.OfferedMethods"/>.</param>
    /// <remarks>
    /// <b>A method whose hook the deployment did not configure is not hosted at all</b>, and the
    /// binding's method set -- and therefore its protocol hash -- narrows with it. A worker that
    /// resolves credentials but does not mint grants hosts <c>introspect_token</c> and not
    /// <c>issue_grant</c>, and a client discovers that through ordinary reflection rather than by
    /// calling and reading an error. A server offering half the methods is not offering the same
    /// surface, so it must not claim the same hash.
    /// </remarks>
    public static IReadOnlyDictionary<string, RpcMethodInfo> MethodsFor(IReadOnlySet<string> offered) =>
        ServiceRegistry.GetMethods(typeof(IIdentityProtocol))
            .Where(entry => offered.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
