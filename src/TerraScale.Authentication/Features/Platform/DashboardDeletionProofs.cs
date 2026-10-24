using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>A platform scope whose auth logical deletion has been verified; id is its durable erasure ULID.</summary>
public sealed record DashboardDeletionProof(string Id, string Scope, string SubjectId, string? OrganizationId);

/// <summary>A bounded batch of proofs awaiting dashboard-owned cleanup.</summary>
public sealed record DashboardDeletionList(IReadOnlyList<DashboardDeletionProof> Items);

/// <summary>The exact scope being acknowledged for the proof id in the route.</summary>
public sealed record DashboardDeletionAcknowledgement(string Scope, string SubjectId, string? OrganizationId);

/// <summary>Auth-owned durable position in the SDK query; null starts the next traversal.</summary>
public sealed record DashboardDeletionCursor(string? NextCursor);

/// <summary>Private service API metadata; unknown acknowledgement fields are rejected.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DashboardDeletionList))]
[JsonSerializable(typeof(DashboardDeletionAcknowledgement))]
[JsonSerializable(typeof(DashboardDeletionCursor))]
[JsonSerializable(typeof(JsonObject))]
public sealed partial class DashboardDeletionJson : JsonSerializerContext;

internal static class DashboardDeletionProofs
{
    internal static async Task<DashboardDeletionList> ListAsync(AuthStore store, TerraBaseDatabase database, CancellationToken cancellationToken)
    {
        using var filter = JsonDocument.Parse(new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/instanceId", PlatformDirectory.Instance) },
                new JsonObject { ["ne"] = new JsonArray("/authLogicalVerifiedAt", null) },
                new JsonObject { ["eq"] = new JsonArray("/dashboardVerifiedAt", null) }),
        }.ToJsonString());
        var cursorId = AuthStore.NaturalKeyId(PlatformDirectory.Instance, AuthStore.DashboardDeletionCursorCollection, "pending");
        var position = await database.GetDocumentAsync(AuthStore.DashboardDeletionCursorCollection, cursorId, cancellationToken).ConfigureAwait(false);
        var cursor = position?.Document.Deserialize(DashboardDeletionJson.Default.DashboardDeletionCursor);
        var page = await database.QueryAsync(AuthStore.DeletionJobCollection, new TerraBaseQuery
        {
            Filter = filter.RootElement,
            Limit = 20,
            Cursor = cursor?.NextCursor,
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            // Claim the entire page before validating anything. Even malformed or maximum-size jobs stay untouched.
            // The SDK's null NextCursor wraps the next poll to the beginning, without a second query this poll.
            await database.PutDocumentAsync(AuthStore.DashboardDeletionCursorCollection, cursorId,
                JsonSerializer.SerializeToElement(new DashboardDeletionCursor(page.NextCursor), DashboardDeletionJson.Default.DashboardDeletionCursor),
                position is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(position.Version), cancellationToken).ConfigureAwait(false);
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
        {
            // Another poll advanced the durable position. Retry on the next poll, with no retry scan here.
            return new([]);
        }

        List<DashboardDeletionProof> items = [];
        foreach (var document in page.Items)
        {
            if (await ProofAsync(store, document, cancellationToken).ConfigureAwait(false) is { } proof)
            {
                items.Add(proof);
            }
        }

        return new(items);
    }

    internal static async Task AcknowledgeAsync(AuthStore store, TimeProvider clock, string? id, DashboardDeletionAcknowledgement request, CancellationToken cancellationToken)
    {
        if (!PlatformDirectory.IsId(id) || !PlatformDirectory.IsId(request.SubjectId) || request.Scope is not ("account" or "organization" or "project"))
        {
            throw Missing();
        }

        var key = DeletionJob.Key(PlatformDirectory.Instance, request.Scope, request.SubjectId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var document = await store.GetAsync(AuthStore.DeletionJobCollection, PlatformDirectory.Instance, key, cancellationToken).ConfigureAwait(false);
            if (document is null || await ProofAsync(store, document, cancellationToken).ConfigureAwait(false) is not { } proof
                || proof != new DashboardDeletionProof(id!, request.Scope, request.SubjectId, request.OrganizationId))
            {
                throw Missing();
            }

            var job = document.Document.Deserialize(DeletionJson.Default.DeletionJob)!;
            if (job.DashboardVerifiedAt is not null || await store.CompareAndPutAsync(AuthStore.DeletionJobCollection, job.InstanceId, job.Id,
                JsonSerializer.SerializeToElement(job with { DashboardVerifiedAt = clock.GetUtcNow() }, DeletionJson.Default.DeletionJob), document.Version, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new ApiException(StatusCodes.Status409Conflict, "deletion_changed", "Retry acknowledgement of the same proof.");
    }

    private static async Task<DashboardDeletionProof?> ProofAsync(AuthStore store, TerraBaseDocument document, CancellationToken cancellationToken)
    {
        DeletionJob? job;
        try
        {
            job = document.Document.Deserialize(DeletionJson.Default.DeletionJob);
            if (job is not null && job.DueAt != job.RequestedAt.AddDays(30))
            {
                return null;
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }

        if (job is null || job.InstanceId != PlatformDirectory.Instance || !PlatformDirectory.IsId(job.SubjectId)
            || !PlatformDirectory.IsId(job.ErasureId) || job.Id != DeletionJob.Key(job.InstanceId, job.Scope, job.SubjectId)
            || document.Id != job.FenceId || job.AuthLogicalVerifiedAt is null)
        {
            return null;
        }

        var root = job.Scope switch
        {
            "account" when job.OrganizationId is null => AuthStore.UserCollection,
            "organization" when job.OrganizationId is null || job.OrganizationId == job.SubjectId => AuthStore.OrganizationCollection,
            "project" when PlatformDirectory.IsId(job.OrganizationId) => AuthStore.ProjectCollection,
            _ => null,
        };
        if (root is null || await store.GetAsync(root, job.InstanceId, job.SubjectId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        return new(job.ErasureId!, job.Scope, job.SubjectId, job.Scope == "organization" ? job.SubjectId : job.OrganizationId);
    }

    private static ApiException Missing() => new(StatusCodes.Status404NotFound, "not_found", "No such deletion proof.");
}
