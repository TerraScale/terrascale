using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>A completed backup.</summary>
/// <param name="BackupId">Backup identifier (sortable by creation time).</param>
/// <param name="Database">Source database and its tenant.</param>
/// <param name="CreatedAt">Completion time.</param>
/// <param name="Bytes">Stored size.</param>
public sealed record BackupInfo(Ulid BackupId, TenantContext Database, DateTimeOffset CreatedAt, long Bytes);

/// <summary>Consistent database backups to the object tier, and restore. Not implemented yet.</summary>
public interface IBackupService
{
    /// <summary>Takes a consistent backup of <paramref name="database"/>.</summary>
    ValueTask<BackupInfo> BackupAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Restores a backup of <paramref name="source"/> into <paramref name="target"/>, which must not exist and must belong
    /// to the same organization: a backup id alone never reaches another tenant's backups.
    /// </summary>
    ValueTask RestoreAsync(TenantContext source, Ulid backupId, TenantContext target, CancellationToken cancellationToken);
}
