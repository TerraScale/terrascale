using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

internal sealed record SweepEntry(string Kind, string OrganizationId, string Id)
{
    public string Key => $"{Kind}:{Id}";
}

internal static class StorageSweep
{
    public const string Backup = "backup";

    public const string DatabaseBackups = "database_backups";

    private const string Kind = "storage_sweep";

    public static ValueTask AddAsync(IStorageTransaction transaction, SweepEntry entry, CancellationToken cancellationToken) =>
        PutAsync(transaction, Kind, entry.Key, entry, TerraBaseJsonContext.Default.SweepEntry, cancellationToken);

    public static async ValueTask RemoveAsync(IStorageTransaction transaction, SweepEntry entry, CancellationToken cancellationToken) =>
        await transaction.DeleteAsync(Kind, entry.Key, cancellationToken).ConfigureAwait(false);

    public static ValueTask<ManagementPage<SweepEntry>> ListAsync(IStorageTransaction transaction, string? after, int limit, CancellationToken cancellationToken) =>
        SystemCatalog.ListAsync(transaction, Kind, after, limit, TerraBaseJsonContext.Default.SweepEntry, entry => entry.Key, cancellationToken);

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
