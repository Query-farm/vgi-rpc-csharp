using System.Text.Json;
using QueryFarm.VgiRpc.Attributes;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Conformance;

/// <summary>
/// <c>conformance.Whoami.v1</c> -- hosted only by the grant worker (IDENTITY_CONFORMANCE_FIXTURE.md
/// §10): reports what HTTP authentication decided for the request. Pinned hash
/// <c>a280333ba72432020e162cab388a78355969a30aa74f0665ad9d2932d7a10b8f</c>.
/// </summary>
[ProtocolName(Name)]
public interface IWhoami
{
    /// <summary>The fixture protocol's routing key.</summary>
    public const string Name = "conformance.Whoami.v1";

    /// <summary>The caller's <c>AuthContext</c> as compact JSON with sorted keys:
    /// <c>{"authenticated":bool,"claims":{…},"domain":str,"principal":str}</c>, <c>""</c> for an
    /// absent domain or principal.</summary>
    string Whoami(ICallContext? ctx = null);
}

/// <summary>The reference behaviour of <see cref="IWhoami"/>.</summary>
public sealed class WhoamiImpl : IWhoami
{
    /// <inheritdoc/>
    public string Whoami(ICallContext? ctx = null)
    {
        var auth = ctx?.Auth ?? AuthContext.Anonymous;
        var document = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["authenticated"] = auth.Authenticated,
            ["claims"] = new SortedDictionary<string, object?>(auth.Claims.ToDictionary(), StringComparer.Ordinal),
            ["domain"] = auth.Domain ?? "",
            ["principal"] = auth.Principal ?? "",
        };
        return JsonSerializer.Serialize(document);
    }
}
