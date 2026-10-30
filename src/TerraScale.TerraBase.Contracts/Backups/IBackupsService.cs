using System.Text.Json;
using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Backups
{
    /// <summary>TerraBase backups APIs, with the REST pipeline authorization and placement rules.</summary>
    public interface IBackupsService : IService<IBackupsService>
    {
        /// <summary><c>GET /v1/projects/{project}/databases/{database}/backups</c>.</summary>
        UnaryResult<JsonElement> ListAsync(string projectId, string database, string? after);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/backups</c>.</summary>
        UnaryResult<JsonElement> CreateAsync(string projectId, string database);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/backup-policy</c>.</summary>
        UnaryResult<JsonElement> GetPolicyAsync(string projectId, string database);

        /// <summary><c>PUT /v1/projects/{project}/databases/{database}/backup-policy</c>.</summary>
        UnaryResult<JsonElement> SetPolicyAsync(string projectId, string database, JsonElement request);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/export-schedule</c>.</summary>
        UnaryResult<JsonElement> GetExportScheduleAsync(string projectId, string database);

        /// <summary><c>PUT /v1/projects/{project}/databases/{database}/export-schedule</c>.</summary>
        UnaryResult<JsonElement> SetExportScheduleAsync(string projectId, string database, JsonElement request);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/point-in-time/recovery-preview</c>.</summary>
        UnaryResult<JsonElement> PreviewRecoveryAsync(string projectId, string database, JsonElement request);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/point-in-time/recover</c>.</summary>
        UnaryResult<JsonElement> RecoverAsync(string projectId, string database, JsonElement request);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/backups/{backup}/restore</c>.</summary>
        UnaryResult<JsonElement> RestoreBackupAsync(string projectId, string database, string backupId, JsonElement request);

        /// <summary><c>GET /v1/projects/{project}/backup-destination</c>.</summary>
        UnaryResult<JsonElement> GetDestinationAsync(string projectId);

        /// <summary><c>PUT /v1/projects/{project}/backup-destination</c>.</summary>
        UnaryResult<JsonElement> SetDestinationAsync(string projectId, JsonElement request);

        /// <summary><c>DELETE /v1/projects/{project}/backup-destination</c>.</summary>
        UnaryResult<JsonElement?> RemoveDestinationAsync(string projectId);

        /// <summary><c>GET /v1/projects/{project}/databases/{database}/point-in-time</c>.</summary>
        UnaryResult<TerraBaseRestoreWindow> GetRestoreWindowAsync(string projectId, string database, string? destination);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/point-in-time/plan</c>.</summary>
        UnaryResult<TerraBasePointInTimePlan> PlanPointInTimeAsync(string projectId, string database, TerraBasePointInTimeRequest request);

        /// <summary><c>POST /v1/projects/{project}/databases/{database}/point-in-time/restore</c>.</summary>
        UnaryResult<JsonElement> RestorePointInTimeAsync(string projectId, string database, TerraBasePointInTimeRequest request);

    }
}
