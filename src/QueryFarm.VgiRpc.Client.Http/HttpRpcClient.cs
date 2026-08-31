using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client.Http;

/// <summary>Schema-first vgi-rpc client for the stateless HTTP transport.</summary>
public sealed partial class HttpRpcClient : IRpcClient
{
    private const string ArrowContentType = "application/vnd.apache.arrow.stream";
    private readonly System.Net.Http.HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly HttpRpcClientOptions _options;
    private readonly string _prefix;
    private readonly Stack<SessionSnapshot> _sessionScopes = new();
    private string? _sessionToken;
    private bool _acceptNewSession;

    public HttpRpcClient(Uri baseAddress, HttpRpcClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        _options = options ?? new HttpRpcClientOptions();
        HttpMessageHandler handler;
        if (_options.TcpProxy is not null)
        {
            var sockets = new SocketsHttpHandler
            {
                AllowAutoRedirect = _options.FollowRedirects,
                AutomaticDecompression = DecompressionMethods.None,
                UseProxy = false,
                ConnectTimeout = _options.ConnectTimeout,
                ConnectCallback = async (context, token) =>
                {
                    var socket = await Socks5h.ConnectAsync(context.DnsEndPoint.Host,
                        context.DnsEndPoint.Port, _options.TcpProxy, _options.ConnectTimeout, token)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            if (_options.ClientCertificate is not null)
                sockets.SslOptions.ClientCertificates = new X509CertificateCollection { _options.ClientCertificate };
            handler = sockets;
        }
        else
        {
            var standard = new HttpClientHandler
            {
                AllowAutoRedirect = _options.FollowRedirects,
                AutomaticDecompression = DecompressionMethods.None,
            };
            if (_options.ClientCertificate is not null) standard.ClientCertificates.Add(_options.ClientCertificate);
            handler = standard;
        }

        _http = new System.Net.Http.HttpClient(handler) { BaseAddress = baseAddress };
        _ownsHttpClient = true;
        _prefix = NormalizePrefix(_options.Prefix);
        _acceptNewSession = _options.AcceptNewSession;
    }

    public HttpRpcClient(System.Net.Http.HttpClient httpClient, HttpRpcClientOptions? options = null, bool ownsHttpClient = false)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new HttpRpcClientOptions();
        _ownsHttpClient = ownsHttpClient;
        _prefix = NormalizePrefix(_options.Prefix);
        _acceptNewSession = _options.AcceptNewSession;
    }

    public string? SessionToken => _sessionToken;

    /// <summary>Creates a reflection-based typed facade over this HTTP client.</summary>
    public TContract CreateProxy<TContract>() where TContract : class =>
        RpcClientProxy<TContract>.Create(this);

    /// <summary>Starts a disposable sticky-session scope, optionally from an existing token.</summary>
    public HttpSessionScope WithSession(string? token = null) => new(this, token);

    async Task<IRpcProducerSession> IRpcClient.OpenProducerAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken) =>
        await OpenProducerAsync(method, parameters, hasHeader, metadata, cancellationToken).ConfigureAwait(false);

    async Task<IRpcExchangeSession> IRpcClient.OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        Schema inputSchema,
        bool hasHeader,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken) =>
        await OpenExchangeAsync(method, parameters, hasHeader, metadata, cancellationToken).ConfigureAwait(false);

    async Task<IRpcExchangeSession> IRpcClient.OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken) =>
        await OpenExchangeAsync(method, parameters, hasHeader, metadata, cancellationToken).ConfigureAwait(false);

    public IReadOnlyDictionary<string, string> LastEchoHeaders { get; private set; } = new Dictionary<string, string>();

    /// <summary>
    /// Starts a sticky-session scope. Scopes may be nested; ending the inner scope restores the
    /// outer token and its captured echo headers.
    /// </summary>
    public void BeginSession()
    {
        _sessionScopes.Push(new SessionSnapshot(_sessionToken, _acceptNewSession, LastEchoHeaders));
        _sessionToken = null;
        _acceptNewSession = true;
        LastEchoHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public void AttachSession(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _sessionToken = token;
        _acceptNewSession = false;
    }

    public string? DetachSession()
    {
        var token = _sessionToken;
        _sessionToken = null;
        _acceptNewSession = false;
        return token;
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_sessionToken is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_prefix}/__session__");
                AddCommonHeaders(request);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_sessionScopes.TryPop(out var outer))
            {
                _sessionToken = outer.Token;
                _acceptNewSession = outer.AcceptNew;
                LastEchoHeaders = outer.EchoHeaders;
            }
            else
            {
                _sessionToken = null;
                _acceptNewSession = false;
                LastEchoHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public async Task<AnnotatedBatch> CallUnaryAsync(
        string method,
        RecordBatch parameters,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var responseBody = await PostBatchAsync(
            $"{_prefix}/{Uri.EscapeDataString(method)}",
            parameters,
            RequestMetadata(method, metadata),
            cancellationToken).ConfigureAwait(false);
        return await ReadUnaryAsync(responseBody, method, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpProducerSession> OpenProducerAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var body = await PostBatchAsync(
            $"{_prefix}/{Uri.EscapeDataString(method)}/init",
            parameters,
            RequestMetadata(method, metadata),
            cancellationToken).ConfigureAwait(false);
        var parsed = await ParseStreamResponseAsync(body, hasHeader, cancellationToken).ConfigureAwait(false);
        return new HttpProducerSession(this, method, parsed.Header, parsed.Data, parsed.State, parsed.CallState, parsed.Finished);
    }

    public async Task<HttpExchangeSession> OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var body = await PostBatchAsync(
            $"{_prefix}/{Uri.EscapeDataString(method)}/init",
            parameters,
            RequestMetadata(method, metadata),
            cancellationToken).ConfigureAwait(false);
        var parsed = await ParseStreamResponseAsync(body, hasHeader, cancellationToken).ConfigureAwait(false);
        foreach (var unexpected in parsed.Data)
        {
            unexpected.Batch.Dispose();
        }

        return new HttpExchangeSession(this, method, parsed.Header, parsed.State, parsed.CallState, parsed.Finished);
    }

    public async Task<HttpServerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, $"{_prefix}/health");
        AddCommonHeaders(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return new HttpServerCapabilities(
            LongHeader(response, "VGI-Max-Request-Bytes"),
            LongHeader(response, "VGI-Max-Response-Bytes"),
            LongHeader(response, "VGI-Max-Upload-Bytes"),
            LongHeader(response, "VGI-Max-Externalized-Response-Bytes"),
            BoolHeader(response, "VGI-Externalization-Enabled"),
            BoolHeader(response, "VGI-Upload-URL-Support"),
            BoolHeader(response, StickySessions.StickyEnabledHeader),
            LongHeader(response, StickySessions.StickyDefaultTtlHeader),
            (Header(response, StickySessions.StickyEchoHeadersHeader) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ContentEncodingNegotiation.ParseEncodingList(Header(response, "VGI-Supported-Encodings")));
    }

    public async Task<IReadOnlyList<UploadUrl>> RequestUploadUrlsAsync(
        int count = 1,
        CancellationToken cancellationToken = default)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        // The public __upload_url__ contract defines count as nullable.  The value we send is
        // always present, but nullability is part of Arrow schema equality and Rust validates
        // this request strictly (as does the canonical Python schema).
        var schema = new Schema([new Field("count", Int64Type.Default, true)], null);
        using var request = new RecordBatch(schema, [new Int64Array.Builder().Append(count).Build()], 1);
        var body = await PostBatchAsync(
            $"{_prefix}/__upload_url__/init",
            request,
            RequestMetadata("__upload_url__", null),
            cancellationToken,
            allowExternalize: false).ConfigureAwait(false);
        using var stream = new MemoryStream(body);
        using var reader = new WireReader(stream);
        await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<UploadUrl>();
        while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } batch)
        {
            using (batch.Batch)
            {
                if (batch.GetMetadata(MetadataKeys.LogLevel) is { } level)
                {
                    if (level == "EXCEPTION")
                    {
                        throw RpcErrorDecoder.Decode(batch);
                    }

                    DispatchLog(batch);
                    continue;
                }

                var uploadIndex = batch.Batch.Schema.GetFieldIndex("upload_url");
                var downloadIndex = batch.Batch.Schema.GetFieldIndex("download_url");
                var expirationIndex = batch.Batch.Schema.GetFieldIndex("expires_at");
                if (uploadIndex < 0 || downloadIndex < 0 || expirationIndex < 0)
                {
                    throw new InvalidDataException(
                        "Upload URL response must contain upload_url, download_url, and expires_at columns.");
                }

                var uploads = batch.Batch.Column(uploadIndex) as StringArray
                    ?? throw new InvalidDataException("Upload URL response column 'upload_url' must be utf8.");
                var downloads = batch.Batch.Column(downloadIndex) as StringArray
                    ?? throw new InvalidDataException("Upload URL response column 'download_url' must be utf8.");
                var expirations = batch.Batch.Column(expirationIndex) as TimestampArray
                    ?? throw new InvalidDataException("Upload URL response column 'expires_at' must be timestamp.");
                for (var index = 0; index < batch.Batch.Length; index++)
                {
                    result.Add(new UploadUrl(
                        uploads.GetString(index)
                            ?? throw new InvalidDataException("Upload URL response contained a null upload_url."),
                        downloads.GetString(index)
                            ?? throw new InvalidDataException("Upload URL response contained a null download_url."),
                        expirations.GetTimestamp(index)
                            ?? throw new InvalidDataException("Upload URL response contained a null expires_at.")));
                }
            }
        }

        return result.Count == 0
            ? throw new InvalidDataException("Upload URL response contained no URL pairs.")
            : result;
    }

    internal async Task<byte[]> PostTurnAsync(
        string method,
        RecordBatch batch,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken) =>
        await PostBatchAsync($"{_prefix}/{Uri.EscapeDataString(method)}/exchange", batch, metadata, cancellationToken).ConfigureAwait(false);

    private async Task<byte[]> PostBatchAsync(
        string path,
        RecordBatch batch,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken,
        bool allowExternalize = true,
        bool useCompression = true)
    {
        var serializedBody = await SerializeAsync(batch, metadata, cancellationToken).ConfigureAwait(false);
        var body = serializedBody;
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AddCommonHeaders(request);
        if (useCompression && _options.CompressionLevel is { } level)
        {
            body = HttpCodec.Compress(body, _options.PreferredEncoding, level);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentEncoding.Add(WireEncoding(_options.PreferredEncoding));
            request.Headers.TryAddWithoutValidation("X-VGI-Accept-Encoding", "zstd, gzip");
        }
        else
        {
            request.Content = new ByteArrayContent(body);
        }

        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ArrowContentType);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        CaptureSession(response);
        if (response.StatusCode == HttpStatusCode.UnsupportedMediaType && useCompression)
        {
            // A 415 is rejected before RPC dispatch, so one identity retry cannot duplicate user
            // side effects. This also handles capability changes between discovery and dispatch.
            return await PostBatchAsync(
                path,
                batch,
                metadata,
                cancellationToken,
                allowExternalize,
                useCompression: false).ConfigureAwait(false);
        }

        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && allowExternalize && _options.ExternalLocation is not null)
        {
            var urls = await RequestUploadUrlsAsync(1, cancellationToken).ConfigureAwait(false);
            var target = urls[0];
            _options.ExternalLocation.UrlValidator?.Invoke(target.Upload);
            _options.ExternalLocation.UrlValidator?.Invoke(target.Download);
            using var uploadContent = new ByteArrayContent(serializedBody);
            uploadContent.Headers.ContentType = MediaTypeHeaderValue.Parse(ArrowContentType);
            using var uploadResponse = await _http.PutAsync(target.Upload, uploadContent, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(uploadResponse, cancellationToken).ConfigureAwait(false);
            var pointer = ExternalLocation.MakePointerBatch(batch.Schema, target.Download);
            using (pointer.Batch)
            {
                foreach (var (key, value) in metadata)
                {
                    pointer.Metadata[key] = value;
                }

                return await PostBatchAsync(
                    path,
                    pointer.Batch,
                    pointer.Metadata,
                    cancellationToken,
                    allowExternalize: false,
                    useCompression: useCompression).ConfigureAwait(false);
            }
        }

        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var encoding = Header(response, "X-VGI-Content-Encoding") ?? response.Content.Headers.ContentEncoding.FirstOrDefault();
        responseBody = HttpCodec.Decompress(responseBody, encoding);
        if (!response.IsSuccessStatusCode)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (responseBody.Length == 0 || !string.Equals(mediaType, ArrowContentType, StringComparison.OrdinalIgnoreCase))
            {
                const int MaxErrorDetailBytes = 4096;
                var detail = responseBody.Length == 0
                    ? ""
                    : $": {System.Text.Encoding.UTF8.GetString(responseBody.AsSpan(0, Math.Min(responseBody.Length, MaxErrorDetailBytes)))}"
                        + (responseBody.Length > MaxErrorDetailBytes ? "…" : "");
                throw new HttpRequestException(
                    $"vgi-rpc HTTP request failed with {(int)response.StatusCode} {response.ReasonPhrase}{detail}.",
                    null,
                    response.StatusCode);
            }
        }

        return responseBody;
    }

    private static async Task<byte[]> SerializeAsync(
        RecordBatch batch,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using (var writer = new WireWriter(buffer, batch.Schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, metadata), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private async Task<AnnotatedBatch> ReadUnaryAsync(byte[] body, string method, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(body);
        using var reader = new WireReader(stream);
        await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        AnnotatedBatch? result = null;
        try
        {
            while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } batch)
            {
                if (batch.GetMetadata(MetadataKeys.LogLevel) is { } level)
                {
                    if (level == "EXCEPTION")
                    {
                        var error = RpcErrorDecoder.Decode(batch);
                        batch.Batch.Dispose();
                        throw error;
                    }

                    DispatchLog(batch);
                    batch.Batch.Dispose();
                    continue;
                }

                result?.Batch.Dispose();
                result = await ResolveExternalAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            if (result is null)
            {
                throw new InvalidDataException($"HTTP response for '{method}' contained no result batch.");
            }

            var transfer = result;
            result = null;
            return transfer;
        }
        finally
        {
            result?.Batch.Dispose();
        }
    }

    private async Task<AnnotatedBatch> ResolveExternalAsync(AnnotatedBatch incoming, CancellationToken cancellationToken)
    {
        var (batch, metadata) = await ExternalLocation.ResolveAsync(
            incoming.Batch,
            incoming.Metadata,
            _options.ExternalLocation,
            cancellationToken,
            HandleExternalLog).ConfigureAwait(false);
        if (!ReferenceEquals(batch, incoming.Batch))
        {
            incoming.Batch.Dispose();
        }

        return new AnnotatedBatch(batch, metadata);
    }

    private void DispatchLog(AnnotatedBatch batch)
    {
        if (_options.OnLog is null)
        {
            return;
        }

        _ = Enum.TryParse<VgiLogLevel>(batch.GetMetadata(MetadataKeys.LogLevel), true, out var level);
        IReadOnlyDictionary<string, object?>? extra = null;
        if (batch.GetMetadata(MetadataKeys.LogExtra) is { } json)
        {
            try
            {
                extra = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            }
            catch (JsonException)
            {
                // Malformed optional log extras must not hide the actual RPC result.
            }
        }

        _options.OnLog(new LogMessage(level, batch.GetMetadata(MetadataKeys.LogMessage) ?? "", extra));
    }

    private void HandleExternalLog(AnnotatedBatch batch)
    {
        if (string.Equals(batch.GetMetadata(MetadataKeys.LogLevel), "EXCEPTION", StringComparison.OrdinalIgnoreCase))
        {
            throw RpcErrorDecoder.Decode(batch);
        }

        DispatchLog(batch);
    }

    private Dictionary<string, string> RequestMetadata(string method, IReadOnlyDictionary<string, string>? metadata)
    {
        var result = metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata);
        result[MetadataKeys.Method] = method;
        result[MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion;
        result.TryAdd(MetadataKeys.RequestId, Guid.NewGuid().ToString("n"));
        return result;
    }

    private void AddCommonHeaders(HttpRequestMessage request)
    {
        if (_options.DefaultHeaders is not null)
        {
            foreach (var (name, value) in _options.DefaultHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (_sessionToken is not null)
        {
            request.Headers.TryAddWithoutValidation(StickySessions.SessionHeader, _sessionToken);
            foreach (var (name, value) in LastEchoHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        else if (_acceptNewSession)
        {
            request.Headers.TryAddWithoutValidation(StickySessions.SessionAcceptHeader, "true");
        }
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (Header(response, StickySessions.SessionHeader) is { Length: > 0 } token)
        {
            _sessionToken = token;
            _acceptNewSession = false;
        }

        if (string.Equals(Header(response, StickySessions.SessionCloseHeader), "true", StringComparison.OrdinalIgnoreCase))
        {
            _sessionToken = null;
        }

        var echoHeaders = response.Headers
            .Where(header => header.Key.StartsWith(StickySessions.EchoHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key[StickySessions.EchoHeaderPrefix.Length..].ToLowerInvariant(),
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
        if (echoHeaders.Count > 0)
        {
            LastEchoHeaders = echoHeaders;
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() :
        response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;

    private static long? LongHeader(HttpResponseMessage response, string name) =>
        long.TryParse(Header(response, name), out var value) ? value : null;

    private static bool BoolHeader(HttpResponseMessage response, string name) =>
        string.Equals(Header(response, name), "true", StringComparison.OrdinalIgnoreCase);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"vgi-rpc HTTP request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}",
            null,
            response.StatusCode);
    }

    private static string NormalizePrefix(string prefix) =>
        string.IsNullOrWhiteSpace(prefix) ? "" : "/" + prefix.Trim('/');

    private static string WireEncoding(ContentEncoding encoding) => encoding switch
    {
        ContentEncoding.Zstd => "zstd",
        ContentEncoding.Gzip => "gzip",
        _ => "identity",
    };

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private sealed record SessionSnapshot(
        string? Token,
        bool AcceptNew,
        IReadOnlyDictionary<string, string> EchoHeaders);
}

/// <summary>A nestable sticky-session scope over one <see cref="HttpRpcClient"/>.</summary>
public sealed class HttpSessionScope : IAsyncDisposable
{
    private readonly HttpRpcClient _client;
    private bool _disposed;

    internal HttpSessionScope(HttpRpcClient client, string? token)
    {
        _client = client;
        _client.BeginSession();
        if (!string.IsNullOrWhiteSpace(token))
        {
            _client.AttachSession(token);
        }
    }

    public string? CurrentToken => _client.SessionToken;

    public IReadOnlyDictionary<string, string> EchoHeaders => _client.LastEchoHeaders;

    public TContract CreateProxy<TContract>() where TContract : class => _client.CreateProxy<TContract>();

    public string? Detach() => _client.DetachSession();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _client.EndSessionAsync().ConfigureAwait(false);
    }
}
