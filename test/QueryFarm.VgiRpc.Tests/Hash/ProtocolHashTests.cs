using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Hash;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Hash;

/// <summary>
/// The cross-port contract.
/// </summary>
/// <remarks>
/// These values are produced by the Python reference (<c>vgi_rpc/rpc/_protocol_hash.py</c>); a
/// mismatch means this port and that one would disagree about whether they speak the same
/// protocol. A failure is a JSON diff, not a guess: <c>CanonicalDescription</c> returns the exact
/// preimage, so print it and compare.
/// </remarks>
public class ProtocolHashTests
{
    private static Schema Utf8Schema(string name) =>
        new Schema.Builder().Field(new Field(name, StringType.Default, nullable: false)).Build();

    [Fact]
    public void MatchesThePythonReferenceDigest()
    {
        var methods = new List<ProtocolHash.HashMethod>
        {
            new("echo", "unary", true, false, Utf8Schema("value"), Utf8Schema("result"), null),
        };
        Assert.Equal(
            "a4b8ae57bf777c906081ff3610d435836b77dbc1f17a381c6a2febf1a2adb115",
            ProtocolHash.ComputeProtocolHash("demo.Hash.v1", methods));
    }

    /// <summary>Absent and empty are different and must not hash alike.</summary>
    [Fact]
    public void OmitsResultForAMethodReturningNothing()
    {
        var methods = new List<ProtocolHash.HashMethod>
        {
            new("fire", "unary", false, false, Utf8Schema("v"), null, null),
        };
        Assert.Equal(
            "{\"methods\":[{\"has_header\":false,\"has_return\":false,\"name\":\"fire\","
                + "\"params\":[{\"name\":\"v\",\"nullable\":false,\"type\":\"utf8\"}],"
                + "\"type\":\"unary\"}],\"protocol\":\"demo.Void.v1\"}",
            ProtocolHash.CanonicalDescription("demo.Void.v1", methods));
    }

    /// <summary>A port iterating a hash map must still produce this order.</summary>
    [Fact]
    public void SortsMethodsByName()
    {
        var empty = new Schema.Builder().Build();
        ProtocolHash.HashMethod Mk(string name) => new(name, "unary", false, false, empty, null, null);
        Assert.Equal(
            ProtocolHash.ComputeProtocolHash("p", new[] { Mk("a"), Mk("b") }),
            ProtocolHash.ComputeProtocolHash("p", new[] { Mk("b"), Mk("a") }));
    }

    /// <summary>
    /// Arrow ignores a list child's <em>name</em>, so the token must too -- otherwise two ports
    /// that default differently hash the same protocol differently. Nullability it does not
    /// ignore, so the token keeps that.
    /// </summary>
    [Fact]
    public void ListChildNameIsNormalisedButNullabilityIsKept()
    {
        var named = new Field(
            "col",
            new ListType(new Field("element", Int64Type.Default, nullable: true)),
            nullable: false);
        Assert.Equal("list<item?:int64>", TypeTokens.TypeToken(named));

        var nonNull = new Field(
            "col",
            new ListType(new Field("item", Int64Type.Default, nullable: false)),
            nullable: false);
        Assert.Equal("list<item:int64>", TypeTokens.TypeToken(nonNull));
    }

    /// <summary>
    /// Decimal128Type and Decimal256Type derive from FixedSizeBinaryType in this Arrow binding,
    /// so an arm ordered by convenience rather than by derivation would spell a decimal as
    /// fixed_size_binary(16) and disagree with every other port.
    /// </summary>
    [Fact]
    public void DecimalIsNotSwallowedByTheFixedSizeBinaryArm()
    {
        var dec = new Field("d", new Decimal128Type(38, 9), nullable: false);
        Assert.Equal("decimal128(38,9)", TypeTokens.TypeToken(dec));
    }
}
