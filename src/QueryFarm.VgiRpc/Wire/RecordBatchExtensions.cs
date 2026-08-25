using Apache.Arrow;

namespace QueryFarm.VgiRpc.Wire;

/// <summary>Small <see cref="RecordBatch"/> helpers shared across the size-threshold checks that
/// gate both external-storage (<c>QueryFarm.VgiRpc.Http.ExternalLocation</c>, M13) and SHM
/// (<c>QueryFarm.VgiRpc.Shm</c>, M14) offload — both need a fast "is this batch big enough to be
/// worth moving off the main channel" estimate, and both want the identical estimate so a batch
/// that would offload to one channel behaves consistently if reconfigured to use the other. Lives
/// in core (not either offload feature's own project) since both are separate assemblies with no
/// dependency on each other.</summary>
public static class RecordBatchExtensions
{
    /// <summary>Approximates <paramref name="batch"/>'s total buffer size — the C# stand-in for
    /// pyarrow's <c>RecordBatch.get_total_buffer_size()</c>, which <c>Apache.Arrow</c> (the .NET
    /// package) doesn't expose. Sums every column's <see cref="ArrowBuffer"/> lengths, recursing
    /// into <see cref="ArrayData.Children"/> for nested types (list/struct/dictionary). A fast
    /// O(columns) estimate, not an exact wire-byte count (matches Python's own stated
    /// "fast O(1) estimate" framing for this check).</summary>
    public static long GetTotalBufferSize(this RecordBatch batch)
    {
        long total = 0;
        for (var i = 0; i < batch.ColumnCount; i++)
        {
            total += SumBufferSizes(batch.Column(i).Data);
        }

        return total;

        static long SumBufferSizes(ArrayData data)
        {
            long sum = 0;
            if (data.Buffers is not null)
            {
                foreach (var buffer in data.Buffers)
                {
                    sum += buffer.Length;
                }
            }

            if (data.Children is not null)
            {
                foreach (var child in data.Children)
                {
                    sum += SumBufferSizes(child);
                }
            }

            return sum;
        }
    }
}
