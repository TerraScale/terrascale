using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthPlatformClient
    {
        public async Task<TsAuthDeletionPage> ListDeletionsAsync(
            string clientId, string clientSecret, CancellationToken ct = default)
        {
            var page = await client.SendAuthenticatedJsonAsync(HttpMethod.Get, client.ManagementRoute("/deletions"),
                null, TsAuthIntegrationJsonContext.Default.TsAuthAcknowledgeDeletion,
                TsAuthIntegrationJsonContext.Default.TsAuthDeletionPage,
                TsAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.OK, 16 * 1024, ct).ConfigureAwait(false);
            if (page.Items is null || page.Items.Count > 20 || page.Items.Any(proof => !ValidDeletionProof(proof)))
            {
                throw new TsAuthException(HttpStatusCode.OK, "ts-auth returned an invalid deletion proof page.", "invalid_response", null);
            }
            return page;
        }

        public Task AcknowledgeDeletionAsync(string clientId, string clientSecret,
            TsAuthDeletionProof proof, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(proof);
            if (!ValidDeletionProof(proof))
            {
                throw new ArgumentException("Deletion proofs require canonical ULIDs, a known scope and its exact organization binding.", nameof(proof));
            }
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Post,
                client.ManagementRoute("/deletions/" + proof.Id + "/acknowledge"),
                new TsAuthAcknowledgeDeletion(proof.Scope, proof.SubjectId, proof.OrganizationId),
                TsAuthIntegrationJsonContext.Default.TsAuthAcknowledgeDeletion,
                TsAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.NoContent, 16 * 1024, ct);
        }

        private static bool ValidDeletionProof(TsAuthDeletionProof? proof)
        {
            return proof is not null && TerraScaleIds.IsValid(proof.Id) && TerraScaleIds.IsValid(proof.SubjectId) &&
            proof.Scope switch
            {
                "account" => proof.OrganizationId is null,
                "organization" => proof.OrganizationId == proof.SubjectId,
                "project" => TerraScaleIds.IsValid(proof.OrganizationId),
                _ => false
            };
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record TsAuthDeletionPage(
        [property: JsonPropertyName("items")] IReadOnlyList<TsAuthDeletionProof> Items);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record TsAuthDeletionProof(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("subjectId")] string SubjectId,
        [property: JsonPropertyName("organizationId")] string? OrganizationId);

    internal sealed record TsAuthAcknowledgeDeletion(
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("subjectId")] string SubjectId,
        [property: JsonPropertyName("organizationId")] string? OrganizationId);
}
