using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal static class DatabaseTransactions
{
    extension(IStorageEngine engine)
    {
        internal ValueTask<IStorageTransaction?> BeginTransactionAsync(TenantContext database, bool write, CancellationToken cancellationToken) =>
            engine.BeginAsync(database, store => store.BeginTransactionAsync(write, cancellationToken), cancellationToken);

        internal ValueTask<IStorageTransaction?> BeginPromotionTransactionAsync(TenantContext database, CancellationToken cancellationToken) =>
            engine.BeginAsync(database, store => store.BeginPromotionTransactionAsync(cancellationToken), cancellationToken);

        private async ValueTask<IStorageTransaction?> BeginAsync(
            TenantContext database, Func<IDatabaseStore, ValueTask<IStorageTransaction>> begin, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
                {
                    return null;
                }

                try
                {
                    return await begin(store).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (attempt == 0)
                {
                }
            }
        }
    }
}
