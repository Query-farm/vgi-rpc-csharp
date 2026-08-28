using System.Net;
using System.Text;
using QueryFarm.VgiRpc.Client.OAuth;
using Xunit;

namespace QueryFarm.VgiRpc.Http.OAuth.Tests;

public sealed class OAuthClientTests
{
    [Fact]
    public async Task Pkce_DiscoversBuildsChallengeValidatesStateAndExchangesCode()
    {
        string? tokenBody = null;
        var handler = new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
            {
                return Json("""
                    {"authorization_endpoint":"https://id.example/authorize","token_endpoint":"https://id.example/token","device_authorization_endpoint":"https://id.example/device"}
                    """);
            }

            tokenBody = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return Json("""
                {"access_token":"access","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh","scope":"openid"}
                """);
        });
        using var http = new System.Net.Http.HttpClient(handler);
        await using var client = new OAuthClient(
            new OAuthClientOptions
            {
                Authority = new Uri("https://id.example"),
                ClientId = "desktop-client",
                Scopes = ["openid", "rpc"],
            },
            http);

        var request = await client.CreatePkceAuthorizationAsync(
            new Uri("http://127.0.0.1:43210/callback"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("code_challenge_method=S256", request.AuthorizationUri.Query);
        Assert.Contains("scope=openid+rpc", request.AuthorizationUri.Query);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ExchangeAuthorizationCodeAsync(request, "code", "wrong", TestContext.Current.CancellationToken));

        var token = await client.ExchangeAuthorizationCodeAsync(
            request,
            "code",
            request.State,
            TestContext.Current.CancellationToken);
        Assert.Equal("access", token.AccessToken);
        Assert.Contains("grant_type=authorization_code", tokenBody);
        Assert.Contains($"code_verifier={WebUtility.UrlEncode(request.CodeVerifier)}", tokenBody);
    }

    [Fact]
    public async Task DeviceAuthorization_AndRefresh_UseAdvertisedEndpoints()
    {
        var tokenRequests = new List<string>();
        var handler = new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
            {
                return Json("""
                    {"authorization_endpoint":"https://id.example/authorize","token_endpoint":"https://id.example/token","device_authorization_endpoint":"https://id.example/device"}
                    """);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("device", StringComparison.Ordinal))
            {
                return Json("""
                    {"device_code":"device","user_code":"ABCD","verification_uri":"https://id.example/verify","expires_in":30,"interval":1}
                    """);
            }

            tokenRequests.Add(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return Json("""
                {"access_token":"access","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh","scope":"openid"}
                """);
        });
        using var http = new System.Net.Http.HttpClient(handler);
        await using var client = new OAuthClient(
            new OAuthClientOptions { Authority = new Uri("https://id.example"), ClientId = "device-client" },
            http);

        var authorization = await client.BeginDeviceAuthorizationAsync(TestContext.Current.CancellationToken);
        var prompted = false;
        var token = await client.PollDeviceTokenAsync(
            authorization,
            _ => prompted = true,
            TestContext.Current.CancellationToken);
        var refreshed = await client.RefreshAsync("refresh", TestContext.Current.CancellationToken);

        Assert.True(prompted);
        Assert.Equal("access", token.AccessToken);
        Assert.Equal("access", refreshed.AccessToken);
        Assert.Contains(tokenRequests, body => body.Contains("device_code=device", StringComparison.Ordinal));
        Assert.Contains(tokenRequests, body => body.Contains("grant_type=refresh_token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BearerHandler_AddsProviderToken()
    {
        string? authorization = null;
        using var inner = new DelegateHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var handler = new OAuthBearerHandler(new StaticTokenProvider()) { InnerHandler = inner };
        using var http = new System.Net.Http.HttpClient(handler);

        using var response = await http.GetAsync("https://rpc.example/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Bearer secret", authorization);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class StaticTokenProvider : IOAuthTokenProvider
    {
        public Task<OAuthToken> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OAuthToken("secret", "Bearer", 3600, null, null, null));
    }
}
