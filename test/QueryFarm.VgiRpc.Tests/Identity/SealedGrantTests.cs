using System.Text.Json;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Identity;

/// <summary>Sealed grants (IDENTITY_V1_SPEC.md §9.1), pinned by the reference's
/// <c>grant_token_vectors.json</c>: exact mint output, every accept case, every reject case.</summary>
public class SealedGrantTests
{
    private static readonly JsonElement s_vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "grant_token_vectors.json"), System.Text.Encoding.UTF8)).RootElement.Clone();

    private static JsonElement Defaults => s_vectors.GetProperty("defaults");

    public static TheoryData<string> Cases(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var c in s_vectors.GetProperty(kind).EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> MintCases => Cases("mint");

    public static TheoryData<string> AcceptCases => Cases("accept");

    public static TheoryData<string> RejectCases => Cases("reject");

    private static JsonElement Case(string kind, string name) =>
        s_vectors.GetProperty(kind).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static JsonElement Field(JsonElement c, string name) =>
        c.TryGetProperty(name, out var value) ? value : Defaults.GetProperty(name);

    private static GrantKeys VerifierFor(JsonElement c) => GrantKeys.Parse(
        Field(c, "verify_keys_b64").EnumerateArray().Select(k => k.GetString()!),
        audience: Field(c, "audience").GetString()!,
        maxTtlSeconds: Field(c, "max_ttl_seconds").GetInt64(),
        clockSkewSeconds: Field(c, "clock_skew_seconds").GetInt64());

    [Theory]
    [MemberData(nameof(MintCases))]
    public void MintReproducesTheVectorTokenExactly(string name)
    {
        var c = Case("mint", name);
        var request = c.GetProperty("request");
        var keys = GrantKeys.Parse(
            [c.GetProperty("minting_key_b64").GetString()!],
            audience: c.GetProperty("audience").GetString()!,
            maxTtlSeconds: c.GetProperty("max_ttl_seconds").GetInt64());

        var (token, claims) = SealedGrants.Mint(
            keys,
            request.GetProperty("principal").GetString()!,
            request.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).ToList(),
            request.GetProperty("purpose").GetString()!,
            request.GetProperty("ttl_seconds").GetInt64(),
            now: c.GetProperty("now").GetInt64(),
            grantId: request.GetProperty("grant_id").GetString(),
            nonce: Convert.FromHexString(c.GetProperty("nonce_hex").GetString()!));

        Assert.Equal(c.GetProperty("token").GetString(), token);
        Assert.Equal(c.GetProperty("kid_hex").GetString(), Convert.ToHexString(GrantKeys.KeyId(keys.Keys[0])).ToLowerInvariant());
        var expected = c.GetProperty("claims");
        Assert.Equal(expected.GetProperty("expires_at").GetInt64(), claims.ExpiresAt);
        Assert.Equal(expected.GetProperty("issued_at").GetInt64(), claims.IssuedAt);

        // And the minter's own output verifies, with the same claims.
        var verified = SealedGrants.Verify(keys, token, now: claims.IssuedAt + 1);
        Assert.Equal(claims.Principal, verified.Principal);
        Assert.Equal(claims.Scopes, verified.Scopes);
    }

    [Theory]
    [MemberData(nameof(AcceptCases))]
    public void AcceptCasesVerify(string name)
    {
        var c = Case("accept", name);
        var claims = SealedGrants.Verify(VerifierFor(c), c.GetProperty("token").GetString()!, now: Field(c, "now").GetDouble());
        Assert.NotEqual("", claims.Principal);
    }

    [Theory]
    [MemberData(nameof(RejectCases))]
    public void RejectCasesAreRefusedWithTheRightExpiryFlag(string name)
    {
        var c = Case("reject", name);
        var error = Assert.Throws<GrantInvalidException>(
            () => SealedGrants.Verify(VerifierFor(c), c.GetProperty("token").GetString()!, now: Field(c, "now").GetDouble()));
        Assert.Equal(c.GetProperty("expired").GetBoolean(), error.Expired);
    }

    [Fact]
    public void MalformedKeysRefuseToStart()
    {
        Assert.Throws<ArgumentException>(() => GrantKeys.Parse(["not base64!"]));
        Assert.Throws<ArgumentException>(() => GrantKeys.Parse([Convert.ToBase64String(new byte[31])]));
        var key = Convert.ToBase64String(new byte[32]);
        Assert.Throws<ArgumentException>(() => GrantKeys.Parse([key, key]));
        Assert.Throws<ArgumentException>(() => GrantKeys.Parse([key], maxTtlSeconds: 0));
        // Padding is optional.
        Assert.Single(GrantKeys.Parse([key.TrimEnd('=')]).Keys);
    }

    [Fact]
    public void TheEnvironmentConfiguresGrantsAndUnsetMeansOff()
    {
        Assert.Null(GrantKeys.FromEnvironment(new Dictionary<string, string?>()));
        var a = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        var b = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());
        var keys = GrantKeys.FromEnvironment(new Dictionary<string, string?>
        {
            [GrantKeys.KeysEnvironmentVariable] = $"{a}, {b}",
            [GrantKeys.AudienceEnvironmentVariable] = "aud",
            [GrantKeys.MaxTtlEnvironmentVariable] = "60",
        })!;
        Assert.Equal(2, keys.Keys.Count);
        Assert.Equal(1, keys.Keys[0][0]);
        Assert.Equal(("aud", 60L), (keys.Audience, keys.MaxTtlSeconds));
        Assert.Throws<ArgumentException>(() => GrantKeys.FromEnvironment(new Dictionary<string, string?>
        {
            [GrantKeys.KeysEnvironmentVariable] = "short",
        }));
    }

    private static GrantKeys Keys() => new([Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()], maxTtlSeconds: 3600);

    /// <summary>Expiry is enforced -- after authenticity, with the 60 s skew.</summary>
    [Fact]
    public void ExpiryIsEnforced()
    {
        var keys = Keys();
        var (token, claims) = SealedGrants.Mint(keys, "p", [], "x", 100, now: 1_000);
        SealedGrants.Verify(keys, token, now: claims.ExpiresAt + 59);
        Assert.True(Assert.Throws<GrantInvalidException>(() => SealedGrants.Verify(keys, token, now: claims.ExpiresAt + 60)).Expired);
    }

    /// <summary>The tag is verified: one flipped ciphertext bit is refused, not expiry-flagged.</summary>
    [Fact]
    public void TheTagIsVerified()
    {
        var keys = Keys();
        var (token, _) = SealedGrants.Mint(keys, "p", [], "x", 100, now: 1_000);
        var flipped = token[..^5] + (token[^5] == 'A' ? 'B' : 'A') + token[^4..];
        Assert.False(Assert.Throws<GrantInvalidException>(() => SealedGrants.Verify(keys, flipped, now: 1_001)).Expired);
    }

    /// <summary>The framework's minter: a non-positive lifetime is refused, the lifetime is capped,
    /// and what it mints verifies.</summary>
    [Fact]
    public void TheSealedMinterIssuesVerifiableGrants()
    {
        var keys = Keys();
        var mint = SealedGrants.Minter(keys);
        Assert.Throws<GrantRefusedException>(() => mint("alice", "x", [], 0));
        var grant = mint("alice", "nightly", ["read"], 999_999);
        var claims = SealedGrants.Verify(keys, grant.Token);
        Assert.Equal(("alice", "nightly", grant.GrantId), (claims.Principal, claims.Purpose, claims.GrantId));
        Assert.Equal(3600, claims.ExpiresAt - claims.IssuedAt);
        Assert.Equal(32, grant.GrantId.Length);
    }

    /// <summary>A grant-authenticated caller carries no auth_time, so it cannot mint: grants never
    /// mint grants.</summary>
    [Fact]
    public void AGrantCannotMintAGrant()
    {
        var keys = Keys();
        var impl = new IdentityImpl(grantKeys: keys);
        var grantCaller = new AuthContext("grant", true, "alice",
            new Dictionary<string, object?> { ["grant_id"] = "g", ["scopes"] = new List<string>(), ["purpose"] = "x" });
        Assert.Throws<StaleAuthException>(() => impl.IssueGrant("x", [], 60, IdentityTestDoubles.Ctx(grantCaller)));
        var fresh = IdentityTestDoubles.Auth("alice", authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.StartsWith(SealedGrants.TokenPrefix, impl.IssueGrant("x", [], 60, IdentityTestDoubles.Ctx(fresh)).Token);
    }

    /// <summary>Opt-in: keys on and no IdentityImpl hosts issue_grant alone; no keys hosts nothing.</summary>
    [Fact]
    public void GrantKeysHostIssueGrantAlone()
    {
        var off = new RpcServer(typeof(QueryFarm.VgiRpc.Tests.Server.IGreeter), new QueryFarm.VgiRpc.Tests.Server.Greeter(), grantKeysFromEnvironment: false);
        Assert.Null(off.HostedIdentity);
        var on = new RpcServer(typeof(QueryFarm.VgiRpc.Tests.Server.IGreeter), new QueryFarm.VgiRpc.Tests.Server.Greeter(), grantKeys: Keys());
        Assert.Equal(["issue_grant"], on.MethodsForProtocol(IdentityProtocol.ProtocolName)!.Keys);
        Assert.Throws<ArgumentException>(() => new RpcServer(
            typeof(QueryFarm.VgiRpc.Tests.Server.IGreeter), new QueryFarm.VgiRpc.Tests.Server.Greeter(),
            identity: new IdentityImpl(mintGrant: IdentityTestDoubles.Minter), grantKeys: Keys()));
    }
}
