using System.Text.Json.Serialization;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Backups;

[McpServerToolType]
public sealed class BackupMcpTools(IHttpContextAccessor http, BackupService backups)
{
    [McpServerTool(Name = "list_backups", Title = "List backups", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a database's backups and its backup policy.")]
    public async Task<McpBackupList> ListBackups(string databaseId, string? organizationId = null, string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "")
    {
        var context = Scope(organizationId, projectId);
        var listing = await backups.ListAsync(context, databaseId, Cancellation);
        var page = CursorPaging.Page(listing.Backups, backup => backup.Id, CursorPaging.Scope(context, "backups", databaseId), limit, after);
        return new(listing.Policy, page.Items, page.NextCursor, page.HasMore, page.Limit);
    }

    [McpServerTool(Name = "create_backup", Title = "Create backup", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Take an on-demand backup of a database's documents, schemas, and indexes. Requires an owner or admin role.")]
    public Task<BackupView> CreateBackup(string databaseId, string? organizationId = null, string? projectId = null) =>
        backups.CreateAsync(Scope(organizationId, projectId), databaseId, Cancellation);

    [McpServerTool(Name = "set_backup_policy", Title = "Set backup policy", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Save a database's full-backup schedule and retention. Shorter retention can expire existing snapshots; clearing or disabling the schedule reduces future recovery coverage. Incremental schedules are unavailable. Requires an owner or admin role.")]
    public Task<BackupPolicyView> SetBackupPolicy(string databaseId,
        [Description("Five-field full-backup cron, up to 256 characters; empty clears the schedule.")] string fullBackupCron,
        [Description("Snapshot retention in days, from 1 to 35.")] int retentionDays,
        [Description("Omit or pass an empty string. Incremental schedules are unavailable.")] string incrementalBackupCron = "", bool disabled = false,
        string? organizationId = null, string? projectId = null) =>
        backups.SetPolicyAsync(Scope(organizationId, projectId), databaseId, fullBackupCron, incrementalBackupCron, retentionDays, disabled, Cancellation);

    [McpServerTool(Name = "restore_backup", Title = "Restore backup", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Restore a database backup into a new database; the source database is unchanged. Requires an owner or admin role and provisioning rights.")]
    public Task<BackupRestoreResult> RestoreBackup(string databaseId, string name = "", string backupId = "", string? organizationId = null, string? projectId = null) =>
        backups.RestoreAsync(Scope(organizationId, projectId), databaseId, backupId, name, Cancellation);

    [McpServerTool(Name = "get_export_schedule", Title = "Get export schedule", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read the project's own backup bucket (endpoint and bucket; credentials are never returned) and a database's scheduled NDJSON export to it, with the last run.")]
    public Task<BackupExportView> GetExportSchedule(string databaseId, string? organizationId = null, string? projectId = null) =>
        backups.ExportAsync(Scope(organizationId, projectId), databaseId, Cancellation);

    [McpServerTool(Name = "set_export_schedule", Title = "Set export schedule", Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Schedule a database's NDJSON export to the project's bucket: a 5-field UTC cron such as 0 3 * * * (daily) or 0 3 * * 0 (weekly), or empty to turn it off. The bucket is saved in the dashboard first. Requires an owner or admin role.")]
    public Task<ExportScheduleView> SetExportSchedule(string databaseId, string cron, string? organizationId = null, string? projectId = null) =>
        backups.SetExportScheduleAsync(Scope(organizationId, projectId), databaseId, cron, Cancellation);

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

public sealed record McpBackupList([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] BackupPolicyView? Policy, IReadOnlyList<BackupView> Backups, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
