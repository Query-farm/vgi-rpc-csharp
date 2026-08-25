using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Apache.Arrow;
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
    private static readonly HttpClient s_client = new();
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
    {
        var allocBody = contentEncoding is not null ? new Dictionary<string, string> { ["content_encoding"] = contentEncoding } : [];
        var allocResponse = await s_client.PostAsJsonAsync($"{_baseUrl}/alloc", allocBody, cancellationToken).ConfigureAwait(false);
        allocResponse.EnsureSuccessStatusCode();
        var allocation = await allocResponse.Content.ReadFromJsonAsync<Allocation>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("fake-storage /alloc returned no body");

        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (contentEncoding is not null)
        {
            content.Headers.ContentEncoding.Add(contentEncoding);
        }

        var putResponse = await s_client.PutAsync(allocation.UploadUrl ?? allocation.ObjectUrl, content, cancellationToken).ConfigureAwait(false);
        putResponse.EnsureSuccessStatusCode();
        return allocation.DownloadUrl ?? allocation.ObjectUrl ?? throw new InvalidOperationException("fake-storage /alloc returned no download_url/object_url");
    }

    public async Task<UploadUrl> GenerateUploadUrlAsync(Schema schema, CancellationToken cancellationToken)
    {
        var allocResponse = await s_client.PostAsJsonAsync($"{_baseUrl}/alloc", new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
        allocResponse.EnsureSuccessStatusCode();
        var allocation = await allocResponse.Content.ReadFromJsonAsync<Allocation>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("fake-storage /alloc returned no body");
        var objectUrl = allocation.ObjectUrl ?? throw new InvalidOperationException("fake-storage /alloc returned no object_url");
        return new UploadUrl(
            UploadUrlValue: allocation.UploadUrl ?? objectUrl,
            DownloadUrl: allocation.DownloadUrl ?? objectUrl,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1));
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
