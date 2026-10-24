using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthPlatformClient
{
    /// <summary>
    /// GET /v1/{slug}/deletions with service client Basic credentials. Returns at most 20 completed,
    /// unacknowledged deletion proofs. Validates every proof before returning the page; sent once.
    /// </summary>
    public async Task<TerraScaleAuthDeletionPage> ListDeletionsAsync(
        string clientId, string clientSecret, CancellationToken ct = default)
    {
        var page = await client.SendAuthenticatedJsonAsync(HttpMethod.Get, client.ManagementRoute("/deletions"),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthAcknowledgeDeletion,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthDeletionPage,
            TerraScaleAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.OK, 16 * 1024, ct).ConfigureAwait(false);
        if (page.Items is null || page.Items.Count > 20 || page.Items.Any(proof => !ValidDeletionProof(proof)))
        {
            throw new TerraScaleAuthException(HttpStatusCode.OK, "ts-auth returned an invalid deletion proof page.", "invalid_response", null);
        }
        return page;
    }

    /// <summary>
    /// POST /v1/{slug}/deletions/{id}/acknowledge with service client Basic credentials and the exact proof
    /// scope, subject and organization. Requires 204. The server compares the exact proof idempotently;
    /// acknowledge only after durable consumer cleanup. Each call is sent once, without automatic retries.
    /// </summary>
    public Task AcknowledgeDeletionAsync(string clientId, string clientSecret,
        TerraScaleAuthDeletionProof proof, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (!ValidDeletionProof(proof))
        {
            throw new ArgumentException("Deletion proofs require canonical ULIDs, a known scope and its exact organization binding.", nameof(proof));
        }
        return client.SendAuthenticatedForStatusAsync(HttpMethod.Post,
            client.ManagementRoute("/deletions/" + proof.Id + "/acknowledge"),
            new TerraScaleAuthAcknowledgeDeletion(proof.Scope, proof.SubjectId, proof.OrganizationId),
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthAcknowledgeDeletion,
            TerraScaleAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.NoContent, 16 * 1024, ct);
    }

    private static bool ValidDeletionProof(TerraScaleAuthDeletionProof? proof) =>
        proof is not null && TerraScaleIds.IsValid(proof.Id) && TerraScaleIds.IsValid(proof.SubjectId) &&
        proof.Scope switch
        {
            "account" => proof.OrganizationId is null,
            "organization" => proof.OrganizationId == proof.SubjectId,
            "project" => TerraScaleIds.IsValid(proof.OrganizationId),
            _ => false
        };
}

/// <summary>A bounded batch of completed deletion proofs awaiting consumer acknowledgement.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TerraScaleAuthDeletionPage(
    [property: JsonPropertyName("items")] IReadOnlyList<TerraScaleAuthDeletionProof> Items);

/// <summary>An exact deletion identity: account has no organization, organization binds itself, project binds its organization.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TerraScaleAuthDeletionProof(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("organizationId")] string? OrganizationId);

internal sealed record TerraScaleAuthAcknowledgeDeletion(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("organizationId")] string? OrganizationId);
