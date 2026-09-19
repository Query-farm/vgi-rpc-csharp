using QueryFarm.VgiRpc.Reflection;

namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>Wide types nested inside containers. Mirrors <c>_types.DeepNested</c>.</summary>
/// <remarks>The two dictionary-encoded fields are strings, not <c>Status</c>: the reference
/// declares <c>Annotated[str, ArrowType(pa.dictionary(pa.int16(), pa.string()))]</c>, an open set
/// of values that merely shares an enum's wire type.</remarks>
public sealed class DeepNested
{
    public List<List<decimal>> ListOfListsDecimal { get; set; } = [];
    public List<DateOnly>? OptionalListDate { get; set; }

    [DictionaryEncoded]
    public string DictEncodedString { get; set; } = "";

    [DictionaryEncoded]
    public List<string> ListOfDictEncoded { get; set; } = [];
}
