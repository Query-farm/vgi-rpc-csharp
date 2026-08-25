using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace QueryFarm.VgiRpc.Http.OAuth;

/// <summary>
/// RFC 9728 OAuth 2.0 Protected Resource Metadata for vgi-rpc's HTTP transport — a port of the
/// canonical Python repo's <c>vgi_rpc.http._oauth.OAuthResourceMetadata</c>. Serves
/// <c>GET /.well-known/oauth-protected-resource</c> and builds the <c>WWW-Authenticate</c>
/// challenge header for 401 responses. See <c>docs/roadmap.md</c> M15.
/// </summary>
public sealed partial class OAuthResourceMetadata
{
    [GeneratedRegex(@"^[A-Za-z0-9\-._~]+$")]
    private static partial Regex UrlSafeRegex();

    public required string Resource { get; init; }

    public required IReadOnlyList<string> AuthorizationServers { get; init; }

    public IReadOnlyList<string> ScopesSupported { get; init; } = [];

    public IReadOnlyList<string> BearerMethodsSupported { get; init; } = ["header"];

    public IReadOnlyList<string> ResourceSigningAlgValuesSupported { get; init; } = [];

    public string? ResourceName { get; init; }

    public string? ResourceDocumentation { get; init; }

    public string? ResourcePolicyUri { get; init; }

    public string? ResourceTosUri { get; init; }

    /// <summary>OAuth <c>client_id</c> clients should use against the authorization server —
    /// a custom extension, not defined by RFC 9728. Required (alongside PKCE configuration) for
    /// <see cref="OAuthPkce"/>'s browser flow to activate.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth <c>client_secret</c> — a custom extension. Some identity providers (Google
    /// notably) require it even for PKCE public-client flows; see
    /// <see cref="OAuthEndpoints.BuildWwwAuthenticate"/>'s doc comment on why it's safe to expose
    /// here rather than treat as truly confidential.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>When <see langword="true"/>, tells clients (and <see cref="OAuthPkce"/>'s own
    /// callback handler) to use the OIDC <c>id_token</c> as the bearer credential instead of the
    /// <c>access_token</c> — a custom extension.</summary>
    public bool UseIdTokenAsBearer { get; init; }

    public string? DeviceCodeClientId { get; init; }

    public string? DeviceCodeClientSecret { get; init; }

    /// <summary>Validates required fields and the URL-safe-character constraint on
    /// <see cref="ClientId"/>/<see cref="ClientSecret"/>/device-code fields — call once after
    /// construction (object initializers can't run constructor validation, so this isn't done
    /// implicitly; <see cref="OAuthEndpoints.MapVgiRpcOAuth"/> calls it for you).</summary>
    /// <exception cref="ArgumentException">A required field is empty, or a *_id/*_secret field
    /// contains characters outside <c>[A-Za-z0-9\-._~]</c>.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(Resource))
        {
            throw new ArgumentException("OAuthResourceMetadata.Resource must not be empty.");
        }

        if (AuthorizationServers.Count == 0)
        {
            throw new ArgumentException("OAuthResourceMetadata.AuthorizationServers must contain at least one entry.");
        }

        CheckUrlSafe(ClientId, nameof(ClientId));
        CheckUrlSafe(ClientSecret, nameof(ClientSecret));
        CheckUrlSafe(DeviceCodeClientId, nameof(DeviceCodeClientId));
        CheckUrlSafe(DeviceCodeClientSecret, nameof(DeviceCodeClientSecret));

        static void CheckUrlSafe(string? value, string fieldName)
        {
            if (value is not null && !UrlSafeRegex().IsMatch(value))
            {
                throw new ArgumentException(
                    $"OAuthResourceMetadata.{fieldName} must contain only URL-safe characters (alphanumeric, hyphen, underscore, period, tilde), got: '{value}'.");
            }
        }
    }

    /// <summary>Serializes to the RFC 9728 JSON shape — only non-default fields are included,
    /// matching Python's <c>to_json_dict</c>.</summary>
    public Dictionary<string, object> ToJsonDict(string? tokenEndpoint)
    {
        var d = new Dictionary<string, object>
        {
            ["resource"] = Resource,
            ["authorization_servers"] = AuthorizationServers,
        };
        if (ScopesSupported.Count > 0)
        {
            d["scopes_supported"] = ScopesSupported;
        }

        if (!(BearerMethodsSupported.Count == 1 && BearerMethodsSupported[0] == "header"))
        {
            d["bearer_methods_supported"] = BearerMethodsSupported;
        }

        if (ResourceSigningAlgValuesSupported.Count > 0)
        {
            d["resource_signing_alg_values_supported"] = ResourceSigningAlgValuesSupported;
        }

        if (ResourceName is not null)
        {
            d["resource_name"] = ResourceName;
        }

        if (ResourceDocumentation is not null)
        {
            d["resource_documentation"] = ResourceDocumentation;
        }

        if (ResourcePolicyUri is not null)
        {
            d["resource_policy_uri"] = ResourcePolicyUri;
        }

        if (ResourceTosUri is not null)
        {
            d["resource_tos_uri"] = ResourceTosUri;
        }

        if (ClientId is not null)
        {
            d["client_id"] = ClientId;
        }

        if (ClientSecret is not null)
        {
            d["client_secret"] = ClientSecret;
        }

        if (UseIdTokenAsBearer)
        {
            d["use_id_token_as_bearer"] = true;
        }

        if (DeviceCodeClientId is not null)
        {
            d["device_code_client_id"] = DeviceCodeClientId;
        }

        if (DeviceCodeClientSecret is not null)
        {
            d["device_code_client_secret"] = DeviceCodeClientSecret;
        }

        if (tokenEndpoint is not null)
        {
            // Non-standard extension (matches Python's own _OAuthResourceMetadataResource):
            // lets a browser client route PKCE token exchanges through this server's own
            // proxy instead of holding a client_secret itself. This port doesn't implement
            // that proxy route (see docs/roadmap.md M15's "not implemented" note) but still
            // accepts a pre-built endpoint URL here for a caller that wires its own.
            d["token_endpoint"] = tokenEndpoint;
        }

        return d;
    }
}

/// <summary>Route registration for <see cref="OAuthResourceMetadata"/>. Split from
/// <see cref="OAuthPkce"/>'s own route/middleware registration since the well-known discovery
/// document is useful standalone (a deployment might advertise OAuth discovery without enabling
/// this port's own browser-flow middleware at all — e.g. when browser auth is handled by a
/// front-end SPA talking to the IdP directly).</summary>
public static class OAuthEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    /// <summary>Maps <c>GET /.well-known/oauth-protected-resource</c>, serving
    /// <paramref name="metadata"/> as RFC 9728 JSON. Always at the root, never under an RPC route
    /// prefix — RFC 9728 well-known URIs are resolved against the resource's origin, not an
    /// arbitrary API path.</summary>
    /// <param name="endpoints">The route builder to register onto.</param>
    /// <param name="metadata">The resource metadata to serve.</param>
    /// <param name="tokenEndpoint">Non-standard extension: advertises a <c>token_endpoint</c> in
    /// the metadata document (see <see cref="OAuthResourceMetadata.ToJsonDict"/>'s doc comment).
    /// <see langword="null"/> omits it.</param>
    public static IEndpointRouteBuilder MapVgiRpcOAuth(this IEndpointRouteBuilder endpoints, OAuthResourceMetadata metadata, string? tokenEndpoint = null)
    {
        metadata.Validate();
        var body = JsonSerializer.SerializeToUtf8Bytes(metadata.ToJsonDict(tokenEndpoint), s_jsonOptions);
        endpoints.MapGet("/.well-known/oauth-protected-resource", (HttpContext context) =>
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "public, max-age=60";
            return context.Response.Body.WriteAsync(body).AsTask();
        });
        return endpoints;
    }

    /// <summary>Builds a <c>WWW-Authenticate</c> header value per RFC 9728 §5.1, for a 401
    /// response's own header set. <paramref name="prefix"/> must match whatever URL prefix the
    /// RPC routes themselves were mapped under (see <c>RpcHttpEndpoints.MapVgiRpc</c>'s own
    /// <c>prefix</c> parameter).</summary>
    public static string BuildWwwAuthenticate(OAuthResourceMetadata metadata, string prefix = "")
    {
        var resourceUri = new Uri(metadata.Resource);
        var pathSuffix = prefix == "/" ? "" : prefix;
        var wellKnownUrl = $"{resourceUri.Scheme}://{resourceUri.Authority}/.well-known/oauth-protected-resource{pathSuffix}";
        var challenge = $"Bearer resource_metadata=\"{wellKnownUrl}\"";
        if (metadata.ClientId is not null)
        {
            challenge += $", client_id=\"{metadata.ClientId}\"";
        }

        // client_secret in the challenge header is intentional, not a leak: identity providers
        // that require it for PKCE (Google notably) treat it as a "public" value for native/SPA
        // apps, not a confidential one — matches Python's own comment on this exactly.
        if (metadata.ClientSecret is not null)
        {
            challenge += $", client_secret=\"{metadata.ClientSecret}\"";
        }

        if (metadata.UseIdTokenAsBearer)
        {
            challenge += ", use_id_token_as_bearer=\"true\"";
        }

        if (metadata.DeviceCodeClientId is not null)
        {
            challenge += $", device_code_client_id=\"{metadata.DeviceCodeClientId}\"";
        }

        if (metadata.DeviceCodeClientSecret is not null)
        {
            challenge += $", device_code_client_secret=\"{metadata.DeviceCodeClientSecret}\"";
        }

        return challenge;
    }
}
