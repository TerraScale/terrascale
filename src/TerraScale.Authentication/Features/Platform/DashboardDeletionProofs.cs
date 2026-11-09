using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public sealed record DashboardDeletionProof(string Id, string Scope, string SubjectId, string? OrganizationId);

public sealed record DashboardDeletionList(IReadOnlyList<DashboardDeletionProof> Items);

public sealed record DashboardDeletionAcknowledgement(string Scope, string SubjectId, string? OrganizationId);

public sealed record DeletionCursorRecord(string Name, string? After);


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
        var position = await data.ReadAsync((db, token) => db.DeletionCursors.Where(cursor => cursor.Name == Pending).WithVersion().SingleOrDefaultAsync(token),
            cancellationToken).ConfigureAwait(false);
        var after = position?.Value.After ?? string.Empty;
        var page = await data.ReadAsync((db, token) => Proven(db, db.DeletionJobs.Where(job => job.DashboardVerifiedAt == null && string.Compare(job.Id, after) > 0))
            .OrderBy(job => job.Id).Take(20).ToListAsync(token),
            cancellationToken).ConfigureAwait(false);
        var next = new DeletionCursorRecord(Pending, page.Count == 20 ? page[^1].Id : null);
        if (!await data.SaveAsync(position is null ? Change.Insert(next) : Change.Update(position, next), cancellationToken).ConfigureAwait(false))
        {
            return new([]);
        }

        return new([.. page.Select(Proof).OfType<DashboardDeletionProof>()]);
    }

    internal static async Task AcknowledgeAsync(AuthData data, TimeProvider clock, string? id, DashboardDeletionAcknowledgement request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!PlatformDirectory.IsId(id) || !PlatformDirectory.IsId(request.SubjectId) || request.Scope is not ("account" or "organization" or "project"))
        {
            throw Missing();
        }

        var key = DeletionJob.Key(PlatformDirectory.Instance, request.Scope, request.SubjectId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var job = await data.ReadAsync((db, token) => Proven(db, db.DeletionJobs.Where(job => job.Id == key)).WithVersion().SingleOrDefaultAsync(token),
                cancellationToken).ConfigureAwait(false);
            if (job is null || Proof(job.Value) is not { } proof || proof != new DashboardDeletionProof(id!, request.Scope, request.SubjectId, request.OrganizationId))
            {
                throw Missing();
            }

            if (job.Value.DashboardVerifiedAt is not null
                || await data.SaveAsync(Change.Update(job, job.Value with { DashboardVerifiedAt = clock.GetUtcNow() }), cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new ApiException(StatusCodes.Status409Conflict, "deletion_changed", "Retry acknowledgement of the same proof.");
    }

    private static IQueryable<DeletionJob> Proven(AuthDbContext db, IQueryable<DeletionJob> jobs) =>
        jobs.Where(job => job.InstanceId == PlatformDirectory.Instance && job.AuthLogicalVerifiedAt != null
            && (job.Scope == "account" && job.OrganizationId == null && !db.Users.Any(user => user.Id == job.SubjectId)
                || job.Scope == "organization" && (job.OrganizationId == null || job.OrganizationId == job.SubjectId) && !db.Organizations.Any(organization => organization.Id == job.SubjectId)
                || job.Scope == "project" && job.OrganizationId != null && !db.Projects.Any(project => project.Id == job.SubjectId)));

    private static DashboardDeletionProof? Proof(DeletionJob job) =>
        job.DueAt == job.RequestedAt.AddDays(30) && PlatformDirectory.IsId(job.SubjectId) && PlatformDirectory.IsId(job.ErasureId)
        && job.Id == DeletionJob.Key(job.InstanceId, job.Scope, job.SubjectId) && (job.Scope != "project" || PlatformDirectory.IsId(job.OrganizationId))
            ? new(job.ErasureId!, job.Scope, job.SubjectId, job.Scope == "organization" ? job.SubjectId : job.OrganizationId)
            : null;

    private static ApiException Missing() => new(StatusCodes.Status404NotFound, "not_found", "No such deletion proof.");
}
