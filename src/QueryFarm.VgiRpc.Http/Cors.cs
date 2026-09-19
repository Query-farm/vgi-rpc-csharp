using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// CORS support for <see cref="RpcHttpEndpoints"/> — mirrors the canonical Python repo's
/// <c>make_wsgi_app(cors_origins=..., cors_max_age=..., cors_resource_policy=...)</c> (see
/// <c>vgi_rpc/http/server/_factory.py</c> and its <c>_CorsExtrasMiddleware</c>).
///
/// This is deliberately three separate pieces rather than one <c>MapVgiRpc</c> parameter, because
/// ASP.NET Core's CORS needs both service registration (<see cref="IServiceCollection"/>, only
/// reachable before <c>WebApplicationBuilder.Build()</c>) and middleware
/// (<see cref="IApplicationBuilder"/>) — neither of which <c>MapVgiRpc</c>'s own
/// <see cref="Microsoft.AspNetCore.Routing.IEndpointRouteBuilder"/> parameter can reach.
/// <see cref="AddVgiRpcCors"/> runs on <c>builder.Services</c>, <see cref="UseVgiRpcCorsExtras"/>
/// runs on the built <c>app</c> alongside the framework's own <c>app.UseCors()</c>, and
/// <c>MapVgiRpc</c>'s own <c>corsPolicyName</c> parameter applies the registered policy to its
/// routes. A caller wiring all three together:
/// <code>
/// builder.Services.AddVgiRpcCors("vgi-rpc", ["https://example.com"], maxResponseBytes: cap);
/// var app = builder.Build();
/// app.UseCors();
/// app.UseVgiRpcCorsExtras();
/// app.MapVgiRpc(server, corsPolicyName: "vgi-rpc");
/// </code>
/// </summary>
public static class Cors
{
    /// <summary>
    /// Computes the <c>Access-Control-Expose-Headers</c> list for a server configured with the
    /// given options — every custom response header a browser client would otherwise be unable
    /// to read cross-origin. Follows Python's list in <c>_factory.py</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the two headers this method is told about stay conditional. The rest are exposed
    /// unconditionally, because <see cref="AddVgiRpcCors"/> is registered separately from
    /// <c>MapVgiRpc</c> and cannot see whether request caps, externalization, uploads, sticky
    /// sessions or proxy proof are configured -- and naming a header a server never sends has no
    /// effect, while leaving out one it does send hides it from every browser client. That is
    /// what this list used to do for <c>VGI-Max-Request-Bytes</c>, <c>VGI-Max-Upload-Bytes</c>,
    /// the externalized-response cap, the proof and sticky headers and <c>X-Request-ID</c>, all of
    /// which the reference exposes (reference suite <c>TestCors</c>).
    /// </para>
    /// </remarks>
    public static string[] ExposedHeaders(long? maxResponseBytes = null, string? proxyHint = null)
    {
        var headers = new List<string>
        {
            "WWW-Authenticate",
            "X-Request-ID",
            RpcHttpEndpoints.RpcErrorHeader,
            "X-VGI-Content-Encoding",
            "VGI-Auth-Reason",
            "VGI-Max-Request-Bytes",
            "VGI-Max-Externalized-Response-Bytes",
            "VGI-Externalization-Enabled",
            "VGI-Upload-URL-Support",
            "VGI-Max-Upload-Bytes",
            "VGI-Supported-Encodings",
            RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader,
            ProxyProof.ProofRequiredHeader,
            "VGI-Sticky-Enabled",
            "VGI-Sticky-Default-TTL",
            "VGI-Sticky-Echo-Headers",
            "VGI-Session",
            "VGI-Session-Close",
        };
        if (maxResponseBytes is not null)
        {
            headers.Add("VGI-Max-Response-Bytes");
        }

        if (!string.IsNullOrEmpty(proxyHint))
        {
            headers.Add("VGI-Auth-Proxy-Required");
        }

        return [.. headers];
    }

    /// <summary>
    /// Registers a CORS policy suitable for a vgi-rpc HTTP server. Every RPC call is preflighted
    /// (the Arrow content type is not CORS-safelisted), so <paramref name="maxAge"/> matters more
    /// here than for a typical REST API — without it every call doubles into two requests.
    /// </summary>
    /// <param name="services">The service collection — call on <c>builder.Services</c>, before
    /// <c>Build()</c>.</param>
    /// <param name="policyName">Name to pass to <c>MapVgiRpc(..., corsPolicyName: ...)</c> and,
    /// if composing your own routes, <c>.RequireCors(policyName)</c>.</param>
    /// <param name="origins">Allowed origins — matches Python's <c>cors_origins</c>.</param>
    /// <param name="maxResponseBytes">Forwarded to <see cref="ExposedHeaders"/> — pass the same
    /// value given to <c>MapVgiRpc</c>.</param>
    /// <param name="proxyHint">Forwarded to <see cref="ExposedHeaders"/> — pass the same value
    /// given to <c>MapVgiRpc</c>.</param>
    /// <param name="maxAge">Preflight cache lifetime — matches Python's default
    /// <c>cors_max_age=7200</c> (2 hours). <see langword="null"/> omits
    /// <c>Access-Control-Max-Age</c> (browsers then re-preflight per their own default, commonly
    /// 5-10 minutes).</param>
    public static IServiceCollection AddVgiRpcCors(
        this IServiceCollection services,
        string policyName,
        IEnumerable<string> origins,
        long? maxResponseBytes = null,
        string? proxyHint = null,
        TimeSpan? maxAge = null)
    {
        var exposedHeaders = ExposedHeaders(maxResponseBytes, proxyHint);
        var effectiveMaxAge = maxAge ?? TimeSpan.FromHours(2);
        services.AddCors(options => options.AddPolicy(policyName, policy =>
        {
            policy.WithOrigins([.. origins])
                .WithMethods("GET", "HEAD", "POST", "OPTIONS")
                .AllowAnyHeader()
                .WithExposedHeaders(exposedHeaders)
                .SetPreflightMaxAge(effectiveMaxAge);
        }));
        services.Replace(ServiceDescriptor.Transient<ICorsService, PreflightExposingCorsService>());
        return services;
    }

    /// <summary>
    /// ASP.NET Core's <see cref="CorsService"/>, which also writes
    /// <c>Access-Control-Expose-Headers</c> on an allowed preflight.
    /// </summary>
    /// <remarks>
    /// The stock service writes the exposed list only on the actual response. The reference server
    /// (Falcon's <c>CORSMiddleware</c>) writes it on every CORS response, preflight included, and
    /// the reference suite's <c>TestCors</c> reads the list off the preflight. A browser takes it
    /// from the actual response, where both already agreed, so this changes what a conformance
    /// probe sees and nothing a browser does.
    /// </remarks>
    private sealed class PreflightExposingCorsService(IOptions<CorsOptions> options, ILoggerFactory loggerFactory)
        : CorsService(options, loggerFactory)
    {
        public override void ApplyResult(CorsResult result, HttpResponse response)
        {
            base.ApplyResult(result, response);
            if (result.IsPreflightRequest && result.IsOriginAllowed && result.AllowedExposedHeaders.Count > 0)
            {
                response.Headers.AccessControlExposeHeaders = string.Join(",", result.AllowedExposedHeaders);
            }
        }
    }

    /// <summary>
    /// Adds the two response headers Python's <c>_CorsExtrasMiddleware</c> sets that ASP.NET
    /// Core's own CORS middleware does not: register alongside (not instead of)
    /// <c>app.UseCors()</c>.
    /// <list type="bullet">
    /// <item><c>Cross-Origin-Resource-Policy</c> on every response — CORS alone doesn't satisfy a
    /// browser that opted into cross-origin isolation (<c>Cross-Origin-Embedder-Policy:
    /// require-corp</c> blocks the page's own fetches unless each response also carries CORP,
    /// invisibly from the server's side).</item>
    /// </list>
    /// <c>Access-Control-Max-Age</c> (Python's other extra header) doesn't need one here — ASP.NET
    /// Core's CORS middleware already sets it from <see cref="CorsPolicy.PreflightMaxAge"/>
    /// (configured via <see cref="AddVgiRpcCors"/>'s <c>maxAge</c>), unlike Falcon's, which needed
    /// a second middleware for it.
    /// </summary>
    public static IApplicationBuilder UseVgiRpcCorsExtras(this IApplicationBuilder app, string resourcePolicy = "cross-origin")
    {
        return app.Use(async (context, next) =>
        {
            context.Response.Headers["Cross-Origin-Resource-Policy"] = resourcePolicy;
            await next(context).ConfigureAwait(false);
        });
    }
}
