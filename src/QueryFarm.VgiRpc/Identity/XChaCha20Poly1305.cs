using System.Buffers.Binary;
using System.Security.Cryptography;

namespace QueryFarm.VgiRpc.Identity;

/// <summary>
/// XChaCha20-Poly1305 (libsodium's <c>crypto_aead_xchacha20poly1305_ietf</c>), built from .NET's
/// IETF <see cref="ChaCha20Poly1305"/> plus a managed HChaCha20.
/// </summary>
/// <remarks>
/// <para>
/// .NET ships no XChaCha20, so the 24-byte-nonce variant is derived the standard way: the first 16
/// nonce bytes and the key go through HChaCha20 to produce a subkey, and the IETF cipher runs under
/// that subkey with the nonce <c>0x00000000 || nonce[16..24]</c>. Output is <c>ciphertext ||
/// tag(16)</c> -- byte-identical to libsodium, which is what the reference uses and what the
/// sealed-grant vectors pin.
/// </para>
/// <para>
/// Sealed grants need exactly this cipher (IDENTITY_V1_SPEC.md §9.1: "the stream-state envelope").
/// This port's own HTTP state tokens use AES-GCM -- they never cross a port boundary, grants do.
/// <see cref="ChaCha20Poly1305"/> is unavailable on Windows before Server 2022 / Windows 11, where
/// sealing throws <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
internal static class XChaCha20Poly1305
{
    public const int KeySize = 32;
    public const int NonceSize = 24;
    public const int TagSize = 16;

    /// <summary>Encrypts and authenticates <paramref name="plaintext"/>.</summary>
    /// <returns><c>ciphertext || tag</c>.</returns>
    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        Span<byte> subkey = stackalloc byte[KeySize];
        Span<byte> ietfNonce = stackalloc byte[12];
        Derive(key, nonce, subkey, ietfNonce);
        var output = new byte[plaintext.Length + TagSize];
        using var cipher = new ChaCha20Poly1305(subkey);
        cipher.Encrypt(ietfNonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), aad);
        CryptographicOperations.ZeroMemory(subkey);
        return output;
    }

    /// <summary>Verifies and decrypts <c>ciphertext || tag</c>.</summary>
    /// <exception cref="CryptographicException">The tag does not verify.</exception>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> body, ReadOnlySpan<byte> aad)
    {
        if (body.Length < TagSize)
        {
            throw new CryptographicException("ciphertext is shorter than its tag");
        }

        Span<byte> subkey = stackalloc byte[KeySize];
        Span<byte> ietfNonce = stackalloc byte[12];
        Derive(key, nonce, subkey, ietfNonce);
        var plaintext = new byte[body.Length - TagSize];
        try
        {
            using var cipher = new ChaCha20Poly1305(subkey);
            cipher.Decrypt(ietfNonce, body[..^TagSize], body[^TagSize..], plaintext, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }

        return plaintext;
    }

    private static void Derive(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> subkey, Span<byte> ietfNonce)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"key must be {KeySize} bytes", nameof(key));
        }

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException($"nonce must be {NonceSize} bytes", nameof(nonce));
        }

        HChaCha20(key, nonce[..16], subkey);
        ietfNonce[..4].Clear();
        nonce[16..].CopyTo(ietfNonce[4..]);
    }

    /// <summary>HChaCha20 (draft-irtf-cfrg-xchacha §2.2): the ChaCha20 block function over
    /// <c>constants || key || nonce16</c>, without the final feed-forward, emitting words 0..3 and
    /// 12..15.</summary>
    internal static void HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16, Span<byte> output)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865;
        s[1] = 0x3320646e;
        s[2] = 0x79622d32;
        s[3] = 0x6b206574;
        for (var i = 0; i < 8; i++)
        {
            s[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(4 * i)..]);
        }

        for (var i = 0; i < 4; i++)
        {
            s[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16[(4 * i)..]);
        }

        for (var round = 0; round < 10; round++)
        {
            QuarterRound(s, 0, 4, 8, 12);
            QuarterRound(s, 1, 5, 9, 13);
            QuarterRound(s, 2, 6, 10, 14);
            QuarterRound(s, 3, 7, 11, 15);
            QuarterRound(s, 0, 5, 10, 15);
            QuarterRound(s, 1, 6, 11, 12);
            QuarterRound(s, 2, 7, 8, 13);
            QuarterRound(s, 3, 4, 9, 14);
        }

        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output[(4 * i)..], s[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(output[(16 + (4 * i))..], s[12 + i]);
        }

        s.Clear();
    }

    private static void QuarterRound(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] = uint.RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d]; s[b] = uint.RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b]; s[d] = uint.RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d]; s[b] = uint.RotateLeft(s[b] ^ s[c], 7);
    }
}
