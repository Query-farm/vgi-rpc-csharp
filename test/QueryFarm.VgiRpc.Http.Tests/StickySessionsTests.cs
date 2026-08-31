using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>Direct unit coverage for <see cref="StickySessions"/>'s registry and token codec —
/// the wire-behavior half is covered end-to-end by the canonical TestSticky group imported into
/// test_csharp_conformance.py (see docs/roadmap.md M10).</summary>
public class StickySessionsTests
{
    private static readonly byte[] s_key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    // -------------------------------------------------------------------
    // Token seal/open round-trip
    // -------------------------------------------------------------------

    [Fact]
    public void SealAndOpenToken_RoundTrips()
    {
        var sessionId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var aad = StickySessions.ComputeAad(null);

        var token = StickySessions.SealToken("server-1", sessionId, expiresAt, s_key, aad);
        var (serverId, decodedSessionId, decodedExpiresAt) = StickySessions.OpenToken(token, s_key, aad);

        Assert.Equal("server-1", serverId);
        Assert.Equal(sessionId, decodedSessionId);
        Assert.Equal(expiresAt, decodedExpiresAt);
    }

    [Fact]
    public void OpenToken_MalformedBase64_ThrowsSessionLost()
    {
        Assert.Throws<SessionLostException>(() => StickySessions.OpenToken("not-valid-base64!!!", s_key, []));
    }

    [Fact]
    public void OpenToken_WrongKey_ThrowsSessionLost()
    {
        var sessionId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var token = StickySessions.SealToken("server-1", sessionId, 0, s_key, []);
        var wrongKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        Assert.Throws<SessionLostException>(() => StickySessions.OpenToken(token, wrongKey, []));
    }

    [Fact]
    public void OpenToken_WrongAad_ThrowsSessionLost()
    {
        var sessionId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var token = StickySessions.SealToken("server-1", sessionId, 0, s_key, StickySessions.ComputeAad(new AuthIdentity("d", "alice")));

        Assert.Throws<SessionLostException>(() => StickySessions.OpenToken(token, s_key, StickySessions.ComputeAad(new AuthIdentity("d", "bob"))));
    }

    [Fact]
    public void ComputeAad_AnonymousVsAuthenticated_Differ()
    {
        var anon = StickySessions.ComputeAad(null);
        var alice = StickySessions.ComputeAad(new AuthIdentity("d", "alice"));
        var bob = StickySessions.ComputeAad(new AuthIdentity("d", "bob"));

        Assert.NotEqual(anon, alice);
        Assert.NotEqual(alice, bob);
    }

    [Fact]
    public void ComputeAad_SameIdentity_IsDeterministic()
    {
        var a = StickySessions.ComputeAad(new AuthIdentity("d", "alice"));
        var b = StickySessions.ComputeAad(new AuthIdentity("d", "alice"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeAad_PeerEvidenceBindingPreventsReplay()
    {
        var first = StickySessions.ComputeAad(new AuthIdentity("d", "alice", "binding-a"));
        var second = StickySessions.ComputeAad(new AuthIdentity("d", "alice", "binding-b"));

        Assert.NotEqual(first, second);
        var sessionId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var token = StickySessions.SealToken("server-1", sessionId, 0, s_key, first);
        Assert.Throws<SessionLostException>(() => StickySessions.OpenToken(token, s_key, second));
    }

    [Fact]
    public void ComputeAad_AnonymousPeerEvidenceBindingPreventsReplay()
    {
        var first = new AuthIdentity("", "", "binding-a", Authenticated: false);
        var second = first with { PeerEvidenceBinding = "binding-b" };

        Assert.NotEqual(StickySessions.ComputeAad(null), StickySessions.ComputeAad(first));
        Assert.NotEqual(StickySessions.ComputeAad(first), StickySessions.ComputeAad(second));
        Assert.NotEqual(StickySessions.PrincipalKey(first), StickySessions.PrincipalKey(second));
    }

    [Fact]
    public void ComputeCallAad_IsDomainSeparatedAndPeerBound()
    {
        var identity = new AuthIdentity("d", "alice", "binding-a");

        Assert.NotEqual(StickySessions.ComputeAad(identity), StickySessions.ComputeCallAad(identity));
        Assert.NotEqual(
            StickySessions.ComputeCallAad(identity),
            StickySessions.ComputeCallAad(identity with { PeerEvidenceBinding = "binding-b" }));
    }

    [Fact]
    public void PrincipalKey_AnonymousVsAuthenticated_Differ()
    {
        Assert.NotEqual(StickySessions.PrincipalKey(null), StickySessions.PrincipalKey(new AuthIdentity("d", "alice")));
    }

    [Fact]
    public void PrincipalKey_IncludesPeerEvidenceBinding()
    {
        Assert.NotEqual(
            StickySessions.PrincipalKey(new AuthIdentity("d", "alice", "binding-a")),
            StickySessions.PrincipalKey(new AuthIdentity("d", "alice", "binding-b")));
    }

    // -------------------------------------------------------------------
    // Registry
    // -------------------------------------------------------------------

    [Fact]
    public void Registry_OpenThenTryGet_ResolvesSameState()
    {
        using var registry = new StickySessionRegistry();
        var state = new object();

        var (sessionId, _) = registry.Open(state, ttl: null, principalKey: "p");
        var entry = registry.TryGet(sessionId, "p");

        Assert.NotNull(entry);
        Assert.Same(state, entry!.State);
    }

    [Fact]
    public void Registry_TryGet_WrongPrincipal_ReturnsNull()
    {
        using var registry = new StickySessionRegistry();
        var (sessionId, _) = registry.Open(new object(), ttl: null, principalKey: "alice");

        Assert.Null(registry.TryGet(sessionId, "bob"));
    }

    [Fact]
    public void Registry_TryGet_UnknownSessionId_ReturnsNull()
    {
        using var registry = new StickySessionRegistry();

        Assert.Null(registry.TryGet(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12), "p"));
    }

    [Fact]
    public void Registry_Close_RemovesEntryAndDisposesState()
    {
        using var registry = new StickySessionRegistry();
        var state = new DisposableProbe();
        var (sessionId, _) = registry.Open(state, ttl: null, principalKey: "p");

        var closed = registry.Close(sessionId);

        Assert.True(closed);
        Assert.True(state.Disposed);
        Assert.Null(registry.TryGet(sessionId, "p"));
    }

    [Fact]
    public void Registry_Close_UnknownSessionId_ReturnsFalse()
    {
        using var registry = new StickySessionRegistry();

        Assert.False(registry.Close(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)));
    }

    [Fact]
    public void Registry_TryGet_ExpiredEntry_EvictsAndDisposes()
    {
        using var registry = new StickySessionRegistry(defaultTtl: TimeSpan.FromMilliseconds(1));
        var state = new DisposableProbe();
        var (sessionId, _) = registry.Open(state, ttl: TimeSpan.FromMilliseconds(1), principalKey: "p");

        Thread.Sleep(50);

        Assert.Null(registry.TryGet(sessionId, "p"));
        Assert.True(state.Disposed);
    }

    [Fact]
    public void Registry_DrainExpired_EvictsPastTtlOnly()
    {
        using var registry = new StickySessionRegistry();
        var (expiredId, _) = registry.Open(new object(), ttl: TimeSpan.FromMilliseconds(1), principalKey: "p");
        var (liveId, _) = registry.Open(new object(), ttl: TimeSpan.FromMinutes(5), principalKey: "p");

        Thread.Sleep(50);
        var evicted = registry.DrainExpired();

        Assert.Equal(1, evicted);
        Assert.Null(registry.TryGet(expiredId, "p"));
        Assert.NotNull(registry.TryGet(liveId, "p"));
    }

    [Fact]
    public void Registry_Open_WhileDraining_ThrowsServerDraining()
    {
        using var registry = new StickySessionRegistry();
        registry.Drain();

        Assert.Throws<ServerDrainingException>(() => registry.Open(new object(), ttl: null, principalKey: "p"));
    }

    [Fact]
    public void Registry_ClearDrain_AllowsOpensAgain()
    {
        using var registry = new StickySessionRegistry();
        registry.Drain();
        registry.ClearDrain();

        var (sessionId, _) = registry.Open(new object(), ttl: null, principalKey: "p");

        Assert.NotNull(registry.TryGet(sessionId, "p"));
    }

    [Fact]
    public void Registry_ExistingSession_SurvivesDrain()
    {
        using var registry = new StickySessionRegistry();
        var (sessionId, _) = registry.Open(new object(), ttl: null, principalKey: "p");

        registry.Drain();

        Assert.NotNull(registry.TryGet(sessionId, "p"));
    }

    [Fact]
    public void Registry_Shutdown_DisposesAllAndClears()
    {
        using var registry = new StickySessionRegistry();
        var a = new DisposableProbe();
        var b = new DisposableProbe();
        registry.Open(a, ttl: null, principalKey: "p");
        registry.Open(b, ttl: null, principalKey: "p");

        registry.Shutdown();

        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Registry_DefaultTtl_DefaultsTo300Seconds()
    {
        using var registry = new StickySessionRegistry();

        Assert.Equal(TimeSpan.FromSeconds(300), registry.DefaultTtl);
    }

    [Fact]
    public void Registry_CloseState_ExceptionDuringDispose_IsSuppressed()
    {
        using var registry = new StickySessionRegistry();
        var (sessionId, _) = registry.Open(new ThrowingDisposable(), ttl: null, principalKey: "p");

        var exception = Record.Exception(() => registry.Close(sessionId));

        Assert.Null(exception);
    }

    private sealed class DisposableProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("boom");
    }
}
