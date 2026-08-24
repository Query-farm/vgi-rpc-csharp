using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Streaming;

/// <summary>
/// Server-side state for one streaming RPC call, driven once per lockstep turn. Mirrors
/// Python's <c>StreamState</c>. See <see cref="ProducerState"/>/<see cref="ExchangeState"/> for
/// the two conveniences most implementations use instead of implementing this directly.
/// </summary>
public abstract class StreamState
{
    /// <summary>Called once per lockstep turn: an input batch arrives, and the implementation
    /// emits at most one output batch (via <paramref name="output"/>) and/or calls
    /// <see cref="OutputCollector.Finish"/> to end the stream.</summary>
    public abstract Task ProcessAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken);

    /// <summary>Called when the client sends an explicit cancel batch mid-stream (see docs/roadmap.md M3).</summary>
    public virtual void OnCancel(ICallContext? ctx)
    {
    }
}

/// <summary>
/// A producer stream: the client sends empty "tick" batches, the server emits data until it
/// calls <see cref="OutputCollector.Finish"/>. Mirrors Python's <c>ProducerState</c>.
/// </summary>
public abstract class ProducerState : StreamState
{
    public abstract Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken);

    public sealed override Task ProcessAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken) =>
        ProduceAsync(output, ctx, cancellationToken);
}

/// <summary>
/// An exchange stream: the client sends real data batches, the server replies with exactly one
/// output batch per turn (never calling <see cref="OutputCollector.Finish"/> itself — the client
/// ends the exchange by closing its input stream). Mirrors Python's <c>ExchangeState</c>.
/// </summary>
public abstract class ExchangeState : StreamState
{
    public abstract Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken);

    public sealed override Task ProcessAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken) =>
        ExchangeAsync(input, output, ctx, cancellationToken);
}
