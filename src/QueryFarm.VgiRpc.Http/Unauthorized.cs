using Microsoft.AspNetCore.Http;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// The closed set of reason codes an HTTP 401 may carry — mirrors the canonical Python repo's
/// <c>vgi_rpc.http.AuthReason</c> and the cross-language contract in
/// <c>docs/unauthorized-spec.md</c> exactly. Coarse by design: a code names the *stage* that
/// refused the request, never a verifier's internal diagnosis (telling a caller "the `kid` you
/// supplied is not in my secret map" turns a rejection into an oracle). A port that cannot map a
/// failure onto a specific member uses <see cref="Unauthorized"/> rather than inventing a code,
/// so the set stays closed across languages.
/// </summary>
public enum AuthReason
{
    /// <summary>No credential was presented at all.</summary>
    MissingCredential,

    /// <summary>A credential was presented and rejected.</summary>
    InvalidCredential,

    /// <summary>A well-formed credential outside its validity window.</summary>
    ExpiredCredential,

    /// <summary>The caller was identified but is not permitted.</summary>
    InsufficientScope,

    /// <summary>The request did not carry evidence that it arrived through the trusted proxy.</summary>
    ProxyRequired,

    /// <summary>Refused, unclassified — the fallback for a custom authenticator.</summary>
    Unauthorized,
}

/// <summary>Wire-name conversions for <see cref="AuthReason"/> — the exact tokens
/// <c>docs/unauthorized-spec.md</c> §3 specifies.</summary>
public static class AuthReasonExtensions
{
    public static string ToWireString(this AuthReason reason) => reason switch
    {
        AuthReason.MissingCredential => "missing_credential",
        AuthReason.InvalidCredential => "invalid_credential",
        AuthReason.ExpiredCredential => "expired_credential",
        AuthReason.InsufficientScope => "insufficient_scope",
        AuthReason.ProxyRequired => "proxy_required",
        _ => "unauthorized",
    };
}

/// <summary>
/// A rejected credential, carrying its <see cref="AuthReason"/>. Thrown by an
/// <see cref="RpcHttpEndpoints.AuthenticateDelegate"/> to reject a request — mirrors Python's
/// <c>AuthFailure</c>. Any other exception an authenticate delegate throws is treated as
/// <see cref="AuthReason.Unauthorized"/> with an empty detail (never leaking the exception's own
/// message — see <c>docs/unauthorized-spec.md</c> §2 on why a rejection must not become an
/// oracle for whoever is probing it).
/// </summary>
public sealed class AuthFailure(AuthReason reason, string detail = "") : Exception(string.IsNullOrEmpty(detail) ? reason.ToWireString() : detail)
{
    public AuthReason Reason { get; } = reason;

    /// <summary>Human-readable rejection text — may be empty. Subject to the same "never a
    /// verifier's internal diagnosis" rule as <see cref="Reason"/> itself.</summary>
    public string Detail { get; } = detail;
}

/// <summary>
/// Builds the standardized HTTP 401 — headers, content negotiation, and the JSON/HTML body shape
/// — per <c>docs/unauthorized-spec.md</c> §4. Reused by every authenticator this port adds (bearer
/// now, mTLS/JWT/proxy-proof later — see docs/roadmap.md M8+): the whole point of this being its
/// own module, per the plan, is that later auth features only ever need to raise
/// <see cref="AuthFailure"/> and never touch response-shaping again.
/// </summary>
public static class UnauthorizedResponseWriter
{
    /// <summary>Writes a §4-shaped 401 response. <paramref name="proxyHint"/> — non-empty only on
    /// a service whose authentication depends on a reverse proxy (§5) — adds
    /// <c>VGI-Auth-Proxy-Required: true</c> and the <c>proxy_hint</c> body field; omitted
    /// (not empty) otherwise, matching the spec's "presence alone is a usable signal" rule.</summary>
    public static async Task WriteAsync(HttpContext context, AuthReason reason, string detail, string? proxyHint, CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers["VGI-Auth-Reason"] = reason.ToWireString();
        if (!string.IsNullOrEmpty(proxyHint))
        {
            response.Headers["VGI-Auth-Proxy-Required"] = "true";
        }

        // A 401 is per-request and flips to 200 on the next attempt with a credential — a shared
        // cache holding onto it would be a bug (docs/unauthorized-spec.md §4.1).
        response.Headers["Cache-Control"] = "no-store";

        // Substring match, not full media-type negotiation — intentional (§4.2): the only
        // clients that ask for text/html are browsers; an RPC client sends */*, which must
        // resolve to JSON.
        var wantsHtml = context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
        byte[] body;
        if (wantsHtml)
        {
            response.ContentType = "text/html; charset=utf-8";
            body = System.Text.Encoding.UTF8.GetBytes(RenderHtml(reason, detail, proxyHint));
        }
        else
        {
            response.ContentType = "application/json";
            body = RenderJson(reason, detail, proxyHint);
        }

        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] RenderJson(AuthReason reason, string detail, string? proxyHint)
    {
        var payload = new Dictionary<string, string>
        {
            ["error"] = "unauthorized",
            ["reason"] = reason.ToWireString(),
            ["detail"] = detail,
        };
        if (!string.IsNullOrEmpty(proxyHint))
        {
            payload["proxy_hint"] = proxyHint;
        }

        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static string RenderHtml(AuthReason reason, string detail, string? proxyHint)
    {
        var detailHtml = string.IsNullOrEmpty(detail) ? "" : $"<div class=\"detail\">{System.Net.WebUtility.HtmlEncode(detail)}</div>";
        var noteHtml = string.IsNullOrEmpty(proxyHint)
            ? ""
            : $"<div class=\"note\"><strong>Is the reverse proxy configured?</strong> {System.Net.WebUtility.HtmlEncode(proxyHint)}</div>";
        return $"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>401 Unauthorized</title></head>
            <body><h1>401 Unauthorized</h1><div class="reason">{System.Net.WebUtility.HtmlEncode(reason.ToWireString())}</div>{detailHtml}{noteHtml}</body></html>
            """;
    }
}

/// <summary>
/// A minimal bearer-token authenticator — <c>Authorization: Bearer &lt;token&gt;</c> — matching
/// Python's <c>bearer_authenticate_static</c>. Throws <see cref="AuthFailure"/> directly rather
/// than returning a result, matching the <see cref="RpcHttpEndpoints.AuthenticateDelegate"/>
/// contract every authenticator implements.
/// </summary>
public static class BearerAuth
{
    /// <summary>Builds an <see cref="RpcHttpEndpoints.AuthenticateDelegate"/> that accepts any
    /// token in <paramref name="validTokens"/>, rejecting everything else.</summary>
    public static RpcHttpEndpoints.AuthenticateDelegate Static(IReadOnlySet<string> validTokens) =>
        context =>
        {
            var token = ExtractToken(context);
            if (!validTokens.Contains(token))
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "Unknown bearer token");
            }

            return Task.CompletedTask;
        };

    /// <summary>Extracts the bearer token from the request's <c>Authorization</c> header, or
    /// throws <see cref="AuthFailure"/> (<see cref="AuthReason.MissingCredential"/> when the
    /// header is absent, <see cref="AuthReason.InvalidCredential"/> when it's present but not a
    /// Bearer credential).</summary>
    public static string ExtractToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            throw new AuthFailure(AuthReason.MissingCredential, "Missing Authorization header");
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthFailure(AuthReason.InvalidCredential, "Authorization header is not a Bearer credential");
        }

        return header[prefix.Length..].Trim();
    }
}
