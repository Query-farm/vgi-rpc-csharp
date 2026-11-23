using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Logging;

namespace QueryFarm.VgiRpc.Server;

/// <summary>
/// Framework-injected per-call context. A service interface method may declare a trailing
/// parameter of this type — it's excluded from the wire schema entirely (not a request field)
/// and the server supplies an instance per call. Mirrors Python's <c>CallContext</c>/
/// <c>ctx: CallContext</c> convention.
///
/// <para>The sticky-session members (<see cref="Session"/>, <see cref="SessionId"/>,
/// <see cref="OpenSession"/>, <see cref="CloseSession"/>) are HTTP-only (see
/// <c>docs/roadmap.md</c> M10 and <c>docs/sticky-sessions-spec.md</c>) and carry default
/// implementations here so every non-HTTP <see cref="ICallContext"/> — the pipe/unix/tcp
/// transports' <c>BufferedCallContext</c>/<c>StreamCallContext</c> in <see cref="RpcServer"/> —
/// automatically gets the spec-correct behavior (<see cref="Session"/>/<see cref="SessionId"/>
/// read as <see langword="null"/>; <see cref="OpenSession"/>/<see cref="CloseSession"/> throw)
/// without needing to implement them at all. Only <c>QueryFarm.VgiRpc.Http</c>'s call-context
/// implementations override them, and only when the operator actually enabled sticky sessions.
/// </para>
/// </summary>
public interface ICallContext
{
    /// <summary>Application or transport authentication for this call.</summary>
    AuthContext Auth => AuthContext.Anonymous;

    /// <summary>Immutable off-wire transport-peer evidence; empty when disabled.</summary>
    PeerEvidenceSet PeerEvidence => PeerEvidenceSet.Empty;

    /// <summary>Negotiated hard decoded-Arrow response limit, or null when unbounded.</summary>
    long? ResponseLimitBytes => null;

    /// <summary>Server batching target clamped to the hard limit, or null when unset.</summary>
    long? PreferredResponseBytes => null;

    /// <summary>Emits a log message to the client, interleaved with the method's data/result batches.</summary>
    void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null);

    /// <summary>The live sticky-session state object bound to this request, or <see langword="null"/>
    /// if none is bound. Always <see langword="null"/> on transports other than HTTP, or on HTTP
    /// without sticky sessions enabled.</summary>
    object? Session => null;

    /// <summary>The opaque hex session id bound to this request, or <see langword="null"/> if none.</summary>
    string? SessionId => null;

    /// <summary>
    /// Registers a sticky session holding <paramref name="state"/> for subsequent requests that
    /// echo the minted <c>VGI-Session</c> token. See <c>docs/sticky-sessions-spec.md</c> §4.
    /// </summary>
    /// <exception cref="RpcException">Sticky sessions are not available on the current transport;
    /// the client did not opt in (missing <c>VGI-Session-Accept: true</c>); or a session is
    /// already bound to this request. Reports as the wire type <c>"RuntimeError"</c> — matching
    /// Python, which raises its own built-in <c>RuntimeError</c> for exactly these three cases
    /// (not a vgi-rpc-specific exception class), so this port's framework-level validation
    /// throws an <see cref="RpcException"/> carrying that literal wire type rather than a CLR
    /// <see cref="InvalidOperationException"/> (which would report as itself, not
    /// <c>"RuntimeError"</c> — see <see cref="RpcException.ErrorType"/>'s role in
    /// <c>LogMessage.FromException</c>).</exception>
    void OpenSession(object state, TimeSpan? ttl = null) =>
        throw new RpcException("RuntimeError", "sticky sessions not available on this transport");

    /// <summary>
    /// Invalidates the sticky session bound to this request — disposes <c>state</c> if it
    /// implements <see cref="IDisposable"/>/<see cref="IAsyncDisposable"/> (this port's idiomatic
    /// translation of Python's duck-typed <c>state.close()</c> convention), removes the registry
    /// entry, and arranges for <c>VGI-Session-Close: true</c> on the response. Idempotent.
    /// </summary>
    /// <exception cref="RpcException">Sticky sessions are not available on the current transport
    /// — reports as the wire type <c>"RuntimeError"</c>, matching Python (see
    /// <see cref="OpenSession"/>'s doc comment for why).</exception>
    void CloseSession() =>
        throw new RpcException("RuntimeError", "sticky sessions not available on this transport");

    /// <summary>
    /// Answers this unary call with a pre-published <see cref="External.ExternalRef"/> instead of
    /// the method's return value — this port's spelling of the canonical Python repo's "return
    /// an <c>ExternalRef</c> from a unary method".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method still returns a value of its declared type to satisfy the compiler; the server
    /// ignores it (no null check, no result batch) and writes the ref's ExternalLocation pointer
    /// batch directly: no serialization, compression or upload, never inline and never through
    /// shared memory, regardless of the server's external storage configuration and
    /// <c>externalize_threshold_bytes</c>, and not counted toward
    /// <c>max_externalized_response_bytes</c>. Log messages emitted during the call still precede
    /// the pointer. A second call replaces the first; an exception thrown by the method after
    /// calling this still answers with that error.
    /// </para>
    /// <para>
    /// Unary calls only, on every transport. Stream calls (and their init) refuse it.
    /// </para>
    /// <code>
    /// public async Task&lt;string&gt; CatalogAsync(ICallContext? ctx = null)
    /// {
    ///     ctx!.RespondWithExternalRef(await GetOrPublishCatalogRefAsync());
    ///     return null!; // ignored
    /// }
    /// </code>
    /// </remarks>
    /// <exception cref="RpcException">This call is not a unary call (reports as the wire type
    /// <c>"RuntimeError"</c>).</exception>
    void RespondWithExternalRef(External.ExternalRef reference) =>
        throw new RpcException("RuntimeError", "an ExternalRef can only answer a unary call");

    /// <summary>
    /// Wraps <paramref name="value"/> in a <see cref="Streaming.StateRef"/>: stream state that does
    /// not change across turns (<c>STATE_REF_V1.md</c> §2). Hold the result in the stream's state
    /// and read it back on any turn with <see cref="Resolve(Streaming.StateRef)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Valid only on the context handed to a stream method itself, while it builds the stream
    /// (HTTP <c>/init</c>, or stream construction on the byte-stream transports). Every other
    /// context -- unary calls, every turn (the first producer turn included), cancel -- throws
    /// <c>state_ref_misuse</c>. State transitions copy the reference; they never create another.
    /// Within one init, the same value (byte-equal) gives the same reference.
    /// </para>
    /// <para>
    /// vgi-rpc-csharp keeps stream state in memory on every transport (single-replica in 3.4,
    /// §5.4), so the reference simply holds the value: nothing is encoded, stored or fetched.
    /// Treat <paramref name="value"/> as immutable afterwards.
    /// </para>
    /// </remarks>
    /// <exception cref="RpcException"><c>state_ref_misuse</c> outside a stream's init.</exception>
    Streaming.StateRef StateRef(byte[] value) =>
        throw StateRefErrors.StateRefMisuse(null, null);

    /// <summary>Wraps a record (serialized the way a nested record field is) in a
    /// <see cref="Streaming.StateRef"/>; see <see cref="StateRef(byte[])"/>. The same instance,
    /// or a byte-equal record, gives the same reference, and a typed
    /// <see cref="Resolve{T}(Streaming.StateRef)"/> returns this instance.</summary>
    /// <exception cref="RpcException"><c>state_ref_misuse</c> outside a stream's init.</exception>
    Streaming.StateRef StateRef<T>(T record)
        where T : class =>
        throw StateRefErrors.StateRefMisuse(null, null);

    /// <summary>The value's bytes (§5.3). Valid on every turn and every transport.</summary>
    /// <exception cref="RpcException"><c>invalid_state_ref</c> for a reference that does not
    /// decode (an unknown form or codec, or a stored tag, which this server never mints).</exception>
    byte[] Resolve(Streaming.StateRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.ResolveBytes(Streaming.TokenCodec.MaxTokenBytes);
    }

    /// <summary>The value as a record: the instance it was created from when the reference was
    /// made from one, else its bytes decoded as <typeparamref name="T"/>.</summary>
    /// <exception cref="RpcException"><c>invalid_state_ref</c>, as
    /// <see cref="Resolve(Streaming.StateRef)"/>, or when the bytes are not a
    /// <typeparamref name="T"/>.</exception>
    T Resolve<T>(Streaming.StateRef reference)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.ResolveRecord<T>(Streaming.TokenCodec.MaxTokenBytes);
    }

    /// <summary>
    /// Resolves an argument reference minted by this server's <c>__upload_url__/init</c> grant
    /// (<c>STATE_REF_V1.md</c> §10.2): opens <paramref name="reference"/> under this call's
    /// principal, checks its sealed size and SHA-256 equal <paramref name="size"/> and
    /// <paramref name="sha256"/>, then fetches through the process's object cache and verifies.
    /// </summary>
    /// <param name="reference">The sealed reference (the grant's <c>ref</c> column).</param>
    /// <param name="size">The byte length claimed alongside it.</param>
    /// <param name="sha256">The SHA-256 claimed alongside it, 64 lowercase hex characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified object bytes. Treat them as immutable: the object cache shares
    /// them.</returns>
    /// <exception cref="RpcException"><c>invalid_arguments_ref</c> (does not open for this caller,
    /// differs from the claimed values, no object storage, or not an HTTP server: byte-stream
    /// transports vend no grants), <c>arguments_ref_integrity</c> (fetched bytes differ),
    /// <c>arguments_ref_missing</c> (object gone), <c>storage_unavailable</c>.</exception>
    Task<byte[]> ResolveArgumentRefAsync(byte[] reference, long size, string sha256, CancellationToken cancellationToken = default) =>
        throw StateRefErrors.InvalidArgumentsRef("argument references can only be resolved by an HTTP server");
}
