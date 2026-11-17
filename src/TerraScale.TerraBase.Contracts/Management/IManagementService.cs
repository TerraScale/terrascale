using System.Text.Json;
using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Management
{
    /// <summary>TerraBase management APIs, with the REST pipeline authorization and placement rules.</summary>
    public interface IManagementService : IService<IManagementService>
    {
        /// <summary><c>GET /v1/projects/{project}/databases</c>.</summary>
        UnaryResult<TerraBaseDatabasePage> ListDatabasesAsync(string projectId, string? after, int? limit);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> GetDatabaseAsync(string projectId, string database);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/regions</c>.</summary>
        UnaryResult<TerraBaseDatabaseRegions> GetDatabaseRegionsAsync(string projectId, string database);

        /// <summary><c>POST /v1/projects/{project}/databases</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> CreateDatabaseAsync(string projectId, TerraBaseDatabasePlacement request);

        /// <summary><c>PATCH /v1/projects/{project}/databases/{database}</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> SetDatabaseAsync(string projectId, string database, TerraBaseDatabasePlacement request);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/move-home</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> MoveDatabaseHomeAsync(string projectId, string database, TerraBaseMoveHome request);

        /// <summary><c>DELETE /v1/projects/{project}/databases/{database}</c>.</summary>
        UnaryResult<bool> DeleteDatabaseAsync(string projectId, string database);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/branches</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> BranchAsync(string projectId, string database, string name, long? ttlSeconds = null);

        /// <summary><c>PUT /v1/projects/{project}/databases/{database}/ttl</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> SetDatabaseTtlAsync(string projectId, string database, TerraBaseDatabaseTtl request);

        /// <summary><c>DELETE /v1/projects/{project}/databases/{database}/ttl</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> ClearDatabaseTtlAsync(string projectId, string database);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/sql/execute</c>.</summary>
        UnaryResult<JsonElement> ExecuteSqlAsync(string projectId, string database, JsonElement request);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/rules</c>.</summary>
        UnaryResult<TerraBaseRules> GetRulesAsync(string projectId, string database);

        /// <summary><c>PUT /v1/projects/{project}/databases/{database}/rules</c> with <c>If-Match</c> on <paramref name="version"/>, or <c>If-None-Match: *</c> when it is null.</summary>
        UnaryResult<TerraBaseRulesDeploy> DeployRulesAsync(string projectId, string database, string? version, JsonElement rules);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/rules/versions</c>.</summary>
        UnaryResult<TerraBaseRulesVersionPage> ListRulesVersionsAsync(string projectId, string database, string? after, int? limit);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/rules/versions/{version}</c>.</summary>
        UnaryResult<TerraBaseRules> GetRulesVersionAsync(string projectId, string database, string version);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/rules/versions/{version}/restore</c> with <c>If-Match</c> on <paramref name="current"/>.</summary>
        UnaryResult<TerraBaseRulesDeploy> RestoreRulesVersionAsync(string projectId, string database, string version, string current);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/rules:test</c>.</summary>
        UnaryResult<TerraBaseRulesTestResult> TestRulesAsync(string projectId, string database, JsonElement request);
    }
}
