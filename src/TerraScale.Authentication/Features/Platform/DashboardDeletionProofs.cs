using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public sealed record DashboardDeletionProof(string Id, string Scope, string SubjectId, string? OrganizationId);

public sealed record DashboardDeletionList(IReadOnlyList<DashboardDeletionProof> Items);

public sealed record DashboardDeletionAcknowledgement(string Scope, string SubjectId, string? OrganizationId);

public readonly union DeletionAcknowledgementResult(AuthCompleted, AuthNotFound, AuthConflict);

public sealed record DeletionCursorRecord(string Name, string? After)
{
    public string? Version { get; init; }
}


[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DashboardDeletionList))]
[JsonSerializable(typeof(DashboardDeletionAcknowledgement))]
[JsonSerializable(typeof(JsonObject))]
public sealed partial class DashboardDeletionJson : JsonSerializerContext;

internal static class DashboardDeletionProofs
{
    private const int PageSize = 20;
    private const string Pending = "pending";
    private static readonly DateTimeOffset MaxRequestedAt = DateTimeOffset.MaxValue.AddDays(-30);
    private const string PendingFilter = """
        SELECT id, instance_id, scope, subject_id, organization_id, requested_at, due_at, erasure_id
        FROM deletion_jobs
        WHERE dashboard_verified_at IS NULL AND id > @after AND instance_id = @platform AND auth_logical_verified_at IS NOT NULL
            AND (scope = 'account' AND organization_id IS NULL AND NOT EXISTS (SELECT 1 FROM users u WHERE u.id = deletion_jobs.subject_id)
                OR scope = 'organization' AND (organization_id IS NULL OR organization_id = subject_id)
                    AND NOT EXISTS (SELECT 1 FROM organizations o WHERE o.id = deletion_jobs.subject_id)
                OR scope = 'project' AND organization_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = deletion_jobs.subject_id))
        ORDER BY id
        """;
    private const string PendingPage = PendingFilter + " LIMIT 21";

    internal static async Task<DashboardDeletionList> ListAsync(AuthData data, bool peek, CancellationToken cancellationToken)
    {
        if (peek)
        {
            var peekAfter = string.Empty;
            while (true)
            {
                var peeked = await PageAsync(data, peekAfter, cancellationToken);
                foreach (var job in peeked)
                {
                    if (Proof(job) is { } valid)
                    {
                        return new([valid]);
                    }
                }

                if (peeked.Count <= PageSize)
                {
                    return new([]);
                }

                peekAfter = peeked[^1].Id;
            }
        }

        var position = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<DeletionCursorRecord>(
            "SELECT * FROM deletion_cursors WHERE name = @Pending", new { Pending }), cancellationToken));
        var after = position?.Value.After ?? string.Empty;
        var page = await PageAsync(data, after, cancellationToken);
        if (page.Count == 0 && after.Length > 0)
        {
            page = await PageAsync(data, string.Empty, cancellationToken);
        }

        var hasMore = page.Count > PageSize;
        var returned = hasMore ? page[..PageSize] : page;
        var next = new DeletionCursorRecord(Pending, hasMore ? returned[^1].Id : null);
        if (next.After != position?.Value.After
            && !await data.SaveAsync(position is null ? Change.Insert(next) : Change.Update(position, next), cancellationToken))
        {
            return new([]);
        }

        return new([.. returned.Select(Proof).OfType<DashboardDeletionProof>()]);
    }

    private static async Task<List<PendingDeletionJobRow>> PageAsync(AuthData data, string after, CancellationToken cancellationToken)
    {
        var platform = PlatformDirectory.Instance;
        return (await data.ReadAsync(connection => connection.QueryAsync<PendingDeletionJobRow>(PendingPage, new { after, platform }), cancellationToken)).AsList();
    }

    internal static async Task<DeletionAcknowledgementResult> AcknowledgeAsync(AuthData data, TimeProvider clock, string? id, DashboardDeletionAcknowledgement request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (id is not { } proofId || !PlatformDirectory.IsId(proofId) || !PlatformDirectory.IsId(request.SubjectId) || request.Scope is not ("account" or "organization" or "project"))
        {
            return Missing();
        }

        var key = DeletionJob.Key(PlatformDirectory.Instance, request.Scope, request.SubjectId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var platform = PlatformDirectory.Instance;
            var job = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<DeletionJob>(
                """
                SELECT * FROM deletion_jobs WHERE id = @key AND instance_id = @platform AND auth_logical_verified_at IS NOT NULL
                AND (scope = 'account' AND organization_id IS NULL AND NOT EXISTS (SELECT 1 FROM users u WHERE u.id = deletion_jobs.subject_id)
                    OR scope = 'organization' AND (organization_id IS NULL OR organization_id = subject_id)
                        AND NOT EXISTS (SELECT 1 FROM organizations o WHERE o.id = deletion_jobs.subject_id)
                    OR scope = 'project' AND organization_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = deletion_jobs.subject_id))
                """, new { key, platform }), cancellationToken));
            if (job is null || Proof(job.Value) is not { } proof || proof != new DashboardDeletionProof(proofId, request.Scope, request.SubjectId, request.OrganizationId))
            {
                return Missing();
            }

            if (job.Value.DashboardVerifiedAt is not null
                || await data.SaveAsync(Change.Update(job, job.Value with { DashboardVerifiedAt = clock.GetUtcNow() }), cancellationToken))
            {
                return new AuthCompleted();
            }
        }

        return new AuthConflict("Retry acknowledgement of the same proof.", "deletion_changed");
    }

    private static DashboardDeletionProof? Proof(PendingDeletionJobRow job) =>
        DateTimeOffset.TryParse(job.RequestedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var requestedAt)
        && DateTimeOffset.TryParse(job.DueAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dueAt)
            ? Proof(job.Id, job.InstanceId, job.Scope, job.SubjectId, job.OrganizationId, requestedAt, dueAt, job.ErasureId)
            : null;

    private static DashboardDeletionProof? Proof(DeletionJob job) =>
        Proof(job.Id, job.InstanceId, job.Scope, job.SubjectId, job.OrganizationId, job.RequestedAt, job.DueAt, job.ErasureId);

    private static DashboardDeletionProof? Proof(
        string id,
        string instanceId,
        string scope,
        string subjectId,
        string? organizationId,
        DateTimeOffset requestedAt,
        DateTimeOffset dueAt,
        string? erasureId) =>
        requestedAt <= MaxRequestedAt
        && dueAt == requestedAt.ToUniversalTime().AddDays(30)
        && PlatformDirectory.IsId(subjectId)
        && erasureId is not null && PlatformDirectory.IsId(erasureId)
        && id == DeletionJob.Key(instanceId, scope, subjectId)
        && (scope != "project" || PlatformDirectory.IsId(organizationId))
            ? new(erasureId, scope, subjectId, scope == "organization" ? subjectId : organizationId)
            : null;

    private static AuthNotFound Missing() => new("No such deletion proof.", "not_found");

    internal sealed record PendingDeletionJobRow(
        string Id,
        string InstanceId,
        string Scope,
        string SubjectId,
        string? OrganizationId,
        string? RequestedAt,
        string? DueAt,
        string? ErasureId);
}
