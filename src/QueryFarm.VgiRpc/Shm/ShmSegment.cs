using System.IO.MemoryMappedFiles;
using Apache.Arrow;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Shm;

/// <summary>
/// A shared memory segment for zero-copy Arrow IPC batch transfer between co-located processes —
/// a port of the canonical Python repo's <c>vgi_rpc.shm.ShmSegment</c>. See
/// <c>docs/roadmap.md</c> M14 for the platform-primitive finding this class embodies.
///
/// <para><b>Platform split (empirically verified, not guessed)</b>: named, backing-file-less
/// <see cref="MemoryMappedFile.CreateOrOpen(string, long)"/> throws
/// <see cref="PlatformNotSupportedException"/> ("Named maps are not supported") on Linux —
/// confirmed via a real linux/amd64 Docker container, not inferred from documentation. Windows
/// uses that named/backing-file-less API directly; Linux uses an explicit
/// <c>/dev/shm/&lt;name&gt;</c>-backed <see cref="MemoryMappedFile.CreateFromFile(string, FileMode, string?, long, MemoryMappedFileAccess)"/>
/// instead, which round-trips cross-process correctly (also verified in Docker) — this matches
/// where Python's own <c>multiprocessing.shared_memory.SharedMemory</c> places its segments on
/// Linux (confirmed by direct inspection: it creates a real file at exactly
/// <c>/dev/shm/&lt;name&gt;</c>, no extra prefix), so a segment either side creates is directly
/// attachable by the other. macOS (not a supported deployment target per CLAUDE.md, but where
/// this port's own dev loop runs) falls back to a plain temp-directory-backed file — functional
/// for local testing, not a production code path.</para>
///
/// <para><b>Segment identity is cross-process, not cross-language-name-scheme</b>: whichever side
/// creates the segment picks the name and advertises it in
/// <see cref="Wire.MetadataKeys.ShmSegmentName"/>/<see cref="Wire.MetadataKeys.ShmSegmentSize"/>;
/// the other side always attaches by that exact name. This port's <see cref="Create"/> mints its
/// own name (<c>vgi-rpc-&lt;guid&gt;</c>) for when this port is the creating side; when this port
/// is the attaching side (the only role the conformance worker plays today — see M14's roadmap
/// note on client-side SHM being out of scope), the name comes from the peer's request metadata
/// unchanged, whatever scheme minted it (e.g. Python's <c>psm_&lt;hex&gt;</c>).</para>
///
/// <para><b>Lifecycle</b>: the segment's OS-level cleanup is the CREATOR's responsibility
/// (<see cref="Unlink"/>) — an attaching side (<see cref="Attach"/>) only ever closes its own
/// handle (<see cref="Dispose"/>), matching Python's <c>track=False</c> dynamic-attach posture:
/// it must never delete a segment it doesn't own. On Windows, a backing-file-less named section
/// is destroyed automatically once every handle (across every process) closes, so
/// <see cref="Unlink"/> is a no-op there; on Linux/macOS it deletes the backing file explicitly.
/// </para>
/// </summary>
public sealed class ShmSegment : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _headerAccessor;
    private readonly ShmAllocator _allocator;
    private readonly string? _backingFilePath; // null on Windows (no backing file to unlink)
    private bool _disposed;

    private ShmSegment(MemoryMappedFile mmf, MemoryMappedViewAccessor headerAccessor, ShmAllocator allocator, string name, long size, string? backingFilePath)
    {
        _mmf = mmf;
        _headerAccessor = headerAccessor;
        _allocator = allocator;
        Name = name;
        Size = size;
        _backingFilePath = backingFilePath;
    }

    /// <summary>The OS name of this segment — what a peer needs to <see cref="Attach"/> to it.</summary>
    public string Name { get; }

    /// <summary>Total segment size in bytes, exactly as requested (this port's file-backed and
    /// named-map implementations both report the exact size — see this class's doc comment on
    /// why no page-rounding surprise applies here the way it can with Python's allocator on
    /// macOS).</summary>
    public long Size { get; }

    /// <summary>Creates a new segment with a freshly minted name and an initialized (empty)
    /// allocator header. Not exercised by the conformance worker today (it only ever attaches to
    /// a peer-created segment — see this class's doc comment), but implemented for symmetry and
    /// for this port's own tests to create real segments to attach to.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not larger than
    /// <see cref="ShmAllocator.HeaderSize"/>.</exception>
    public static ShmSegment Create(long size)
    {
        if (size <= ShmAllocator.HeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"Segment size must be > {ShmAllocator.HeaderSize}, got {size}.");
        }

        var name = $"vgi-rpc-{Guid.NewGuid():N}";
        var (mmf, backingFilePath) = CreateBacking(name, size);
        var headerAccessor = mmf.CreateViewAccessor(0, ShmAllocator.HeaderSize, MemoryMappedFileAccess.ReadWrite);
        try
        {
            ShmAllocator.Initialize(headerAccessor, size);
            var allocator = ShmAllocator.Attach(headerAccessor, size);
            return new ShmSegment(mmf, headerAccessor, allocator, name, size, backingFilePath);
        }
        catch
        {
            headerAccessor.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    /// <summary>Attaches to an existing segment by name, validating the allocator header. This is
    /// the role the conformance worker actually plays: a peer client creates and owns the
    /// segment, advertises it in request metadata, and this port's server dynamically attaches
    /// for the duration of that request/turn.</summary>
    /// <param name="name">The peer-advertised segment name.</param>
    /// <param name="size">The peer-advertised segment size — trusted as-is (see this class's doc
    /// comment on why no independent size discovery is needed on this port's platforms).</param>
    /// <exception cref="InvalidDataException">The header's magic/version/data_size don't check
    /// out — see <see cref="ShmAllocator.Attach"/>.</exception>
    public static ShmSegment Attach(string name, long size)
    {
        var mmf = OpenBacking(name, size, out var backingFilePath);
        var headerAccessor = mmf.CreateViewAccessor(0, ShmAllocator.HeaderSize, MemoryMappedFileAccess.ReadWrite);
        try
        {
            var allocator = ShmAllocator.Attach(headerAccessor, size);
            return new ShmSegment(mmf, headerAccessor, allocator, name, size, backingFilePath);
        }
        catch
        {
            headerAccessor.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    private static (MemoryMappedFile Mmf, string? BackingFilePath) CreateBacking(string name, long size)
    {
        if (OperatingSystem.IsWindows())
        {
            return (MemoryMappedFile.CreateOrOpen(name, size), null);
        }

        var path = BackingFilePath(name);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            fs.SetLength(size);
        }

        return (MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, size, MemoryMappedFileAccess.ReadWrite), path);
    }

    private static MemoryMappedFile OpenBacking(string name, long size, out string? backingFilePath)
    {
        if (OperatingSystem.IsWindows())
        {
            backingFilePath = null;
            return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        }

        var path = BackingFilePath(name);
        backingFilePath = path;
        return MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, size, MemoryMappedFileAccess.ReadWrite);
    }

    /// <summary>Where a non-Windows segment's backing file lives — real <c>tmpfs</c> under
    /// <c>/dev/shm</c> on Linux (matching where Python's own <c>SharedMemory</c> places its
    /// segments, confirmed by direct inspection — no extra prefix), a plain temp-directory file
    /// on any other non-Windows OS (macOS dev-loop fallback only, not a supported target).</summary>
    private static string BackingFilePath(string name) =>
        OperatingSystem.IsLinux() ? $"/dev/shm/{name}" : Path.Combine(Path.GetTempPath(), name);

    /// <summary>Serializes <paramref name="batch"/> directly into the segment, allocating just
    /// enough space first. Non-dictionary-encoded batches are written as a complete standalone
    /// IPC stream (schema + batch + EOS) straight into the mapped memory via
    /// <see cref="MemoryMappedFile.CreateViewStream(long, long, MemoryMappedFileAccess)"/> handed
    /// to <see cref="WireWriter"/> — no intermediate buffer. Dictionary-encoded batches (this
    /// port's enum columns) go through a two-step buffer-then-copy path since only the dictionary
    /// + record-batch messages are stored, not the schema/EOS framing (see
    /// <c>docs/WIRE_PROTOCOL.md</c> §11 "Batch serialization in SHM").</summary>
    /// <returns><c>(offset, length)</c>, or <see langword="null"/> if nothing free fits.</returns>
    public async Task<(long Offset, long Length)?> AllocateAndWriteAsync(RecordBatch batch, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HasDictionaryColumn(batch.Schema))
        {
            var serialized = await SerializeForShmAsync(batch, cancellationToken).ConfigureAwait(false);
            var dictOffset = _allocator.Allocate(serialized.Length);
            if (dictOffset is not { } dOff)
            {
                return null;
            }

            using var dictAccessor = _mmf.CreateViewAccessor(dOff, serialized.Length, MemoryMappedFileAccess.ReadWrite);
            dictAccessor.WriteArray(0, serialized, 0, serialized.Length);
            dictAccessor.Flush();
            return (dOff, serialized.Length);
        }

        // Estimate generously (buffer size + framing overhead) — matches Python's
        // ipc.get_record_batch_size(batch) + _STREAM_OVERHEAD approach; this port has no
        // equivalent exact-size estimator, so GetTotalBufferSize() (already used identically by
        // ExternalLocation's own threshold check) plus a fixed overhead covers schema/message
        // framing safely without a second full serialize pass.
        var estimated = batch.GetTotalBufferSize() + StreamOverhead;
        var offset = _allocator.Allocate(estimated);
        if (offset is not { } off)
        {
            return null;
        }

        var viewStream = _mmf.CreateViewStream(off, estimated, MemoryMappedFileAccess.ReadWrite);
        await using (viewStream.ConfigureAwait(false))
        {
            var writer = new WireWriter(viewStream, batch.Schema);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteBatchAsync(new AnnotatedBatch(batch, null), cancellationToken).ConfigureAwait(false);
            }

            return (off, viewStream.Position);
        }
    }

    /// <summary>Zero-copy read of <paramref name="length"/> bytes at <paramref name="offset"/> —
    /// returns a view accessor the caller disposes once done deserializing (backs the returned
    /// bytes; there is no managed zero-copy <see cref="ReadOnlyMemory{T}"/> over a memory-mapped
    /// region in .NET the way <c>pa.py_buffer()</c> gives Python, so this port copies into a
    /// managed array — still avoids any pipe/socket round trip, which is the actual point of the
    /// side channel).</summary>
    public byte[] ReadBuffer(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = new byte[length];
        using var accessor = _mmf.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);
        accessor.ReadArray(0, buffer, 0, (int)length);
        return buffer;
    }

    /// <summary>Frees a previously allocated region by offset.</summary>
    public void Free(long offset) => _allocator.Free(offset);

    /// <summary>Clears every allocation — call between calls to reuse the segment, once the
    /// caller is certain nothing still references data written into it.</summary>
    public void Reset() => _allocator.Reset();

    /// <summary>Deletes the segment's OS-level backing (Linux/macOS: the <c>/dev/shm</c>/temp
    /// file; Windows: a no-op — a backing-file-less named section cleans up automatically once
    /// every handle closes). Only the segment's <i>creator</i> should ever call this — an
    /// attaching side just <see cref="Dispose"/>s its handle. Safe to call even if a
    /// <see cref="ReadBuffer"/> result is still referenced elsewhere: deleting the directory
    /// entry doesn't invalidate already-mapped pages or already-copied managed arrays.</summary>
    public void Unlink()
    {
        if (_backingFilePath is { } path)
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _headerAccessor.Dispose();
        _mmf.Dispose();
    }

    private const long StreamOverhead = 4096;

    private static bool HasDictionaryColumn(Schema schema) =>
        schema.FieldsList.Any(f => f.DataType is Apache.Arrow.Types.DictionaryType);

    /// <summary>Writes a full standalone IPC stream to a scratch buffer, then strips the leading
    /// schema message and trailing EOS marker — keeping only the dictionary-batch and
    /// record-batch messages, exactly matching Python's <c>_serialize_for_shm</c>. Reconstruction
    /// (<see cref="ShmPointerBatch.ResolveAsync"/>) reverses this by re-synthesizing the schema
    /// message and re-appending the EOS marker before handing the concatenation to
    /// <see cref="WireReader"/>.</summary>
    private static async Task<byte[]> SerializeForShmAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        var full = new MemoryStream();
        await using (full.ConfigureAwait(false))
        {
            var writer = new WireWriter(full, batch.Schema);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteBatchAsync(new AnnotatedBatch(batch, null), cancellationToken).ConfigureAwait(false);
            }

            var fullBytes = full.ToArray();
            var schemaMessage = await SchemaMessageBytesAsync(batch.Schema, cancellationToken).ConfigureAwait(false);
            var withoutEos = fullBytes.Length - ShmPointerBatch.EosMarker.Length;
            return fullBytes[schemaMessage.Length..withoutEos];
        }
    }

    /// <summary>The schema message a fresh <see cref="WireWriter"/> emits before any batch is
    /// written (schema message + EOS, minus the EOS) — used to find where the schema message ends
    /// in a full serialized stream, and to re-synthesize one on the read side. Writing an empty
    /// stream and measuring it (rather than hand-computing the schema message's
    /// FlatBuffers-encoded size) keeps this in lockstep with whatever <see cref="WireWriter"/>
    /// actually emits.</summary>
    internal static async Task<byte[]> SchemaMessageBytesAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        var buffer = new MemoryStream();
        await using (buffer.ConfigureAwait(false))
        {
            var writer = new WireWriter(buffer, schema);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteStartAsync(cancellationToken).ConfigureAwait(false);
            }

            var bytes = buffer.ToArray();
            return bytes[..^ShmPointerBatch.EosMarker.Length];
        }
    }
}
