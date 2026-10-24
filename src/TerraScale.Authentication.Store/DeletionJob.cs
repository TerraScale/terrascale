using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Store;

/// <summary>
/// Auth logical deletion evidence, then TerraBase's shared-history erasure of the auth database and SYSTEM
/// (<see cref="HistoryStatus"/>, docs/terrabase/history-erasure.md). The job is the subject's write fence; it retires only after
/// that erasure is complete (every holder, residue and backups) and <see cref="FenceExpiresAt"/> passed.
/// </summary>
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
    string? ClosureGeneration = null)
{
    /// <summary>
    /// How long the fence must outlive its request: every credential issued before it expires by then. Sessions and OIDC
    /// refresh token families end 14 days after issue and rotation never extends them; access JWTs, OIDC access tokens and
    /// GitHub Actions grants last at most 24 hours; invitations 7 days; passcodes minutes. Stored credentials of the subject
    /// (sessions, refresh tokens, API keys, MFA) also die with its root or its cleanup. One day covers admitted in-flight writes.
    /// </summary>
    public static readonly TimeSpan CredentialBound = TimeSpan.FromDays(15);

    /// <summary>
    /// The fence's document id in the auth database (a natural-key hash). TerraBase never stores it: it reads it from the auth
    /// request document <see cref="ErasureId"/> (a random ULID, the only id TerraBase keeps) names.
    /// </summary>
    public string FenceId => AuthStore.NaturalKeyId(this.InstanceId, AuthStore.DeletionJobCollection, this.Id);

    /// <summary>A stable ULID also used as the permanent write fence for this erased identity.</summary>
    public static string Key(string instanceId, string scope, string subjectId) => AuthStore.NaturalKeyId(instanceId, "deletion-" + scope, subjectId);

    /// <summary>Creates the immutable request/deadline in the same transaction that removes its authorized root.</summary>
    public static StoreOperation Enqueue(string instanceId, string scope, string subjectId, TimeProvider clock, string? organizationId = null, string? closureGeneration = null)
    {
        var now = clock.GetUtcNow();
        var job = new DeletionJob(Key(instanceId, scope, subjectId), instanceId, scope, subjectId, organizationId, now, now.AddDays(30), now,
            FenceExpiresAt: now + CredentialBound, ErasureId: AuthStore.NewId(), ClosureGeneration: closureGeneration);
        return AuthStore.Create(AuthStore.DeletionJobCollection, instanceId, job.Id, JsonSerializer.SerializeToElement(job, DeletionJson.Default.DeletionJob));
    }
}

/// <summary>Source-generated storage metadata for deletion jobs.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeletionJob))]
[JsonSerializable(typeof(AccountDeletionReceipt))]
public sealed partial class DeletionJson : JsonSerializerContext;
