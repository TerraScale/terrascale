using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public sealed record DeletionJob(
    string Id,
    string InstanceId,
    string Scope,
    string SubjectId,
    string? OrganizationId,
    DateTimeOffset RequestedAt,
    DateTimeOffset DueAt,
    DateTimeOffset NextAttemptAt,
    int Attempts = 0,
    string? FailureCode = null,
    DateTimeOffset? AuthLogicalVerifiedAt = null,
    DateTimeOffset? FenceExpiresAt = null,
    string? HistoryStatus = null,
    DateTimeOffset? HistoryCompletedAt = null,
    string? ErasureId = null,
    string? ClosureGeneration = null,
    DateTimeOffset? DashboardVerifiedAt = null)
{
    public static readonly TimeSpan CredentialBound = TimeSpan.FromDays(15);

    public static string Key(string instanceId, string scope, string subjectId) => AuthIds.Derived(instanceId, "deletion-" + scope, subjectId);

    public static Change Enqueue(string instanceId, string scope, string subjectId, TimeProvider clock, string? organizationId = null, string? closureGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        return Change.Insert(new DeletionJob(Key(instanceId, scope, subjectId), instanceId, scope, subjectId, organizationId, now, now.AddDays(30), now,
            FenceExpiresAt: now + CredentialBound, ErasureId: AuthIds.New(), ClosureGeneration: closureGeneration));
    }
}

public sealed record AccountDeletionReceipt(string InstanceId, string TokenHash, string SubjectId, string Generation, DateTimeOffset ExpiresAt, IReadOnlyList<string> OrganizationIds);
