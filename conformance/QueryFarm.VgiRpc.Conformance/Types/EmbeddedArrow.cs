using Apache.Arrow;

namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>A record batch and a schema carried as embedded IPC bytes. Mirrors
/// <c>_types.EmbeddedArrow</c>.</summary>
public sealed class EmbeddedArrow
{
    public RecordBatch? Batch { get; set; }
    public Schema? Schema { get; set; }
}
