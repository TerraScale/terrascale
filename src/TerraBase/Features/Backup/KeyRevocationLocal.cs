using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal sealed class KeyRevocationLocal(KeyCustody custody, IShreddedDatabases management, RegionReplication replicas,
    ContinuousBackup backup, Shapes.ShapeMirror shapes)
{
    internal async Task<bool> RevokeAsync(TenantContext database, CancellationToken token)
    {
        if (ShredGuard.Refusal(database) is not null)
        {
            return false;
        }

        var scope = BackupLayout.KeyScope(database);
        if (await ShredRecords.ReadAsync(custody.Store, scope, token).ConfigureAwait(false) is not { } record)
        {
            return false;
        }
        return await management.ObserveShreddedAsync(database, record.ShreddedAt, token).ConfigureAwait(false) switch
        {
            true => await EraseAsync(database, token).ConfigureAwait(false),
            false => false,
            Forbidden => false,
        };
    }

    private async Task<bool> EraseAsync(TenantContext database, CancellationToken token)
    {
        custody.Revocations.Forget(BackupLayout.KeyScope(database));
        await replicas.DeleteShreddedAsync(database, token).ConfigureAwait(false);
        _ = await management.EraseShreddedAsync(database, token).ConfigureAwait(false);
        await backup.RetireDeletedAsync(database, token).ConfigureAwait(false);
        await shapes.RetireDeletedAsync(database, token).ConfigureAwait(false);
        return await management.EraseShreddedAsync(database, token).ConfigureAwait(false);
    }
}
