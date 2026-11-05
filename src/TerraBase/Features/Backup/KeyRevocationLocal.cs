using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

// A native replica has its own node-wrapped page key. Envelope eviction alone cannot destroy it.
internal sealed class KeyRevocationLocal(KeyCustody custody, Lazy<EngineManagementService> management, RegionReplication replicas,
    ContinuousBackup backup, Shapes.ShapeMirror shapes)
{
    internal async Task<bool> RevokeAsync(TenantContext database, CancellationToken token)
    {
        var scope = BackupLayout.KeyScope(database);
        if (await ShredRecords.ReadAsync(custody.Store, scope, token).ConfigureAwait(false) is not { } record)
        {
            return false;
        }
        custody.Revocations.Forget(scope);
        if (!await management.Value.ObserveShreddedAsync(database, record.ShreddedAt, token).ConfigureAwait(false))
        {
            return false;
        }
        await replicas.DeleteShreddedAsync(database, token).ConfigureAwait(false);
        // Fence and unlink the engine copy before releasing the tap: snapshot ownership
        // can still be catalogued, so discovery must not be able to reopen its file.
        _ = await management.Value.EraseShreddedAsync(database, token).ConfigureAwait(false);
        await backup.RetireDeletedAsync(database, token).ConfigureAwait(false);
        await shapes.RetireDeletedAsync(database, token).ConfigureAwait(false);
        return await management.Value.EraseShreddedAsync(database, token).ConfigureAwait(false);
    }
}
