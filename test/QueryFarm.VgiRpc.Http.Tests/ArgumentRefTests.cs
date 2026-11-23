using System.Collections.Concurrent;
using System.Security.Cryptography;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Streaming;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// Argument references (STATE_REF_V1.md §10.2), fix 1 (the advertised upload cap never exceeds the
/// fetch cap), the 16 KiB request-cap floor and the 6 h stream duration. The shared suite drives
/// grants and resolution end to end; these pin the scope binding, the failure kinds and the
/// startup rules.
/// </summary>
public sealed class ArgumentRefTests
{
    private static readonly byte[] s_key = RandomNumberGenerator.GetBytes(32);
    private static readonly AuthIdentity s_alice = new("conformance", "alice");
    private static readonly byte[] s_payload = "a large argument"u8.ToArray();
    private static readonly string s_sha = Convert.ToHexStringLower(SHA256.HashData(s_payload));

    [Fact]
    public void Mint_OpensForTheSamePrincipalOnly()
    {
        var reference = ArgumentRefs.Mint(s_key, s_alice, s_payload.Length, s_sha, "object-1");
        Assert.Equal(new ArgumentRef(s_payload.Length, s_sha, "object-1"), ArgumentRefs.Open(s_key, s_alice, reference));

        AssertInvalid(() => ArgumentRefs.Open(s_key, new AuthIdentity("conformance", "bob"), reference));
        AssertInvalid(() => ArgumentRefs.Open(s_key, null, reference));
        AssertInvalid(() => ArgumentRefs.Open(RandomNumberGenerator.GetBytes(32), s_alice, reference));
        // The peer-evidence binding digest is part of the AAD: a ref does not cross bindings.
        AssertInvalid(() => ArgumentRefs.Open(s_key, s_alice with { PeerEvidenceBinding = "binding-x" }, reference));
        var tampered = reference.ToArray();
        tampered[^1] ^= 1;
        AssertInvalid(() => ArgumentRefs.Open(s_key, s_alice, tampered));
    }

    [Fact]
    public void Mint_IsNotOpenableAsAnotherSealedKind()
    {
        // A stream call token is sealed under the call AAD for a protocol; an argument ref under the
        // argref prefix and the server scope. Neither opens as the other.
        var reference = ArgumentRefs.Mint(s_key, s_alice, 1, s_sha, "o");
        Assert.Throws<Crypto.SealException>(() => Crypto.Open(reference, s_key, StickySessions.ComputeCallAad(s_alice, ArgumentRefs.ServerScope)));
        var callToken = Crypto.Seal(new byte[16], s_key, StickySessions.ComputeCallAad(s_alice, "Proto"));
        AssertInvalid(() => ArgumentRefs.Open(s_key, s_alice, callToken));
    }

    [Theory]
    [InlineData(0L, "o")]
    [InlineData(1L, "")]
    public void Mint_RefusesBadValues(long size, string objectId) =>
        Assert.ThrowsAny<ArgumentException>(() => ArgumentRefs.Mint(s_key, s_alice, size, s_sha, objectId));

    [Fact]
    public async Task Resolve_FetchesOnceThenServesTheCache()
    {
        var store = new MemoryStore();
        var id = await store.PutObjectAsync(s_payload, CancellationToken.None);
        var cache = new ObjectCache(1 << 20);
        var reference = ArgumentRefs.Mint(s_key, s_alice, s_payload.Length, s_sha, id);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(s_payload, await ArgumentRefs.ResolveAsync(reference, s_payload.Length, s_sha, s_key, s_alice, store, 1 << 20, cache));
        }

        Assert.Equal(1, store.Gets);
    }

    [Fact]
    public async Task Resolve_RefusesClaimsThatDifferFromTheSealedValues()
    {
        var store = new MemoryStore();
        var id = await store.PutObjectAsync(s_payload, CancellationToken.None);
        var reference = ArgumentRefs.Mint(s_key, s_alice, s_payload.Length, s_sha, id);
        await AssertKindAsync(StateRefErrors.InvalidArgumentsRefKind,
            ArgumentRefs.ResolveAsync(reference, s_payload.Length + 1, s_sha, s_key, s_alice, store, 1 << 20, new ObjectCache(1024)));
        await AssertKindAsync(StateRefErrors.InvalidArgumentsRefKind,
            ArgumentRefs.ResolveAsync(reference, s_payload.Length, new string('0', 64), s_key, s_alice, store, 1 << 20, new ObjectCache(1024)));
        await AssertKindAsync(StateRefErrors.InvalidArgumentsRefKind,
            ArgumentRefs.ResolveAsync(reference, s_payload.Length, s_sha, s_key, s_alice, null, 1 << 20, new ObjectCache(1024)));
        await AssertKindAsync(StateRefErrors.InvalidArgumentsRefKind,
            ArgumentRefs.ResolveAsync(reference, s_payload.Length, s_sha, s_key, s_alice, store, 4, new ObjectCache(1024)));
        Assert.Equal(0, store.Gets);
    }

    [Fact]
    public async Task Resolve_MapsStorageOutcomesToTheirKinds()
    {
        var store = new MemoryStore();
        var reference = ArgumentRefs.Mint(s_key, s_alice, s_payload.Length, s_sha, "missing");
        await AssertKindAsync(StateRefErrors.ArgumentsRefMissingKind,
            ArgumentRefs.ResolveAsync(reference, s_payload.Length, s_sha, s_key, s_alice, store, 1 << 20, new ObjectCache(1024)));

        store.Objects["swapped"] = "a different value"u8.ToArray();
        reference = ArgumentRefs.Mint(s_key, s_alice, s_payload.Length, s_sha, "swapped");
        await AssertKindAsync(StateRefErrors.ArgumentsRefIntegrityKind,
            ArgumentRefs.ResolveAsync(reference, s_payload.Length, s_sha, s_key, s_alice, store, 1 << 20, new ObjectCache(1024)));

        store.Objects["swapped"] = s_payload.Reverse().ToArray();
        await AssertKindAsync(StateRefErrors.ArgumentsRefIntegrityKind,
            ArgumentRefs.ResolveAsync(reference, s_payload.Length, s_sha, s_key, s_alice, store, 1 << 20, new ObjectCache(1024)));

        store.Fail = true;
        await AssertKindAsync(StateRefErrors.StorageUnavailableKind,
            ArgumentRefs.ResolveAsync(reference, s_payload.Length, s_sha, s_key, s_alice, store, 1 << 20, new ObjectCache(1024)));
    }

    [Fact]
    public void UploadCap_DefaultsToTheFetchCap()
    {
        var resolved = RpcHttpEndpoints.ResolveUploadCap(new ExternalizationOptions { UploadUrlProvider = new NullProvider() });
        Assert.Equal(FetchConfig.DefaultMaxFetchBytes, resolved!.MaxUploadBytes);

        resolved = RpcHttpEndpoints.ResolveUploadCap(new ExternalizationOptions
        {
            UploadUrlProvider = new NullProvider(),
            External = new ServerExternalConfig { FetchConfig = new FetchConfig { MaxFetchBytes = 1000 } },
        });
        Assert.Equal(1000, resolved!.MaxUploadBytes);
    }

    [Fact]
    public void UploadCap_AboveAnExplicitFetchCap_RefusesStartup() =>
        Assert.Throws<ArgumentException>(() => RpcHttpEndpoints.ResolveUploadCap(new ExternalizationOptions
        {
            UploadUrlProvider = new NullProvider(),
            MaxUploadBytes = 2000,
            External = new ServerExternalConfig { FetchConfig = new FetchConfig { MaxFetchBytes = 1000 } },
        }));

    [Fact]
    public void UploadCap_AboveTheDefaultFetchCap_RaisesTheFetchCap()
    {
        var upload = FetchConfig.DefaultMaxFetchBytes * 2;
        var resolved = RpcHttpEndpoints.ResolveUploadCap(new ExternalizationOptions
        {
            UploadUrlProvider = new NullProvider(),
            MaxUploadBytes = upload,
            External = new ServerExternalConfig(),
        });
        Assert.Equal(upload, resolved!.MaxUploadBytes);
        Assert.Equal(upload, resolved.External!.FetchConfig.MaxFetchBytes);
    }

    [Fact]
    public void UploadCap_WithoutAProvider_IsLeftAlone()
    {
        var options = new ExternalizationOptions { MaxRequestBytes = 10 };
        Assert.Same(options, RpcHttpEndpoints.ResolveUploadCap(options));
    }

    [Theory]
    [InlineData(9000L, true)]
    [InlineData(16383L, true)]
    [InlineData(16384L, false)]
    [InlineData(null, false)]
    public void RequestFloor_WarnsBelow16KiB(long? cap, bool warns)
    {
        var warning = RpcHttpEndpoints.RequestFloorWarning(cap);
        Assert.Equal(warns, warning is not null);
        if (warns)
        {
            Assert.Equal($"max_request_bytes ({cap}) is below 16384: the cursor rule is disabled and stream continuations may not fit", warning);
        }
    }

    [Theory]
    [InlineData(null, 16384)]
    [InlineData("4096", 4096)]
    public void StateRefInlineBytes_ReadsTheEnvironment(string? raw, int expected) =>
        Assert.Equal(expected, RpcHttpEndpoints.StateRefInlineBytesFromEnvironment(_ => raw));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void StateRefInlineBytes_RefusesNonPositiveValues(string raw) =>
        Assert.Throws<ArgumentException>(() => RpcHttpEndpoints.StateRefInlineBytesFromEnvironment(_ => raw));

    [Fact]
    public async Task Registry_RefusesStreamsOlderThanTheMaximumDuration()
    {
        var registry = new StreamCallRegistry(maxAge: TimeSpan.FromMilliseconds(50));
        var key = registry.Register(new RpcStream<NoState>(new Apache.Arrow.Schema([], null), new NoState()));
        Assert.True(registry.TryGet(key, out _));
        await Task.Delay(120, TestContext.Current.CancellationToken);
        Assert.False(registry.TryGet(key, out _));
        Assert.Equal(TimeSpan.FromSeconds(21600), StreamCallRegistry.DefaultMaxAge);
    }

    private static void AssertInvalid(Action open)
    {
        var error = Assert.Throws<RpcException>(open);
        Assert.Equal(StateRefErrors.InvalidArgumentsRefKind, error.ErrorKind);
    }

    private static async Task AssertKindAsync(string kind, Task<byte[]> resolve)
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => resolve);
        Assert.Equal(kind, error.ErrorKind);
    }

    public sealed class NoState : ExchangeState
    {
        public override Task ExchangeAsync(Wire.AnnotatedBatch input, OutputCollector output, Server.ICallContext? ctx, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullProvider : IUploadUrlProvider
    {
        public Task<UploadUrl> GenerateUploadUrlAsync(long size, string sha256Hex, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryStore : IObjectStore
    {
        private int _gets;

        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

        public bool Fail { get; set; }

        public int Gets => Volatile.Read(ref _gets);

        public Task<string> PutObjectAsync(byte[] data, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            Objects[id] = data;
            return Task.FromResult(id);
        }

        public Task<byte[]> GetAsync(string objectId, long? maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _gets);
            if (Fail)
            {
                throw new HttpRequestException("503 from storage");
            }

            return Objects.TryGetValue(objectId, out var data)
                ? Task.FromResult(data)
                : throw new ObjectMissingException("gone");
        }
    }
}
