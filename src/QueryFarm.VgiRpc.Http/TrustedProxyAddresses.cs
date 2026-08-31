using System.Net;
using System.Net.Sockets;

namespace QueryFarm.VgiRpc.Http;

/// <summary>Exact, normalized IP-literal trust boundaries for HTTP proxy evidence.</summary>
internal static class TrustedProxyAddresses
{
    public static HashSet<string> Parse(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalized = Normalize(value)
                ?? throw new ArgumentException("trusted proxy must be an exact IP literal", parameterName);
            if (!result.Add(normalized))
                throw new ArgumentException("duplicate normalized trusted proxy address", parameterName);
        }
        if (result.Count == 0)
            throw new ArgumentException("at least one trusted proxy address is required", parameterName);
        return result;
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace)
            || value.IndexOfAny(['%', '[', ']', '/']) >= 0
            || !IPAddress.TryParse(value, out var address))
            return null;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (!IsCanonicalDottedQuad(value)) return null;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (!value.Contains(':') || address.ScopeId != 0) return null;
            var dottedTail = value[(value.LastIndexOf(':') + 1)..];
            if (value.Contains('.')
                && (!dottedTail.Contains('.') || !IsCanonicalDottedQuad(dottedTail)))
                return null;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        }
        else
        {
            return null;
        }
        return address.ToString();
    }

    private static bool IsCanonicalDottedQuad(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 4 && parts.All(part =>
            part.Length is >= 1 and <= 3
            && (part.Length == 1 || part[0] != '0')
            && part.All(character => character is >= '0' and <= '9')
            && byte.TryParse(part, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _));
    }
}
