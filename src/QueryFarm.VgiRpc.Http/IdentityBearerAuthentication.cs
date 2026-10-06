using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Bearer authenticators that close the <c>vgi_rpc.Identity.v1</c> loop (WIRE_PROTOCOL.md §16,
/// "Accepting identity credentials"; IDENTITY_V1_SPEC.md §9).
/// </summary>
/// <remarks>
/// <para>
/// <c>issue_grant</c> mints a credential for automation to present later as an ordinary bearer,
/// and <c>resolve_token</c> answers which principal an opaque credential is -- but until these,
/// neither fed back into authentication. <see cref="GrantAuthenticate"/> accepts the framework's
/// own sealed grants; <see cref="ResolveTokenAuthenticate"/> asks the worker's resolver;
/// <see cref="Compose"/> puts them after the deployment's own authenticator in the normative order,
/// and <c>MapVgiRpc</c> calls it automatically for a server hosting identity with grant keys or a
/// resolver.
/// </para>
/// <para>
/// <b>Chain semantics.</b> A member that throws an <see cref="AuthFailure"/> is saying "not my
/// credential" and the next member tries; one that throws an <see cref="AuthFailure"/> with
/// <see cref="AuthFailure.StopsChain"/> set ends the chain with a 401; an
/// <see cref="AuthUnavailableException"/> ends it with a 503. A member that returns normally
/// accepts (anonymously, if it published no identity).
/// </para>
/// </remarks>
public static class IdentityBearerAuthentication
{
    /// <summary><c>AuthContext.Domain</c> of a grant-authenticated request.</summary>
    public const string GrantAuthDomain = "grant";

    /// <summary><c>AuthContext.Domain</c> of a request authenticated through <c>resolve_token</c>.</summary>
    public const string TokenAuthDomain = "token";

    private const string BearerPrefix = "Bearer ";

    /// <summary>Tries each member in order; see the class remarks.</summary>
    public static RpcHttpEndpoints.AuthenticateDelegate Chain(params RpcHttpEndpoints.AuthenticateDelegate[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Length == 0)
        {
            throw new ArgumentException("a chain needs at least one member", nameof(members));
        }

        return async context =>
        {
            AuthFailure? last = null;
            foreach (var member in members)
            {
                try
                {
                    await member(context).ConfigureAwait(false);
                    return;
                }
                catch (AuthFailure failure) when (!failure.StopsChain)
                {
                    last = failure;
                }
            }

            throw last!;
        };
    }

    /// <summary>Accepts this deployment's own sealed grants as bearer credentials.</summary>
    /// <remarks>
    /// The resulting <see cref="AuthContext"/> has domain <c>"grant"</c>, the grant's principal, and
    /// claims <c>{grant_id, scopes, purpose}</c> -- and <b>no <c>auth_time</c></b>, so a
    /// grant-authenticated caller cannot <c>issue_grant</c>: grants never mint grants. A bearer
    /// without the exact <c>vgig1.</c> prefix never reaches the verifier (the chain moves on); one
    /// with it that does not verify is a 401 that stops the chain (<c>expired_credential</c> only
    /// for an authentic grant outside its lifetime, <c>invalid_credential</c> for everything else).
    /// </remarks>
    public static RpcHttpEndpoints.AuthenticateDelegate GrantAuthenticate(GrantKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return context =>
        {
            var token = Bearer(context);
            if (!token.StartsWith(SealedGrants.TokenPrefix, StringComparison.Ordinal))
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "not a sealed grant");
            }

            GrantClaims claims;
            try
            {
                claims = SealedGrants.Verify(keys, token);
            }
            catch (GrantInvalidException exc)
            {
                throw new AuthFailure(
                    exc.Expired ? AuthReason.ExpiredCredential : AuthReason.InvalidCredential,
                    "sealed grant rejected", stopsChain: true);
            }

            PeerIdentityAuthentication.SetAuth(context, new AuthContext(
                GrantAuthDomain, authenticated: true, claims.Principal,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["grant_id"] = claims.GrantId,
                    ["scopes"] = claims.Scopes.ToList(),
                    ["purpose"] = claims.Purpose,
                }));
            return Task.CompletedTask;
        };
    }

    /// <summary>Accepts bearer credentials the worker's <c>resolve_token</c> resolves.</summary>
    /// <remarks>
    /// A resolved identity authenticates with domain <c>"token"</c>, its principal, and
    /// <c>{"token_name": ...}</c> (no <c>auth_time</c>). <see langword="null"/> means unknown: the
    /// chain falls through, ending in 401 if nothing else accepts. An outage --
    /// <see cref="AuthUnavailableException"/> or <see cref="IdentityUnavailableException"/> from the
    /// hook -- is a 503 with the hook's <c>Retry-After</c>, never a 401, so a blip does not log a
    /// fleet out. The hook never sees a <c>vgig1.</c> token, a JWS-shaped token, a blank one, or one
    /// over 4096 UTF-8 bytes -- the shape guards introspection applies.
    /// </remarks>
    public static RpcHttpEndpoints.AuthenticateDelegate ResolveTokenAuthenticate(IdentityImpl.TokenResolver resolveToken)
    {
        ArgumentNullException.ThrowIfNull(resolveToken);
        return context =>
        {
            var token = Bearer(context);
            if (token.StartsWith(SealedGrants.TokenPrefix, StringComparison.Ordinal))
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "sealed grants are not resolved by resolve_token");
            }

            try
            {
                // Size, blank and JWS shape -- the same guard introspect_token applies.
                IdentityGuards.RejectJwsShaped(token);
            }
            catch (TokenUnresolvedException)
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "bearer credential rejected");
            }

            TokenIdentity? identity;
            try
            {
                identity = resolveToken(token);
            }
            catch (IdentityUnavailableException exc)
            {
                throw new AuthUnavailableException(
                    string.IsNullOrEmpty(exc.Detail) ? "identity lookup unavailable" : exc.Detail, exc.RetryAfterSeconds);
            }

            if (identity is null)
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "bearer credential did not resolve");
            }

            PeerIdentityAuthentication.SetAuth(context, new AuthContext(
                TokenAuthDomain, authenticated: true, identity.Principal,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["token_name"] = identity.TokenName }));
            return Task.CompletedTask;
        };
    }

    /// <summary>Appends the identity bearer authenticators after the deployment's own, in the
    /// normative order: <paramref name="authenticate"/>, then sealed grants, then
    /// <c>resolve_token</c> (IDENTITY_V1_SPEC.md §9.3).</summary>
    /// <param name="authenticate">The deployment's authenticator, or <see langword="null"/>. It must
    /// throw <see cref="AuthFailure"/> for a credential it does not recognise -- one that answers
    /// anonymous for everything ends the chain first.</param>
    /// <param name="grantKeys">Sealed-grant configuration, or <see langword="null"/>.</param>
    /// <param name="resolveToken">The worker's resolver, or <see langword="null"/>.</param>
    /// <returns>The composed authenticator; <paramref name="authenticate"/> unchanged with neither
    /// identity source. With no <paramref name="authenticate"/>, a request carrying no
    /// <c>Authorization</c> header stays anonymous exactly as before, and one carrying a credential
    /// nothing accepts is 401.</returns>
    public static RpcHttpEndpoints.AuthenticateDelegate? Compose(
        RpcHttpEndpoints.AuthenticateDelegate? authenticate, GrantKeys? grantKeys, IdentityImpl.TokenResolver? resolveToken)
    {
        var members = new List<RpcHttpEndpoints.AuthenticateDelegate>();
        if (grantKeys is not null)
        {
            members.Add(GrantAuthenticate(grantKeys));
        }

        if (resolveToken is not null)
        {
            members.Add(ResolveTokenAuthenticate(resolveToken));
        }

        if (members.Count == 0)
        {
            return authenticate;
        }

        if (authenticate is null)
        {
            members.Add(AnonymousWithoutCredentials);
            return Chain([.. members]);
        }

        return Chain([authenticate, .. members]);
    }

    private static Task AnonymousWithoutCredentials(HttpContext context) =>
        string.IsNullOrEmpty(context.Request.Headers.Authorization.ToString())
            ? Task.CompletedTask
            : throw new AuthFailure(AuthReason.InvalidCredential, "bearer credential not accepted");

    private static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            throw new AuthFailure(AuthReason.MissingCredential, "Missing Authorization header");
        }

        return header.StartsWith(BearerPrefix, StringComparison.Ordinal)
            ? header[BearerPrefix.Length..]
            : throw new AuthFailure(AuthReason.InvalidCredential, "Authorization header is not a Bearer credential");
    }
}
