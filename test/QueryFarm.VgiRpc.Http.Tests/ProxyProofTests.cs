using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>Direct unit coverage for <see cref="ProxyProof"/> — the wire-behavior half is covered
/// end-to-end by the canonical TestProxyProof/TestProxyProofOffMode groups imported into
/// test_csharp_conformance.py (see docs/roadmap.md M11).</summary>
public class ProxyProofTests
{
    private static readonly byte[] s_secret = Convert.FromHexString("11" + string.Concat(Enumerable.Repeat("11", 31)));
    private const string Kid = "conformance-proxy";
    private const string OriginId = "conformance-origin";

    private static ProxyProofConfig MakeConfig(ProxyProofMode mode = ProxyProofMode.Require, int skewSeconds = 30, bool replayCache = true) =>
        new(mode, OriginId, new Dictionary<string, ProxyProofSecret> { [Kid] = new(s_secret, Kid) }, skewSeconds, enableReplayCache: replayCache);

    // -------------------------------------------------------------------
    // Mint / verify round-trip
    // -------------------------------------------------------------------

    [Fact]
    public void MintAndVerify_RoundTrips()
    {
        var token = ProxyProof.MintProof(s_secret, Kid, OriginId);

        var result = ProxyProof.VerifyProof(token, MakeConfig(), nonceCache: null);

        Assert.True(result.Verified);
        Assert.Equal(Kid, result.Proxy);
        Assert.Equal(Kid, result.Kid);
        Assert.Equal(OriginId, result.OriginId);
        Assert.Equal("ok", result.Reason);
    }

    [Fact]
    public void CanonicalString_MatchesFixedVector()
    {
        // Fixed vector, independently computable — proves this port's framing matches the spec's
        // NUL-separated layout exactly, not just that mint/verify round-trip against themselves.
        var canonical = ProxyProof.CanonicalString("k", "1", "n", "o");

        Assert.Equal("vgi.proxy.proof.v1\0k\01\0n\0o"u8.ToArray(), canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1.a.b.c")]
    [InlineData("v1.a.b.c.d.e")]
    public void VerifyProof_WrongFieldCount_ThrowsMalformed(string token)
    {
        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(token, MakeConfig(), null));
        Assert.Equal("malformed", exc.Reason);
    }

    [Fact]
    public void VerifyProof_WrongVersion_ThrowsMalformed()
    {
        var mac = new string('A', 43);
        var token = $"v2.{Kid}.1.AAAAAAAAAAAAAAAAAAAAAA.{mac}";

        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(token, MakeConfig(), null));
        Assert.Equal("malformed", exc.Reason);
    }

    [Theory]
    [InlineData("v1.bad!kid.1.AAAAAAAAAAAAAAAAAAAAAA.")] // kid charset
    [InlineData("v1.conformance-proxy.notanumber.AAAAAAAAAAAAAAAAAAAAAA.")] // ts charset
    [InlineData("v1.conformance-proxy.1.short.")] // nonce charset (wrong length)
    public void VerifyProof_BadCharset_ThrowsMalformed(string prefix)
    {
        var token = prefix + new string('A', 43);

        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(token, MakeConfig(), null));
        Assert.Equal("malformed", exc.Reason);
    }

    [Fact]
    public void VerifyProof_UnknownKid_ThrowsUnknownKid()
    {
        var foreignSecret = new byte[32];
        var token = ProxyProof.MintProof(foreignSecret, "no-such-kid", OriginId);

        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(token, MakeConfig(), null));
        Assert.Equal("unknown_kid", exc.Reason);
    }

    [Fact]
    public void VerifyProof_TamperedMac_ThrowsBadMac()
    {
        var token = ProxyProof.MintProof(s_secret, Kid, OriginId, nonce: "AAAAAAAAAAAAAAAAAAAAAA");
        var parts = token.Split('.');
        parts[4] = new string('B', 43);
        var tampered = string.Join('.', parts);

        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(tampered, MakeConfig(), null));
        Assert.Equal("bad_mac", exc.Reason);
    }

    [Fact]
    public void VerifyProof_WrongOrigin_ThrowsBadMac()
    {
        // origin_id is folded into the MAC but never transmitted — a proof minted for another
        // worker fails here even though every wire field looks well-formed.
        var token = ProxyProof.MintProof(s_secret, Kid, "some-other-worker");

        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(token, MakeConfig(), null));
        Assert.Equal("bad_mac", exc.Reason);
    }

    [Fact]
    public void VerifyProof_Expired_ThrowsExpired()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ProxyProof.MintProof(s_secret, Kid, OriginId, now: now - 100);

        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(token, MakeConfig(skewSeconds: 30), null, now: now));
        Assert.Equal("expired", exc.Reason);
    }

    [Fact]
    public void VerifyProof_NotYetValid_ThrowsNotYetValid()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ProxyProof.MintProof(s_secret, Kid, OriginId, now: now + 100);

        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(token, MakeConfig(skewSeconds: 30), null, now: now));
        Assert.Equal("not_yet_valid", exc.Reason);
    }

    [Fact]
    public void VerifyProof_WithinSkew_Accepted()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ProxyProof.MintProof(s_secret, Kid, OriginId, now: now - 10);

        var result = ProxyProof.VerifyProof(token, MakeConfig(skewSeconds: 30), null, now: now);

        Assert.True(result.Verified);
    }

    // -------------------------------------------------------------------
    // Replay cache
    // -------------------------------------------------------------------

    [Fact]
    public void VerifyProof_ReplayedNonce_ThrowsReplayed()
    {
        var cache = new NonceCache(30);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ProxyProof.MintProof(s_secret, Kid, OriginId, now: now, nonce: "AAAAAAAAAAAAAAAAAAAAAA");

        var first = ProxyProof.VerifyProof(token, MakeConfig(), cache, now: now);
        Assert.True(first.Verified);

        var exc = Assert.Throws<ProofException>(() => ProxyProof.VerifyProof(token, MakeConfig(), cache, now: now));
        Assert.Equal("replayed", exc.Reason);
    }

    [Fact]
    public void VerifyProof_DistinctNonceSameTimestamp_Accepted()
    {
        var cache = new NonceCache(30);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var first = ProxyProof.MintProof(s_secret, Kid, OriginId, now: now, nonce: "AAAAAAAAAAAAAAAAAAAAAA");
        var second = ProxyProof.MintProof(s_secret, Kid, OriginId, now: now, nonce: "BBBBBBBBBBBBBBBBBBBBBB");

        Assert.True(ProxyProof.VerifyProof(first, MakeConfig(), cache, now: now).Verified);
        Assert.True(ProxyProof.VerifyProof(second, MakeConfig(), cache, now: now).Verified);
    }

    [Fact]
    public void NonceCache_CheckAndAdd_FirstSeenTrueThenFalse()
    {
        var cache = new NonceCache(30);

        Assert.True(cache.CheckAndAdd("n1"));
        Assert.False(cache.CheckAndAdd("n1"));
    }

    [Fact]
    public void NonceCache_OverCapacity_EvictsOldest()
    {
        var cache = new NonceCache(30, capacity: 2);

        Assert.True(cache.CheckAndAdd("n1"));
        Assert.True(cache.CheckAndAdd("n2"));
        Assert.True(cache.CheckAndAdd("n3")); // evicts n1

        Assert.True(cache.CheckAndAdd("n1")); // n1 was evicted, so this is "fresh" again
    }

    [Fact]
    public void NonceCache_ExpiredEntry_SweptAndAcceptedAgain()
    {
        var clockValue = 0.0;
        var cache = new NonceCache(1, clock: () => clockValue);

        Assert.True(cache.CheckAndAdd("n1"));
        clockValue = 5.0; // well past the 1s TTL

        Assert.True(cache.CheckAndAdd("n1"));
    }

    // -------------------------------------------------------------------
    // ParseSecrets / ProxyProofConfig validation
    // -------------------------------------------------------------------

    [Fact]
    public void ParseSecrets_SingleEntry_Parses()
    {
        var secrets = ProxyProof.ParseSecrets($"{Kid}:{Convert.ToHexStringLower(s_secret)}");

        Assert.Single(secrets);
        Assert.Equal(s_secret, secrets[Kid].Secret);
        Assert.Equal(Kid, secrets[Kid].Label);
    }

    [Fact]
    public void ParseSecrets_MultipleEntries_Parses()
    {
        var secrets = ProxyProof.ParseSecrets($"{Kid}:{Convert.ToHexStringLower(s_secret)},other:{"22" + string.Concat(Enumerable.Repeat("22", 31))}");

        Assert.Equal(2, secrets.Count);
        Assert.Contains("other", secrets.Keys);
    }

    [Fact]
    public void ParseSecrets_MissingColon_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProxyProof.ParseSecrets("no-colon-here"));
    }

    [Fact]
    public void ParseSecrets_WrongLengthSecret_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProxyProof.ParseSecrets($"{Kid}:aabb"));
    }

    [Fact]
    public void ParseSecrets_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProxyProof.ParseSecrets(""));
    }

    [Fact]
    public void ProxyProofConfig_OffMode_SkipsValidation()
    {
        var exception = Record.Exception(() => new ProxyProofConfig(ProxyProofMode.Off));

        Assert.Null(exception);
    }

    [Fact]
    public void ProxyProofConfig_RequireModeNoSecrets_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ProxyProofConfig(ProxyProofMode.Require, OriginId));
    }

    [Fact]
    public void ProxyProofConfig_RequireModeNoOriginId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ProxyProofConfig(
            ProxyProofMode.Require,
            originId: "",
            secrets: new Dictionary<string, ProxyProofSecret> { [Kid] = new(s_secret, Kid) }));
    }

    [Fact]
    public void ProxyProofConfig_WrongLengthSecret_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ProxyProofConfig(
            ProxyProofMode.Require,
            OriginId,
            new Dictionary<string, ProxyProofSecret> { [Kid] = new(new byte[16], Kid) }));
    }

    // -------------------------------------------------------------------
    // Gate / composition
    // -------------------------------------------------------------------

    [Fact]
    public async Task CreateGate_RequireMode_ValidProof_Accepted()
    {
        var gate = ProxyProof.CreateGate(MakeConfig());
        var context = new DefaultHttpContext();
        context.Request.Headers[ProxyProof.ProofHeader] = ProxyProof.MintProof(s_secret, Kid, OriginId);

        await gate(context); // does not throw

        var result = ProxyProofResult.GetFrom(context);
        Assert.NotNull(result);
        Assert.True(result!.Verified);
    }

    [Fact]
    public async Task CreateGate_RequireMode_MissingProof_ThrowsAuthFailureProxyRequired()
    {
        var gate = ProxyProof.CreateGate(MakeConfig());
        var context = new DefaultHttpContext();

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => gate(context));
        Assert.Equal(AuthReason.ProxyRequired, exc.Reason);
    }

    [Fact]
    public async Task CreateGate_RequireMode_DoesNotLeakReasonInDetail()
    {
        // docs/proxy-proof-spec.md §6: "Rejection is uniform. The response body MUST NOT contain
        // the verifier's message, the reason code, or any echo of kid."
        var gate = ProxyProof.CreateGate(MakeConfig());
        var context = new DefaultHttpContext();
        context.Request.Headers[ProxyProof.ProofHeader] = "v1.no-such-kid.1.AAAAAAAAAAAAAAAAAAAAAA." + new string('A', 43);

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => gate(context));
        Assert.DoesNotContain("no-such-kid", exc.Detail);
        Assert.DoesNotContain("unknown_kid", exc.Detail);
    }

    [Fact]
    public async Task CreateGate_AllowMode_MissingProof_DoesNotThrow()
    {
        var gate = ProxyProof.CreateGate(MakeConfig(ProxyProofMode.Allow));
        var context = new DefaultHttpContext();

        await gate(context); // does not throw — allow mode never denies

        var result = ProxyProofResult.GetFrom(context);
        Assert.NotNull(result);
        Assert.False(result!.Verified);
    }

    [Fact]
    public async Task CreateGate_AllowMode_ValidProof_RecordsAttribution()
    {
        var gate = ProxyProof.CreateGate(MakeConfig(ProxyProofMode.Allow));
        var context = new DefaultHttpContext();
        context.Request.Headers[ProxyProof.ProofHeader] = ProxyProof.MintProof(s_secret, Kid, OriginId);

        await gate(context);

        Assert.True(ProxyProofResult.GetFrom(context)!.Verified);
    }

    [Fact]
    public void CreateGate_OffMode_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProxyProof.CreateGate(new ProxyProofConfig(ProxyProofMode.Off)));
    }

    [Fact]
    public async Task RequireAll_GateFails_InnerNeverRuns()
    {
        var gate = ProxyProof.CreateGate(MakeConfig());
        var innerRan = false;
        RpcHttpEndpoints.AuthenticateDelegate inner = _ =>
        {
            innerRan = true;
            return Task.CompletedTask;
        };
        var combined = ProxyProof.RequireAll(gate, inner);
        var context = new DefaultHttpContext();

        await Assert.ThrowsAsync<AuthFailure>(() => combined(context));
        Assert.False(innerRan);
    }

    [Fact]
    public async Task RequireAll_GateSucceeds_InnerRuns()
    {
        var gate = ProxyProof.CreateGate(MakeConfig());
        var innerRan = false;
        RpcHttpEndpoints.AuthenticateDelegate inner = _ =>
        {
            innerRan = true;
            return Task.CompletedTask;
        };
        var combined = ProxyProof.RequireAll(gate, inner);
        var context = new DefaultHttpContext();
        context.Request.Headers[ProxyProof.ProofHeader] = ProxyProof.MintProof(s_secret, Kid, OriginId);

        await combined(context);

        Assert.True(innerRan);
    }

    [Fact]
    public async Task RequireAll_NullInner_Succeeds()
    {
        var gate = ProxyProof.CreateGate(MakeConfig());
        var combined = ProxyProof.RequireAll(gate, null);
        var context = new DefaultHttpContext();
        context.Request.Headers[ProxyProof.ProofHeader] = ProxyProof.MintProof(s_secret, Kid, OriginId);

        await combined(context); // does not throw
    }

    // -------------------------------------------------------------------
    // DeriveSecret
    // -------------------------------------------------------------------

    [Fact]
    public void DeriveSecret_Deterministic()
    {
        var baseKey = new byte[32];
        var a = ProxyProof.DeriveSecret(baseKey, "proxy-a", "worker-1");
        var b = ProxyProof.DeriveSecret(baseKey, "proxy-a", "worker-1");

        Assert.Equal(a, b);
    }

    [Fact]
    public void DeriveSecret_DifferentOrigin_DifferentSecret()
    {
        var baseKey = new byte[32];
        var a = ProxyProof.DeriveSecret(baseKey, "proxy-a", "worker-1");
        var b = ProxyProof.DeriveSecret(baseKey, "proxy-a", "worker-2");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeriveSecret_WrongLengthBaseKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProxyProof.DeriveSecret(new byte[16], "proxy-a", "worker-1"));
    }
}
