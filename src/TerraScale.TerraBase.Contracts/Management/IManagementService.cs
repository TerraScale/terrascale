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

        /// <summary><c>POST /v1/projects/{project}/databases</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> CreateDatabaseAsync(string projectId, TerraBaseDatabasePlacement request);

        /// <summary><c>PATCH /v1/projects/{project}/databases/{database}</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> SetDatabaseAsync(string projectId, string database, TerraBaseDatabasePlacement request);

        /// <summary><c>DELETE /v1/projects/{project}/databases/{database}</c>.</summary>
        UnaryResult<bool> DeleteDatabaseAsync(string projectId, string database);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/branches</c>.</summary>
        UnaryResult<TerraBaseManagedDatabase> BranchAsync(string projectId, string database, string name);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/schema</c>.</summary>
        UnaryResult<TerraBaseCollectionStatsPage> ListCollectionStatsAsync(string projectId, string database);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/indexes</c>.</summary>
        UnaryResult<TerraBaseIndexPage> ListIndexesAsync(string projectId, string database);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/indexes</c>.</summary>
        UnaryResult<TerraBaseIndex> CreateIndexAsync(string projectId, string database, TerraBaseCreateIndex request);

        /// <summary><c>DELETE /v1/projects/{project}/databases/{database}/indexes/{name}</c>.</summary>
        UnaryResult<bool> DropIndexAsync(string projectId, string database, string name);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/sql/execute</c>.</summary>
        UnaryResult<JsonElement> ExecuteSqlAsync(string projectId, string database, JsonElement request);

    }
}
