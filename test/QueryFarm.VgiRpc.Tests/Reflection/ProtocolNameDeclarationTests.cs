using QueryFarm.VgiRpc.Attributes;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Reflection;

/// <summary>A contract that says nothing: the derived name still applies.</summary>
public interface IUndeclaredService
{
    Task<string> EchoAsync(string value);
}

/// <summary>A contract whose wire name no C# identifier could spell.</summary>
[ProtocolName("declared.v2")]
public interface IDeclaredService
{
    Task<string> EchoAsync(string value);
}

/// <summary>
/// Extends a declared protocol and stays silent. It must get its OWN name, not its parent's
/// routing key — a fixture that subclasses a protocol to vary one thing must not impersonate it.
/// </summary>
public interface IExtendingService : IDeclaredService
{
    Task<string> ExtraAsync(string value);
}

[ProtocolName("not a name")]
public interface IMalformedNameService
{
    Task PingAsync();
}

[ProtocolName("vgi_rpc.Reflection.v1")]
public interface IReservedNameService
{
    Task PingAsync();
}

public sealed class DeclaredService : IDeclaredService, IUndeclaredService, IExtendingService
{
    public Task<string> EchoAsync(string value) => Task.FromResult(value);

    public Task<string> ExtraAsync(string value) => Task.FromResult(value);
}

public sealed class ProtocolNameDeclarationTests
{
    [Fact]
    public void UndeclaredContract_KeepsTheDerivedName()
    {
        Assert.Equal("UndeclaredService", WireNaming.ForProtocol(typeof(IUndeclaredService)));
    }

    [Fact]
    public void DeclaredContract_UsesTheDeclaredName()
    {
        Assert.Equal("declared.v2", WireNaming.ForProtocol(typeof(IDeclaredService)));
    }

    /// <summary>
    /// Read as declared, not inherited — the reference reads <c>vars(protocol)</c>, not
    /// <c>getattr</c>. Silently sharing a parent's routing key is how a subinterface that varies
    /// one thing ends up answering for the protocol it was derived from.
    /// </summary>
    [Fact]
    public void ExtendingContract_DoesNotInheritTheParentsName()
    {
        Assert.Equal("ExtendingService", WireNaming.ForProtocol(typeof(IExtendingService)));
    }

    [Fact]
    public void MalformedDeclaration_IsRefused()
    {
        var exception = Assert.Throws<ArgumentException>(() => WireNaming.ForProtocol(typeof(IMalformedNameService)));
        Assert.Contains("is not a protocol name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlongDeclaration_IsRefused()
    {
        Assert.False(WireNaming.IsValidProtocolName(new string('a', WireNaming.MaxProtocolNameLength + 1)));
        Assert.True(WireNaming.IsValidProtocolName(new string('a', WireNaming.MaxProtocolNameLength)));
    }

    /// <summary>
    /// An application protocol under <c>vgi_rpc.</c> could shadow reflection on the server hosting
    /// both — and reflection is the one endpoint a confused client reaches for to find out what
    /// went wrong.
    /// </summary>
    [Fact]
    public void ReservedPrefixDeclaration_IsRefused()
    {
        var exception = Assert.Throws<ArgumentException>(() => WireNaming.ForProtocol(typeof(IReservedNameService)));
        Assert.Contains(WireNaming.ReservedProtocolPrefix, exception.Message, StringComparison.Ordinal);
        Assert.True(WireNaming.IsReservedProtocolName("vgi_rpc.Identity.v1"));
        Assert.False(WireNaming.IsReservedProtocolName("vgi.v2"));
    }

    /// <summary>Resolved at construction: an unroutable declaration fails when the server is
    /// built, not on every request.</summary>
    [Fact]
    public void ServerConstruction_FailsOnAnUnroutableDeclaration()
    {
        Assert.Throws<ArgumentException>(() => new RpcServer(typeof(IMalformedNameService), new DeclaredService()));
        Assert.Throws<ArgumentException>(() => new RpcServer(typeof(IReservedNameService), new DeclaredService()));
    }

    [Fact]
    public void Server_HostsUnderTheDeclaredName()
    {
        var server = new RpcServer(typeof(IDeclaredService), new DeclaredService());

        Assert.Equal("declared.v2", server.ProtocolName);
        Assert.Equal(["declared.v2", ReflectionProtocol.ProtocolName], server.HostedProtocols);
        Assert.NotNull(server.MethodsForProtocol("declared.v2"));
        Assert.Null(server.MethodsForProtocol("DeclaredService"));
    }

    /// <summary>The client derives the same name from the same contract, so the two agree without
    /// anyone setting <see cref="RpcClientOptions.Protocol"/>.</summary>
    [Fact]
    public async Task ClientAndServer_AgreeOnTheDeclaredNameEndToEnd()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IDeclaredService), new DeclaredService());
        var serveTask = server.ServeOneAsync(serverTransport);

        var client = new RpcConnection<IDeclaredService>(clientTransport).CreateProxy();

        Assert.Equal("hello", await client.EchoAsync("hello"));
        await serveTask;
    }

    /// <summary>The name the contract's C# type would have derived is no longer hosted, and
    /// naming it is refused as an unhosted protocol rather than quietly dispatched.</summary>
    [Fact]
    public async Task AddressingTheDerivedName_IsRefusedAsUnhosted()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IDeclaredService), new DeclaredService());
        var serveTask = server.ServeOneAsync(serverTransport);

        var client = new RpcConnection<IDeclaredService>(
            clientTransport,
            new RpcClientOptions { Protocol = "DeclaredService" }).CreateProxy();

        var exception = await Assert.ThrowsAsync<RpcException>(() => client.EchoAsync("hello"));
        Assert.Equal(ProtocolNotSupportedException.ErrorKindConst, exception.ErrorKind);
        await serveTask;
    }
}
