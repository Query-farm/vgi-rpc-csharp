using QueryFarm.VgiRpc.Logging;

namespace QueryFarm.VgiRpc.Client;

public sealed class RpcClientOptions
{
    public Action<LogMessage>? OnLog { get; init; }

    public string? ProtocolVersion { get; init; }

    /// <summary>Creates and negotiates a per-connection shared-memory segment of this size.</summary>
    public long? SharedMemorySize { get; init; }
}
