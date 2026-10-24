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

/// <summary>Private service API metadata; unknown acknowledgement fields are rejected.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DashboardDeletionList))]
[JsonSerializable(typeof(DashboardDeletionAcknowledgement))]
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
        var documents = await store.QueryAsync(AuthStore.DeletionJobCollection, filter.RootElement, 20,
            [new TerraBaseSort("/dashboardDeliveryTurn")], cancellationToken).ConfigureAwait(false);
        List<DashboardDeletionProof> items = [];
        foreach (var document in documents)
        {
            // Rotate every candidate before validation, including a corrupt job with no trustworthy natural key.
            // Missing/null sorts before numbers; thereafter the least visited jobs lead, with id breaking ties.
            var value = JsonNode.Parse(document.Document.GetRawText())!.AsObject();
            var turn = document.Document.TryGetProperty("dashboardDeliveryTurn", out var previous)
                && previous.ValueKind == JsonValueKind.Number && previous.TryGetInt64(out var number)
                && number is >= 0 and < long.MaxValue ? number : 0;
            value["dashboardDeliveryTurn"] = checked(turn + 1);
            try
            {
                await database.PutDocumentAsync(AuthStore.DeletionJobCollection, document.Id,
                    JsonSerializer.SerializeToElement(value, DashboardDeletionJson.Default.JsonObject),
                    TerraBasePrecondition.Matches(document.Version), cancellationToken).ConfigureAwait(false);
            }
            catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
            {
                // Another poll, acknowledgement or cleanup won. No unbounded retry or stale proof delivery.
                continue;
            }

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
