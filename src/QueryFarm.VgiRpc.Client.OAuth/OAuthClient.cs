using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QueryFarm.VgiRpc.Client.OAuth;

/// <summary>OAuth 2.0/OIDC public-client flows with PKCE and device authorization.</summary>
public sealed class OAuthClient : IAsyncDisposable
{
    private readonly System.Net.Http.HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly OAuthClientOptions _options;
    private OAuthDiscoveryDocument? _discovery;

    public OAuthClient(OAuthClientOptions options, System.Net.Http.HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? new System.Net.Http.HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public async Task<OAuthDiscoveryDocument> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (_discovery is not null)
        {
            return _discovery;
        }

        var authority = _options.Authority.ToString().TrimEnd('/');
        using var response = await _http.GetAsync($"{authority}/.well-known/openid-configuration", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        _discovery = await response.Content.ReadFromJsonAsync<OAuthDiscoveryDocument>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("OIDC discovery returned an empty document.");
        return _discovery;
    }

    public async Task<PkceAuthorizationRequest> CreatePkceAuthorizationAsync(
        Uri redirectUri,
        string? loginHint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = string.Join(' ', _options.Scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            parameters["login_hint"] = loginHint;
        }

        if (_options.AdditionalAuthorizationParameters is not null)
        {
            foreach (var (key, value) in _options.AdditionalAuthorizationParameters)
            {
                parameters[key] = value;
            }
        }

        var separator = discovery.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var uri = new Uri(discovery.AuthorizationEndpoint + separator + Query(parameters));
        return new PkceAuthorizationRequest(uri, state, verifier, redirectUri);
    }

    public async Task<OAuthToken> ExchangeAuthorizationCodeAsync(
        PkceAuthorizationRequest authorization,
        string code,
        string returnedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(authorization.State),
            Encoding.UTF8.GetBytes(returnedState)))
        {
            throw new InvalidOperationException("OAuth state mismatch.");
        }

        return await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = _options.ClientId,
                ["code"] = code,
                ["redirect_uri"] = authorization.RedirectUri.ToString(),
                ["code_verifier"] = authorization.CodeVerifier,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceAuthorization> BeginDeviceAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(discovery.DeviceAuthorizationEndpoint))
        {
            throw new NotSupportedException("The identity provider does not advertise a device authorization endpoint.");
        }

        using var response = await _http.PostAsync(
            discovery.DeviceAuthorizationEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["scope"] = string.Join(' ', _options.Scopes),
            }),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DeviceAuthorization>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Device authorization returned an empty document.");
    }

    public async Task<OAuthToken> PollDeviceTokenAsync(
        DeviceAuthorization authorization,
        Action<DeviceAuthorization>? prompt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        prompt?.Invoke(authorization);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(1, authorization.Interval ?? 5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _http.PostAsync(
                discovery.TokenEndpoint,
                new FormUrlEncodedContent(AddClientAuthentication(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = _options.ClientId,
                    ["device_code"] = authorization.DeviceCode,
                })),
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await ReadTokenAsync(response, cancellationToken).ConfigureAwait(false);
            }

            var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            if (error == "authorization_pending")
            {
                continue;
            }

            if (error == "slow_down")
            {
                interval += TimeSpan.FromSeconds(5);
                continue;
            }

            throw new InvalidOperationException($"Device authorization failed: {error}.");
        }

        throw new TimeoutException("The device authorization code expired before authorization completed.");
    }

    public Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _options.ClientId,
                ["refresh_token"] = refreshToken,
            },
            cancellationToken);

    private async Task<OAuthToken> RequestTokenAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        using var response = await _http.PostAsync(
            discovery.TokenEndpoint,
            new FormUrlEncodedContent(AddClientAuthentication(parameters)),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadTokenAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private Dictionary<string, string> AddClientAuthentication(Dictionary<string, string> parameters)
    {
        if (_options.ClientSecret is not null)
        {
            parameters["client_secret"] = _options.ClientSecret;
        }

        return parameters;
    }

    private static async Task<OAuthToken> ReadTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<OAuthToken>(cancellationToken: cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("Token endpoint returned an empty document.");

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "unknown_error" : "unknown_error";
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"OAuth endpoint returned HTTP {(int)response.StatusCode}: {detail}", null, response.StatusCode);
    }

    private static string Query(IReadOnlyDictionary<string, string> values) =>
        string.Join('&', values.Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
