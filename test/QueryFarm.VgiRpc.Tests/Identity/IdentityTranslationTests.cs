using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Identity;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Identity;

/// <summary>WIRE_PROTOCOL.md §16: a hook raising the transport-auth "unavailable" error is
/// emitted as <c>identity_unavailable</c> carrying that error's own retry hint.</summary>
public class IdentityTranslationTests
{
    private static readonly System.Collections.Generic.List<string> s_noScopes = [];

    [Fact]
    public void ResolveHookTranslatesAuthUnavailable()
    {
        var impl = new IdentityImpl(
            resolveToken: _ => throw new AuthUnavailableException("store down", 7),
            introspectPrincipals: ["proxy"]);
        var error = Assert.Throws<IdentityUnavailableException>(
            () => impl.IntrospectToken("token", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy"))));
        Assert.Equal(7, error.RetryAfterSeconds);
        Assert.Equal(("UNAVAILABLE", "identity_unavailable"), (error.ErrorCode, error.ErrorKind));
        Assert.Equal(7, error.GetRetryInfo()!.RetryDelaySeconds);
    }

    /// <summary>The peer-identity flavour is the same error, so it translates too.</summary>
    [Fact]
    public void ResolveHookTranslatesPeerIdentityUnavailable()
    {
        var impl = new IdentityImpl(
            resolveToken: _ => throw new PeerIdentityUnavailableException("sidecar down", 11),
            introspectPrincipals: ["proxy"]);
        var error = Assert.Throws<IdentityUnavailableException>(
            () => impl.IntrospectToken("token", IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("proxy"))));
        Assert.Equal(11, error.RetryAfterSeconds);
    }

    [Fact]
    public void MintHookTranslatesAuthUnavailable()
    {
        var impl = new IdentityImpl(mintGrant: (_, _, _, _) => throw new AuthUnavailableException("grant store down", 7));
        var now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var error = Assert.Throws<IdentityUnavailableException>(
            () => impl.IssueGrant("p", s_noScopes, 60, IdentityTestDoubles.Ctx(IdentityTestDoubles.Auth("alice", authTime: now))));
        Assert.Equal(7, error.RetryAfterSeconds);
        Assert.Equal("grant store down", error.Detail);
    }

    /// <summary>Untranslated, the transport error would still read UNAVAILABLE with a RetryInfo
    /// of 7 -- only the kind tells a translated error apart, which is what is asserted above.</summary>
    [Fact]
    public void TheTransportErrorCarriesCodeAndHintButNoKind()
    {
        var exc = new AuthUnavailableException("x", 7);
        Assert.Equal("UNAVAILABLE", ErrorModel.CodeOf(exc));
        Assert.Null(ErrorModel.KindOf(exc));
    }
}
