namespace QueryFarm.VgiRpc.Reflection;

/// <summary>An RPC method's wire behavior, determined by its C# return type shape.</summary>
public enum RpcMethodKind
{
    /// <summary>A single request batch, a single response batch (plus zero or more log batches).</summary>
    Unary,

    /// <summary>A lockstep streaming call (producer or exchange — determined per-call by whether
    /// the returned <see cref="Streaming.IRpcStream"/>'s InputSchema is set, not statically).</summary>
    Stream,
}
