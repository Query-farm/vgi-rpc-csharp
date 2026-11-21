using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Http.OAuth;

public static partial class JwtAuth
{
    /// <summary>
    /// Builds an <see cref="RpcHttpEndpoints.AuthenticateDelegate"/> that validates Bearer JWTs
    /// against a JWKS, with the reference's opt-in hardening (see <see cref="JwtAuthOptions"/>).
    /// </summary>
    /// <remarks>
    /// <para>An accepted token is published with
    /// <see cref="PeerIdentityAuthentication.SetAuth"/> as an authenticated
    /// <see cref="AuthContext"/> whose claims are exactly the token's JSON payload, in their JSON
    /// types: booleans stay <see cref="bool"/>, integers are <see cref="long"/>, other numbers
    /// <see cref="double"/>, arrays <see cref="List{T}"/> and objects dictionaries.</para>
    /// <para>A missing, malformed or failing token throws <see cref="AuthFailure"/> (401;
    /// <see cref="AuthReason.ExpiredCredential"/> for an expired one, so the caller knows to
    /// refresh rather than re-authenticate). A JWKS that cannot be fetched while no key set within
    /// its maximum age exists throws <see cref="AuthUnavailableException"/> (503 with
    /// <c>Retry-After</c>): the verifier cannot tell whether the token is valid, so it does not
    /// claim that it isn't.</para>
    /// <para>Signature and claim checks are Microsoft.IdentityModel's
    /// <see cref="JsonWebTokenHandler"/>. The key cache is this class's own, because the
    /// reference's contract (a hard maximum age that discards keys, <c>Cache-Control</c>, a rate
    /// limit on unknown-<c>kid</c> refreshes) is not what <c>ConfigurationManager</c> does: it
    /// keeps serving the last keys when a refresh fails.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">An empty issuer or audience list, an algorithm list
    /// that is empty or names <c>none</c>/<c>HS*</c>, a blank <see cref="JwtAuthOptions.RequiredTyp"/>
    /// or a negative duration.</exception>
    public static RpcHttpEndpoints.AuthenticateDelegate Create(JwtAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issuers = NonEmpty(options.Issuers, "Issuers");
        var audiences = NonEmpty(options.Audiences, "Audiences");
        var algorithms = options.Algorithms is null ? null : ValidateAlgorithms(options.Algorithms);
        string? expectedTyp = null;
        if (options.RequiredTyp is not null)
        {
            expectedTyp = NormalizeTyp(options.RequiredTyp);
            if (expectedTyp.Length == 0)
            {
                throw new ArgumentException("RequiredTyp must not be empty.", nameof(options));
            }
        }

        foreach (var (name, value) in new (string, TimeSpan?)[]
                 {
                     ("Leeway", options.Leeway), ("JwksMaxAge", options.JwksMaxAge),
                     ("JwksRefreshMinInterval", options.JwksRefreshMinInterval), ("JwksFetchTimeout", options.JwksFetchTimeout),
                 })
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentException($"{name} must not be negative.", nameof(options));
            }
        }

        var principalClaim = options.PrincipalClaim;
        var domain = options.Domain;
        var cache = new JwksCache(options, issuers[0]);
        var handler = new JsonWebTokenHandler();

        return async context =>
        {
            // Missing / non-Bearer already throws AuthFailure with the right reason.
            var token = BearerAuth.ExtractToken(context);

            JsonWebToken jwt;
            try
            {
                jwt = handler.ReadJsonWebToken(token);
            }
            catch (Exception)
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "Invalid JWT: malformed token");
            }

            // Checked before any key is looked at: the algorithm and typ are facts about the
            // token alone.
            var alg = jwt.Alg;
            if (string.IsNullOrEmpty(alg) || IsForbiddenAlgorithm(alg)
                || (algorithms is not null && !algorithms.Contains(alg, StringComparer.Ordinal)))
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "Invalid JWT: signing algorithm not accepted");
            }

            if (expectedTyp is not null && (string.IsNullOrEmpty(jwt.Typ) || NormalizeTyp(jwt.Typ) != expectedTyp))
            {
                throw new AuthFailure(AuthReason.InvalidCredential, $"Invalid JWT: JOSE header 'typ' must be '{options.RequiredTyp}'");
            }

            var keys = await cache.CurrentAsync(context.RequestAborted).ConfigureAwait(false);
            var kid = jwt.Kid;
            if (!string.IsNullOrEmpty(kid) && !keys.HasKid(kid))
            {
                // Unknown kid: re-fetch (rate limited) and try once more.
                var refreshed = await cache.RefreshForUnknownKidAsync(keys, context.RequestAborted).ConfigureAwait(false);
                if (refreshed is null || !refreshed.HasKid(kid))
                {
                    throw new AuthFailure(AuthReason.InvalidCredential, "Invalid JWT: unknown signing key");
                }

                keys = refreshed;
            }

            var parameters = new TokenValidationParameters
            {
                ValidIssuers = issuers,
                ValidAudiences = audiences,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                IssuerSigningKeys = keys.Keys,
                TryAllIssuerSigningKeys = true,
                ValidAlgorithms = algorithms,
                RequireSignedTokens = true,
                RequireExpirationTime = options.RequireExp,
                ValidateLifetime = true,
                ClockSkew = options.Leeway,
            };
            var result = await handler.ValidateTokenAsync(jwt, parameters).ConfigureAwait(false);
            if (!result.IsValid)
            {
                // Coarse shape only: the framework's text can name internal state.
                var reason = result.Exception is SecurityTokenExpiredException
                    ? AuthReason.ExpiredCredential
                    : AuthReason.InvalidCredential;
                var detail = result.Exception switch
                {
                    SecurityTokenExpiredException => "Invalid JWT: expired",
                    SecurityTokenNoExpirationException => "Invalid JWT: exp is required",
                    SecurityTokenNotYetValidException => "Invalid JWT: not yet valid",
                    SecurityTokenInvalidSignatureException => "Invalid JWT: signature did not verify",
                    SecurityTokenInvalidIssuerException => "Invalid JWT: issuer not accepted",
                    SecurityTokenInvalidAudienceException => "Invalid JWT: audience not accepted",
                    SecurityTokenInvalidAlgorithmException => "Invalid JWT: signing algorithm not accepted",
                    _ => "Invalid JWT: validation failed",
                };
                throw new AuthFailure(reason, detail);
            }

            if (options.SingleAudience && jwt.Audiences.Count() != 1)
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "Invalid JWT: exactly one audience is required");
            }

            Dictionary<string, object?> claims;
            try
            {
                claims = DecodeClaims(jwt.EncodedPayload);
            }
            catch (Exception)
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "Invalid JWT: payload is not a JSON object");
            }

            // NumericDate claims are JSON numbers (RFC 7519 §2); IdentityModel also reads a
            // numeric string, which the reference rejects.
            if (!IsNumericDateOrAbsent(claims, "exp") || !IsNumericDateOrAbsent(claims, "nbf") || !IsNumericDateOrAbsent(claims, "iat"))
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "Invalid JWT: exp, nbf and iat must be numbers");
            }

            var principal = claims.TryGetValue(principalClaim, out var value) ? value as string ?? value?.ToString() ?? "" : "";
            PeerIdentityAuthentication.SetAuth(context, new AuthContext(domain, authenticated: true, principal, claims));
        };
    }

    private static bool IsNumericDateOrAbsent(Dictionary<string, object?> claims, string name) =>
        !claims.TryGetValue(name, out var value) || value is long or double;

    private static string[] NonEmpty(IReadOnlyList<string>? values, string name)
    {
        if (values is not { Count: > 0 } || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"{name} must be a non-empty list of non-blank values.", nameof(values));
        }

        return [.. values];
    }

    private static bool IsForbiddenAlgorithm(string alg)
    {
        var lowered = alg.Trim().ToLowerInvariant();
        return lowered.StartsWith("none", StringComparison.Ordinal) || lowered.StartsWith("hs", StringComparison.Ordinal);
    }

    private static string[] ValidateAlgorithms(IReadOnlyList<string> algorithms)
    {
        if (algorithms.Count == 0)
        {
            throw new ArgumentException("Algorithms must not be empty.", nameof(algorithms));
        }

        foreach (var alg in algorithms)
        {
            if (string.IsNullOrWhiteSpace(alg))
            {
                throw new ArgumentException("Algorithms must not contain a blank name.", nameof(algorithms));
            }

            if (IsForbiddenAlgorithm(alg))
            {
                throw new ArgumentException($"algorithm '{alg}' is not allowed for JWKS-verified tokens", nameof(algorithms));
            }
        }

        return [.. algorithms.Select(a => a.Trim())];
    }

    /// <summary>Media type names compare case-insensitively and the <c>application/</c> prefix is
    /// optional (RFC 7515 §4.1.9, RFC 9068 §2.1).</summary>
    private static string NormalizeTyp(string value)
    {
        var typ = value.Trim().ToLowerInvariant();
        return typ.StartsWith("application/", StringComparison.Ordinal) ? typ["application/".Length..] : typ;
    }

    /// <summary>The token's payload as decoded JSON, every value in its JSON type.</summary>
    internal static Dictionary<string, object?> DecodeClaims(string encodedPayload)
    {
        using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(encodedPayload));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("JWT payload is not a JSON object");
        }

        return (Dictionary<string, object?>)FromJson(document.RootElement)!;
    }

    private static object? FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble();
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(FromJson).ToList();
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = FromJson(property.Value);
                }

                return map;
            default:
                return null;
        }
    }

    /// <summary>A fetched key set: its signing keys and their <c>kid</c>s.</summary>
    private sealed class KeySet(IReadOnlyList<SecurityKey> keys)
    {
        private readonly HashSet<string> _kids = [.. keys.Select(k => k.KeyId).Where(k => !string.IsNullOrEmpty(k))];

        public IReadOnlyList<SecurityKey> Keys { get; } = keys;

        public bool HasKid(string kid) => _kids.Contains(kid);
    }

    /// <summary>A key set and the timestamp (<see cref="TimeProvider.GetTimestamp"/>) it stops being
    /// usable at; <see langword="null"/> means never.</summary>
    private sealed record CachedKeySet(KeySet Keys, long? ExpiresAt);

    /// <summary>The JWKS cache of the reference's <c>jwt_authenticate</c>: fetch on first use,
    /// a hard maximum age measured from when the fetch started, never a stale key after a failed
    /// re-fetch, and a rate-limited, stampede-free refresh on an unknown <c>kid</c>.</summary>
    private sealed class JwksCache
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly HttpClient _http;
        private readonly TimeProvider _time;
        private readonly TimeSpan? _maxAge;
        private readonly TimeSpan? _refreshMinInterval;
        private readonly string _discoveryIssuer;
        private string? _jwksUri;
        private volatile CachedKeySet? _cache;
        private long? _lastFetchAttempt;

        public JwksCache(JwtAuthOptions options, string discoveryIssuer)
        {
            _http = options.JwksHttpHandler is null
                ? new HttpClient()
                : new HttpClient(options.JwksHttpHandler, disposeHandler: false);
            if (options.JwksFetchTimeout is { } timeout && timeout > TimeSpan.Zero)
            {
                _http.Timeout = timeout;
            }

            _time = options.TimeProvider;
            _maxAge = options.JwksMaxAge;
            _refreshMinInterval = options.JwksRefreshMinInterval;
            _discoveryIssuer = discoveryIssuer;
            _jwksUri = options.JwksUri;
        }

        private bool Usable(CachedKeySet? entry, long now) =>
            entry is not null && (entry.ExpiresAt is null || now < entry.ExpiresAt);

        public async Task<KeySet> CurrentAsync(CancellationToken cancellationToken)
        {
            var entry = _cache;
            if (entry is not null && Usable(entry, _time.GetTimestamp()))
            {
                return entry.Keys;
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                entry = _cache;
                if (entry is not null && Usable(entry, _time.GetTimestamp()))
                {
                    return entry.Keys;
                }

                return await FetchLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Re-fetches after an unknown <c>kid</c>; <see langword="null"/> when rate limited.
        /// If another request refreshed while this one waited, its result is reused rather than
        /// stampeding the JWKS endpoint during a key rotation.</summary>
        public async Task<KeySet?> RefreshForUnknownKidAsync(KeySet seen, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = _cache;
                var now = _time.GetTimestamp();
                if (entry is not null && !ReferenceEquals(entry.Keys, seen) && Usable(entry, now))
                {
                    return entry.Keys;
                }

                if (_refreshMinInterval is { } interval && _lastFetchAttempt is { } last
                    && _time.GetElapsedTime(last, now) < interval)
                {
                    return null;
                }

                return await FetchLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Fetches the JWKS and replaces the cache; the caller holds the lock.</summary>
        private async Task<KeySet> FetchLockedAsync(CancellationToken cancellationToken)
        {
            var started = _time.GetTimestamp();
            _lastFetchAttempt = started;
            KeySet keys;
            TimeSpan? advertised;
            try
            {
                if (_jwksUri is null)
                {
                    var discovery = $"{_discoveryIssuer.TrimEnd('/')}/.well-known/openid-configuration";
                    using var discoveryResponse = await _http.GetAsync(discovery, cancellationToken).ConfigureAwait(false);
                    discoveryResponse.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(
                        await discoveryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    _jwksUri = document.RootElement.GetProperty("jwks_uri").GetString()
                        ?? throw new FormatException("discovery document has no jwks_uri");
                }

                using var response = await _http.GetAsync(_jwksUri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                keys = new KeySet([.. new JsonWebKeySet(json).GetSigningKeys()]);
                advertised = response.Headers.CacheControl?.MaxAge;
            }
            catch (Exception exc) when (!(exc is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // Never fall back to stale keys: drop an expired entry so nothing can read it, and
                // make the caller fail closed.
                if (!Usable(_cache, _time.GetTimestamp()))
                {
                    _cache = null;
                }

                throw new AuthUnavailableException("JWKS endpoint unavailable");
            }

            long? expiresAt = null;
            if (_maxAge is { } maxAge)
            {
                var ttl = advertised is { } shorter && shorter < maxAge ? shorter : maxAge;
                // Measured from when the fetch started, so a slow response cannot stretch it.
                expiresAt = started + (long)(ttl.TotalSeconds * _time.TimestampFrequency);
            }

            _cache = new CachedKeySet(keys, expiresAt);
            return keys;
        }
    }
}
