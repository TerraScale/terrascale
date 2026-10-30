using System.Text.Json;
using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Usage
{
    /// <summary>TerraBase usage APIs, with the REST pipeline authorization and placement rules.</summary>
    public interface IUsageService : IService<IUsageService>
    {
        /// <summary><c>GET /v1/projects/{project}/usage</c>.</summary>
        UnaryResult<TerraBaseProjectUsage> GetProjectUsageAsync(string projectId, string? granularity, DateTimeOffset? from, DateTimeOffset? until);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/usage</c>.</summary>
        UnaryResult<TerraBaseDatabaseUsage> GetDatabaseUsageAsync(string projectId, string database, string? granularity, DateTimeOffset? from, DateTimeOffset? until);

        /// <summary><c>GET /v1/projects/{project}/analytics</c>.</summary>
        UnaryResult<JsonElement> GetAnalyticsAsync(string projectId, string range, string? database, string? region);

        /// <summary><c>GET /v1/projects/{project}/replication</c>.</summary>
        UnaryResult<JsonElement> GetReplicationAsync(string projectId, string range, string? database);

        /// <summary><c>GET /v1/usage/projects/{project}/analytics?range=1h</c>.</summary>
        UnaryResult<JsonElement> GetOperatorAnalyticsAsync(string projectId);

        /// <summary><c>GET /v1/usage/allowances</c>.</summary>
        UnaryResult<JsonElement> ListAllowancesAsync(string? after);

    }
}
