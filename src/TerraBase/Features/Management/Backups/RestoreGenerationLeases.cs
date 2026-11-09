using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;

namespace TerraBase.Features.Management.Backups;

internal static class RestoreGenerationLeases
{
    public static async ValueTask<HashSet<Ulid>> ActiveAsync(
        IStorageTransaction transaction, string organizationId, string databaseId, Ulid? generation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        HashSet<Ulid> active = [];
        var leases = await SystemCatalog.ListAllAsync(
            transaction, BackupCatalog.GenerationLeases(organizationId), ManagementJsonContext.Default.RestoreGenerationLease, cancellationToken).ConfigureAwait(false);
        foreach (var lease in leases)
        {
            if (lease.DatabaseId != databaseId || generation is { } only && lease.Generation != only || lease.Until <= now)
            {
                continue;
            }

            if (await SystemCatalog.GetAsync(transaction, RestoreCatalog.Kind, lease.OperationId, ManagementJsonContext.Default.RestoreOperationRecord, cancellationToken)
                .ConfigureAwait(false) is { State: RestoreOperationState.Running })
            {
                _ = active.Add(lease.Generation);
            }
        }

        return active;
    }
}
