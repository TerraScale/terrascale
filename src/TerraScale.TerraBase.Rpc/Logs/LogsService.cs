using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Logs;

namespace TerraScale.TerraBase.Rpc.Logs
{
    /// <summary>Serves the logs contracts through TerraBase’s REST pipeline.</summary>
    public sealed class LogsService(RestApi api) : ServiceBase<ILogsService>, ILogsService
    {
        private static readonly LogsJson Json = LogsJson.Default;

        /// <inheritdoc />
        public async UnaryResult<TerraBaseRequestLogPage> ReadAsync(string projectId, string database, string? status, string? after)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/logs" + RestPaths.Query(("status", status), ("after", after)));
            return RestApi.Read(answer, Json.TerraBaseRequestLogPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseRequestLogPage> ReadDrainAsync(string projectId, string databaseId, string after)
        {
            var answer = await api.SendAsync(Context, "GET", "/v1/usage/projects/" + Uri.EscapeDataString(projectId) + "/databases/" + Uri.EscapeDataString(databaseId) + "/logs" + RestPaths.Query(("after", after)));
            return RestApi.Read(answer, Json.TerraBaseRequestLogPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseQueryShapePage> ListQueryShapesAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/query-shapes");
            return RestApi.Read(answer, Json.TerraBaseQueryShapePage);
        }
    }
}
