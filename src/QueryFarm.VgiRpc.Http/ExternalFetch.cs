using QueryFarm.VgiRpc.Errors;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// URL fetching for ExternalLocation pointer resolution — a simplified port of the canonical
/// Python repo's <c>vgi_rpc.external_fetch</c>. See <c>docs/roadmap.md</c> M13.
///
/// <para><b>Deliberately simpler than Python here.</b> Python's fetcher does HEAD-first probing
/// with parallel Range-request chunking and speculative-hedge retries for large objects — real
/// production sophistication that nothing in the conformance surface actually requires (the
/// externalization conformance payloads are single-digit KB to tens of KB). This port does a
/// single streaming GET with manual, per-hop-validated redirect following and the same two
/// independent size caps (encoded/on-wire vs. decoded/decompressed) Python enforces — every
/// <c>TestExternalFetchSecurity</c> conformance case passes against this, but a future milestone
/// wanting the parallel-range-request behavior for genuinely large externalized objects would
/// need to add it.</para>
/// </summary>
public static class ExternalFetch
{
    private static readonly HttpClient s_client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, // redirects are followed manually so each hop can be validated
        AutomaticDecompression = System.Net.DecompressionMethods.None, // Content-Encoding is informational metadata here, not a negotiated transport encoding — decompressed manually against ExternalCompression
    });

    /// <summary>Rejects URLs that do not use the <c>https</c> scheme — the default
    /// <see cref="ServerExternalConfig.UrlValidator"/>/<see cref="ClientExternalConfig.UrlValidator"/>.</summary>
    public static void HttpsOnlyValidator(string url)
    {
        Uri parsed;
        try
        {
            parsed = new Uri(url);
        }
        catch (UriFormatException exc)
        {
            throw new ArgumentException($"malformed URL: {exc.Message}", nameof(url));
        }

        if (!string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"URL scheme '{parsed.Scheme}' not allowed (only 'https' is permitted)", nameof(url));
        }
    }

    /// <summary>Returns a diagnostic-safe URL with credentials and query/fragment data removed —
    /// query strings on external-location URLs commonly carry S3/GCS signing credentials. Keeps
    /// only scheme, host, port, and path so errors remain actionable without turning logs or
    /// remote tracebacks into credential stores.</summary>
    public static string RedactUrl(string url)
    {
        try
        {
            var parsed = new Uri(url);
            var builder = new UriBuilder(parsed.Scheme, parsed.Host, parsed.IsDefaultPort ? -1 : parsed.Port, parsed.AbsolutePath);
            return builder.Uri.ToString();
        }
        catch (UriFormatException)
        {
            return "<invalid-url>";
        }
    }

    /// <summary>
    /// Fetches <paramref name="url"/>, following redirects manually (each hop re-validated via
    /// <paramref name="urlValidator"/> <i>before</i> the request is issued — a disallowed target
    /// is never contacted at all), enforcing both size caps, and returning the fully decompressed
    /// body bytes.
    /// </summary>
    /// <exception cref="RpcException">On a validator rejection, exceeded redirect limit, a
    /// non-success status, or either cap being exceeded. Every message is built from
    /// <see cref="RedactUrl"/>, never the raw URL.</exception>
    public static async Task<byte[]> FetchUrlAsync(string url, FetchConfig config, Action<string>? urlValidator, CancellationToken cancellationToken)
    {
        var current = url;
        var maxDecompressed = config.MaxDecompressedBytes ?? config.MaxFetchBytes * 16;

        for (var hop = 0; ; hop++)
        {
            if (hop > config.MaxRedirects)
            {
                throw new RpcException("RuntimeError", $"ExternalLocation redirect limit exceeded (max_redirects={config.MaxRedirects}) fetching {RedactUrl(url)}");
            }

            if (urlValidator is not null)
            {
                try
                {
                    urlValidator(current);
                }
                catch (Exception exc) when (exc is ArgumentException or FormatException)
                {
                    throw new RpcException("ValueError", $"ExternalLocation URL rejected: {exc.Message}");
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(config.Timeout);
            HttpResponseMessage response;
            try
            {
                response = await s_client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
            {
                throw new RpcException("RuntimeError", $"ExternalLocation fetch failed for {RedactUrl(current)}: {exc.GetType().Name}");
            }

            using (response)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
                    or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location
                        ?? throw new RpcException("RuntimeError", $"ExternalLocation redirect from {RedactUrl(current)} carried no Location header");
                    current = new Uri(new Uri(current), location).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new RpcException("RuntimeError", $"ExternalLocation fetch failed with status {(int)response.StatusCode} for {RedactUrl(current)}");
                }

                if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > config.MaxFetchBytes)
                {
                    throw new RpcException("RuntimeError", $"ExternalLocation fetch exceeded max_fetch_bytes ({declaredLength} > {config.MaxFetchBytes}) for {RedactUrl(current)}");
                }

                var encoded = await ReadBoundedAsync(response.Content, config.MaxFetchBytes, "max_fetch_bytes", current, cancellationToken).ConfigureAwait(false);

                var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
                if (string.IsNullOrEmpty(contentEncoding))
                {
                    if (encoded.Length > maxDecompressed)
                    {
                        throw new RpcException("RuntimeError", $"ExternalLocation body exceeded max_decompressed_bytes ({encoded.Length} > {maxDecompressed}) for {RedactUrl(current)}");
                    }

                    return encoded;
                }

                byte[] decoded;
                try
                {
                    decoded = ExternalCompression.Decompress(contentEncoding, encoded);
                }
                catch (Exception exc)
                {
                    throw new RpcException("RuntimeError", $"ExternalLocation failed to decode content-encoding '{contentEncoding}' from {RedactUrl(current)}: {exc.GetType().Name}");
                }

                if (decoded.Length > maxDecompressed)
                {
                    throw new RpcException("RuntimeError", $"ExternalLocation decoded body exceeded max_decompressed_bytes ({decoded.Length} > {maxDecompressed}) for {RedactUrl(current)}");
                }

                return decoded;
            }
        }
    }

    /// <summary>Streams <paramref name="content"/> into memory, aborting as soon as the running
    /// total exceeds <paramref name="capBytes"/> — a missing/understated <c>Content-Length</c>
    /// must not let an oversized body be read to completion before the cap is enforced.</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long capBytes, string capName, string urlForError, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > capBytes)
            {
                throw new RpcException("RuntimeError", $"ExternalLocation fetch exceeded {capName} ({buffer.Length} > {capBytes}) for {RedactUrl(urlForError)}");
            }
        }

        return buffer.ToArray();
    }
}

/// <summary>Configuration for fetching ExternalLocation URLs. See
/// <see cref="ExternalFetch"/>'s class doc comment for what this port simplified relative to
/// Python's parallel range-request fetcher.</summary>
public sealed class FetchConfig
{
    /// <summary>Hard cap on encoded/on-wire download size.</summary>
    public long MaxFetchBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>Hard cap after content decoding. <see langword="null"/> means
    /// <c>16 * MaxFetchBytes</c> (matching Python's default).</summary>
    public long? MaxDecompressedBytes { get; init; }

    /// <summary>Maximum redirects followed. Every target is validated before it is requested.
    /// <c>0</c> rejects any redirect.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Overall deadline for one fetch (one hop's request/response cycle; the redirect
    /// loop as a whole is bounded by <see cref="MaxRedirects"/>, not by a single shared deadline —
    /// a documented simplification versus Python's single end-to-end timeout).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}
