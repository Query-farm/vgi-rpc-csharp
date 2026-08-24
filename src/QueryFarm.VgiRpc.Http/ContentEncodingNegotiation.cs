using Microsoft.AspNetCore.Http;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Content-encoding codecs the HTTP transport can produce/consume for response/request bodies
/// — mirrors the canonical Python repo's <c>vgi_rpc._codec.Encoding</c>. Wire names (used in
/// <c>Content-Encoding</c>/<c>Accept-Encoding</c> headers) are the lowercase enum member names.
/// <see cref="Identity"/> is the no-op transform, not a compressor — it exists so a client can
/// *explicitly* ask for an uncompressed response by listing it in an accept header; otherwise
/// "no compression" is only reachable by accident, when nothing the client offers is producible.
/// </summary>
public enum ContentEncoding
{
    Zstd,
    Gzip,
    Identity,
}

/// <summary>
/// Parses <c>Accept-Encoding</c>/<c>X-VGI-Accept-Encoding</c> headers and picks a response
/// codec, mirroring Python's <c>_CompressionMiddleware._pick_response_encoding</c> /
/// <c>parse_encoding_list</c>.
/// </summary>
public static class ContentEncodingNegotiation
{
    /// <summary>
    /// Parses a comma-separated <c>Accept-Encoding</c>-style header. Tokens not naming a known
    /// codec are skipped silently; q-values are ignored (only presence/order matters here, not
    /// quality weighting); order is preserved and duplicates removed so callers can honour the
    /// client's stated preference.
    /// </summary>
    public static List<ContentEncoding> ParseEncodingList(string? headerValue)
    {
        var result = new List<ContentEncoding>();
        if (string.IsNullOrEmpty(headerValue))
        {
            return result;
        }

        foreach (var raw in headerValue.Split(','))
        {
            var token = raw.Trim();
            var semicolon = token.IndexOf(';');
            if (semicolon >= 0)
            {
                token = token[..semicolon].Trim();
            }

            if (token.Length == 0)
            {
                continue;
            }

            ContentEncoding? encoding = token.ToLowerInvariant() switch
            {
                "zstd" => ContentEncoding.Zstd,
                "gzip" => ContentEncoding.Gzip,
                "identity" => ContentEncoding.Identity,
                _ => null,
            };
            if (encoding is { } enc && !result.Contains(enc))
            {
                result.Add(enc);
            }
        }

        return result;
    }

    /// <summary>
    /// Picks the response codec for <paramref name="request"/>, honoring the client's preference
    /// order with <c>X-VGI-Accept-Encoding</c> taking precedence over the generic
    /// <c>Accept-Encoding</c> — matches Python's reasoning: some HTTP client libraries inject
    /// their own <c>Accept-Encoding</c> listing gzip before zstd, which would silently override
    /// the zstd-first order a VGI-aware caller stated via the custom header.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="producible">Codecs this server can actually produce (excludes
    /// <see cref="ContentEncoding.Identity"/>, which is always implicitly available).</param>
    /// <returns>
    /// The chosen codec (<see langword="null"/> for no compression — either no overlap, or the
    /// client explicitly listed <c>identity</c> first), and whether the client's preference came
    /// from the custom header (which decides which response header gets stamped).
    /// </returns>
    public static (ContentEncoding? Chosen, bool UseCustomHeader) PickResponseEncoding(HttpRequest request, IReadOnlySet<ContentEncoding> producible)
    {
        var standard = ParseEncodingList(request.Headers.AcceptEncoding.ToString());
        var custom = ParseEncodingList(request.Headers["X-VGI-Accept-Encoding"].ToString());

        foreach (var enc in custom.Concat(standard.Where(e => !custom.Contains(e))))
        {
            if (enc == ContentEncoding.Identity)
            {
                return (null, false);
            }

            if (producible.Contains(enc))
            {
                return (enc, custom.Contains(enc) && !standard.Contains(enc));
            }
        }

        return (null, custom.Count > 0);
    }
}
