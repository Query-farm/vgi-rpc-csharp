using Apache.Arrow;
using QueryFarm.VgiRpc.Logging;

namespace QueryFarm.VgiRpc.Streaming;

/// <summary>
/// Collects the (at most one) output batch and any client-directed log messages a
/// <see cref="StreamState.ProcessAsync"/> call emits for one lockstep turn. Mirrors Python's
/// <c>OutputCollector</c>.
/// </summary>
public sealed class OutputCollector(Schema outputSchema)
{
    private readonly List<LogMessage> _logs = [];

    public Schema OutputSchema { get; } = outputSchema;

    /// <summary>The batch emitted this turn, or <see langword="null"/> if <see cref="Emit"/> wasn't called.</summary>
    public RecordBatch? EmittedBatch { get; private set; }

    /// <summary>True once <see cref="Finish"/> has been called — ends the stream after this turn.</summary>
    public bool Finished { get; private set; }

    internal IReadOnlyList<LogMessage> Logs => _logs;

    /// <summary>Emits this turn's output batch. At most one per turn.</summary>
    public void Emit(RecordBatch batch)
    {
        if (EmittedBatch is not null)
        {
            throw new InvalidOperationException("OutputCollector.Emit() was called more than once in a single turn — a stream turn emits at most one data batch.");
        }

        EmittedBatch = batch;
    }

    /// <summary>Emits a log message to the client, interleaved with this turn's data batch (log batches first).</summary>
    public void ClientLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        _logs.Add(new LogMessage(level, message, extra));

    /// <summary>Ends the stream after this turn (producer streams only — see <see cref="ProducerState"/>).</summary>
    public void Finish() => Finished = true;
}
