using Apache.Arrow;

namespace QueryFarm.VgiRpc.External;

/// <summary>
/// A reference to an already-published unary result — a port of the canonical Python repo's
/// <c>vgi_rpc.external.ExternalRef</c>.
/// </summary>
/// <remarks>
/// <para>
/// A unary method answers with a ref by calling
/// <see cref="Server.ICallContext.RespondWithExternalRef"/> on its injected context (and
/// returning any placeholder value — it is ignored). The server then writes the
/// ExternalLocation pointer batch for <see cref="Url"/> directly: no result batch is built or
/// validated, nothing is serialized, compressed or uploaded during the call, and the ref is used
/// whether or not the server has external storage configured and regardless of
/// <see cref="ServerExternalConfig.ExternalizeThresholdBytes"/>. It is never inlined and never
/// routed through shared memory, and it does not count toward
/// <c>max_externalized_response_bytes</c>. Clients resolve it like any other pointer, so they
/// need no change (WIRE_PROTOCOL.md §12).
/// </para>
/// <para>
/// Build one with <see cref="ExternalLocation.PublishExternalAsync"/>, or by hand for an object
/// published out of band. The object at <see cref="Url"/> must be an Arrow IPC stream
/// (optionally <c>Content-Encoding</c>-compressed) whose schema is the method's result schema
/// and which holds exactly one 1-row data batch.
/// </para>
/// <para>
/// The caller owns caching the ref and the object's lifecycle: a long-lived ref must not point
/// at an object under the short-TTL lifecycle rule used for per-call uploads, and a pre-signed
/// URL expires — re-sign or rebuild the ref before then. Only return a ref to callers who are
/// all entitled to the same content.
/// </para>
/// </remarks>
public sealed record ExternalRef
{
    /// <summary>Creates a reference to a published object.</summary>
    /// <param name="url">Where the published IPC stream lives. Must be non-empty.</param>
    /// <param name="sha256">Lowercase hex SHA-256 of the raw (pre-compression) IPC stream
    /// bytes, sent as <c>vgi_rpc.location.sha256</c>. <see langword="null"/> omits the key, so
    /// clients skip the content check — use this for an object rewritten in place or one too
    /// large to hash.</param>
    /// <exception cref="ArgumentException"><paramref name="url"/> is empty, or
    /// <paramref name="sha256"/> is not 64 lowercase hex characters.</exception>
    public ExternalRef(string url, string? sha256 = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Length == 0)
        {
            throw new ArgumentException("ExternalRef.Url must be non-empty", nameof(url));
        }

        if (sha256 is not null && !IsLowerHexSha256(sha256))
        {
            throw new ArgumentException("ExternalRef.Sha256 must be 64 lowercase hex characters (or null)", nameof(sha256));
        }

        Url = url;
        Sha256 = sha256;
    }

    /// <summary>Where the published IPC stream lives.</summary>
    public string Url { get; }

    /// <summary>Lowercase hex SHA-256 of the raw (pre-compression) IPC bytes, or
    /// <see langword="null"/> to have clients skip the content check.</summary>
    public string? Sha256 { get; }

    /// <summary>Builds the zero-row pointer batch announcing this ref.</summary>
    /// <param name="schema">The method's result schema.</param>
    public (RecordBatch Batch, Dictionary<string, string> Metadata) PointerBatch(Schema schema) =>
        ExternalLocation.MakePointerBatch(schema, Url, Sha256);

    private static bool IsLowerHexSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
