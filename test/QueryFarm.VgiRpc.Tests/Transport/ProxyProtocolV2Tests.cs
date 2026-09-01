using System.Buffers.Binary;
using System.Net;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Transport;

public sealed class ProxyProtocolV2Tests
{
    private static ReadOnlySpan<byte> Signature =>
        [0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a];

    [Fact]
    public async Task ReadsIpv4WithBoundedTlvsAndPreservesFollowingBytes()
    {
        var header = Ipv4Header();
        header = [.. header, 0xee, 0x00, 0x02, 0xaa, 0xbb];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14, 2), 17);
        var stream = new MemoryStream([.. header, 0x41, 0x42]);

        var address = await ProxyProtocolV2.ReadAsync(stream);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.7"), 42000), address.Source);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.9"), 19400), address.Destination);
        Assert.Equal(0x41, stream.ReadByte());
        Assert.Equal(0x42, stream.ReadByte());
    }

    [Fact]
    public void ParsesIpv6AndNormalizesMappedIpv4()
    {
        var body = new byte[36];
        IPAddress.Parse("::ffff:192.0.2.8").GetAddressBytes().CopyTo(body, 0);
        IPAddress.Parse("2001:db8::9").GetAddressBytes().CopyTo(body, 16);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(32, 2), 42001);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(34, 2), 19400);

        var address = ProxyProtocolV2.Parse(Header(0x21, 0x21, body));

        Assert.Equal(IPAddress.Parse("192.0.2.8"), address.Source.Address);
        Assert.Equal(IPAddress.Parse("2001:db8::9"), address.Destination.Address);
    }

    [Fact]
    public void RejectsUnsafeCommandsFamiliesFramingAndLimits()
    {
        var valid = Ipv4Header();
        var local = valid.ToArray();
        local[12] = 0x20;
        var udp = valid.ToArray();
        udp[13] = 0x12;
        var unspecified = valid.ToArray();
        unspecified[13] = 0x00;
        var badSignature = valid.ToArray();
        badSignature[0] ^= 0xff;
        var truncated = valid[..^1];
        var overlong = new byte[valid.Length + 1];
        valid.CopyTo(overlong, 0);
        var truncatedTlv = valid.ToArray();
        Array.Resize(ref truncatedTlv, valid.Length + 4);
        BinaryPrimitives.WriteUInt16BigEndian(truncatedTlv.AsSpan(14, 2), 16);
        truncatedTlv[^4] = 0xee;
        BinaryPrimitives.WriteUInt16BigEndian(truncatedTlv.AsSpan(truncatedTlv.Length - 3, 2), 2);

        foreach (var rejected in new[]
        {
            local, udp, unspecified, badSignature, truncated, overlong, truncatedTlv,
        })
            Assert.Throws<InvalidDataException>(() => ProxyProtocolV2.Parse(rejected));
        Assert.Throws<InvalidDataException>(() => ProxyProtocolV2.Parse(valid, 16));
    }

    private static byte[] Ipv4Header()
    {
        var body = new byte[12];
        IPAddress.Parse("192.0.2.7").GetAddressBytes().CopyTo(body, 0);
        IPAddress.Parse("198.51.100.9").GetAddressBytes().CopyTo(body, 4);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8, 2), 42000);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10, 2), 19400);
        return Header(0x21, 0x11, body);
    }

    private static byte[] Header(byte versionCommand, byte familyProtocol, byte[] body)
    {
        var value = new byte[16 + body.Length];
        Signature.CopyTo(value);
        value[12] = versionCommand;
        value[13] = familyProtocol;
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(14, 2), checked((ushort)body.Length));
        body.CopyTo(value, 16);
        return value;
    }
}
