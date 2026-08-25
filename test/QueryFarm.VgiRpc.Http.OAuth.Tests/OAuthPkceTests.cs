using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace QueryFarm.VgiRpc.Http.OAuth.Tests;

/// <summary>
/// End-to-end: a real in-process Kestrel host serves the OAuth PKCE middleware + callback route
/// (protecting a plain endpoint that always 401s), a second real in-process Kestrel host serves
/// as a fake identity provider (OIDC discovery + a token endpoint) — mirrors
/// <c>JwtAuthTests</c>' own "real HTTP, fake IdP" pattern (M9).
/// </summary>
public sealed class OAuthPkceTests : IAsyncLifetime
{
    private const string ClientId = "test-client-id";
    private const string IssuedToken = "fake-access-token-12345";

    private WebApplication _idp = null!;
    private WebApplication _app = null!;
    private string _idpIssuer = null!;
    private string _appBaseUrl = null!;
    private HttpClient _noRedirectClient = null!;
    private byte[] _tokenKey = null!;
    private string? _lastTokenRequestBody;

    public async ValueTask InitializeAsync()
    {
        _tokenKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        // --- Fake IdP: OIDC discovery + token endpoint ---
        var idpBuilder = WebApplication.CreateBuilder();
        idpBuilder.Logging.ClearProviders();
        idpBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        _idp = idpBuilder.Build();
        _idp.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = _idpIssuer,
            authorization_endpoint = $"{_idpIssuer}authorize",
            token_endpoint = $"{_idpIssuer}token",
        }));
        _idp.MapPost("/token", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            _lastTokenRequestBody = await reader.ReadToEndAsync();
            return Results.Json(new { access_token = IssuedToken, expires_in = 3600, token_type = "Bearer" });
        });
        await _idp.StartAsync();
        var idpPort = new Uri(_idp.Urls.First()).Port;
        _idpIssuer = $"http://127.0.0.1:{idpPort}/";

        // --- App under test: OAuth PKCE middleware + a protected endpoint that always 401s ---
        // Minimal hosting freezes the middleware/routing pipeline at StartAsync — so, unlike the
        // IdP above (whose routes need no info about its own port), everything here must be
        // registered *before* starting. That means the app's port (needed for RedirectUri) must
        // be known upfront: reserve one via a throwaway listener rather than requesting Kestrel's
        // ephemeral-port autoselect (":0"), which isn't known until after bind/start.
        var appPort = GetFreeTcpPort();
        _appBaseUrl = $"http://127.0.0.1:{appPort}";

        var resourceMetadata = new OAuthResourceMetadata { Resource = _appBaseUrl, AuthorizationServers = [_idpIssuer], ClientId = ClientId };
        var config = OAuthPkce.CreateConfig(resourceMetadata, _tokenKey, prefix: "", new Uri(_appBaseUrl), secureCookie: false);
        // Discovery must tolerate plain HTTP (both hosts are http:// in this test) — same seam
        // JwtAuthTests uses via HttpDocumentRetriever { RequireHttps = false }.
        var discoveryUri = $"{_idpIssuer}.well-known/openid-configuration";
        config = new OAuthPkceConfig
        {
            SessionKey = config.SessionKey,
            ClientId = config.ClientId,
            ClientSecret = config.ClientSecret,
            UseIdToken = config.UseIdToken,
            Prefix = config.Prefix,
            SecureCookie = config.SecureCookie,
            RedirectUri = config.RedirectUri,
            Scope = config.Scope,
            ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(discoveryUri, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = false }),
        };

        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.Logging.ClearProviders();
        appBuilder.WebHost.UseUrls(_appBaseUrl);
        _app = appBuilder.Build();
        _app.UseVgiRpcOAuthPkce(config);
        _app.MapVgiRpcOAuthPkce(config);
        var alwaysUnauthorized = (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        _app.MapGet("/protected", alwaysUnauthorized);
        _app.MapPost("/protected", alwaysUnauthorized);

        await _app.StartAsync();

        _noRedirectClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

        // Expose the config for tests that need it (e.g. building a well-known route separately).
        Config = config;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private OAuthPkceConfig Config { get; set; } = null!;

    public async ValueTask DisposeAsync()
    {
        _noRedirectClient.Dispose();
        await _app.StopAsync();
        await _idp.StopAsync();
    }

    private static string? ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            if (cookie.StartsWith($"{cookieName}=", StringComparison.Ordinal))
            {
                var value = cookie[(cookieName.Length + 1)..];
                var semicolon = value.IndexOf(';');
                return semicolon >= 0 ? value[..semicolon] : value;
            }
        }

        return null;
    }

    [Fact]
    public async Task BrowserGet401_RedirectsToAuthorizationServer()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_appBaseUrl}/protected");
        request.Headers.Add("Accept", "text/html");

        var response = await _noRedirectClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith($"{_idpIssuer}authorize", location, StringComparison.Ordinal);
        Assert.Contains("code_challenge=", location, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", location, StringComparison.Ordinal);
        Assert.Contains($"client_id={ClientId}", location, StringComparison.Ordinal);
        Assert.NotNull(ExtractCookieValue(response, "_vgi_oauth_session"));
    }

    [Fact]
    public async Task NonBrowserRequest_Gets401Unchanged()
    {
        // No Accept: text/html — the middleware must leave a plain API 401 untouched.
        var response = await _noRedirectClient.GetAsync($"{_appBaseUrl}/protected");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostRequest_401NeverRedirected()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_appBaseUrl}/protected");
        request.Headers.Add("Accept", "text/html");

        var response = await _noRedirectClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FullCallbackFlow_ExchangesCodeAndSetsAuthCookie()
    {
        // Step 1: trigger the redirect to capture a real session cookie + state.
        var initial = new HttpRequestMessage(HttpMethod.Get, $"{_appBaseUrl}/protected");
        initial.Headers.Add("Accept", "text/html");
        var redirectResponse = await _noRedirectClient.SendAsync(initial);
        var sessionCookie = ExtractCookieValue(redirectResponse, "_vgi_oauth_session");
        Assert.NotNull(sessionCookie);
        var authUrl = redirectResponse.Headers.Location!.ToString();
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authUrl).Query)["state"];
        Assert.NotNull(state);

        // Step 2: simulate the IdP redirecting back to our callback with a code + the real state.
        var callbackRequest = new HttpRequestMessage(HttpMethod.Get, $"{_appBaseUrl}/_oauth/callback?code=fake-auth-code&state={state}");
        callbackRequest.Headers.Add("Cookie", $"_vgi_oauth_session={sessionCookie}");
        var callbackResponse = await _noRedirectClient.SendAsync(callbackRequest);

        Assert.Equal(HttpStatusCode.Found, callbackResponse.StatusCode);
        // Redirects back to the page that originally 401'd, not the prefix root — the whole
        // point of carrying the original URL through the signed session cookie.
        Assert.Equal("/protected", callbackResponse.Headers.Location!.ToString());
        var authCookie = ExtractCookieValue(callbackResponse, "_vgi_auth");
        Assert.Equal(IssuedToken, authCookie);
        // Session cookie must be cleared on success.
        Assert.Contains("_vgi_oauth_session=;", string.Join(';', callbackResponse.Headers.GetValues("Set-Cookie")), StringComparison.Ordinal);

        // The token exchange actually reached the fake IdP with PKCE's code_verifier — not just a
        // locally-fabricated success.
        Assert.NotNull(_lastTokenRequestBody);
        Assert.Contains("grant_type=authorization_code", _lastTokenRequestBody, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", _lastTokenRequestBody, StringComparison.Ordinal);
        Assert.Contains($"client_id={ClientId}", _lastTokenRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_StateMismatch_Returns400()
    {
        var initial = new HttpRequestMessage(HttpMethod.Get, $"{_appBaseUrl}/protected");
        initial.Headers.Add("Accept", "text/html");
        var redirectResponse = await _noRedirectClient.SendAsync(initial);
        var sessionCookie = ExtractCookieValue(redirectResponse, "_vgi_oauth_session");

        var callbackRequest = new HttpRequestMessage(HttpMethod.Get, $"{_appBaseUrl}/_oauth/callback?code=fake-auth-code&state=wrong-state-value");
        callbackRequest.Headers.Add("Cookie", $"_vgi_oauth_session={sessionCookie}");
        var response = await _noRedirectClient.SendAsync(callbackRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("State mismatch", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_MissingSessionCookie_Returns400()
    {
        var response = await _noRedirectClient.GetAsync($"{_appBaseUrl}/_oauth/callback?code=fake-auth-code&state=some-state");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Session cookie missing", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_TamperedSessionCookie_Returns400()
    {
        var initial = new HttpRequestMessage(HttpMethod.Get, $"{_appBaseUrl}/protected");
        initial.Headers.Add("Accept", "text/html");
        var redirectResponse = await _noRedirectClient.SendAsync(initial);
        var sessionCookie = ExtractCookieValue(redirectResponse, "_vgi_oauth_session")!;
        var authUrl = redirectResponse.Headers.Location!.ToString();
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authUrl).Query)["state"];

        var tampered = sessionCookie.Length > 4 ? sessionCookie[..^4] + "AAAA" : sessionCookie + "AAAA";
        var callbackRequest = new HttpRequestMessage(HttpMethod.Get, $"{_appBaseUrl}/_oauth/callback?code=fake-auth-code&state={state}");
        callbackRequest.Headers.Add("Cookie", $"_vgi_oauth_session={tampered}");
        var response = await _noRedirectClient.SendAsync(callbackRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Callback_MissingCodeOrState_Returns400()
    {
        var response = await _noRedirectClient.GetAsync($"{_appBaseUrl}/_oauth/callback");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Callback_AuthorizationServerError_Returns400WithDescription()
    {
        var response = await _noRedirectClient.GetAsync($"{_appBaseUrl}/_oauth/callback?error=access_denied&error_description=User+declined");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("User declined", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_ClearsAuthCookieAndRedirects()
    {
        var response = await _noRedirectClient.GetAsync($"{_appBaseUrl}/_oauth/logout");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        var cookies = string.Join(';', response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("_vgi_auth=;", cookies, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceMetadata_ValidatesRequiredFields()
    {
        var missingResource = new OAuthResourceMetadata { Resource = "", AuthorizationServers = ["https://idp.example"] };
        Assert.Throws<ArgumentException>(missingResource.Validate);

        var missingServers = new OAuthResourceMetadata { Resource = "https://res.example", AuthorizationServers = [] };
        Assert.Throws<ArgumentException>(missingServers.Validate);

        var badClientId = new OAuthResourceMetadata { Resource = "https://res.example", AuthorizationServers = ["https://idp.example"], ClientId = "has spaces!" };
        Assert.Throws<ArgumentException>(badClientId.Validate);

        var valid = new OAuthResourceMetadata { Resource = "https://res.example", AuthorizationServers = ["https://idp.example"], ClientId = "valid-client_id.123~" };
        valid.Validate(); // does not throw
    }

    [Fact]
    public void ResourceMetadata_ToJsonDict_OmitsDefaults()
    {
        var metadata = new OAuthResourceMetadata { Resource = "https://res.example", AuthorizationServers = ["https://idp.example"] };
        var dict = metadata.ToJsonDict(tokenEndpoint: null);

        Assert.Equal("https://res.example", dict["resource"]);
        Assert.False(dict.ContainsKey("scopes_supported"));
        Assert.False(dict.ContainsKey("bearer_methods_supported")); // default ["header"] omitted
        Assert.False(dict.ContainsKey("client_id"));
    }

    [Fact]
    public void ResourceMetadata_ToJsonDict_IncludesConfiguredFields()
    {
        var metadata = new OAuthResourceMetadata
        {
            Resource = "https://res.example",
            AuthorizationServers = ["https://idp.example"],
            ClientId = "abc",
            ClientSecret = "shh",
            UseIdTokenAsBearer = true,
            ScopesSupported = ["openid", "email"],
        };
        var dict = metadata.ToJsonDict(tokenEndpoint: "https://res.example/_oauth/token");

        Assert.Equal("abc", dict["client_id"]);
        Assert.Equal("shh", dict["client_secret"]);
        Assert.Equal(true, dict["use_id_token_as_bearer"]);
        Assert.Equal(new[] { "openid", "email" }, dict["scopes_supported"]);
        Assert.Equal("https://res.example/_oauth/token", dict["token_endpoint"]);
    }

    [Fact]
    public void BuildWwwAuthenticate_IncludesResourceMetadataAndClientId()
    {
        var metadata = new OAuthResourceMetadata { Resource = "https://res.example/api", AuthorizationServers = ["https://idp.example"], ClientId = "abc" };
        var header = OAuthEndpoints.BuildWwwAuthenticate(metadata, prefix: "/rpc");

        Assert.StartsWith("Bearer resource_metadata=\"https://res.example/.well-known/oauth-protected-resource/rpc\"", header, StringComparison.Ordinal);
        Assert.Contains("client_id=\"abc\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WellKnownEndpoint_ServesMetadataJson()
    {
        var metadata = new OAuthResourceMetadata { Resource = _appBaseUrl, AuthorizationServers = [_idpIssuer], ClientId = ClientId };
        // A dedicated tiny app avoids route collisions with the already-mapped fixture app.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var wellKnownApp = builder.Build();
        wellKnownApp.MapVgiRpcOAuth(metadata);
        await wellKnownApp.StartAsync();
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"{wellKnownApp.Urls.First()}/.well-known/oauth-protected-resource");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(_appBaseUrl, doc.RootElement.GetProperty("resource").GetString());
            Assert.Equal(ClientId, doc.RootElement.GetProperty("client_id").GetString());
        }
        finally
        {
            await wellKnownApp.StopAsync();
        }
    }

    [Fact]
    public void Pkce_CodeChallenge_IsCorrectS256Hash()
    {
        // RFC 7636 Appendix B worked example.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(expectedChallenge, Pkce.GenerateCodeChallenge(verifier));
    }

    [Fact]
    public void Pkce_GenerateCodeVerifier_IsUrlSafeAndUniquePerCall()
    {
        var a = Pkce.GenerateCodeVerifier();
        var b = Pkce.GenerateCodeVerifier();

        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
        Assert.True(a.Length is >= 43 and <= 128);
    }

    [Fact]
    public void Pkce_GenerateStateNonce_IsUrlSafeAndUniquePerCall()
    {
        var a = Pkce.GenerateStateNonce();
        var b = Pkce.GenerateStateNonce();

        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
    }

    [Fact]
    public void DeriveSessionKey_IsDeterministicAndDistinctFromMasterKey()
    {
        var masterKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var derived1 = OAuthPkce.DeriveSessionKey(masterKey);
        var derived2 = OAuthPkce.DeriveSessionKey(masterKey);

        Assert.Equal(derived1, derived2);
        Assert.NotEqual(masterKey, derived1);
    }
}
