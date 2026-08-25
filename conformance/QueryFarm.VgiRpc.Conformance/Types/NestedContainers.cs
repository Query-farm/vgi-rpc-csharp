using Apache.Arrow;

namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>Enums and dataclasses nested in containers and annotation wrappers. Mirrors
/// <c>_types.NestedContainers</c>. <c>TaggedStatus</c>/<c>TaggedPoint</c>/<c>TaggedBatch</c>
/// mirror Python's <c>Annotated[T, "conformance-tag"] | None</c> fields — the bare string
/// annotation carries no wire meaning (only an <c>ArrowType</c> marker does; see
/// <c>_infer_arrow_type</c>), so these are plain nullable fields here.</summary>
public sealed class NestedContainers
{
    public List<Status> Statuses { get; set; } = [];
    public List<Point> Points { get; set; } = [];
    public Dictionary<string, Status> StatusByName { get; set; } = [];

    /// <summary>Mirrors Python's <c>frozenset[Status]</c> — Arrow has no native set type, so both
    /// sides carry it as a <c>list</c> on the wire (see <see cref="Reflection.SchemaDerivation"/>'s
    /// HashSet-maps-to-list rule); the receiving side reconstructs an actual set from it.</summary>
    public HashSet<Status> FrozenStatuses { get; set; } = [];

    public Status? TaggedStatus { get; set; }
    public Point? TaggedPoint { get; set; }

    /// <summary>Mirrors Python's <c>Annotated[pa.RecordBatch, ArrowType(pa.binary())] | None</c> —
    /// a RecordBatch carried as embedded IPC bytes (see <see cref="Reflection.ValueCodec"/>'s
    /// RecordBatch-as-binary build/extract helpers).</summary>
    public RecordBatch? TaggedBatch { get; set; }
}
