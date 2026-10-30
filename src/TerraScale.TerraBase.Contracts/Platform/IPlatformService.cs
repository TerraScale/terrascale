using System.Text.Json;
using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Platform
{
    /// <summary>TerraBase platform APIs, with the REST pipeline authorization and placement rules.</summary>
    public interface IPlatformService : IService<IPlatformService>
    {
        /// <summary><c>GET /v1/platform/databases</c>.</summary>
        UnaryResult<JsonElement> ListDatabasesAsync();

        /// <summary><c>GET /v1/platform/databases/{database}/collections</c>.</summary>
        UnaryResult<JsonElement> ListCollectionsAsync(string database);

        /// <summary><c>GET /v1/platform/databases/{database}/collections/{collection}/documents</c>.</summary>
        UnaryResult<JsonElement> ListDocumentsAsync(string database, string collection, int limit, string? cursor);

        /// <summary><c>GET /v1/platform/databases/{database}/collections/{collection}/documents/by-key?id=</c>.</summary>
        UnaryResult<JsonElement> GetDocumentAsync(string database, string collection, string id);

        /// <summary><c>PUT /admin/organizations/{organization}/plan</c>.</summary>
        UnaryResult<bool> SetOrganizationPlanAsync(string organizationId, JsonElement request);

        /// <summary><c>PUT /admin/projects/{project}/plan</c>.</summary>
        UnaryResult<bool> SetProjectPlanAsync(string projectId, JsonElement request);

    }
}
