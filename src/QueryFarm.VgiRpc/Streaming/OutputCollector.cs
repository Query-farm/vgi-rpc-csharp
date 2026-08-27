using Apache.Arrow;
using QueryFarm.VgiRpc.Logging;

namespace QueryFarm.VgiRpc.Streaming;

/// <summary>
/// Collects the (at most one) output batch and any client-directed log messages a
/// <see cref="StreamState.ProcessAsync"/> call emits for one lockstep turn. Mirrors Python's
/// <c>OutputCollector</c>.
/// </summary>
public sealed class OutputCollector(Schema outputSchema) : IDisposable
{
    private readonly List<LogMessage> _logs = [];

    public Schema OutputSchema { get; } = outputSchema;

    /// <summary>This turn's INCOMING batch's custom_metadata (e.g. an application-level protocol
    /// riding control data on an otherwise-empty producer "tick" batch — VGI's dynamic Top-N
    /// filter tightening is exactly this), or <see langword="null"/> if the input batch carried
    /// none. Populated by the framework before <see cref="StreamState.ProcessAsync"/> runs; for a
    /// <see cref="ProducerState"/> subclass this is the only way to see the tick's metadata, since
    /// <see cref="ProducerState.ProduceAsync"/>'s signature doesn't otherwise expose the input
    /// batch (an <see cref="ExchangeState"/> subclass already receives it directly via
    /// <see cref="ExchangeState.ExchangeAsync"/>'s own <c>input</c> parameter and doesn't need this).
    /// The setter is public (not internal) so a downstream <see cref="ExchangeState"/> consumer whose
    /// OWN per-turn callback signature doesn't carry the <see cref="Wire.AnnotatedBatch"/> through
    /// (e.g. VGI's <c>TableInOutExchangeStreamState</c>, which only hands its
    /// <c>ITableInOutProcessor.Process</c> callback the raw <see cref="RecordBatch"/>) can still
    /// thread the turn's incoming metadata onto this collector itself before invoking that callback —
    /// mirroring exactly what <see cref="ProducerState"/> does above.</summary>
    public IReadOnlyDictionary<string, string>? InputMetadata { get; set; }

    /// <summary>The batch emitted this turn, or <see langword="null"/> if <see cref="Emit(RecordBatch)"/>
    /// wasn't called. Calling <see cref="Emit(RecordBatch)"/> transfers ownership to the collector;
    /// callers must not dispose the batch afterwards.</summary>
    public RecordBatch? EmittedBatch { get; private set; }

    /// <summary>The custom_metadata to carry on <see cref="EmittedBatch"/>'s IPC Message wrapper
    /// (see <see cref="Wire.AnnotatedBatch"/>), or <see langword="null"/> if none was supplied to
    /// <see cref="Emit(RecordBatch, IReadOnlyDictionary{string, string}?)"/>. Application-level
    /// control keys (e.g. a higher-level protocol's own per-batch cache-control metadata) live
    /// here, distinct from the framework's own SHM-pointer-batch metadata, which <see cref="Server.RpcServer"/>
    /// merges in separately when writing the batch to the wire.</summary>
    public IReadOnlyDictionary<string, string>? EmittedMetadata { get; private set; }

    /// <summary>True once <see cref="Finish"/> has been called — ends the stream after this turn.</summary>
    public bool Finished { get; private set; }

    internal IReadOnlyList<LogMessage> Logs => _logs;

    /// <summary>Emits this turn's output batch. At most one per turn.</summary>
    public void Emit(RecordBatch batch) => Emit(batch, metadata: null);

    /// <summary>Emits this turn's output batch, carrying <paramref name="metadata"/> as the batch's
    /// IPC Message custom_metadata (e.g. an application-level cache-control key). At most one
    /// <see cref="Emit(RecordBatch)"/>/<see cref="Emit(RecordBatch, IReadOnlyDictionary{string, string}?)"/>
    /// call per turn.</summary>
    public void Emit(RecordBatch batch, IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (EmittedBatch is not null)
        {
            throw new InvalidOperationException("OutputCollector.Emit() was called more than once in a single turn — a stream turn emits at most one data batch.");
        }

        EmittedBatch = batch;
        EmittedMetadata = metadata;
    }

    /// <summary>Transfers ownership of the emitted batch to the framework's wire layer.</summary>
    internal RecordBatch? DetachEmittedBatch()
    {
        var batch = EmittedBatch;
        EmittedBatch = null;
        return batch;
    }

    /// <summary>Emits a log message to the client, interleaved with this turn's data batch (log batches first).</summary>
    public void ClientLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        _logs.Add(new LogMessage(level, message, extra));

    /// <summary>Ends the stream after this turn (producer streams only — see <see cref="ProducerState"/>).</summary>
    public void Finish() => Finished = true;

    public void Dispose()
    {
        EmittedBatch?.Dispose();
        EmittedBatch = null;
    }
}
