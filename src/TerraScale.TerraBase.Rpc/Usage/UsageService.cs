using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Usage;

namespace TerraScale.TerraBase.Rpc.Usage
{
    /// <summary>Serves usage RPCs through the matching REST routes.</summary>
    public sealed class UsageService(RestApi api) : ServiceBase<IUsageService>, IUsageService
    {
        /// <inheritdoc />
        public async UnaryResult<TerraBaseProjectUsage> GetProjectUsageAsync(string projectId, string? granularity, DateTimeOffset? from, DateTimeOffset? until)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Project(projectId) + "/usage" + RestPaths.Query(("granularity", granularity), ("from", from?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("to", until?.ToString("O", System.Globalization.CultureInfo.InvariantCulture))));
            return RestApi.Read(answer, UsageJson.Default.TerraBaseProjectUsage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseDatabaseUsage> GetDatabaseUsageAsync(string projectId, string database, string? granularity, DateTimeOffset? from, DateTimeOffset? until)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/usage" + RestPaths.Query(("granularity", granularity), ("from", from?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("to", until?.ToString("O", System.Globalization.CultureInfo.InvariantCulture))));
            return RestApi.Read(answer, UsageJson.Default.TerraBaseDatabaseUsage);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> GetAnalyticsAsync(string projectId, string range, string? database, string? region)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Project(projectId) + "/analytics" + RestPaths.Query(("range", range), ("database", database), ("region", region)));
            return RestApi.Read(answer, UsageJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> GetReplicationAsync(string projectId, string range, string? database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Project(projectId) + "/replication" + RestPaths.Query(("range", range), ("database", database)));
            return RestApi.Read(answer, UsageJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> GetOperatorAnalyticsAsync(string projectId)
        {
            var answer = await api.SendAsync(Context, "GET", "/v1/usage/projects/" + Uri.EscapeDataString(projectId) + "/analytics?range=1h");
            return RestApi.Read(answer, UsageJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> ListAllowancesAsync(string? after)
        {
            var answer = await api.SendAsync(Context, "GET", "/v1/usage/allowances" + RestPaths.Query(("after", after)));
            return RestApi.Read(answer, UsageJson.Default.JsonElement);
        }

    }
}
