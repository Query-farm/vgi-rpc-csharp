using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.AccessLog;

/// <summary>
/// Every place that builds an <c>AccessLogRecord</c> derives its <c>protocol_hash</c> from the
/// same expression it used for <c>protocol</c>, through a canonical per-binding accessor.
/// </summary>
/// <remarks>
/// <para>
/// A source-level guard rather than another end-to-end case, because the risk here is a
/// <em>future</em> emit site, not a present bug. The behavioural tests cover the sites that
/// exist; this one covers the site someone adds next year. The canonical Python implementation
/// had seven spread across two HTTP dispatchers, a stream resource and three raw-transport
/// paths, and six of them independently reached for the server's primary hash -- which is what a
/// per-site convention gets you once there are enough sites.
/// </para>
/// <para>
/// Two independent failures are ruled out at once, because both produce a record that is
/// well-formed, passes the schema, and groups plausibly on a dashboard:
/// </para>
/// <list type="number">
/// <item>naming one protocol and carrying another's digest -- <c>access-log-spec.md</c> §3 makes
/// <c>protocol_hash</c> "the registry key when decoding archived records", so the record decodes
/// against the wrong description;</item>
/// <item>carrying the right protocol's digest computed the wrong way -- a port-local hash is a
/// key in no registry at all, since a registry is built from what <c>describe</c> reports. Both
/// this port and Go shipped that one.</item>
/// </list>
/// </remarks>
public class RecordIdentitySourceGuardTests
{
    /// <summary>The accessors that return a protocol's canonical digest (WIRE_PROTOCOL.md §14).</summary>
    private static readonly string[] s_canonicalAccessors = ["BindingHashFor", "ProtocolHashFor"];

    [Fact]
    public void EveryAccessRecordDerivesItsHashFromTheProtocolItNames()
    {
        var sites = ConstructionSites();

        // Not an incidental assertion: if the type is renamed or the scan stops matching, every
        // check below passes vacuously and is counted as coverage.
        Assert.True(sites.Count >= 2, $"expected every transport's emit site to be found, saw {sites.Count}");

        foreach (var (file, args) in sites)
        {
            var protocol = Argument(args, "Protocol");
            var hash = Argument(args, "ProtocolHash");

            Assert.True(
                s_canonicalAccessors.Any(accessor => hash.Contains(accessor, StringComparison.Ordinal)),
                $"{file}: protocol_hash is built by `{hash}`, which is not one of "
                    + $"[{string.Join(", ", s_canonicalAccessors)}]. access-log-spec.md §3 requires the "
                    + "canonical digest -- the one describe reports and every other port computes. A "
                    + "port-local hash is 64 hex characters that key no registry.");

            Assert.True(
                hash.Contains(protocol, StringComparison.Ordinal),
                $"{file}: protocol is `{protocol}` but protocol_hash is `{hash}`, which does not "
                    + "derive from it. The pair must come from one lookup: a record naming one "
                    + "protocol and carrying another's digest is decoded against the wrong "
                    + "description, and nothing about it looks wrong.");
        }
    }

    /// <summary>The text of one named argument in a record-construction argument list.</summary>
    private static string Argument(string args, string name)
    {
        var match = Regex.Match(args, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}:\s*(?<value>[^,\r\n]+)");
        Assert.True(match.Success, $"no `{name}:` argument in: {args}");
        return match.Groups["value"].Value.Trim();
    }

    /// <summary>
    /// Every <c>new AccessLogRecord(...)</c> in the shipped sources, as (file, argument text).
    /// </summary>
    private static List<(string File, string Args)> ConstructionSites()
    {
        var src = Path.Combine(RepoRoot(), "src");
        Assert.True(Directory.Exists(src), $"sources not found at {src}; this guard reads them and cannot be skipped");

        var sites = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            const string Marker = "new AccessLogRecord(";
            for (var at = text.IndexOf(Marker, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(Marker, at + 1, StringComparison.Ordinal))
            {
                sites.Add((Path.GetFileName(file), ArgumentList(text, at + Marker.Length)));
            }
        }

        return sites;
    }

    /// <summary>The text between a call's parentheses, tracking nesting so a nested call's own
    /// close paren does not end the list early.</summary>
    private static string ArgumentList(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text[start..i];
        }

        throw new InvalidOperationException("unbalanced parentheses in an AccessLogRecord construction");
    }

    /// <summary>This file's own location is the only repo anchor a test binary has.</summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
}
