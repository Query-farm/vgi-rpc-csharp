using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public class CorsTests
{
    [Fact]
    public void ExposedHeaders_BaseSet_AlwaysPresent()
    {
        var headers = Cors.ExposedHeaders();

        Assert.Contains(RpcHttpEndpoints.RpcErrorHeader, headers);
        Assert.Contains("X-VGI-Content-Encoding", headers);
        Assert.Contains("VGI-Auth-Reason", headers);
        Assert.Contains("VGI-Externalization-Enabled", headers);
        Assert.Contains("VGI-Upload-URL-Support", headers);
        Assert.Contains("VGI-Supported-Encodings", headers);
        Assert.DoesNotContain("VGI-Max-Response-Bytes", headers);
        Assert.DoesNotContain("VGI-Auth-Proxy-Required", headers);
    }

    [Fact]
    public void ExposedHeaders_MaxResponseBytes_AddsHeaderOnlyWhenConfigured()
    {
        Assert.Contains("VGI-Max-Response-Bytes", Cors.ExposedHeaders(maxResponseBytes: 65536));
        Assert.DoesNotContain("VGI-Max-Response-Bytes", Cors.ExposedHeaders(maxResponseBytes: null));
    }

    [Fact]
    public void ExposedHeaders_ProxyHint_AddsHeaderOnlyWhenConfigured()
    {
        Assert.Contains("VGI-Auth-Proxy-Required", Cors.ExposedHeaders(proxyHint: "https://proxy.example.com"));
        Assert.DoesNotContain("VGI-Auth-Proxy-Required", Cors.ExposedHeaders(proxyHint: null));
        Assert.DoesNotContain("VGI-Auth-Proxy-Required", Cors.ExposedHeaders(proxyHint: ""));
    }

    [Fact]
    public void AddVgiRpcCors_RegistersPolicyWithExpectedShape()
    {
        var services = new ServiceCollection();
        services.AddVgiRpcCors("vgi-rpc", ["https://example.com", "https://other.example.com"], maxResponseBytes: 65536, proxyHint: "https://proxy.example.com");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        var policy = options.GetPolicy("vgi-rpc");

        Assert.NotNull(policy);
        Assert.Equal(["https://example.com", "https://other.example.com"], policy.Origins);
        Assert.Contains("GET", policy.Methods);
        Assert.Contains("HEAD", policy.Methods);
        Assert.Contains("POST", policy.Methods);
        Assert.Contains("OPTIONS", policy.Methods);
        Assert.True(policy.AllowAnyHeader);
        Assert.Contains("VGI-Max-Response-Bytes", policy.ExposedHeaders);
        Assert.Contains("VGI-Auth-Proxy-Required", policy.ExposedHeaders);
        Assert.Equal(TimeSpan.FromHours(2), policy.PreflightMaxAge);
    }

    [Fact]
    public void AddVgiRpcCors_DefaultMaxAge_IsTwoHours_CustomOverrides()
    {
        var services = new ServiceCollection();
        services.AddVgiRpcCors("vgi-rpc", ["https://example.com"], maxAge: TimeSpan.FromMinutes(30));
        using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy("vgi-rpc");

        Assert.Equal(TimeSpan.FromMinutes(30), policy!.PreflightMaxAge);
    }

    [Fact]
    public async Task UseVgiRpcCorsExtras_SetsCrossOriginResourcePolicyHeader()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Microsoft.AspNetCore.Http.RequestDelegate terminal = _ => Task.CompletedTask;
        var app = new ApplicationBuilderStub(terminal);
        Cors.UseVgiRpcCorsExtras(app);
        var pipeline = app.Build();

        await pipeline(context);

        Assert.Equal("cross-origin", context.Response.Headers["Cross-Origin-Resource-Policy"]);
    }

    [Fact]
    public async Task UseVgiRpcCorsExtras_CustomResourcePolicy_IsHonored()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Microsoft.AspNetCore.Http.RequestDelegate terminal = _ => Task.CompletedTask;
        var app = new ApplicationBuilderStub(terminal);
        Cors.UseVgiRpcCorsExtras(app, resourcePolicy: "same-site");
        var pipeline = app.Build();

        await pipeline(context);

        Assert.Equal("same-site", context.Response.Headers["Cross-Origin-Resource-Policy"]);
    }

    /// <summary>
    /// Minimal <see cref="Microsoft.AspNetCore.Builder.IApplicationBuilder"/> so
    /// <see cref="Cors.UseVgiRpcCorsExtras"/> can be exercised without spinning up a full Kestrel
    /// host — the middleware delegate itself is what's under test, not ASP.NET Core's own pipeline
    /// wiring (which the conformance worker's real usage already exercises end-to-end).
    /// </summary>
    private sealed class ApplicationBuilderStub(Microsoft.AspNetCore.Http.RequestDelegate terminal) : Microsoft.AspNetCore.Builder.IApplicationBuilder
    {
        private readonly List<Func<Microsoft.AspNetCore.Http.RequestDelegate, Microsoft.AspNetCore.Http.RequestDelegate>> _components = [];

        public IServiceProvider ApplicationServices { get; set; } = new ServiceCollection().BuildServiceProvider();

        public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>();

        public Microsoft.AspNetCore.Http.Features.IFeatureCollection ServerFeatures { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();

        public Microsoft.AspNetCore.Http.RequestDelegate Build()
        {
            var app = terminal;
            for (var i = _components.Count - 1; i >= 0; i--)
            {
                app = _components[i](app);
            }

            return app;
        }

        public Microsoft.AspNetCore.Builder.IApplicationBuilder New() => throw new NotSupportedException();

        public Microsoft.AspNetCore.Builder.IApplicationBuilder Use(Func<Microsoft.AspNetCore.Http.RequestDelegate, Microsoft.AspNetCore.Http.RequestDelegate> middleware)
        {
            _components.Add(middleware);
            return this;
        }
    }
}
