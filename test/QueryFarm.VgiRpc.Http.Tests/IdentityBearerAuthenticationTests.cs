using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Identity;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>The identity bearer authenticators and their chain (IDENTITY_V1_SPEC.md §9.2–9.3).</summary>
public class IdentityBearerAuthenticationTests
{
    private static readonly GrantKeys s_keys = new([Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()], audience: "a", maxTtlSeconds: 3600);

    private static DefaultHttpContext Request(string? authorization)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    /// <summary>A resolver that answers for anything and records what it saw -- so a token that
    /// wrongly reaches it turns a 401 into a success (the resolvable-probe pattern).</summary>
    private sealed class Resolver
    {
        public List<string> Seen { get; } = [];

        public TokenIdentity? Resolve(string token)
        {
            Seen.Add(token);
            return token == "unknown" ? null : new TokenIdentity("resolved@example", "name");
        }
    }

    private static RpcHttpEndpoints.AuthenticateDelegate Chain(Resolver resolver) =>
        IdentityBearerAuthentication.Compose(null, s_keys, resolver.Resolve)!;

    [Fact]
    public async Task AGrantAuthenticatesAsItsPrincipalWithNoAuthTime()
    {
        var (token, claims) = SealedGrants.Mint(s_keys, "owner@example", ["read"], "nightly", 60);
        var context = Request($"Bearer {token}");
        await Chain(new Resolver())(context);
        var auth = PeerIdentityAuthentication.GetAuth(context);
        Assert.Equal(("grant", true, "owner@example"), (auth.Domain, auth.Authenticated, auth.Principal));
        Assert.Equal(claims.GrantId, auth.Claims["grant_id"]);
        Assert.False(auth.Claims.ContainsKey("auth_time"));
    }

    /// <summary>A bad vgig1. token is a 401 that stops the chain: it never reaches the resolver.</summary>
    [Fact]
    public async Task ABadGrantNeverFallsThroughToTheResolver()
    {
        var resolver = new Resolver();
        var (token, _) = SealedGrants.Mint(s_keys, "owner@example", [], "x", 60);
        var tampered = token[..^3] + (token[^3] == 'A' ? 'B' : 'A') + token[^2..];
        var failure = await Assert.ThrowsAsync<AuthFailure>(() => Chain(resolver)(Request($"Bearer {tampered}")));
        Assert.Equal(AuthReason.InvalidCredential, failure.Reason);
        Assert.Empty(resolver.Seen);
    }

    [Fact]
    public async Task AnExpiredGrantIsExpiredCredential()
    {
        var (token, _) = SealedGrants.Mint(s_keys, "owner@example", [], "x", 1, now: 1_000);
        var failure = await Assert.ThrowsAsync<AuthFailure>(() => Chain(new Resolver())(Request($"Bearer {token}")));
        Assert.Equal(AuthReason.ExpiredCredential, failure.Reason);
    }

    /// <summary>Without the exact prefix a token never reaches the grant verifier -- it is resolved.</summary>
    [Fact]
    public async Task PrefixRouting()
    {
        var resolver = new Resolver();
        var context = Request("Bearer vgig2.whatever");
        await Chain(resolver)(context);
        Assert.Equal("token", PeerIdentityAuthentication.GetAuth(context).Domain);
        Assert.Equal(["vgig2.whatever"], resolver.Seen);
    }

    [Fact]
    public async Task UnknownIs401AndNoCredentialStaysAnonymous()
    {
        await Assert.ThrowsAsync<AuthFailure>(() => Chain(new Resolver())(Request("Bearer unknown")));
        var context = Request(null);
        await Chain(new Resolver())(context);
        Assert.False(PeerIdentityAuthentication.GetAuth(context).Authenticated);
    }

    /// <summary>A JWS is never handed to the resolver.</summary>
    [Fact]
    public async Task AJwsIsNotResolved()
    {
        var resolver = new Resolver();
        await Assert.ThrowsAsync<AuthFailure>(() => Chain(resolver)(Request("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2lnbmF0dXJl")));
        Assert.Empty(resolver.Seen);
    }

    /// <summary>An outage is a 503 with the hook's hint, never a 401.</summary>
    [Fact]
    public async Task AnOutageIsUnavailableWithTheHooksHint()
    {
        var chain = IdentityBearerAuthentication.Compose(null, null, _ => throw new IdentityUnavailableException("down", 9))!;
        var error = await Assert.ThrowsAsync<AuthUnavailableException>(() => chain(Request("Bearer opaque")));
        Assert.Equal(9, error.RetryAfterSeconds);
    }

    /// <summary>The deployment's own authenticator runs first; "not mine" moves on.</summary>
    [Fact]
    public async Task TheDeploymentAuthenticatorRunsFirst()
    {
        var resolver = new Resolver();
        RpcHttpEndpoints.AuthenticateDelegate own = context =>
            context.Request.Headers.Authorization == "Bearer mine"
                ? Task.CompletedTask
                : throw new AuthFailure(AuthReason.InvalidCredential);
        var chain = IdentityBearerAuthentication.Compose(own, s_keys, resolver.Resolve)!;
        await chain(Request("Bearer mine"));
        Assert.Empty(resolver.Seen);
        await chain(Request("Bearer other"));
        Assert.Equal(["other"], resolver.Seen);
    }

    [Fact]
    public void NeitherSourceLeavesAuthenticateUnchanged()
    {
        RpcHttpEndpoints.AuthenticateDelegate own = _ => Task.CompletedTask;
        Assert.Same(own, IdentityBearerAuthentication.Compose(own, null, null));
    }
}
