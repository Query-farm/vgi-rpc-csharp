using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Wire;

public sealed class LargeBytesBufferTests
{
    [Fact]
    public void SchemaDerivation_MapsBufferToLargeBinary()
    {
        var field = SchemaDerivation.FieldFor("value", typeof(LargeBytesBuffer));

        Assert.IsType<LargeBinaryType>(field.DataType);
    }

    [Fact]
    public void BuildRow_DoesNotCopyManagedPayload()
    {
        var bytes = new byte[] { 1, 2, 3 };
        using var value = new LargeBytesBuffer(bytes);
        var schema = new Schema([new Field("value", LargeBinaryType.Default, false)], null);
        using var batch = ValueCodec.BuildRow(schema, [value]);

        bytes[1] = 42;

        Assert.Equal(new byte[] { 1, 42, 3 }, ((LargeBinaryArray)batch.Column(0)).GetBytes(0).ToArray());
    }

    [Fact]
    public async Task ExtractedValueAndOutputArray_OutliveInputBatch()
    {
        var schema = new Schema([new Field("value", LargeBinaryType.Default, false)], null);
        using var sourceBatch = new RecordBatch(
            schema,
            [new LargeBinaryArray.Builder().Append(new byte[] { 7, 8, 9, 10 }).Build()],
            1);
        await using var stream = new MemoryStream();
        await using (var writer = new WireWriter(stream, schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(sourceBatch, null));
        }

        stream.Position = 0;
        LargeBytesBuffer value;
        using (var reader = new WireReader(stream))
        {
            await reader.ReadSchemaAsync();
            var input = await reader.ReadNextAsync();
            Assert.NotNull(input);
            value = (LargeBytesBuffer)ValueCodec.ExtractRow(
                input!.Batch,
                [typeof(LargeBytesBuffer)])[0]!;
            input.Batch.Dispose();
        }

        using var output = ValueCodec.BuildRow(schema, [value]);
        value.Dispose();

        Assert.Equal(
            new byte[] { 7, 8, 9, 10 },
            ((LargeBinaryArray)output.Column(0)).GetBytes(0).ToArray());
    }

    [Fact]
    public void DisposedValueRejectsAccess()
    {
        var value = new LargeBytesBuffer(new byte[] { 1 });
        value.Dispose();

        Assert.Throws<ObjectDisposedException>(() => value.ToArray());
    }
}
