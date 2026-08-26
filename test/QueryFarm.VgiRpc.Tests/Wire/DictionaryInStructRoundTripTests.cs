using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Wire;

/// <summary>
/// Regression coverage for a real bug found this session in the vendored
/// <c>third_party/apache-arrow-dotnet</c> fork's <c>ArrowStreamWriter</c> — see "Fifth patch" in
/// that directory's README.md for the full root-cause writeup. In short:
/// <see cref="ArrowStreamWriter"/> assigns dictionary ids while writing the schema by walking its
/// own canonical <c>Schema</c> property, but used to assign/look up ids for the actual DATA batch
/// by walking that SPECIFIC BATCH's own (possibly independently-constructed) schema instead —
/// and <c>Field</c> has no value-equality override, so two structurally-identical-but-different-
/// instance schemas got DIFFERENT dictionary ids, corrupting the stream. This only reliably
/// reproduced for a dictionary-encoded (enum) column NESTED INSIDE A STRUCT — a top-level
/// dictionary column's schema/batch <see cref="Field"/> objects usually happen to be literally the
/// same instance in real usage.
///
/// This test constructs the exact failure shape directly: two SEPARATELY-BUILT
/// <see cref="Schema"/> instances describing the identical struct-of-dictionary type, writes a
/// batch built against one against a writer configured with the OTHER (simulating an "echo"
/// worker re-emitting a batch it just decoded off the wire, whose schema is a fresh object graph
/// distinct from whatever schema the outgoing stream was set up with) — and asserts the round
/// trip produces the correct value rather than throwing or silently reading back something else.
/// </summary>
public sealed class DictionaryInStructRoundTripTests
{
    private static Schema BuildSchema()
    {
        var dictType = new DictionaryType(new Int16Type(), new StringType(), ordered: false);
        var structType = new StructType([new Field("state", dictType, nullable: true)]);
        return new Schema([new Field("s", structType, nullable: true)], metadata: null);
    }

    private static RecordBatch BuildBatch(Schema schema)
    {
        var dictType = (DictionaryType)((StructType)schema.GetFieldByIndex(0).DataType).Fields[0].DataType;

        var indices = new Int16Array.Builder().Append(0).Build();
        var dictionaryValues = new StringArray.Builder().Append("happy").Build();
        var dictionaryArray = new DictionaryArray(dictType, indices, dictionaryValues);

        var structType = (StructType)schema.GetFieldByIndex(0).DataType;
        var structArray = new StructArray(structType, length: 1, [dictionaryArray], default);

        return new RecordBatch(schema, [structArray], 1);
    }

    [Fact]
    public async Task WritingABatchAgainstAStructurallyIdenticalButDistinctSchema_RoundTripsCorrectly()
    {
        // Two independently-built Schema objects describing the identical type — NOT the same
        // instance, and neither is the other's Field objects. Mirrors "the writer's configured
        // output schema" vs. "the specific batch's own (freshly-decoded) schema" in the real bug.
        var writerSchema = BuildSchema();
        var batchSchema = BuildSchema();
        Assert.NotSame(writerSchema, batchSchema);
        Assert.NotSame(writerSchema.GetFieldByIndex(0), batchSchema.GetFieldByIndex(0));

        var batch = BuildBatch(batchSchema);

        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, writerSchema, leaveOpen: true))
        {
            await writer.WriteRecordBatchAsync(batch);
        }

        stream.Position = 0;
        using var reader = new ArrowStreamReader(stream);
        var readBack = await reader.ReadNextRecordBatchAsync();

        Assert.NotNull(readBack);
        var structArray = (StructArray)readBack!.Column(0);
        var dictionaryArray = (DictionaryArray)structArray.Fields[0];
        var values = (StringArray)dictionaryArray.Dictionary;
        var indexArray = (Int16Array)dictionaryArray.Indices;

        Assert.Equal("happy", values.GetString(indexArray.GetValue(0)!.Value));
    }
}
