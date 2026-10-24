using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Store;

/// <summary>
/// An authorized, durable closure intent (docs/terrabase/project-closure.md), committed before any external effect. While
/// it exists it fences writes that would add access to its scope (see <see cref="AuthStore.TransactAsync"/>). It holds no
/// bearer: TerraBase is reached with the service's own peer transport.
/// </summary>
/// <param name="Id">
/// An opaque random ULID, never derived from the subject. For a project it is the closure id TerraBase records: the
/// project's <c>ClosureId</c>, drawn once and reused by every intent of that project.
/// </param>
/// <param name="InstanceId">The tenant.</param>
/// <param name="Scope"><c>project</c>, <c>organization</c> (a sole-owner organization of a closing account) or <c>account</c>.</param>
/// <param name="SubjectId">The project, organization or user id.</param>
/// <param name="OrganizationId">The organization of a project or organization scope.</param>
/// <param name="RequestedBy">The user whose role authorized the intent.</param>
/// <param name="RequestedAt">When it was authorized.</param>
/// <param name="NextAttemptAt">When the background pass resumes it.</param>
/// <param name="AccountId">The closing account that owns this intent, if any; its account job drives it.</param>
/// <param name="Attempts">Background attempts so far.</param>
/// <param name="FailureCode">An allowlisted code of the last failed attempt.</param>
/// <param name="FenceSeen">
/// A project scope only: TerraBase answered <c>accepted</c> or <c>pending</c> for this closure id once, so its permanent
/// fence exists somewhere. A later refusal (another machine that has not received it yet) can no longer cancel the intent.
/// </param>
/// <param name="Generation">
/// A fresh ULID per creation. A project's <see cref="Id"/> is the same for every intent of that project (TerraBase's closure
/// id), so the generation tells a re-created intent from the one a caller read.
/// </param>
/// <param name="CommittedAt">
/// An account scope only: when it began removing organizations, memberships or the user. From then on it never cancels;
/// a late refusal pauses it until resolved.
/// </param>
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
    DateTimeOffset? CommittedAt = null)
{
    /// <summary>Whether <paramref name="other"/> is this very intent (same subject, same creation), whatever its later state.</summary>
    public bool Same(ClosureJob other) =>
        other is not null && other.Id == this.Id && other.Scope == this.Scope && other.SubjectId == this.SubjectId && other.Generation == this.Generation;

    /// <summary>The natural key of a scope's intent: one per subject.</summary>
    public static string Key(string scope, string subjectId) => scope + "/" + subjectId;

    /// <summary>Creates an intent record; a concurrent intent for the same subject fails the transaction.</summary>
    public static (ClosureJob Job, StoreOperation Create) New(
        string instanceId, string scope, string subjectId, string? organizationId, string requestedBy, TimeProvider clock, string? accountId = null, string? closureId = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        var job = new ClosureJob(closureId ?? AuthStore.NewId(), instanceId, scope, subjectId, organizationId, requestedBy, now, now, accountId,
            Generation: AuthStore.NewId());
        return (job, AuthStore.Create(AuthStore.ClosureJobCollection, instanceId, Key(scope, subjectId), JsonSerializer.SerializeToElement(job, ClosureJson.Default.ClosureJob)));
    }
}

/// <summary>Source-generated storage metadata for closure intents.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClosureJob))]
public sealed partial class ClosureJson : JsonSerializerContext;
