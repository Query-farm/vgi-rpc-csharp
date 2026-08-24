namespace QueryFarm.VgiRpc.Wire;

/// <summary>Wire-level constants from WIRE_PROTOCOL.md in the canonical Python repo.</summary>
public static class WireConstants
{
    /// <summary>
    /// The 8-byte end-of-stream marker every Arrow IPC stream ends with: a continuation
    /// indicator (0xFFFFFFFF) followed by a zero-length metadata length. Written by
    /// <see cref="Apache.Arrow.Ipc.ArrowStreamWriter.WriteEnd"/>/<c>WriteEndAsync</c> — this
    /// constant exists for tests and any code that needs to recognize it in a raw byte stream,
    /// not for hand-writing it.
    /// </summary>
    public static readonly byte[] EosMarker = [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];

    /// <summary>
    /// The fixed 56-byte serialized form of an empty Arrow schema (<c>pa.schema([])</c> via
    /// <c>pa.Schema.serialize()</c>), per Appendix C of WIRE_PROTOCOL.md. Stable across Arrow
    /// versions; used by <c>Wire/EmptySchemaFixtureTests</c> to confirm the vendored writer's
    /// schema-message encoding matches the canonical Python implementation byte-for-byte.
    /// </summary>
    public static readonly byte[] EmptySchemaFixture =
    [
        0xff, 0xff, 0xff, 0xff, 0x30, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0a, 0x00,
        0x0c, 0x00, 0x06, 0x00, 0x05, 0x00, 0x08, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x01, 0x04, 0x00,
        0x0c, 0x00, 0x00, 0x00, 0x08, 0x00, 0x08, 0x00, 0x00, 0x00, 0x04, 0x00, 0x08, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];
}
