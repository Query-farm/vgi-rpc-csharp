using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace QueryFarm.VgiRpc.Http.OAuth;

/// <summary>
/// JWT authenticate factory for vgi-rpc's HTTP transport — the C# analog of the canonical Python
/// repo's <c>vgi_rpc.http.jwt_authenticate</c> (<c>vgi_rpc/http/_oauth_jwt.py</c>), built on
/// <see cref="RpcHttpEndpoints.AuthenticateDelegate"/>/<see cref="AuthFailure"/> from
/// <c>QueryFarm.VgiRpc.Http</c> — see docs/roadmap.md M8 for why that machinery exists once,
/// shared by every authenticator rather than each reinventing 401 shaping.
///
/// Validates Bearer JWTs against a JWKS endpoint discovered via OIDC discovery
/// (<c>{issuer}/.well-known/openid-configuration</c>), with automatic key-set refresh on an
/// unknown <c>kid</c> — <see cref="ConfigurationManager{T}"/> handles both natively, which is
/// most of why this is far shorter than Python's hand-rolled thread-safe cache-with-refresh: the
/// framework already provides it. Unlike Python's <c>jwt_authenticate</c>, this only supports the
/// discovery flow — a caller-supplied raw <c>jwks_uri</c> (skipping discovery entirely) isn't
/// wired up yet; add it if a deployment needs it.
/// </summary>
public static class JwtAuth
{
    /// <summary>
    /// Builds an <see cref="RpcHttpEndpoints.AuthenticateDelegate"/> that validates Bearer JWTs.
    /// </summary>
    /// <param name="issuers">Accepted <c>iss</c> claim values — a token matching any one passes.
    /// The first is used for OIDC discovery.</param>
    /// <param name="audiences">Accepted <c>aud</c> claim values — a token matching any one passes.</param>
    /// <param name="clockSkew">Leeway for expiry/not-before checks — defaults to .NET's usual
    /// 5 minutes; Python's joserfc-based implementation has no equivalent slack, so a
    /// byte-for-byte identical expiry decision across ports isn't guaranteed at the margin (state
    /// tokens' precedent applies here too: this is transport-internal, not a wire contract).</param>
    /// <param name="configurationManager">Overrides the default discovery-based
    /// <see cref="ConfigurationManager{T}"/> — a testability seam only (the default enforces
    /// HTTPS on the discovery endpoint, correctly, since <see cref="HttpDocumentRetriever"/>
    /// refuses plain HTTP; a test fixture serving discovery over HTTP has no other way to inject
    /// itself). Leave <see langword="null"/> in production.</param>
    public static RpcHttpEndpoints.AuthenticateDelegate Create(
        IReadOnlyList<string> issuers,
        IReadOnlyList<string> audiences,
        TimeSpan? clockSkew = null,
        BaseConfigurationManager? configurationManager = null)
    {
        if (issuers.Count == 0)
        {
            throw new ArgumentException("issuers must not be empty", nameof(issuers));
        }

        var discoveryUri = $"{issuers[0].TrimEnd('/')}/.well-known/openid-configuration";
        var configManager = configurationManager ?? new ConfigurationManager<OpenIdConnectConfiguration>(discoveryUri, new OpenIdConnectConfigurationRetriever());
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();

        return async context =>
        {
            // Missing/non-Bearer-scheme already throws AuthFailure(MissingCredential /
            // InvalidCredential) with the right reason — nothing to add here.
            var token = BearerAuth.ExtractToken(context);

            var validationParameters = new TokenValidationParameters
            {
                ValidIssuers = issuers,
                ValidAudiences = audiences,
                // ConfigurationManager, not a fixed IssuerSigningKeys list — the handler retries
                // once against a freshly-fetched key set when validation fails on an unrecognised
                // kid, matching Python's InvalidKeyIdError-triggered refresh.
                ConfigurationManager = configManager,
                ClockSkew = clockSkew ?? TimeSpan.FromMinutes(5),
            };

            var result = await handler.ValidateTokenAsync(token, validationParameters).ConfigureAwait(false);
            if (!result.IsValid)
            {
                // Matches docs/unauthorized-spec.md §2: "your JWT expired" / "your JWT signature
                // did not verify" are facts about a token the caller holds, so they're fine to
                // say — but the raw framework exception text is not (it can name internal state,
                // e.g. exactly which kid lookup failed), so only the coarse shape survives.
                var reason = result.Exception is SecurityTokenExpiredException
                    ? AuthReason.ExpiredCredential
                    : AuthReason.InvalidCredential;
                var detail = result.Exception switch
                {
                    SecurityTokenExpiredException => "JWT is expired",
                    SecurityTokenInvalidSignatureException => "JWT signature did not verify",
                    SecurityTokenInvalidIssuerException => "JWT issuer not accepted",
                    SecurityTokenInvalidAudienceException => "JWT audience not accepted",
                    _ => "JWT validation failed",
                };
                throw new AuthFailure(reason, detail);
            }
        };
    }
}
