namespace QueryFarm.VgiRpc.Server;

/// <summary>Authentication result made available to worker method implementations.</summary>
public sealed record AuthContext
{
    public static AuthContext Anonymous { get; } = new("", false, null);

    public AuthContext(
        string? domain,
        bool authenticated,
        string? principal,
        IReadOnlyDictionary<string, object?>? claims = null)
    {
        Domain = domain ?? "";
        Authenticated = authenticated;
        Principal = principal;
        Claims = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(claims ?? new Dictionary<string, object?>()));
    }

    public string Domain { get; }
    public bool Authenticated { get; }
    public string? Principal { get; }
    public IReadOnlyDictionary<string, object?> Claims { get; }

    public void RequireAuthenticated()
    {
        if (!Authenticated)
        {
            throw new UnauthorizedAccessException("Authentication required");
        }
    }
}
