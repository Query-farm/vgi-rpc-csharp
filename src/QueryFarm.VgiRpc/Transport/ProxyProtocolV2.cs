using System.Buffers.Binary;
using System.Net;

namespace QueryFarm.VgiRpc.Transport;

/// <summary>The asserted TCP endpoints from one validated PROXY protocol v2 preamble.</summary>
public sealed record ProxyProtocolV2Address(IPEndPoint Source, IPEndPoint Destination);

/// <summary>Canonical EndpointId from the dedicated trusted Iroh PROXY/UNSPEC form.</summary>
public sealed record ProxyProtocolV2IrohIdentity(string EndpointId);

internal sealed record ProxyProtocolV2Peer(
    ProxyProtocolV2Address? Address,
    ProxyProtocolV2IrohIdentity? IrohIdentity);

/// <summary>Strict, bounded PROXY protocol v2 parsing for trusted TCP frontends.</summary>
public static class ProxyProtocolV2
{
    public const int DefaultMaximumPreambleBytes = 536;
    public const byte VgiIrohEndpointTlv = 0xe0;
    private const int FixedPreambleBytes = 16;
    private static ReadOnlySpan<byte> Signature =>
        [0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a];

    /// <summary>
    /// Reads exactly one preamble. Bytes belonging to the following VGI frame remain unread.
    /// </summary>
    public static async ValueTask<ProxyProtocolV2Address> ReadAsync(
        Stream input,
        int maximumBytes = DefaultMaximumPreambleBytes,
        CancellationToken cancellationToken = default)
    {
        var peer = await ReadPeerAsync(input, maximumBytes, allowIrohIdentity: false,
            cancellationToken).ConfigureAwait(false);
        return peer.Address!;
    }

    /// <summary>
    /// Reads the opt-in PROXY/UNSPEC form carrying one bridge-verified Iroh EndpointId.
    /// Following VGI bytes remain unread.
    /// </summary>
    public static async ValueTask<ProxyProtocolV2IrohIdentity> ReadIrohIdentityAsync(
        Stream input,
        int maximumBytes = DefaultMaximumPreambleBytes,
        CancellationToken cancellationToken = default)
    {
        var peer = await ReadPeerAsync(input, maximumBytes, allowIrohIdentity: true,
            cancellationToken).ConfigureAwait(false);
        return peer.IrohIdentity
            ?? throw new InvalidDataException("VGI Iroh identity requires PROXY/UNSPEC");
    }

    internal static ValueTask<ProxyProtocolV2Peer> ReadAllowingIrohIdentityAsync(
        Stream input,
        int maximumBytes = DefaultMaximumPreambleBytes,
        CancellationToken cancellationToken = default) =>
        ReadPeerAsync(input, maximumBytes, allowIrohIdentity: true, cancellationToken);

    private static async ValueTask<ProxyProtocolV2Peer> ReadPeerAsync(
        Stream input,
        int maximumBytes,
        bool allowIrohIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateMaximum(maximumBytes);
        var fixedPreamble = new byte[FixedPreambleBytes];
        try
        {
            await input.ReadExactlyAsync(fixedPreamble, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("truncated PROXY v2 fixed preamble", exception);
        }

        ValidateSignature(fixedPreamble);
        var bodyLength = BinaryPrimitives.ReadUInt16BigEndian(fixedPreamble.AsSpan(14, 2));
        var totalLength = FixedPreambleBytes + bodyLength;
        if (totalLength > maximumBytes)
            throw new InvalidDataException("PROXY v2 preamble exceeds configured limit");

        var preamble = new byte[totalLength];
        fixedPreamble.CopyTo(preamble, 0);
        try
        {
            await input.ReadExactlyAsync(preamble.AsMemory(FixedPreambleBytes), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("truncated PROXY v2 body", exception);
        }
        return ParsePeer(preamble, maximumBytes, allowIrohIdentity);
    }

    /// <summary>Validates and parses one exact PROXY protocol v2 preamble.</summary>
    public static ProxyProtocolV2Address Parse(
        ReadOnlySpan<byte> preamble,
        int maximumBytes = DefaultMaximumPreambleBytes) =>
        ParsePeer(preamble, maximumBytes, allowIrohIdentity: false).Address!;

    /// <summary>Parses the opt-in PROXY/UNSPEC Iroh identity form.</summary>
    public static ProxyProtocolV2IrohIdentity ParseIrohIdentity(
        ReadOnlySpan<byte> preamble,
        int maximumBytes = DefaultMaximumPreambleBytes)
    {
        var peer = ParsePeer(preamble, maximumBytes, allowIrohIdentity: true);
        return peer.IrohIdentity
            ?? throw new InvalidDataException("VGI Iroh identity requires PROXY/UNSPEC");
    }

    private static ProxyProtocolV2Peer ParsePeer(
        ReadOnlySpan<byte> preamble,
        int maximumBytes,
        bool allowIrohIdentity)
    {
        ValidateMaximum(maximumBytes);
        if (preamble.Length < FixedPreambleBytes)
            throw new InvalidDataException("truncated PROXY v2 fixed preamble");
        if (preamble.Length > maximumBytes)
            throw new InvalidDataException("PROXY v2 preamble exceeds configured limit");
        ValidateSignature(preamble);

        var versionCommand = preamble[12];
        if ((versionCommand >> 4) != 2)
            throw new InvalidDataException("unsupported PROXY protocol version");
        if ((versionCommand & 0x0f) == 0)
            throw new InvalidDataException("PROXY v2 LOCAL command is not accepted");
        if ((versionCommand & 0x0f) != 1)
            throw new InvalidDataException("unsupported PROXY v2 command");

        var bodyLength = BinaryPrimitives.ReadUInt16BigEndian(preamble.Slice(14, 2));
        if (preamble.Length != FixedPreambleBytes + bodyLength)
            throw new InvalidDataException("truncated or overlong PROXY v2 preamble");

        IPEndPoint? source;
        IPEndPoint? destination;
        int addressBytes;
        if (preamble[13] == 0x00 && allowIrohIdentity)
        {
            source = null;
            destination = null;
            addressBytes = 0;
        }
        else
        {
            (source, destination, addressBytes) = preamble[13] switch
            {
                0x11 => ParseIpv4(preamble, bodyLength),
                0x21 => ParseIpv6(preamble, bodyLength),
                _ => throw new InvalidDataException("PROXY v2 requires TCP over IPv4 or IPv6"),
            };
        }

        var endpointId = ParseTlvs(
            preamble.Slice(FixedPreambleBytes + addressBytes), allowIrohIdentity);
        if (preamble[13] == 0x00 && endpointId is null)
            throw new InvalidDataException("PROXY/UNSPEC requires one VGI Iroh identity TLV");
        if (endpointId is not null && preamble[13] != 0x00)
            throw new InvalidDataException("VGI Iroh identity requires PROXY/UNSPEC");
        return new ProxyProtocolV2Peer(
            source is null ? null : new ProxyProtocolV2Address(source, destination!),
            endpointId is null
                ? null
                : new ProxyProtocolV2IrohIdentity(Convert.ToHexStringLower(endpointId)));
    }

    internal static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static (IPEndPoint Source, IPEndPoint Destination, int AddressBytes) ParseIpv4(
        ReadOnlySpan<byte> preamble, int bodyLength)
    {
        const int addressBytes = 12;
        if (bodyLength < addressBytes)
            throw new InvalidDataException("truncated PROXY v2 TCP/IPv4 address block");
        var source = new IPAddress(preamble.Slice(16, 4));
        var destination = new IPAddress(preamble.Slice(20, 4));
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(preamble.Slice(24, 2));
        var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(preamble.Slice(26, 2));
        return (new IPEndPoint(source, sourcePort), new IPEndPoint(destination, destinationPort),
            addressBytes);
    }

    private static (IPEndPoint Source, IPEndPoint Destination, int AddressBytes) ParseIpv6(
        ReadOnlySpan<byte> preamble, int bodyLength)
    {
        const int addressBytes = 36;
        if (bodyLength < addressBytes)
            throw new InvalidDataException("truncated PROXY v2 TCP/IPv6 address block");
        var source = Normalize(new IPAddress(preamble.Slice(16, 16)));
        var destination = Normalize(new IPAddress(preamble.Slice(32, 16)));
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(preamble.Slice(48, 2));
        var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(preamble.Slice(50, 2));
        return (new IPEndPoint(source, sourcePort), new IPEndPoint(destination, destinationPort),
            addressBytes);
    }

    private static byte[]? ParseTlvs(ReadOnlySpan<byte> tlvs, bool allowIrohIdentity)
    {
        byte[]? endpointId = null;
        while (!tlvs.IsEmpty)
        {
            if (tlvs.Length < 3)
                throw new InvalidDataException("truncated PROXY v2 TLV header");
            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(tlvs.Slice(1, 2));
            if (tlvs.Length < 3 + valueLength)
                throw new InvalidDataException("truncated PROXY v2 TLV value");
            if (tlvs[0] == VgiIrohEndpointTlv && allowIrohIdentity)
            {
                if (endpointId is not null)
                    throw new InvalidDataException("duplicate VGI Iroh identity TLV");
                if (valueLength != 33 || tlvs[3] != 1)
                    throw new InvalidDataException("invalid VGI Iroh identity TLV");
                endpointId = tlvs.Slice(4, 32).ToArray();
            }
            tlvs = tlvs[(3 + valueLength)..];
        }
        return endpointId;
    }

    private static void ValidateSignature(ReadOnlySpan<byte> preamble)
    {
        if (!preamble[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("missing PROXY v2 signature");
    }

    private static void ValidateMaximum(int maximumBytes)
    {
        if (maximumBytes < FixedPreambleBytes || maximumBytes > FixedPreambleBytes + ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes),
                "maximum PROXY v2 preamble bytes must be between 16 and 65551");
    }
}
