using Apache.Arrow;

namespace QueryFarm.VgiRpc.Streaming;

/// <summary>
/// Non-generic view of an <see cref="RpcStream{TState}"/> — lets <c>RpcServer</c> dispatch a
/// stream method without needing to know its concrete <c>TState</c> at compile time.
/// </summary>
public interface IRpcStream
{
    /// <summary>Schema of the batches the server emits.</summary>
    Schema OutputSchema { get; }

    /// <summary>Schema of the batches the client sends, or <see langword="null"/>/empty for a
    /// producer stream (client sends empty "tick" batches instead — see
    /// <see cref="Streaming.ProducerState"/>).</summary>
    Schema? InputSchema { get; }

    StreamState State { get; }
}

/// <summary>
/// A service method returns this to declare a streaming RPC call — construction just
/// describes the stream; <see cref="State"/>'s <see cref="StreamState.ProcessAsync"/> does the
/// actual per-turn work, invoked by the server's dispatch loop once per lockstep turn. Mirrors
/// Python's <c>Stream[StreamState]</c>. Named <c>RpcStream</c>, not <c>Stream</c>, to avoid
/// colliding with <see cref="System.IO.Stream"/>.
/// </summary>
public sealed record RpcStream<TState>(Schema OutputSchema, TState State, Schema? InputSchema = null) : IRpcStream
    where TState : StreamState
{
    StreamState IRpcStream.State => State;
}
