using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Tailnet;

public interface ITailnetEvidenceService
{
    Task<string> SnapshotAsync();
}

public interface IConformanceService
{
    Task<string> EchoStringAsync(string value, ICallContext? context = null);
}

public sealed class TailnetConformanceService(TailnetServerExpectations expectations) : IConformanceService
{
    public Task<string> EchoStringAsync(string value, ICallContext? context = null)
    {
        TailnetEvidenceValidator.ValidateServerContext(
            context ?? throw new InvalidOperationException("call context is required"), expectations);
        return Task.FromResult(value);
    }
}

public sealed record TailnetServerExpectations(
    string Issuer,
    string Capability,
    string EvidenceSource = "serve_proxy",
    string Assurance = "configured_proxy",
    PeerSubjectKind SubjectKind = PeerSubjectKind.Unknown,
    SubjectStability SubjectStability = SubjectStability.None,
    bool SubjectVerified = false,
    bool ExpectProxy = true,
    string? Tag = null,
    string? CapabilityTargetKind = null);

public sealed record TailnetSnapshotExpectations(
    string Issuer,
    string EvidenceSource,
    string Assurance,
    string SubjectKind,
    string SubjectStability,
    string Capability,
    string? Tag,
    string? TargetKind,
    bool Authenticated,
    bool ExpectProxy);
