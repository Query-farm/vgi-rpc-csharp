using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Reflection;

public sealed class ValueCodecEmptyRowTests
{
    [Fact]
    public void EmptyRow_AllVariableLengthColumnsHaveZeroLength()
    {
        var list = new ListType(new Field("item", StringType.Default, true));
        var map = new MapType(
            new Field("key", StringType.Default, false),
            new Field("value", new ListType(new Field("item", Int32Type.Default, true)), true));
        var nested = new StructType(
            [
                new Field("labels", list, true),
                new Field("attributes", map, true),
            ]);
        var schema = new Schema(
            [
                new Field("tags", list, true),
                new Field("attributes", map, true),
                new Field("nested", nested, true),
            ],
            null);

        using var batch = ValueCodec.EmptyRow(schema);

        Assert.Equal(0, batch.Length);
        Assert.All(batch.Arrays, array => Assert.Equal(batch.Length, array.Length));
        var nestedArray = Assert.IsType<StructArray>(batch.Column(2));
        Assert.All(nestedArray.Fields, array => Assert.Equal(batch.Length, array.Length));
    }

    /// <summary>A dictionary column's empty array must be built from the DECLARED index and value
    /// types. The previous implementation hard-coded Int16 indices and a String dictionary, so any
    /// dictionary whose index width was not 16-bit — a DuckDB ENUM's width tracks its cardinality,
    /// making Int8/UInt8 the common case — threw ArgumentException out of
    /// <c>new DictionaryArray(...)</c>'s own type check. The empty row is what HTTP dispatch writes
    /// as a stream's continuation-token sentinel batch, so this turned every HTTP stream over an
    /// enum-typed schema into a 500.</summary>
    [Theory]
    [InlineData(ArrowTypeId.Int8)]
    [InlineData(ArrowTypeId.UInt8)]
    [InlineData(ArrowTypeId.Int16)]
    [InlineData(ArrowTypeId.UInt16)]
    [InlineData(ArrowTypeId.Int32)]
    [InlineData(ArrowTypeId.Int64)]
    public void EmptyRow_BuildsDictionaryColumnsFromTheirDeclaredIndexType(ArrowTypeId indexTypeId)
    {
        IArrowType indexType = indexTypeId switch
        {
            ArrowTypeId.Int8 => Int8Type.Default,
            ArrowTypeId.UInt8 => UInt8Type.Default,
            ArrowTypeId.Int16 => Int16Type.Default,
            ArrowTypeId.UInt16 => UInt16Type.Default,
            ArrowTypeId.Int32 => Int32Type.Default,
            _ => Int64Type.Default,
        };
        var dictionary = new DictionaryType(indexType, StringType.Default, ordered: false);
        var schema = new Schema([new Field("mood", dictionary, true)], null);

        using var batch = ValueCodec.EmptyRow(schema);

        Assert.Equal(0, batch.Length);
        var column = Assert.IsType<DictionaryArray>(batch.Column(0));
        Assert.Equal(0, column.Length);
        Assert.Equal(indexTypeId, column.Indices.Data.DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, column.Dictionary.Data.DataType.TypeId);
    }

    /// <summary>The dictionary's VALUE type is equally declared, not assumed to be string.</summary>
    [Fact]
    public void EmptyRow_BuildsDictionaryColumnsFromTheirDeclaredValueType()
    {
        var dictionary = new DictionaryType(Int8Type.Default, Int64Type.Default, ordered: false);
        var schema = new Schema([new Field("code", dictionary, true)], null);

        using var batch = ValueCodec.EmptyRow(schema);

        var column = Assert.IsType<DictionaryArray>(batch.Column(0));
        Assert.Equal(ArrowTypeId.Int64, column.Dictionary.Data.DataType.TypeId);
    }

    /// <summary>
    /// The empty row is what HTTP dispatch writes as a stream's continuation-token sentinel batch,
    /// so <see cref="ValueCodec.EmptyRow"/> must cover every type a worker can declare in an OUTPUT
    /// schema — not only the types a reflected RPC parameter can have. It did not, and the gap was
    /// invisible: each unsupported type threw out of the sentinel construction, answered 500, and
    /// DuckDB's sqllogictest runner then SKIPPED rather than failed the statement, because its
    /// `ignore_error_messages` default swallows any error message containing "HTTP". They surfaced
    /// one at a time — a UUID column (fixed-size binary), then UNION, then INTERVAL — which is why
    /// the implementation now dispatches every remaining fixed-width type by LAYOUT rather than by
    /// name.
    ///
    /// <para>Asserted through a real IPC round trip rather than by inspecting the arrays: a batch
    /// that constructs but does not serialize is exactly the failure mode this guards.</para>
    /// </summary>
    [Fact]
    public async Task EmptyRow_RoundTripsThroughIpc_ForEveryOutputColumnTypeAWorkerCanDeclare()
    {
        var unionFields = new[]
        {
            new Field("as_int", Int32Type.Default, true),
            new Field("as_text", StringType.Default, true),
        };
        var schema = new Schema(
            [
                new Field("uuid", new FixedSizeBinaryType(16), true),
                new Field("interval_ym", new IntervalType(IntervalUnit.YearMonth), true),
                new Field("interval_dt", new IntervalType(IntervalUnit.DayTime), true),
                new Field("interval_mdn", new IntervalType(IntervalUnit.MonthDayNanosecond), true),
                new Field("date64", Date64Type.Default, true),
                new Field("time32", new Time32Type(TimeUnit.Millisecond), true),
                new Field("half", HalfFloatType.Default, true),
                new Field("dec256", new Decimal256Type(40, 8), true),
                new Field("nothing", NullType.Default, true),
                new Field("dense_union", new UnionType(unionFields, [0, 1], UnionMode.Dense), true),
                new Field("sparse_union", new UnionType(unionFields, [0, 1], UnionMode.Sparse), true),
                new Field("mood", new DictionaryType(Int8Type.Default, StringType.Default, ordered: false), true),
                new Field("tags", new ListType(new Field("item", StringType.Default, true)), true),
                new Field("vec3", new FixedSizeListType(new Field("item", FloatType.Default, true), 3), true),
                new Field("point", new StructType([new Field("x", DoubleType.Default, true)]), true),
                new Field("attributes", new MapType(
                    new Field("key", StringType.Default, false),
                    new Field("value", Int32Type.Default, true)), true),
            ],
            null);

        using var stream = new MemoryStream();
        await using (var writer = new WireWriter(stream, schema))
        {
            using var batch = ValueCodec.EmptyRow(schema);
            Assert.Equal(0, batch.Length);
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, null));
        }

        stream.Position = 0;
        using var reader = new WireReader(stream);
        var readSchema = await reader.ReadSchemaAsync();
        Assert.Equal(schema.FieldsList.Count, readSchema.FieldsList.Count);
        var read = await reader.ReadNextAsync();
        Assert.NotNull(read);
        using var readBatch = read.Batch;
        Assert.Equal(0, readBatch.Length);
        Assert.Equal(schema.FieldsList.Count, readBatch.ColumnCount);
        Assert.All(readBatch.Arrays, array => Assert.Equal(0, array.Length));
    }
}
