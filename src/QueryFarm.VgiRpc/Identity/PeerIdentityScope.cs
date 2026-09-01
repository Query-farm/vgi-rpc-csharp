using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Identity;

/// <summary>Connection-lifetime off-wire identity snapshot for raw transports.</summary>
public sealed record PeerConnectionIdentity(
    AuthContext Auth,
    PeerEvidenceSet Evidence,
    IReadOnlyDictionary<string, object?> TransportMetadata)
{
    public static PeerConnectionIdentity Anonymous { get; } =
        new(AuthContext.Anonymous, PeerEvidenceSet.Empty,
            new Dictionary<string, object?>());
}

/// <summary>
/// Async-flow scope used by a transport to expose one accepted connection's
/// verified identity to every RPC call dispatched on that connection.
/// </summary>
public static class PeerIdentityScope
{
    private static readonly AsyncLocal<PeerConnectionIdentity?> s_currentValue = new();

    public static PeerConnectionIdentity Current => s_currentValue.Value ?? PeerConnectionIdentity.Anonymous;

    public static IDisposable Push(PeerConnectionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var previous = s_currentValue.Value;
        s_currentValue.Value = identity;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(PeerConnectionIdentity? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            s_currentValue.Value = previous;
            _disposed = true;
        }
    }
}
