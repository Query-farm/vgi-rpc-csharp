using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
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
}
