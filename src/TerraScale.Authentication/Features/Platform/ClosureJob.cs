using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public sealed record ClosureJob(
    string Id,
    string InstanceId,
    string Scope,
    string SubjectId,
    string? OrganizationId,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset NextAttemptAt,
    string? AccountId = null,
    int Attempts = 0,
    string? FailureCode = null,
    bool FenceSeen = false,
    string? Generation = null,
    DateTimeOffset? CommittedAt = null,
    int ProjectClosureVersion = 1)
{
    private static readonly TimeSpan RequestAttempt = TimeSpan.FromSeconds(30);

    public bool Same(ClosureJob other) =>
        other is not null && other.Id == this.Id && other.Scope == this.Scope && other.SubjectId == this.SubjectId && other.Generation == this.Generation
        && other.ProjectClosureVersion == this.ProjectClosureVersion;

    public static (ClosureJob Job, Change Create) New(
        string instanceId, string scope, string subjectId, string? organizationId, string requestedBy, TimeProvider clock, string? accountId = null, string? closureId = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        var job = new ClosureJob(closureId ?? AuthIds.New(), instanceId, scope, subjectId, organizationId, requestedBy, now, now + RequestAttempt, accountId,
            Generation: AuthIds.New(), ProjectClosureVersion: scope == "project" && accountId is not null ? 2 : 1);
        return (job, Change.Insert(job));
    }
}

public sealed record ClosureCancellation(string Generation);

public sealed record ClosureCommit(string Generation, DateTimeOffset CommittedAt);
