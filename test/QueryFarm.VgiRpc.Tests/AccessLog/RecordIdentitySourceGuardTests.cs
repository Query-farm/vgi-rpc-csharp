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

    /// <summary>
    /// No HTTP emit site smuggles the server's primary in where a resolved routing key exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard above reads <c>new AccessLogRecord(...)</c>, and <c>RpcHttpEndpoints</c> has
    /// exactly one of those — inside <c>EmitAccessLog</c>, which has always derived both fields
    /// from one lookup. It was green the entire time five of that method's eight callers were
    /// passing nothing at all, because the site it inspects was never the site that was wrong:
    /// the routing key is chosen by the caller, and the omission was invisible one frame down.
    /// </para>
    /// <para>
    /// Presence is now the compiler's job — <c>protocol</c> is a required parameter, so a new
    /// emit site cannot forget it the way the old optional one let five sites forget. What no
    /// compiler can catch is a site that passes <c>server.ProtocolName</c> while holding a real
    /// routing key, which is the same bug wearing an explicit argument. So the exemption list is
    /// pinned here by name: <c>__upload_url__</c> and <c>__transport_options__</c> belong to no
    /// protocol and <c>access-log-spec.md</c> §3 prescribes the server's primary for exactly
    /// those. A sixth site joining them has to say so here first.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyTheProtocolLessFrameworkEndpointsLogTheServersPrimary()
    {
        var file = Path.Combine(RepoRoot(), "src", "QueryFarm.VgiRpc.Http", "RpcHttpEndpoints.cs");
        Assert.True(File.Exists(file), $"sources not found at {file}; this guard reads them and cannot be skipped");

        var primarySites = File.ReadAllLines(file)
            .Select((text, index) => (Text: text, Line: index + 1))
            .Where(l => l.Text.Contains("EmitAccessLog(server, server.ProtocolName", StringComparison.Ordinal)
                        || l.Text.Contains("ErrorResultAsync(server, server.ProtocolName", StringComparison.Ordinal)
                        || l.Text.Contains("server, server.ProtocolName,", StringComparison.Ordinal))
            .ToList();

        foreach (var (text, line) in primarySites)
        {
            Assert.True(
                text.Contains("__upload_url__", StringComparison.Ordinal)
                    || text.Contains("__transport_options__", StringComparison.Ordinal),
                $"RpcHttpEndpoints.cs:{line} logs the server's primary protocol, but is not one of the "
                    + "framework endpoints that belong to no protocol (__upload_url__, "
                    + "__transport_options__). access-log-spec.md §3 requires the record to name the "
                    + "binding that owns the dispatched method. For an application method the primary "
                    + "IS that binding, so this reads as correct and stays correct until the first "
                    + "co-hosted protocol -- at which point the record names one protocol and carries "
                    + $"another's digest, and nothing about it looks wrong. Offending line: {text.Trim()}");
        }
    }

    /// <summary>The guard is only worth having if it can see the sites it is guarding.</summary>
    [Fact]
    public void TheHttpEmitSitesAreActuallyFound()
    {
        var file = Path.Combine(RepoRoot(), "src", "QueryFarm.VgiRpc.Http", "RpcHttpEndpoints.cs");
        var calls = File.ReadAllLines(file)
            .Count(l => l.Contains("EmitAccessLog(server,", StringComparison.Ordinal));

        // Eight call sites today: two unary, four stream, the shared error path, and
        // __upload_url__. A scan that matches nothing passes vacuously.
        Assert.True(calls >= 8, $"expected at least the eight known HTTP emit sites, found {calls}");
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

            // Doc comments are dropped before scanning: prose that *names* the construction is not
            // a construction, and a `<c>new AccessLogRecord(...)</c>` in a remark otherwise
            // matches, yielding an argument list of "..." and a failure that blames the wrong
            // thing entirely. Found exactly that way.
            var text = string.Join(
                "\n",
                File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
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

    /// <summary>
    /// The repository root, found by walking up from the test binary to the solution file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not <see cref="CallerFilePathAttribute"/>, which is the obvious answer and is wrong under
    /// CI. <c>Directory.Build.props</c> sets <c>ContinuousIntegrationBuild</c> when
    /// <c>GITHUB_ACTIONS</c> is set, which turns on deterministic source paths, which rewrites
    /// every embedded source path to <c>/_/...</c> — so the anchor this guard reads resolves to a
    /// directory that exists on no machine, and a guard that cannot find its sources fails
    /// (deliberately: it must never pass vacuously) on exactly the runs that matter most. Found
    /// in CI, green locally, which is the signature of reading a build-rewritten path.
    /// </para>
    /// <para>
    /// The binary's own location survives that rewrite, so walk up from it to the marker file the
    /// repository root is defined by — the same anchor <c>WorkerPoolTests</c> already uses to find
    /// a built example.
    /// </para>
    /// </remarks>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, RepositoryMarker)))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"no '{RepositoryMarker}' above '{AppContext.BaseDirectory}'; this guard reads the "
                + "repository's sources and cannot be skipped");
        return directory!.FullName;
    }

    /// <summary>The file that marks the repository root.</summary>
    private const string RepositoryMarker = "vgi-rpc-csharp.slnx";
}
