using System.Collections.Generic;
using QueryFarm.VgiRpc.Hash;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Reflection;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Identity;

/// <summary>
/// The cross-port contract for <c>vgi_rpc.Identity.v1</c>.
/// </summary>
/// <remarks>
/// These digests are produced by the canonical Python reference and are not negotiable: a
/// mismatch means this port and that one would disagree about whether they speak the same
/// protocol. A failure is a JSON diff, not a guess -- <see cref="ProtocolHash.CanonicalDescription"/>
/// returns the exact preimage, which is why <see cref="CanonicalPreimageMatchesTheReference"/>
/// asserts it directly: it says <em>which</em> field, name or type token this port spells
/// differently, instead of just saying that some byte somewhere moved.
/// </remarks>
public class IdentityProtocolHashTests
{
    private static string HashOf(params string[] offered) =>
        ReflectionProtocol.BindingHash(
            IdentityProtocol.ProtocolName,
            IdentityProtocol.MethodsFor(new HashSet<string>(offered)));

    /// <summary>Framework-owned, so an application cannot register a protocol impersonating it.</summary>
    [Fact]
    public void ClaimsTheReservedPrefix() =>
        Assert.StartsWith("vgi_rpc.", IdentityProtocol.ProtocolName, System.StringComparison.Ordinal);

    [Fact]
    public void BothMethodsMatchTheReferenceDigest() =>
        Assert.Equal(
            "8317f2ad8e2476bb99e8b94800ab79b19a8cf0c6bdd6d66c2d82bd62ffbe69d5",
            HashOf("introspect_token", "issue_grant"));

    /// <summary>
    /// Not decoration: this digest proves method-level narrowing actually narrows the hash rather
    /// than hosting a method that refuses.
    /// </summary>
    [Fact]
    public void IntrospectOnlyMatchesTheReferenceDigest() =>
        Assert.Equal(
            "27b75bef22e4c70baab92a5188a473506b89055d2cb2b58cc187f6fe7a436385",
            HashOf("introspect_token"));

    /// <summary>The other half of the narrowing proof -- see <see cref="IntrospectOnlyMatchesTheReferenceDigest"/>.</summary>
    [Fact]
    public void IssueGrantOnlyMatchesTheReferenceDigest() =>
        Assert.Equal(
            "c71b12f453310139b6b6a445378064661c52711d03ae1e4fba29b8f7976ef4d8",
            HashOf("issue_grant"));

    /// <summary>A server offering half the methods is not offering the same surface.</summary>
    [Fact]
    public void NarrowingTheMethodSetNarrowsTheHash()
    {
        var both = HashOf("introspect_token", "issue_grant");
        Assert.NotEqual(both, HashOf("introspect_token"));
        Assert.NotEqual(both, HashOf("issue_grant"));
        Assert.NotEqual(HashOf("introspect_token"), HashOf("issue_grant"));
    }

    /// <summary>
    /// The preimage, byte for byte. The single most likely thing to get wrong is
    /// <c>scopes</c>'s list <em>item</em> being nullable -- <c>list&lt;item?:utf8&gt;</c>, not
    /// <c>list&lt;item:utf8&gt;</c>, which is a different Arrow type and a different digest.
    /// TypeScript shipped exactly that bug once, across every list type it had.
    /// </summary>
    [Fact]
    public void CanonicalPreimageMatchesTheReference()
    {
        var methods = IdentityProtocol.MethodsFor(
            new HashSet<string> { "introspect_token", "issue_grant" });
        var entries = new List<ProtocolHash.HashMethod>();
        foreach (var info in methods.Values)
        {
            entries.Add(new ProtocolHash.HashMethod(
                info.WireName,
                "unary",
                ReflectionProtocol.UnaryHasReturn(info),
                info.HeaderClrType is not null,
                info.ParamsSchema,
                info.ResultSchema,
                ReflectionProtocol.HeaderSchemaOf(info)));
        }

        Assert.Equal(
            "{\"methods\":[{\"has_header\":false,\"has_return\":true,\"name\":\"introspect_token\","
                + "\"params\":[{\"name\":\"token\",\"nullable\":false,\"type\":\"utf8\"}],"
                + "\"result\":[{\"name\":\"result\",\"nullable\":false,\"type\":\"binary\"}],\"type\":\"unary\"},"
                + "{\"has_header\":false,\"has_return\":true,\"name\":\"issue_grant\","
                + "\"params\":[{\"name\":\"purpose\",\"nullable\":false,\"type\":\"utf8\"},"
                + "{\"name\":\"scopes\",\"nullable\":false,\"type\":\"list<item?:utf8>\"},"
                + "{\"name\":\"ttl_seconds\",\"nullable\":false,\"type\":\"int64\"}],"
                + "\"result\":[{\"name\":\"result\",\"nullable\":false,\"type\":\"binary\"}],\"type\":\"unary\"}],"
                + "\"protocol\":\"vgi_rpc.Identity.v1\"}",
            ProtocolHash.CanonicalDescription(IdentityProtocol.ProtocolName, entries));
    }

    /// <summary>Both methods are unary, return a value, and carry no stream header.</summary>
    [Fact]
    public void BothMethodsAreUnaryWithAReturnAndNoHeader()
    {
        foreach (var info in IdentityProtocol.MethodsFor(
            new HashSet<string> { "introspect_token", "issue_grant" }).Values)
        {
            Assert.Equal(RpcMethodKind.Unary, info.Kind);
            Assert.True(ReflectionProtocol.UnaryHasReturn(info));
            Assert.Null(info.HeaderClrType);
        }
    }
}
