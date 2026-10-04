using TerraScale.TerraBase.Engine;

namespace TerraScale.TerraBase.Features.Backup;

/// <summary>A completed backup.</summary>
/// <param name="BackupId">Backup identifier (sortable by creation time).</param>
/// <param name="Database">Source database.</param>
/// <param name="CreatedAt">Completion time.</param>
/// <param name="Bytes">Stored size.</param>
public sealed record BackupInfo(Ulid BackupId, DatabaseName Database, DateTimeOffset CreatedAt, long Bytes);

/// <summary>Consistent database backups to the object tier, and restore. Not implemented yet.</summary>
public interface IBackupService
{
    /// <summary>Takes a consistent backup of <paramref name="database"/>.</summary>
    ValueTask<BackupInfo> BackupAsync(DatabaseName database, CancellationToken cancellationToken);

    /// <summary>Restores a backup into <paramref name="target"/>, which must not exist.</summary>
    ValueTask RestoreAsync(Ulid backupId, DatabaseName target, CancellationToken cancellationToken);
}
