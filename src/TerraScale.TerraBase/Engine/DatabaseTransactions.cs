using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>Opens a database and begins a transaction on it as one step.</summary>
internal static class DatabaseTransactions
{
    /// <summary>
    /// Begins a transaction on <paramref name="database"/>, or returns <see langword="null"/> when it does not exist, also when
    /// a delete closed it between the open and the begin.
    /// </summary>
    public static async ValueTask<IStorageTransaction?> BeginTransactionAsync(
        this IStorageEngine engine, TenantContext database, bool write, CancellationToken cancellationToken)
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
