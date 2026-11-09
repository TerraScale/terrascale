using TerraScale.TerraBase.Contracts.Backups;
using System.Text.Json.Serialization;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Backups
{
    [McpServerToolType]
    public sealed class BackupMcpTools(IHttpContextAccessor http, BackupService backups, WholeDatabaseRecovery recovery)
    {
        [McpServerTool(Name = "list_backups", Title = "List backups", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a database's backups and its backup policy, one bounded producer page per call.")]
        public async Task<McpBackupList> ListBackups(string databaseId, string? organizationId = null, string? projectId = null,
            [Description(CursorPaging.IssuerDescription)] int limit = 100, string after = "")
        {
            var context = Scope(organizationId, projectId);
            var slice = await backups.ListPageAsync(context, databaseId, after.Length == 0 ? null : after, limit, Cancellation);
            return new(slice.Policy, slice.Items, slice.NextCursor, slice.HasMore, limit);
        }

        [McpServerTool(Name = "create_backup", Title = "Create backup", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Take an on-demand backup of a database. Requires an owner or admin role.")]
        public Task<BackupView> CreateBackup(string databaseId, string? organizationId = null, string? projectId = null)
        {
            return backups.CreateAsync(Scope(organizationId, projectId), databaseId, Cancellation);
        }

        [McpServerTool(Name = "set_backup_policy", Title = "Set backup policy", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Save a database's full-backup schedule and retention. Shorter retention can expire existing snapshots; clearing the schedule reduces future recovery coverage. Incremental schedules are unavailable. Requires an owner or admin role.")]
        public Task<BackupPolicyView> SetBackupPolicy(string databaseId,
            [Description("Five-field full-backup cron, up to 256 characters; empty clears the schedule.")] string fullBackupCron,
            [Description("Snapshot retention in days, from 1 to 35.")] int retentionDays,
            [Description("Omit or pass an empty string. Incremental schedules are unavailable.")] string incrementalBackupCron = "",
            string? organizationId = null, string? projectId = null)
        {
            return backups.SetPolicyAsync(Scope(organizationId, projectId), databaseId, fullBackupCron, incrementalBackupCron, retentionDays, Cancellation);
        }

        [McpServerTool(Name = "restore_backup", Title = "Restore backup", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Start restoring a database backup into a new database and return the running operation at once instead of waiting for the copy; poll get_restore_status for the completed database. The source database is unchanged. Requires an owner or admin role and provisioning rights.")]
        public async Task<McpRestoreStart> RestoreBackup(string databaseId, string name = "", string backupId = "", string? organizationId = null, string? projectId = null)
        {
            var started = await backups.StartRestoreAsync(Scope(organizationId, projectId), databaseId, backupId, name, Cancellation);
            return new(started.OperationId, started.Status, name.Trim().Length == 0 ? null : name.Trim());
        }

        [McpServerTool(Name = "get_restore_status", Title = "Get restore status", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read a restore operation started by restore_backup: its status, the target database identity and name once completed, or the error code and message once failed. Requires read access to the source database.")]
        public Task<RestoreProgress> GetRestoreStatus(string databaseId, string operationId, string? organizationId = null, string? projectId = null)
        {
            return backups.RestoreStatusAsync(Scope(organizationId, projectId), databaseId, operationId, Cancellation);
        }

        [McpServerTool(Name = "get_point_in_time_window", Title = "Read retained PITR window", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read actual retained UTC bounds from platform or customer backup history, with database allowance. Destination selects the source bucket, not target database configuration. Requires management access.")]
        public Task<WholeDatabaseWindow> GetPointInTimeWindow(string databaseId, string destination = "platform", string? organizationId = null, string? projectId = null)
        {
            return recovery.WindowAsync(Scope(organizationId, projectId), databaseId, destination, Cancellation);
        }

        [McpServerTool(Name = "plan_point_in_time_recovery", Title = "Preview advisory PITR metadata", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read advisory metadata only for a retained point: an ISO 8601 timestamp ending Z or +00:00, such as a bound get_point_in_time_window returns. Does not verify replay bytes, reserve history or guarantee restore. Requires management access.")]
        public Task<TerraBasePointInTimePlan> PlanPointInTimeRecovery(string databaseId, string pointInTime, string destination = "platform", string? organizationId = null, string? projectId = null)
        {
            return recovery.PlanAsync(Scope(organizationId, projectId), databaseId, destination, pointInTime, Cancellation);
        }

        [McpServerTool(Name = "restore_database_point_in_time", Title = "Restore PITR as a new database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Restore retained UTC history into a NEW named database in the same project, leaving the source unchanged. Obtain explicit user consent for the new name, time and source bucket; confirmName must equal name. Consumes one database slot and normal storage quota. Requires management and provisioning access. If interrupted, inspect the database list before retrying.")]
        public Task<BackupRestoreResult> RestoreDatabasePointInTime(string databaseId, string pointInTime, string name, string confirmName, string destination = "platform", string? organizationId = null, string? projectId = null)
        {
            return recovery.RestoreAsync(Scope(organizationId, projectId), databaseId, destination, pointInTime, name, confirmName, Cancellation);
        }

        private AccountContext Scope(string? organizationId, string? projectId)
        {
            return DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        }

        private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
    }

    public sealed record McpBackupList([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] BackupPolicyView? Policy, IReadOnlyList<BackupView> Backups, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
    public sealed record McpRestoreStart(string OperationId, string Status, string? Name);
}
