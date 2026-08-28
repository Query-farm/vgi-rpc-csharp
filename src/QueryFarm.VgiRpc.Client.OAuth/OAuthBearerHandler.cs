using System.Net.Http.Headers;

namespace QueryFarm.VgiRpc.Client.OAuth;

public interface IOAuthTokenProvider
{
    Task<OAuthToken> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Adds a current OAuth bearer token to outgoing vgi-rpc HTTP requests.</summary>
public sealed class OAuthBearerHandler(IOAuthTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue(token.TokenType, token.AccessToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
