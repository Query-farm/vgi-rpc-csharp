using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Wire;

public sealed class EmptyStructRoundTripTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyStruct_PreservesFieldsRowsAndNullsThroughIpc(bool nested)
    {
        var emptyType = new StructType([]);
        var field = new Field("empty", emptyType, nullable: true);
        var emptyArray = new StructArray(emptyType, length: 2, [], new ArrowBuffer(new byte[] { 1 }), nullCount: 1);
        IArrowArray column = emptyArray;
        if (nested)
        {
            var parentType = new StructType([field]);
            field = new Field("parent", parentType, nullable: false);
            column = new StructArray(parentType, length: 2, [emptyArray], default);
        }

        var schema = new Schema([field], metadata: null);
        using var batch = new RecordBatch(schema, [column], length: 2);
        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true))
        {
            await writer.WriteRecordBatchAsync(batch);
            await writer.WriteEndAsync();
        }

        stream.Position = 0;
        using var reader = new ArrowStreamReader(stream);
        using var decoded = await reader.ReadNextRecordBatchAsync();
        Assert.NotNull(decoded);
        Assert.Equal(2, decoded.Length);
        var result = Assert.IsType<StructArray>(decoded.Column(0));
        if (nested)
        {
            result = Assert.IsType<StructArray>(Assert.Single(result.Fields));
        }
        Assert.Empty(Assert.IsType<StructType>(result.Data.DataType).Fields);
        Assert.Empty(result.Fields);
        Assert.Equal(2, result.Length);
        Assert.Equal(1, result.NullCount);
        Assert.True(result.IsValid(0));
        Assert.True(result.IsNull(1));
    }

    [Fact]
    public void EmptyStruct_DoesNotRelaxNullOrOtherNestedTypeValidation()
    {
        Assert.Throws<ArgumentNullException>(() => new StructType(null!));
        Assert.Throws<ArgumentNullException>(() => new UnionType([], [], UnionMode.Dense));
    }
}
