using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Platform;

namespace TerraScale.TerraBase.Rpc.Platform
{
    /// <summary>Serves platform RPCs through the matching REST routes.</summary>
    public sealed class PlatformService(RestApi api) : ServiceBase<IPlatformService>, IPlatformService
    {
        /// <inheritdoc />
        public async UnaryResult<JsonElement> ListDatabasesAsync()
        {
            var answer = await api.SendAsync(Context, "GET", "/v1/platform/databases");
            return RestApi.Read(answer, PlatformJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<bool> SetOrganizationPlanAsync(string organizationId, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "PUT", "/admin/organizations/" + Uri.EscapeDataString(organizationId) + "/plan", RestApi.Json(request, PlatformJson.Default.JsonElement));
            RestApi.Ensure(answer);
            return true;
        }

        /// <inheritdoc />
        public async UnaryResult<bool> SetProjectPlanAsync(string projectId, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "PUT", "/admin/projects/" + Uri.EscapeDataString(projectId) + "/plan", RestApi.Json(request, PlatformJson.Default.JsonElement));
            RestApi.Ensure(answer);
            return true;
        }

    }
}
