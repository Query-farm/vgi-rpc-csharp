namespace QueryFarm.VgiRpc.Attributes;

/// <summary>
/// Declares the record type a stream method emits as its per-stream header.
/// </summary>
/// <remarks>
/// <para>
/// This port supplies a header at <em>run time</em>, through
/// <see cref="Streaming.RpcStream{TState}.Header"/>, which means nothing about the method's
/// signature says whether it has one or what shape it is. That is fine for serving a call and
/// wrong for describing a protocol: <c>has_header</c> and the header schema are part of the wire
/// surface, so they are part of the protocol hash, and a port that cannot state them statically
/// cannot agree with any other port about what protocol it speaks.
/// </para>
/// <para>
/// Hence this attribute. It is a declaration, not an enforcement -- the framework does not check
/// that the value handed to <c>RpcStream.Header</c> matches, exactly as the other ports do not
/// re-validate a declared header type at emit time. What it buys is a truthful description.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StreamHeaderAttribute(Type headerType) : Attribute
{
    /// <summary>The record type emitted as the stream header.</summary>
    public Type HeaderType { get; } = headerType;
}
