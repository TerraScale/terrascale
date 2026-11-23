using System.Net;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthPlatformClient
{
    public async Task<AuthResult<TerraScaleAuthDeletionPage>> ListDeletionsAsync(
        string clientId, string clientSecret, CancellationToken ct = default)
    {
        var page = await client.SendAuthenticatedJsonAsync(HttpMethod.Get, client.ManagementRoute("/deletions"),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthAcknowledgeDeletion,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthDeletionPage,
            TerraScaleAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.OK, 16 * 1024, ct).ConfigureAwait(false);
        return page switch
        {
            TerraScaleAuthDeletionPage listed => listed.Items is null || listed.Items.Count > 20 || listed.Items.Any(proof => !ValidDeletionProof(proof))
                ? TerraScaleAuthErrors.InvalidResponse("ts-auth returned an invalid deletion proof page.")
                : listed,
            AuthRefused refused => refused,
        };
    }

    public Task<AuthOutcome> AcknowledgeDeletionAsync(string clientId, string clientSecret,
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TerraScaleAuthDeletionPage(
    [property: JsonPropertyName("items")] IReadOnlyList<TerraScaleAuthDeletionProof> Items);

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
