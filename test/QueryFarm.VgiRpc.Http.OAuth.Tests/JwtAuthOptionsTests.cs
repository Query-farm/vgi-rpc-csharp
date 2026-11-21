using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.OAuth.Tests;

/// <summary>
/// <see cref="JwtAuth.Create(JwtAuthOptions)"/> -- the hardened, JWKS-URI form -- against a real
/// in-process JWKS endpoint whose <c>Cache-Control</c> and availability each test controls, with
/// tokens built byte by byte so the header (<c>alg</c>, <c>typ</c>, <c>kid</c>) and the claims'
/// JSON types are exactly what the test says.
/// </summary>
public sealed class JwtAuthOptionsTests : IAsyncLifetime
{
    private const string Issuer = "https://auth.orchard.test";
    private const string Audience = "https://desk.orchard.test/d/dep_123";
    private const string EcKid = "desk-es256-1";
    private const string RsaKid = "desk-rs256-1";

    private readonly ECDsa _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplication _jwks = null!;
    private string _jwksUri = null!;

    /// <summary>What the JWKS endpoint answers; tests change it between requests.</summary>
    private volatile string? _cacheControl;
    private volatile bool _fail;
    private volatile string _body = "";
    private int _hits;

    public async ValueTask InitializeAsync()
    {
        _body = Jwks(includeEc: true);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _jwks = builder.Build();
        _jwks.MapGet("/jwks", (HttpContext context) =>
        {
            Interlocked.Increment(ref _hits);
            if (_fail)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (_cacheControl is { } cacheControl)
            {
                context.Response.Headers.CacheControl = cacheControl;
            }

            return Results.Text(_body, "application/json");
        });
        await _jwks.StartAsync();
        _jwksUri = $"{_jwks.Urls.First()}/jwks";
    }

    public async ValueTask DisposeAsync()
    {
        await _jwks.StopAsync();
        await _jwks.DisposeAsync();
        _ec.Dispose();
        _rsa.Dispose();
    }

    private string Jwks(bool includeEc)
    {
        var keys = new List<object>();
        if (includeEc)
        {
            var q = _ec.ExportParameters(false).Q;
            keys.Add(new { kty = "EC", crv = "P-256", x = Base64UrlEncoder.Encode(q.X), y = Base64UrlEncoder.Encode(q.Y), kid = EcKid, use = "sig", alg = "ES256" });
        }

        var rsa = _rsa.ExportParameters(false);
        keys.Add(new { kty = "RSA", n = Base64UrlEncoder.Encode(rsa.Modulus), e = Base64UrlEncoder.Encode(rsa.Exponent), kid = RsaKid, use = "sig", alg = "RS256" });
        return JsonSerializer.Serialize(new { keys });
    }

    private static Dictionary<string, object?> Claims(Action<Dictionary<string, object?>>? change = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = "user_01M7KNJYC18T",
            ["kind"] = "user",
            ["email"] = "ada@example.com",
            ["email_verified"] = true,
            ["org_id"] = "org_01M7",
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 900,
        };
        change?.Invoke(claims);
        return claims;
    }

    private string Pass(Dictionary<string, object?>? claims = null, string alg = "ES256", string? typ = "at+jwt", string? kid = EcKid)
    {
        var header = new Dictionary<string, object?> { ["alg"] = alg };
        if (typ is not null)
        {
            header["typ"] = typ;
        }

        if (kid is not null)
        {
            header["kid"] = kid;
        }

        var signingInput = $"{Segment(header)}.{Segment(claims ?? Claims())}";
        var data = Encoding.ASCII.GetBytes(signingInput);
        var signature = alg switch
        {
            "ES256" => _ec.SignData(data, HashAlgorithmName.SHA256),
            "RS256" => _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "HS256" => HMACSHA256.HashData(Encoding.ASCII.GetBytes(new string('k', 32)), data),
            _ => [],
        };
        return $"{signingInput}.{Base64UrlEncoder.Encode(signature)}";
    }

    private static string Segment(Dictionary<string, object?> value) =>
        Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(value));

    private RpcHttpEndpoints.AuthenticateDelegate Hosted(TimeProvider? time = null) => JwtAuth.Create(new JwtAuthOptions
    {
        Issuers = [Issuer],
        Audiences = [Audience],
        JwksUri = _jwksUri,
        Domain = "orchard",
        Algorithms = ["ES256"],
        RequiredTyp = "at+jwt",
        RequireExp = true,
        SingleAudience = true,
        Leeway = TimeSpan.FromSeconds(60),
        JwksMaxAge = TimeSpan.FromMinutes(10),
        JwksRefreshMinInterval = TimeSpan.FromSeconds(60),
        JwksFetchTimeout = TimeSpan.FromSeconds(10),
        TimeProvider = time ?? TimeProvider.System,
    });

    private static DefaultHttpContext Bearer(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }

    private static async Task<AuthReason> Rejected(RpcHttpEndpoints.AuthenticateDelegate authenticate, string token) =>
        (await Assert.ThrowsAsync<AuthFailure>(() => authenticate(Bearer(token)))).Reason;

    [Fact]
    public async Task AValidPassPublishesItsClaimsInTheirJsonTypes()
    {
        var context = Bearer(Pass());
        await Hosted()(context);

        var auth = PeerIdentityAuthentication.GetAuth(context);
        Assert.True(auth.Authenticated);
        Assert.Equal("orchard", auth.Domain);
        Assert.Equal("user_01M7KNJYC18T", auth.Principal);
        Assert.Equal(true, auth.Claims["email_verified"]);
        Assert.IsType<long>(auth.Claims["exp"]);
        Assert.Equal("org_01M7", auth.Claims["org_id"]);
    }

    [Theory]
    [InlineData("at+jwt")]
    [InlineData("AT+JWT")]
    [InlineData("application/at+jwt")]
    public async Task TypMatchesCaseInsensitivelyWithOptionalApplicationPrefix(string typ) =>
        await Hosted()(Bearer(Pass(typ: typ)));

    [Theory]
    [InlineData("JWT")]
    [InlineData(null)]
    public async Task AnyOtherTypIsRefused(string? typ) =>
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(Hosted(), Pass(typ: typ)));

    [Fact]
    public async Task OnlyTheAllowedAlgorithmVerifies()
    {
        var authenticate = Hosted();
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(alg: "RS256", kid: RsaKid)));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(alg: "HS256")));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(alg: "none")));
    }

    [Theory]
    [InlineData("HS256")]
    [InlineData("none")]
    [InlineData("hs512")]
    public void NoneAndHmacAreRefusedAtConstruction(string alg) =>
        Assert.Throws<ArgumentException>(() => JwtAuth.Create(new JwtAuthOptions
        {
            Issuers = [Issuer],
            Audiences = [Audience],
            JwksUri = _jwksUri,
            Algorithms = ["ES256", alg],
        }));

    [Fact]
    public async Task IssuerAndAudienceMatchExactlyAndOnlyOneAudience()
    {
        var authenticate = Hosted();
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(Claims(c => c["iss"] = "https://evil.test"))));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(Claims(c => c["aud"] = Audience + "/"))));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(Claims(c => c["aud"] = "https://desk.orchard.test/d/other"))));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(Claims(c => c["aud"] = new[] { Audience, "https://desk.orchard.test/d/other" }))));
        await authenticate(Bearer(Pass(Claims(c => c["aud"] = new[] { Audience }))));
    }

    [Fact]
    public async Task ExpIsRequiredAndCheckedWithLeeway()
    {
        var authenticate = Hosted();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(Claims(c => c.Remove("exp")))));
        Assert.Equal(AuthReason.ExpiredCredential, await Rejected(authenticate, Pass(Claims(c =>
        {
            c["iat"] = now - 1200;
            c["nbf"] = now - 1200;
            c["exp"] = now - 120;
        }))));
        // Inside the 60 s leeway on both ends.
        await authenticate(Bearer(Pass(Claims(c =>
        {
            c["iat"] = now - 600;
            c["nbf"] = now - 600;
            c["exp"] = now - 30;
        }))));
        await authenticate(Bearer(Pass(Claims(c => c["nbf"] = now + 30))));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass(Claims(c => c["nbf"] = now + 300))));
    }

    [Fact]
    public async Task ATamperedOrMalformedPassIsRefused()
    {
        var authenticate = Hosted();
        var parts = Pass().Split('.');
        var forged = Segment(Claims(c => c["org_id"] = "org_EVIL"));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, $"{parts[0]}.{forged}.{parts[2]}"));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, "not-a-jwt"));
        Assert.Equal(AuthReason.MissingCredential,
            (await Assert.ThrowsAsync<AuthFailure>(() => authenticate(new DefaultHttpContext()))).Reason);
    }

    [Fact]
    public async Task KeysLiveAtMostTheMaxAgeOrAShorterCacheControl()
    {
        var time = new ManualTime();
        _cacheControl = "public, max-age=30";
        var authenticate = Hosted(time);
        await authenticate(Bearer(Pass()));
        await authenticate(Bearer(Pass()));
        Assert.Equal(1, _hits);
        time.Advance(TimeSpan.FromSeconds(31));
        await authenticate(Bearer(Pass()));
        Assert.Equal(2, _hits);

        // A longer max-age than 10 minutes is capped at 10 minutes.
        _cacheControl = "max-age=86400";
        time.Advance(TimeSpan.FromSeconds(31));
        await authenticate(Bearer(Pass()));
        Assert.Equal(3, _hits);
        time.Advance(TimeSpan.FromSeconds(599));
        await authenticate(Bearer(Pass()));
        Assert.Equal(3, _hits);
        time.Advance(TimeSpan.FromSeconds(2));
        await authenticate(Bearer(Pass()));
        Assert.Equal(4, _hits);
    }

    [Fact]
    public async Task AFailedRefetchAfterExpiryIsUnavailableNeverStale()
    {
        var time = new ManualTime();
        var authenticate = Hosted(time);
        await authenticate(Bearer(Pass()));
        _fail = true;
        // Within the maximum age, the cached keys serve without a fetch.
        time.Advance(TimeSpan.FromSeconds(599));
        await authenticate(Bearer(Pass()));
        time.Advance(TimeSpan.FromSeconds(2));
        var unavailable = await Assert.ThrowsAsync<AuthUnavailableException>(() => authenticate(Bearer(Pass())));
        Assert.True(unavailable.RetryAfterSeconds > 0);
        // Still unavailable on the next request: the expired keys were discarded.
        await Assert.ThrowsAsync<AuthUnavailableException>(() => authenticate(Bearer(Pass())));
        _fail = false;
        await authenticate(Bearer(Pass()));
    }

    [Fact]
    public async Task AnUnknownKidRefetchesAtMostOncePerInterval()
    {
        var time = new ManualTime();
        _body = Jwks(includeEc: false);
        var authenticate = Hosted(time);
        // First use fetches; the kid is unknown, and the refresh is rate limited (it was just fetched).
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass()));
        Assert.Equal(1, _hits);
        // The key appears at the endpoint (a rotation), but within 60 s nothing re-fetches.
        _body = Jwks(includeEc: true);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AuthReason.InvalidCredential, await Rejected(authenticate, Pass()));
        Assert.Equal(1, _hits);
        time.Advance(TimeSpan.FromSeconds(31));
        await authenticate(Bearer(Pass()));
        Assert.Equal(2, _hits);
    }

    [Fact]
    public async Task ADownJwksIsUnavailableNotUnauthorized()
    {
        _fail = true;
        await Assert.ThrowsAsync<AuthUnavailableException>(() => Hosted()(Bearer(Pass())));
    }

    /// <summary>A clock the test advances by hand.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
