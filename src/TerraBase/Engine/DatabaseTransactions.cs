using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal static class DatabaseTransactions
{
    extension(IStorageEngine engine)
    {
        internal async ValueTask<IStorageTransaction?> BeginTransactionAsync(
            TenantContext database, bool write, CancellationToken cancellationToken)
        {
            // A closed store throws ObjectDisposedException; opening again finds the database deleted, or a newer store of a
            // database with the same id. A stopped engine throws from the open instead, so it is not retried.
            for (var attempt = 0; ; attempt++)
            {
                if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
                {
                    return null;
                }

                try
                {
                    return await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (attempt == 0)
                {
                }
            }
        }
    }
}
