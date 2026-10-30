using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Backups;

namespace TerraScale.TerraBase.Rpc.Backups
{
    /// <summary>Serves backups RPCs through the matching REST routes.</summary>
    public sealed class BackupsService(RestApi api) : ServiceBase<IBackupsService>, IBackupsService
    {
        /// <inheritdoc />
        public async UnaryResult<JsonElement> ListAsync(string projectId, string database, string? after)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/backups" + RestPaths.Query(("limit", "100"), ("after", after)));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> CreateAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/backups");
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> GetPolicyAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/backup-policy");
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> SetPolicyAsync(string projectId, string database, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "PUT", RestPaths.Database(projectId, database) + "/backup-policy", RestApi.Json(request, BackupsJson.Default.JsonElement));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> GetExportScheduleAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/export-schedule");
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> SetExportScheduleAsync(string projectId, string database, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "PUT", RestPaths.Database(projectId, database) + "/export-schedule", RestApi.Json(request, BackupsJson.Default.JsonElement));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> PreviewRecoveryAsync(string projectId, string database, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/point-in-time/recovery-preview", RestApi.Json(request, BackupsJson.Default.JsonElement));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> RecoverAsync(string projectId, string database, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/point-in-time/recover", RestApi.Json(request, BackupsJson.Default.JsonElement));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> RestoreBackupAsync(string projectId, string database, string backupId, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/backups/" + Uri.EscapeDataString(backupId) + "/restore", RestApi.Json(request, BackupsJson.Default.JsonElement));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> GetDestinationAsync(string projectId)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Project(projectId) + "/backup-destination");
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> SetDestinationAsync(string projectId, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "PUT", RestPaths.Project(projectId) + "/backup-destination", RestApi.Json(request, BackupsJson.Default.JsonElement));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement?> RemoveDestinationAsync(string projectId)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Project(projectId) + "/backup-destination");
            RestApi.Ensure(answer);
            return answer.Body.IsEmpty ? null : RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseRestoreWindow> GetRestoreWindowAsync(string projectId, string database, string? destination)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/point-in-time" + RestPaths.Query(("destination", destination)));
            return RestApi.Read(answer, BackupsJson.Default.TerraBaseRestoreWindow);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBasePointInTimePlan> PlanPointInTimeAsync(string projectId, string database, TerraBasePointInTimeRequest request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/point-in-time/plan", RestApi.Json(request, BackupsJson.Default.TerraBasePointInTimeRequest));
            return RestApi.Read(answer, BackupsJson.Default.TerraBasePointInTimePlan);
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> RestorePointInTimeAsync(string projectId, string database, TerraBasePointInTimeRequest request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/point-in-time/restore", RestApi.Json(request, BackupsJson.Default.TerraBasePointInTimeRequest));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

    }
}
