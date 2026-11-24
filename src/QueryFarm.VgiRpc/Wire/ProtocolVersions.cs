using System.Globalization;
using System.Text.RegularExpressions;

namespace QueryFarm.VgiRpc.Wire;

/// <summary>An inclusive window of <c>(major, minor)</c> versions (<c>PROTOCOL_VERSIONS_V1.md</c>).</summary>
public readonly record struct VersionRange((int Major, int Minor) Min, (int Major, int Minor) Max)
{
    /// <summary>Parses <c>MAJOR.MINOR</c>, or <c>MAJOR.MINOR.PATCH</c> (the patch dropped).</summary>
    public static (int Major, int Minor)? ParseMajorMinor(string text)
    {
        var parts = text.Trim().Split('.');
        if (parts.Length is < 2 or > 3 || parts.Any(p => !IsComponent(p)))
        {
            return null;
        }

        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static bool IsComponent(string p) =>
        p.Length > 0 && p.Length <= 9 && p.All(char.IsAsciiDigit) && (p.Length == 1 || p[0] != '0');

    /// <summary>Parses <c>MIN-MAX</c>, or a single version meaning <c>V-V</c>; <see langword="null"/>
    /// when malformed or inverted.</summary>
    public static VersionRange? Parse(string text)
    {
        var trimmed = text.Trim();
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        var lo = ParseMajorMinor(dash < 0 ? trimmed : trimmed[..dash]);
        var hi = dash < 0 ? lo : ParseMajorMinor(trimmed[(dash + 1)..]);
        if (lo is not { } min || hi is not { } max || Compare(min, max) > 0)
        {
            return null;
        }

        return new VersionRange(min, max);
    }

    /// <summary>The single-version window of a version's <c>major.minor</c>.</summary>
    public static VersionRange? Exactly(string version) =>
        ParseMajorMinor(version) is { } v ? new VersionRange(v, v) : null;

    /// <summary>Orders two <c>(major, minor)</c> versions.</summary>
    public static int Compare((int Major, int Minor) a, (int Major, int Minor) b) =>
        a.Major != b.Major ? a.Major.CompareTo(b.Major) : a.Minor.CompareTo(b.Minor);

    /// <summary>Whether <paramref name="v"/> lies inside the window.</summary>
    public bool Contains((int Major, int Minor) v) => Compare(Min, v) <= 0 && Compare(v, Max) <= 0;

    /// <summary>The highest version inside both windows, or <see langword="null"/> when disjoint.</summary>
    public (int Major, int Minor)? HighestCommon(VersionRange other)
    {
        var top = Compare(Max, other.Max) <= 0 ? Max : other.Max;
        return Compare(top, Min) >= 0 && Compare(top, other.Min) >= 0 ? top : null;
    }

    /// <summary>Wire form: <c>MIN-MAX</c>, always both ends.</summary>
    public override string ToString() => $"{Min.Major}.{Min.Minor}-{Max.Major}.{Max.Minor}";
}

/// <summary>Protocol version negotiation (<c>PROTOCOL_VERSIONS_V1.md</c>): the wire names and the
/// <c>name=MIN-MAX[, ...]</c> advertisement syntax.</summary>
public static partial class ProtocolVersions
{
    /// <summary>The HTTP response header carrying the server's windows.</summary>
    public const string Header = "VGI-Protocol-Versions";

    /// <summary>The HTTP request header carrying the client's windows.</summary>
    public const string ClientHeader = "VGI-Client-Protocol-Versions";

    /// <summary>The metadata key carrying the server's windows.</summary>
    public const string Key = "vgi_rpc.protocol_versions";

    /// <summary>The request metadata key carrying the client's windows.</summary>
    public const string ClientKey = "vgi_rpc.protocol_versions_client";

    /// <summary>Renders <c>name=MIN-MAX[, name=MIN-MAX]...</c> in the given order.</summary>
    public static string Format(IEnumerable<(string Name, VersionRange Range)> ranges) =>
        string.Join(", ", ranges.Select(r => $"{r.Name}={r.Range}"));

    /// <summary>Parses an advertisement. Tolerant: a malformed entry is skipped, and a repeated
    /// name keeps its first window.</summary>
    public static Dictionary<string, VersionRange> Parse(string? text)
    {
        var result = new Dictionary<string, VersionRange>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        foreach (var entry in text.Split(','))
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            var name = entry[..eq].Trim();
            if (!NameRegex().IsMatch(name) || result.ContainsKey(name))
            {
                continue;
            }

            if (VersionRange.Parse(entry[(eq + 1)..]) is { } range)
            {
                result[name] = range;
            }
        }

        return result;
    }

    /// <summary>A pre-negotiation refusal's single server version: <c>Server: X.Y[.Z]</c> or
    /// <c>server requires X.Y[.Z]</c>.</summary>
    public static (int Major, int Minor)? LegacyServerVersion(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = LegacyRegex().Match(text);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_.:/-]+$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"(?:Server:\s*|server requires\s+)(\d+)\.(\d+)")]
    private static partial Regex LegacyRegex();
}
