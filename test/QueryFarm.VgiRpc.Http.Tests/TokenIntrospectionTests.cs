using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>Direct unit coverage for <see cref="TokenIntrospection"/> — the wire-behavior half is
/// covered end-to-end by the canonical TestTokenIntrospection/TestTokenIntrospectionOffMode
/// groups imported into test_csharp_conformance.py (see docs/roadmap.md M12).</summary>
public class TokenIntrospectionTests
{
    private const string Introspector = "conformance-introspector";
    private const string SubjectToken = "conformance-opaque-subject-token";
    private const string SubjectPrincipal = "subject@conformance.example";
    private const string JwsTrapToken = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2lnbmF0dXJl";
    private const string UnavailableToken = "conformance-unavailable-token";

    private static readonly IReadOnlySet<string> s_principals = new HashSet<string> { Introspector };

    private static TokenIntrospection.TokenResolver MakeResolver() => token => Task.FromResult(token switch
    {
        SubjectToken => new TokenIdentity(SubjectPrincipal),
        UnavailableToken => throw new AuthUnavailableException(),
        JwsTrapToken => new TokenIdentity("alice"), // resolvable — the shape guard must reject before reaching here
        _ => (TokenIdentity?)null,
    });

    private static DefaultHttpContext MakeContext(string? caller, object? body)
    {
        var context = new DefaultHttpContext();
        if (caller is not null)
        {
            AuthIdentity.SetOn(context, "conformance", caller);
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            context.Request.ContentLength = Encoding.UTF8.GetByteCount(json);
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task HandleAsync_ValidCredential_Resolves200()
    {
        var context = MakeContext(Introspector, new { token = SubjectToken });

        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(ResponseBody(context));
        Assert.Equal(SubjectPrincipal, body.GetProperty("principal").GetString());
        Assert.Equal(300, body.GetProperty("ttl_seconds").GetInt32());
    }

    [Fact]
    public async Task HandleAsync_ResponseCarriesNoClaims()
    {
        var context = MakeContext(Introspector, new { token = SubjectToken });

        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        var body = JsonSerializer.Deserialize<JsonElement>(ResponseBody(context));
        var keys = new HashSet<string>();
        foreach (var prop in body.EnumerateObject())
        {
            keys.Add(prop.Name);
        }

        Assert.DoesNotContain("claims", keys);
        Assert.True(keys.IsSubsetOf(new HashSet<string> { "principal", "token_name", "ttl_seconds" }));
    }

    [Fact]
    public async Task HandleAsync_UnknownCredential_404()
    {
        var context = MakeContext(Introspector, new { token = "no-such-credential" });

        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("no-such-credential")]
    [InlineData("expired-credential")]
    [InlineData("!!malformed!!")]
    public async Task HandleAsync_RejectionsAreIndistinguishable(string token)
    {
        var baselineContext = MakeContext(Introspector, new { token = "no-such-credential" });
        await TokenIntrospection.HandleAsync(baselineContext, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        var context = MakeContext(Introspector, new { token });
        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        Assert.Equal(baselineContext.Response.StatusCode, context.Response.StatusCode);
        Assert.Equal(ResponseBody(baselineContext), ResponseBody(context));
    }

    [Fact]
    public async Task HandleAsync_UnauthenticatedCaller_Refused()
    {
        var context = MakeContext(null, new { token = SubjectToken });

        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        Assert.Contains(context.Response.StatusCode, new[] { 401, 403, 404 });
        Assert.DoesNotContain(SubjectPrincipal, ResponseBody(context));
    }

    [Fact]
    public async Task HandleAsync_NonIntrospectorCaller_403()
    {
        var context = MakeContext("someone-else", new { token = SubjectToken });

        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.DoesNotContain(SubjectPrincipal, ResponseBody(context));
    }

    [Fact]
    public async Task HandleAsync_JwsShapedSubject_RejectedWithoutReachingResolver()
    {
        var resolverCalled = false;
        TokenIntrospection.TokenResolver resolver = token =>
        {
            resolverCalled = true;
            return Task.FromResult<TokenIdentity?>(new TokenIdentity("alice"));
        };
        var context = MakeContext(Introspector, new { token = JwsTrapToken });

        await TokenIntrospection.HandleAsync(context, resolver, s_principals, new IntrospectionRateLimiter(20));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(resolverCalled, "a JWS-shaped subject must be rejected before ever reaching the resolver");
    }

    [Theory]
    [InlineData(SubjectToken)]
    [InlineData("no-such-credential")]
    public async Task HandleAsync_CredentialNeverAppearsInResponse(string token)
    {
        var context = MakeContext(Introspector, new { token });

        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        Assert.DoesNotContain(token, ResponseBody(context));
    }

    [Fact]
    public async Task HandleAsync_ResolverOutage_503WithRetryAfter()
    {
        var context = MakeContext(Introspector, new { token = UnavailableToken });

        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.True(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.DoesNotContain(UnavailableToken, ResponseBody(context));
    }

    [Fact]
    public async Task HandleAsync_MalformedBody_TreatedAsUnresolved()
    {
        var context = new DefaultHttpContext();
        AuthIdentity.SetOn(context, "conformance", Introspector);
        context.Request.Body = new MemoryStream("not json"u8.ToArray());
        context.Response.Body = new MemoryStream();

        await TokenIntrospection.HandleAsync(context, MakeResolver(), s_principals, new IntrospectionRateLimiter(20));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_RateLimited_429()
    {
        var limiter = new IntrospectionRateLimiter(1);
        var first = MakeContext(Introspector, new { token = SubjectToken });
        await TokenIntrospection.HandleAsync(first, MakeResolver(), s_principals, limiter);

        var second = MakeContext(Introspector, new { token = SubjectToken });
        await TokenIntrospection.HandleAsync(second, MakeResolver(), s_principals, limiter);

        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
    }

    [Fact]
    public async Task HandleDisabledAsync_Returns404NotEnabled()
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await TokenIntrospection.HandleDisabledAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(ResponseBody(context));
        Assert.Equal("not_enabled", body.GetProperty("error").GetString());
    }

    [Fact]
    public void NormalizePrincipals_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => TokenIntrospection.NormalizePrincipals(null));
        Assert.Throws<ArgumentException>(() => TokenIntrospection.NormalizePrincipals([]));
    }

    [Fact]
    public void NormalizePrincipals_ValidSet_Returned()
    {
        var result = TokenIntrospection.NormalizePrincipals(["a", "b", ""]);

        Assert.Equal(new HashSet<string> { "a", "b" }, result);
    }

    [Fact]
    public void TokenDigest_IsStableAndNotTheToken()
    {
        var digest = TokenIntrospection.TokenDigest(SubjectToken);

        Assert.Equal(64, digest.Length); // hex SHA-256
        Assert.DoesNotContain(SubjectToken, digest);
        Assert.Equal(digest, TokenIntrospection.TokenDigest(SubjectToken));
    }

    [Fact]
    public void IntrospectionRateLimiter_AllowsUpToLimit()
    {
        var limiter = new IntrospectionRateLimiter(2);

        Assert.True(limiter.Allow("k"));
        Assert.True(limiter.Allow("k"));
        Assert.False(limiter.Allow("k"));
    }

    [Fact]
    public void IntrospectionRateLimiter_DifferentKeys_Independent()
    {
        var limiter = new IntrospectionRateLimiter(1);

        Assert.True(limiter.Allow("a"));
        Assert.True(limiter.Allow("b"));
    }

    [Fact]
    public void IntrospectionRateLimiter_WindowResets()
    {
        var clockValue = 0.0;
        var limiter = new IntrospectionRateLimiter(1);

        Assert.True(limiter.Allow("k", now: 0.0));
        Assert.False(limiter.Allow("k", now: 0.1));
        Assert.True(limiter.Allow("k", now: 2.0)); // past the 1s window
        _ = clockValue;
    }
}
