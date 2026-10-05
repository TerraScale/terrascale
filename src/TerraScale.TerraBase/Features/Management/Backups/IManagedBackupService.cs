using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management.Backups;

/// <summary>A consistent snapshot of a managed database.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="DatabaseId">The database it was taken of.</param>
/// <param name="Status">Always <c>ready</c>: a backup is listed only once registered (complete).</param>
/// <param name="Trigger"><c>manual</c> or <c>scheduled</c>.</param>
/// <param name="SizeBytes">Bytes the snapshot occupies on disk.</param>
/// <param name="CreatedAt">When the backup completed; retention counts from here.</param>
public sealed record DatabaseBackup(string Id, string DatabaseId, string Status, string Trigger, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>When a database is backed up and how long its backups are kept.</summary>
/// <param name="FullBackupCron">Standard 5-field cron expression (UTC), or <see langword="null"/> for no scheduled backups.</param>
/// <param name="RetentionDays">Backups older than this many days are deleted (1 to 35).</param>
/// <param name="Disabled">Whether scheduled backups are paused.</param>
/// <param name="UpdatedAt">When the policy was last saved; <see langword="null"/> for the default policy.</param>
public sealed record BackupPolicy(string? FullBackupCron, int RetentionDays, bool Disabled, DateTimeOffset? UpdatedAt);

/// <summary>The body of <c>PUT …/backup-policy</c>.</summary>
/// <param name="FullBackupCron">Standard 5-field cron expression (UTC); null or empty for no scheduled backups.</param>
/// <param name="RetentionDays">1 to 35.</param>
/// <param name="Disabled">Whether scheduled backups are paused; defaults to false.</param>
public sealed record BackupPolicyRequest(string? FullBackupCron, int? RetentionDays, bool? Disabled);

/// <summary>Backups of managed databases, their policy, and restoring them into new databases.</summary>
public interface IManagedBackupService
{
    /// <summary>Lists the registered backups of a database, oldest first; the cursor is opaque.</summary>
    ValueTask<ManagementPage<DatabaseBackup>> ListBackupsAsync(string organizationId, string databaseId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Takes a backup of <paramref name="database"/> now.</summary>
    ValueTask<ManagementResult<DatabaseBackup>> CreateBackupAsync(string userId, string organizationId, TenantContext database, CancellationToken cancellationToken);

    /// <summary>Reads the policy of a database, or the default policy when none was saved.</summary>
    ValueTask<BackupPolicy> GetPolicyAsync(string databaseId, CancellationToken cancellationToken);

    /// <summary>Saves the policy of a database; the values are validated by the caller.</summary>
    ValueTask<ManagementResult<BackupPolicy>> SetPolicyAsync(
        string userId, string organizationId, TenantContext database, string? fullBackupCron, int retentionDays, bool disabled, CancellationToken cancellationToken);

    /// <summary>
    /// Restores a backup into a new database in the same project, named <paramref name="name"/> or, when
    /// <see langword="null"/>, <c>&lt;source&gt;-restore</c>, <c>-restore-2</c>, … whichever is free first.
    /// </summary>
    ValueTask<ManagementResult<ManagedDatabase>> RestoreAsync(
        string userId, string organizationId, TenantContext database, string backupId, string? name, CancellationToken cancellationToken);

    /// <summary>One scheduler pass: a page of the storage sweep, retention, then the scheduled backups that are due.</summary>
    ValueTask RunScheduleAsync(CancellationToken cancellationToken);
}
