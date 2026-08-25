namespace QueryFarm.VgiRpc.Server;

/// <summary>
/// A generic dispatch-observability seam — the C# analog of the canonical Python repo's
/// <c>_DispatchHook</c> protocol (<c>vgi_rpc/rpc/_common.py</c>), which
/// <c>QueryFarm.VgiRpc.OpenTelemetry</c> and <c>QueryFarm.VgiRpc.Sentry</c> (M16) both implement
/// as pure consumers, exactly as <c>docs/roadmap.md</c>'s original plan intended.
///
/// <para><b>Granularity</b>: one <see cref="OnDispatchStart"/>/<see cref="OnDispatchEnd"/> pair
/// per RPC <i>call</i> — for a unary method, the one request/response; for a streaming method
/// over the pipe/unix/tcp transports, the whole lockstep session from the constructor invocation
/// to the final turn (matching how <c>RpcServer</c>'s own access-log record is emitted once per
/// stream call, not once per turn); for a streaming method over HTTP, one pair per HTTP request
/// (init, or each exchange turn) — HTTP has no single continuous connection to wrap the way pipe
/// transport does, and this matches the granularity <c>RpcHttpEndpoints</c>'s own access-log
/// emission already uses there.</para>
///
/// <para><b>Scope narrower than Python here</b>: this port's hook contract carries method/
/// protocol/server identity, duration, and error — not Python's additional
/// <c>AuthContext</c> (caller principal/domain/claims) or <c>CallStatistics</c> (input/output
/// row/byte counts). Auth identity is resolved through entirely different mechanisms on HTTP
/// (<c>AuthIdentity</c>, <c>HttpContext.Items</c>-based) versus pipe/unix/tcp (no auth concept at
/// all in this protocol), with no shared abstraction yet to surface through one hook parameter;
/// per-call row/byte statistics aren't tracked anywhere in this port today. Both are documented
/// gaps (see <c>docs/roadmap.md</c> M16), not silent omissions — a future milestone adding either
/// can extend <see cref="DispatchHookInfo"/> without an interface-breaking change, since callers
/// only ever construct it via <c>with</c>-style initialization at call sites this port owns.</para>
/// </summary>
public interface IRpcDispatchHook
{
    /// <summary>Called before dispatching to the implementation method. Returns an opaque token
    /// (e.g. a started span/activity) handed back unchanged to <see cref="OnDispatchEnd"/> —
    /// <see langword="null"/> is a valid token when a hook has nothing to carry forward.</summary>
    object? OnDispatchStart(DispatchHookInfo info);

    /// <summary>Called after the call completes (success or failure). <paramref name="error"/> is
    /// the unwrapped exception the call raised, or <see langword="null"/> on success.</summary>
    void OnDispatchEnd(object? token, DispatchHookInfo info, Exception? error);
}

/// <summary>Identity of one RPC call, passed to both halves of <see cref="IRpcDispatchHook"/>.
/// The same instance is passed to <see cref="IRpcDispatchHook.OnDispatchStart"/> and
/// <see cref="IRpcDispatchHook.OnDispatchEnd"/> for a given call.</summary>
/// <param name="MethodName">Wire (snake_case) method name.</param>
/// <param name="MethodType">Either <c>"unary"</c> or <c>"stream"</c> — matches the access-log
/// schema's own <c>method_type</c> values.</param>
/// <param name="ProtocolName">The service interface's protocol name (<c>RpcServer.ProtocolName</c>).</param>
/// <param name="ServerId">This server instance's id (<c>RpcServer.ServerId</c>).</param>
public sealed record DispatchHookInfo(string MethodName, string MethodType, string ProtocolName, string ServerId);

/// <summary>
/// Fans one logical dispatch-hook call out to every hook in a fixed list — lets
/// <c>RpcServer</c>/<c>RpcHttpEndpoints</c> carry a single <see cref="IRpcDispatchHook"/>
/// reference regardless of how many concrete hooks (OpenTelemetry, Sentry, both, neither) are
/// actually configured, matching Python's own <c>_register_dispatch_hook</c> composition —
/// though as a fixed fan-out list rather than a linked chain, since this port has no scenario
/// (yet) needing hooks registered incrementally after server construction.
/// </summary>
public sealed class CompositeDispatchHook(IReadOnlyList<IRpcDispatchHook> hooks) : IRpcDispatchHook
{
    public object? OnDispatchStart(DispatchHookInfo info)
    {
        var tokens = new object?[hooks.Count];
        for (var i = 0; i < hooks.Count; i++)
        {
            tokens[i] = hooks[i].OnDispatchStart(info);
        }

        return tokens;
    }

    public void OnDispatchEnd(object? token, DispatchHookInfo info, Exception? error)
    {
        var tokens = (object?[])token!;
        for (var i = 0; i < hooks.Count; i++)
        {
            hooks[i].OnDispatchEnd(tokens[i], info, error);
        }
    }
}
