namespace QueryFarm.VgiRpc.Attributes;

/// <summary>What a stream method does: produce only, or exchange.</summary>
/// <remarks>
/// <para>
/// This port decides per call, by whether the returned <c>RpcStream</c> sets an input schema.
/// That is fine for serving a call and insufficient for describing a protocol: <c>stream_kind</c>
/// is the only field in a description that says whether a stream accepts input -- the description
/// carries parameter, result and header schemas, but a stream's <em>input</em> schema arrives at
/// init time. So a client, a code generator, or a human reading a describe page has exactly one
/// place to learn whether a method is send-and-receive or receive-only, and without this
/// attribute that place reads "unknown".
/// </para>
/// <para>
/// A declaration, not an enforcement: the framework does not check that the returned
/// <c>RpcStream</c> matches, exactly as it does not re-validate a declared header type at emit
/// time. What it buys is a truthful description.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StreamKindAttribute(StreamKind kind) : Attribute
{
    /// <summary>The declared kind.</summary>
    public StreamKind Kind { get; } = kind;
}

/// <summary>The values <see cref="StreamKindAttribute"/> may declare.</summary>
public enum StreamKind
{
    /// <summary>Server-driven: the client receives batches and sends none.</summary>
    Producer,

    /// <summary>Bidirectional: the client sends input batches and receives output.</summary>
    Exchange,
}
