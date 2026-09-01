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
    private static readonly AsyncLocal<PeerConnectionIdentity?> CurrentValue = new();

    public static PeerConnectionIdentity Current => CurrentValue.Value ?? PeerConnectionIdentity.Anonymous;

    public static IDisposable Push(PeerConnectionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var previous = CurrentValue.Value;
        CurrentValue.Value = identity;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(PeerConnectionIdentity? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            CurrentValue.Value = previous;
            _disposed = true;
        }
    }
}
