using Apache.Arrow;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

/// <summary>Transport-independent schema-first client surface.</summary>
public interface IRpcClient : IAsyncDisposable
{
    /// <summary>Calls a unary method with an exact, caller-declared Arrow parameter batch.</summary>
    Task<AnnotatedBatch> CallUnaryAsync(
        string method,
        RecordBatch parameters,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a producer stream whose turns are driven by empty tick batches.</summary>
    Task<IRpcProducerSession> OpenProducerAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a bidirectional lockstep exchange stream.</summary>
    Task<IRpcExchangeSession> OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        Schema inputSchema,
        bool hasHeader = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a dynamic exchange whose first input batch establishes its schema.</summary>
    Task<IRpcExchangeSession> OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Transport-neutral lifecycle and header surface shared by producer and exchange sessions.</summary>
public interface IRpcStreamSession : IAsyncDisposable
{
    AnnotatedBatch? Header { get; }

    THeader GetHeader<THeader>();

    Task CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>Transport-neutral producer stream driven one lockstep turn at a time.</summary>
public interface IRpcProducerSession : IRpcStreamSession
{
    Task<AnnotatedBatch?> ReadNextAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Transport-neutral bidirectional lockstep exchange stream.</summary>
public interface IRpcExchangeSession : IRpcStreamSession
{
    Task<AnnotatedBatch?> ExchangeAsync(
        RecordBatch input,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);
}
