using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace QueryFarm.VgiRpc.Shm;

/// <summary>
/// First-fit allocator whose free/occupied bookkeeping lives entirely in a fixed-size header at
/// the start of a shared memory segment — a byte-for-byte port of the canonical Python repo's
/// <c>vgi_rpc.shm.ShmAllocator</c>. See <c>docs/WIRE_PROTOCOL.md</c> §11 ("Segment header format")
/// for the normative layout; this is the cross-language wire contract every SHM-capable port
/// shares, so field order/width/endianness must match exactly.
///
/// <para>Header layout (all integers little-endian, explicit via
/// <see cref="BinaryPrimitives"/> rather than the view accessor's native-endianness read/write
/// methods — every .NET target this port supports is little-endian in practice, but the wire
/// contract itself is endianness-explicit, so this doesn't rely on that coincidence):</para>
///
/// <code>
/// Offset  Size    Field
/// 0       4       magic: "VGIS"
/// 4       4       version: uint32 = 1
/// 8       8       data_size: uint64 (segment size minus HeaderSize)
/// 16      4       num_allocs: uint32
/// 20      4       padding: uint32 = 0
/// 24      N*16    allocations: (offset: uint64, length: uint64), sorted by offset
/// </code>
///
/// <para>The lockstep RPC protocol guarantees only one side is ever active on a given segment at
/// a time, so — like the Python reference — no OS-level locking guards these header reads/writes.
/// Adjacent free regions coalesce implicitly: only occupied regions are tracked, so the gap
/// between two allocations grows on its own once one of them frees.</para>
/// </summary>
public sealed class ShmAllocator
{
    /// <summary>Total header size in bytes — allocation data starts immediately after.</summary>
    public const long HeaderSize = 65536;

    private const int MagicSize = 4;
    private const int HeaderFixedSize = 24; // magic(4) + version(4) + data_size(8) + num_allocs(4) + padding(4)
    private const int AllocEntrySize = 16; // offset(8) + length(8)

    /// <summary>Maximum number of concurrent allocations the header can hold — <c>(65536 - 24) / 16 = 4094</c>.</summary>
    public const int MaxAllocs = (int)((HeaderSize - HeaderFixedSize) / AllocEntrySize);

    private static readonly byte[] s_magic = "VGIS"u8.ToArray();
    private const uint CurrentVersion = 1;
    private const int WarnThreshold90 = MaxAllocs * 8 / 10; // 80% — matches Python's _WARN_THRESHOLD

    private readonly MemoryMappedViewAccessor _header;
    private readonly long _totalSize;

    private ShmAllocator(MemoryMappedViewAccessor header, long totalSize)
    {
        _header = header;
        _totalSize = totalSize;
    }

    /// <summary>Writes a fresh, empty header (<c>num_allocs = 0</c>) — call once when a new
    /// segment is created, before anyone allocates from it.</summary>
    public static void Initialize(MemoryMappedViewAccessor header, long totalSize)
    {
        var buffer = new byte[HeaderFixedSize];
        s_magic.CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), CurrentVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), (ulong)(totalSize - HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), 0);
        header.WriteArray(0, buffer, 0, buffer.Length);
        header.Flush();
    }

    /// <summary>Attaches to an existing header, validating magic/version/<c>data_size</c> against
    /// <paramref name="totalSize"/> (the kernel-reported segment size, which may differ slightly
    /// from what a caller requested due to page rounding).</summary>
    /// <exception cref="InvalidDataException">Bad magic, unsupported version, or a
    /// <c>data_size</c> that doesn't match <paramref name="totalSize"/> minus the header.</exception>
    public static ShmAllocator Attach(MemoryMappedViewAccessor header, long totalSize)
    {
        var buffer = new byte[HeaderFixedSize];
        header.ReadArray(0, buffer, 0, buffer.Length);
        if (!buffer.AsSpan(0, MagicSize).SequenceEqual(s_magic))
        {
            throw new InvalidDataException($"Bad SHM magic: {Convert.ToHexStringLower(buffer.AsSpan(0, MagicSize))}");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported SHM version: {version}");
        }

        var dataSize = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
        var expectedDataSize = (ulong)(totalSize - HeaderSize);
        if (dataSize != expectedDataSize)
        {
            throw new InvalidDataException($"data_size mismatch: header says {dataSize}, expected {expectedDataSize}");
        }

        return new ShmAllocator(header, totalSize);
    }

    /// <summary>Current number of active allocations.</summary>
    public int NumAllocs => (int)ReadUInt32(16);

    /// <summary>Finds the first gap of at least <paramref name="size"/> bytes and reserves it.
    /// Returns the absolute segment offset, or <see langword="null"/> if nothing fits (including
    /// when the header is already at <see cref="MaxAllocs"/>).</summary>
    public long? Allocate(long size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Allocation size must be positive.");
        }

        var allocs = ReadAllocs();
        if (allocs.Count >= MaxAllocs)
        {
            return null;
        }

        var prevEnd = HeaderSize;
        for (var i = 0; i < allocs.Count; i++)
        {
            var (off, length) = allocs[i];
            var gap = off - prevEnd;
            if (gap >= size)
            {
                allocs.Insert(i, (prevEnd, size));
                WriteAllocs(allocs);
                WarnIfNearLimit(allocs.Count);
                return prevEnd;
            }

            prevEnd = off + length;
        }

        var tailGap = _totalSize - prevEnd;
        if (tailGap >= size)
        {
            allocs.Add((prevEnd, size));
            WriteAllocs(allocs);
            WarnIfNearLimit(allocs.Count);
            return prevEnd;
        }

        return null;
    }

    /// <summary>Removes the allocation entry starting at <paramref name="offset"/>.</summary>
    /// <exception cref="ArgumentException">No allocation starts at that offset.</exception>
    public void Free(long offset)
    {
        var allocs = ReadAllocs();
        for (var i = 0; i < allocs.Count; i++)
        {
            if (allocs[i].Offset == offset)
            {
                allocs.RemoveAt(i);
                WriteAllocs(allocs);
                return;
            }
        }

        throw new ArgumentException($"No allocation at offset {offset}.", nameof(offset));
    }

    /// <summary>Clears every allocation (<c>num_allocs = 0</c>) — for reuse between calls.</summary>
    public void Reset() => WriteUInt32(16, 0);

    private List<(long Offset, long Length)> ReadAllocs()
    {
        var count = (int)ReadUInt32(16);
        var result = new List<(long, long)>(count);
        if (count == 0)
        {
            return result;
        }

        var buffer = new byte[count * AllocEntrySize];
        _header.ReadArray(HeaderFixedSize, buffer, 0, buffer.Length);
        for (var i = 0; i < count; i++)
        {
            var span = buffer.AsSpan(i * AllocEntrySize);
            var off = (long)BinaryPrimitives.ReadUInt64LittleEndian(span);
            var len = (long)BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
            result.Add((off, len));
        }

        return result;
    }

    private void WriteAllocs(List<(long Offset, long Length)> allocs)
    {
        WriteUInt32(16, (uint)allocs.Count);
        var buffer = new byte[allocs.Count * AllocEntrySize];
        for (var i = 0; i < allocs.Count; i++)
        {
            var span = buffer.AsSpan(i * AllocEntrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)allocs[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)allocs[i].Length);
        }

        if (buffer.Length > 0)
        {
            _header.WriteArray(HeaderFixedSize, buffer, 0, buffer.Length);
        }

        _header.Flush();
    }

    private static void WarnIfNearLimit(int count)
    {
        // No logging framework threaded through this low-level class — matches this port's
        // existing posture elsewhere (callers own diagnostics). The Python reference logs a
        // warning at 80% capacity; this port's equivalent hook is left for a future caller that
        // wants it, since MaxAllocs is public and NumAllocs is cheap to poll.
        _ = count >= WarnThreshold90;
    }

    private uint ReadUInt32(int offset)
    {
        var buffer = new byte[4];
        _header.ReadArray(offset, buffer, 0, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private void WriteUInt32(int offset, uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _header.WriteArray(offset, buffer, 0, 4);
        _header.Flush();
    }
}
