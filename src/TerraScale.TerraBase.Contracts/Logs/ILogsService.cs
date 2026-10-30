using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Logs
{
    /// <summary>The logs API, authorized and metered through the corresponding REST routes.</summary>
    public interface ILogsService : IService<ILogsService>
    {
        /// <summary><c>GET …/logs</c>: authorized public request logs.</summary>
        UnaryResult<TerraBaseRequestLogPage> ReadAsync(string projectId, string database, string? status, string? after);

        /// <summary><c>GET /v1/usage/projects/{project}/databases/{databaseId}/logs</c>: operator cursor read for log drains.</summary>
        UnaryResult<TerraBaseRequestLogPage> ReadDrainAsync(string projectId, string databaseId, string after);

        /// <summary><c>GET …/query-shapes</c>.</summary>
        UnaryResult<TerraBaseQueryShapePage> ListQueryShapesAsync(string projectId, string database);
    }
}
