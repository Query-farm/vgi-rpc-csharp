using System.IO.Compression;
using QueryFarm.VgiRpc.Http;

namespace QueryFarm.VgiRpc.Client.Http;

internal static class HttpCodec
{
    public static byte[] Compress(byte[] data, ContentEncoding encoding, int level) => encoding switch
    {
        ContentEncoding.Zstd => CompressZstd(data, level),
        ContentEncoding.Gzip => CompressGzip(data),
        _ => data,
    };

    public static byte[] Decompress(byte[] data, string? encoding) => encoding?.Trim().ToLowerInvariant() switch
    {
        "zstd" => DecompressZstd(data),
        "gzip" => DecompressGzip(data),
        _ => data,
    };

    private static byte[] CompressZstd(byte[] data, int level)
    {
        using var compressor = new ZstdSharp.Compressor(level);
        return compressor.Wrap(data).ToArray();
    }

    private static byte[] DecompressZstd(byte[] data)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }

    private static byte[] CompressGzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
