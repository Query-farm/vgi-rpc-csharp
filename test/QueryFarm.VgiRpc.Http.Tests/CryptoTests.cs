using System.Text;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public class CryptoTests
{
    private static readonly byte[] s_key = SHA256HashOf("test-key");

    [Fact]
    public void SealOpen_RoundTrips()
    {
        var payload = Encoding.UTF8.GetBytes("hello state token");
        var aad = Encoding.UTF8.GetBytes("aad-context");

        var sealed_ = Crypto.Seal(payload, s_key, aad);
        var opened = Crypto.Open(sealed_, s_key, aad);

        Assert.Equal(payload, opened);
    }

    [Fact]
    public void SealOpen_EmptyPayload_RoundTrips()
    {
        var sealed_ = Crypto.Seal([], s_key, []);
        var opened = Crypto.Open(sealed_, s_key, []);

        Assert.Empty(opened);
    }

    [Fact]
    public void Seal_TwoCallsProduceDifferentCiphertext()
    {
        // Random nonce per seal — same payload/key/aad must not produce identical envelopes.
        var payload = Encoding.UTF8.GetBytes("same payload");
        var first = Crypto.Seal(payload, s_key, []);
        var second = Crypto.Seal(payload, s_key, []);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Open_WrongKey_ThrowsSealException()
    {
        var sealed_ = Crypto.Seal(Encoding.UTF8.GetBytes("secret"), s_key, []);
        var wrongKey = SHA256HashOf("a different key");

        Assert.Throws<Crypto.SealException>(() => Crypto.Open(sealed_, wrongKey, []));
    }

    [Fact]
    public void Open_WrongAad_ThrowsSealException()
    {
        var sealed_ = Crypto.Seal(Encoding.UTF8.GetBytes("secret"), s_key, Encoding.UTF8.GetBytes("principal-a"));

        Assert.Throws<Crypto.SealException>(() => Crypto.Open(sealed_, s_key, Encoding.UTF8.GetBytes("principal-b")));
    }

    [Fact]
    public void Open_TamperedCiphertext_ThrowsSealException()
    {
        var sealed_ = Crypto.Seal(Encoding.UTF8.GetBytes("secret"), s_key, []);
        var tampered = (byte[])sealed_.Clone();
        tampered[^1] ^= 0xFF; // flip a bit in the tag

        Assert.Throws<Crypto.SealException>(() => Crypto.Open(tampered, s_key, []));
    }

    [Fact]
    public void Open_TruncatedToken_ThrowsSealException()
    {
        Assert.Throws<Crypto.SealException>(() => Crypto.Open(new byte[5], s_key, []));
    }

    [Fact]
    public void Open_WrongVersion_ThrowsSealException()
    {
        var sealed_ = Crypto.Seal(Encoding.UTF8.GetBytes("secret"), s_key, [], version: 1);

        Assert.Throws<Crypto.SealException>(() => Crypto.Open(sealed_, s_key, [], version: 2));
    }

    [Fact]
    public void NormalizeKey_ExactLength_ReturnedAsIs()
    {
        var exact32 = new byte[32];
        Assert.Same(exact32, Crypto.NormalizeKey(exact32));
    }

    [Fact]
    public void NormalizeKey_OtherLength_Stretched()
    {
        var normalized = Crypto.NormalizeKey(Encoding.UTF8.GetBytes("short"));
        Assert.Equal(32, normalized.Length);
    }

    private static byte[] SHA256HashOf(string value) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
