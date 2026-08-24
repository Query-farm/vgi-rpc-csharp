using System.Text.Json;
using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public class UnauthorizedTests
{
    [Fact]
    public async Task WriteAsync_SetsStatusAndReasonHeader()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await UnauthorizedResponseWriter.WriteAsync(context, AuthReason.InvalidCredential, "bad token", null, CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("invalid_credential", context.Response.Headers["VGI-Auth-Reason"]);
        Assert.Equal("no-store", context.Response.Headers["Cache-Control"]);
        Assert.False(context.Response.Headers.ContainsKey("VGI-Auth-Proxy-Required"));
    }

    [Fact]
    public async Task WriteAsync_JsonEnvelope_MatchesSpecShape()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await UnauthorizedResponseWriter.WriteAsync(context, AuthReason.ExpiredCredential, "token expired", null, CancellationToken.None);

        Assert.StartsWith("application/json", context.Response.ContentType);
        var json = ReadBody(context);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("unauthorized", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("expired_credential", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal("token expired", doc.RootElement.GetProperty("detail").GetString());
        Assert.False(doc.RootElement.TryGetProperty("proxy_hint", out _));
    }

    [Fact]
    public async Task WriteAsync_ProxyHint_AddsHeaderAndBodyField()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await UnauthorizedResponseWriter.WriteAsync(context, AuthReason.Unauthorized, "", "configure your proxy", CancellationToken.None);

        Assert.Equal("true", context.Response.Headers["VGI-Auth-Proxy-Required"]);
        using var doc = JsonDocument.Parse(ReadBody(context));
        Assert.Equal("configure your proxy", doc.RootElement.GetProperty("proxy_hint").GetString());
    }

    [Fact]
    public async Task WriteAsync_HtmlRequested_ReturnsHtmlWithReasonHeaderStillSet()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = "text/html,application/xhtml+xml";
        context.Response.Body = new MemoryStream();

        await UnauthorizedResponseWriter.WriteAsync(context, AuthReason.MissingCredential, "", null, CancellationToken.None);

        Assert.StartsWith("text/html", context.Response.ContentType);
        Assert.Equal("missing_credential", context.Response.Headers["VGI-Auth-Reason"]);
        var html = ReadBody(context);
        Assert.Contains("missing_credential", html);
    }

    [Fact]
    public async Task WriteAsync_JsonRequested_ForWildcardAccept()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = "*/*";
        context.Response.Body = new MemoryStream();

        await UnauthorizedResponseWriter.WriteAsync(context, AuthReason.Unauthorized, "", null, CancellationToken.None);

        Assert.StartsWith("application/json", context.Response.ContentType);
    }

    [Theory]
    [InlineData(AuthReason.MissingCredential, "missing_credential")]
    [InlineData(AuthReason.InvalidCredential, "invalid_credential")]
    [InlineData(AuthReason.ExpiredCredential, "expired_credential")]
    [InlineData(AuthReason.InsufficientScope, "insufficient_scope")]
    [InlineData(AuthReason.ProxyRequired, "proxy_required")]
    [InlineData(AuthReason.Unauthorized, "unauthorized")]
    public void ToWireString_MatchesSpecTokens(AuthReason reason, string expected)
    {
        Assert.Equal(expected, reason.ToWireString());
    }

    [Fact]
    public void AuthFailure_DefaultsDetailToReasonWireString()
    {
        var failure = new AuthFailure(AuthReason.InvalidCredential);
        Assert.Equal("invalid_credential", failure.Message);
        Assert.Equal("", failure.Detail);
    }

    [Fact]
    public void BearerAuth_ExtractToken_MissingHeader_ThrowsMissingCredential()
    {
        var context = new DefaultHttpContext();
        var exc = Assert.Throws<AuthFailure>(() => BearerAuth.ExtractToken(context));
        Assert.Equal(AuthReason.MissingCredential, exc.Reason);
    }

    [Fact]
    public void BearerAuth_ExtractToken_NonBearerScheme_ThrowsInvalidCredential()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";
        var exc = Assert.Throws<AuthFailure>(() => BearerAuth.ExtractToken(context));
        Assert.Equal(AuthReason.InvalidCredential, exc.Reason);
    }

    [Fact]
    public void BearerAuth_ExtractToken_ValidHeader_ReturnsToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer secret-token";
        Assert.Equal("secret-token", BearerAuth.ExtractToken(context));
    }

    [Fact]
    public async Task BearerAuth_Static_AcceptsKnownToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer good";
        var authenticate = BearerAuth.Static(new HashSet<string> { "good" });
        await authenticate(context); // does not throw
    }

    [Fact]
    public async Task BearerAuth_Static_RejectsUnknownToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer bad";
        var authenticate = BearerAuth.Static(new HashSet<string> { "good" });
        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authenticate(context));
        Assert.Equal(AuthReason.InvalidCredential, exc.Reason);
    }

    private static string ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }
}
