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

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/read-grants</c>.</summary>
        UnaryResult<TerraBaseReadGrantPage> ListReadGrantsAsync(string projectId, string database);

        /// <summary><c>PUT /v1/projects/{project}/databases/{database}/tables/{table}/read-grant</c>.</summary>
        UnaryResult<TerraBaseReadGrant> PutReadGrantAsync(string projectId, string database, string table, TerraBaseReadGrantRule request);

        /// <summary><c>DELETE /v1/projects/{project}/databases/{database}/tables/{table}/read-grant</c>.</summary>
        UnaryResult<bool> DeleteReadGrantAsync(string projectId, string database, string table);

    }
}
