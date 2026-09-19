using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Reflection;

/// <summary>
/// The wide-type shapes the reference suite's <c>TestUnaryWideTypes</c> sends, each of which the
/// codec used to refuse or mangle: <c>fixed_size_binary</c>, width declarations on record fields,
/// set-typed fields, element nullability in a list, dictionary-encoded strings that are not enum
/// members, a <c>pa.Schema</c> field, and decimal/date/timestamp elements inside containers.
/// </summary>
/// <remarks>
/// Each case round-trips through <see cref="ValueCodec.BuildRow"/> and
/// <see cref="ValueCodec.ExtractRow"/> the way a server decodes a request and encodes the echo;
/// a record goes through the top-level binary field, the embedded-IPC encoding the reference uses.
/// </remarks>
public sealed class WideTypeCodecTests
{
    [Fact]
    public void FixedSizeBinary_RoundTrips()
    {
        var schema = new Schema([new Field("value", new FixedSizeBinaryType(8), nullable: false)], null);
        byte[] value = [1, 2, 3, 4, 5, 6, 7, 8];

        using var batch = ValueCodec.BuildRow(schema, [value]);

        Assert.IsType<FixedSizeBinaryType>(batch.Column(0).Data.DataType);
        Assert.Equal(value, ValueCodec.ExtractRow(batch, [typeof(byte[])])[0]);
    }

    [Fact]
    public void FixedSizeBinary_OfTheWrongLength_IsRefused()
    {
        var schema = new Schema([new Field("value", new FixedSizeBinaryType(8), nullable: false)], null);

        Assert.Throws<ArgumentException>(() => ValueCodec.BuildRow(schema, [new byte[7]]));
    }

    public sealed class WidthRecord
    {
        [LargeWidth]
        public string LargeText { get; set; } = "";

        [LargeWidth]
        public byte[] LargeBytes { get; set; } = [];

        [FixedBinary(8)]
        public byte[] Fixed { get; set; } = new byte[8];
    }

    /// <summary>A record's embedded schema is what goes on the wire, so a width declared on one of
    /// its fields has to reach it -- not only the (hash-only) per-property derivation.</summary>
    [Fact]
    public void RecordFieldWidthDeclarations_ReachTheEmbeddedSchema()
    {
        var schema = SchemaDerivation.InnerSchemaFor(typeof(WidthRecord));

        Assert.IsType<LargeStringType>(schema.GetFieldByName("large_text").DataType);
        Assert.IsType<LargeBinaryType>(schema.GetFieldByName("large_bytes").DataType);
        Assert.Equal(8, Assert.IsType<FixedSizeBinaryType>(schema.GetFieldByName("fixed").DataType).ByteWidth);
    }

    [Fact]
    public void RecordWithWidthDeclarations_RoundTrips()
    {
        var record = new WidthRecord { LargeText = "wide", LargeBytes = [9, 9], Fixed = [1, 2, 3, 4, 5, 6, 7, 8] };

        var echoed = RoundTrip(record);

        Assert.Equal(record.LargeText, echoed.LargeText);
        Assert.Equal(record.LargeBytes, echoed.LargeBytes);
        Assert.Equal(record.Fixed, echoed.Fixed);
    }

    public sealed class ContainerRecord
    {
        public HashSet<long> Set { get; set; } = [];

        public List<long?> OptionalElements { get; set; } = [];

        public List<decimal> Decimals { get; set; } = [];

        public List<DateOnly> Dates { get; set; } = [];

        public List<DateTime> Timestamps { get; set; } = [];

        public Dictionary<string, decimal> DecimalsByName { get; set; } = [];

        public List<List<decimal>> NestedDecimals { get; set; } = [];

        public List<DateOnly>? OptionalDates { get; set; }
    }

    [Fact]
    public void ContainersOfWideTypes_RoundTrip()
    {
        var record = new ContainerRecord
        {
            Set = [3, 1, 2],
            OptionalElements = [1, null, 3],
            Decimals = [1.5m, -2.25m],
            Dates = [new DateOnly(2024, 1, 15), new DateOnly(1999, 12, 31)],
            Timestamps = [new DateTime(2024, 1, 15, 10, 30, 0), new DateTime(2000, 2, 29, 23, 59, 59)],
            DecimalsByName = new() { ["a"] = 1.25m, ["b"] = 0m },
            NestedDecimals = [[1.5m], [], [2.5m, 3.5m]],
            OptionalDates = null,
        };

        var echoed = RoundTrip(record);

        Assert.True(record.Set.SetEquals(echoed.Set));
        Assert.Equal(record.OptionalElements, echoed.OptionalElements);
        Assert.Equal(record.Decimals, echoed.Decimals);
        Assert.Equal(record.Dates, echoed.Dates);
        Assert.Equal(record.Timestamps, echoed.Timestamps);
        Assert.Equal(record.DecimalsByName, echoed.DecimalsByName);
        Assert.Equal(record.NestedDecimals, echoed.NestedDecimals);
        Assert.Null(echoed.OptionalDates);
    }

    public sealed class DictionaryEncodedRecord
    {
        [DictionaryEncoded]
        public string Single { get; set; } = "";

        [DictionaryEncoded]
        public List<string> Many { get; set; } = [];
    }

    [Fact]
    public void DictionaryEncodedStrings_AreDictionaryColumns()
    {
        var schema = SchemaDerivation.InnerSchemaFor(typeof(DictionaryEncodedRecord));

        var single = Assert.IsType<DictionaryType>(schema.GetFieldByName("single").DataType);
        Assert.IsType<Int16Type>(single.IndexType);
        Assert.IsType<StringType>(single.ValueType);
        var many = Assert.IsType<ListType>(schema.GetFieldByName("many").DataType);
        Assert.IsType<DictionaryType>(many.ValueDataType);
    }

    /// <summary>Any string, not only the member names of some enum -- "hello" and the empty string
    /// are exactly the values the reference sends.</summary>
    [Fact]
    public void DictionaryEncodedStrings_RoundTripValuesThatAreNotEnumMembers()
    {
        var record = new DictionaryEncodedRecord { Single = "hello", Many = ["a", "", "a", "b"] };

        var echoed = RoundTrip(record);

        Assert.Equal("hello", echoed.Single);
        Assert.Equal(record.Many, echoed.Many);
    }

    public interface IDictionaryEncodedParameter
    {
        [return: DictionaryEncoded]
        Task<string> EchoAsync([DictionaryEncoded] string value);
    }

    [Fact]
    public void DictionaryEncodedParameterAndResult_RoundTrip()
    {
        var method = new RpcMethodInfo(typeof(IDictionaryEncodedParameter).GetMethod(nameof(IDictionaryEncodedParameter.EchoAsync))!);
        Assert.IsType<DictionaryType>(method.ParamsSchema.GetFieldByIndex(0).DataType);
        Assert.IsType<DictionaryType>(method.ResultSchema.GetFieldByIndex(0).DataType);

        using var request = ValueCodec.BuildRow(method.ParamsSchema, ["dictionary-encoded"]);
        Assert.Equal("dictionary-encoded", ValueCodec.ExtractRow(request, method.ParameterTypes)[0]);
    }

    public sealed class SchemaRecord
    {
        public Schema? Described { get; set; }
    }

    [Fact]
    public void SchemaField_IsBinaryAndRoundTrips()
    {
        var described = new Schema(
            [new Field("x", Int64Type.Default, nullable: false), new Field("label", StringType.Default, nullable: true)],
            null);

        Assert.IsType<BinaryType>(SchemaDerivation.InnerSchemaFor(typeof(SchemaRecord)).GetFieldByName("described").DataType);
        var echoed = RoundTrip(new SchemaRecord { Described = described }).Described;

        Assert.NotNull(echoed);
        Assert.Equal(
            described.FieldsList.Select(f => (f.Name, f.DataType.TypeId, f.IsNullable)),
            echoed.FieldsList.Select(f => (f.Name, f.DataType.TypeId, f.IsNullable)));
    }

    private static T RoundTrip<T>(T record)
        where T : class
    {
        var schema = new Schema([SchemaDerivation.FieldFor("data", typeof(T))], null);
        using var batch = ValueCodec.BuildRow(schema, [record]);
        return Assert.IsType<T>(ValueCodec.ExtractRow(batch, [typeof(T)])[0]);
    }
}
