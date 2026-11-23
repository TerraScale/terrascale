using Dapper;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
    private const string Pending = "pending";

    internal static async Task<DashboardDeletionList> ListAsync(AuthData data, CancellationToken cancellationToken)
    {
        var position = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<DeletionCursorRecord>(
            "SELECT * FROM deletion_cursors WHERE name = @Pending", new { Pending }), cancellationToken));
        var after = position?.Value.After ?? string.Empty;
        var platform = PlatformDirectory.Instance;
        var page = (await data.ReadAsync(connection => connection.QueryAsync<DeletionJob>(
            """
            SELECT * FROM deletion_jobs WHERE dashboard_verified_at IS NULL AND id > @after AND instance_id = @platform AND auth_logical_verified_at IS NOT NULL
                AND (scope = 'account' AND organization_id IS NULL AND NOT EXISTS (SELECT 1 FROM users u WHERE u.id = deletion_jobs.subject_id)
                    OR scope = 'organization' AND (organization_id IS NULL OR organization_id = subject_id)
                        AND NOT EXISTS (SELECT 1 FROM organizations o WHERE o.id = deletion_jobs.subject_id)
                    OR scope = 'project' AND organization_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = deletion_jobs.subject_id))
            ORDER BY id LIMIT 20
            """, new { after, platform }), cancellationToken)).AsList();
        var next = new DeletionCursorRecord(Pending, page.Count == 20 ? page[^1].Id : null);
        if (!await data.SaveAsync(position is null ? Change.Insert(next) : Change.Update(position, next), cancellationToken))
        {
            return new([]);
        }

        return new([.. page.Select(Proof).OfType<DashboardDeletionProof>()]);
    }

    internal static async Task<DeletionAcknowledgementResult> AcknowledgeAsync(AuthData data, TimeProvider clock, string? id, DashboardDeletionAcknowledgement request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!PlatformDirectory.IsId(id) || !PlatformDirectory.IsId(request.SubjectId) || request.Scope is not ("account" or "organization" or "project"))
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
            if (job is null || Proof(job.Value) is not { } proof || proof != new DashboardDeletionProof(id!, request.Scope, request.SubjectId, request.OrganizationId))
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

    private static DashboardDeletionProof? Proof(DeletionJob job) =>
        job.DueAt == job.RequestedAt.AddDays(30) && PlatformDirectory.IsId(job.SubjectId) && PlatformDirectory.IsId(job.ErasureId)
        && job.Id == DeletionJob.Key(job.InstanceId, job.Scope, job.SubjectId) && (job.Scope != "project" || PlatformDirectory.IsId(job.OrganizationId))
            ? new(job.ErasureId!, job.Scope, job.SubjectId, job.Scope == "organization" ? job.SubjectId : job.OrganizationId)
            : null;

    private static AuthNotFound Missing() => new("No such deletion proof.", "not_found");
}
