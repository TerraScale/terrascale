namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>Replication of the system database between a catalog primary and its followers (docs/decisions/0009).</summary>
internal sealed partial class SqliteStorageEngine : ISystemReplication
{
    public ValueTask<SystemDocumentPage> ReadDocumentsAsync(
        string? afterCollection, string? afterId, int maxDocuments, int maxBytes, CancellationToken cancellationToken) =>
        ReadSystemAsync(transaction => transaction.ReadSystemDocumentsAsync(afterCollection, afterId, maxDocuments, maxBytes, cancellationToken), cancellationToken);

    public ValueTask<SystemGroupPage> ReadGroupAsync(long sequence, int skip, int maxChanges, int maxBytes, CancellationToken cancellationToken) =>
        ReadSystemAsync(transaction => transaction.ReadSystemGroupAsync(sequence, skip, maxChanges, maxBytes, cancellationToken), cancellationToken);

    public ValueTask<JournalPosition> GetHeadAsync(CancellationToken cancellationToken) =>
        ReadSystemAsync(async transaction =>
        {
            var origin = await transaction.GetOriginAsync(cancellationToken).ConfigureAwait(false);
            var sequence = await transaction.GetLastChangeSequenceAsync(cancellationToken).ConfigureAwait(false);
            var digest = await transaction.GetChangeDigestAsync(sequence, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("the system journal is missing its head digest");
            return new JournalPosition(origin, sequence, digest);
        }, cancellationToken);

    public async ValueTask<ISystemReplicaTransaction> BeginReplicaAsync(ReadOnlyMemory<byte>? adopt, CancellationToken cancellationToken)
    {
        var store = (SqliteDatabaseStore)await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (!store.Replica)
        {
            throw new InvalidOperationException("only a catalog follower writes replicated state into its system database");
        }

        var transaction = await store.BeginReplicaAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (adopt is { } origin)
            {
                await transaction.ResetReplicaAsync(origin, cancellationToken).ConfigureAwait(false);
            }

            return new ReplicaTransaction(transaction);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<T> ReadSystemAsync<T>(Func<SqliteStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var store = await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction).ConfigureAwait(false);
        }
    }

    private sealed class ReplicaTransaction(SqliteStorageTransaction inner) : ISystemReplicaTransaction
    {
        public ValueTask ReplaceAsync(DocumentChange change, CancellationToken cancellationToken) => inner.ReplaceAsync(change, cancellationToken);

        public async ValueTask CommitAsync(JournalPosition head, long offset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(head);
            await inner.SetReplicaHeadAsync(head, offset, cancellationToken).ConfigureAwait(false);
            await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
