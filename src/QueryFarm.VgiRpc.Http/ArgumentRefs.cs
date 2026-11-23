using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.External;

namespace QueryFarm.VgiRpc.Http;

/// <summary>The opened contents of an argument reference.</summary>
/// <param name="Size">The sealed byte length.</param>
/// <param name="Sha256">The sealed SHA-256, 64 lowercase hex characters.</param>
/// <param name="ObjectId">The storage id.</param>
public sealed record ArgumentRef(long Size, string Sha256, string ObjectId);

/// <summary>
/// Argument references (<c>STATE_REF_V1.md</c> §10.2): the <c>ref</c> column of an
/// <c>__upload_url__/init</c> grant, and the server primitive that resolves one.
/// </summary>
/// <remarks>
/// <para>
/// <c>ref = seal(0x01 || size u64le || sha256[32] || id_len u16le || object_id)</c> under the
/// server's token key with <c>ARGREF_AAD = "vgi_rpc.argref.v1\0" || BASE_AAD(identity, "\0server")</c>,
/// where <c>BASE_AAD</c> is exactly the stream-token AAD this server already computes
/// (<see cref="StickySessions.ComputeCallAad"/>): the identity's domain and principal, and its
/// peer-evidence binding digest when present. So a reference opens only for the principal (and
/// binding) that requested the grant, on a server holding the same token key, and cannot open as
/// any other sealed kind.
/// </para>
/// <para>
/// The envelope is this port's <see cref="Crypto"/> (AES-256-GCM, 12-byte nonce), not the
/// reference's XChaCha20-Poly1305: a reference is minted and opened by the same deployment and
/// never crosses ports, like every token this port seals.
/// </para>
/// <para>
/// A reference is minted from the <i>claimed</i> <c>(size, sha256)</c> without seeing the bytes:
/// storage enforces them on the PUT where it can, and <see cref="ResolveAsync"/> verifies them on
/// the GET. There is no expiry; on an unauthenticated server a reference is a bearer capability
/// for as long as the object lives (bucket lifecycle, at least a day).
/// </para>
/// </remarks>
public static class ArgumentRefs
{
    /// <summary>The scope a grant is bound to: the server, not a protocol (the reference's
    /// <c>SERVER_SCOPE</c>).</summary>
    public const string ServerScope = "\0server";

    private const byte SealVersion = 0x01;
    private const byte PlaintextVersion = 0x01;
    private const int Sha256Length = 32;
    private const int FixedLength = 1 + 8 + Sha256Length + 2;
    private const int MaxObjectIdBytes = 1024;
    private static readonly byte[] s_aadPrefix = "vgi_rpc.argref.v1\0"u8.ToArray();

    /// <summary><c>ARGREF_AAD</c> for <paramref name="identity"/>.</summary>
    public static byte[] ComputeAad(AuthIdentity? identity) =>
        [.. s_aadPrefix, .. StickySessions.ComputeCallAad(identity, ServerScope)];

    /// <summary>Seals an argument reference for one granted upload.</summary>
    /// <param name="tokenKey">The server's token key.</param>
    /// <param name="identity">The <c>__upload_url__/init</c> request's identity.</param>
    /// <param name="size">The entry's declared size (at least 1).</param>
    /// <param name="sha256Hex">The entry's declared SHA-256, 64 lowercase hex characters.</param>
    /// <param name="objectId">The storage id the provider returned (1-1024 bytes of UTF-8).</param>
    /// <exception cref="ArgumentException">A bad size, digest or object id.</exception>
    public static byte[] Mint(byte[] tokenKey, AuthIdentity? identity, long size, string sha256Hex, string objectId)
    {
        ArgumentNullException.ThrowIfNull(tokenKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        if (!UploadDigest.IsLowerHexSha256(sha256Hex))
        {
            throw new ArgumentException("sha256 must be 64 lowercase hex characters", nameof(sha256Hex));
        }

        var id = Encoding.UTF8.GetBytes(objectId ?? "");
        if (id.Length is < 1 or > MaxObjectIdBytes)
        {
            throw new ArgumentException($"object_id must be 1-{MaxObjectIdBytes} bytes of UTF-8, got {id.Length}", nameof(objectId));
        }

        var plaintext = new byte[FixedLength + id.Length];
        plaintext[0] = PlaintextVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(plaintext.AsSpan(1), (ulong)size);
        Convert.FromHexString(sha256Hex).CopyTo(plaintext, 9);
        BinaryPrimitives.WriteUInt16LittleEndian(plaintext.AsSpan(9 + Sha256Length), (ushort)id.Length);
        id.CopyTo(plaintext, FixedLength);
        return Crypto.Seal(plaintext, tokenKey, ComputeAad(identity), SealVersion);
    }

    /// <summary>Opens an argument reference sealed for <paramref name="identity"/>.</summary>
    /// <exception cref="RpcException"><c>invalid_arguments_ref</c>: wrong key, principal, binding
    /// or scope, tampering, an unknown version, or a malformed layout.</exception>
    public static ArgumentRef Open(byte[] tokenKey, AuthIdentity? identity, byte[] reference)
    {
        ArgumentNullException.ThrowIfNull(tokenKey);
        ArgumentNullException.ThrowIfNull(reference);
        byte[] plaintext;
        try
        {
            plaintext = Crypto.Open(reference, tokenKey, ComputeAad(identity), SealVersion);
        }
        catch (Crypto.SealException)
        {
            throw StateRefErrors.InvalidArgumentsRef(
                "argument reference does not open (tampered, or granted to another principal or server)");
        }

        if (plaintext.Length < FixedLength || plaintext[0] != PlaintextVersion)
        {
            throw StateRefErrors.InvalidArgumentsRef("argument reference has an unknown version or a malformed layout");
        }

        var size = BinaryPrimitives.ReadUInt64LittleEndian(plaintext.AsSpan(1));
        var digest = Convert.ToHexStringLower(plaintext.AsSpan(9, Sha256Length));
        int idLength = BinaryPrimitives.ReadUInt16LittleEndian(plaintext.AsSpan(9 + Sha256Length));
        if (idLength is < 1 or > MaxObjectIdBytes || FixedLength + idLength != plaintext.Length || size > long.MaxValue)
        {
            throw StateRefErrors.InvalidArgumentsRef("argument reference has a malformed layout");
        }

        string objectId;
        try
        {
            objectId = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plaintext, FixedLength, idLength);
        }
        catch (DecoderFallbackException)
        {
            throw StateRefErrors.InvalidArgumentsRef("argument reference has a malformed layout");
        }

        return new ArgumentRef((long)size, digest, objectId);
    }

    /// <summary>
    /// Opens, fetches (through <paramref name="cache"/>) and verifies an argument reference. In
    /// order: validate the claimed location; open the reference with <c>ARGREF_AAD</c>; check the
    /// sealed size and SHA-256 equal the claimed ones; look the <c>object_id</c> up in the object
    /// cache; on a miss, one single-flight <see cref="IObjectStore.GetAsync"/>; verify length and
    /// SHA-256; return.
    /// </summary>
    /// <param name="reference">The sealed reference.</param>
    /// <param name="size">The claimed byte length (<c>ArgumentsLocation.size</c>).</param>
    /// <param name="sha256Hex">The claimed SHA-256, 64 lowercase hex characters.</param>
    /// <param name="tokenKey">The server's token key.</param>
    /// <param name="identity">The presenting request's identity.</param>
    /// <param name="storage">The server's object store, or <see langword="null"/>.</param>
    /// <param name="maxFetchBytes">The server's inbound fetch cap.</param>
    /// <param name="cache">The object cache; <see langword="null"/> uses <see cref="ObjectCache.Default"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified object bytes, shared with the cache: treat them as immutable.</returns>
    /// <exception cref="RpcException"><c>invalid_arguments_ref</c>, <c>arguments_ref_integrity</c>,
    /// <c>arguments_ref_missing</c> or <c>storage_unavailable</c>.</exception>
    public static async Task<byte[]> ResolveAsync(
        byte[] reference, long size, string sha256Hex, byte[] tokenKey, AuthIdentity? identity,
        IObjectStore? storage, long maxFetchBytes, ObjectCache? cache = null, CancellationToken cancellationToken = default)
    {
        if (size < 1)
        {
            throw StateRefErrors.InvalidArgumentsRef($"argument location size must be an integer >= 1, got {size}");
        }

        if (!UploadDigest.IsLowerHexSha256(sha256Hex))
        {
            throw StateRefErrors.InvalidArgumentsRef("argument location sha256 must be 64 lowercase hex characters");
        }

        if (storage is null)
        {
            throw StateRefErrors.InvalidArgumentsRef("this server has no object storage configured to resolve argument references");
        }

        var opened = Open(tokenKey, identity, reference);
        if (opened.Size != size || opened.Sha256 != sha256Hex)
        {
            throw StateRefErrors.InvalidArgumentsRef("argument reference does not match the size and sha256 it was sent with");
        }

        if (size > maxFetchBytes)
        {
            throw StateRefErrors.InvalidArgumentsRef(
                $"argument object is {size} bytes, over this server's max_fetch_bytes ({maxFetchBytes})");
        }

        var expected = Convert.FromHexString(sha256Hex);
        var (value, _) = await (cache ?? ObjectCache.Default).GetOrFetchAsync(
            opened.ObjectId,
            async ct =>
            {
                byte[] data;
                try
                {
                    data = await storage.GetAsync(opened.ObjectId, maxFetchBytes, ct).ConfigureAwait(false);
                }
                catch (ObjectMissingException)
                {
                    throw StateRefErrors.ArgumentsRefMissing("the uploaded argument object is gone (expired or deleted)");
                }
                catch (Exception exc) when (exc is not RpcException)
                {
                    throw StateRefErrors.StorageUnavailable($"object storage failed to return the argument object ({exc.GetType().Name})");
                }

                if (data.LongLength != size)
                {
                    throw StateRefErrors.ArgumentsRefIntegrity($"argument object size mismatch: expected {size}, got {data.LongLength}");
                }

                if (!SHA256.HashData(data).AsSpan().SequenceEqual(expected))
                {
                    throw StateRefErrors.ArgumentsRefIntegrity("argument object sha256 mismatch");
                }

                return data;
            },
            cancellationToken).ConfigureAwait(false);

        // A cached value passed the checks above for this object id; a length change means the id
        // was reused, which storage never does.
        return value.LongLength != size
            ? throw StateRefErrors.ArgumentsRefIntegrity($"argument object size mismatch: expected {size}, got {value.LongLength}")
            : value;
    }
}
