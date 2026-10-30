using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraScale.TerraBase.Rpc.Management
{
    /// <summary>Serves management RPCs through the matching REST routes.</summary>
    public sealed class ManagementService(RestApi api) : ServiceBase<IManagementService>, IManagementService
    {
        /// <inheritdoc />
        public async UnaryResult<TerraBaseDatabasePage> ListDatabasesAsync(string projectId, string? after, int? limit)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Project(projectId) + "/databases" + RestPaths.Query(("after", after), ("limit", RestPaths.Number(limit))));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseDatabasePage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseManagedDatabase> GetDatabaseAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseManagedDatabase);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseManagedDatabase> CreateDatabaseAsync(string projectId, TerraBaseDatabasePlacement request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Project(projectId) + "/databases", RestApi.Json(request, ManagementJson.Default.TerraBaseDatabasePlacement));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseManagedDatabase);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseManagedDatabase> SetDatabaseAsync(string projectId, string database, TerraBaseDatabasePlacement request)
        {
            var answer = await api.SendAsync(Context, "PATCH", RestPaths.Database(projectId, database), RestApi.Json(request, ManagementJson.Default.TerraBaseDatabasePlacement));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseManagedDatabase);
        }

        /// <inheritdoc />
        public async UnaryResult<bool> DeleteDatabaseAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Database(projectId, database));
            RestApi.Ensure(answer);
            return true;
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseManagedDatabase> BranchAsync(string projectId, string database, string name)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/branches", RestApi.Json(new TerraBaseDatabasePlacement(Name: name), ManagementJson.Default.TerraBaseDatabasePlacement));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseManagedDatabase);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseCollectionStatsPage> ListCollectionStatsAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/schema");
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseCollectionStatsPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseIndexPage> ListIndexesAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/indexes");
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseIndexPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseIndex> CreateIndexAsync(string projectId, string database, TerraBaseCreateIndex request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/indexes", RestApi.Json(request, ManagementJson.Default.TerraBaseCreateIndex));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseIndex);
        }

        /// <inheritdoc />
        public async UnaryResult<bool> DropIndexAsync(string projectId, string database, string name)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Database(projectId, database) + "/indexes/" + Uri.EscapeDataString(name));
            RestApi.Ensure(answer);
            return true;
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> ExecuteSqlAsync(string projectId, string database, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/sql/execute", RestApi.Json(request, ManagementJson.Default.JsonElement));
            return RestApi.Read(answer, ManagementJson.Default.JsonElement);
        }

    }
}
