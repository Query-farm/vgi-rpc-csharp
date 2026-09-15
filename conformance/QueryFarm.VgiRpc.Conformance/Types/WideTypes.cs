using QueryFarm.VgiRpc.Reflection;

namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>Every Arrow type wider than the common set, in one record. Mirrors
/// <c>_types.WideTypes</c>.</summary>
/// <remarks>
/// The width declarations are not decoration: <c>large_string</c> and <c>string</c>, and
/// <c>fixed_size_binary(8)</c> and <c>binary</c>, are different Arrow types. A port that infers
/// the narrow one from the CLR type describes a protocol the reference does not have, and the
/// same values round trip through either -- so only the protocol hash catches it.
/// </remarks>
public sealed class WideTypes
{
    public sbyte Int8Field { get; set; }
    public short Int16Field { get; set; }
    public int Int32Field { get; set; }
    public byte Uint8Field { get; set; }
    public ushort Uint16Field { get; set; }
    public uint Uint32Field { get; set; }
    public ulong Uint64Field { get; set; }
    public float Float32Field { get; set; }
    public DateOnly DateField { get; set; }
    public DateTime TimestampField { get; set; }
    public DateTimeOffset TimestampUtcField { get; set; }
    public TimeOnly TimeField { get; set; }
    public TimeSpan DurationField { get; set; }
    public decimal DecimalField { get; set; }

    [LargeWidth]
    public string LargeStringField { get; set; } = "";

    [LargeWidth]
    public byte[] LargeBinaryField { get; set; } = [];

    [FixedBinary(8)]
    public byte[] FixedBinaryField { get; set; } = new byte[8];
}
