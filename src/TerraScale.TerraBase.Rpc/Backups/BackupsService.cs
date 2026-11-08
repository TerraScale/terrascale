using System.Globalization;
using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.AspNetCore.Http;
using TerraScale.TerraBase.Contracts.Backups;

namespace TerraScale.TerraBase.Rpc.Backups
{
    /// <summary>Serves backups RPCs through the matching REST routes.</summary>
    public sealed class BackupsService(RestApi api) : ServiceBase<IBackupsService>, IBackupsService
    {
        /// <inheritdoc />
        public async UnaryResult<JsonElement> ListAsync(string projectId, string database, string? after, int limit)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/backups" + RestPaths.Query(("limit", limit.ToString(CultureInfo.InvariantCulture)), ("after", after)));
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
        public async UnaryResult<JsonElement> RestoreBackupAsync(string projectId, string database, string backupId, JsonElement request)
        {
            return await CompletedAsync(projectId, database, await StartRestoreAsync(projectId, database, backupId, request));
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> StartRestoreAsync(string projectId, string database, string backupId, JsonElement request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/backups/" + Uri.EscapeDataString(backupId) + "/restore", RestApi.Json(request, BackupsJson.Default.JsonElement));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

        private static readonly TimeSpan PollDeadline = TimeSpan.FromMinutes(10);

        private async Task<JsonElement> CompletedAsync(string projectId, string database, JsonElement accepted)
        {
            var operationId = accepted.GetProperty("operationId").GetString()!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Context.CallContext.CancellationToken);
            deadline.CancelAfter(PollDeadline);
            while (true)
            {
                var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/restores/" + Uri.EscapeDataString(operationId));
                if (answer.Status == StatusCodes.Status404NotFound)
                {
                    throw RestApi.Fault(answer);
                }

                var operation = RestApi.Read(answer, BackupsJson.Default.JsonElement);
                var status = operation.GetProperty("status").GetString();
                if (status == "completed")
                {
                    var target = operation.GetProperty("database").GetString()!;
                    return RestApi.Read(await api.SendAsync(Context, "GET", RestPaths.Database(projectId, target)), BackupsJson.Default.JsonElement);
                }

                if (status == "failed")
                {
                    throw RestApi.Fault(new RestAnswer(400, new HeaderDictionary(), RestoreError(operation)));
                }

                if (deadline.IsCancellationRequested)
                {
                    throw RestApi.Fault(new RestAnswer(504, new HeaderDictionary(),
                        "{\"error\":{\"code\":\"restore_timeout\",\"message\":\"the restore did not finish in time\"}}"u8.ToArray()));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
            }
        }

        private static byte[] RestoreError(JsonElement operation)
        {
            var code = operation.TryGetProperty("errorCode", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            var message = operation.TryGetProperty("errorMessage", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            var body = new MemoryStream();
            using (var writer = new Utf8JsonWriter(body))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("error");
                writer.WriteString("code", code ?? "restore_failed");
                writer.WriteString("message", message ?? "the restore could not be completed");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return body.ToArray();
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> GetRestoreOperationAsync(string projectId, string database, string operationId)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/restores/" + Uri.EscapeDataString(operationId));
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
            return await CompletedAsync(projectId, database, RestApi.Read(answer, BackupsJson.Default.JsonElement));
        }

        /// <inheritdoc />
        public async UnaryResult<JsonElement> RewindPointInTimeAsync(string projectId, string database, TerraBasePointInTimeRequest request)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/point-in-time/rewind", RestApi.Json(request, BackupsJson.Default.TerraBasePointInTimeRequest));
            return RestApi.Read(answer, BackupsJson.Default.JsonElement);
        }

    }
}
