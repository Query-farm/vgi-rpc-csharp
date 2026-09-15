namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>Wide types as container elements and optionals. Mirrors
/// <c>_types.ContainerWideTypes</c>.</summary>
public sealed class ContainerWideTypes
{
    public List<decimal> ListDecimal { get; set; } = [];
    public List<DateOnly> ListDate { get; set; } = [];
    public List<DateTime> ListTimestamp { get; set; } = [];
    public DateOnly? OptionalDate { get; set; }
    public decimal? OptionalDecimal { get; set; }
    public DateTime? OptionalTimestamp { get; set; }
    public Dictionary<string, decimal> DictStrDecimal { get; set; } = [];

    /// <summary>Mirrors Python's <c>frozenset[int]</c> -- Arrow has no set type, so both sides
    /// carry it as a list.</summary>
    public HashSet<long> FrozensetInt { get; set; } = [];

    public List<long> ListOptionalInt { get; set; } = [];
}
