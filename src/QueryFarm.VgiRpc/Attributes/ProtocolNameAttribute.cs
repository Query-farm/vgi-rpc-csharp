namespace QueryFarm.VgiRpc.Attributes;

/// <summary>
/// Declares the wire name of the protocol a service contract defines — its routing key.
/// </summary>
/// <example>
/// <code>
/// [ProtocolName("vgi.v2")]
/// public interface IVgiService { ... }
/// </code>
/// </example>
/// <remarks>
/// <para>
/// The name rides every request as <c>vgi_rpc.protocol</c>, and over HTTP it is also the protocol
/// path segment. Absent, the name falls back to the contract type's name with C#'s conventional
/// <c>I</c> prefix stripped (see <see cref="Reflection.WireNaming.ForProtocol"/>) — so every
/// contract that predates this attribute keeps the name it already answered to and the addition
/// breaks no peer.
/// </para>
/// <para><b>Why declare rather than derive.</b> A derived name is an accident of the local type
/// system, and the local type system differs per language: six implementations of the VGI protocol
/// derived four different names — Python <c>VgiProtocol</c>, Java and C# <c>VgiService</c>, Go the
/// framework default <c>Service</c>, TypeScript <c>vgi</c> — so no client could address them all.
/// That stayed invisible until the transports made <c>vgi_rpc.protocol</c> a required routing key,
/// at which point it became load-bearing. A wire name is a cross-port contract and has to be
/// written down somewhere a reader can see it. A C# identifier additionally cannot express the
/// convention the contract uses: <c>vgi.v2</c> and <c>vgi_rpc.Reflection.v1</c> carry a major
/// version in a dot-qualified name, and no C# identifier contains a dot, so renaming the interface
/// is not an available workaround.</para>
/// <para><b>Put the major version in the name.</b> Following gRPC's AIP-185, Kubernetes API groups
/// and D-Bus: an incompatible major becomes a <em>different</em> protocol and therefore a 404 — an
/// answer every proxy, WAF and load balancer understands without an Arrow parser — and
/// <c>foo.v1</c> and <c>foo.v2</c> can be served side by side while clients migrate.</para>
/// <para><b>Not inherited.</b> Read from the type's <em>own</em> declaration, mirroring the
/// canonical Python implementation's <c>vars(protocol)</c> lookup rather than a
/// <c>getattr</c>-style walk. A contract that derives from a declared protocol and does not
/// redeclare gets its own derived name, not its parent's wire name: silently sharing a routing key
/// with a parent is how a fixture that subclasses a protocol to vary one thing ends up
/// impersonating it — the reference and two ports were each bitten by exactly that. Declare it
/// again on the derived contract when that is what you mean.</para>
/// <para>The name must match the protocol-name grammar (<c>[A-Za-z_][A-Za-z0-9_.]*</c>, at most
/// <see cref="Reflection.WireNaming.MaxProtocolNameLength"/> UTF-8 bytes) and may not claim the
/// <see cref="Reflection.WireNaming.ReservedProtocolPrefix"/> prefix, which is reserved for
/// protocols the framework itself defines. A violation is an <see cref="ArgumentException"/> where
/// the name is resolved — at server/client construction, not on every request.</para>
/// </remarks>
/// <param name="name">The wire name, conventionally dot-qualified with a major version.</param>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, Inherited = false)]
public sealed class ProtocolNameAttribute(string name) : Attribute
{
    /// <summary>The declared wire name.</summary>
    public string Name { get; } = name;
}
