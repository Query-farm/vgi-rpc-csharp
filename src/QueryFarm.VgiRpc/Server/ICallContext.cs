using QueryFarm.VgiRpc.Logging;

namespace QueryFarm.VgiRpc.Server;

/// <summary>
/// Framework-injected per-call context. A service interface method may declare a trailing
/// parameter of this type — it's excluded from the wire schema entirely (not a request field)
/// and the server supplies an instance per call. Mirrors Python's <c>CallContext</c>/
/// <c>ctx: CallContext</c> convention, scoped to what Milestone 2 needs (client-directed
/// logging); auth/transport metadata land alongside the auth milestones (see docs/roadmap.md).
/// </summary>
public interface ICallContext
{
    /// <summary>Emits a log message to the client, interleaved with the method's data/result batches.</summary>
    void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null);
}
