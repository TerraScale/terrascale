using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Backups;

[McpServerToolType]
public sealed class BackupMcpTools(IHttpContextAccessor http, BackupService backups)
{
    [McpServerTool(Name = "list_backups", Title = "List backups", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a database's backups and its backup policy.")]
    public Task<BackupListing> ListBackups(string databaseId, string? organizationId = null, string? projectId = null) =>
        backups.ListAsync(Scope(organizationId, projectId), databaseId, Cancellation);

    [McpServerTool(Name = "create_backup", Title = "Create backup", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Take an on-demand backup of a database's documents, schemas, and indexes. Requires an owner or admin role.")]
    public Task<BackupView> CreateBackup(string databaseId, string? organizationId = null, string? projectId = null) =>
        backups.CreateAsync(Scope(organizationId, projectId), databaseId, Cancellation);

    [McpServerTool(Name = "set_backup_policy", Title = "Set backup policy", Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Save a database's full-backup schedule and retention. Incremental schedules are unavailable. Requires an owner or admin role.")]
    public Task<BackupPolicyView> SetBackupPolicy(string databaseId, string fullBackupCron, string incrementalBackupCron, int retentionDays, bool disabled = false,
        string? organizationId = null, string? projectId = null) =>
        backups.SetPolicyAsync(Scope(organizationId, projectId), databaseId, fullBackupCron, incrementalBackupCron, retentionDays, disabled, Cancellation);

    [McpServerTool(Name = "restore_backup", Title = "Restore backup", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Restore a database backup into a new database; the source database is unchanged. Requires an owner or admin role and provisioning rights.")]
    public Task<BackupRestoreResult> RestoreBackup(string databaseId, string name = "", string backupId = "", string? organizationId = null, string? projectId = null) =>
        backups.RestoreAsync(Scope(organizationId, projectId), databaseId, backupId, name, Cancellation);

    private AccountContext Scope(string? organizationId, string? projectId) => McpScope.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}
