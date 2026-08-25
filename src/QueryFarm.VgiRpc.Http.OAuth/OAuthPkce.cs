using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace QueryFarm.VgiRpc.Http.OAuth;

/// <summary>
/// RFC 7636 (PKCE) code verifier/challenge generation — a port of the canonical Python repo's
/// <c>vgi_rpc.http._oauth_pkce</c> helpers of the same name.
/// </summary>
public static class Pkce
{
    /// <summary>Generates a URL-safe random code verifier per RFC 7636 §4.1 (32 random bytes,
    /// base64url-encoded — 43 characters, within the spec's 43–128 length bound).</summary>
    public static string GenerateCodeVerifier() => Base64UrlNoPadding(RandomNumberGenerator.GetBytes(32));

    /// <summary>Computes the S256 code challenge for <paramref name="codeVerifier"/> per RFC 7636 §4.2.</summary>
    public static string GenerateCodeChallenge(string codeVerifier) => Base64UrlNoPadding(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    /// <summary>Generates a random state nonce for CSRF protection.</summary>
    public static string GenerateStateNonce() => Base64UrlNoPadding(RandomNumberGenerator.GetBytes(24));

    private static string Base64UrlNoPadding(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Server-side OAuth 2.0 Authorization Code + PKCE browser flow for vgi-rpc's HTTP transport — a
/// port of the canonical Python repo's <c>vgi_rpc.http._oauth_pkce</c>. When wired in (see
/// <see cref="UseVgiRpcOAuthPkce"/>/<see cref="MapVgiRpcOAuthPkce"/>), a browser <c>GET</c> that
/// would otherwise 401 is instead redirected to the authorization server's login page; after
/// login, the authorization server redirects back to <c>{prefix}/_oauth/callback</c>, which
/// exchanges the code for a token, sets it as a cookie, and redirects to the original page.
///
/// <para><b>Scope narrower than Python here</b> — three pieces of the reference module are
/// deliberately not ported, all product-specific extensions layered on top of the standards-based
/// flow rather than the flow itself: (1) the external-frontend <c>_vgi_return_to</c> redirect
/// (token delivered via URL fragment to a separate SPA origin — a Query.Farm/Cupola-specific
/// integration, not a generic OAuth2/PKCE concern); (2) <c>POST {prefix}/_oauth/token</c>, a
/// same-site token-exchange proxy for browser clients that can't hold a <c>client_secret</c>
/// themselves; (3) the JS-readable display-identity cookie derived from the <c>id_token</c>'s
/// profile claims (cosmetic landing-page integration). See <c>docs/roadmap.md</c> M15 for the
/// full "not implemented" list.</para>
///
/// <para>The session cookie carrying PKCE state across the redirect uses this port's own signed
/// binary format (HMAC-SHA256), not Python's byte-for-byte layout — matching the precedent M6's
/// state-token crypto and M10's sticky-session tokens already established: this cookie is
/// transport-implementation-internal (read only by the same server process that wrote it, never
/// parsed cross-language), so there is no wire contract to stay compatible with.</para>
/// </summary>
public static class OAuthPkce
{
    private const string SessionCookieName = "_vgi_oauth_session";
    private const string AuthCookieName = "_vgi_auth";
    private const string CallbackPathSuffix = "/_oauth/callback";
    private const string LogoutPathSuffix = "/_oauth/logout";
    private const byte SessionCookieVersion = 1;
    private const int HmacLength = 32;
    private static readonly byte[] s_sessionKeyInfo = "oauth-pkce-session"u8.ToArray();

    /// <summary>Builds a session-key-derivation + config bundle from
    /// <paramref name="metadata"/>. Requires <see cref="OAuthResourceMetadata.ClientId"/> and at
    /// least one authorization server; the first authorization server is used for OIDC discovery.</summary>
    /// <param name="metadata">Resource metadata supplying the client id/secret, scopes, and
    /// authorization server issuer.</param>
    /// <param name="tokenKey">Master secret this deployment already has (e.g. the same one
    /// backing sticky-session/stream-call tokens) — a session-cookie-specific key is derived from
    /// it via HMAC (matching Python's <c>_derive_session_key</c>), so a single secret can safely
    /// back multiple token purposes without cross-protocol forgery risk.</param>
    /// <param name="prefix">Must match the URL prefix RPC routes were mapped under.</param>
    /// <param name="resourceBaseUrl">Origin (scheme+host[:port]) the callback redirect URI is
    /// built against — normally the same origin as <see cref="OAuthResourceMetadata.Resource"/>.</param>
    /// <param name="secureCookie">Whether cookies get the <c>Secure</c> attribute — defaults to
    /// whether <paramref name="resourceBaseUrl"/> is HTTPS.</param>
    /// <param name="scope">OAuth scope string used when <paramref name="metadata"/> declares none.</param>
    public static OAuthPkceConfig CreateConfig(OAuthResourceMetadata metadata, byte[] tokenKey, string prefix, Uri resourceBaseUrl, bool? secureCookie = null, string scope = "openid email")
    {
        if (metadata.ClientId is null)
        {
            throw new ArgumentException("OAuthResourceMetadata.ClientId is required for the PKCE browser flow.", nameof(metadata));
        }

        if (metadata.AuthorizationServers.Count == 0)
        {
            throw new ArgumentException("OAuthResourceMetadata.AuthorizationServers must contain at least one entry.", nameof(metadata));
        }

        var issuer = metadata.AuthorizationServers[0];
        var discoveryUri = $"{issuer.TrimEnd('/')}/.well-known/openid-configuration";
        var redirectUri = $"{resourceBaseUrl.Scheme}://{resourceBaseUrl.Authority}{prefix}{CallbackPathSuffix}";
        return new OAuthPkceConfig
        {
            SessionKey = DeriveSessionKey(tokenKey),
            ClientId = metadata.ClientId,
            ClientSecret = metadata.ClientSecret,
            UseIdToken = metadata.UseIdTokenAsBearer,
            Prefix = prefix,
            SecureCookie = secureCookie ?? resourceBaseUrl.Scheme == "https",
            RedirectUri = redirectUri,
            Scope = metadata.ScopesSupported.Count > 0 ? string.Join(' ', metadata.ScopesSupported) : scope,
            ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(discoveryUri, new OpenIdConnectConfigurationRetriever()),
        };
    }

    /// <summary>Derives a session-cookie-specific key from a shared master secret — prevents
    /// cross-protocol forgery with any other token purpose the same master key backs (matches
    /// Python's <c>_derive_session_key</c>).</summary>
    public static byte[] DeriveSessionKey(byte[] tokenKey) => HMACSHA256.HashData(tokenKey, s_sessionKeyInfo);

    /// <summary>Installs the 401→302 redirect middleware: on a <c>GET</c> request whose response
    /// would be <c>401</c> and whose <c>Accept</c> header names <c>text/html</c>, overwrites it
    /// with a redirect to the authorization server (PKCE parameters + a signed session cookie
    /// carrying the original URL). Every other response passes through unchanged. Register before
    /// the routes it protects (standard ASP.NET Core middleware ordering).</summary>
    public static IApplicationBuilder UseVgiRpcOAuthPkce(this IApplicationBuilder app, OAuthPkceConfig config) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Method != HttpMethods.Get)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // Buffer the response so a downstream 401's body can be discarded and replaced with a
            // 302 — ASP.NET Core offers no "peek the status, then decide" hook otherwise, since by
            // the time next() returns, a naive write may already be flushed to the real stream.
            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            var accept = context.Request.Headers.Accept.ToString();
            if (context.Response.StatusCode != StatusCodes.Status401Unauthorized || !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            var configuration = await config.ConfigurationManager.GetConfigurationAsync(context.RequestAborted).ConfigureAwait(false);
            if (string.IsNullOrEmpty(configuration.AuthorizationEndpoint))
            {
                // OIDC discovery failed to yield an authorization_endpoint — fall through to the
                // original 401 rather than redirect to a broken URL (matches Python's posture:
                // "PKCE redirect skipped: OIDC discovery failed").
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            var codeVerifier = Pkce.GenerateCodeVerifier();
            var codeChallenge = Pkce.GenerateCodeChallenge(codeVerifier);
            var stateNonce = Pkce.GenerateStateNonce();
            var originalUrl = ValidateOriginalUrl($"{context.Request.Path}{context.Request.QueryString}", config.Prefix);
            var cookieValue = PackSessionCookie(codeVerifier, stateNonce, originalUrl, config.SessionKey);

            var authParams = new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = config.ClientId,
                ["redirect_uri"] = config.RedirectUri,
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
                ["state"] = stateNonce,
                ["scope"] = config.Scope,
            };
            var authUrl = QueryHelpers.AddQueryString(configuration.AuthorizationEndpoint, authParams);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = authUrl;
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            AppendCookie(context, SessionCookieName, cookieValue, TimeSpan.FromMinutes(10), $"{config.Prefix}/_oauth/", config.SecureCookie, httpOnly: true);
        });

    /// <summary>Maps <c>GET {prefix}/_oauth/callback</c> (exchanges the authorization code for a
    /// token, sets the auth cookie, redirects to the original page) and
    /// <c>GET {prefix}/_oauth/logout</c> (clears cookies, redirects to <paramref name="config"/>'s
    /// prefix root).</summary>
    public static IEndpointRouteBuilder MapVgiRpcOAuthPkce(this IEndpointRouteBuilder endpoints, OAuthPkceConfig config)
    {
        endpoints.MapGet($"{config.Prefix}{CallbackPathSuffix}", (HttpContext context) => HandleCallbackAsync(context, config));
        endpoints.MapGet($"{config.Prefix}{LogoutPathSuffix}", (HttpContext context) =>
        {
            var cookiePath = string.IsNullOrEmpty(config.Prefix) ? "/" : config.Prefix;
            DeleteCookie(context, AuthCookieName, cookiePath, config.SecureCookie);
            context.Response.Redirect(string.IsNullOrEmpty(config.Prefix) ? "/" : config.Prefix);
            return Task.CompletedTask;
        });
        return endpoints;
    }

    private static async Task HandleCallbackAsync(HttpContext context, OAuthPkceConfig config)
    {
        var retryUrl = string.IsNullOrEmpty(config.Prefix) ? "/" : config.Prefix;

        var error = context.Request.Query["error"].ToString();
        if (!string.IsNullOrEmpty(error))
        {
            var errorDescription = context.Request.Query["error_description"].ToString();
            await WriteErrorPageAsync(context, StatusCodes.Status400BadRequest, "The authorization server returned an error.", string.IsNullOrEmpty(errorDescription) ? error : errorDescription, retryUrl).ConfigureAwait(false);
            return;
        }

        var code = context.Request.Query["code"].ToString();
        var state = context.Request.Query["state"].ToString();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            await WriteErrorPageAsync(context, StatusCodes.Status400BadRequest, "Missing authorization code or state parameter.", null, retryUrl).ConfigureAwait(false);
            return;
        }

        if (!context.Request.Cookies.TryGetValue(SessionCookieName, out var sessionCookie) || string.IsNullOrEmpty(sessionCookie))
        {
            await WriteErrorPageAsync(context, StatusCodes.Status400BadRequest, "Session cookie missing or expired. Please try again.", null, retryUrl).ConfigureAwait(false);
            return;
        }

        string codeVerifier, expectedState, originalUrl;
        try
        {
            (codeVerifier, expectedState, originalUrl) = UnpackSessionCookie(sessionCookie, config.SessionKey);
        }
        catch (Exception exc) when (exc is FormatException or InvalidDataException or CryptographicException)
        {
            await WriteErrorPageAsync(context, StatusCodes.Status400BadRequest, "Session expired or invalid. Please try again.", null, retryUrl).ConfigureAwait(false);
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
        {
            await WriteErrorPageAsync(context, StatusCodes.Status400BadRequest, "State mismatch — possible CSRF. Please try again.", null, retryUrl).ConfigureAwait(false);
            return;
        }

        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await config.ConfigurationManager.GetConfigurationAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await WriteErrorPageAsync(context, StatusCodes.Status502BadGateway, "Could not reach the authorization server.", "OIDC discovery failed.", retryUrl).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(configuration.TokenEndpoint))
        {
            await WriteErrorPageAsync(context, StatusCodes.Status502BadGateway, "Could not reach the authorization server.", "OIDC discovery failed.", retryUrl).ConfigureAwait(false);
            return;
        }

        TokenExchangeResult exchanged;
        try
        {
            exchanged = await ExchangeCodeForTokenAsync(configuration.TokenEndpoint, code, config.RedirectUri, codeVerifier, config.ClientId, config.ClientSecret, config.UseIdToken, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            await WriteErrorPageAsync(context, StatusCodes.Status502BadGateway, "Token exchange with the authorization server failed.", exc.Message, retryUrl).ConfigureAwait(false);
            return;
        }

        var cookiePath = string.IsNullOrEmpty(config.Prefix) ? "/" : config.Prefix;
        var redirectTarget = ValidateOriginalUrl(originalUrl, config.Prefix);
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = redirectTarget;
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        // Auth cookie: JS-readable (no HttpOnly) so a WASM/browser client can read it directly,
        // matching Python's own posture for this cookie specifically (unlike the HttpOnly session
        // cookie above, which never needs to be JS-readable).
        AppendCookie(context, AuthCookieName, exchanged.Token, exchanged.MaxAge, cookiePath, config.SecureCookie, httpOnly: false);
        DeleteCookie(context, SessionCookieName, $"{config.Prefix}/_oauth/", config.SecureCookie);
    }

    private static async Task<TokenExchangeResult> ExchangeCodeForTokenAsync(string tokenEndpoint, string code, string redirectUri, string codeVerifier, string clientId, string? clientSecret, bool useIdToken, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["client_id"] = clientId,
        };
        if (clientSecret is not null)
        {
            form["client_secret"] = clientSecret;
        }

        using var client = new HttpClient();
        using var response = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token exchange failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (useIdToken)
        {
            if (!root.TryGetProperty("id_token", out var idTokenElement) || idTokenElement.GetString() is not { } idToken)
            {
                throw new InvalidOperationException("Token response missing id_token.");
            }

            var maxAge = TimeSpan.FromHours(1);
            if (TryGetJwtExpiry(idToken, out var exp))
            {
                var seconds = Math.Max(exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 60);
                maxAge = TimeSpan.FromSeconds(seconds);
            }

            return new TokenExchangeResult(idToken, maxAge);
        }

        if (!root.TryGetProperty("access_token", out var accessTokenElement) || accessTokenElement.GetString() is not { } accessToken)
        {
            throw new InvalidOperationException("Token response missing access_token.");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var expiresInElement) && expiresInElement.TryGetInt64(out var seconds2) ? seconds2 : 3600;
        return new TokenExchangeResult(accessToken, TimeSpan.FromSeconds(expiresIn));
    }

    private static bool TryGetJwtExpiry(string jwt, out long exp)
    {
        exp = 0;
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("exp", out var expElement) && expElement.TryGetInt64(out exp))
            {
                return true;
            }
        }
        catch (Exception exc) when (exc is FormatException or JsonException)
        {
            // Not a decodable JWT payload — treated as "no exp claim," matching Python's broad
            // except-and-ignore here (this is a best-effort UX nicety, not a security check).
        }

        return false;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }

    /// <summary>Validates that a redirect target is a relative URL within <paramref name="prefix"/> —
    /// an absolute URL (open-redirect risk) or one outside the configured prefix falls back to
    /// the prefix root, matching Python's <c>_validate_original_url</c>.</summary>
    private static string ValidateOriginalUrl(string url, string prefix)
    {
        const int maxLength = 2048;
        if (url.Length > maxLength)
        {
            url = url[..maxLength];
        }

        if (url.Length == 0 || url[0] != '/' || url.StartsWith("//", StringComparison.Ordinal))
        {
            // Not a same-origin-relative path (empty, scheme-relative, or otherwise not rooted).
            return string.IsNullOrEmpty(prefix) ? "/" : prefix;
        }

        if (!string.IsNullOrEmpty(prefix) && !url.StartsWith(prefix, StringComparison.Ordinal))
        {
            return prefix;
        }

        return url;
    }

    private static void AppendCookie(HttpContext context, string name, string value, TimeSpan maxAge, string path, bool secure, bool httpOnly)
    {
        context.Response.Cookies.Append(name, value, new CookieOptions
        {
            MaxAge = maxAge,
            Path = path,
            Secure = secure,
            HttpOnly = httpOnly,
            SameSite = SameSiteMode.Lax,
        });
    }

    private static void DeleteCookie(HttpContext context, string name, string path, bool secure)
    {
        context.Response.Cookies.Append(name, "", new CookieOptions
        {
            MaxAge = TimeSpan.Zero,
            Path = path,
            Secure = secure,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
        });
    }

    /// <summary>Packs PKCE session state into a signed, base64url-encoded cookie value. Own
    /// binary format (version/timestamp/length-prefixed UTF-8 fields/HMAC-SHA256 tag) — see this
    /// class's doc comment on why byte-compatibility with Python's own cookie layout isn't a
    /// goal.</summary>
    private static string PackSessionCookie(string codeVerifier, string stateNonce, string originalUrl, byte[] sessionKey)
    {
        var cv = Encoding.UTF8.GetBytes(codeVerifier);
        var st = Encoding.UTF8.GetBytes(stateNonce);
        var url = Encoding.UTF8.GetBytes(originalUrl);
        var payload = new byte[1 + 8 + (2 + cv.Length) + (2 + st.Length) + (2 + url.Length)];
        var pos = 0;
        payload[pos++] = SessionCookieVersion;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(pos), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        pos += 8;
        pos = WriteField(payload, pos, cv);
        pos = WriteField(payload, pos, st);
        _ = WriteField(payload, pos, url);

        var mac = HMACSHA256.HashData(sessionKey, payload);
        var combined = new byte[payload.Length + mac.Length];
        payload.CopyTo(combined, 0);
        mac.CopyTo(combined, payload.Length);
        return Convert.ToBase64String(combined).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static int WriteField(byte[] buffer, int offset, byte[] field)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)field.Length);
            offset += 2;
            field.CopyTo(buffer, offset);
            return offset + field.Length;
        }
    }

    /// <summary>Unpacks and verifies a signed session cookie, enforcing a 10-minute max age.</summary>
    /// <exception cref="FormatException">Malformed base64/too short.</exception>
    /// <exception cref="InvalidDataException">Wrong version, or expired.</exception>
    /// <exception cref="CryptographicException">Signature mismatch (tampered).</exception>
    private static (string CodeVerifier, string StateNonce, string OriginalUrl) UnpackSessionCookie(string cookieValue, byte[] sessionKey)
    {
        var padded = cookieValue.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        var raw = Convert.FromBase64String(padded);

        const int minimumSize = 1 + 8 + 2 + 2 + 2 + HmacLength;
        if (raw.Length < minimumSize)
        {
            throw new FormatException("Session cookie too short.");
        }

        var payload = raw.AsSpan(0, raw.Length - HmacLength);
        var receivedMac = raw.AsSpan(raw.Length - HmacLength);
        var expectedMac = HMACSHA256.HashData(sessionKey, payload);
        if (!CryptographicOperations.FixedTimeEquals(receivedMac, expectedMac))
        {
            throw new CryptographicException("Session cookie signature mismatch.");
        }

        var pos = 0;
        var version = payload[pos++];
        if (version != SessionCookieVersion)
        {
            throw new InvalidDataException($"Unexpected session cookie version: {version}.");
        }

        var createdAt = BinaryPrimitives.ReadInt64LittleEndian(payload[pos..]);
        pos += 8;
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - createdAt;
        if (age is < 0 or > 600)
        {
            throw new InvalidDataException($"Session cookie expired (age={age}s, max=600s).");
        }

        var codeVerifier = ReadField(payload, ref pos);
        var stateNonce = ReadField(payload, ref pos);
        var originalUrl = ReadField(payload, ref pos);
        return (codeVerifier, stateNonce, originalUrl);

        static string ReadField(ReadOnlySpan<byte> buffer, ref int offset)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer[offset..]);
            offset += 2;
            var field = Encoding.UTF8.GetString(buffer.Slice(offset, length));
            offset += length;
            return field;
        }
    }

    private static async Task WriteErrorPageAsync(HttpContext context, int statusCode, string message, string? detail, string retryUrl)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(OAuthErrorPage.Render(message, detail, retryUrl), context.RequestAborted).ConfigureAwait(false);
    }

    private sealed record TokenExchangeResult(string Token, TimeSpan MaxAge);
}

/// <summary>Configuration bundle produced by <see cref="OAuthPkce.CreateConfig"/> — everything
/// <see cref="OAuthPkce.UseVgiRpcOAuthPkce"/>/<see cref="OAuthPkce.MapVgiRpcOAuthPkce"/> need.</summary>
public sealed class OAuthPkceConfig
{
    public required byte[] SessionKey { get; init; }

    public required string ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public bool UseIdToken { get; init; }

    public required string Prefix { get; init; }

    public required bool SecureCookie { get; init; }

    public required string RedirectUri { get; init; }

    public required string Scope { get; init; }

    /// <summary>Caches OIDC discovery (<c>{issuer}/.well-known/openid-configuration</c>) with
    /// automatic refresh — the same <see cref="ConfigurationManager{T}"/> class
    /// <c>QueryFarm.VgiRpc.Http.OAuth.JwtAuth</c> (M9) uses, so this port has no hand-rolled
    /// discovery cache to keep correct in two places. Typed concretely (not
    /// <see cref="BaseConfigurationManager"/>, unlike <c>JwtAuth</c>'s own testability seam) since
    /// this class needs <see cref="OpenIdConnectConfiguration.AuthorizationEndpoint"/>/
    /// <see cref="OpenIdConnectConfiguration.TokenEndpoint"/> specifically — a caller wanting to
    /// inject a test retriever still can, via <see cref="ConfigurationManager{T}"/>'s own
    /// constructor overload.</summary>
    public required ConfigurationManager<OpenIdConnectConfiguration> ConfigurationManager { get; init; }
}
