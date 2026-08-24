using System.Security.Cryptography;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Generic AEAD seal/open primitive for HTTP streaming state tokens (see docs/roadmap.md M6 —
/// the AES-GCM state-token codec). Mirrors the canonical Python repo's <c>vgi_rpc.crypto</c>
/// module's envelope shape and API one-for-one, with one deliberate substitution: **AES-256-GCM**
/// (12-byte nonce) in place of Python's XChaCha20-Poly1305 (24-byte nonce).
///
/// Two concrete reasons, both from the original plan doc: .NET's <see cref="ChaCha20Poly1305"/>
/// throws <see cref="PlatformNotSupportedException"/> on Windows Server versions before Windows
/// Server 2022/Windows 11 (a CNG gap), which is disqualifying given this port's explicit
/// Windows+Linux requirement with no stated Windows-version floor; and .NET doesn't ship XChaCha20
/// at all (only IETF IETF 12-byte-nonce ChaCha20). This is safe because state tokens are confirmed
/// transport-implementation-internal, not part of the cross-language wire contract — every other
/// vgi-rpc port already picked its own envelope (Rust uses HMAC-signed tokens, Java uses
/// CBOR+HMAC).
///
/// Wire format (identical shape to Python's, modulo nonce length):
/// <c>version (1 byte) || nonce (12 bytes) || ciphertext || tag (16 bytes)</c>.
/// </summary>
public static class Crypto
{
    private const int KeyLen = 32;
    private const int NonceLen = 12; // AES-GCM's standard nonce length (vs. XChaCha20's 24)
    private const int TagLen = 16;
    private const int VersionLen = 1;
    private const int MinTokenLen = VersionLen + NonceLen + TagLen;

    /// <summary>
    /// Raised by <see cref="Open"/> for any token it cannot open. Malformed, wrong-version,
    /// tampered, wrong-key, and wrong-AAD tokens all map to this single exception so callers
    /// cannot distinguish them (e.g. via exception type or message) — "wrong AAD"
    /// (cross-principal replay) is indistinguishable from "garbage input". Mirrors Python's
    /// <c>SealError</c>.
    /// </summary>
    public sealed class SealException : Exception
    {
        public SealException(string message)
            : base(message)
        {
        }

        public SealException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Stretches or compresses an operator-supplied key to the 32-byte AES-256 key length.
    /// AES-256-GCM requires exactly 32 bytes; hashing through SHA-256 yields a 32-byte
    /// pseudo-random key for any input — collision-resistant, deterministic, and
    /// indistinguishable from a directly-supplied 32-byte key to an attacker who never sees the
    /// input. A key already 32 bytes long is used as-is. Mirrors Python's <c>normalize_key</c>.
    /// </summary>
    public static byte[] NormalizeKey(byte[] key) => key.Length == KeyLen ? key : SHA256.HashData(key);

    /// <summary>
    /// Seals <paramref name="payload"/> into an authenticated-encrypted envelope.
    /// </summary>
    /// <param name="payload">Plaintext bytes to encrypt.</param>
    /// <param name="key">Master key. Any length — normalized via <see cref="NormalizeKey"/>.</param>
    /// <param name="aad">Associated data: authenticated but not encrypted. The identical
    /// <paramref name="aad"/> must be supplied to <see cref="Open"/>. Bind identity or any
    /// non-swappable context here.</param>
    /// <param name="version">1-byte format selector, echoed as the first output byte.</param>
    /// <returns>The sealed envelope: <c>version || nonce || ciphertext || tag</c>.</returns>
    public static byte[] Seal(ReadOnlySpan<byte> payload, byte[] key, ReadOnlySpan<byte> aad, byte version = 1)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[TagLen];
        using (var aesGcm = new AesGcm(NormalizeKey(key), TagLen))
        {
            aesGcm.Encrypt(nonce, payload, ciphertext, tag, aad);
        }

        var result = new byte[VersionLen + NonceLen + ciphertext.Length + TagLen];
        result[0] = version;
        nonce.CopyTo(result.AsSpan(VersionLen));
        ciphertext.CopyTo(result.AsSpan(VersionLen + NonceLen));
        tag.CopyTo(result.AsSpan(VersionLen + NonceLen + ciphertext.Length));
        return result;
    }

    /// <summary>
    /// Opens and verifies an envelope produced by <see cref="Seal"/>.
    /// </summary>
    /// <param name="token">The sealed envelope.</param>
    /// <param name="key">Master key — must match the key used to seal.</param>
    /// <param name="aad">Associated data — must match the <paramref name="aad"/> used to seal.</param>
    /// <param name="version">Expected 1-byte format selector.</param>
    /// <returns>The decrypted plaintext.</returns>
    /// <exception cref="SealException">On any malformed, wrong-version, tampered, wrong-key, or
    /// wrong-AAD token. All failure modes are indistinguishable.</exception>
    public static byte[] Open(ReadOnlySpan<byte> token, byte[] key, ReadOnlySpan<byte> aad, byte version = 1)
    {
        if (token.Length < MinTokenLen || token[0] != version)
        {
            throw new SealException("malformed or wrong-version token");
        }

        var nonce = token.Slice(VersionLen, NonceLen);
        var ciphertext = token[(VersionLen + NonceLen)..^TagLen];
        var tag = token[^TagLen..];
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aesGcm = new AesGcm(NormalizeKey(key), TagLen);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException exc)
        {
            throw new SealException("token verification failed", exc);
        }

        return plaintext;
    }
}
