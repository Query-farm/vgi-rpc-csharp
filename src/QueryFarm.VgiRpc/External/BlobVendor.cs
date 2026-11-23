using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apache.Arrow;

namespace QueryFarm.VgiRpc.External;

// Blob-vendor client: region-pinned, single-object pre-signed URLs. Ported from vgi-rpc-go's
// vgirpc/vendor.go (itself mirroring vgi-rpc-python's vgi_rpc.vendor); the wire format is the
// vendor's (vgi docs/testing/blob-test-catalog.md, "Vendor HTTP API").
//
// The blob vendor is a small HTTP service that holds the object-storage credentials so workers
// never do. A worker asks it for a pre-signed URL for one object of a declared size and SHA-256,
// uploads straight to storage with that URL, and hands a download URL (or an object id) to whoever
// needs the bytes. Storage enforces the signed Content-Length and x-amz-checksum-sha256, so whatever
// sits at the object's key is exactly the declared bytes.
//
// Every vendor request carries "Authorization: Bearer <token>". Signed-URL requests go through a
// separate bare client and carry exactly the headers the grant names: never the token. Neither the
// token nor a signed query string is ever logged or put in an error: diagnostics name a URL by
// scheme, host, port and path only (ExternalFetch.RedactUrl).

/// <summary>A blob vendor object's purpose: <see cref="Rpc"/> objects get a download URL at put time
/// and a short lifecycle; <see cref="Fs"/> objects are fetched with a fresh URL from get.</summary>
public enum BlobPurpose
{
    /// <summary>Results, client uploads and stored protocol objects.</summary>
    Rpc,

    /// <summary>Worker function-storage values.</summary>
    Fs,
}

/// <summary>A failed blob-vendor or signed-URL request. Its message never contains the bearer token
/// or a signed query string.</summary>
public sealed class BlobVendorException : Exception
{
    /// <summary>The vendor's <c>size_mismatch</c> code set by <see cref="VendorBlobClient.GetBytesAsync"/>.</summary>
    public const string SizeMismatchCode = "size_mismatch";

    /// <summary>The <c>sha256_mismatch</c> code set by <see cref="VendorBlobClient.GetBytesAsync"/>.</summary>
    public const string Sha256MismatchCode = "sha256_mismatch";

    /// <summary>Creates the exception.</summary>
    public BlobVendorException(string message, int status = 0, string code = "")
        : base(message)
    {
        Status = status;
        Code = code;
    }

    /// <summary>The HTTP status of the failing response; 0 for a transport failure or a malformed response.</summary>
    public int Status { get; }

    /// <summary>The vendor's machine-readable code (<c>not_found</c>, <c>not_uploaded</c>,
    /// <c>quota_exceeded</c>, <c>idempotency_conflict</c>), the storage's XML code
    /// (<c>BadDigest</c>, <c>NoSuchKey</c>), <see cref="SizeMismatchCode"/> /
    /// <see cref="Sha256MismatchCode"/>, or empty.</summary>
    public string Code { get; }
}

/// <summary>The execution a blob belongs to, for scoped clean-up (1-128 characters of
/// <c>[A-Za-z0-9._:-]</c> in each field).</summary>
public sealed record BlobScope(string Shard, string ExecutionId);

/// <summary>One vendored blob: small enough to store inline.</summary>
/// <param name="ObjectId">The vendor object id.</param>
/// <param name="Size">The exact byte length.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of the bytes.</param>
public sealed record BlobRef(string ObjectId, long Size, string Sha256);

/// <summary>The vendor's <c>/v1/info</c>: the storage hosts of the tenant's region.</summary>
public sealed record VendorInfo(
    string Region,
    string Tenant,
    string Jurisdiction,
    IReadOnlyList<string> UploadHosts,
    IReadOnlyList<string> DownloadHosts,
    IReadOnlyList<string> UrlPrefixes);

/// <summary>Tunes a vendor client. The defaults suit production.</summary>
public sealed class VendorOptions
{
    /// <summary>Per-request timeout. Default 30 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts per request; transport errors, 429 and 5xx are retried with exponential
    /// backoff. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Base delay between attempts. Default 250 ms.</summary>
    public TimeSpan Backoff { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the <c>/v1/info</c> host pin is cached. Default 10 min.</summary>
    public TimeSpan InfoTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Caps a blob <see cref="VendorBlobClient.GetBytesAsync"/> downloads. Default 4 GiB.</summary>
    public long MaxFetchBytes { get; init; } = 4L << 30;

    /// <summary>Handler for vendor API requests (tests); signed-URL requests always use a bare one.</summary>
    public HttpMessageHandler? VendorHandler { get; init; }
}

/// <summary>The environment the blob vendor's <c>*FromEnvironment</c> helpers read.</summary>
public static class BlobVendorEnvironment
{
    /// <summary>The vendor's base URL; unset means no vendor.</summary>
    public const string Url = "VGI_BLOB_VENDOR_URL";

    /// <summary>The vendor's bearer token.</summary>
    public const string Token = "VGI_BLOB_VENDOR_TOKEN";

    /// <summary>Consulted when <see cref="Token"/> is unset.</summary>
    public const string TokenFallback = "VGI_CF_DO_TOKEN";

    /// <summary>Result-batch size at or above which the server externalizes (default 1 MiB).</summary>
    public const string ExternalizeThreshold = "VGI_RPC_EXTERNALIZE_THRESHOLD_BYTES";

    /// <summary><c>none</c> (default) or <c>zstd</c>, optionally with a level (<c>zstd:3</c>).</summary>
    public const string ExternalCompression = "VGI_RPC_EXTERNAL_COMPRESSION";

    /// <summary>The largest client upload the server vends a URL for (<c>VGI-Max-Upload-Bytes</c>);
    /// unset advertises the inbound fetch cap.</summary>
    public const string MaxUploadBytes = "VGI_RPC_MAX_UPLOAD_BYTES";

    /// <summary>The inbound fetch cap. Unset: 256 MiB raised to <see cref="MaxUploadBytes"/>; both
    /// set with the upload size above it is a startup error.</summary>
    public const string MaxFetchBytes = "VGI_RPC_MAX_FETCH_BYTES";

    /// <summary>The inline request cap a server advertises and enforces.</summary>
    public const string MaxRequestBytes = "VGI_RPC_MAX_REQUEST_BYTES";

    internal const long DefaultExternalizeThreshold = 1_048_576;

    /// <summary>A positive integer variable, or <see langword="null"/> when unset.</summary>
    /// <exception cref="ArgumentException">Set but not a positive integer.</exception>
    public static long? PositiveInt(string name, Func<string, string?>? environment = null)
    {
        var raw = (environment ?? Environment.GetEnvironmentVariable)(name)?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new ArgumentException($"{name} must be a positive integer, got '{raw}'");
        }

        return value;
    }

    /// <summary><see cref="MaxFetchBytes"/> as described there.</summary>
    /// <exception cref="ArgumentException">A malformed value, or an upload cap above an explicit
    /// fetch cap.</exception>
    public static long MaxFetchBytesFrom(Func<string, string?>? environment = null)
    {
        var fetch = PositiveInt(MaxFetchBytes, environment);
        var upload = PositiveInt(MaxUploadBytes, environment);
        if (fetch is null)
        {
            return Math.Max(FetchConfig.DefaultMaxFetchBytes, upload ?? 0);
        }

        return upload > fetch
            ? throw new ArgumentException(
                $"{MaxUploadBytes} ({upload}) exceeds {MaxFetchBytes} ({fetch}): clients would upload requests the server " +
                $"then refuses to fetch. Raise {MaxFetchBytes} or lower {MaxUploadBytes}")
            : fetch.Value;
    }

    /// <summary>Parses <see cref="ExternalCompression"/>.</summary>
    public static Compression? CompressionFrom(Func<string, string?>? environment = null)
    {
        var raw = (environment ?? Environment.GetEnvironmentVariable)(ExternalCompression) ?? "";
        var text = raw.Trim().ToLowerInvariant();
        if (text is "" or "none" or "off" or "identity")
        {
            return null;
        }

        var colon = text.IndexOf(':');
        var name = colon < 0 ? text : text[..colon];
        if (name != "zstd")
        {
            throw new ArgumentException($"{ExternalCompression} must be none or zstd (optionally ':<level>'), got '{raw}'");
        }

        if (colon < 0)
        {
            return new Compression("zstd");
        }

        return int.TryParse(text[(colon + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var level)
            ? new Compression("zstd", level)
            : throw new ArgumentException($"{ExternalCompression} level must be an integer, got '{raw}'");
    }

    /// <summary>The vendor URL and token, or <see langword="null"/> when <see cref="Url"/> is unset.</summary>
    /// <exception cref="ArgumentException">The URL is set without a token.</exception>
    public static (string Url, string Token)? UrlAndToken(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var url = env(Url)?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var token = env(Token)?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            token = env(TokenFallback)?.Trim();
        }

        return string.IsNullOrEmpty(token)
            ? throw new ArgumentException($"{Url} is set but neither {Token} nor {TokenFallback} is")
            : (url, token);
    }

    /// <summary>
    /// The server external-location config from the blob-vendor environment, or
    /// <see langword="null"/> when <see cref="Url"/> is unset: it uploads results through a
    /// <see cref="VendorStorage"/> (which is also the upload-URL provider and object store), pins
    /// inbound pointer URLs to the vendor's hosts, and reads <see cref="ExternalizeThreshold"/>,
    /// <see cref="ExternalCompression"/> and <see cref="MaxFetchBytes"/>.
    /// </summary>
    public static ServerExternalConfig? ServerConfigFrom(Func<string, string?>? environment = null)
    {
        if (UrlAndToken(environment) is not var (url, token))
        {
            return null;
        }

        var storage = new VendorStorage(url, token);
        // Either cap set is the operator's statement of the fetch cap (MaxFetchBytesFrom validates
        // the pair); neither leaves the default, which MapVgiRpc may raise to the upload cap.
        var fetch = PositiveInt(MaxFetchBytes, environment) is not null || PositiveInt(MaxUploadBytes, environment) is not null
            ? new FetchConfig().WithMaxFetchBytes(MaxFetchBytesFrom(environment))
            : new FetchConfig();

        return new ServerExternalConfig
        {
            Storage = storage,
            ExternalizeThresholdBytes = PositiveInt(ExternalizeThreshold, environment) ?? DefaultExternalizeThreshold,
            Compression = CompressionFrom(environment),
            FetchConfig = fetch,
            UrlValidator = storage.ValidateUrl,
        };
    }
}

/// <summary>Builds URL validators that accept only URLs on pinned storage hosts.</summary>
public static class PinnedHosts
{
    /// <summary>
    /// A validator that accepts only URLs whose scheme is https (or http with
    /// <paramref name="allowHttp"/>), with no userinfo, on exactly one of <paramref name="hosts"/>
    /// (<c>host</c> or <c>host:port</c>; an entry without a port matches the scheme's default port
    /// only; subdomains do not match). With <paramref name="prefixes"/>, the URL without its query
    /// must also start with one of them and its path may not contain a <c>..</c> segment.
    /// <paramref name="allowHttp"/> is only for loopback test vendors.
    /// </summary>
    public static Action<string> Validator(IEnumerable<string> hosts, bool allowHttp, IEnumerable<string>? prefixes = null)
    {
        string[] schemes = allowHttp ? ["https", "http"] : ["https"];
        var allowed = new HashSet<(string Host, int Port)>();
        foreach (var entry in hosts)
        {
            var (host, port) = ParseHostEntry(entry);
            if (port is null)
            {
                foreach (var scheme in schemes)
                {
                    allowed.Add((host, DefaultPort(scheme)));
                }
            }
            else
            {
                allowed.Add((host, port.Value));
            }
        }

        if (allowed.Count == 0)
        {
            throw new ArgumentException("a pinned-hosts validator needs at least one host");
        }

        var urlPrefixes = (prefixes ?? []).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        return raw =>
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var url))
            {
                throw new ArgumentException("URL is malformed");
            }

            if (!schemes.Contains(url.Scheme, StringComparer.Ordinal))
            {
                throw new ArgumentException($"URL scheme '{url.Scheme}' not allowed (only {string.Join(" or ", schemes)})");
            }

            if (url.UserInfo.Length != 0)
            {
                throw new ArgumentException("URL userinfo is not allowed");
            }

            if (!allowed.Contains((url.Host.Trim('[', ']').ToLowerInvariant(), url.Port)))
            {
                throw new ArgumentException($"URL host is not a pinned storage host: {ExternalFetch.RedactUrl(raw)}");
            }

            if (urlPrefixes.Length > 0)
            {
                var path = url.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
                var bare = $"{url.Scheme}://{url.Authority}/{path}";
                var rawPath = RawPath(raw);
                if (rawPath.Split('/').Contains("..") || !urlPrefixes.Any(p => bare.StartsWith(p, StringComparison.Ordinal)))
                {
                    throw new ArgumentException($"URL is outside the pinned storage prefixes: {ExternalFetch.RedactUrl(raw)}");
                }
            }
        };
    }

    // System.Uri collapses "..", so a dot segment is looked for in the URL as written.
    private static string RawPath(string raw)
    {
        var start = raw.IndexOf("://", StringComparison.Ordinal);
        var rest = start < 0 ? raw : raw[(start + 3)..];
        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            return "";
        }

        var path = rest[slash..];
        var end = path.IndexOfAny(['?', '#']);
        return end < 0 ? path : path[..end];
    }

    private static int DefaultPort(string scheme) => scheme == "https" ? 443 : 80;

    /// <summary><c>host</c>, <c>host:port</c> or <c>[v6]:port</c>; anything else is refused.</summary>
    private static (string Host, int? Port) ParseHostEntry(string entry)
    {
        var text = entry.Trim();
        string host;
        var portText = "";
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0 || (close + 1 < text.Length && text[close + 1] != ':'))
            {
                throw Invalid(entry);
            }

            host = text[1..close];
            portText = close + 1 < text.Length ? text[(close + 2)..] : "";
            if (close + 1 < text.Length && portText.Length == 0)
            {
                throw Invalid(entry);
            }
        }
        else
        {
            var colon = text.IndexOf(':');
            if (colon >= 0 && text.IndexOf(':', colon + 1) >= 0)
            {
                throw Invalid(entry);
            }

            host = colon < 0 ? text : text[..colon];
            portText = colon < 0 ? "" : text[(colon + 1)..];
            if (colon >= 0 && portText.Length == 0)
            {
                throw Invalid(entry);
            }
        }

        if (host.Length == 0 || host.IndexOfAny(['/', '?', '#', '@', ' ', '\\']) >= 0)
        {
            throw Invalid(entry);
        }

        if (portText.Length == 0)
        {
            return (host.ToLowerInvariant(), null);
        }

        return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? (host.ToLowerInvariant(), port)
            : throw Invalid(entry);
    }

    private static ArgumentException Invalid(string entry) => new($"invalid pinned host entry: '{entry}'");
}

/// <summary>The vendor HTTP API: the one place that knows its JSON shapes.</summary>
internal sealed class VendorApi : IDisposable
{
    private const int MaxResponseBytes = 16 << 20;
    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromSeconds(5);

    private readonly string _baseUrl;
    private readonly string _token;
    private readonly VendorOptions _options;
    private readonly HttpClient _vendor;
    private readonly HttpClient _storage;

    public VendorApi(string url, string token, VendorOptions? options)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http") || parsed.Host.Length == 0)
        {
            throw new ArgumentException($"blob vendor URL must be an absolute https URL: {ExternalFetch.RedactUrl(url)}");
        }

        LoopbackHttp = parsed.Scheme == "http";
        if (LoopbackHttp && !parsed.IsLoopback)
        {
            // The bearer token would cross the network in plain text.
            throw new ArgumentException($"blob vendor URL must use https (http is allowed only for loopback): {ExternalFetch.RedactUrl(url)}");
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("blob vendor token is empty");
        }

        _baseUrl = url.TrimEnd('/');
        _token = token;
        _options = options ?? new VendorOptions();
        // Never follow a redirect: one could carry the token or a signed URL to an unvetted host.
        _vendor = new HttpClient(_options.VendorHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: _options.VendorHandler is null)
        {
            Timeout = _options.Timeout,
        };
        _storage = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = _options.Timeout };
    }

    public bool LoopbackHttp { get; }

    public int Attempts => Math.Max(1, _options.MaxAttempts);

    public void Dispose()
    {
        _vendor.Dispose();
        _storage.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> build, string url, string op, bool storage, CancellationToken cancellationToken)
    {
        var client = storage ? _storage : _vendor;
        var lastError = "";
        var status = 0;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0)
            {
                var delay = TimeSpan.FromTicks(Math.Min(_options.Backoff.Ticks * (1L << (attempt - 1)), s_maxBackoff.Ticks));
                delay = TimeSpan.FromTicks((long)(delay.Ticks * (0.5 + (Random.Shared.NextDouble() / 2))));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            HttpResponseMessage response;
            try
            {
                using var request = build();
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new BlobVendorException($"blob vendor {op} cancelled: {ExternalFetch.RedactUrl(url)}");
            }
            catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or IOException)
            {
                // Never exc.Message: it can embed the full (signed) URL.
                status = 0;
                lastError = exc is TaskCanceledException ? "timeout" : "transport error";
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                status = (int)response.StatusCode;
                lastError = $"HTTP {status}";
                response.Dispose();
                continue;
            }

            return response;
        }

        throw new BlobVendorException(
            $"blob vendor {op} failed after {Attempts} attempts ({lastError}): {ExternalFetch.RedactUrl(url)}", status);
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, long max, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new BlobVendorException($"response exceeds the {max}-byte cap");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, string op, object? payload, CancellationToken cancellationToken)
    {
        var url = _baseUrl + path;
        var body = payload is null ? null : JsonSerializer.SerializeToUtf8Bytes(payload);
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body is not null)
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }

                return request;
            },
            url, op, storage: false, cancellationToken).ConfigureAwait(false);
        byte[] data;
        try
        {
            data = await ReadCappedAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException)
        {
            throw new BlobVendorException($"blob vendor {op}: reading the response failed", (int)response.StatusCode);
        }

        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300)
        {
            var (code, detail) = ErrorDetail(data);
            throw new BlobVendorException($"blob vendor {op} failed: HTTP {status}{detail}", status, code);
        }

        if (data.Length == 0)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(data).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new BlobVendorException($"blob vendor {op} returned non-JSON", status);
        }

        return root.ValueKind == JsonValueKind.Object
            ? root
            : throw new BlobVendorException($"blob vendor {op} returned a non-object JSON body", status);
    }

    private static (string Code, string Detail) ErrorDetail(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ("", "");
            }

            var code = Truncate(OptString(doc.RootElement, "error"), 64);
            var message = Truncate(OptString(doc.RootElement, "message"), 200);
            var parts = new[] { code, message }.Where(p => p.Length > 0).ToArray();
            return (code, parts.Length == 0 ? "" : ": " + string.Join(": ", parts));
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    private static string OptString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static string S3ErrorCode(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var start = text.IndexOf("<Code>", StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        var end = text.IndexOf("</Code>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return "";
        }

        var code = text[(start + 6)..end];
        return code.Length is > 0 and <= 64 && code.All(char.IsAsciiLetterOrDigit) ? code : "";
    }

    private static BlobVendorException Malformed(string op, string key) => new($"blob vendor {op} response has a malformed '{key}'");

    private static string Str(JsonElement obj, string key, string op)
    {
        if (!obj.TryGetProperty(key, out var v))
        {
            throw new BlobVendorException($"blob vendor {op} response is missing '{key}'");
        }

        return v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : throw Malformed(op, key);
    }

    private static string OptStr(JsonElement obj, string key, string op)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return "";
        }

        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : throw Malformed(op, key);
    }

    private static long Int(JsonElement obj, string key, string op)
    {
        if (!obj.TryGetProperty(key, out var v))
        {
            throw new BlobVendorException($"blob vendor {op} response is missing '{key}'");
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : throw Malformed(op, key);
    }

    private static DateTimeOffset Time(JsonElement obj, string key, string op)
    {
        if (!obj.TryGetProperty(key, out var v))
        {
            throw new BlobVendorException($"blob vendor {op} response is missing '{key}'");
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var seconds))
        {
            if (seconds > 1e11)
            {
                seconds /= 1000;
            }

            return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        }

        if (v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        throw new BlobVendorException("blob vendor returned a malformed expires_at");
    }

    private static List<string> StrList(JsonElement obj, string key, string op)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (v.ValueKind != JsonValueKind.Array)
        {
            throw Malformed(op, key);
        }

        return v.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s ? s : throw Malformed(op, key))
            .ToList();
    }

    private static int Deleted(JsonElement obj) =>
        obj.TryGetProperty("deleted", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    internal sealed record PutGrant(
        string ObjectId,
        string UploadUrl,
        IReadOnlyDictionary<string, string> RequiredHeaders,
        string DownloadUrl,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? DownloadExpiresAt);

    internal sealed record BlobLocation(string DownloadUrl, long Size, string Sha256B64, string ContentEncoding);

    public async Task<PutGrant> PutAsync(
        long size, string sha256B64, BlobPurpose purpose, BlobScope? scope, string? contentEncoding,
        string? idempotencyKey, string? origin, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["size"] = size,
            ["sha256_b64"] = sha256B64,
            ["purpose"] = purpose == BlobPurpose.Rpc ? "rpc" : "fs",
        };
        if (scope is not null)
        {
            body["scope"] = new Dictionary<string, string> { ["shard"] = scope.Shard, ["execution_id"] = scope.ExecutionId };
        }

        if (!string.IsNullOrEmpty(contentEncoding))
        {
            body["content_encoding"] = contentEncoding;
        }

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            body["idempotency_key"] = idempotencyKey;
        }

        if (!string.IsNullOrEmpty(origin))
        {
            body["origin"] = origin;
        }

        var resp = await CallAsync(HttpMethod.Post, "/v1/blobs/put", "put", body, cancellationToken).ConfigureAwait(false);
        if (resp.TryGetProperty("upload_method", out var method) && !(method.ValueKind == JsonValueKind.String && method.GetString() == "PUT"))
        {
            throw new BlobVendorException("blob vendor put asked for an unsupported upload_method");
        }

        if (!resp.TryGetProperty("required_headers", out var rawHeaders) || rawHeaders.ValueKind != JsonValueKind.Object)
        {
            throw new BlobVendorException("blob vendor put response has malformed 'required_headers'");
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in rawHeaders.EnumerateObject())
        {
            headers[header.Name] = header.Value.ValueKind == JsonValueKind.String
                ? header.Value.GetString()!
                : throw new BlobVendorException("blob vendor put response has malformed 'required_headers'");
        }

        DateTimeOffset? downloadExpires = resp.TryGetProperty("download_expires_at", out var de) && de.ValueKind != JsonValueKind.Null
            ? Time(resp, "download_expires_at", "put")
            : null;
        return new PutGrant(
            Str(resp, "object_id", "put"), Str(resp, "upload_url", "put"), headers, OptStr(resp, "download_url", "put"),
            Time(resp, "expires_at", "put"), downloadExpires);
    }

    public async Task<BlobLocation> GetAsync(string objectId, CancellationToken cancellationToken)
    {
        var resp = await CallAsync(HttpMethod.Post, "/v1/blobs/get", "get", new Dictionary<string, string> { ["object_id"] = objectId }, cancellationToken)
            .ConfigureAwait(false);
        _ = Time(resp, "expires_at", "get");
        return new BlobLocation(Str(resp, "download_url", "get"), Int(resp, "size", "get"), Str(resp, "sha256_b64", "get"), OptStr(resp, "content_encoding", "get"));
    }

    public async Task<int> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        Deleted(await CallAsync(HttpMethod.Post, "/v1/blobs/delete", "delete", new Dictionary<string, object> { ["object_ids"] = ids }, cancellationToken).ConfigureAwait(false));

    public async Task<int> DeleteScopeAsync(BlobScope scope, CancellationToken cancellationToken) =>
        Deleted(await CallAsync(
            HttpMethod.Post, "/v1/blobs/delete_scope", "delete_scope",
            new Dictionary<string, string> { ["shard"] = scope.Shard, ["execution_id"] = scope.ExecutionId }, cancellationToken).ConfigureAwait(false));

    public async Task<VendorInfo> InfoAsync(CancellationToken cancellationToken)
    {
        var resp = await CallAsync(HttpMethod.Get, "/v1/info", "info", null, cancellationToken).ConfigureAwait(false);
        return new VendorInfo(
            OptString(resp, "region"), OptString(resp, "tenant"), OptStr(resp, "jurisdiction", "info"),
            StrList(resp, "upload_hosts", "info"), StrList(resp, "download_hosts", "info"), StrList(resp, "url_prefixes", "info"));
    }

    /// <summary>PUTs <paramref name="data"/> to a granted URL with exactly its required headers, on
    /// the bare storage client.</summary>
    public async Task PutObjectAsync(PutGrant grant, byte[] data, string? contentEncoding, CancellationToken cancellationToken)
    {
        var hasEncoding = false;
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in grant.RequiredHeaders)
        {
            switch (name.ToLowerInvariant())
            {
                case "content-length":
                    if (value != data.Length.ToString(CultureInfo.InvariantCulture))
                    {
                        throw new BlobVendorException($"blob vendor granted Content-Length {value} for a {data.Length}-byte object");
                    }

                    continue;
                case "content-encoding":
                    hasEncoding = true;
                    break;
            }

            headers.Add(new(name, value));
        }

        if (!string.IsNullOrEmpty(contentEncoding) && !hasEncoding)
        {
            headers.Add(new("Content-Encoding", contentEncoding));
        }

        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, grant.UploadUrl) { Content = new ByteArrayContent(data) };
                foreach (var (name, value) in headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                    {
                        request.Content.Headers.TryAddWithoutValidation(name, value);
                    }
                }

                return request;
            },
            grant.UploadUrl, "upload", storage: true, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadCappedAsync(response, 1 << 16, cancellationToken).ConfigureAwait(false);
            var code = S3ErrorCode(body);
            throw new BlobVendorException(
                $"signed upload failed: HTTP {(int)response.StatusCode}{(code.Length > 0 ? " " + code : "")}: {ExternalFetch.RedactUrl(grant.UploadUrl)}",
                (int)response.StatusCode, code);
        }
    }

    /// <summary>GETs a signed URL on the bare storage client, pinned and capped.</summary>
    public async Task<byte[]> DownloadAsync(string url, Action<string> validate, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            validate(url);
        }
        catch (ArgumentException exc)
        {
            throw new BlobVendorException($"download rejected: {exc.Message}");
        }

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), url, "download", storage: true, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var body = await ReadCappedAsync(response, 1 << 16, cancellationToken).ConfigureAwait(false);
            throw new BlobVendorException(
                $"download failed: HTTP {(int)response.StatusCode}: {ExternalFetch.RedactUrl(url)}", (int)response.StatusCode, S3ErrorCode(body));
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new BlobVendorException($"download of {response.Content.Headers.ContentLength} bytes exceeds the {maxBytes}-byte cap");
        }

        try
        {
            return await ReadCappedAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException)
        {
            throw new BlobVendorException($"download failed while reading: {ExternalFetch.RedactUrl(url)}");
        }
    }
}

/// <summary>Shared base: the API and the <c>/v1/info</c> host pin (cached; a failed refresh keeps the
/// last good list).</summary>
public abstract class VendorClientBase : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _ttl;
    private Action<string>? _validator;
    private DateTimeOffset _fetchedAt;

    private protected VendorClientBase(string url, string token, VendorOptions? options)
    {
        Options = options ?? new VendorOptions();
        Api = new VendorApi(url, token, Options);
        _ttl = Options.InfoTtl;
    }

    private protected VendorApi Api { get; }

    private protected VendorOptions Options { get; }

    /// <summary>Accepts only URLs on the vendor's storage hosts (for
    /// <see cref="ServerExternalConfig.UrlValidator"/>); throws <see cref="ArgumentException"/>.</summary>
    public void ValidateUrl(string url) => CurrentValidator()(url);

    /// <summary>The vendor's region and storage hosts (uncached).</summary>
    public Task<VendorInfo> InfoAsync(CancellationToken cancellationToken = default) => Api.InfoAsync(cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        Api.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private Action<string> CurrentValidator()
    {
        _gate.Wait();
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_validator is not null && now - _fetchedAt < _ttl)
            {
                return _validator;
            }

            try
            {
                var info = Api.InfoAsync(CancellationToken.None).GetAwaiter().GetResult();
                _validator = PinnedHosts.Validator([.. info.UploadHosts, .. info.DownloadHosts], Api.LoopbackHttp, info.UrlPrefixes);
            }
            catch (Exception exc) when (exc is BlobVendorException or ArgumentException)
            {
                if (_validator is null)
                {
                    throw new ArgumentException($"cannot pin storage hosts: {exc.Message}");
                }

                // Keep the last known-good pin through a vendor blip.
            }

            _fetchedAt = now;
            return _validator;
        }
        finally
        {
            _gate.Release();
        }
    }

    private protected void Check(string url)
    {
        try
        {
            ValidateUrl(url);
        }
        catch (ArgumentException exc)
        {
            throw new BlobVendorException($"blob vendor returned a URL off its pinned hosts: {exc.Message}");
        }
    }

    private protected CancellationTokenSource RequestTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Api.Attempts * (Options.Timeout + TimeSpan.FromSeconds(5)));
        return cts;
    }
}

/// <summary>
/// <see cref="IExternalStorage"/>, <see cref="IUploadUrlProvider"/> and <see cref="IObjectStore"/>
/// backed by the blob vendor.
/// </summary>
/// <remarks>
/// <para>Upload (server-side externalization) asks the vendor for a sized, checksummed grant, PUTs
/// the bytes with exactly the grant's required headers, and returns the grant's download URL.
/// <see cref="GenerateUploadUrlAsync"/> (client uploads) asks the same sized endpoint (purpose rpc,
/// advisory origin <see cref="ClientUploadOrigin"/>) for a PUT signed for the client's exact size
/// and SHA-256. Vended URLs are checked against the vendor's pinned hosts before use.</para>
/// <para>Thread-safe. Objects are cleaned up by the vendor's bucket lifecycle; vgi-rpc never
/// deletes them.</para>
/// </remarks>
public sealed class VendorStorage : VendorClientBase, IExternalStorage, IUploadUrlProvider, IObjectStore
{
    /// <summary>The advisory origin on grants minted for a client upload (<c>__upload_url__</c>).</summary>
    public const string ClientUploadOrigin = "client_upload";

    /// <summary>The advisory origin on grants for a stored <c>StateRef</c> object.</summary>
    public const string StateRefOrigin = "state_ref";

    private readonly BlobPurpose _purpose;

    /// <summary>Creates a vendor-backed storage. <paramref name="url"/> must be https (http only
    /// for loopback).</summary>
    public VendorStorage(string url, string token, BlobPurpose purpose = BlobPurpose.Rpc, VendorOptions? options = null)
        : base(url, token, options)
    {
        _purpose = purpose;
    }

    /// <summary>A <see cref="VendorStorage"/> from <c>VGI_BLOB_VENDOR_URL</c> and the token
    /// variables, or <see langword="null"/> when the URL is unset.</summary>
    public static VendorStorage? FromEnvironment(BlobPurpose purpose = BlobPurpose.Rpc, Func<string, string?>? environment = null) =>
        BlobVendorEnvironment.UrlAndToken(environment) is var (url, token) ? new VendorStorage(url, token, purpose) : null;

    /// <inheritdoc/>
    public async Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
    {
        using var cts = RequestTimeout(cancellationToken);
        var grant = await Api.PutAsync(data.LongLength, B64Sha256(data), _purpose, null, contentEncoding, null, null, cts.Token).ConfigureAwait(false);
        Check(grant.UploadUrl);
        await Api.PutObjectAsync(grant, data, contentEncoding, cts.Token).ConfigureAwait(false);
        var downloadUrl = grant.DownloadUrl.Length > 0 ? grant.DownloadUrl : (await Api.GetAsync(grant.ObjectId, cts.Token).ConfigureAwait(false)).DownloadUrl;
        Check(downloadUrl);
        return downloadUrl;
    }

    /// <inheritdoc/>
    public async Task<UploadUrl> GenerateUploadUrlAsync(long size, string sha256Hex, CancellationToken cancellationToken)
    {
        byte[] digest;
        try
        {
            digest = Convert.FromHexString(sha256Hex);
        }
        catch (FormatException)
        {
            digest = [];
        }

        if (digest.Length != 32)
        {
            throw new ArgumentException("upload sha256 must be 64 hex characters", nameof(sha256Hex));
        }

        using var cts = RequestTimeout(cancellationToken);
        var grant = await Api.PutAsync(size, Convert.ToBase64String(digest), BlobPurpose.Rpc, null, null, null, ClientUploadOrigin, cts.Token)
            .ConfigureAwait(false);
        if (grant.DownloadUrl.Length == 0)
        {
            throw new BlobVendorException("blob vendor put response for an rpc object has no 'download_url'");
        }

        Check(grant.UploadUrl);
        Check(grant.DownloadUrl);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in grant.RequiredHeaders)
        {
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (value != size.ToString(CultureInfo.InvariantCulture))
                {
                    throw new BlobVendorException($"blob vendor granted Content-Length {value} for a {size}-byte upload");
                }

                continue;
            }

            headers[name] = value;
        }

        var expires = grant.DownloadExpiresAt is { } d && d < grant.ExpiresAt ? d : grant.ExpiresAt;
        return new UploadUrl(grant.UploadUrl, grant.DownloadUrl, expires, headers) { ObjectId = grant.ObjectId };
    }

    /// <inheritdoc/>
    public async Task<string> PutObjectAsync(byte[] data, CancellationToken cancellationToken)
    {
        using var cts = RequestTimeout(cancellationToken);
        var grant = await Api.PutAsync(data.LongLength, B64Sha256(data), BlobPurpose.Rpc, null, null, null, StateRefOrigin, cts.Token)
            .ConfigureAwait(false);
        Check(grant.UploadUrl);
        await Api.PutObjectAsync(grant, data, null, cts.Token).ConfigureAwait(false);
        return grant.ObjectId;
    }

    /// <inheritdoc/>
    public async Task<byte[]> GetAsync(string objectId, long? maxBytes, CancellationToken cancellationToken)
    {
        var cap = maxBytes is > 0 ? maxBytes.Value : FetchConfig.DefaultMaxFetchBytes;
        using var cts = RequestTimeout(cancellationToken);
        VendorApi.BlobLocation location;
        try
        {
            location = await Api.GetAsync(objectId, cts.Token).ConfigureAwait(false);
        }
        catch (BlobVendorException exc) when (exc.Code is "not_found" or "not_uploaded" || exc.Status == 404)
        {
            throw new ObjectMissingException($"blob vendor object is gone ({(exc.Code.Length > 0 ? exc.Code : exc.Status.ToString(CultureInfo.InvariantCulture))})", exc);
        }

        if (location.Size > cap)
        {
            throw new BlobVendorException($"blob vendor object is {location.Size} bytes, over max_fetch_bytes ({cap})");
        }

        if (location.ContentEncoding is not ("" or "identity"))
        {
            throw new BlobVendorException($"blob vendor object has unsupported content_encoding '{location.ContentEncoding}'");
        }

        try
        {
            return await Api.DownloadAsync(location.DownloadUrl, ValidateUrl, cap, cts.Token).ConfigureAwait(false);
        }
        catch (BlobVendorException exc) when (exc.Status == 404)
        {
            throw new ObjectMissingException("blob vendor object is gone (storage 404)", exc);
        }
    }

    internal static string B64Sha256(byte[] data) => Convert.ToBase64String(SHA256.HashData(data));
}

/// <summary>
/// Stores raw byte blobs through the vendor, referenced by <see cref="BlobRef"/>, for callers that
/// keep the references themselves (a key-value store that externalizes large values). Purpose
/// <see cref="BlobPurpose.Fs"/> by default: no download URL at put; <see cref="GetBytesAsync"/>
/// asks for a fresh one. Thread-safe.
/// </summary>
public sealed class VendorBlobClient : VendorClientBase
{
    private const int MaxDeleteIds = 1000;
    private readonly BlobPurpose _purpose;

    /// <summary>Creates a blob client.</summary>
    public VendorBlobClient(string url, string token, BlobPurpose purpose = BlobPurpose.Fs, VendorOptions? options = null)
        : base(url, token, options)
    {
        _purpose = purpose;
    }

    /// <summary>A <see cref="VendorBlobClient"/> from the environment, or <see langword="null"/>
    /// when <c>VGI_BLOB_VENDOR_URL</c> is unset.</summary>
    public static VendorBlobClient? FromEnvironment(BlobPurpose purpose = BlobPurpose.Fs, Func<string, string?>? environment = null) =>
        BlobVendorEnvironment.UrlAndToken(environment) is var (url, token) ? new VendorBlobClient(url, token, purpose) : null;

    /// <summary>Stores <paramref name="data"/> and returns its reference. <paramref name="scope"/>
    /// names the execution it belongs to; <paramref name="idempotencyKey"/> makes a retried logical
    /// write reuse the object.</summary>
    public async Task<BlobRef> PutBytesAsync(byte[] data, BlobScope? scope = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        var digest = SHA256.HashData(data);
        var grant = await Api.PutAsync(data.LongLength, Convert.ToBase64String(digest), _purpose, scope, null, idempotencyKey, null, cancellationToken)
            .ConfigureAwait(false);
        Check(grant.UploadUrl);
        await Api.PutObjectAsync(grant, data, null, cancellationToken).ConfigureAwait(false);
        return new BlobRef(grant.ObjectId, data.LongLength, Convert.ToHexStringLower(digest));
    }

    /// <summary>Fetches and verifies the blob <paramref name="reference"/> names: pinned, capped,
    /// and checked against the reference's size and SHA-256 (not the vendor's own report).</summary>
    /// <exception cref="BlobVendorException">Including <see cref="BlobVendorException.SizeMismatchCode"/>
    /// and <see cref="BlobVendorException.Sha256MismatchCode"/>.</exception>
    public async Task<byte[]> GetBytesAsync(BlobRef reference, CancellationToken cancellationToken = default)
    {
        if (reference.Size > Options.MaxFetchBytes)
        {
            throw new BlobVendorException($"blob {reference.ObjectId} is {reference.Size} bytes, over the {Options.MaxFetchBytes}-byte fetch cap");
        }

        var location = await Api.GetAsync(reference.ObjectId, cancellationToken).ConfigureAwait(false);
        if (location.ContentEncoding is not ("" or "identity"))
        {
            throw new BlobVendorException($"blob {reference.ObjectId} has unsupported content_encoding '{location.ContentEncoding}'");
        }

        if (location.Size != reference.Size)
        {
            throw new BlobVendorException(
                $"blob {reference.ObjectId} size mismatch: vendor reports {location.Size}, ref has {reference.Size}", code: BlobVendorException.SizeMismatchCode);
        }

        byte[] data;
        try
        {
            data = await Api.DownloadAsync(location.DownloadUrl, ValidateUrl, Options.MaxFetchBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (BlobVendorException exc)
        {
            throw new BlobVendorException($"blob {reference.ObjectId} {exc.Message}", exc.Status, exc.Code);
        }

        if (data.LongLength != reference.Size)
        {
            throw new BlobVendorException(
                $"blob {reference.ObjectId} size mismatch: expected {reference.Size}, got {data.LongLength}", code: BlobVendorException.SizeMismatchCode);
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(data));
        return actual == reference.Sha256.ToLowerInvariant()
            ? data
            : throw new BlobVendorException(
                $"blob {reference.ObjectId} SHA-256 mismatch: expected {reference.Sha256}, got {actual}", code: BlobVendorException.Sha256MismatchCode);
    }

    /// <summary>Deletes objects by id (missing ones are ignored); returns how many were deleted.</summary>
    public async Task<int> DeleteAsync(IReadOnlyList<string> objectIds, CancellationToken cancellationToken = default)
    {
        var total = 0;
        for (var start = 0; start < objectIds.Count; start += MaxDeleteIds)
        {
            total += await Api.DeleteAsync(objectIds.Skip(start).Take(MaxDeleteIds).ToList(), cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>Deletes every object put under <paramref name="scope"/>.</summary>
    public Task<int> DeleteScopeAsync(BlobScope scope, CancellationToken cancellationToken = default) =>
        Api.DeleteScopeAsync(scope, cancellationToken);
}
