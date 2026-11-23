using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>
/// <c>state_ref_exchange</c>'s state (STATE_REF_V1.md §15): resolves the stream's
/// <see cref="StateRef"/> (or a presented one) on every exchange. Mirrors Python's
/// <c>StateRefExchangeState</c>. The ref is created by the init handler and only ever copied;
/// <c>padding</c> sits in the state itself; with <c>misuse</c> every exchange calls
/// <c>ctx.StateRef</c>, which this port refuses with <c>state_ref_misuse</c>.
/// </summary>
public sealed class StateRefExchangeState(StateRef value, byte[] padding, bool misuse) : ExchangeState
{
    public static readonly Schema InputSchema = new([new Field("override", BinaryType.Default, nullable: true)], metadata: null);

    public static readonly Schema OutputSchema = new(
        [
            new Field("size", Int64Type.Default, nullable: false),
            new Field("sha256", StringType.Default, nullable: false),
            new Field("form", StringType.Default, nullable: false),
            new Field("ref", BinaryType.Default, nullable: true),
        ],
        metadata: null);

    /// <summary>Held in the state to size it; never read.</summary>
    public byte[] Padding { get; } = padding;

    /// <summary>The deterministic payload every port builds (Python <c>state_ref_payload</c>):
    /// compressible is <c>"vgi-rpc StateRef conformance {seed} "</c> repeated and truncated;
    /// incompressible is <c>SHA-256(seed || u64le(i))</c> for i = 0, 1, ... concatenated and
    /// truncated.</summary>
    public static byte[] Payload(long size, bool compressible, string seed)
    {
        if (size <= 0)
        {
            return [];
        }

        var output = new byte[size];
        if (compressible)
        {
            var unit = Encoding.UTF8.GetBytes($"vgi-rpc StateRef conformance {seed} ");
            for (long i = 0; i < size; i++)
            {
                output[i] = unit[i % unit.Length];
            }

            return output;
        }

        var seedBytes = Encoding.UTF8.GetBytes(seed);
        var block = new byte[seedBytes.Length + 8];
        seedBytes.CopyTo(block, 0);
        long offset = 0;
        for (ulong i = 0; offset < size; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(seedBytes.Length), i);
            var digest = SHA256.HashData(block);
            var take = (int)Math.Min(digest.Length, size - offset);
            digest.AsSpan(0, take).CopyTo(output.AsSpan((int)offset));
            offset += take;
        }

        return output;
    }

    public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (misuse)
        {
            _ = ctx.StateRef("a continuation may not create a StateRef"u8.ToArray());
        }

        byte[]? presented = null;
        if (input.Batch.Length > 0 && input.Batch.Column(0) is BinaryArray overrides && !overrides.IsNull(0))
        {
            presented = overrides.GetBytes(0).ToArray();
        }

        var reference = presented is null ? value : StateRef.FromEncoded(presented);
        var data = ctx.Resolve(reference);
        output.Emit(ValueCodec.BuildRow(OutputSchema,
            [(long)data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), value.Form, value.Encoded]));
        return Task.CompletedTask;
    }
}
