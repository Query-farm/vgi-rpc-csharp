namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>Wide types nested inside containers. Mirrors <c>_types.DeepNested</c>.</summary>
public sealed class DeepNested
{
    public List<List<decimal>> ListOfListsDecimal { get; set; } = [];
    public List<DateOnly>? OptionalListDate { get; set; }
    public Status DictEncodedString { get; set; }
    public List<Status> ListOfDictEncoded { get; set; } = [];
}
