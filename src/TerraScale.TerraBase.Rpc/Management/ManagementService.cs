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
        public async UnaryResult<TerraBaseDatabaseRegions> GetDatabaseRegionsAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/regions");
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseDatabaseRegions);
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
        public async UnaryResult<TerraBaseManagedDatabase> MoveDatabaseHomeAsync(string projectId, string database, TerraBaseMoveHome request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/move-home", RestApi.Json(request, ManagementJson.Default.TerraBaseMoveHome));
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
        public async UnaryResult<TerraBaseManagedDatabase> BranchAsync(string projectId, string database, string name, long? ttlSeconds = null)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/branches", RestApi.Json(new TerraBaseDatabasePlacement(Name: name, TtlSeconds: ttlSeconds), ManagementJson.Default.TerraBaseDatabasePlacement));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseManagedDatabase);
        }

        /// <summary><c>PUT /v1/projects/{project}/databases/{database}/ttl</c>.</summary>
        public async UnaryResult<TerraBaseManagedDatabase> SetDatabaseTtlAsync(string projectId, string database, TerraBaseDatabaseTtl request)
        {
            var answer = await api.SendAsync(Context, "PUT", RestPaths.Database(projectId, database) + "/ttl", RestApi.Json(request, ManagementJson.Default.TerraBaseDatabaseTtl));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseManagedDatabase);
        }

        /// <summary><c>DELETE /v1/projects/{project}/databases/{database}/ttl</c>.</summary>
        public async UnaryResult<TerraBaseManagedDatabase> ClearDatabaseTtlAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Database(projectId, database) + "/ttl");
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseManagedDatabase);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> ExecuteSqlAsync(string projectId, string database, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/sql/execute", RestApi.Json(request, ManagementJson.Default.JsonElement));
            return RestApi.Read(answer, ManagementJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseReadGrantPage> ListReadGrantsAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/read-grants");
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseReadGrantPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseReadGrant> PutReadGrantAsync(string projectId, string database, string table, TerraBaseReadGrantRule request)
        {
            var answer = await api.SendAsync(Context, "PUT", RestPaths.Database(projectId, database) + "/tables/" + Uri.EscapeDataString(table) + "/read-grant",
                RestApi.Json(request, ManagementJson.Default.TerraBaseReadGrantRule));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseReadGrant);
        }

        /// <inheritdoc />
        public async UnaryResult<bool> DeleteReadGrantAsync(string projectId, string database, string table)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Database(projectId, database) + "/tables/" + Uri.EscapeDataString(table) + "/read-grant");
            RestApi.Ensure(answer);
            return true;
        }

    }
}
