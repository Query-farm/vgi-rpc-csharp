using QueryFarm.VgiRpc.Errors;

namespace QueryFarm.VgiRpc.External;

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
    // Redirects are followed manually so each hop can be validated; Content-Encoding is
    // informational metadata here, not a negotiated transport encoding (decompressed manually
    // against ExternalCompression). Connect timeout 30 s and no total deadline: each attempt is
    // bounded by FetchConfig.Timeout up to the response headers and by a stall watchdog
    // (FetchConfig.StallTimeout) over the body, so a large object on a slow link is never cut off
    // while bytes are still arriving.
    private static readonly HttpClient s_client = StorageTransport.NewClient(StorageTimeouts.DefaultConnect);

    private const int Attempts = 3;
    private static readonly TimeSpan s_backoff = TimeSpan.FromMilliseconds(250);

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

            // Retry connect errors, resets, stalls (also mid-body), 429 and 5xx: up to Attempts
            // tries with jittered exponential backoff. A body that keeps arriving is read to the
            // end however slowly it comes.
            HttpResponseMessage? response = null;
            StallWatchdog? watchdog = null;
            byte[]? encoded = null;
            var lastError = "";
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(StorageTransport.Backoff(s_backoff, attempt), cancellationToken).ConfigureAwait(false);
                }

                response?.Dispose();
                response = null;
                watchdog?.Dispose();
                watchdog = new StallWatchdog(config.StallTimeout, StorageTimeouts.DefaultConnect, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                try
                {
                    using (var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token))
                    {
                        headerDeadline.CancelAfter(config.Timeout);
                        response = await s_client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerDeadline.Token).ConfigureAwait(false);
                    }

                    watchdog.Progress();
                    var code = (int)response.StatusCode;
                    if (code == 429 || code >= 500)
                    {
                        lastError = $"HTTP {code}";
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > config.MaxFetchBytes)
                    {
                        throw new RpcException("RuntimeError", $"ExternalLocation fetch exceeded max_fetch_bytes ({declaredLength} > {config.MaxFetchBytes}) for {RedactUrl(current)}");
                    }

                    encoded = await ReadBoundedAsync(response.Content, config.MaxFetchBytes, "max_fetch_bytes", current, watchdog).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    response?.Dispose();
                    watchdog.Dispose();
                    throw;
                }
                catch (RpcException)
                {
                    response?.Dispose();
                    watchdog.Dispose();
                    throw;
                }
                catch (Exception exc) when (exc is HttpRequestException or OperationCanceledException or IOException)
                {
                    lastError = watchdog.Stalled ? "stalled" : exc.GetType().Name;
                    response?.Dispose();
                    response = null;
                }
            }

            watchdog?.Dispose();
            if (response is null || (encoded is null && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)))
            {
                response?.Dispose();
                throw new RpcException("RuntimeError", $"ExternalLocation fetch failed for {RedactUrl(current)} after {Attempts} attempts: {lastError}");
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

                if (encoded is null)
                {
                    throw new RpcException("RuntimeError", $"ExternalLocation fetch failed for {RedactUrl(current)}: {lastError}");
                }

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
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long capBytes, string capName, string urlForError, StallWatchdog watchdog)
    {
        await using var stream = await content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, watchdog.Token).ConfigureAwait(false)) > 0)
        {
            watchdog.Progress();
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
    /// <summary>The default <see cref="MaxFetchBytes"/>: 256 MiB.</summary>
    public const long DefaultMaxFetchBytes = 256L * 1024 * 1024;

    private readonly long? _maxFetchBytes;

    /// <summary>Hard cap on encoded/on-wire download size (default <see cref="DefaultMaxFetchBytes"/>).</summary>
    public long MaxFetchBytes
    {
        get => _maxFetchBytes ?? DefaultMaxFetchBytes;
        init => _maxFetchBytes = value;
    }

    /// <summary>Whether <see cref="MaxFetchBytes"/> was set rather than defaulted: an explicit fetch
    /// cap below the advertised upload cap refuses startup, a defaulted one is raised to it (fix 1,
    /// see <c>MapVgiRpc</c>).</summary>
    public bool MaxFetchBytesIsExplicit => _maxFetchBytes is not null;

    /// <summary>A copy with <see cref="MaxFetchBytes"/> replaced (and so explicit).</summary>
    public FetchConfig WithMaxFetchBytes(long maxFetchBytes) => new()
    {
        MaxFetchBytes = maxFetchBytes,
        MaxDecompressedBytes = MaxDecompressedBytes,
        MaxRedirects = MaxRedirects,
        Timeout = Timeout,
        StallTimeout = StallTimeout,
    };

    /// <summary>Hard cap after content decoding. <see langword="null"/> means
    /// <c>16 * MaxFetchBytes</c> (matching Python's default).</summary>
    public long? MaxDecompressedBytes { get; init; }

    /// <summary>Maximum redirects followed. Every target is validated before it is requested.
    /// <c>0</c> rejects any redirect.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Deadline for one attempt's response headers (connect, send, and the server's time to
    /// first byte). The body has no total deadline: it is bounded by <see cref="StallTimeout"/>, so a
    /// large object on a slow link is read to the end while bytes keep arriving. Default 60 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the response body may deliver no bytes before the fetch fails (and is
    /// retried, up to 3 attempts). Default 30 s, or <c>VGI_RPC_STORAGE_STALL_TIMEOUT_SECONDS</c>.</summary>
    public TimeSpan StallTimeout { get; init; } = StorageTimeouts.StallFromEnvironment();
}
