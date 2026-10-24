using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

// Outside project state: deleting authorization must not discard the sealed credentials owed to a purge.
internal static class BackupDestinationHistory
{
    internal static string Collection(string projectId) => $"backup_destination_history/{projectId}";

    internal static ValueTask SaveAsync(IStorageTransaction transaction, string projectId, ProjectBackupDestinationRecord record, CancellationToken token) =>
        PutAsync(transaction, Collection(projectId), record.Revision, record, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, token);

    internal static async ValueTask CaptureAsync(IStorageTransaction transaction, TenantContext database, CancellationToken token)
    {
        if (await GetAsync(transaction, ProjectBackupDestinations.Collection(database.ProjectId!), "current",
            TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, token).ConfigureAwait(false) is { } current)
        {
            await SaveAsync(transaction, database.ProjectId!, current, token).ConfigureAwait(false);
        }
    }
}
