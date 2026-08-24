using Apache.Arrow;

namespace QueryFarm.VgiRpc.Wire;

/// <summary>
/// A single Arrow RecordBatch paired with the <c>vgi_rpc.*</c> custom_metadata carried on its
/// IPC Message wrapper. This is the unit every part of the RPC engine (request framing, unary
/// results, stream log/data batches, errors) operates on — never a bare <see cref="RecordBatch"/>.
/// See docs/WIRE_PROTOCOL.md in the canonical Python repo for the full metadata key catalogue.
/// </summary>
/// <param name="Batch">The Arrow record batch.</param>
/// <param name="Metadata">
/// The RecordBatch message's custom_metadata, or <see langword="null"/> if it carried none.
/// Never a schema-level metadata dictionary — see Appendix D of WIRE_PROTOCOL.md.
/// </param>
public sealed record AnnotatedBatch(RecordBatch Batch, IReadOnlyDictionary<string, string>? Metadata)
{
    /// <summary>Looks up a single metadata value, or <see langword="null"/> if absent.</summary>
    public string? GetMetadata(string key) =>
        Metadata is not null && Metadata.TryGetValue(key, out var value) ? value : null;
}
