using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Identity;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class PeerIdentityAuthenticationTests
{
    [Fact]
    public void ComposeRejectsDuplicateProvidersBeforeServingRequests()
    {
        var first = new StubProvider("spiffe", _ => throw new InvalidOperationException());
        var second = new StubProvider("spiffe", _ => throw new InvalidOperationException());

        Assert.Throws<ArgumentException>(() => PeerIdentityAuthentication.Compose(
            null, [first, second], PeerAuthenticationPolicies.Primary("spiffe")));
    }

    [Fact]
    public void ComposeRejectsCapacityBelowProviderFanout()
    {
        var first = new StubProvider("first", _ =>
            ValueTask.FromResult(new PeerIdentityResult("first", PeerIdentityStatus.NoMatch)));
        var second = new StubProvider("second", _ =>
            ValueTask.FromResult(new PeerIdentityResult("second", PeerIdentityStatus.NoMatch)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PeerIdentityAuthentication.Compose(
            null, [first, second], PeerAuthenticationPolicies.Observe, maxProviderConcurrency: 1));
    }

    [Fact]
    public async Task PrimaryPolicyCreatesAuthAndExposesEvidence()
    {
        var peer = StablePeer("spiffe", "spiffe://example.org", "spiffe://example.org/workload");
        var authenticate = PeerIdentityAuthentication.Compose(
            null,
            [new StubProvider("spiffe", _ => ValueTask.FromResult(PeerIdentityResult.Available(peer)))],
            PeerAuthenticationPolicies.Primary("spiffe"));
        var context = NewContext();

        await authenticate(context);

        var auth = PeerIdentityAuthentication.GetAuth(context);
        Assert.True(auth.Authenticated);
        Assert.Equal(peer.CanonicalPrincipal, auth.Principal);
        Assert.Single(PeerIdentityAuthentication.GetEvidence(context).Identities);
        var httpIdentity = AuthIdentity.GetFrom(context);
        Assert.NotNull(httpIdentity);
        Assert.False(string.IsNullOrEmpty(httpIdentity!.PeerEvidenceBinding));
    }

    [Fact]
    public async Task HttpResolutionSeparatesTrustedPeerIpFromWhoIsEndpoint()
    {
        var provider = new StubProvider("capture", _ =>
            ValueTask.FromResult(new PeerIdentityResult("capture", PeerIdentityStatus.NoMatch)));
        var authenticate = PeerIdentityAuthentication.Compose(
            null, [provider], PeerAuthenticationPolicies.Observe);

        await authenticate(NewContext());

        Assert.Equal("127.0.0.1", provider.LastContext!.ImmediatePeer);
        Assert.Equal("127.0.0.1:4242", provider.LastContext.SourceEndpoint);
        Assert.Equal("127.0.0.1:9400", provider.LastContext.DestinationAddress);
    }

    [Fact]
    public async Task MissingPhysicalPeerSnapshotIsUnavailableEvidenceInObserveMode()
    {
        var provider = new StubProvider("capture", _ =>
            ValueTask.FromResult(new PeerIdentityResult("capture", PeerIdentityStatus.NoMatch)));
        var authenticate = PeerIdentityAuthentication.Compose(
            null, [provider], PeerAuthenticationPolicies.Observe);
        var context = NewContext(capturePhysicalPeer: false);

        await authenticate(context);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(PeerIdentityStatus.Unavailable,
            PeerIdentityAuthentication.GetEvidence(context).Status("capture"));
    }

    [Fact]
    public async Task MissingPhysicalPeerSnapshotAllowsValidApplicationAnyOf()
    {
        var provider = new StubProvider("spiffe", _ =>
            ValueTask.FromResult(new PeerIdentityResult("spiffe", PeerIdentityStatus.NoMatch)));
        RpcHttpEndpoints.AuthenticateDelegate application = context =>
        {
            AuthIdentity.SetOn(context, "bearer", "alice");
            return Task.CompletedTask;
        };
        var authenticate = PeerIdentityAuthentication.Compose(
            application, [provider], PeerAuthenticationPolicies.AnyOf("spiffe"));
        var context = NewContext(capturePhysicalPeer: false);

        await authenticate(context);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal("alice", PeerIdentityAuthentication.GetAuth(context).Principal);
        Assert.Equal(PeerIdentityStatus.Unavailable,
            PeerIdentityAuthentication.GetEvidence(context).Status("spiffe"));
    }

    [Fact]
    public async Task MissingPhysicalPeerSnapshotFailsClosedWhenProviderIsRequired()
    {
        var provider = new StubProvider("spiffe", _ =>
            ValueTask.FromResult(new PeerIdentityResult("spiffe", PeerIdentityStatus.NoMatch)));
        var authenticate = PeerIdentityAuthentication.Compose(
            null, [provider], PeerAuthenticationPolicies.Require("spiffe"));

        await Assert.ThrowsAsync<PeerIdentityUnavailableException>(() =>
            authenticate(NewContext(capturePhysicalPeer: false)));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task PhysicalPeerSnapshotPrecedesForwardedAddressRewriting()
    {
        var provider = new StubProvider("capture", _ =>
            ValueTask.FromResult(new PeerIdentityResult("capture", PeerIdentityStatus.NoMatch)));
        var authenticate = PeerIdentityAuthentication.Compose(
            null, [provider], PeerAuthenticationPolicies.Observe);
        var application = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        application.UseVgiRpcPhysicalPeerSnapshot();
        application.Run(async context =>
        {
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
            context.Connection.RemotePort = 12345;
            await authenticate(context);
        });
        var context = NewContext();

        await application.Build()(context);

        Assert.Equal("127.0.0.1", provider.LastContext!.ImmediatePeer);
        Assert.Equal("127.0.0.1:4242", provider.LastContext.SourceEndpoint);
    }

    [Fact]
    public async Task NullPhysicalEndpointsDoNotFallBackToLaterRewrittenValues()
    {
        var provider = new StubProvider("capture", _ =>
            ValueTask.FromResult(new PeerIdentityResult("capture", PeerIdentityStatus.NoMatch)));
        var authenticate = PeerIdentityAuthentication.Compose(
            null, [provider], PeerAuthenticationPolicies.Observe);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;
        context.Connection.RemotePort = 0;
        context.Connection.LocalIpAddress = null;
        context.Connection.LocalPort = 0;
        PeerIdentityAuthentication.CapturePhysicalPeer(context);

        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        context.Connection.RemotePort = 12345;
        context.Connection.LocalIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        context.Connection.LocalPort = 443;
        await authenticate(context);

        Assert.Null(provider.LastContext!.ImmediatePeer);
        Assert.Null(provider.LastContext.SourceEndpoint);
        Assert.Null(provider.LastContext.DestinationAddress);
    }

    [Fact]
    public async Task AnonymousRequiredCapabilityEvidenceStillBindsState()
    {
        var capability = new PeerIdentity(
            "tailscale", "serve_proxy", IdentityAssurance.ConfiguredProxy, "tailnet:example", "http",
            capabilities: new Dictionary<string, object?> { ["query.farm/run"] = Array.Empty<object>() },
            capabilitiesVerified: true);
        var authenticate = PeerIdentityAuthentication.Compose(
            null,
            [new StubProvider("tailscale", _ =>
                ValueTask.FromResult(PeerIdentityResult.Available(capability)))],
            PeerAuthenticationPolicies.Require("tailscale"));
        var context = NewContext();

        await authenticate(context);

        Assert.False(PeerIdentityAuthentication.GetAuth(context).Authenticated);
        var identity = Assert.IsType<AuthIdentity>(AuthIdentity.GetFrom(context));
        Assert.False(identity.Authenticated);
        Assert.False(string.IsNullOrEmpty(identity.PeerEvidenceBinding));
        Assert.NotEqual(
            StickySessions.ComputeAad(null),
            StickySessions.ComputeAad(identity));
        Assert.NotEqual(
            StickySessions.ComputeAad(identity),
            StickySessions.ComputeAad(identity with { PeerEvidenceBinding = "different" }));
    }

    [Fact]
    public async Task InvalidApplicationCredentialNeverFallsBackToPeerIdentity()
    {
        var provider = new StubProvider("spiffe", _ => throw new InvalidOperationException("must not run"));
        RpcHttpEndpoints.AuthenticateDelegate application = _ =>
            throw new AuthFailure(AuthReason.InvalidCredential, "rejected");
        var authenticate = PeerIdentityAuthentication.Compose(
            application,
            [provider],
            PeerAuthenticationPolicies.Primary("spiffe"));

        var failure = await Assert.ThrowsAsync<AuthFailure>(() => authenticate(NewContext()));

        Assert.Equal(AuthReason.InvalidCredential, failure.Reason);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ObserveDoesNotEraseMissingApplicationCredential()
    {
        RpcHttpEndpoints.AuthenticateDelegate application = _ =>
            throw new AuthFailure(AuthReason.MissingCredential, "missing");
        var authenticate = PeerIdentityAuthentication.Compose(
            application,
            [],
            PeerAuthenticationPolicies.Observe);

        var failure = await Assert.ThrowsAsync<AuthFailure>(() => authenticate(NewContext()));

        Assert.Equal(AuthReason.MissingCredential, failure.Reason);
    }

    [Fact]
    public async Task ProviderTimeoutIsUnavailable()
    {
        var provider = new StubProvider("spiffe", async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var authenticate = PeerIdentityAuthentication.Compose(
            null,
            [provider],
            PeerAuthenticationPolicies.Primary("spiffe"),
            timeout: TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<PeerIdentityUnavailableException>(() => authenticate(NewContext()));
    }

    [Fact]
    public async Task TimedOutProviderRetainsCapacityUntilItActuallyExits()
    {
        var never = new TaskCompletionSource<PeerIdentityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubProvider("spiffe", _ => new ValueTask<PeerIdentityResult>(never.Task));
        var authenticate = PeerIdentityAuthentication.Compose(
            null,
            [provider],
            PeerAuthenticationPolicies.Primary("spiffe"),
            timeout: TimeSpan.FromMilliseconds(20),
            maxProviderConcurrency: 1);

        await Assert.ThrowsAsync<PeerIdentityUnavailableException>(() => authenticate(NewContext()));
        await Assert.ThrowsAsync<PeerIdentityUnavailableException>(() => authenticate(NewContext()));
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task UnavailableProviderIsPolicyInputForObserveAndValidApplicationAnyOf()
    {
        static StubProvider Never() => new("spiffe", _ =>
            new ValueTask<PeerIdentityResult>(new TaskCompletionSource<PeerIdentityResult>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task));
        var observe = PeerIdentityAuthentication.Compose(
            null, [Never()], PeerAuthenticationPolicies.Observe,
            timeout: TimeSpan.FromMilliseconds(20), maxProviderConcurrency: 1);
        var observedContext = NewContext();
        await observe(observedContext);
        Assert.Equal(PeerIdentityStatus.Unavailable,
            PeerIdentityAuthentication.GetEvidence(observedContext).Status("spiffe"));

        RpcHttpEndpoints.AuthenticateDelegate application = context =>
        {
            AuthIdentity.SetOn(context, "bearer", "alice");
            return Task.CompletedTask;
        };
        var anyOf = PeerIdentityAuthentication.Compose(
            application, [Never()], PeerAuthenticationPolicies.AnyOf("spiffe"),
            timeout: TimeSpan.FromMilliseconds(20), maxProviderConcurrency: 1);
        var anyOfContext = NewContext();
        await anyOf(anyOfContext);
        Assert.True(PeerIdentityAuthentication.GetAuth(anyOfContext).Authenticated);
        Assert.Equal("alice", PeerIdentityAuthentication.GetAuth(anyOfContext).Principal);
    }

    [Fact]
    public async Task MismatchedResultDoesNotCorruptProviderCapacity()
    {
        var calls = 0;
        var provider = new StubProvider("expected", _ => ValueTask.FromResult(
            ++calls == 1
                ? new PeerIdentityResult("wrong", PeerIdentityStatus.NoMatch)
                : new PeerIdentityResult("expected", PeerIdentityStatus.NoMatch)));
        var authenticate = PeerIdentityAuthentication.Compose(
            null, [provider], PeerAuthenticationPolicies.Observe, maxProviderConcurrency: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authenticate(NewContext()));
        await authenticate(NewContext());
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task CustomPolicyDetailsAreRedactedAtTheCompositionBoundary()
    {
        const string secret = "raw-capability-policy-secret";
        PeerAuthenticationPolicy rejected = (_, _) =>
            throw new PeerIdentityRejectedException(secret);
        var reject = PeerIdentityAuthentication.Compose(null, [], rejected);
        var failure = await Assert.ThrowsAsync<AuthFailure>(() => reject(NewContext()));
        Assert.Equal("peer identity rejected", failure.Detail);
        Assert.DoesNotContain(secret, failure.Detail, StringComparison.Ordinal);

        PeerAuthenticationPolicy unavailable = (_, _) =>
            throw new PeerIdentityUnavailableException(secret, 17);
        var retry = PeerIdentityAuthentication.Compose(null, [], unavailable);
        var outage = await Assert.ThrowsAsync<PeerIdentityUnavailableException>(() => retry(NewContext()));
        Assert.Equal("peer identity unavailable", outage.Message);
        Assert.Equal(17, outage.RetryAfterSeconds);
        Assert.DoesNotContain(secret, outage.Message, StringComparison.Ordinal);
    }

    private static DefaultHttpContext NewContext(bool capturePhysicalPeer = true)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Connection.RemotePort = 4242;
        context.Connection.LocalIpAddress = System.Net.IPAddress.Loopback;
        context.Connection.LocalPort = 9400;
        context.Request.Host = new HostString("worker.example", 9400);
        if (capturePhysicalPeer) PeerIdentityAuthentication.CapturePhysicalPeer(context);
        return context;
    }

    private static PeerIdentity StablePeer(string provider, string issuer, string subject) => new(
        provider,
        "test",
        IdentityAssurance.CryptographicPeer,
        issuer,
        "http",
        PeerSubjectKind.Workload,
        subject,
        SubjectStability.Stable,
        subjectVerified: true);

    private sealed class StubProvider(
        string provider,
        Func<CancellationToken, ValueTask<PeerIdentityResult>> resolve) : IPeerIdentityProvider
    {
        public string Provider { get; } = provider;
        public int CallCount { get; private set; }
        public PeerResolutionContext? LastContext { get; private set; }

        public ValueTask<PeerIdentityResult> ResolveAsync(
            PeerResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastContext = context;
            return resolve(cancellationToken);
        }
    }
}
