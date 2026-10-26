using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Backups;

/// <summary>A consistent snapshot of a managed database.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
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

/// <summary>The body of <c>POST …/point-in-time/restore</c>.</summary>
/// <param name="PointInTime">The time to restore the database as of, inside its restore window.</param>
/// <param name="Destination"><c>platform</c> (default) or <c>customer</c>.</param>
/// <param name="Name">The new database's name; the first free of <c>&lt;source&gt;-restore</c>, <c>-restore-2</c>, … without it.</param>
public sealed record PointInTimeRestoreRequest(DateTimeOffset? PointInTime, string? Name, string? Destination = null);

/// <summary>A page of a database's backups.</summary>
public readonly union ListBackupsResult(ManagementPage<DatabaseBackup>, Unavailable);

/// <summary>A completed backup, or that its database is gone or out of the user's reach.</summary>
public readonly union CreateBackupResult(DatabaseBackup, NotFound, Unavailable);

/// <summary>A database's backup policy.</summary>
public readonly union GetBackupPolicyResult(BackupPolicy, Unavailable);

/// <summary>The saved policy, or why it was not saved.</summary>
public readonly union SetBackupPolicyResult(BackupPolicy, NotFound, Forbidden, Unavailable);

/// <summary>The database a backup was restored into, or why it was not restored; a taken name is a conflict.</summary>
public readonly union RestoreBackupResult(ManagedDatabase, BackupElsewhere, NotFound, Conflict, Forbidden, Validation, QuotaExceeded, Unavailable);

/// <summary>A recovery request that must execute in the named region.</summary>
/// <param name="Region">The region holding the recovery source.</param>
public sealed record BackupElsewhere(string Region);

/// <summary>Backups of managed databases, their policy, and restoring them into new databases.</summary>
public interface IManagedBackupService
{
    /// <summary>Lists the registered backups of a database, oldest first; the cursor is opaque.</summary>
    ValueTask<ListBackupsResult> ListBackupsAsync(string organizationId, string databaseId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Takes a backup of <paramref name="database"/> now.</summary>
    ValueTask<CreateBackupResult> CreateBackupAsync(string userId, string organizationId, TenantContext database, CancellationToken cancellationToken);

    /// <summary>Reads the policy of a database, or the default policy when none was saved.</summary>
    ValueTask<GetBackupPolicyResult> GetPolicyAsync(string databaseId, CancellationToken cancellationToken);

    /// <summary>Saves the policy of a database; the values are validated by the caller.</summary>
    ValueTask<SetBackupPolicyResult> SetPolicyAsync(
        string userId, string organizationId, TenantContext database, string? fullBackupCron, int retentionDays, bool disabled, CancellationToken cancellationToken);

    /// <summary>
    /// Restores a backup into a new database in the same project, named <paramref name="name"/> or, when
    /// <see langword="null"/>, <c>&lt;source&gt;-restore</c>, <c>-restore-2</c>, … whichever is free first.
    /// </summary>
    ValueTask<RestoreBackupResult> RestoreAsync(
        string userId, string organizationId, TenantContext database, string backupId, string? name, CancellationToken cancellationToken);

    /// <summary>
    /// Restores <paramref name="database"/> as of <paramref name="at"/> from continuous backup into a new database in the same
    /// project, named like <see cref="RestoreAsync"/>; validates <paramref name="at"/> against its restore window before downloading.
    /// </summary>
    ValueTask<RestoreBackupResult> RestorePointInTimeAsync(
        string userId, string organizationId, TenantContext database, DateTimeOffset at, string? name, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);

    /// <summary>
    /// One scheduler pass: a page of the storage sweep, retention, then the scheduled backups that are due; none while the
    /// catalog is at a version this build cannot use.
    /// </summary>
    ValueTask RunScheduleAsync(CancellationToken cancellationToken);
}
