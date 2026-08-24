using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public class ContentEncodingNegotiationTests
{
    private static readonly IReadOnlySet<ContentEncoding> s_zstdAndGzip = new HashSet<ContentEncoding> { ContentEncoding.Zstd, ContentEncoding.Gzip };

    [Theory]
    [InlineData("zstd, gzip", new[] { ContentEncoding.Zstd, ContentEncoding.Gzip })]
    [InlineData("gzip;q=0.8, zstd;q=1.0", new[] { ContentEncoding.Gzip, ContentEncoding.Zstd })]
    [InlineData("deflate, gzip, br, zstd", new[] { ContentEncoding.Gzip, ContentEncoding.Zstd })]
    [InlineData("", new ContentEncoding[0])]
    [InlineData("zstd, zstd", new[] { ContentEncoding.Zstd })]
    public void ParseEncodingList_ParsesAndDedupes(string header, ContentEncoding[] expected)
    {
        Assert.Equal(expected, ContentEncodingNegotiation.ParseEncodingList(header));
    }

    [Fact]
    public void PickResponseEncoding_PrefersCustomHeaderOrder()
    {
        // Standard header lists gzip first (mimicking cpp-httplib-style clients); the custom
        // VGI header states zstd-first — the custom header's order must win on which codec is
        // picked. useCustomHeader stays false here since zstd is *also* in the standard header
        // (see the next test for the case where it's exclusively in the custom one).
        var context = MakeContext(accept: "gzip, zstd", vgiAccept: "zstd, gzip");
        var (chosen, useCustom) = ContentEncodingNegotiation.PickResponseEncoding(context.Request, s_zstdAndGzip);

        Assert.Equal(ContentEncoding.Zstd, chosen);
        Assert.False(useCustom);
    }

    [Fact]
    public void PickResponseEncoding_UsesCustomHeaderWhenCodecOnlyThere()
    {
        // zstd appears only in the custom header — picking it must stamp the response via
        // X-VGI-Content-Encoding, not the standard Content-Encoding.
        var context = MakeContext(accept: "gzip", vgiAccept: "zstd");
        var (chosen, useCustom) = ContentEncodingNegotiation.PickResponseEncoding(context.Request, s_zstdAndGzip);

        Assert.Equal(ContentEncoding.Zstd, chosen);
        Assert.True(useCustom);
    }

    [Fact]
    public void PickResponseEncoding_FallsBackToStandardWhenNoCustomHeader()
    {
        var context = MakeContext(accept: "gzip", vgiAccept: null);
        var (chosen, useCustom) = ContentEncodingNegotiation.PickResponseEncoding(context.Request, s_zstdAndGzip);

        Assert.Equal(ContentEncoding.Gzip, chosen);
        Assert.False(useCustom);
    }

    [Fact]
    public void PickResponseEncoding_ExplicitIdentityWins()
    {
        var context = MakeContext(accept: "identity, gzip, zstd", vgiAccept: null);
        var (chosen, _) = ContentEncodingNegotiation.PickResponseEncoding(context.Request, s_zstdAndGzip);

        Assert.Null(chosen);
    }

    [Fact]
    public void PickResponseEncoding_NoOverlap_ReturnsNull()
    {
        var context = MakeContext(accept: "br", vgiAccept: null);
        var (chosen, useCustom) = ContentEncodingNegotiation.PickResponseEncoding(context.Request, s_zstdAndGzip);

        Assert.Null(chosen);
        Assert.False(useCustom);
    }

    [Fact]
    public void PickResponseEncoding_NoHeadersAtAll_ReturnsNull()
    {
        var context = MakeContext(accept: null, vgiAccept: null);
        var (chosen, _) = ContentEncodingNegotiation.PickResponseEncoding(context.Request, s_zstdAndGzip);

        Assert.Null(chosen);
    }

    [Fact]
    public void PickResponseEncoding_EmptyProducibleSet_NeverCompresses()
    {
        var context = MakeContext(accept: "zstd, gzip", vgiAccept: null);
        var (chosen, _) = ContentEncodingNegotiation.PickResponseEncoding(context.Request, new HashSet<ContentEncoding>());

        Assert.Null(chosen);
    }

    private static DefaultHttpContext MakeContext(string? accept, string? vgiAccept)
    {
        var context = new DefaultHttpContext();
        if (accept is not null)
        {
            context.Request.Headers.AcceptEncoding = accept;
        }

        if (vgiAccept is not null)
        {
            context.Request.Headers["X-VGI-Accept-Encoding"] = vgiAccept;
        }

        return context;
    }
}
