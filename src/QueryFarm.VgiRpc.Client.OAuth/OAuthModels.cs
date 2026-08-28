using System.Text.Json.Serialization;

namespace QueryFarm.VgiRpc.Client.OAuth;

public sealed record OAuthDiscoveryDocument(
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("device_authorization_endpoint")] string? DeviceAuthorizationEndpoint);

public sealed record OAuthToken(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("id_token")] string? IdToken)
{
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddSeconds(ExpiresIn);
}

public sealed record PkceAuthorizationRequest(Uri AuthorizationUri, string State, string CodeVerifier, Uri RedirectUri);

public sealed record DeviceAuthorization(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("interval")] int? Interval);

public sealed class OAuthClientOptions
{
    public required Uri Authority { get; init; }

    public required string ClientId { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = ["openid"];

    public string? ClientSecret { get; init; }

    public IReadOnlyDictionary<string, string>? AdditionalAuthorizationParameters { get; init; }
}
