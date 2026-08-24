namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>Bounding box with nested Point structs. Mirrors <c>_types.BoundingBox</c>.</summary>
public sealed class BoundingBox
{
    public Point TopLeft { get; set; } = new();
    public Point BottomRight { get; set; } = new();
    public string Label { get; set; } = "";
}
