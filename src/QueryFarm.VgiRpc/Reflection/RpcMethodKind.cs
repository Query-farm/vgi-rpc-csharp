namespace QueryFarm.VgiRpc.Reflection;

/// <summary>An RPC method's wire behavior, determined by its C# return type shape.</summary>
public enum RpcMethodKind
{
    /// <summary>A single request batch, a single response batch (plus zero or more log batches).</summary>
    Unary,

    // ProducerStream / ExchangeStream are added in Milestone 3 (Streaming) — see docs/roadmap.md.
}
