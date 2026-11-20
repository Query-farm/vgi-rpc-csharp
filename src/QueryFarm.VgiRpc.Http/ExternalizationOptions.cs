using QueryFarm.VgiRpc.External;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Bundles every M13 externalization knob <see cref="RpcHttpEndpoints.MapVgiRpc"/> accepts, so
/// that call site takes one parameter instead of five. All fields are independently optional —
/// an operator wanting only request-side pointer resolution (no response externalization, no
/// upload-URL vending) sets just <see cref="External"/>, for example.
/// </summary>
public sealed class ExternalizationOptions
{
    /// <summary>Drives both directions of server-side externalization: uploading oversized unary
    /// results (<see cref="ServerExternalConfig.Storage"/>) and resolving client-vended pointer
    /// batches on incoming requests/exchange turns (<see cref="ServerExternalConfig.FetchConfig"/>/
    /// <see cref="ServerExternalConfig.UrlValidator"/>). <see langword="null"/> disables both.</summary>
    public ServerExternalConfig? External { get; init; }

    /// <summary>Enables <c>POST {prefix}/__upload_url__/init</c> when non-null — lets a client
    /// externalize an oversized <i>request</i> by vending it a pre-signed upload/download URL
    /// pair to PUT to directly.</summary>
    public IUploadUrlProvider? UploadUrlProvider { get; init; }

    /// <summary>Hard cap on inbound request body size (pre-decompression, on-wire bytes) — see
    /// <see cref="RequestCap"/>. Advertised via <c>VGI-Max-Request-Bytes</c>.</summary>
    public long? MaxRequestBytes { get; init; }

    /// <summary>Advertised via <c>VGI-Max-Upload-Bytes</c>. <c>__upload_url__/init</c> refuses
    /// any entry whose declared <c>size</c> exceeds it, before minting a URL; the vended URL is
    /// signed for that exact size where the backend can, so storage rejects a longer body.</summary>
    public long? MaxUploadBytes { get; init; }

    /// <summary>Hard cap on the raw (pre-compression) byte count of any single externalized
    /// upload — unlike <c>max_response_bytes</c>, this has no soft/continuation escape valve
    /// (see <see cref="ExternalLocation.PredictExternalizeBytes"/>'s doc comment). Advertised via
    /// <c>VGI-Max-Externalized-Response-Bytes</c>.</summary>
    public long? MaxExternalizedResponseBytes { get; init; }
}
