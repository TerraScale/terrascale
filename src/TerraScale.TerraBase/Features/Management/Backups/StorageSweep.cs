using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

/// <summary>A unit of storage cleanup the sweep owes.</summary>
/// <param name="Kind"><see cref="StorageSweep.Backup"/> or <see cref="StorageSweep.DatabaseBackups"/>.</param>
/// <param name="OrganizationId">The organization (tenant) the storage belonged to.</param>
/// <param name="Id">A backup id, or the id of a deleted database whose backups must expire.</param>
internal sealed record SweepEntry(string Kind, string OrganizationId, string Id)
{
    /// <summary>The entry's key: kind and id, so one queue holds every kind without collisions.</summary>
    public string Key => $"{Kind}:{Id}";
}

/// <summary>
/// The durable storage sweep. The transaction that makes backup storage unreferenced (a backup that expires, fails or
/// is abandoned, or the delete of a database, whose backups then expire) records an entry here; files are deleted after
/// the commit, and an entry is removed only once its files are confirmed gone. A deleted database's own files are
/// cleaned up through the engine's database cleanup intent. Every backup pass works through one page of the queue from a rotating cursor,
/// so entries that keep failing cannot hide the rest. It is a work queue: nothing is addressed or authorized through it.
/// </summary>
internal static class StorageSweep
{
    /// <summary>A backup record in <see cref="BackupState.Expiring"/>, or possibly abandoned, whose snapshot to delete.</summary>
    public const string Backup = "backup";

    /// <summary>The registered backups of a deleted database, expired a page at a time.</summary>
    public const string DatabaseBackups = "database_backups";

    private const string Collection = "storage_sweep";

    public static ValueTask AddAsync(IStorageTransaction transaction, SweepEntry entry, CancellationToken cancellationToken) =>
        PutAsync(transaction, Collection, entry.Key, entry, TerraBaseJsonContext.Default.SweepEntry, cancellationToken);

    public static async ValueTask RemoveAsync(IStorageTransaction transaction, SweepEntry entry, CancellationToken cancellationToken) =>
        await transaction.DeleteAsync(Collection, entry.Key, cancellationToken).ConfigureAwait(false);

    /// <summary>One page of the queue after <paramref name="after"/>, in key order.</summary>
    public static ValueTask<ManagementPage<SweepEntry>> ListAsync(IStorageTransaction transaction, string? after, int limit, CancellationToken cancellationToken) =>
        SystemCatalog.ListAsync(transaction, Collection, after, limit, TerraBaseJsonContext.Default.SweepEntry, entry => entry.Key, cancellationToken);

    /// <summary>
    /// Deletes the files of <paramref name="storage"/> under its tenant's directory; <see langword="true"/> once they are
    /// gone (already gone counts). A failure is logged by id and left for the next pass. Not cancellable: the catalog no
    /// longer names the files.
    /// </summary>
    public static async ValueTask<bool> DeleteFilesAsync(IStorageEngine engine, TenantContext storage, ILogger logger)
    {
        try
        {
            await engine.DeleteDatabaseAsync(storage, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            logger.ZLogWarning(error, $"Deleting the files of {storage.DatabaseId:@Storage} failed; the storage sweep retries on its next pass");
            return false;
        }
    }
}
