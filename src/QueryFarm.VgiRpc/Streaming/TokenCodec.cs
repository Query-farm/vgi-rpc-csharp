using System.IO.Compression;

namespace QueryFarm.VgiRpc.Streaming;

/// <summary>
/// The standard token codec (<c>STATE_REF_V1.md</c> §7): <c>codec (1 byte) || body</c>, applied
/// before sealing so a token's redundancy is removed while it is still plaintext.
/// </summary>
/// <remarks>
/// <para>
/// <c>0x00</c> raw, <c>0x01</c> zstd (the writer's choice, level 3), <c>0x02</c> raw DEFLATE
/// (RFC 1951). The writer compresses only when that makes the body smaller, so a small payload
/// never grows. The reader accepts all three and rejects any other tag.
/// </para>
/// <para>
/// In this port the codec frames inline <see cref="StateRef"/> payloads (§3.1). No sealed token
/// of vgi-rpc-csharp carries a payload worth compressing in 3.4: HTTP stream state lives in
/// server memory and the stream token seals only a 16-byte call id (§5.4), the sticky-session
/// token a server id and a session id, and an argument reference a fixed 43-byte record plus an
/// object id. When this port's streams go stateless (post-3.4) its cursor and call tokens adopt
/// this codec.
/// </para>
/// </remarks>
public static class TokenCodec
{
    /// <summary>Raw body.</summary>
    public const byte Raw = 0x00;

    /// <summary>zstd frame.</summary>
    public const byte Zstd = 0x01;

    /// <summary>Raw DEFLATE (RFC 1951).</summary>
    public const byte Deflate = 0x02;

    /// <summary>The decompressed-size bound for tokens: 64 MiB.</summary>
    public const long MaxTokenBytes = 64L * 1024 * 1024;

    /// <summary>Frames <paramref name="body"/>: zstd when that is smaller, raw otherwise.</summary>
    /// <param name="body">The plaintext.</param>
    /// <param name="level">zstd level (the reference uses 3).</param>
    /// <returns><c>codec || body</c>.</returns>
    public static byte[] Pack(ReadOnlySpan<byte> body, int level = 3)
    {
        if (body.Length > 0)
        {
            using var compressor = new ZstdSharp.Compressor(level);
            var compressed = compressor.Wrap(body);
            if (compressed.Length < body.Length)
            {
                var framed = new byte[compressed.Length + 1];
                framed[0] = Zstd;
                compressed.CopyTo(framed.AsSpan(1));
                return framed;
            }
        }

        var raw = new byte[body.Length + 1];
        raw[0] = Raw;
        body.CopyTo(raw.AsSpan(1));
        return raw;
    }

    /// <summary>Opens a framed payload.</summary>
    /// <param name="payload"><c>codec || body</c>.</param>
    /// <param name="maxBytes">The largest decompressed body accepted.</param>
    /// <returns>The plaintext.</returns>
    /// <exception cref="InvalidDataException">An empty payload, an unknown codec, a body that
    /// does not decompress, or one that decompresses past <paramref name="maxBytes"/>.</exception>
    public static byte[] Unpack(ReadOnlySpan<byte> payload, long maxBytes)
    {
        if (payload.IsEmpty)
        {
            throw new InvalidDataException("empty codec payload");
        }

        var body = payload[1..];
        switch (payload[0])
        {
            case Raw:
                return body.Length > maxBytes
                    ? throw new InvalidDataException($"payload exceeds {maxBytes} bytes")
                    : body.ToArray();
            case Zstd:
                return ReadBounded(stream => new ZstdSharp.DecompressionStream(stream), body, maxBytes);
            case Deflate:
                return ReadBounded(stream => new DeflateStream(stream, CompressionMode.Decompress), body, maxBytes);
            default:
                throw new InvalidDataException($"unknown codec 0x{payload[0]:x2}");
        }
    }

    private static byte[] ReadBounded(Func<Stream, Stream> open, ReadOnlySpan<byte> body, long maxBytes)
    {
        try
        {
            using var input = new MemoryStream(body.ToArray(), writable: false);
            using var decoder = open(input);
            using var output = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = decoder.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk, 0, read);
                if (output.Length > maxBytes)
                {
                    throw new InvalidDataException($"payload decompresses past {maxBytes} bytes");
                }
            }

            return output.ToArray();
        }
        catch (Exception exc) when (exc is not InvalidDataException)
        {
            throw new InvalidDataException("payload does not decompress", exc);
        }
    }
}
