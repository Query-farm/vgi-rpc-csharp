using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace QueryFarm.VgiRpc.Identity;

/// <summary>
/// A deployment's sealed-grant configuration (IDENTITY_V1_SPEC.md §9.1): the first key mints,
/// every key verifies.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in. With no grant key configured nothing changes: no <c>issue_grant</c> appears and no
/// bearer is accepted that was not before. With keys, the framework mints grants itself (unless the
/// worker supplies its own minter) and an HTTP server accepts its own grants back as bearer
/// credentials -- no storage, no author code.
/// </para>
/// <para>
/// <b>Rotation:</b> add the new key first, keep the old one after it until every grant it minted
/// has expired, then remove it. <b>Revocation:</b> a sealed grant is not individually revocable;
/// the levers are a short <see cref="MaxTtlSeconds"/> with re-issue, and removing a key (which
/// revokes every grant it minted).
/// </para>
/// </remarks>
public sealed class GrantKeys
{
    /// <summary>Comma-separated standard base64 keys, minting key first.</summary>
    public const string KeysEnvironmentVariable = "VGI_RPC_GRANT_KEYS";

    /// <summary>Audience bound into every token's associated data. Default <c>""</c>.</summary>
    public const string AudienceEnvironmentVariable = "VGI_RPC_GRANT_AUDIENCE";

    /// <summary>Lifetime ceiling in seconds. Default 7 days.</summary>
    public const string MaxTtlEnvironmentVariable = "VGI_RPC_GRANT_MAX_TTL_SECONDS";

    /// <summary>Default lifetime ceiling -- short on purpose: expiry is a sealed grant's only revocation.</summary>
    public const long DefaultMaxTtlSeconds = 7 * 24 * 3600;

    /// <summary>Tolerance for clocks disagreeing between the minting and verifying worker.</summary>
    public const long DefaultClockSkewSeconds = 60;

    private const int KeyLength = 32;
    private const int MaxField = 0xFFFF;

    /// <param name="keys">32-byte keys, minting key first.</param>
    /// <param name="audience">Bound into every token's AAD, so deployments that (against advice)
    /// share a key still cannot accept each other's grants when their audiences differ.</param>
    /// <param name="maxTtlSeconds">Lifetime ceiling, at minting and at verification.</param>
    /// <param name="clockSkewSeconds">Tolerance applied to <c>issued_at</c> and <c>expires_at</c>.</param>
    /// <exception cref="ArgumentException">No key, a key that is not exactly 32 bytes, two keys
    /// with one id, a non-positive lifetime, a negative skew, or an over-long audience. A worker
    /// refuses to start rather than run with a key it misread.</exception>
    public GrantKeys(
        IReadOnlyList<byte[]> keys, string audience = "", long maxTtlSeconds = DefaultMaxTtlSeconds,
        long clockSkewSeconds = DefaultClockSkewSeconds)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(audience);
        if (keys.Count == 0)
        {
            throw new ArgumentException("grant configuration needs at least one key", nameof(keys));
        }

        if (keys.Any(k => k is null || k.Length != KeyLength))
        {
            throw new ArgumentException($"every grant key must be exactly {KeyLength} bytes", nameof(keys));
        }

        var ids = keys.Select(k => Convert.ToHexString(KeyId(k))).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new ArgumentException("grant keys must be distinct", nameof(keys));
        }

        if (maxTtlSeconds <= 0)
        {
            throw new ArgumentException("max_ttl_seconds must be positive", nameof(maxTtlSeconds));
        }

        if (clockSkewSeconds < 0)
        {
            throw new ArgumentException("clock_skew_seconds must not be negative", nameof(clockSkewSeconds));
        }

        if (Encoding.UTF8.GetByteCount(audience) > MaxField)
        {
            throw new ArgumentException("audience is too long", nameof(audience));
        }

        if (!ChaCha20Poly1305.IsSupported)
        {
            // Sealed grants are XChaCha20-Poly1305 by contract (IDENTITY_V1_SPEC.md §9.1); a
            // platform without the IETF cipher cannot mint or verify one, and should say so at
            // startup rather than on the first bearer.
            throw new PlatformNotSupportedException(
                "sealed grants need ChaCha20-Poly1305, which this platform's crypto library does not provide");
        }

        Keys = keys.Select(k => (byte[])k.Clone()).ToList();
        Audience = audience;
        MaxTtlSeconds = maxTtlSeconds;
        ClockSkewSeconds = clockSkewSeconds;
    }

    /// <summary>The keys, minting key first.</summary>
    public IReadOnlyList<byte[]> Keys { get; }

    /// <summary>See the constructor.</summary>
    public string Audience { get; }

    /// <summary>See the constructor.</summary>
    public long MaxTtlSeconds { get; }

    /// <summary>See the constructor.</summary>
    public long ClockSkewSeconds { get; }

    /// <summary>The 8-byte id a token names its sealing key with:
    /// <c>SHA-256("vgi_rpc.grant.kid.v1" 0x00 || key)[0:8]</c>.</summary>
    public static byte[] KeyId(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var input = new byte[s_kidDomain.Length + key.Length];
        s_kidDomain.CopyTo(input, 0);
        key.CopyTo(input, s_kidDomain.Length);
        return SHA256.HashData(input)[..8];
    }

    /// <summary>Builds from standard base64 key text (padding optional), minting key first.</summary>
    /// <exception cref="ArgumentException">A key that is not base64 of exactly 32 bytes.</exception>
    public static GrantKeys Parse(
        IEnumerable<string> encodedKeys, string audience = "", long maxTtlSeconds = DefaultMaxTtlSeconds,
        long clockSkewSeconds = DefaultClockSkewSeconds)
    {
        ArgumentNullException.ThrowIfNull(encodedKeys);
        var keys = new List<byte[]>();
        var index = 0;
        foreach (var text in encodedKeys)
        {
            index++;
            var stripped = (text ?? "").Trim();
            byte[] key;
            try
            {
                if (stripped.Length == 0 || !stripped.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
                {
                    throw new FormatException();
                }

                var unpadded = stripped.TrimEnd('=');
                key = Convert.FromBase64String(unpadded + new string('=', (4 - (unpadded.Length % 4)) % 4));
            }
            catch (FormatException exc)
            {
                throw new ArgumentException($"grant key #{index} is not valid base64", nameof(encodedKeys), exc);
            }

            if (key.Length != KeyLength)
            {
                throw new ArgumentException(
                    $"grant key #{index} decodes to {key.Length} bytes; exactly {KeyLength} are required", nameof(encodedKeys));
            }

            keys.Add(key);
        }

        return new GrantKeys(keys, audience, maxTtlSeconds, clockSkewSeconds);
    }

    /// <summary>Reads <c>VGI_RPC_GRANT_KEYS</c> / <c>_AUDIENCE</c> / <c>_MAX_TTL_SECONDS</c>, or
    /// returns <see langword="null"/> -- grants off -- when no key is set.</summary>
    /// <param name="environment">A mapping to read instead of the process environment.</param>
    /// <exception cref="ArgumentException">A malformed key or lifetime.</exception>
    public static GrantKeys? FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? Read(string name) => environment is null
            ? Environment.GetEnvironmentVariable(name)
            : environment.TryGetValue(name, out var value) ? value : null;

        var raw = (Read(KeysEnvironmentVariable) ?? "").Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var ttlText = (Read(MaxTtlEnvironmentVariable) ?? "").Trim();
        var maxTtl = DefaultMaxTtlSeconds;
        if (ttlText.Length > 0 && !long.TryParse(ttlText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out maxTtl))
        {
            throw new ArgumentException($"{MaxTtlEnvironmentVariable}='{ttlText}' is not an integer");
        }

        return Parse(
            raw.Split(',').Where(part => part.Trim().Length > 0),
            audience: Read(AudienceEnvironmentVariable) ?? "",
            maxTtlSeconds: maxTtl);
    }

    internal byte[] Aad(byte[] kid)
    {
        var audience = Encoding.UTF8.GetBytes(Audience);
        var aad = new byte[s_aadDomain.Length + kid.Length + audience.Length];
        s_aadDomain.CopyTo(aad, 0);
        kid.CopyTo(aad, s_aadDomain.Length);
        audience.CopyTo(aad, s_aadDomain.Length + kid.Length);
        return aad;
    }

    private static readonly byte[] s_kidDomain = Encoding.ASCII.GetBytes("vgi_rpc.grant.kid.v1\0");
    private static readonly byte[] s_aadDomain = Encoding.ASCII.GetBytes("vgi_rpc.grant.v1\0");
}

/// <summary>What a verified grant says.</summary>
/// <param name="Principal">Whose standing delegation this is -- the caller it was minted for.</param>
/// <param name="Scopes">What it may do; the worker interprets them.</param>
/// <param name="Purpose">Why it was minted, for the audit trail.</param>
/// <param name="GrantId">Correlation handle.</param>
/// <param name="IssuedAt">Seconds since the Unix epoch.</param>
/// <param name="ExpiresAt">Seconds since the Unix epoch.</param>
public sealed record GrantClaims(
    string Principal, IReadOnlyList<string> Scopes, string Purpose, string GrantId, long IssuedAt, long ExpiresAt);

/// <summary>A token carrying the grant prefix that could not be accepted.</summary>
/// <remarks>
/// One type for every cause -- malformed, wrong key, wrong audience, tampered, expired -- so a
/// caller cannot tell a forged token from a stale one except by <see cref="Expired"/>, which is
/// only set once the token was proven authentic and so tells a forger nothing.
/// </remarks>
public sealed class GrantInvalidException(string detail, bool expired = false) : Exception(detail)
{
    /// <summary>Authentic, but outside its lifetime.</summary>
    public bool Expired { get; } = expired;
}

/// <summary>Mints and verifies sealed grants (IDENTITY_V1_SPEC.md §9.1).</summary>
/// <remarks>
/// <code>
/// token    = "vgig1." base64url_nopad( kid(8) || envelope )
/// envelope = 0x01 || nonce(24) || XChaCha20-Poly1305(payload, aad)   ; ciphertext || tag(16)
/// aad      = "vgi_rpc.grant.v1" 0x00 || kid || UTF-8(audience)
/// payload  = issued_at i64 | expires_at i64 | grant_id | principal | purpose | u16 count | scopes   (LE; strings u16-length-prefixed UTF-8)
/// </code>
/// Every port mints and verifies byte-identically; <c>grant_token_vectors.json</c> pins it.
/// </remarks>
public static class SealedGrants
{
    /// <summary>The token prefix. The version is in the prefix, so an incompatible format routes
    /// elsewhere instead of being half-parsed.</summary>
    public const string TokenPrefix = "vgig1.";

    /// <summary>Longest token text considered at all -- the cap introspection applies.</summary>
    public const int MaxTokenChars = 4096;

    private const byte EnvelopeVersion = 1;
    private const int KidLength = 8;
    private const int NonceLength = XChaCha20Poly1305.NonceSize;
    private const int MaxField = 0xFFFF;

    /// <summary>Mints a grant with the first configured key.</summary>
    /// <param name="keys">The deployment's grant configuration.</param>
    /// <param name="principal">The caller the grant is for.</param>
    /// <param name="scopes">What it may do, in request order.</param>
    /// <param name="purpose">Why it is being minted.</param>
    /// <param name="ttlSeconds">Requested lifetime; capped at <see cref="GrantKeys.MaxTtlSeconds"/>.</param>
    /// <param name="now">Clock override (seconds), for tests and vectors.</param>
    /// <param name="grantId">Grant-id override, for tests and vectors.</param>
    /// <param name="nonce">A fixed 24-byte nonce, <b>for test vectors only</b> -- reusing a nonce
    /// under one key destroys the cipher's confidentiality and authenticity.</param>
    /// <returns>The token and the claims it carries.</returns>
    /// <exception cref="ArgumentException">A non-positive lifetime or a field too long to encode.</exception>
    public static (string Token, GrantClaims Claims) Mint(
        GrantKeys keys, string principal, IReadOnlyList<string> scopes, string purpose, long ttlSeconds,
        long? now = null, string? grantId = null, byte[]? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (ttlSeconds <= 0)
        {
            throw new ArgumentException("ttl_seconds must be positive", nameof(ttlSeconds));
        }

        var issuedAt = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new GrantClaims(
            principal, scopes.ToList(), purpose,
            grantId ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            issuedAt, issuedAt + Math.Min(ttlSeconds, keys.MaxTtlSeconds));
        var key = keys.Keys[0];
        var kid = GrantKeys.KeyId(key);
        if (nonce is not null && nonce.Length != NonceLength)
        {
            throw new ArgumentException($"nonce must be {NonceLength} bytes", nameof(nonce));
        }

        nonce ??= RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBody = XChaCha20Poly1305.Seal(key, nonce, EncodePayload(claims), keys.Aad(kid));
        var raw = new byte[KidLength + 1 + NonceLength + sealedBody.Length];
        kid.CopyTo(raw, 0);
        raw[KidLength] = EnvelopeVersion;
        nonce.CopyTo(raw, KidLength + 1);
        sealedBody.CopyTo(raw, KidLength + 1 + NonceLength);
        return (TokenPrefix + Base64UrlEncode(raw), claims);
    }

    /// <summary>Verifies a grant and returns its claims.</summary>
    /// <remarks>Order, normative: prefix, length, canonical base64url, key id, AEAD open, payload,
    /// then lifetime -- the lifetime is inside the ciphertext, so it is trusted only after the tag
    /// verified.</remarks>
    /// <param name="keys">The deployment's grant configuration.</param>
    /// <param name="token">The bearer credential, exactly as presented.</param>
    /// <param name="now">Clock override (seconds), for tests.</param>
    /// <exception cref="GrantInvalidException">For every cause; <see cref="GrantInvalidException.Expired"/>
    /// is set only for an authentic grant outside its lifetime.</exception>
    public static GrantClaims Verify(GrantKeys keys, string token, double? now = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (token is null || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            throw new GrantInvalidException("not a sealed grant");
        }

        if (token.Length > MaxTokenChars)
        {
            throw new GrantInvalidException("grant token is too long");
        }

        var raw = Base64UrlDecodeStrict(token[TokenPrefix.Length..]);
        if (raw.Length < KidLength)
        {
            throw new GrantInvalidException("grant token is truncated");
        }

        var kid = raw[..KidLength];
        var envelope = raw.AsSpan(KidLength);
        var key = keys.Keys.FirstOrDefault(k => GrantKeys.KeyId(k).AsSpan().SequenceEqual(kid))
            ?? throw new GrantInvalidException("grant was sealed with a key this deployment does not hold");
        if (envelope.Length < 1 + NonceLength + XChaCha20Poly1305.TagSize || envelope[0] != EnvelopeVersion)
        {
            throw new GrantInvalidException("grant failed verification");
        }

        byte[] payload;
        try
        {
            payload = XChaCha20Poly1305.Open(key, envelope.Slice(1, NonceLength), envelope[(1 + NonceLength)..], keys.Aad(kid));
        }
        catch (CryptographicException exc)
        {
            throw new GrantInvalidException("grant failed verification: " + exc.GetType().Name);
        }

        var claims = DecodePayload(payload);
        if (claims.Principal.Length == 0)
        {
            throw new GrantInvalidException("grant names no principal");
        }

        if (claims.ExpiresAt <= claims.IssuedAt || claims.ExpiresAt - claims.IssuedAt > keys.MaxTtlSeconds)
        {
            throw new GrantInvalidException("grant lifetime exceeds this deployment's maximum");
        }

        var current = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var skew = keys.ClockSkewSeconds;
        if (claims.IssuedAt > current + skew)
        {
            throw new GrantInvalidException("grant is not yet valid", expired: true);
        }

        if (current >= claims.ExpiresAt + skew)
        {
            throw new GrantInvalidException("grant has expired", expired: true);
        }

        return claims;
    }

    /// <summary>A minter that issues sealed grants -- what <see cref="IdentityImpl"/> installs when
    /// grant keys are configured and the worker supplied no minter of its own.</summary>
    public static IdentityImpl.GrantMinter Minter(GrantKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return (principal, purpose, scopes, ttlSeconds) =>
        {
            if (ttlSeconds <= 0)
            {
                throw new GrantRefusedException("ttl_seconds must be positive");
            }

            try
            {
                var (token, claims) = Mint(keys, principal, scopes, purpose, ttlSeconds);
                return new IssuedGrant(token, claims.ExpiresAt, claims.GrantId);
            }
            catch (ArgumentException exc)
            {
                throw new GrantRefusedException(exc.Message);
            }
        };
    }

    internal static byte[] EncodePayload(GrantClaims claims)
    {
        if (claims.Scopes.Count > MaxField)
        {
            throw new ArgumentException("too many scopes");
        }

        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(header, claims.IssuedAt);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], claims.ExpiresAt);
        stream.Write(header);
        WriteText(stream, claims.GrantId);
        WriteText(stream, claims.Principal);
        WriteText(stream, claims.Purpose);
        Span<byte> count = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)claims.Scopes.Count);
        stream.Write(count);
        foreach (var scope in claims.Scopes)
        {
            WriteText(stream, scope);
        }

        return stream.ToArray();
    }

    private static void WriteText(MemoryStream stream, string value)
    {
        var raw = Encoding.UTF8.GetBytes(value ?? "");
        if (raw.Length > MaxField)
        {
            throw new ArgumentException("grant field longer than 65535 bytes");
        }

        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)raw.Length);
        stream.Write(length);
        stream.Write(raw);
    }

    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Parses strictly: exact lengths, valid UTF-8, no trailing bytes.</summary>
    internal static GrantClaims DecodePayload(byte[] payload)
    {
        var pos = 0;

        ReadOnlySpan<byte> Take(int n)
        {
            if (pos + n > payload.Length)
            {
                throw new GrantInvalidException("grant payload is truncated");
            }

            var chunk = payload.AsSpan(pos, n);
            pos += n;
            return chunk;
        }

        string Text()
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            try
            {
                return s_strictUtf8.GetString(Take(length));
            }
            catch (DecoderFallbackException)
            {
                throw new GrantInvalidException("grant payload is not UTF-8");
            }
        }

        var issuedAt = BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        var expiresAt = BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        var grantId = Text();
        var principal = Text();
        var purpose = Text();
        var count = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        var scopes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            scopes.Add(Text());
        }

        if (pos != payload.Length)
        {
            throw new GrantInvalidException("grant payload has trailing bytes");
        }

        return new GrantClaims(principal, scopes, purpose, grantId, issuedAt, expiresAt);
    }

    internal static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes unpadded base64url, rejecting any non-canonical spelling: re-encoding must
    /// reproduce the text, so non-zero trailing bits are refused and one token has one spelling.</summary>
    internal static byte[] Base64UrlDecodeStrict(string text)
    {
        if (text.Length == 0 || text.Length % 4 == 1
            || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new GrantInvalidException("grant token is not unpadded base64url");
        }

        byte[] raw;
        try
        {
            var standard = text.Replace('-', '+').Replace('_', '/');
            raw = Convert.FromBase64String(standard + new string('=', (4 - (standard.Length % 4)) % 4));
        }
        catch (FormatException)
        {
            throw new GrantInvalidException("grant token is not unpadded base64url");
        }

        if (!string.Equals(Base64UrlEncode(raw), text, StringComparison.Ordinal))
        {
            throw new GrantInvalidException("grant token is not canonical base64url");
        }

        return raw;
    }
}
