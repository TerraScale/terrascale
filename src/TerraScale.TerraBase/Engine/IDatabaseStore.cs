using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>One open database. The engine owns its lifetime.</summary>
public interface IDatabaseStore
{
    /// <summary>The organization, project and database identity, with the plan normalized to hobby; null only for TerraBase's own database.</summary>
    TenantContext? Tenant { get; }

    /// <summary>Whether deletion or engine shutdown ended this store's lifetime.</summary>
    bool IsClosed { get; }

    /// <summary>Persisted identity shared by database-scoped handles and versions; stable across reopen, fresh for each new database or copy.</summary>
    Guid Incarnation { get; }

    /// <summary>
    /// Starts a transaction. Write transactions are serialized per database; read transactions see
    /// a consistent snapshot and refuse all mutations (<see cref="InvalidOperationException"/>). Dispose without
    /// <see cref="IStorageTransaction.CommitAsync"/> to roll back.
    /// </summary>
    ValueTask<IStorageTransaction> BeginTransactionAsync(bool write, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a write transaction whose commit is synced to disk when <see cref="IStorageTransaction.CommitAsync"/> returns, so
    /// it survives a power loss; other commits are synced at the next checkpoint (docs/decisions/0003, 0010).
    /// </summary>
    ValueTask<IStorageTransaction> BeginDurableTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> in a write transaction it may share with other writes queued at the same time (group
    /// commit), and answers once that transaction committed, with its commit offset. When <paramref name="keep"/>
    /// refuses the work's result, or the work throws, only the work's own changes roll back and the offset is 0.
    /// </summary>
    async ValueTask<(T Result, long Offset)> WriteAsync<T>(
        Func<IStorageTransaction, CancellationToken, ValueTask<T>> work, Func<T, bool> keep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(keep);
        var transaction = await BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await work(transaction, cancellationToken).ConfigureAwait(false);
            return keep(result) ? (result, await transaction.CommitAsync(cancellationToken).ConfigureAwait(false)) : (result, 0);
        }
    }

    /// <summary>Whether the database has collection <paramref name="name"/>, without a storage read when the engine keeps them in memory.</summary>
    async ValueTask<bool> HasCollectionAsync(string name, CancellationToken cancellationToken)
    {
        var transaction = await BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false) is not null;
        }
    }

    /// <summary>Counts this process's write commits to this database; read it before a read to wait for later commits.</summary>
    long CommitSequence { get; }

    /// <summary>
    /// Completes once <see cref="CommitSequence"/> exceeds <paramref name="sequence"/>: right away when a
    /// write transaction committed since it was read, otherwise at the next one. Callers re-read to see what changed.
    /// </summary>
    Task WaitForCommitAsync(long sequence, CancellationToken cancellationToken);
}
