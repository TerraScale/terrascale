using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Store;

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
    internal static async Task<DashboardDeletionList> ListAsync(AuthStore store, CancellationToken cancellationToken)
    {
        using var filter = JsonDocument.Parse(new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/instanceId", PlatformDirectory.Instance) },
                new JsonObject { ["ne"] = new JsonArray("/authLogicalVerifiedAt", null) },
                new JsonObject { ["eq"] = new JsonArray("/dashboardVerifiedAt", null) }),
        }.ToJsonString());
        var position = await store.GetAsync(AuthStore.DashboardDeletionCursorTable, PlatformDirectory.Instance, "pending", cancellationToken).ConfigureAwait(false);
        var cursor = position?.Body.Deserialize(DashboardDeletionJson.Default.DashboardDeletionCursor);
        StoredPage page;
        try
        {
            page = await store.PageAsync(AuthStore.DeletionJobTable, filter.RootElement, 20, cursor?.NextCursor, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreCursorException)
        {
            page = await store.PageAsync(AuthStore.DeletionJobTable, filter.RootElement, 20, null, cancellationToken).ConfigureAwait(false);
        }

        var next = JsonSerializer.SerializeToElement(new DashboardDeletionCursor(page.NextCursor), DashboardDeletionJson.Default.DashboardDeletionCursor);
        var claimed = position is null
            ? await TryCreateAsync(store, next, cancellationToken).ConfigureAwait(false)
            : await store.CompareAndPutAsync(AuthStore.DashboardDeletionCursorTable, PlatformDirectory.Instance, "pending", next, position.Version, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            return new([]);
        }

        List<DashboardDeletionProof> items = [];
        foreach (var rowData in page.Items)
        {
            if (await ProofAsync(store, rowData, cancellationToken).ConfigureAwait(false) is { } proof)
            {
                items.Add(proof);
            }
        }

        return new(items);
    }

    private static async Task<bool> TryCreateAsync(AuthStore store, JsonElement next, CancellationToken cancellationToken)
    {
        try
        {
            await store.CreateAsync(AuthStore.DashboardDeletionCursorTable, PlatformDirectory.Instance, "pending", next, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorePreconditionFailedException)
        {
            return false;
        }
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
            var rowData = await store.GetAsync(AuthStore.DeletionJobTable, PlatformDirectory.Instance, key, cancellationToken).ConfigureAwait(false);
            if (rowData is null || await ProofAsync(store, rowData, cancellationToken).ConfigureAwait(false) is not { } proof
                || proof != new DashboardDeletionProof(id!, request.Scope, request.SubjectId, request.OrganizationId))
            {
                throw Missing();
            }

            var job = rowData.Body.Deserialize(DeletionJson.Default.DeletionJob)!;
            if (job.DashboardVerifiedAt is not null || await store.CompareAndPutAsync(AuthStore.DeletionJobTable, job.InstanceId, job.Id,
                JsonSerializer.SerializeToElement(job with { DashboardVerifiedAt = clock.GetUtcNow() }, DeletionJson.Default.DeletionJob), rowData.Version, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new ApiException(StatusCodes.Status409Conflict, "deletion_changed", "Retry acknowledgement of the same proof.");
    }

    private static async Task<DashboardDeletionProof?> ProofAsync(AuthStore store, StoredRow rowData, CancellationToken cancellationToken)
    {
        DeletionJob? job;
        try
        {
            job = rowData.Body.Deserialize(DeletionJson.Default.DeletionJob);
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
            || rowData.Id != job.FenceId || job.AuthLogicalVerifiedAt is null)
        {
            return null;
        }

        var root = job.Scope switch
        {
            "account" when job.OrganizationId is null => AuthStore.UserTable,
            "organization" when job.OrganizationId is null || job.OrganizationId == job.SubjectId => AuthStore.OrganizationTable,
            "project" when PlatformDirectory.IsId(job.OrganizationId) => AuthStore.ProjectTable,
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
