using System.Text.Json;
using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Platform
{
    /// <summary>TerraBase platform APIs, with the REST pipeline authorization and placement rules.</summary>
    public interface IPlatformService : IService<IPlatformService>
    {
        /// <summary><c>GET /v1/platform/databases</c>.</summary>
        UnaryResult<JsonElement> ListDatabasesAsync();

        /// <summary><c>PUT /admin/organizations/{organization}/plan</c>.</summary>
        UnaryResult<bool> SetOrganizationPlanAsync(string organizationId, JsonElement request);

        /// <summary><c>PUT /admin/projects/{project}/plan</c>.</summary>
        UnaryResult<bool> SetProjectPlanAsync(string projectId, JsonElement request);

    }
}
