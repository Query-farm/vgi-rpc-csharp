namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// The wire surface of <c>vgi_rpc.Reflection.v1</c> -- the declaration reflection describes
/// itself from.
/// </summary>
/// <remarks>
/// <para>
/// Self-description is not special-cased: reflection's binding carries its methods like any
/// other binding, so <c>describe("vgi_rpc.Reflection.v1")</c> returns the two methods it answers
/// and its protocol hash is taken over them. The method table <em>is</em> what
/// <c>describe</c> reports and what the hash is computed over, so an empty one would not be
/// honesty about a framework-owned protocol -- it would be a protocol lying about itself, and a
/// client that discovered this server the documented way (<c>list_protocols</c>, then
/// <c>describe</c>) would be told reflection exists and then told it has no methods, unable to
/// learn how to call the protocol it is already calling.
/// </para>
/// <para>
/// Declaration only: nothing ever invokes these. <see cref="Server.RpcServer"/> serves both
/// methods itself (it is the thing being described, so answering from anywhere else would let
/// the description drift from what dispatch actually routes to), exactly as it did before this
/// interface existed. What the interface supplies is the <see cref="RpcMethodInfo"/> pair --
/// derived by the same <see cref="ServiceRegistry"/> reflection every application protocol goes
/// through, rather than hand-built, so reflection cannot come to describe itself by a different
/// rule than it describes everyone else.
/// </para>
/// <para>
/// Both returns are <c>byte[]</c> because that is genuinely what rides the wire: the framework's
/// convention for a structured return is the payload serialized into a single non-null
/// <c>result</c> binary column, which is the same field the canonical Python reference derives
/// for the same two methods from its <c>ProtocolList</c> / <c>ServiceDescription</c> dataclasses.
/// </para>
/// </remarks>
public interface IReflectionProtocol
{
    /// <summary>The cheap question: what protocols are here, and have they changed.</summary>
    /// <returns>A serialized <c>ProtocolList</c> -- every hosted protocol with its version and
    /// hash, including reflection itself.</returns>
    /// <remarks>
    /// The only one a client needs on a warm path: the hash answers "has it changed" without
    /// transferring any schema.
    /// </remarks>
    byte[] ListProtocols();

    /// <summary>The expensive question, asked once: the full surface of one protocol.</summary>
    /// <param name="protocol">The wire name to describe.</param>
    /// <returns>A serialized <c>ServiceDescription</c>.</returns>
    byte[] Describe(string protocol);
}
