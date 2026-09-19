using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Apache.Arrow;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Http;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>
/// Adapter implementing both <see cref="IExternalStorage"/> and <see cref="IUploadUrlProvider"/>
/// against the canonical Python repo's <c>vgi_rpc.conformance.fake_storage</c> HTTP service — a
/// port of that module's own <c>FakeStorageBackend</c> class. Used by the conformance worker when
/// run with <c>--fake-storage URL</c> (see <c>docs/roadmap.md</c> M13).
///
/// <para><c>UploadAsync</c> covers server-to-client externalization (server uploads, embeds a GET
/// URL in the response pointer batch). <c>GenerateUploadUrlAsync</c> covers the client-to-server
/// upload-URL path (server vends a pre-signed URL pair so the client can PUT, then send a pointer
/// batch back). Wire contract: <c>POST /alloc</c> (optional JSON <c>{"content_encoding": "..."}</c>
/// body) returns <c>upload_url</c>/<c>download_url</c>; <c>PUT</c> the bytes to <c>upload_url</c>.</para>
/// </summary>
public sealed class FakeStorageBackend(string baseUrl) : IExternalStorage, IUploadUrlProvider
{
    /// <summary>
    /// A client that never reuses a connection.
    /// </summary>
    /// <remarks>
    /// The reference's fake storage is a <c>wsgiref</c> server: it answers <c>HTTP/1.0</c> and
    /// closes every connection after one response. The response carries a
    /// <c>Content-Length</c>, so a pooling <see cref="HttpClient"/> keeps the connection and sends
    /// the next request (the <c>PUT</c> after <c>/alloc</c>) on it -- sometimes after the server's
    /// FIN has arrived, sometimes not. When not, the request lands on a closed socket, the server
    /// answers RST, and the call fails with "The response ended prematurely", which
    /// <see cref="HttpClient"/> does not retry for a request with a body. A packet capture shows
    /// exactly that sequence (<c>POST /alloc</c>, the <c>HTTP/1.0</c> answer and FIN, then the
    /// <c>PUT</c> on the same connection, then RST). It is a race, so it grew with load: an
    /// isolated run of the reference suite's <c>http_externalize_always</c> transport passed while
    /// a full run lost most of it, and a 1,500-call stress lost about a quarter of its calls after
    /// the first few hundred. The reference's own adapter opens a new connection per request;
    /// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> of zero does the same.
    /// </remarks>
    private static readonly HttpClient s_client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero });
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
    {
        var allocBody = contentEncoding is not null ? new Dictionary<string, string> { ["content_encoding"] = contentEncoding } : [];
        using var allocResponse = await AllocAsync(allocBody, cancellationToken).ConfigureAwait(false);
        allocResponse.EnsureSuccessStatusCode();
        var allocation = await allocResponse.Content.ReadFromJsonAsync<Allocation>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("fake-storage /alloc returned no body");

        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (contentEncoding is not null)
        {
            content.Headers.ContentEncoding.Add(contentEncoding);
        }

        using var putResponse = await s_client.PutAsync(allocation.UploadUrl ?? allocation.ObjectUrl, content, cancellationToken).ConfigureAwait(false);
        putResponse.EnsureSuccessStatusCode();
        return allocation.DownloadUrl ?? allocation.ObjectUrl ?? throw new InvalidOperationException("fake-storage /alloc returned no download_url/object_url");
    }

    public async Task<UploadUrl> GenerateUploadUrlAsync(Schema schema, CancellationToken cancellationToken)
    {
        using var allocResponse = await AllocAsync([], cancellationToken).ConfigureAwait(false);
        allocResponse.EnsureSuccessStatusCode();
        var allocation = await allocResponse.Content.ReadFromJsonAsync<Allocation>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("fake-storage /alloc returned no body");
        var objectUrl = allocation.ObjectUrl ?? throw new InvalidOperationException("fake-storage /alloc returned no object_url");
        return new UploadUrl(
            UploadUrlValue: allocation.UploadUrl ?? objectUrl,
            DownloadUrl: allocation.DownloadUrl ?? objectUrl,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>
    /// <c>POST /alloc</c> with a buffered JSON body.
    /// </summary>
    /// <remarks>
    /// Not <c>PostAsJsonAsync</c>: its <c>JsonContent</c> cannot state its length up front, so the
    /// request goes out with <c>Transfer-Encoding: chunked</c>, which the fake storage does not
    /// decode -- it reads exactly <c>Content-Length</c> bytes, and closing a socket with unread
    /// bytes in its receive buffer sends RST rather than FIN (the reference's own comment on
    /// <c>make_app</c>). Buffering the body gives the request its length.
    /// </remarks>
    private Task<HttpResponseMessage> AllocAsync(Dictionary<string, string> body, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return s_client.PostAsync($"{_baseUrl}/alloc", content, cancellationToken);
    }

    private sealed class Allocation
    {
        [JsonPropertyName("object_url")]
        public string? ObjectUrl { get; set; }

        [JsonPropertyName("upload_url")]
        public string? UploadUrl { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }
    }
}
