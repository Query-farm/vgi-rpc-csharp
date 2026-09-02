using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
    /// to read cross-origin. Matches Python's conditional-append pattern in <c>_factory.py</c>
    /// exactly (a header is exposed if and only if the corresponding feature is actually
    /// configured), narrowed to what this port currently implements — extend this list as later
    /// milestones (sticky sessions, proxy proof, token introspection) add their own headers.
    /// </summary>
    public static string[] ExposedHeaders(long? maxResponseBytes = null, string? proxyHint = null)
    {
        var headers = new List<string>
        {
            RpcHttpEndpoints.RpcErrorHeader,
            "X-VGI-Content-Encoding",
            "VGI-Auth-Reason",
            "VGI-Externalization-Enabled",
            "VGI-Upload-URL-Support",
            "VGI-Supported-Encodings",
            RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader,
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
        return services.AddCors(options => options.AddPolicy(policyName, policy =>
        {
            policy.WithOrigins([.. origins])
                .WithMethods("GET", "HEAD", "POST", "OPTIONS")
                .AllowAnyHeader()
                .WithExposedHeaders(exposedHeaders)
                .SetPreflightMaxAge(effectiveMaxAge);
        }));
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
