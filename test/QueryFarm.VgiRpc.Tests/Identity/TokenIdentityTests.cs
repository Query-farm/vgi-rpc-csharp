using System;
using System.Collections.Generic;
using System.Linq;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Identity;

/// <summary>
/// <c>vgi_rpc.Identity.v1</c> -- resolving a credential, and minting a grant.
/// </summary>
/// <remarks>
/// The two methods are guarded very differently and the difference is the point, so most of what
/// is tested here is the <em>asymmetry</em>: introspection answers a question about somebody
/// else's credential and is therefore an oracle that has to be locked down; issuance is always
/// about the caller and therefore is not. A port of the canonical Python suite
/// (<c>tests/test_token_identity.py</c>), test for test.
/// </remarks>
public static class IdentityTestDoubles
{
    public static AuthContext Auth(string? principal = "alice", bool authenticated = true, double? authTime = null)
    {
        var claims = new Dictionary<string, object?>();
        if (authTime is not null)
        {
            claims["auth_time"] = authTime.Value;
        }

        return new AuthContext("test", authenticated, principal, claims);
    }

    public static ICallContext Ctx(AuthContext auth) => new StubContext(auth);

    public static TokenIdentity? Resolver(string token) =>
        token == "good" ? new TokenIdentity("bob", "ci-key") : null;

    public static IssuedGrant Minter(string principal, string purpose, List<string> scopes, long ttlSeconds) =>
        new($"grant-for-{principal}", Now() + ttlSeconds, "g1");

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private sealed class StubContext(AuthContext auth) : ICallContext
    {
        public AuthContext Auth => auth;

        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null)
        {
        }
    }
}

/// <summary>
/// The answer is an identity assertion the asker acts on with its own credentials.
/// </summary>
/// <remarks>
/// "Trust it as much as you trust the worker" is the wrong frame: the asker trusts it <em>more</em>,
/// because it authorizes with credentials the worker does not hold.
/// </remarks>
public class IntrospectionIsLockedDownTests
{
    private static IdentityImpl Impl(int rateLimit = 20) =>
        new(resolveToken: IdentityTestDoubles.Resolver, introspectPrincipals: ["proxy"], introspectRateLimit: rateLimit);

    /// <summary>The happy path, for the reverse proxy the method exists for.</summary>
    [Fact]
    public void ResolvesForAnAllowlistedCaller()
    {
        var got = Impl().IntrospectToken("good", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy")));
        Assert.Equal("bob", got.Principal);
        Assert.Equal("ci-key", got.TokenName);
    }

    /// <summary>Authentication is not the same capability as introspection.</summary>
    /// <remarks>
    /// A deployment where any valid credential may introspect lets any user test guesses of any
    /// other user's credential at unlimited rate, and resolve a stolen one to its owner.
    /// </remarks>
    [Theory]
    [InlineData("alice")]
    [InlineData("")]
    [InlineData(null)]
    public void ACallerOffTheAllowlistIsRefused(string? caller) =>
        Assert.Throws<IntrospectionRefusedException>(
            () => Impl().IntrospectToken("good", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth(caller))));

    /// <summary>Pipe, subprocess and unix transports carry no authenticated principal.</summary>
    [Fact]
    public void AnUnauthenticatedCallerIsRefused() =>
        Assert.Throws<IntrospectionRefusedException>(
            () => Impl().IntrospectToken(
                "good", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy", authenticated: false))));

    /// <summary>An unauthorized caller learns nothing, including how long it took.</summary>
    [Fact]
    public void RefusalPrecedesTheResolver()
    {
        var seen = new List<string>();
        var impl = new IdentityImpl(
            resolveToken: token => { seen.Add(token); return null; },
            introspectPrincipals: ["proxy"]);

        Assert.Throws<IntrospectionRefusedException>(
            () => impl.IntrospectToken("secret", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("mallory"))));
        Assert.Empty(seen);
    }

    /// <summary>
    /// The guard ORDER, pinned: authorization and the rate limit run before anything touches the
    /// subject credential -- before its length is measured and before its shape is matched.
    /// </summary>
    /// <remarks>
    /// An unauthorized caller presenting an over-long or JWS-shaped token must still be told only
    /// that it is not an introspector. If these guards were reordered "for tidiness" so the cheap
    /// shape checks came first, this caller would get <c>token_unresolved</c> instead -- which is
    /// a statement about the subject credential, made to someone with no right to any statement
    /// about it at all, and a timing side channel besides.
    /// </remarks>
    [Theory]
    [InlineData("aaa.bbb.ccc")]
    [InlineData("")]
    public void AuthorizationPrecedesEveryCheckOnTheSubjectCredential(string token)
    {
        var exc = Assert.Throws<IntrospectionRefusedException>(
            () => Impl().IntrospectToken(token, IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("mallory"))));
        Assert.Equal(MetadataKeys.ErrorKinds.IntrospectionRefused, exc.ErrorKind);
    }

    /// <summary>Same ordering claim, for the over-long case, which allocates before it rejects.</summary>
    [Fact]
    public void AuthorizationPrecedesTheLengthCheck() =>
        Assert.Throws<IntrospectionRefusedException>(
            () => Impl().IntrospectToken(
                new string('x', IdentityGuards.MaxTokenChars + 1),
                IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("mallory"))));

    /// <summary>The rate limit also precedes the shape checks, for the same reason.</summary>
    [Fact]
    public void TheRateLimitPrecedesTheShapeChecks()
    {
        var impl = Impl(rateLimit: 1);
        var ctx = IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy"));
        impl.IntrospectToken("good", ctx);

        var exc = Assert.Throws<IntrospectionRefusedException>(() => impl.IntrospectToken("aaa.bbb.ccc", ctx));
        Assert.Contains("rate limit", exc.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>Unknown, malformed and over-long are one answer.</summary>
    /// <remarks>Distinguishing them would confirm that a guessed credential exists.</remarks>
    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("oversized")]
    public void RejectionsAreUniform(string token)
    {
        var subject = token == "oversized" ? new string('x', IdentityGuards.MaxTokenChars + 1) : token;
        var exc = Assert.Throws<TokenUnresolvedException>(
            () => Impl().IntrospectToken(subject, IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy"))));
        Assert.Equal("unresolved", exc.ErrorMessage);
    }

    /// <summary>Routing one onward hands a third party a token the asker may have rejected.</summary>
    /// <remarks>
    /// A JWS is validated locally against a key set. Forwarding one the asker already refused --
    /// expired, wrong audience -- to something that might accept it turns this method into a
    /// laundering step.
    /// </remarks>
    [Fact]
    public void AJwsNeverReachesTheResolver()
    {
        var seen = new List<string>();
        var impl = new IdentityImpl(
            resolveToken: token => { seen.Add(token); return new TokenIdentity("bob"); },
            introspectPrincipals: ["proxy"]);

        Assert.Throws<TokenUnresolvedException>(
            () => impl.IntrospectToken("aaa.bbb.ccc", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy"))));
        Assert.Empty(seen);
    }

    /// <summary>A caller that negative-caches "unknown" must not cache this.</summary>
    /// <remarks>
    /// Cache an outage and a worker restart takes the fleet down for the cache's lifetime; retry
    /// a rejection and the worker is hammered.
    /// </remarks>
    [Fact]
    public void UnavailableIsTransientNotDefinitive()
    {
        var impl = new IdentityImpl(
            resolveToken: _ => throw new IdentityUnavailableException("store is down"),
            introspectPrincipals: ["proxy"]);

        var exc = Assert.Throws<IdentityUnavailableException>(
            () => impl.IntrospectToken("good", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy"))));

        Assert.True(exc.RetryAfterSeconds > 0);

        // Must not be catchable as the invalid-argument member of the taxonomy, nor as a
        // permission-denied refusal, nor as a CLR ArgumentException. Python keeps it off
        // ValueError because chain_authenticate advances on ValueError, so a sidecar outage
        // raised as one reads as "not my credential, try the next" and becomes a 401 from the
        // end of the chain -- restarting every session in the fleet over a thirty-second blip.
        Assert.IsNotAssignableFrom<TokenUnresolvedException>(exc);
        Assert.IsNotAssignableFrom<IdentityRefusedException>(exc);
        Assert.IsNotAssignableFrom<ArgumentException>(exc);
    }

    /// <summary>Bounds, rather than closes, the oracle an allowlisted caller still has.</summary>
    [Fact]
    public void RateLimited()
    {
        var impl = Impl(rateLimit: 2);
        var ctx = IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy"));

        Assert.Equal("bob", impl.IntrospectToken("good", ctx).Principal);
        Assert.Equal("bob", impl.IntrospectToken("good", ctx).Principal);

        var exc = Assert.Throws<IntrospectionRefusedException>(() => impl.IntrospectToken("good", ctx));
        Assert.Contains("rate limit", exc.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>There is no permissive default, so it cannot be reached by omission.</summary>
    [Fact]
    public void AnAllowlistIsMandatory()
    {
        var omitted = Assert.Throws<ArgumentException>(
            () => new IdentityImpl(resolveToken: IdentityTestDoubles.Resolver));
        Assert.Contains("at least one principal", omitted.Message, StringComparison.Ordinal);

        var empty = Assert.Throws<ArgumentException>(
            () => new IdentityImpl(resolveToken: IdentityTestDoubles.Resolver, introspectPrincipals: []));
        Assert.Contains("at least one principal", empty.Message, StringComparison.Ordinal);
    }
}

/// <summary>Issuance is always about the caller, so it needs neither allowlist nor limit.</summary>
public class IssuanceIsNotAnOracleTests
{
    /// <summary>The happy path: a present user minting their own standing grant.</summary>
    [Fact]
    public void MintsForTheCaller()
    {
        var impl = new IdentityImpl(mintGrant: IdentityTestDoubles.Minter);
        var grant = impl.IssueGrant(
            "reports", ["read"], 3600,
            IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("alice", authTime: IdentityTestDoubles.Now())));

        Assert.Equal("grant-for-alice", grant.Token);
        Assert.True(grant.ExpiresAt > IdentityTestDoubles.Now());
    }

    /// <summary>Cross-subject minting is closed by construction, not by a check.</summary>
    /// <remarks>A check is something one of seven ports can forget; a missing parameter is not.</remarks>
    [Fact]
    public void TheSubjectIsTheCallerAndIsNotAParameter()
    {
        var names = typeof(IIdentityProtocol)
            .GetMethod(nameof(IIdentityProtocol.IssueGrant))!
            .GetParameters()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain("subject", names);
        Assert.DoesNotContain("principal", names);
    }

    /// <summary>Unlike introspection -- and the asymmetry is the whole design.</summary>
    [Fact]
    public void NeedsNoAllowlist() =>
        Assert.Equal(
            new[] { "issue_grant" },
            new IdentityImpl(mintGrant: IdentityTestDoubles.Minter).OfferedMethods().Order().ToArray());
}

/// <summary>A credential with no verifiable auth_time cannot mint.</summary>
public class FreshnessTests
{
    private static IdentityImpl Impl() => new(mintGrant: IdentityTestDoubles.Minter, maxAuthAge: 900.0);

    /// <summary>A static bearer proves a machine holds a secret, never that a human just logged in.</summary>
    [Fact]
    public void AbsentAuthTimeIsRefused()
    {
        var exc = Assert.Throws<StaleAuthException>(
            () => Impl().IssueGrant("p", [], 60, IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("alice"))));
        Assert.Contains("no auth_time", exc.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>Naming the reason leaks nothing here: it is always about the caller.</summary>
    /// <remarks>
    /// A console that cannot tell "your login is too old" from "no" cannot know to re-prompt.
    /// </remarks>
    [Fact]
    public void StaleAuthTimeIsRefusedActionably()
    {
        var exc = Assert.Throws<StaleAuthException>(
            () => Impl().IssueGrant(
                "p", [], 60,
                IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("alice", authTime: IdentityTestDoubles.Now() - 5000))));
        Assert.Contains("re-authenticate", exc.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>The ceiling is a ceiling, not an equality.</summary>
    [Fact]
    public void FreshAuthTimeIsAccepted()
    {
        var grant = Impl().IssueGrant(
            "p", [], 60,
            IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("alice", authTime: IdentityTestDoubles.Now() - 10)));
        Assert.Equal("grant-for-alice", grant.Token);
    }

    /// <summary>The lineage cannot escape the identity provider.</summary>
    /// <remarks>
    /// A grant is not an IdP-issued token, so it carries no <c>auth_time</c>, so presenting one
    /// here fails the freshness check. That single rule is what stops indefinite self-renewal.
    /// </remarks>
    [Fact]
    public void AGrantCannotMintAnotherGrant()
    {
        // No auth_time: this is what a grant looks like.
        var grantBearer = IdentityTestDoubles.Auth("alice");
        Assert.Throws<StaleAuthException>(
            () => Impl().IssueGrant("p", [], 60, IdentityTestDoubles.Ctx(grantBearer)));
    }

    /// <summary>Pipe, subprocess and unix have no authenticated principal at all.</summary>
    [Fact]
    public void UnauthenticatedTransportFailsClosed()
    {
        var exc = Assert.Throws<StaleAuthException>(
            () => Impl().IssueGrant(
                "p", [], 60,
                IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth(null, authenticated: false))));
        Assert.Contains("not authenticated", exc.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>A claim that is present but not a number is refused, never coerced.</summary>
    /// <remarks>
    /// Reading garbage as zero would treat every caller as having authenticated in 1970 -- which
    /// fails open in exactly the direction this guard exists to close.
    /// </remarks>
    [Fact]
    public void UnusableAuthTimeIsRefused()
    {
        var auth = new AuthContext(
            "test", authenticated: true, principal: "alice",
            claims: new Dictionary<string, object?> { ["auth_time"] = "not-a-number" });
        var exc = Assert.Throws<StaleAuthException>(
            () => Impl().IssueGrant("p", [], 60, IdentityTestDoubles.Ctx(auth)));
        Assert.Contains("unusable auth_time", exc.ErrorMessage, StringComparison.Ordinal);
    }
}

/// <summary>Calling a method the deployment did not configure.</summary>
public class AbsentHooksTests
{
    /// <summary>Refused rather than crashing, for a caller that reached it anyway.</summary>
    [Fact]
    public void IntrospectionWithoutAResolver()
    {
        var exc = Assert.Throws<IntrospectionRefusedException>(
            () => new IdentityImpl(mintGrant: IdentityTestDoubles.Minter)
                .IntrospectToken("good", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy"))));
        Assert.Contains("does not resolve", exc.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>Same, on the other side.</summary>
    [Fact]
    public void IssuanceWithoutAMinter()
    {
        var impl = new IdentityImpl(
            resolveToken: IdentityTestDoubles.Resolver, introspectPrincipals: ["proxy"]);
        var exc = Assert.Throws<GrantRefusedException>(
            () => impl.IssueGrant(
                "p", [], 60,
                IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("alice", authTime: IdentityTestDoubles.Now()))));
        Assert.Contains("does not mint", exc.ErrorMessage, StringComparison.Ordinal);
    }
}

/// <summary>The credential must never reach a log, a span, or an error message.</summary>
public class IdentityDiagnosticsTests
{
    /// <summary>Stable enough to correlate one credential's failures; not the credential.</summary>
    [Fact]
    public void TheDigestIsNotTheToken()
    {
        Assert.NotEqual("secret", IdentityGuards.TokenDigest("secret"));
        Assert.Equal(IdentityGuards.TokenDigest("secret"), IdentityGuards.TokenDigest("secret"));
        Assert.NotEqual(IdentityGuards.TokenDigest("other"), IdentityGuards.TokenDigest("secret"));
        Assert.Equal(64, IdentityGuards.TokenDigest("secret").Length);
    }

    /// <summary>They are the only definitive/transient signal a caller has.</summary>
    /// <remarks>
    /// These were an HTTP route whose callers classified on the status code (404 vs 503). As
    /// protocol methods every handler exception surfaces the same way, so <c>error_kind</c>
    /// carries the whole distinction.
    /// </remarks>
    [Fact]
    public void ErrorKindsAreStable()
    {
        Assert.Equal("introspection_refused", IntrospectionRefusedException.ErrorKindConst);
        Assert.Equal("token_unresolved", TokenUnresolvedException.ErrorKindConst);
        Assert.Equal("stale_auth", StaleAuthException.ErrorKindConst);
        Assert.Equal("grant_refused", GrantRefusedException.ErrorKindConst);
        Assert.Equal("identity_unavailable", IdentityUnavailableException.ErrorKindConst);
    }

    /// <summary>An instance carries its class's kind, so it survives onto the wire.</summary>
    /// <remarks>
    /// <see cref="LogMessage.FromException"/> hoists an <see cref="RpcException.ErrorKind"/> to
    /// the top-level <c>vgi_rpc.error_kind</c> metadata key; a kind declared but never attached
    /// to the instance would be a constant nobody ever reads.
    /// </remarks>
    [Fact]
    public void ErrorKindsRideOnTheInstance()
    {
        Assert.Equal("introspection_refused", new IntrospectionRefusedException("x").ErrorKind);
        Assert.Equal("token_unresolved", new TokenUnresolvedException("x").ErrorKind);
        Assert.Equal("stale_auth", new StaleAuthException("x").ErrorKind);
        Assert.Equal("grant_refused", new GrantRefusedException("x").ErrorKind);
        Assert.Equal("identity_unavailable", new IdentityUnavailableException("x").ErrorKind);

        var metadata = LogMessage.FromException(new TokenUnresolvedException("unresolved")).AddToMetadata();
        Assert.Equal("token_unresolved", metadata["vgi_rpc.error_kind"]);
    }

    /// <summary>The wire error type is the cross-language spelling, not this port's class name.</summary>
    [Fact]
    public void WireErrorTypesAreThePortableSpelling()
    {
        Assert.Equal("IntrospectionRefusedError", new IntrospectionRefusedException("x").ErrorType);
        Assert.Equal("TokenUnresolvedError", new TokenUnresolvedException("x").ErrorType);
        Assert.Equal("StaleAuthError", new StaleAuthException("x").ErrorType);
        Assert.Equal("GrantRefusedError", new GrantRefusedException("x").ErrorType);
        Assert.Equal("IdentityUnavailableError", new IdentityUnavailableException("x").ErrorType);
    }
}

/// <summary>Fixed-window, because the state is two integers rather than an aged float.</summary>
public class IdentityRateLimiterTests
{
    /// <summary>Within a window.</summary>
    [Fact]
    public void AdmitsUpToTheLimit()
    {
        var limiter = new IdentityRateLimiter(3);
        Assert.Equal(
            new[] { true, true, true, false },
            Enumerable.Range(0, 4).Select(_ => limiter.Allow("a", now: 100.0)).ToArray());
    }

    /// <summary>A new window resets the count.</summary>
    [Fact]
    public void WindowRolls()
    {
        var limiter = new IdentityRateLimiter(1);
        Assert.True(limiter.Allow("a", now: 100.0));
        Assert.False(limiter.Allow("a", now: 100.5));
        Assert.True(limiter.Allow("a", now: 101.5));
    }

    /// <summary>One caller exhausting its budget must not refuse another.</summary>
    [Fact]
    public void CallersAreIndependent()
    {
        var limiter = new IdentityRateLimiter(1);
        Assert.True(limiter.Allow("a", now: 100.0));
        Assert.True(limiter.Allow("b", now: 100.0));
        Assert.False(limiter.Allow("a", now: 100.0));
    }

    /// <summary>Whole-map reset rather than per-key ageing, so an attacker cannot grow the map.</summary>
    /// <remarks>
    /// Per-key ageing would let a caller cycling keys grow the map without bound between sweeps.
    /// </remarks>
    [Fact]
    public void CyclingKeysCannotGrowTheMap()
    {
        var limiter = new IdentityRateLimiter(1);
        for (var i = 0; i < 1000; i++)
        {
            limiter.Allow($"k{i}", now: 100.0);
        }

        limiter.Allow("fresh", now: 200.0);
        Assert.Equal(1, limiter.TrackedKeyCount);
    }

    /// <summary>Safe under this port's concurrency model.</summary>
    /// <remarks>
    /// The limiter is shared by every connection a server is handling, and every transport in
    /// this port dispatches concurrently. A limiter that lost increments under contention would
    /// admit more than its budget precisely when it is being hammered -- which is the only time
    /// it matters. The assertion is on the exact admitted count, not on "no exception thrown":
    /// a torn read-modify-write does not throw, it over-admits.
    /// </remarks>
    [Fact]
    public void IsThreadSafe()
    {
        const int budget = 500;
        var limiter = new IdentityRateLimiter(budget);
        var admitted = 0;

        System.Threading.Tasks.Parallel.For(0, 4000, _ =>
        {
            if (limiter.Allow("a", now: 100.0))
            {
                System.Threading.Interlocked.Increment(ref admitted);
            }
        });

        Assert.Equal(budget, admitted);
    }
}

/// <summary>Whitespace must not be a way to walk a JWS past the guard.</summary>
/// <remarks>
/// <para>
/// The shape test runs against the trimmed credential while the resolver still receives what the
/// caller sent, so trimming can only add refusals.
/// </para>
/// <para>
/// This exists because the ports diverged here and the reference was the accident. Measured
/// against this port before the fix: .NET's <c>$</c> matches before a single trailing newline, so
/// <c>"a.b.c\n"</c> was refused -- while <c>"a.b.c\n\n"</c>, <c>"a.b.c\r\n"</c> and
/// <c>"  a.b.c  "</c> all went straight to the resolver, which is the one outcome the guard
/// exists to prevent. Python had the identical inconsistency; Go's <c>\A..\z</c> and
/// JavaScript's unflagged <c>$</c> refused none of them. <c>"a.b.c\r\n"</c> is the one that
/// should worry a .NET port specifically: CRLF is what its own Windows CI matrix runs on.
/// Trimming first is the rule that means the same thing in seven regex dialects.
/// </para>
/// </remarks>
public class JwsShapeTestSurvivesTranslationTests
{
    /// <summary>No amount of surrounding whitespace makes a JWS resolvable.</summary>
    [Theory]
    [InlineData("aaa.bbb.ccc")]
    [InlineData("aaa.bbb.ccc\n")]
    [InlineData("aaa.bbb.ccc\n\n")]
    [InlineData("  aaa.bbb.ccc  ")]
    [InlineData("\taaa.bbb.ccc\r\n")]
    public void PaddingDoesNotSmuggleAJwsPastTheGuard(string token) =>
        Assert.Throws<TokenUnresolvedException>(() => IdentityGuards.RejectJwsShaped(token));

    /// <summary>Whitespace-only never reaches a resolver either.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData("\t\r\n")]
    public void ABlankCredentialIsNotACredential(string token) =>
        Assert.Throws<TokenUnresolvedException>(() => IdentityGuards.RejectJwsShaped(token));

    /// <summary>Trimming tightens the JWS test; it must not refuse ordinary tokens.</summary>
    [Theory]
    [InlineData("opaque-token")]
    [InlineData("a.b.c.d")]
    [InlineData("two.segments")]
    [InlineData("sk_live_abc123")]
    public void AnOpaqueCredentialStillReachesTheResolver(string token) =>
        IdentityGuards.RejectJwsShaped(token);

    /// <summary>The length cap is still measured against the original, not the trimmed form.</summary>
    /// <remarks>
    /// Trimming is a test-time view of the credential, never a rewrite of it -- so padding cannot
    /// buy a caller room under the cap either.
    /// </remarks>
    [Fact]
    public void TheLengthCapMeasuresTheOriginal()
    {
        var padded = "  " + new string('x', IdentityGuards.MaxTokenChars - 1) + "  ";
        Assert.Equal(IdentityGuards.MaxTokenChars + 3, padded.Length);
        Assert.Throws<TokenUnresolvedException>(() => IdentityGuards.RejectJwsShaped(padded));
    }

    /// <summary>Trimming is for the shape test only -- never for what is resolved.</summary>
    /// <remarks>
    /// Rewriting a credential before resolving it would make the worker answer about a string the
    /// caller never sent.
    /// </remarks>
    [Fact]
    public void TheResolverReceivesTheCredentialUnmodified()
    {
        var seen = new List<string>();
        var impl = new IdentityImpl(
            resolveToken: token => { seen.Add(token); return new TokenIdentity("p"); },
            introspectPrincipals: ["proxy"]);

        impl.IntrospectToken("  padded-opaque-token  ", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy")));

        Assert.Equal(["  padded-opaque-token  "], seen);
    }
}
