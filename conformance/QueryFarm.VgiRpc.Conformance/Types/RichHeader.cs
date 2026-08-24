namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>Multi-type stream header for cross-language conformance testing. Mirrors <c>_types.RichHeader</c>.</summary>
public sealed class RichHeader
{
    public string StrField { get; set; } = "";
    public byte[] BytesField { get; set; } = [];
    public long IntField { get; set; }
    public double FloatField { get; set; }
    public bool BoolField { get; set; }
    public List<long> ListOfInt { get; set; } = [];
    public List<string> ListOfStr { get; set; } = [];
    public Dictionary<string, long> DictField { get; set; } = [];
    public Status EnumField { get; set; }
    public Point NestedPoint { get; set; } = new();
    public string? OptionalStr { get; set; }
    public long? OptionalInt { get; set; }
    public Point? OptionalNested { get; set; }
    public List<Point> ListOfNested { get; set; } = [];
    public List<List<long>> NestedList { get; set; } = [];
    public int AnnotatedInt32 { get; set; }
    public float AnnotatedFloat32 { get; set; }
    public Dictionary<string, string> DictStrStr { get; set; } = [];
}
