using QueryFarm.VgiRpc.Logging;

namespace QueryFarm.VgiRpc.Client;

public sealed class RpcClientOptions
{
    public Action<LogMessage>? OnLog { get; init; }

    /// <summary>
    /// Routing key of the hosted protocol this client addresses — e.g.
    /// <c>"ConformanceService"</c>, <c>"vgi_rpc.Reflection.v1"</c>. Stamped on every request as
    /// <c>vgi_rpc.protocol</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a byte-stream transport this key is the only carrier the protocol has, so the server
    /// requires it: there is no path segment to fall back on, and dispatching without one means
    /// landing on whichever protocol happens to be registered first.
    /// </para>
    /// <para>
    /// Leave it unset when using a typed entry point — <see cref="RpcClient.CreateProxy{T}"/> or
    /// <see cref="RpcConnection{T}"/> — and the client derives the name from the contract type
    /// with the same <see cref="Reflection.WireNaming.ForProtocol"/> rule the server hosts it
    /// under, so the two agree by construction. It is only required for a schema-first
    /// <see cref="RpcClient.CallUnaryAsync(string, Apache.Arrow.RecordBatch, System.Collections.Generic.IReadOnlyDictionary{string, string}, System.Threading.CancellationToken)"/> against a dynamic protocol, where there is no
    /// contract to read it from. Setting it always wins over the derivation.
    /// </para>
    /// </remarks>
    public string Protocol { get; init; } = "";

    public string? ProtocolVersion { get; init; }

    /// <summary>Creates and negotiates a per-connection shared-memory segment of this size.</summary>
    public long? SharedMemorySize { get; init; }
}
