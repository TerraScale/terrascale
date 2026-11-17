using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.AspNetCore.Http;
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
        public async UnaryResult<TerraBaseRules> GetRulesAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", Rules(projectId, database));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseRules);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseRulesDeploy> DeployRulesAsync(string projectId, string database, string? version, JsonElement rules)
        {
            var answer = await api.SendAsync(Context, "PUT", Rules(projectId, database), RestApi.Json(rules, ManagementJson.Default.JsonElement),
                headers: sent =>
                {
                    if (version is null)
                    {
                        sent.IfNoneMatch = "*";
                    }
                    else
                    {
                        sent.IfMatch = "\"" + version + "\"";
                    }
                });
            return Deployed(answer);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseRulesVersionPage> ListRulesVersionsAsync(string projectId, string database, string? after, int? limit)
        {
            var answer = await api.SendAsync(Context, "GET", Rules(projectId, database) + "/versions" + RestPaths.Query(("after", after), ("limit", RestPaths.Number(limit))));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseRulesVersionPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseRules> GetRulesVersionAsync(string projectId, string database, string version)
        {
            var answer = await api.SendAsync(Context, "GET", Rules(projectId, database) + "/versions/" + Uri.EscapeDataString(version));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseRules);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseRulesDeploy> RestoreRulesVersionAsync(string projectId, string database, string version, string current)
        {
            var answer = await api.SendAsync(Context, "POST", Rules(projectId, database) + "/versions/" + Uri.EscapeDataString(version) + "/restore",
                headers: sent => sent.IfMatch = "\"" + current + "\"");
            return Deployed(answer);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseRulesTestResult> TestRulesAsync(string projectId, string database, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "POST", Rules(projectId, database) + ":test", RestApi.Json(request, ManagementJson.Default.JsonElement));
            return RestApi.Read(answer, ManagementJson.Default.TerraBaseRulesTestResult);
        }

        private static string Rules(string projectId, string database) => RestPaths.Database(projectId, database) + "/rules";

        private static TerraBaseRulesDeploy Deployed(RestAnswer answer)
        {
            if (answer.Status != StatusCodes.Status400BadRequest || RestApi.FaultOf(answer).Code != "invalid_rules")
            {
                return new(RestApi.Read(answer, ManagementJson.Default.TerraBaseRules), []);
            }

            using var body = JsonDocument.Parse(answer.Body);
            return body.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("details", out var details)
                && details.TryGetProperty("problems", out var problems) && problems.ValueKind == JsonValueKind.Array
                && problems.Deserialize(ManagementJson.Default.IReadOnlyListTerraBaseRulesProblem) is { Count: > 0 } found
                ? new(null, found)
                : throw RestApi.Fault(answer);
        }
    }
}
