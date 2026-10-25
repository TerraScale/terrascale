using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IOrphanDatabaseFiles
{
    ValueTask<IAsyncDisposable> ProtectRestoreAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(TenantContext database, Func<IStorageTransaction, CancellationToken, ValueTask<bool>> authorize, CancellationToken cancellationToken);

    ValueTask<bool?> IsBusyAsync(TenantContext database, CancellationToken cancellationToken);
}
