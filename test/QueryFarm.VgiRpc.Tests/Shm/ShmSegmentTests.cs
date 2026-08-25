using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Shm;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Shm;

/// <summary>
/// Direct coverage for <see cref="ShmAllocator"/>/<see cref="ShmSegment"/>/<see cref="ShmPointerBatch"/>
/// — the M14 shared-memory side channel. See <c>docs/roadmap.md</c> M14 for the platform-primitive
/// finding these tests exercise (named backing-file-less maps aren't supported on Linux — this
/// port always goes through the same <c>/dev/shm</c>-file-backed code path
/// <see cref="ShmSegment.Create"/>/<see cref="ShmSegment.Attach"/> use on non-Windows platforms,
/// so a green run here on any OS/CI runner already exercises the actual production code path,
/// not a platform-specific stand-in).
/// </summary>
public sealed class ShmSegmentTests
{
    private static readonly Schema s_schema = new([new Field("value", Int64Type.Default, nullable: false)], metadata: null);

    private static RecordBatch MakeBatch(int rows)
    {
        var builder = new Int64Array.Builder();
        for (var i = 0; i < rows; i++)
        {
            builder.Append(i);
        }

        return new RecordBatch(s_schema, [builder.Build()], rows);
    }

    [Fact]
    public void Create_ThenAttach_SeesSameHeader()
    {
        using var creator = ShmSegment.Create(1024 * 1024);
        try
        {
            Assert.True(creator.Size >= 1024 * 1024);
            Assert.NotEmpty(creator.Name);

            using var attached = ShmSegment.Attach(creator.Name, creator.Size);
            // No throw — header magic/version/data_size all validated on attach.
        }
        finally
        {
            creator.Unlink();
        }
    }

    [Fact]
    public void Attach_WrongSize_Throws()
    {
        using var creator = ShmSegment.Create(1024 * 1024);
        try
        {
            Assert.ThrowsAny<Exception>(() => ShmSegment.Attach(creator.Name, 2048).Dispose());
        }
        finally
        {
            creator.Unlink();
        }
    }

    [Fact]
    public async Task AllocateAndWrite_ThenRead_RoundTripsBatch()
    {
        using var segment = ShmSegment.Create(4 * 1024 * 1024);
        try
        {
            var batch = MakeBatch(1000);
            var result = await segment.AllocateAndWriteAsync(batch);
            Assert.NotNull(result);
            var (offset, length) = result!.Value;
            Assert.True(offset >= ShmAllocator.HeaderSize);
            Assert.True(length > 0);

            var buffer = segment.ReadBuffer(offset, length);
            using var reader = new WireReader(new MemoryStream(buffer));
            _ = await reader.ReadSchemaAsync();
            var next = await reader.ReadNextAsync();
            Assert.NotNull(next);
            var resultArray = (Int64Array)next!.Batch.Column(0);
            Assert.Equal(1000, resultArray.Length);
            Assert.Equal(999, resultArray.Values[999]);

            segment.Free(offset);
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task Allocate_TooLargeForSegment_ReturnsNull()
    {
        using var segment = ShmSegment.Create(ShmAllocator.HeaderSize + 4096);
        try
        {
            var batch = MakeBatch(100_000); // far larger than the 4 KiB data region
            var result = await segment.AllocateAndWriteAsync(batch);
            Assert.Null(result);
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task Allocator_FirstFit_ReusesFreedGap()
    {
        using var segment = ShmSegment.Create(1024 * 1024);
        try
        {
            var a = await segment.AllocateAndWriteAsync(MakeBatch(1));
            var b = await segment.AllocateAndWriteAsync(MakeBatch(1));
            Assert.NotNull(a);
            Assert.NotNull(b);

            segment.Free(a!.Value.Offset);
            var c = await segment.AllocateAndWriteAsync(MakeBatch(1));
            Assert.NotNull(c);
            // First-fit should reuse the gap `a` freed, not append after `b`.
            Assert.Equal(a.Value.Offset, c!.Value.Offset);
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task Reset_ClearsAllAllocations()
    {
        using var segment = ShmSegment.Create(1024 * 1024);
        try
        {
            await segment.AllocateAndWriteAsync(MakeBatch(1));
            await segment.AllocateAndWriteAsync(MakeBatch(1));
            segment.Reset();

            var afterReset = await segment.AllocateAndWriteAsync(MakeBatch(1));
            Assert.NotNull(afterReset);
            Assert.Equal(ShmAllocator.HeaderSize, afterReset!.Value.Offset);
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public void Unlink_RemovesBackingFile()
    {
        var segment = ShmSegment.Create(1024 * 1024);
        var name = segment.Name;
        segment.Dispose();
        segment.Unlink();

        if (!OperatingSystem.IsWindows())
        {
            var path = OperatingSystem.IsLinux() ? $"/dev/shm/{name}" : Path.Combine(Path.GetTempPath(), name);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void IsShmPointerBatch_ZeroRowWithOffset_IsTrue()
    {
        var (batch, metadata) = ShmPointerBatch.Make(s_schema, 100, 200);
        Assert.True(ShmPointerBatch.IsShmPointerBatch(batch, metadata));
    }

    [Fact]
    public void IsShmPointerBatch_NonZeroRow_IsFalse()
    {
        var batch = MakeBatch(1);
        var metadata = new Dictionary<string, string> { [MetadataKeys.ShmOffset] = "100" };
        Assert.False(ShmPointerBatch.IsShmPointerBatch(batch, metadata));
    }

    [Fact]
    public void IsShmPointerBatch_LogBatch_IsFalse()
    {
        var (batch, metadata) = ShmPointerBatch.Make(s_schema, 100, 200);
        metadata[MetadataKeys.LogLevel] = "INFO";
        Assert.False(ShmPointerBatch.IsShmPointerBatch(batch, metadata));
    }

    [Fact]
    public async Task ResolveAsync_NonPointerBatch_ReturnsUnchanged()
    {
        var batch = MakeBatch(3);
        using var segment = ShmSegment.Create(1024 * 1024);
        try
        {
            var (resolved, metadata, release) = await ShmPointerBatch.ResolveAsync(batch, null, segment);
            Assert.Same(batch, resolved);
            Assert.Null(metadata);
            Assert.Null(release);
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task ResolveAsync_NullSegment_ReturnsUnchanged()
    {
        var (pointerBatch, pointerMetadata) = ShmPointerBatch.Make(s_schema, 100, 200);
        var (resolved, metadata, release) = await ShmPointerBatch.ResolveAsync(pointerBatch, pointerMetadata, null);
        Assert.Same(pointerBatch, resolved);
        Assert.Same(pointerMetadata, metadata);
        Assert.Null(release);
    }

    [Fact]
    public async Task MaybeWriteAsync_ThenResolveAsync_RoundTripsAndFreesOnRelease()
    {
        using var segment = ShmSegment.Create(16 * 1024 * 1024);
        try
        {
            // MinBatchBytes is a static, resolved-once-at-first-use property (mirrors Python's own
            // "resolved once at import" SHM_MIN_BATCH_BYTES) — mutating VGI_RPC_SHM_MIN_BATCH_BYTES
            // mid-test-run wouldn't reliably take effect depending on test execution order, so this
            // sizes the batch to unambiguously exceed the largest platform default (1 MiB, Windows)
            // rather than trying to override the threshold.
            var original = MakeBatch(200_000); // 200_000 * 8 bytes = ~1.6 MiB
            var extraMetadata = new Dictionary<string, string> { ["vgi_rpc.stream_state#b64"] = "token123" };

            var (written, writtenMetadata) = await ShmPointerBatch.MaybeWriteAsync(original, extraMetadata, segment);
            Assert.True(ShmPointerBatch.IsShmPointerBatch(written, writtenMetadata));
            Assert.Equal(0, written.Length);
            // Existing metadata (e.g. a stream-state token) must survive alongside the pointer keys.
            Assert.Equal("token123", writtenMetadata!["vgi_rpc.stream_state#b64"]);

            var (resolved, resolvedMetadata, release) = await ShmPointerBatch.ResolveAsync(written, writtenMetadata, segment);
            Assert.Equal(original.Length, resolved.Length);
            var resolvedColumn = (Int64Array)resolved.Column(0);
            Assert.Equal(199_999, resolvedColumn.Values[199_999]);
            Assert.False(resolvedMetadata!.ContainsKey(MetadataKeys.ShmOffset));
            Assert.False(resolvedMetadata.ContainsKey(MetadataKeys.ShmLength));
            Assert.Equal(segment.Name, resolvedMetadata[MetadataKeys.ShmSource]);
            Assert.Equal("token123", resolvedMetadata["vgi_rpc.stream_state#b64"]);

            Assert.NotNull(release);
            release!(); // must not throw — frees the allocation
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task MaybeWriteAsync_BelowThreshold_ReturnsUnchanged()
    {
        using var segment = ShmSegment.Create(4 * 1024 * 1024);
        try
        {
            var small = MakeBatch(1);
            var (written, _) = await ShmPointerBatch.MaybeWriteAsync(small, null, segment);
            Assert.Same(small, written);
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task MaybeWriteAsync_ZeroRowBatch_NeverWrites()
    {
        using var segment = ShmSegment.Create(4 * 1024 * 1024);
        try
        {
            var empty = MakeBatch(0);
            var (written, _) = await ShmPointerBatch.MaybeWriteAsync(empty, null, segment);
            Assert.Same(empty, written);
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task DictionaryEncodedBatch_RoundTripsThroughShm()
    {
        // Mirrors this port's enum encoding: Int16 indices over a Utf8 dictionary. Directly
        // exercises ShmSegment.AllocateAndWriteAsync/ShmPointerBatch.ResolveAsync's dictionary
        // path (which strips/reconstructs the schema message — see their doc comments) rather
        // than going through MaybeWriteAsync's size-threshold gate, which a 4-row dictionary
        // batch would never cross regardless of platform default.
        var dictType = new DictionaryType(Int16Type.Default, StringType.Default, ordered: false);
        var dictSchema = new Schema([new Field("status", dictType, nullable: false)], metadata: null);

        var indices = new Int16Array.Builder().AppendRange([0, 1, 0, 2]).Build();
        var values = new StringArray.Builder().AppendRange(["ACTIVE", "CLOSED", "PENDING"]).Build();
        var dictArray = new DictionaryArray(dictType, indices, values);
        var batch = new RecordBatch(dictSchema, [dictArray], 4);

        using var segment = ShmSegment.Create(1024 * 1024);
        try
        {
            var result = await segment.AllocateAndWriteAsync(batch);
            Assert.NotNull(result);
            var (pointerBatch, pointerMetadata) = ShmPointerBatch.Make(dictSchema, result!.Value.Offset, result.Value.Length);

            var (resolved, _, release) = await ShmPointerBatch.ResolveAsync(pointerBatch, pointerMetadata, segment);
            var resolvedDict = (DictionaryArray)resolved.Column(0);
            var resolvedIndices = (Int16Array)resolvedDict.Indices;
            Assert.Equal([0, 1, 0, 2], resolvedIndices.Values.ToArray());
            release?.Invoke();
        }
        finally
        {
            segment.Unlink();
        }
    }
}
