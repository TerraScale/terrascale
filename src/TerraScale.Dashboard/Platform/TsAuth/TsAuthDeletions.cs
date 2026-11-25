using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthPlatformClient
    {
        public const int DeletionPageLimit = 20;

        public async Task<IReadOnlyList<TsAuthDeletionItem>> ListDeletionsAsync(
            string clientId, string clientSecret, CancellationToken ct = default)
        {
            var page = await client.SendAuthenticatedJsonAsync(HttpMethod.Get, TsAuthClient.ManagementRoute("/deletions"),
                null, TsAuthIntegrationJsonContext.Default.TsAuthAcknowledgeDeletion,
                TsAuthIntegrationJsonContext.Default.TsAuthDeletionPage,
                TsAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.OK, 16 * 1024, ct);
            if (page.Items is not { Count: <= DeletionPageLimit } items)
            {
                throw new TsAuthException(HttpStatusCode.OK, "ts-auth returned an invalid deletion proof page.", "invalid_response", null);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<TsAuthDeletionItem>(items.Count);
            foreach (var item in items)
            {
                var proof = ReadDeletionProof(item);
                result.Add(proof switch
                {
                    TsAuthDeletionProof valid when !seen.Add(valid.Id) => new TsAuthRejectedDeletion(valid.Id, "duplicate"),
                    TsAuthDeletionProof valid => valid,
                    TsAuthRejectedDeletion rejected => rejected,
                });
            }

            return result;
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
                TsAuthClient.ManagementRoute("/deletions/" + proof.Id + "/acknowledge"),
                new TsAuthAcknowledgeDeletion(proof.Scope, proof.SubjectId, proof.OrganizationId),
                TsAuthIntegrationJsonContext.Default.TsAuthAcknowledgeDeletion,
                TsAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.NoContent, 16 * 1024, ct);
        }

        internal static TsAuthDeletionItem ReadDeletionProof(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return new TsAuthRejectedDeletion(null, "not_object");
            }

            var id = Text(item, "id");
            var safeId = TerraScaleIds.IsValid(id) ? id : null;
            if (id is null || Text(item, "scope") is not { } scope || Text(item, "subjectId") is not { } subjectId)
            {
                return new TsAuthRejectedDeletion(safeId, "missing_field");
            }

            string? organizationId = null;
            if (item.TryGetProperty("organizationId", out var organization) && organization.ValueKind != JsonValueKind.Null)
            {
                if (organization.ValueKind != JsonValueKind.String)
                {
                    return new TsAuthRejectedDeletion(safeId, "invalid_organization");
                }

                organizationId = organization.GetString();
            }

            var proof = new TsAuthDeletionProof(id, scope, subjectId, organizationId);
            return ValidDeletionProof(proof) ? proof : new TsAuthRejectedDeletion(safeId, "invalid_binding");
        }

        private static string? Text(JsonElement item, string name)
        {
            return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        internal static bool ValidDeletionProof(TsAuthDeletionProof? proof)
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

    public sealed record TsAuthRejectedDeletion(string? Id, string Reason);

    public readonly union TsAuthDeletionItem(TsAuthDeletionProof, TsAuthRejectedDeletion);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record TsAuthDeletionPage(
        [property: JsonPropertyName("items")] IReadOnlyList<JsonElement> Items);

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
