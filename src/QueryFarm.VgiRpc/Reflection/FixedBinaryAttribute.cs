namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Declares that a <c>byte[]</c> is Arrow's fixed-width <c>fixed_size_binary(n)</c> rather than
/// the variable-width <c>binary</c> a CLR byte array would otherwise infer.
/// </summary>
/// <remarks>
/// The two are different Arrow types, so a protocol that declares one and a port that emits the
/// other do not speak the same protocol -- a difference invisible to any comparison of method
/// names, and one the conformance assertions cannot see either, because the same bytes round
/// trip through both.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Property)]
public sealed class FixedBinaryAttribute(int byteWidth) : Attribute
{
    /// <summary>The fixed width, in bytes.</summary>
    public int ByteWidth { get; } = byteWidth;
}
