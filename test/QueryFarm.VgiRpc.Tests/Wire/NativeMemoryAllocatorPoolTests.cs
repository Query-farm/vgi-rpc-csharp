using Apache.Arrow.Memory;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Wire;

public sealed class NativeMemoryAllocatorPoolTests
{
    [Fact]
    public void RentedSmallNativeBuffersAreZeroed()
    {
        var allocator = new NativeMemoryAllocator(alignment: 128);

        using (var first = allocator.Allocate(16))
        {
            first.Memory.Span.Fill(0xA5);
        }

        using var second = allocator.Allocate(16);
        Assert.True(second.Memory.Span.SequenceEqual(new byte[16]));
    }
}
