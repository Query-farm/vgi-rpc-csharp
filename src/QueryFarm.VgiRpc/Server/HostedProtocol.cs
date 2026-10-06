using QueryFarm.VgiRpc.Reflection;

namespace QueryFarm.VgiRpc.Server;

/// <summary>
/// One additional application protocol for an <see cref="RpcServer"/> to host beside its primary
/// -- the <c>(protocol, implementation)</c> pair of WIRE_PROTOCOL.md §3.1, "Hosting several
/// application protocols".
/// </summary>
/// <param name="ServiceInterface">The protocol's contract interface. Its wire name is its
/// <see cref="Attributes.ProtocolNameAttribute"/>, else the type name with C#'s <c>I</c> prefix
/// stripped; either way it may not claim the reserved <c>vgi_rpc.</c> prefix.</param>
/// <param name="Implementation">The object calls to this protocol dispatch to. Must implement
/// <paramref name="ServiceInterface"/>.</param>
/// <param name="ProtocolVersion">The protocol's own <c>MAJOR.MINOR.PATCH</c> version, gated
/// against each request addressed to it and reported by reflection; <see langword="null"/> (the
/// default) declares none, so its requests are not gated -- whatever the primary declares.</param>
/// <example>
/// <code>
/// var server = new RpcServer(
///     typeof(IMyService), new MyService(), expectedProtocolVersion: "2.0.0",
///     additionalProtocols: [new HostedProtocol(typeof(IReports), new Reports())]);
/// </code>
/// </example>
public sealed record HostedProtocol(Type ServiceInterface, object Implementation, string? ProtocolVersion = null)
{
    /// <summary>Hosts <typeparamref name="TContract"/> with <paramref name="implementation"/>.</summary>
    public static HostedProtocol For<TContract>(TContract implementation, string? protocolVersion = null)
        where TContract : class =>
        new(typeof(TContract), implementation ?? throw new ArgumentNullException(nameof(implementation)), protocolVersion);
}

/// <summary>One hosted application protocol, resolved: its wire name, its version, its method
/// table and the object those methods dispatch to.</summary>
internal sealed class ProtocolBinding(
    string name, string? version, IReadOnlyDictionary<string, RpcMethodInfo> methods, object implementation)
{
    public string Name { get; } = name;

    public string? Version { get; } = version;

    public IReadOnlyDictionary<string, RpcMethodInfo> Methods { get; } = methods;

    public IReadOnlySet<string> MethodNames { get; } = new HashSet<string>(methods.Keys, StringComparer.Ordinal);

    public object Implementation { get; } = implementation;
}
