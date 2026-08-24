using QueryFarm.VgiRpc.Errors;
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
}
