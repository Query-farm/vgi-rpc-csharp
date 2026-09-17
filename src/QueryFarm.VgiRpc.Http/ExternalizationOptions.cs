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

    /// <summary>Advertised via <c>VGI-Max-Upload-Bytes</c> — informational only in this port (the
    /// upload-URL provider itself is responsible for enforcing it against what actually lands in
    /// storage; this server never sees the uploaded bytes, since the client PUTs directly to the
    /// vended pre-signed URL).</summary>
    public long? MaxUploadBytes { get; init; }

    /// <summary>Hard cap on the raw (pre-compression) byte count of any single externalized
    /// upload — unlike <c>max_response_bytes</c>, this has no soft/continuation escape valve
    /// (see <see cref="ExternalLocation.PredictExternalizeBytes"/>'s doc comment). Advertised via
    /// <c>VGI-Max-Externalized-Response-Bytes</c>.</summary>
    public long? MaxExternalizedResponseBytes { get; init; }
}
