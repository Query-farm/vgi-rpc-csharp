namespace QueryFarm.VgiRpc.External;

/// <summary>
/// Storage that holds opaque objects by id: the protocol-3.4 object API
/// (<c>STATE_REF_V1.md</c> §10.1, <c>put_object</c> / <c>get(object_id)</c>).
/// </summary>
/// <remarks>
/// <para>
/// A separate interface next to <see cref="IExternalStorage"/> rather than new members of it, so a
/// third-party <see cref="IExternalStorage"/> keeps compiling (the reference makes the same split,
/// <c>vgi_rpc.external.ObjectStore</c>). A storage that implements only
/// <see cref="IExternalStorage"/> keeps working for externalization; it cannot resolve argument
/// references (<c>invalid_arguments_ref</c>). Every in-tree backend (<c>S3Storage</c>,
/// <c>GcsStorage</c>, the conformance fake storage) implements both.
/// </para>
/// <para>
/// Objects carry no TTL and the server never deletes them. Operators must set a bucket lifecycle
/// rule of at least one day on the prefix these objects live under.
/// </para>
/// <para>Implementations must be thread-safe.</para>
/// </remarks>
public interface IObjectStore
{
    /// <summary>Stores <paramref name="data"/> as one <c>rpc</c>-purpose object and returns its id.
    /// The write must carry the size and SHA-256 of <paramref name="data"/> where the backend can
    /// verify them (an S3 <c>PutObject</c> with <c>ChecksumSHA256</c>, a sized vendor grant).</summary>
    /// <param name="data">The bytes to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The backend's object id (an S3/GCS key, a vendor <c>object_id</c>), at most 1024
    /// bytes of UTF-8.</returns>
    Task<string> PutObjectAsync(byte[] data, CancellationToken cancellationToken);

    /// <summary>Fetches the object <paramref name="objectId"/> names. The download is minted
    /// server-side from the id, pinned to the same hosts as other storage fetches, and capped at
    /// <paramref name="maxBytes"/>. Callers verify size and SHA-256 themselves.</summary>
    /// <param name="objectId">An id returned by <see cref="PutObjectAsync"/> or carried by an
    /// <see cref="UploadUrl.ObjectId"/>.</param>
    /// <param name="maxBytes">Refuse objects larger than this (the server's
    /// <c>max_fetch_bytes</c>); <see langword="null"/> uses the backend's default (256 MiB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The object bytes.</returns>
    /// <exception cref="ObjectMissingException">The object does not exist (never uploaded,
    /// deleted, or expired by lifecycle). Any other failure is transient.</exception>
    Task<byte[]> GetAsync(string objectId, long? maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Storage reports an object absent (HTTP 404, S3 <c>NoSuchKey</c>, vendor <c>not_found</c>).
/// Distinguishable from a transient failure on purpose: a missing object is final
/// (<c>arguments_ref_missing</c> / <c>state_ref_missing</c>), a 5xx or a timeout is
/// <c>UNAVAILABLE</c> and may succeed on retry. Messages never contain signed URLs. Mirrors the
/// reference's <c>vgi_rpc.external.ObjectMissingError</c>.
/// </summary>
public sealed class ObjectMissingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ObjectMissingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the underlying cause.</summary>
    public ObjectMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
