namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>Stream header for conformance testing. Mirrors <c>_types.ConformanceHeader</c>.</summary>
public sealed class ConformanceHeader
{
    public long TotalExpected { get; set; }
    public string Description { get; set; } = "";
}
