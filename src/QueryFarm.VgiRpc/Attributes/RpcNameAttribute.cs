namespace QueryFarm.VgiRpc.Attributes;

/// <summary>
/// Overrides the wire name (method name, parameter name, or record property name) that would
/// otherwise be derived automatically from the idiomatic PascalCase/camelCase C# identifier via
/// <see cref="Reflection.WireNaming"/>. See docs/wire-protocol.md — the wire protocol itself is
/// snake_case; this repo's C# API stays idiomatic and reconciles the two via this attribute plus
/// a deterministic default conversion, rather than using literal snake_case C# identifiers.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class RpcNameAttribute(string wireName) : Attribute
{
    public string WireName { get; } = wireName;
}
