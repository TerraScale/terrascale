using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IRecoverySnapshotReader
{
    public async ValueTask<IRecoverySnapshot> OpenAsync(string file, TenantContext source, CancellationToken cancellationToken)
    {
        // No Locate, bootstrap, _stores registration, replication callbacks, or tenant file publication.
        var store = new TursoDatabaseStore(source.DatabaseId!, file, Pool, Io, _pragmas, source, Time, committed: null,
            schemaMemory: _schemaMemory, storageReads: _storageReads, encryption: Encryption)
        {
            QueryBudget = QueryBudget,
        };
        Io.Admit();
        try
        {
            var transaction = await Io.WithAdmissionAsync(() => store.BeginTransactionAsync(write: false, cancellationToken)).ConfigureAwait(false);
            return new RecoverySnapshot(transaction, store, Io);
        }
        catch
        {
            try
            {
                await Io.RunAsync(store.Dispose, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Io.Release();
            }
            throw;
        }
    }

    private sealed class RecoverySnapshot(IStorageTransaction transaction, TursoDatabaseStore store, TursoIoPool io) : IRecoverySnapshot
    {
        public IStorageTransaction Transaction => transaction;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await io.RunAsync(store.Dispose, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    io.Release();
                }
            }
        }
    }
}
