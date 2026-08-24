using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Wire;

/// <summary>
/// Milestone 0 exit-criteria tests: confirms the vendored, patched Apache.Arrow (see
/// third_party/apache-arrow-dotnet/README.md) round-trips per-batch custom_metadata correctly
/// through <see cref="WireWriter"/>/<see cref="WireReader"/>, and that its schema-message
/// encoding matches the canonical Python implementation byte-for-byte for the one fixture
/// WIRE_PROTOCOL.md hard-codes (Appendix C).
/// </summary>
public sealed class WireWriterReaderTests
{
    [Fact]
    public async Task EmptySchema_MatchesCanonicalFixtureBytes()
    {
        var schema = new Schema.Builder().Build();

        await using var stream = new MemoryStream();
        var writer = new WireWriter(stream, schema);
        await writer.WriteStartAsync();

        Assert.Equal(WireConstants.EmptySchemaFixture, stream.ToArray());
    }

    [Fact]
    public async Task RoundTrip_PreservesCustomMetadataAndData()
    {
        var schema = new Schema.Builder()
            .Field(f => f.Name("value").DataType(Int64Type.Default).Nullable(false))
            .Build();

        var batch = new RecordBatch.Builder()
            .Append("value", false, col => col.Int64(arr => arr.AppendRange([1, 2, 3])))
            .Build();

        var metadata = new Dictionary<string, string>
        {
            ["vgi_rpc.method"] = "echo_int",
            ["vgi_rpc.request_version"] = "1",
        };

        await using var stream = new MemoryStream();

        await using (var writer = new WireWriter(stream, schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, metadata));
        }

        stream.Position = 0;
        using var reader = new WireReader(stream);
        var readSchema = await reader.ReadSchemaAsync();
        Assert.Equal(schema.FieldsList.Count, readSchema.FieldsList.Count);

        var read = await reader.ReadNextAsync();
        Assert.NotNull(read);
        Assert.Equal(metadata, read!.Metadata);

        var values = (Int64Array)read.Batch.Column(0);
        Assert.Equal([1L, 2L, 3L], values.Values.ToArray());

        Assert.Null(await reader.ReadNextAsync());
    }

    [Fact]
    public async Task RoundTrip_NoMetadata_ReturnsNullMetadata()
    {
        var schema = new Schema.Builder()
            .Field(f => f.Name("value").DataType(BooleanType.Default).Nullable(false))
            .Build();

        var batch = new RecordBatch.Builder()
            .Append("value", false, col => col.Boolean(arr => arr.AppendRange([true, false])))
            .Build();

        await using var stream = new MemoryStream();
        await using (var writer = new WireWriter(stream, schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, null));
        }

        stream.Position = 0;
        using var reader = new WireReader(stream);
        await reader.ReadSchemaAsync();
        var read = await reader.ReadNextAsync();

        Assert.NotNull(read);
        Assert.Null(read!.Metadata);
        Assert.Null(read.GetMetadata("anything"));
    }

    [Fact]
    public async Task RoundTrip_AcrossTypeMappingTable_MatchesWireProtocolSpec()
    {
        // string->utf8, bytes->binary, int->int64, float->float64, bool->bool, list->list
        // per the type-mapping table in WIRE_PROTOCOL.md.
        var schema = new Schema.Builder()
            .Field(f => f.Name("s").DataType(StringType.Default).Nullable(false))
            .Field(f => f.Name("b").DataType(BinaryType.Default).Nullable(false))
            .Field(f => f.Name("i").DataType(Int64Type.Default).Nullable(false))
            .Field(f => f.Name("f").DataType(DoubleType.Default).Nullable(false))
            .Field(f => f.Name("bo").DataType(BooleanType.Default).Nullable(false))
            .Field(f => f.Name("l").DataType(new ListType(Int64Type.Default)).Nullable(false))
            .Build();

        var listBuilder = new ListArray.Builder(Int64Type.Default);
        var listValues = (Int64Array.Builder)listBuilder.ValueBuilder;
        listBuilder.Append();
        listValues.Append(10).Append(20);
        listBuilder.Append();
        listValues.Append(30);
        var listArray = listBuilder.Build();

        var batch = new RecordBatch.Builder()
            .Append("s", false, col => col.String(arr => arr.Append("hello").Append("world")))
            .Append("b", false, col => col.Binary(arr => arr.Append([1, 2, 3]).Append([4, 5])))
            .Append("i", false, col => col.Int64(arr => arr.AppendRange([100, 200])))
            .Append("f", false, col => col.Double(arr => arr.AppendRange([1.5, 2.5])))
            .Append("bo", false, col => col.Boolean(arr => arr.AppendRange([true, false])))
            .Append("l", false, listArray)
            .Build();

        var metadata = new Dictionary<string, string> { ["vgi_rpc.log_level"] = "INFO" };

        await using var stream = new MemoryStream();
        await using (var writer = new WireWriter(stream, schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, metadata));
        }

        stream.Position = 0;
        using var reader = new WireReader(stream);
        await reader.ReadSchemaAsync();
        var read = await reader.ReadNextAsync();

        Assert.NotNull(read);
        Assert.Equal("INFO", read!.GetMetadata("vgi_rpc.log_level"));

        var strings = (StringArray)read.Batch.Column(0);
        Assert.Equal(["hello", "world"], [strings.GetString(0), strings.GetString(1)]);
        Assert.Equal([100L, 200L], ((Int64Array)read.Batch.Column(2)).Values.ToArray());
        Assert.Equal([1.5, 2.5], ((DoubleArray)read.Batch.Column(3)).Values.ToArray());
        Assert.Equal<bool?>([true, false], (BooleanArray)read.Batch.Column(4));

        var readList = (ListArray)read.Batch.Column(5);
        var readListValues = (Int64Array)readList.Values;
        Assert.Equal(0, readList.ValueOffsets[0]);
        Assert.Equal(2, readList.ValueOffsets[1] - readList.ValueOffsets[0]);
        Assert.Equal([10L, 20L, 30L], readListValues.Values.ToArray());
    }

    [Fact]
    public async Task MultipleBatchesThenEos_ReaderStopsExactlyAtEos()
    {
        var schema = new Schema.Builder()
            .Field(f => f.Name("value").DataType(Int64Type.Default).Nullable(false))
            .Build();

        RecordBatch MakeBatch(long value) =>
            new RecordBatch.Builder().Append("value", false, col => col.Int64(arr => arr.Append(value))).Build();

        await using var stream = new MemoryStream();
        await using (var writer = new WireWriter(stream, schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(MakeBatch(1), null));
            await writer.WriteBatchAsync(new AnnotatedBatch(MakeBatch(2), null));
        }

        stream.Position = 0;
        using var reader = new WireReader(stream);
        await reader.ReadSchemaAsync();

        var first = await reader.ReadNextAsync();
        var second = await reader.ReadNextAsync();
        var third = await reader.ReadNextAsync();

        Assert.Equal(1L, ((Int64Array)first!.Batch.Column(0)).Values[0]);
        Assert.Equal(2L, ((Int64Array)second!.Batch.Column(0)).Values[0]);
        Assert.Null(third);
    }
}
