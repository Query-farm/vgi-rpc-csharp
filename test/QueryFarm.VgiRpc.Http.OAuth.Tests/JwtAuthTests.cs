using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.OAuth.Tests;

/// <summary>
/// End-to-end: a real in-process Kestrel host serves OIDC discovery + JWKS, JwtAuth.Create's
/// delegate fetches from it over real HTTP (not a mocked key resolver), and real JWTs (signed
/// with an RSA key generated for the test) are validated against it. The only thing not "real"
/// here is the identity provider itself — everything downstream of the discovery URL is the
/// actual framework code path a production deployment uses.
/// </summary>
public sealed class JwtAuthTests : IAsyncLifetime
{
    private const string Issuer = "http://localhost:0/"; // overwritten with the real bound port in InitializeAsync
    private const string Audience = "vgi-rpc-test-audience";
    private const string KeyId = "test-key-1";

    private RSA _rsa = null!;
    private WebApplication _idp = null!;
    private string _issuer = null!;

    public async ValueTask InitializeAsync()
    {
        _rsa = RSA.Create(2048); // full keypair — used for *signing* test tokens.

        // The JWKS endpoint must publish the *public* key only — JsonWebKeyConverter pulls
        // whatever's in the RSA instance handed to it, so a public-only export here (as any real
        // JWKS endpoint would serve) is what a client actually fetches and validates against.
        var publicOnly = RSA.Create();
        publicOnly.ImportParameters(_rsa.ExportParameters(includePrivateParameters: false));
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(publicOnly) { KeyId = KeyId });
        jwk.Use = "sig";
        jwk.Alg = "RS256";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _idp = builder.Build();

        _idp.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = _issuer,
            jwks_uri = $"{_issuer}jwks",
        }));
        // JsonWebKeySet.ToString() is plain object.ToString() (just the type name), and its real
        // JSON writer (Microsoft.IdentityModel.Tokens.Json.JsonWebKeySetSerializer) is internal —
        // hand-build the standard RFC 7517 shape instead (public-key fields only, matching what
        // publicOnly above actually has: no d/p/q/dp/dq/qi).
        var jwksJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = jwk.Kty, use = jwk.Use, kid = jwk.Kid, alg = jwk.Alg, n = jwk.N, e = jwk.E } },
        });
        _idp.MapGet("/jwks", () => Results.Text(jwksJson, "application/json"));

        await _idp.StartAsync();
        var port = new Uri(_idp.Urls.First()).Port;
        _issuer = $"http://127.0.0.1:{port}/";
    }

    public async ValueTask DisposeAsync()
    {
        await _idp.StopAsync();
        _rsa.Dispose();
    }

    private string MintToken(string? audience = null, TimeSpan? expiresIn = null, bool tamperSignature = false)
    {
        var handler = new JsonWebTokenHandler();
        var expires = DateTime.UtcNow.Add(expiresIn ?? TimeSpan.FromMinutes(5));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = audience ?? Audience,
            Subject = new ClaimsIdentity([new Claim("sub", "test-user")]),
            // NotBefore must be a real issuance time, always before Expires — otherwise a
            // "generate a token that's already expired" test accidentally produces a
            // NotBefore > Expires token instead (a *different*, TokenValidationParameters
            // treats it as SecurityTokenInvalidLifetimeException, not the expiry path this
            // exists to test).
            NotBefore = expires.AddMinutes(-10),
            Expires = expires,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        };
        var token = handler.CreateToken(descriptor);
        if (tamperSignature)
        {
            var parts = token.Split('.');
            var tamperedSignature = new string(parts[2].Reverse().ToArray());
            token = $"{parts[0]}.{parts[1]}.{tamperedSignature}";
        }

        return token;
    }

    private static DefaultHttpContext ContextWithBearer(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }

    /// <summary>Builds the delegate under test with HTTPS-on-discovery relaxed — see
    /// JwtAuth.Create's <c>configurationManager</c> doc comment for why a plain-HTTP test
    /// fixture needs this seam at all.</summary>
    private RpcHttpEndpoints.AuthenticateDelegate CreateAuthenticate(string? audience = null)
    {
        var discoveryUri = $"{_issuer}.well-known/openid-configuration";
        var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            discoveryUri,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = false });
        return JwtAuth.Create([_issuer], [audience ?? Audience], configurationManager: configManager);
    }

    [Fact]
    public async Task ValidToken_Accepted()
    {
        var authenticate = CreateAuthenticate();
        var context = ContextWithBearer(MintToken());

        await authenticate(context); // does not throw
    }

    [Fact]
    public async Task MissingAuthorizationHeader_ThrowsMissingCredential()
    {
        var authenticate = CreateAuthenticate();
        var context = new DefaultHttpContext();

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authenticate(context));
        Assert.Equal(AuthReason.MissingCredential, exc.Reason);
    }

    [Fact]
    public async Task WrongAudience_ThrowsInvalidCredential()
    {
        var authenticate = CreateAuthenticate();
        var context = ContextWithBearer(MintToken(audience: "some-other-audience"));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authenticate(context));
        Assert.Equal(AuthReason.InvalidCredential, exc.Reason);
    }

    [Fact]
    public async Task ExpiredToken_ThrowsExpiredCredential()
    {
        var authenticate = CreateAuthenticate();
        var context = ContextWithBearer(MintToken(expiresIn: TimeSpan.FromMinutes(-10)));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authenticate(context));
        Assert.Equal(AuthReason.ExpiredCredential, exc.Reason);
    }

    [Fact]
    public async Task TamperedSignature_ThrowsInvalidCredential()
    {
        var authenticate = CreateAuthenticate();
        var context = ContextWithBearer(MintToken(tamperSignature: true));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authenticate(context));
        Assert.Equal(AuthReason.InvalidCredential, exc.Reason);
    }

    [Fact]
    public async Task DetailNeverLeaksRawExceptionText()
    {
        // docs/unauthorized-spec.md §2: the detail must be a coarse, safe fact ("signature did
        // not verify"), never the framework's own diagnosis (which could name internal state).
        var authenticate = CreateAuthenticate();
        var context = ContextWithBearer(MintToken(tamperSignature: true));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authenticate(context));
        Assert.DoesNotContain(KeyId, exc.Detail);
        Assert.DoesNotContain("Exception", exc.Detail);
    }
}
