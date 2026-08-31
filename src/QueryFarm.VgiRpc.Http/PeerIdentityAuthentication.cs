using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Http;

/// <summary>Composes ASP.NET application authentication with provider-neutral transport evidence.</summary>
public static class PeerIdentityAuthentication
{
    private const string AuthItem = "vgi_rpc.peer.auth";
    private const string EvidenceItem = "vgi_rpc.peer.evidence";
    private const string PhysicalPeerItem = "vgi_rpc.peer.physical_connection";

    private sealed record PhysicalConnection(
        string? RemoteAddress,
        int RemotePort,
        string? LocalAddress,
        int LocalPort);

    /// <summary>
    /// Snapshots the physical socket endpoints before routing middleware can rewrite them.
    /// Register this before <c>UseForwardedHeaders</c> whenever peer identity is enabled.
    /// </summary>
    public static IApplicationBuilder UseVgiRpcPhysicalPeerSnapshot(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            CapturePhysicalPeer(context);
            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Captures the current socket endpoints. Hosting adapters and tests may
    /// call this directly, but it must run before any forwarded-address rewrite.
    /// </summary>
    public static void CapturePhysicalPeer(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = context.Connection;
        context.Items[PhysicalPeerItem] = new PhysicalConnection(
            connection.RemoteIpAddress?.ToString(),
            connection.RemotePort,
            connection.LocalIpAddress?.ToString(),
            connection.LocalPort);
    }

    public static RpcHttpEndpoints.AuthenticateDelegate Compose(
        RpcHttpEndpoints.AuthenticateDelegate? applicationAuthenticate,
        IReadOnlyList<IPeerIdentityProvider> providers,
        PeerAuthenticationPolicy policy,
        string? serviceName = null,
        TimeSpan? timeout = null,
        int maxProviderConcurrency = 64)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(policy);
        if (maxProviderConcurrency <= 0 || maxProviderConcurrency < providers.Count)
            throw new ArgumentOutOfRangeException(nameof(maxProviderConcurrency),
                "provider concurrency must accommodate one complete resolution fanout");
        var providerTimeout = timeout ?? TimeSpan.FromSeconds(5);
        if (providerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var providerNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.Provider) || !providerNames.Add(provider.Provider))
                throw new ArgumentException("peer identity providers must have unique non-empty names", nameof(providers));
        }
        providers = Array.AsReadOnly(providers.ToArray());
        var providerSlots = new SemaphoreSlim(maxProviderConcurrency, maxProviderConcurrency);
        return async context =>
        {
        AuthFailure? missing = null;
        if (applicationAuthenticate is not null)
        {
            try
            {
                await applicationAuthenticate(context).ConfigureAwait(false);
            }
            catch (AuthFailure failure) when (failure.Reason == AuthReason.MissingCredential)
            {
                missing = failure;
            }
        }

        var identity = AuthIdentity.GetFrom(context);
        var existing = identity is null
            ? AuthContext.Anonymous
            : new AuthContext(identity.Domain, identity.Authenticated, identity.Principal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(providerTimeout);
        var headers = context.Request.Headers.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.Select(value => value ?? "").ToArray()),
            StringComparer.OrdinalIgnoreCase);
        var physical = context.Items[PhysicalPeerItem] as PhysicalConnection;
        var resolution = physical is null ? null : new PeerResolutionContext(
            "http",
            physical.RemoteAddress,
            destinationAddress: FormatAddress(physical.LocalAddress, physical.LocalPort),
            authority: context.Request.Host.Value,
            serviceName: serviceName,
            headers: headers,
            metadata: new Dictionary<string, object?> { ["request_path"] = context.Request.Path.Value ?? "" },
            deadline: DateTimeOffset.UtcNow + providerTimeout,
            sourceEndpoint: FormatAddress(physical.RemoteAddress, physical.RemotePort));
        var results = resolution is null
            ? providers.Select(provider =>
                new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable)).ToArray()
            : await Task.WhenAll(providers.Select(async provider =>
        {
            if (!await providerSlots.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
                return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable);
            Task<PeerIdentityResult> providerTask;
            try
            {
                providerTask = provider.ResolveAsync(resolution, deadline.Token).AsTask();
            }
            catch (PeerIdentityRejectedException)
            {
                providerSlots.Release();
                return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Invalid);
            }
            catch (PeerIdentityUnavailableException)
            {
                providerSlots.Release();
                return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable);
            }
            catch
            {
                providerSlots.Release();
                throw new InvalidOperationException("peer identity provider failed");
            }
            try
            {
                var result = await providerTask.WaitAsync(providerTimeout, context.RequestAborted).ConfigureAwait(false);
                if (result is null || !StringComparer.Ordinal.Equals(provider.Provider, result.Provider))
                    throw new InvalidOperationException("peer identity provider result mismatch");
                providerSlots.Release();
                return result;
            }
            catch (TimeoutException)
            {
                ReleaseProviderSlotWhenComplete(providerTask, providerSlots);
                return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                if (providerTask.IsCompleted) providerSlots.Release();
                else ReleaseProviderSlotWhenComplete(providerTask, providerSlots);
                return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable);
            }
            catch (OperationCanceledException)
            {
                if (providerTask.IsCompleted) providerSlots.Release();
                else ReleaseProviderSlotWhenComplete(providerTask, providerSlots);
                throw;
            }
            catch (PeerIdentityRejectedException)
            {
                providerSlots.Release();
                return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Invalid);
            }
            catch (PeerIdentityUnavailableException)
            {
                providerSlots.Release();
                return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable);
            }
            catch
            {
                providerSlots.Release();
                throw new InvalidOperationException("peer identity provider failed");
            }
        })).ConfigureAwait(false);

        var evidence = new PeerEvidenceSet(results);
        AuthContext auth;
        try
        {
            auth = await policy(evidence, existing).ConfigureAwait(false);
        }
        catch (PeerIdentityRejectedException)
        {
            throw new AuthFailure(AuthReason.InvalidCredential, "peer identity rejected");
        }
        catch (PeerIdentityUnavailableException unavailable)
        {
            throw new PeerIdentityUnavailableException(
                "peer identity unavailable", unavailable.RetryAfterSeconds);
        }
        if (missing is not null && !auth.Authenticated) throw missing;
        context.Items[AuthItem] = auth;
        context.Items[EvidenceItem] = evidence;
        var binding = EvidenceBinding(auth);
        if (auth.Authenticated || binding is not null)
        {
            AuthIdentity.SetOn(context, auth.Domain, auth.Principal ?? "", binding, auth.Authenticated);
        }
        };
    }

    public static AuthContext GetAuth(HttpContext context) => context.Items[AuthItem] as AuthContext
        ?? (AuthIdentity.GetFrom(context) is { } identity
            ? new AuthContext(identity.Domain, identity.Authenticated, identity.Principal)
            : AuthContext.Anonymous);

    public static PeerEvidenceSet GetEvidence(HttpContext context) =>
        context.Items[EvidenceItem] as PeerEvidenceSet ?? PeerEvidenceSet.Empty;

    private static string? EvidenceBinding(AuthContext auth) =>
        auth.Claims.TryGetValue("peer_evidence_binding", out var value) && value is string text && text.Length > 0 ? text : null;

    private static string? FormatAddress(string? address, int port) => address is null ? null
        : address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";

    private static void ReleaseProviderSlotWhenComplete(
        Task<PeerIdentityResult> providerTask, SemaphoreSlim providerSlots) =>
        _ = providerTask.ContinueWith(
            task =>
            {
                _ = task.Exception;
                providerSlots.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
